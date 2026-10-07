import { useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { ProfileView } from '../api/types'

/**
 * The signed-in person's profile, loaded once per page that needs it.
 *
 * Deliberately not in a context. Two pages use it, both of them already protected routes, and a
 * provider wrapping the whole app would fetch a profile for every visitor who never opens their
 * account — including guests, for whom the call is a guaranteed 401.
 */
export function useProfile() {
  const [profile, setProfile] = useState<ProfileView | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(() => {
    api<ProfileView>('/account/profile')
      .then((p) => { setProfile(p); setError(null) })
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not load your account.'))
  }, [])

  useEffect(() => { reload() }, [reload])

  return { profile, error, reload, setProfile }
}
