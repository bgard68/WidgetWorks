// Storefront browsing vocabulary, shared by the header scope select, the
// category rail and the catalog grid so all three stay in step.
//
// The API searches free text (`?search=`) and pages server-side. Category and
// sort are refinements applied to the returned page in the browser: the store
// asks for a full page (PAGE_SIZE, under the API's 100 cap) and narrows it
// here, which keeps the three controls composable without new endpoints.
import type { WidgetView } from '../api/types'

export const PAGE_SIZE = 60

export interface Category {
  /** URL value for the `cat` search param — empty means "everything". */
  slug: string
  label: string
  /** Word matched against a widget's name/sku/description. */
  keyword: string
  /** Key into the CategoryIcon glyph set. */
  icon: string
}

export const CATEGORIES: Category[] = [
  { slug: '', label: 'All departments', keyword: '', icon: 'all' },
  { slug: 'standard', label: 'Standard', keyword: 'standard', icon: 'standard' },
  { slug: 'deluxe', label: 'Deluxe', keyword: 'deluxe', icon: 'deluxe' },
  { slug: 'mega', label: 'Mega', keyword: 'mega', icon: 'mega' },
  { slug: 'mini', label: 'Mini', keyword: 'mini', icon: 'mini' },
  { slug: 'kit', label: 'Kits', keyword: 'kit', icon: 'kit' },
]

export const SORTS = [
  { value: 'featured', label: 'Featured' },
  { value: 'price-asc', label: 'Price: low to high' },
  { value: 'price-desc', label: 'Price: high to low' },
  { value: 'name', label: 'Name: A to Z' },
] as const

export type SortValue = (typeof SORTS)[number]['value']

export function categoryBySlug(slug: string): Category | undefined {
  return CATEGORIES.find((c) => c.slug === slug && c.slug !== '')
}

function matchesCategory(w: WidgetView, keyword: string): boolean {
  if (!keyword) return true
  const needle = keyword.toLowerCase()
  return (
    w.name.toLowerCase().includes(needle) ||
    w.sku.toLowerCase().includes(needle) ||
    w.description.toLowerCase().includes(needle)
  )
}

/** Narrow to a category, then order — pure, so the grid can call it on render. */
export function refine(items: WidgetView[], catSlug: string, sort: string): WidgetView[] {
  const keyword = categoryBySlug(catSlug)?.keyword ?? ''
  const filtered = keyword ? items.filter((w) => matchesCategory(w, keyword)) : items.slice()

  switch (sort) {
    case 'price-asc':
      return filtered.sort((a, b) => a.price - b.price)
    case 'price-desc':
      return filtered.sort((a, b) => b.price - a.price)
    case 'name':
      return filtered.sort((a, b) => a.name.localeCompare(b.name))
    default:
      // "Featured" keeps the order the API returned, with anything out of
      // stock pushed to the end so the grid leads with what can be bought.
      return filtered.sort(
        (a, b) => Number(b.quantityAvailable > 0) - Number(a.quantityAvailable > 0),
      )
  }
}

/** Free-shipping threshold quoted in the header strip and cart nudge. */
export const FREE_SHIPPING_THRESHOLD = 75
