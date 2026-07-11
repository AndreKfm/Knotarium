import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Honor a PORT override (e.g. preview tooling running a parallel instance); default stays 5273.
    port: Number(process.env.PORT) || 5273,
    proxy: {
      '/api': {
        target: 'http://localhost:5232',
        changeOrigin: true,
        secure: false,
      }
    }
  },
  // `vite preview` (production build) uses its own config block — mirror the dev proxy so a prod build
  // run locally still reaches the backend on :5232.
  preview: {
    port: Number(process.env.PORT) || 4173,
    proxy: {
      '/api': {
        target: 'http://localhost:5232',
        changeOrigin: true,
        secure: false,
      }
    }
  },
  // @ts-expect-error (Vitest type support)
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    exclude: ['**/node_modules/**', '**/dist/**', 'e2e/**'],
  }
})
