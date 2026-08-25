// Storefront browsing vocabulary, shared by the header scope select, the
// category rail and the catalog grid so all three stay in step.
//
// Search, category and sort are all applied by the API. They used to be
// narrowed here over a single fetched page, which meant a catalog larger than
// PAGE_SIZE lost its tail from every shelf and a sort only ordered whatever
// happened to be on that page. PAGE_SIZE is now just how many results one
// request asks for; growing past it needs a pager, not a bigger number.
export const PAGE_SIZE = 100

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

/** Free-shipping threshold quoted in the header strip and cart nudge. */
export const FREE_SHIPPING_THRESHOLD = 75
