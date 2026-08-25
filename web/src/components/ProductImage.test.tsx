import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { ProductImage } from './ProductImage'

/**
 * The graceful-failure contract: a photo that cannot load becomes a neutral
 * placeholder, never a broken-image icon or collapsed blank space.
 */
describe('ProductImage', () => {
  it('prefers an admin-supplied image url over the generated one', () => {
    render(<ProductImage sku="WW-001" imageUrl="https://cdn.example/widget.jpg" alt="Standard Widget" />)

    expect(screen.getByRole('img', { name: 'Standard Widget' })).toHaveAttribute('src', 'https://cdn.example/widget.jpg')
  })

  it('falls back to the bundled illustration when no url is set', () => {
    render(<ProductImage sku="WW-001" imageUrl={null} alt="Standard Widget" />)

    expect(screen.getByRole('img', { name: 'Standard Widget' })).toHaveAttribute(
      'src',
      '/products/ww-001.svg',
    )
  })

  it('swaps to the placeholder when the photo fails to load', () => {
    render(<ProductImage sku="WW-001" imageUrl={null} alt="Standard Widget" className="tile" />)

    fireEvent.error(screen.getByRole('img', { name: 'Standard Widget' }))

    const fallback = screen.getByRole('img', { name: 'Standard Widget' })
    expect(fallback.tagName).toBe('SPAN')
    expect(fallback).toHaveClass('img-fallback', 'tile')
  })

  it('labels the placeholder for screen readers even with no alt text', () => {
    render(<ProductImage sku="WW-001" />)

    fireEvent.error(screen.getByRole('presentation'))

    expect(screen.getByRole('img', { name: 'Product image unavailable' })).toBeInTheDocument()
  })
})
