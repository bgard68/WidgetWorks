import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import type { Paged, WidgetView } from '../api/types'
import { pseudoRating } from '../lib/img'
import { CATEGORIES, FREE_SHIPPING_THRESHOLD, PAGE_SIZE, SORTS, categoryBySlug, refine } from '../lib/catalog'
import { AddToCartButton } from '../components/AddToCartButton'
import { ProductImage } from '../components/ProductImage'
import { Price } from '../components/Price'
import { Rating } from '../components/Stars'
import { ProductGridSkeleton } from '../components/Skeleton'

function ProductCard({ widget }: { widget: WidgetView }) {
  const { rating, reviews } = pseudoRating(widget.sku)
  const out = widget.quantityAvailable <= 0
  const low = !out && widget.quantityAvailable <= 10
  const href = `/widgets/${widget.id}`

  return (
    <article className="pcard">
      <Link to={href} className="pcard-media" tabIndex={-1} aria-hidden="true">
        <ProductImage sku={widget.sku} imageUrl={widget.imageUrl} />
      </Link>

      {out
        ? <span className="badge badge-out pcard-flag">Out of stock</span>
        : low ? <span className="badge badge-low pcard-flag">Only {widget.quantityAvailable} left</span> : null}

      <div className="pcard-body">
        <Link to={href} className="pcard-title">{widget.name}</Link>
        <Rating rating={rating} reviews={reviews} />
        <p className="pcard-desc clamp-2">{widget.description}</p>
        <div className="pcard-price"><Price value={widget.price} size="md" /></div>
        {widget.price >= FREE_SHIPPING_THRESHOLD
          ? <span className="pcard-ship">FREE shipping</span>
          : <span className="pcard-ship muted">Free shipping over ${FREE_SHIPPING_THRESHOLD}</span>}
        <div className="pcard-cta">
          <AddToCartButton widgetId={widget.id} outOfStock={out} />
        </div>
      </div>
    </article>
  )
}

export function CatalogPage() {
  const [params, setParams] = useSearchParams()
  const q = params.get('q') ?? ''
  const cat = params.get('cat') ?? ''
  const sort = params.get('sort') ?? 'featured'

  const [data, setData] = useState<Paged<WidgetView> | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let active = true
    setLoading(true)
    const sp = new URLSearchParams({ pageSize: String(PAGE_SIZE) })
    if (q.trim()) sp.set('search', q.trim())
    api<Paged<WidgetView>>(`/catalog/widgets?${sp}`)
      .then((d) => { if (active) { setData(d); setError(null) } })
      .catch((e) => { if (active) setError(e.message) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [q])

  const items = useMemo(() => refine(data?.items ?? [], cat, sort), [data, cat, sort])

  const category = categoryBySlug(cat)
  const browsing = !q && !cat
  const heading = q
    ? `Results for “${q}”`
    : category ? `${category.label} widgets` : 'Featured widgets'

  const setParam = (key: string, value: string) => {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
  }

  return (
    <>
      {browsing && (
        <>
          <section className="hero" aria-label="Featured promotions">
            <div className="hero-main">
              <span className="hero-eyebrow">New season stock</span>
              <h1>Widgets for every job.</h1>
              <p>
                From the everyday Standard to the heavy-duty Mega — quality widgets,
                fast shipping and honest prices, backed by a 30-day return window.
              </p>
              <Link to="/?cat=kit" className="btn btn-primary btn-lg hero-cta">
                Shop the kits
              </Link>
            </div>

            <Link to="/?cat=kit" className="tile tile-a">
              <small>Deals</small>
              <h3>Save on Widget Pro kits</h3>
              <span className="go">Shop kits</span>
            </Link>

            <Link to="/?cat=mega" className="tile tile-b">
              <small>New</small>
              <h3>Weatherproof Mega widgets</h3>
              <span className="go">See what&apos;s new</span>
            </Link>
          </section>

          <nav className="shortcuts" aria-label="Shop by category">
            {CATEGORIES.filter((c) => c.slug).map((c) => (
              <Link key={c.slug} to={`/?cat=${c.slug}`} className="shortcut">
                <span className="ico" aria-hidden="true">{c.icon}</span>
                <span className="lbl">{c.label}</span>
              </Link>
            ))}
          </nav>
        </>
      )}

      {!browsing && (
        <nav className="crumbs" aria-label="Breadcrumb">
          <Link to="/" className="link">Home</Link>
          <span className="sep" aria-hidden="true">›</span>
          <span className="cur">{heading}</span>
        </nav>
      )}

      <div className="toolbar">
        <div>
          <h1>{heading}</h1>
          <div className="toolbar-count">
            {loading
              ? 'Loading products…'
              : `${items.length} ${items.length === 1 ? 'product' : 'products'}${
                data && items.length < data.totalCount ? ` of ${data.totalCount}` : ''
              }`}
          </div>
        </div>

        <div className="toolbar-sort">
          {cat && (
            <button type="button" className="btn btn-secondary btn-sm" onClick={() => setParam('cat', '')}>
              Clear category ✕
            </button>
          )}
          <label htmlFor="sort">Sort by</label>
          <select id="sort" value={sort} onChange={(e) => setParam('sort', e.target.value)}>
            {SORTS.map((s) => <option key={s.value} value={s.value}>{s.label}</option>)}
          </select>
        </div>
      </div>

      {error && <p className="alert alert-err">{error}</p>}

      {loading && !error && <ProductGridSkeleton count={8} />}

      {!loading && !error && items.length > 0 && (
        <div className="grid">
          {items.map((w) => <ProductCard key={w.id} widget={w} />)}
        </div>
      )}

      {!loading && !error && items.length === 0 && (
        <div className="empty">
          <span className="empty-ico" aria-hidden="true">🔎</span>
          <h2>No widgets matched</h2>
          <p>
            {q
              ? `We couldn't find anything for “${q}”${category ? ` in ${category.label}` : ''}. Try a different term or browse all departments.`
              : 'Nothing in this category yet. Browse the full catalog instead.'}
          </p>
          <Link to="/" className="btn btn-secondary">Browse all widgets</Link>
        </div>
      )}
    </>
  )
}
