import { describe, expect, it } from 'vitest'
import type { WidgetView } from '../api/types'
import { CATEGORIES, categoryBySlug, refine } from './catalog'

// The header scope select, the category rail and the sort control all funnel
// through refine(), so this covers the behaviour behind three UI controls.
const widget = (over: Partial<WidgetView>): WidgetView => ({
  id: crypto.randomUUID(),
  sku: 'WW-000',
  name: 'Widget',
  description: '',
  imageUrl: null,
  price: 10,
  isActive: true,
  quantityOnHand: 10,
  quantityReserved: 0,
  quantityAvailable: 10,
  ...over,
})

const catalog: WidgetView[] = [
  widget({ sku: 'WW-001', name: 'Standard Widget', price: 9.99 }),
  widget({ sku: 'WW-002', name: 'Deluxe Widget', price: 24.99 }),
  widget({ sku: 'WW-003', name: 'Mega Widget', price: 49.99 }),
  widget({ sku: 'WW-005', name: 'Widget Pro Kit', price: 79.99, quantityAvailable: 0 }),
]

const names = (items: WidgetView[]) => items.map((w) => w.name)

// Index rather than Array.prototype.at — the project compiles to ES2020.
const last = (items: string[]) => items[items.length - 1]

describe('categoryBySlug', () => {
  it('resolves a real category', () => {
    expect(categoryBySlug('mega')?.keyword).toBe('mega')
  })

  it('treats the empty slug as "no category" rather than a match', () => {
    expect(categoryBySlug('')).toBeUndefined()
  })

  it('every non-empty category carries a keyword and an icon', () => {
    for (const c of CATEGORIES.filter((c) => c.slug)) {
      expect(c.keyword).not.toBe('')
      expect(c.icon).not.toBe('')
    }
  })
})

describe('refine — category filter', () => {
  it('narrows to the matching category', () => {
    expect(names(refine(catalog, 'deluxe', 'featured'))).toEqual(['Deluxe Widget'])
  })

  it('matches the seeded kit by keyword', () => {
    expect(names(refine(catalog, 'kit', 'featured'))).toEqual(['Widget Pro Kit'])
  })

  it('returns everything when no category is chosen', () => {
    expect(refine(catalog, '', 'featured')).toHaveLength(catalog.length)
  })

  it('returns nothing for a category with no members', () => {
    expect(refine(catalog, 'mini', 'featured')).toEqual([])
  })

  it('does not mutate the source array', () => {
    const original = [...catalog]
    refine(catalog, '', 'price-desc')
    expect(catalog).toEqual(original)
  })
})

describe('refine — sorting', () => {
  it('sorts by price ascending', () => {
    expect(names(refine(catalog, '', 'price-asc'))).toEqual([
      'Standard Widget', 'Deluxe Widget', 'Mega Widget', 'Widget Pro Kit',
    ])
  })

  it('sorts by price descending', () => {
    expect(names(refine(catalog, '', 'price-desc'))).toEqual([
      'Widget Pro Kit', 'Mega Widget', 'Deluxe Widget', 'Standard Widget',
    ])
  })

  it('sorts by name', () => {
    expect(names(refine(catalog, '', 'name'))).toEqual([
      'Deluxe Widget', 'Mega Widget', 'Standard Widget', 'Widget Pro Kit',
    ])
  })

  it('featured pushes out-of-stock items to the end', () => {
    expect(last(names(refine(catalog, '', 'featured')))).toBe('Widget Pro Kit')
  })

  it('falls back to featured ordering for an unknown sort', () => {
    expect(last(names(refine(catalog, '', 'nonsense')))).toBe('Widget Pro Kit')
  })

  it('handles an empty catalog', () => {
    expect(refine([], 'mega', 'price-asc')).toEqual([])
  })
})
