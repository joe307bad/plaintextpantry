import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite';

// Build tooling only - the app itself is F# compiled by Fable into ./fable-out.
export default defineConfig({
  plugins: [tailwindcss()],
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
      // The F# server: API, OIDC login callback, and the MCP endpoint with its
      // RFC 9728 metadata. Host header is passed through unchanged so the
      // server builds OIDC redirect URIs on the browser's origin (:5173).
      '/api': 'http://localhost:5050',
      '/mcp': 'http://localhost:5050',
      '/.well-known': 'http://localhost:5050',
    },
  },
});
