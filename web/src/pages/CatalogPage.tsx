import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import type { Paged, WidgetView } from '../api/types'
import { pseudoRating } from '../lib/img'
import { CATEGORIES, FREE_SHIPPING_THRESHOLD, PAGE_SIZE, SORTS, categoryBySlug } from '../lib/catalog'
import { CategoryIcon } from '../components/CategoryIcon'
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
  // Paging lives in the URL like every other part of the query, so a shelf can be
  // linked, bookmarked and reached with the back button.
  const page = Math.max(1, Number(params.get('page') ?? '1') || 1)

  const [data, setData] = useState<Paged<WidgetView> | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let active = true
    setLoading(true)
    const sp = new URLSearchParams({ pageSize: String(PAGE_SIZE), page: String(page) })
    if (q.trim()) sp.set('search', q.trim())
    // Category and sort are the server's job. Narrowing a single fetched page in the browser
    // silently dropped anything past that page from a shelf, and sorted only what happened to be
    // on it. Asking the API means both apply to the whole matching set.
    const keyword = categoryBySlug(cat)?.keyword
    if (keyword) sp.set('category', keyword)
    if (sort) sp.set('sort', sort)
    api<Paged<WidgetView>>(`/catalog/widgets?${sp}`)
      .then((d) => { if (active) { setData(d); setError(null) } })
      .catch((e) => { if (active) setError(e.message) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [q, cat, sort, page])

  const items = data?.items ?? []

  const category = categoryBySlug(cat)
  const browsing = !q && !cat
  const heading = q
    ? `Results for “${q}”`
    : category ? `${category.label} widgets` : 'Featured widgets'

  const setParam = (key: string, value: string) => {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    // Any change to what is being asked for starts again at the first page. Keeping the old
    // page number would strand a reader on page 4 of a result that now has two.
    if (key !== 'page') next.delete('page')
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
              <Link to="/store?cat=kit" className="btn btn-primary btn-lg hero-cta">
                Shop the kits
              </Link>
            </div>

            <Link to="/store?cat=kit" className="tile tile-a">
              <small>Deals</small>
              <h3>Save on Widget Pro kits</h3>
              <span className="go">Shop kits</span>
            </Link>

            <Link to="/store?cat=mega" className="tile tile-b">
              <small>New</small>
              <h3>Weatherproof Mega widgets</h3>
              <span className="go">See what&apos;s new</span>
            </Link>
          </section>

          <nav className="shortcuts" aria-label="Shop by category">
            {CATEGORIES.filter((c) => c.slug).map((c) => (
              <Link key={c.slug} to={`/store?cat=${c.slug}`} className="shortcut">
                <span className="ico" aria-hidden="true"><CategoryIcon name={c.icon} /></span>
                <span className="lbl">{c.label}</span>
              </Link>
            ))}
          </nav>
        </>
      )}

      {!browsing && (
        <nav className="crumbs" aria-label="Breadcrumb">
          <Link to="/store" className="link">Home</Link>
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
                data && data.totalCount > items.length ? ` of ${data.totalCount}` : ''
              }${data && data.totalPages > 1 ? ` · page ${data.page} of ${data.totalPages}` : ''}`}
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

      {!loading && !error && data && data.totalPages > 1 && (
        <nav className="pager" aria-label="Pagination">
          <button
            type="button"
            className="btn btn-secondary btn-sm"
            disabled={data.page <= 1}
            onClick={() => setParam('page', String(data.page - 1))}
          >
            ← Previous
          </button>
          <span className="pager-at" aria-live="polite">
            Page {data.page} of {data.totalPages}
          </span>
          <button
            type="button"
            className="btn btn-secondary btn-sm"
            disabled={data.page >= data.totalPages}
            onClick={() => setParam('page', String(data.page + 1))}
          >
            Next →
          </button>
        </nav>
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
          <Link to="/store" className="btn btn-secondary">Browse all widgets</Link>
        </div>
      )}
    </>
  )
}
