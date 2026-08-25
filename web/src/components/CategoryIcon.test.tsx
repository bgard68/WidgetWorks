import { describe, expect, it } from 'vitest'
import { render } from '@testing-library/react'
import { CategoryIcon } from './CategoryIcon'
import { CATEGORIES } from '../lib/catalog'

describe('CategoryIcon', () => {
  it('renders a glyph for every icon key the catalog declares', () => {
    for (const c of CATEGORIES) {
      const { container, unmount } = render(<CategoryIcon name={c.icon} />)
      expect(container.querySelector('svg'), `icon "${c.icon}"`).not.toBeNull()
      unmount()
    }
  })

  it('renders nothing for an unknown key instead of a broken tile', () => {
    const { container } = render(<CategoryIcon name="does-not-exist" />)
    expect(container.querySelector('svg')).toBeNull()
  })
})
