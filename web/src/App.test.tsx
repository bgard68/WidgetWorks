import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import App from './App'
import { stubFetch } from './test/render'

/**
 * The composition root the browser actually boots: real router, real providers,
 * real layout. One render proves the wiring — routes registered, providers
 * nested in the right order, and the demo guide as the landing page.
 */
describe('App', () => {
  it('boots to the demo guide inside the store chrome', async () => {
    stubFetch([['/cart', () => ({ id: 'c', userId: null, items: [], subtotal: 0, itemCount: 0 })]])

    render(<App />)

    // Layout chrome and the landing page both render.
    expect(screen.getByRole('link', { name: 'WidgetWorks home' })).toBeInTheDocument()
    expect(await screen.findByRole('heading', { name: /A widget store you can actually use/i }))
      .toBeInTheDocument()
  })
})
