import { useEffect, useRef } from 'react'
import { GOOGLE_CLIENT_ID } from '../lib/env'

// Minimal typing for Google Identity Services (loaded from a script tag).
interface GoogleCredentialResponse { credential: string }
interface GoogleAccountsId {
  initialize: (config: { client_id: string; callback: (r: GoogleCredentialResponse) => void }) => void
  renderButton: (parent: HTMLElement, options: Record<string, unknown>) => void
}
declare global {
  interface Window {
    google?: { accounts: { id: GoogleAccountsId } }
  }
}

export function GoogleButton({ onCredential }: { onCredential: (idToken: string) => void }) {
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!GOOGLE_CLIENT_ID) return
    const scriptId = 'google-identity'
    const render = () => {
      if (!window.google || !ref.current) return
      window.google.accounts.id.initialize({
        client_id: GOOGLE_CLIENT_ID,
        callback: (r) => onCredential(r.credential),
      })
      window.google.accounts.id.renderButton(ref.current, { theme: 'outline', size: 'large', width: 280 })
    }
    if (document.getElementById(scriptId)) {
      render()
      return
    }
    const script = document.createElement('script')
    script.id = scriptId
    script.src = 'https://accounts.google.com/gsi/client'
    script.async = true
    script.onload = render
    document.body.appendChild(script)
  }, [onCredential])

  if (!GOOGLE_CLIENT_ID) return null
  return <div ref={ref} />
}
