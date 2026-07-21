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
      include: ['src/**'],
      // Thresholds set to floor(full-source actual). Measured 2026-07-21.
      thresholds: {
        statements: 25,
        branches: 19,
        functions: 19,
        lines: 25,
      },
    },
  },
})
