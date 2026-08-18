import { useState } from 'react'
import { productImage } from '../lib/img'

/**
 * Product thumbnail with a graceful failure mode: if the photo can't be
 * fetched (offline, blocked host, a bad admin-supplied URL) the tile shows a
 * neutral placeholder rather than collapsing to blank space.
 */
export function ProductImage({
  sku,
  imageUrl,
  alt = '',
  className = '',
}: {
  sku: string
  imageUrl?: string | null
  alt?: string
  className?: string
}) {
  const [failed, setFailed] = useState(false)

  if (failed) {
    return (
      <span className={`img-fallback ${className}`.trim()} role="img" aria-label={alt || 'Product image unavailable'}>
        <span aria-hidden="true">⚙️</span>
      </span>
    )
  }

  return (
    <img
      className={className || undefined}
      src={productImage({ sku, imageUrl })}
      alt={alt}
      loading="lazy"
      onError={() => setFailed(true)}
    />
  )
}
