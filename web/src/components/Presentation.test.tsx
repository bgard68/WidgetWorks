import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { Price } from './Price'
import { Rating, Stars } from './Stars'
import { PanelSkeleton, ProductGridSkeleton } from './Skeleton'
import { StatusPill } from './StatusPill'

/**
 * The purely visual pieces. Small, but each carries one rule worth pinning:
 * the split price keeps its accessible label, the star row clamps, the
 * skeletons hold layout with sensible defaults, and an unknown order status
 * still renders as a readable pill instead of crashing a list.
 */
describe('Price', () => {
  it('splits symbol, dollars and cents but reads as the full amount', () => {
    const { container } = render(<Price value={1234.56} />)

    expect(screen.getByLabelText('$1,234.56')).toBeInTheDocument()
    expect(container.querySelector('.cur')).toHaveTextContent('$')
    expect(container.querySelector('.int')).toHaveTextContent('1,234')
    expect(container.querySelector('.cents')).toHaveTextContent('56')
  })

  it('supports the size and deal variants', () => {
    const { container } = render(<Price value={5} size="lg" deal className="extra" />)

    expect(container.firstChild).toHaveClass('price', 'price-lg', 'price-deal', 'extra')
  })

  it('falls back to the plain string when the amount does not split', () => {
    // NaN formats as "$NaN", which the splitter cannot parse — the fallback
    // renders it verbatim rather than blank.
    const { container } = render(<Price value={Number.NaN} />)

    expect(container.firstChild).toHaveTextContent('NaN')
    expect(container.querySelector('.int')).toBeNull()
  })
})

describe('Stars and Rating', () => {
  it('fills the star row proportionally to the rating', () => {
    const { container } = render(<Stars rating={2.5} />)

    expect(container.querySelector('.fill')).toHaveStyle({ width: '50%' })
  })

  it('clamps ratings to the zero-to-five range', () => {
    const low = render(<Stars rating={-1} />)
    expect(low.container.querySelector('.fill')).toHaveStyle({ width: '0%' })

    const high = render(<Stars rating={99} />)
    expect(high.container.querySelector('.fill')).toHaveStyle({ width: '100%' })
  })

  it('announces the rating for screen readers and shows the review count', () => {
    render(<Rating rating={4.3} reviews={1234} />)

    expect(screen.getByText('4.3 out of 5 stars')).toHaveClass('sr-only')
    expect(screen.getByText('(1,234)')).toBeInTheDocument()
  })
})

describe('Skeletons', () => {
  it('the grid skeleton renders its default of eight card placeholders', () => {
    const { container } = render(<ProductGridSkeleton />)

    expect(screen.getByRole('status', { name: 'Loading products' })).toBeInTheDocument()
    expect(container.querySelectorAll('.pcard')).toHaveLength(8)
  })

  it('the panel skeleton renders its default of four lines', () => {
    const { container } = render(<PanelSkeleton />)

    expect(container.querySelectorAll('.sk-line')).toHaveLength(4)
  })
})

describe('StatusPill', () => {
  it.each([
    ['Paid', 'pill-ok', 'Paid'],
    ['AwaitingPayment', 'pill-warn', 'Awaiting payment'],
    ['PaymentFailed', 'pill-err', 'Payment failed'],
  ])('tones and humanises %s', (status, tone, label) => {
    render(<StatusPill status={status} />)

    const pill = screen.getByText(label)
    expect(pill).toHaveClass('pill', tone)
  })

  it('renders an unknown status neutrally instead of crashing', () => {
    render(<StatusPill status="Backordered" />)

    expect(screen.getByText('Backordered')).toHaveClass('pill-info')
  })
})
