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
      // Lines and functions are fully covered, so their floor is the full figure — anything
      // newly unreachable by the suite is a deliberate decision, not an oversight. Branches
      // and statements sit just under: the shortfall is a handful of defensive guards that
      // cannot be reached through the UI (a click on a button that is disabled while busy, a
      // dialog handler with no subject, a ref that is always attached). Reaching them would
      // mean reshaping the source around the measurement.
      thresholds: {
        statements: 98,
        branches: 98,
        functions: 100,
        lines: 100,
      },
    },
  },
})
