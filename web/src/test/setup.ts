import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// jsdom ships <dialog> without its behaviour: showModal/close are absent, so any component
// built on the native modal throws on mount. Minimal stand-in — enough for `open` to reflect
// reality, which is what assertions look at.
if (typeof HTMLDialogElement !== 'undefined' && !HTMLDialogElement.prototype.showModal) {
  HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
    this.open = true
  }
  HTMLDialogElement.prototype.show = function show(this: HTMLDialogElement) {
    this.open = true
  }
  HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
    this.open = false
    this.dispatchEvent(new Event('close'))
  }
}

// Every test gets a clean document and clean storage; a token leaked from one test quietly
// changing another test's auth state is the classic way a suite starts lying.
afterEach(() => {
  cleanup()
  localStorage.clear()
  sessionStorage.clear()
})
