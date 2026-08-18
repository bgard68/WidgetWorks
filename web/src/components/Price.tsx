// Retail price treatment: full-size dollars with the currency symbol and cents
// set small and raised, the way storefront price labels are typeset. Falls back
// to a plain formatted string if Intl gives us something we can't split.
import { money } from '../lib/format'

type Size = 'sm' | 'md' | 'lg'

export function Price({ value, size = 'md', deal = false, className = '' }: {
  value: number
  size?: Size
  deal?: boolean
  className?: string
}) {
  const formatted = money(value)
  const match = /^(\D*)([\d,]+)(?:\.(\d+))?$/.exec(formatted)
  const classes = ['price', `price-${size}`, deal ? 'price-deal' : '', className]
    .filter(Boolean)
    .join(' ')

  if (!match) return <span className={classes}>{formatted}</span>

  const [, symbol, whole, cents] = match
  return (
    <span className={classes} aria-label={formatted}>
      <span aria-hidden="true" className="cur">{symbol}</span>
      <span aria-hidden="true" className="int">{whole}</span>
      {cents && <span aria-hidden="true" className="cents">{cents}</span>}
    </span>
  )
}
