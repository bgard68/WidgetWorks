// Category glyphs for the storefront shortcut tiles: chunky filled shapes,
// one colour per department, drawn inline so they render identically on every
// platform (the previous emoji were redrawn per OS). Keyed by the `icon` value
// in lib/catalog.ts; an unknown key renders nothing rather than a broken tile.
const VIEW = { viewBox: '0 0 48 48', 'aria-hidden': true, focusable: 'false' } as const

const gearTeeth = (
  <>
    <rect x="20.5" y="3" width="7" height="42" rx="3.5" />
    <rect x="20.5" y="3" width="7" height="42" rx="3.5" transform="rotate(45 24 24)" />
    <rect x="20.5" y="3" width="7" height="42" rx="3.5" transform="rotate(90 24 24)" />
    <rect x="20.5" y="3" width="7" height="42" rx="3.5" transform="rotate(135 24 24)" />
    <circle cx="24" cy="24" r="13" />
  </>
)

const cube = (top: string, left: string, right: string) => (
  <>
    <polygon points="27,16.5 36.5,21 27,25.5 17.5,21" fill={top} />
    <polygon points="17.5,21 27,25.5 27,36 17.5,31.5" fill={left} />
    <polygon points="36.5,21 27,25.5 27,36 36.5,31.5" fill={right} />
  </>
)

const ICONS: Record<string, JSX.Element> = {
  standard: (
    <svg {...VIEW}>
      <defs>
        <linearGradient id="ci-std" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#5EA0F8" />
          <stop offset="1" stopColor="#2563EB" />
        </linearGradient>
      </defs>
      <g fill="url(#ci-std)">{gearTeeth}</g>
      <circle cx="24" cy="24" r="6.5" fill="#fff" />
    </svg>
  ),
  deluxe: (
    <svg {...VIEW}>
      <defs>
        <linearGradient id="ci-dlx" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#FB7185" />
          <stop offset="1" stopColor="#E8336E" />
        </linearGradient>
      </defs>
      <path
        fill="url(#ci-dlx)"
        d="M14.6 8h18.8c1 0 2 .45 2.6 1.2l6.9 8.1c1.1 1.3 1.1 3.2 0 4.5L26.6 41.2c-1.35 1.55-3.85 1.55-5.2 0L5.1 21.8c-1.1-1.3-1.1-3.2 0-4.5L12 9.2c.6-.75 1.6-1.2 2.6-1.2z"
      />
      <polygon points="9,19.6 39,19.6 33.2,10.4 14.8,10.4" fill="#FDA4AF" opacity=".65" />
      <polygon points="16.5,19.6 31.5,19.6 24,37.5" fill="#fff" opacity=".22" />
      <circle cx="15.5" cy="14.8" r="1.9" fill="#fff" opacity=".85" />
    </svg>
  ),
  mega: (
    <svg {...VIEW}>
      <defs>
        <linearGradient id="ci-mga" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#FDE68A" />
          <stop offset="1" stopColor="#FBBF24" />
        </linearGradient>
        <linearGradient id="ci-mgb" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#F59E0B" />
          <stop offset="1" stopColor="#DC8A06" />
        </linearGradient>
        <linearGradient id="ci-mgc" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#D97706" />
          <stop offset="1" stopColor="#B45309" />
        </linearGradient>
      </defs>
      <polygon points="24,4.5 43,13.5 24,22.5 5,13.5" fill="url(#ci-mga)" />
      <polygon points="5,13.5 24,22.5 24,43.5 5,34.5" fill="url(#ci-mgb)" />
      <polygon points="43,13.5 24,22.5 24,43.5 43,34.5" fill="url(#ci-mgc)" />
      <path
        d="M9.5 31.5 l5 -3.8 5 3.8 M9.5 38 l5 -3.8 5 3.8"
        fill="none"
        stroke="#fff"
        strokeWidth="2.8"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  ),
  mini: (
    <svg {...VIEW}>
      <defs>
        <linearGradient id="ci-mna" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#99F6E4" />
          <stop offset="1" stopColor="#5EEAD4" />
        </linearGradient>
        <linearGradient id="ci-mnb" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#2DD4BF" />
          <stop offset="1" stopColor="#14B8A6" />
        </linearGradient>
        <linearGradient id="ci-mnc" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#14B8A6" />
          <stop offset="1" stopColor="#0D9488" />
        </linearGradient>
      </defs>
      <rect
        x="5.5"
        y="5.5"
        width="37"
        height="37"
        rx="9"
        fill="none"
        stroke="#99F6E4"
        strokeWidth="3"
        strokeDasharray="7.5 6.5"
        strokeLinecap="round"
      />
      {cube('url(#ci-mna)', 'url(#ci-mnb)', 'url(#ci-mnc)')}
    </svg>
  ),
  kit: (
    <svg {...VIEW}>
      <defs>
        <linearGradient id="ci-kit" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#A78BFA" />
          <stop offset="1" stopColor="#7C3AED" />
        </linearGradient>
      </defs>
      <path
        d="M15.5 18.5v-2.2a3.6 3.6 0 0 1 3.6-3.6h9.8a3.6 3.6 0 0 1 3.6 3.6v2.2"
        fill="none"
        stroke="#7C3AED"
        strokeWidth="3.6"
        strokeLinecap="round"
      />
      <rect x="4" y="18.5" width="40" height="23" rx="5.5" fill="url(#ci-kit)" />
      <path d="M4 29h40" stroke="#6D28D9" strokeWidth="2.6" opacity=".65" />
      <rect x="20" y="25.2" width="8" height="7.6" rx="2" fill="#fff" />
      <rect x="8.5" y="34.5" width="9" height="3.4" rx="1.7" fill="#fff" opacity=".38" />
      <rect x="30.5" y="34.5" width="9" height="3.4" rx="1.7" fill="#fff" opacity=".38" />
    </svg>
  ),
  all: (
    <svg {...VIEW}>
      <rect x="6.5" y="6.5" width="16" height="16" rx="5" fill="#3B82F6" />
      <rect x="25.5" y="6.5" width="16" height="16" rx="5" fill="#F43F5E" />
      <rect x="6.5" y="25.5" width="16" height="16" rx="5" fill="#F59E0B" />
      <rect x="25.5" y="25.5" width="16" height="16" rx="5" fill="#14B8A6" />
    </svg>
  ),
}

export function CategoryIcon({ name }: { name: string }) {
  return ICONS[name] ?? null
}
