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
      // RFC 9728 metadata. changeOrigin must be off (a bare string target turns
      // it on) so the Host header stays localhost:5173 and the server builds the
      // OIDC redirect_uri on the browser's origin; otherwise Keycloak sends the
      // callback to :5050, which has no app to land on.
      '/api': { target: 'http://localhost:5050', changeOrigin: false },
      '/mcp': { target: 'http://localhost:5050', changeOrigin: false },
      '/.well-known': { target: 'http://localhost:5050', changeOrigin: false },
    },
  },
});
