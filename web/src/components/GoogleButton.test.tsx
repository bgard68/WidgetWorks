import { afterEach, describe, expect, it, vi } from 'vitest'
import { render } from '@testing-library/react'
import { GoogleButton } from './GoogleButton'

/**
 * The Google Identity Services integration. The SDK arrives via a script tag and hangs itself on
 * window.google, so the tests stand in for both: the script element and the global. What matters
 * is that the button only exists when a client id is configured, that the SDK is initialised with
 * our callback, and that a second mount reuses the already-loaded script instead of injecting it
 * twice.
 */

let clientId = ''
vi.mock('../lib/env', () => ({
  get GOOGLE_CLIENT_ID() { return clientId },
  API_BASE_URL: 'http://localhost:5080',
}))

type InitConfig = { client_id: string; callback: (r: { credential: string }) => void }

function stubGoogle() {
  const initialize = vi.fn<(c: InitConfig) => void>()
  const renderButton = vi.fn()
  vi.stubGlobal('google', { accounts: { id: { initialize, renderButton } } })
  return { initialize, renderButton }
}

afterEach(() => {
  clientId = ''
  document.getElementById('google-identity')?.remove()
  delete (window as { google?: unknown }).google
})

describe('GoogleButton', () => {
  it('renders nothing at all when no client id is configured', () => {
    clientId = ''

    const { container } = render(<GoogleButton onCredential={() => {}} />)

    expect(container).toBeEmptyDOMElement()
    expect(document.getElementById('google-identity')).toBeNull()
  })

  it('injects the SDK script once and renders on its load', () => {
    clientId = 'client-123'
    const { initialize, renderButton } = stubGoogle()

    render(<GoogleButton onCredential={() => {}} />)

    const script = document.getElementById('google-identity') as HTMLScriptElement
    expect(script).not.toBeNull()
    expect(script.src).toContain('accounts.google.com/gsi/client')

    // The SDK finishes loading; only now does the button render.
    expect(renderButton).not.toHaveBeenCalled()
    script.onload?.(new Event('load'))
    expect(initialize).toHaveBeenCalledWith(expect.objectContaining({ client_id: 'client-123' }))
    expect(renderButton).toHaveBeenCalled()
  })

  it('hands the credential from Google to the caller', () => {
    clientId = 'client-123'
    const { initialize } = stubGoogle()
    const onCredential = vi.fn()

    render(<GoogleButton onCredential={onCredential} />)
    ;(document.getElementById('google-identity') as HTMLScriptElement).onload?.(new Event('load'))

    const config = initialize.mock.calls[0][0]
    config.callback({ credential: 'google-id-token' })

    expect(onCredential).toHaveBeenCalledWith('google-id-token')
  })

  it('reuses the script a previous mount already injected', () => {
    clientId = 'client-123'
    const { renderButton } = stubGoogle()
    const script = document.createElement('script')
    script.id = 'google-identity'
    document.body.appendChild(script)

    render(<GoogleButton onCredential={() => {}} />)

    // No second script; the SDK is already there, so the button renders immediately.
    expect(document.querySelectorAll('#google-identity')).toHaveLength(1)
    expect(renderButton).toHaveBeenCalled()
  })

  it('waits quietly when the script tag exists but the SDK has not attached yet', () => {
    clientId = 'client-123'
    const script = document.createElement('script')
    script.id = 'google-identity'
    document.body.appendChild(script)

    // No window.google: render() must bail without throwing.
    const { container } = render(<GoogleButton onCredential={() => {}} />)

    expect(container.querySelector('div')).not.toBeNull()
  })
})
