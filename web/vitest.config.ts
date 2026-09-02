import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

// Kept separate from vite.config.ts so a production build never needs vitest
// installed (e.g. `npm ci --omit=dev` followed by `npm run build`).
export default defineConfig({
  plugins: [react()],
  test: {
    // jsdom for the component suites; the pure-module suites don't care either way.
    environment: 'jsdom',
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
    setupFiles: ['src/test/setup.ts'],
    restoreMocks: true,
    unstubGlobals: true,
    coverage: {
      provider: 'v8',
      reporter: ['text-summary', 'json-summary', 'lcov'],
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.test.{ts,tsx}',
        'src/test/**',
        // Composition root and declaration-only modules: nothing to assert.
        'src/main.tsx',
        'src/api/types.ts',
      ],
      // A floor, not a target: it catches a regression rather than inviting tests written to
      // hit a number. `npm run test:coverage` fails the run when coverage drops below it.
      //
      // All four floors are the full figure, which is only honest because the handful of
      // genuinely unreachable guards are excluded at the site with a reason each — see the
      // `v8 ignore` comments. A guard the suite cannot reach is a fact about the code's shape
      // (an early return above it, a disabled button, a ref that is always attached), and
      // stating it there is reviewable in a way that a fractional floor is not.
      //
      // These were 98 while ten such guards sat in the denominator, which put the floor within
      // a single branch of the ceiling: the next half-tested `if` anyone wrote would fail the
      // build for a reason unrelated to their change, and the quickest way out would be to
      // lower the number again. Excluding by reason rather than by percentage keeps the gate
      // able to report bad news.
      //
      // So: do not lower these. If coverage drops, either the test is missing or the new guard
      // is unreachable and should say so where it lives.
      thresholds: {
        statements: 100,
        branches: 100,
        functions: 100,
        lines: 100,
      },
    },
  },
})
