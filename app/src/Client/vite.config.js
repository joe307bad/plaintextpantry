import { defineConfig } from 'vite';

// Build tooling only - the app itself is F# compiled by Fable into ./fable-out.
export default defineConfig({
  optimizeDeps: {
    // Contains web workers and WASM; must not be pre-bundled.
    exclude: ['@powersync/web'],
  },
  worker: {
    format: 'es',
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      // Fable.Remoting calls go to the F# server.
      '/api': 'http://localhost:5050',
    },
  },
});
