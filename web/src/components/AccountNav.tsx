import { Link } from 'react-router-dom'
import type { ProfileView } from '../api/types'

const memberFmt = new Intl.DateTimeFormat('en-US', { year: 'numeric' })

/**
 * The account area's persistent left nav.
 *
 * A sidebar rather than a tile grid. Both patterns are in the wild — Company A uses tiles, Company B
 * a sidebar — so this is a judgement, not a rule: tiles look right with nine destinations and look
 * unfinished with two. The sidebar also keeps you oriented when you move between pages, which tiles
 * cannot do because they vanish the moment you pick one.
 *
 * The identity block at the top is lifted straight from Company B, including "Member since", which
 * costs nothing — `users.created_at` has always been there.
 */
export function AccountNav({ profile, current }: { profile: ProfileView | null; current: 'home' | 'security' }) {
  return (
    <nav className="acct-nav" aria-label="Account">
      <div className="acct-id">
        <p className="acct-hi">Hi, {profile?.displayName?.trim() || 'there'}</p>
        {profile && (
          <p className="acct-since">Member since {memberFmt.format(new Date(profile.memberSince))}</p>
        )}
      </div>

      <Link to="/account" className={current === 'home' ? 'acct-navlink on' : 'acct-navlink'}>
        Account home
      </Link>
      <Link to="/orders" className="acct-navlink">Your orders</Link>
      <Link to="/account/security" className={current === 'security' ? 'acct-navlink on' : 'acct-navlink'}>
        Login and security
      </Link>
    </nav>
  )
}
