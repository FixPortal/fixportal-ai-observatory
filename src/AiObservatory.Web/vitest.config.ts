import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    // NOTE: `globals: true` is deliberately NOT set. Tests import { describe, it,
    // expect } from 'vitest' explicitly. This matters for ArchUnitTS -- see
    // architecture.archunit.ts for why the no-globals choice drives the wrapper.
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html'],
      // Thresholds set to floor(actual) capped at 70. Measured 2026-06-15.
      thresholds: {
        statements: 70,
        branches: 70,
        functions: 70,
        lines: 70,
      },
    },
  },
})
