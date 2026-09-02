import { useEffect, useRef, useState } from 'react'
import { useCart } from '../cart/CartContext'

type Phase = 'idle' | 'busy' | 'done' | 'error'

/**
 * Add-to-cart control with its own progress state, so the grid and the product
 * page both give the shopper feedback instead of a button that appears inert
 * while the request is in flight.
 */
export function AddToCartButton({
  widgetId,
  quantity = 1,
  disabled = false,
  outOfStock = false,
  className = 'btn btn-primary btn-block',
  label = 'Add to cart',
  onAdded,
}: {
  widgetId: string
  quantity?: number
  disabled?: boolean
  outOfStock?: boolean
  className?: string
  label?: string
  onAdded?: () => void
}) {
  const { addItem } = useCart()
  const [phase, setPhase] = useState<Phase>('idle')
  const [message, setMessage] = useState<string | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(() => () => { if (timer.current) clearTimeout(timer.current) }, [])

  async function click() {
    /* v8 ignore next -- the button is disabled while busy, so a second click cannot arrive */
    if (phase === 'busy') return
    setPhase('busy')
    setMessage(null)
    try {
      await addItem(widgetId, quantity)
      setPhase('done')
      onAdded?.()
      timer.current = setTimeout(() => setPhase('idle'), 1800)
    } catch (err) {
      setPhase('error')
      setMessage(err instanceof Error ? err.message : 'Could not add to cart.')
    }
  }

  const text =
    outOfStock ? 'Out of stock'
      : phase === 'busy' ? 'Adding…'
        : phase === 'done' ? '✓ Added to cart'
          : label

  return (
    <>
      <button
        type="button"
        className={className}
        disabled={disabled || outOfStock || phase === 'busy'}
        onClick={click}
      >
        {text}
      </button>
      {phase === 'error' && message && <p className="alert alert-err small">{message}</p>}
    </>
  )
}
