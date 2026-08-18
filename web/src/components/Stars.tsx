// Star rating with partial fill: a grey five-star row with an orange copy
// clipped to the rating's width, so 4.3 renders as 4.3 rather than rounding to 4.
export function Stars({ rating }: { rating: number }) {
  const pct = `${Math.max(0, Math.min(5, rating)) * 20}%`
  return (
    <span className="stars" aria-hidden="true">
      <span className="fill" style={{ width: pct }} />
    </span>
  )
}

export function Rating({ rating, reviews, className = '' }: {
  rating: number
  reviews: number
  className?: string
}) {
  return (
    <span className={`rating ${className}`.trim()}>
      <Stars rating={rating} />
      <span className="sr-only">{rating.toFixed(1)} out of 5 stars</span>
      <span aria-hidden="true">{rating.toFixed(1)}</span>
      <span className="rating-count">({reviews.toLocaleString()})</span>
    </span>
  )
}
