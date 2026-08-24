import { describe, expect, it } from 'vitest'
import { productImage, pseudoRating } from './img'

describe('productImage', () => {
  it('prefers a real image url', () => {
    expect(productImage({ sku: 'WW-001', imageUrl: 'https://cdn.example/w.jpg' })).toBe('https://cdn.example/w.jpg')
  })

  it('ignores a blank url and falls back to the seeded sample photo', () => {
    expect(productImage({ sku: 'WW-001', imageUrl: '   ' })).toContain('picsum.photos/seed/ww-ww-001')
    expect(productImage({ sku: 'WW-001', imageUrl: null })).toContain('picsum.photos/seed/ww-ww-001')
  })

  it('still produces a stable seed for a widget with no SKU', () => {
    expect(productImage({ sku: '' })).toContain('/seed/ww-widget/')
  })
})

describe('pseudoRating', () => {
  it('is deterministic per SKU', () => {
    expect(pseudoRating('WW-001')).toEqual(pseudoRating('WW-001'))
    expect(pseudoRating('WW-001')).not.toEqual(pseudoRating('WW-002'))
  })

  it('stays inside the plausible-store bounds', () => {
    for (const sku of ['WW-001', 'WW-002', 'X', 'a-very-long-sku-name']) {
      const { rating, reviews } = pseudoRating(sku)
      expect(rating).toBeGreaterThanOrEqual(3.8)
      expect(rating).toBeLessThanOrEqual(5)
      expect(reviews).toBeGreaterThanOrEqual(40)
      expect(reviews).toBeLessThan(2000)
    }
  })
})
