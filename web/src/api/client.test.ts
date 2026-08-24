import { beforeEach, describe, expect, it, vi } from 'vitest'

/**
 * Regression cover for the refresh-token race.
 *
 * The access token lives in memory only, so after a page reload every request
 * already in flight gets a 401 at the same moment. Refresh tokens rotate on
 * use, so if each 401 posted its own refresh, one would win and the rest would
 * present an already-consumed token, fail, and run clearSession() — destroying
 * the session the winner had just established. The fetch stub below models that
 * rotation, so a regression shows up as a second refresh call and a lost session.
 */

/** Minimal localStorage for the node environment. */
class MemoryStorage {
  private map = new Map<string, string>()
  get length() { return this.map.size }
  key(i: number) { return [...this.map.keys()][i] ?? null }
  getItem(k: string) { return this.map.get(k) ?? null }
  setItem(k: string, v: string) { this.map.set(k, String(v)) }
  removeItem(k: string) { this.map.delete(k) }
  clear() { this.map.clear() }
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const authHeader = (init?: RequestInit) =>
  (init?.headers as Record<string, string> | undefined)?.Authorization

/** Fresh module per test — the client keeps token state at module scope. */
async function loadClient() {
  vi.resetModules()
  return import('./client')
}

/**
 * Stubs the API. The protected route only accepts `Bearer fresh`; /auth/refresh
 * hands that out exactly once, then rejects the spent token the way the real
 * rotating-refresh endpoint does.
 */
function stubApi({ refreshStatus = 200 }: { refreshStatus?: number } = {}) {
  const refreshCalls: string[] = []
  const fetchMock = vi.fn(async (input: unknown, init?: RequestInit) => {
    const url = String(input)

    if (url.endsWith('/auth/refresh')) {
      const sent = JSON.parse(String(init?.body ?? '{}')).refreshToken
      refreshCalls.push(sent)
      if (refreshStatus !== 200) return json(refreshStatus, { error: 'Refresh rejected.' })
      // Rotation: the token is single use. A concurrent second attempt is a bug.
      if (refreshCalls.length > 1) return json(401, { error: 'Refresh token already used.' })
      // Yield, so every concurrent caller is waiting on this one promise.
      await new Promise((r) => setTimeout(r, 5))
      return json(200, { accessToken: 'fresh', refreshToken: 'rotated', role: 'Administrator' })
    }

    return authHeader(init) === 'Bearer fresh'
      ? json(200, { ok: true })
      : json(401, { error: 'Unauthorized' })
  })

  vi.stubGlobal('fetch', fetchMock)
  return { refreshCalls, fetchMock }
}

describe('api client token refresh', () => {
  beforeEach(() => {
    vi.stubGlobal('localStorage', new MemoryStorage())
  })

  it('refreshes once and retries when a single request hits a 401', async () => {
    const client = await loadClient()
    client.setSession({ accessToken: 'stale', refreshToken: 'original', role: 'Administrator' })
    client.setAccessToken('stale')
    const { refreshCalls } = stubApi()

    await expect(client.api('/admin/catalog/widgets')).resolves.toEqual({ ok: true })

    expect(refreshCalls).toEqual(['original'])
    expect(client.getRefreshToken()).toBe('rotated')
  })

  it('shares one refresh across concurrent 401s and keeps the session', async () => {
    const client = await loadClient()
    client.setSession({ accessToken: 'stale', refreshToken: 'original', role: 'Administrator' })
    client.setAccessToken('stale')
    const { refreshCalls } = stubApi()

    // Three requests in flight together, exactly as a reload produces.
    const results = await Promise.all([
      client.api('/a'),
      client.api('/b'),
      client.api('/c'),
    ])

    // The whole point: one refresh, not three.
    expect(refreshCalls).toEqual(['original'])
    expect(results).toEqual([{ ok: true }, { ok: true }, { ok: true }])
    // The pre-fix bug signed the user out here.
    expect(client.getRefreshToken()).toBe('rotated')
    expect(client.isRefreshing()).toBe(false)
  })

  it('does not leave a refresh pending once it settles', async () => {
    const client = await loadClient()
    client.setSession({ accessToken: 'stale', refreshToken: 'original', role: 'Administrator' })
    client.setAccessToken('stale')
    stubApi()

    const inFlight = client.api('/a')
    expect(client.isRefreshing()).toBe(false) // not yet — the 401 hasn't come back
    await inFlight
    expect(client.isRefreshing()).toBe(false)

    // A later 401 is free to start its own refresh with the rotated token.
    expect(client.getRefreshToken()).toBe('rotated')
  })

  it('signs out when the refresh is genuinely rejected', async () => {
    const client = await loadClient()
    client.setSession({ accessToken: 'stale', refreshToken: 'original', role: 'Administrator' })
    client.setAccessToken('stale')
    stubApi({ refreshStatus: 401 })

    await expect(client.api('/a')).rejects.toThrow()
    expect(client.getRefreshToken()).toBeNull()
    expect(client.getRole()).toBeNull()
  })

  it('a rejected refresh signs out only once, even with concurrent callers', async () => {
    const client = await loadClient()
    client.setSession({ accessToken: 'stale', refreshToken: 'original', role: 'Administrator' })
    client.setAccessToken('stale')
    const { refreshCalls } = stubApi({ refreshStatus: 401 })

    const results = await Promise.allSettled([client.api('/a'), client.api('/b')])

    expect(results.every((r) => r.status === 'rejected')).toBe(true)
    expect(refreshCalls).toHaveLength(1)
    expect(client.getRefreshToken()).toBeNull()
  })

  it('does not attempt a refresh when there is no stored token', async () => {
    const client = await loadClient()
    const { refreshCalls } = stubApi()

    await expect(client.api('/a')).rejects.toThrow(/unauthorized/i)
    expect(refreshCalls).toHaveLength(0)
  })

  it('sends the bearer token and surfaces the API error message', async () => {
    const client = await loadClient()
    client.setAccessToken('fresh')
    const { fetchMock } = stubApi()

    await client.api('/a')
    expect(authHeader(fetchMock.mock.calls[0][1] as RequestInit)).toBe('Bearer fresh')

    vi.stubGlobal('fetch', vi.fn(async () => json(400, { error: 'SKU already exists.' })))
    await expect(client.api('/admin/catalog/widgets', { method: 'POST', body: {} }))
      .rejects.toThrow('SKU already exists.')
  })

  it('omits the Authorization header when signed out', async () => {
    const client = await loadClient()
    const fetchMock = vi.fn(async (_input: unknown, _init?: RequestInit) => json(200, { ok: true }))
    vi.stubGlobal('fetch', fetchMock)

    await client.api('/catalog/widgets')

    // A guest must not send an empty or stale bearer — the API would 401 a
    // perfectly valid anonymous request.
    expect(authHeader(fetchMock.mock.calls[0][1])).toBeUndefined()
  })

  it('returns nothing for a 204 rather than trying to parse it', async () => {
    const client = await loadClient()
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 204 })))

    await expect(client.api('/auth/logout', { method: 'POST', body: {} })).resolves.toBeUndefined()
  })

  it('returns nothing for an empty 200 body', async () => {
    const client = await loadClient()
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 200 })))

    // Some endpoints answer 200 with no body at all; JSON.parse('') would throw.
    await expect(client.api('/auth/register', { method: 'POST', body: {} })).resolves.toBeUndefined()
  })

  it('falls back to the status code when an error carries no JSON', async () => {
    const client = await loadClient()
    vi.stubGlobal('fetch', vi.fn(async () => new Response('<html>502 Bad Gateway</html>', {
      status: 502, headers: { 'Content-Type': 'text/html' },
    })))

    // A gateway or proxy error is not JSON; the message must still say something useful.
    await expect(client.api('/a')).rejects.toThrow('Request failed (502)')
  })

  it('reports the status when the JSON body has no error field', async () => {
    const client = await loadClient()
    vi.stubGlobal('fetch', vi.fn(async () => json(500, { detail: 'something else' })))

    await expect(client.api('/a')).rejects.toThrow('Request failed (500)')
  })

  it('lets a caller add headers without losing the defaults', async () => {
    const client = await loadClient()
    client.setAccessToken('fresh')
    const fetchMock = vi.fn(async (_input: unknown, _init?: RequestInit) => json(200, { ok: true }))
    vi.stubGlobal('fetch', fetchMock)

    await client.api('/a', { method: 'POST', body: {} })

    const sent = fetchMock.mock.calls[0][1]?.headers as Record<string, string>
    expect(sent['Content-Type']).toBe('application/json')
    expect(sent.Authorization).toBe('Bearer fresh')
  })

  it('carries the status on the thrown error so callers can branch on it', async () => {
    const client = await loadClient()
    vi.stubGlobal('fetch', vi.fn(async () => json(404, { error: 'Cart not found.' })))

    await expect(client.api('/cart/nope')).rejects.toMatchObject({ status: 404, message: 'Cart not found.' })
    expect(client.ApiError).toBeDefined()
  })
})
