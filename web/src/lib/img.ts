// Product imagery + light cosmetic rating for the storefront.
// If a widget has no imageUrl, fall back to a deterministic sample photo (picsum,
// seeded by SKU) so the grid always looks like a real store. Real product images
// set on the widget (admin) take precedence.
export function productImage(w: { sku: string; imageUrl?: string | null }): string {
  if (w.imageUrl && w.imageUrl.trim()) return w.imageUrl
  const seed = encodeURIComponent((w.sku || 'widget').toLowerCase())
  return `https://picsum.photos/seed/ww-${seed}/600/450`
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
