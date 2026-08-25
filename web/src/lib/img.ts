// Product imagery + light cosmetic rating for the storefront.
// If a widget has no imageUrl, fall back to the bundled illustration for its
// SKU (web/public/products) so the grid needs no external image service and
// renders offline. Real product images set on the widget (admin) take
// precedence; SKUs without bespoke art share a generic widget illustration.
const ILLUSTRATED = /^ww-(\d{3})$/
const ILLUSTRATED_COUNT = 25

export function productImage(w: { sku: string; imageUrl?: string | null }): string {
  if (w.imageUrl && w.imageUrl.trim()) return w.imageUrl
  const sku = (w.sku || '').toLowerCase()
  const match = ILLUSTRATED.exec(sku)
  const n = match ? Number(match[1]) : 0
  return n >= 1 && n <= ILLUSTRATED_COUNT ? `/products/${sku}.svg` : '/products/widget.svg'
}

// Deterministic, cosmetic star rating derived from the SKU. Placeholder visuals
// until a real product-reviews feature exists — stable per product, no backend.
export function pseudoRating(sku: string): { rating: number; reviews: number } {
  let h = 0
  for (let i = 0; i < sku.length; i++) h = (h * 31 + sku.charCodeAt(i)) >>> 0
  const rating = Math.min(5, Math.round((3.8 + (h % 13) / 10) * 10) / 10) // 3.8 .. 5.0
  const reviews = 40 + (h % 1960) // 40 .. 1999
  return { rating, reviews }
}
