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
      thresholds: {
        statements: 80,
        branches: 70,
        functions: 80,
        lines: 82,
      },
    },
  },
})
