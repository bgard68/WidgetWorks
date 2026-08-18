import { defineConfig } from 'vitest/config'

// Kept separate from vite.config.ts so a production build never needs vitest
// installed (e.g. `npm ci --omit=dev` followed by `npm run build`).
export default defineConfig({
  test: {
    // The suites under test are plain modules — no DOM required, so we skip
    // jsdom and stub the couple of browser globals the API client touches.
    environment: 'node',
    include: ['src/**/*.test.ts'],
    restoreMocks: true,
    unstubGlobals: true,
  },
})
