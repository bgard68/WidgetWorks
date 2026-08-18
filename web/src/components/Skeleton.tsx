// Placeholder shapes shown while a request is in flight, so the page keeps its
// layout instead of collapsing to a line of "Loading…" text.
export function ProductCardSkeleton() {
  return (
    <div className="pcard" aria-hidden="true">
      <div className="pcard-media"><div className="sk sk-media" /></div>
      <div className="pcard-body">
        <div className="sk sk-line" />
        <div className="sk sk-line w80" />
        <div className="sk sk-line w40" />
        <div className="sk sk-btn" />
      </div>
    </div>
  )
}

export function ProductGridSkeleton({ count = 8 }: { count?: number }) {
  return (
    <div className="grid" role="status" aria-label="Loading products">
      {Array.from({ length: count }, (_, i) => <ProductCardSkeleton key={i} />)}
    </div>
  )
}

export function PanelSkeleton({ lines = 4 }: { lines?: number }) {
  return (
    <div className="panel panel-body" role="status" aria-label="Loading">
      {Array.from({ length: lines }, (_, i) => (
        <div key={i} className={`sk sk-line${i === lines - 1 ? ' w60' : ''}`} />
      ))}
    </div>
  )
}
