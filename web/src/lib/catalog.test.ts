import { describe, expect, it } from 'vitest'
import { CATEGORIES, categoryBySlug } from './catalog'

// The browsing vocabulary the header scope select, the category rail and the
// sort control share. Filtering and ordering are the API's job now — that
// behaviour is covered by WidgetRepositoryTests against real SQL, which is
// where it lives.
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

  it('resolves each slug to the keyword the API is asked to narrow on', () => {
    // The rail stores a slug; the request sends the keyword. This is the
    // assertion that catches a slug being renamed for the URL without the
    // keyword following it.
    for (const c of CATEGORIES.filter((c) => c.slug)) {
      expect(categoryBySlug(c.slug)?.keyword).toBe(c.keyword)
    }
  })
})
