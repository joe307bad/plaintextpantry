import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite';
import { VitePWA } from 'vite-plugin-pwa';

// Build tooling only - the app itself is F# compiled by Fable into ./fable-out.
export default defineConfig({
  plugins: [
    tailwindcss(),
    // Offline app shell. Production builds only: the service worker precaches
    // every file the app needs (bundle, workers, SQLite WASM, fonts, icons), so
    // once loaded it opens with no network; data was already local (PowerSync).
    // A new deploy installs in the background and the app offers a reload.
    // The manifest is the hand-written public/manifest.webmanifest.
    VitePWA({
      // A new worker waits until the user taps "Reload" (see pwa.js).
      registerType: 'prompt',
      injectRegister: false,
      manifest: false,
      workbox: {
        globPatterns: ['**/*.{js,css,html,wasm,ttf,png,svg,webmanifest}'],
        // The multiple-ciphers SQLite builds are only loaded with an
        // encryptionKey, which we don't use; og.png is for link previews.
        globIgnores: ['**/mc-wa-sqlite*', '**/og.png'],
        // The SQLite WASM is 2.2 MB; workbox's default cap is 2 MiB.
        maximumFileSizeToCacheInBytes: 4 * 1024 * 1024,
        // Any in-app route is the SPA. Everything the F# server or PowerSync
        // answers (including the OIDC login/callback navigations) is not.
        navigateFallback: '/index.html',
        navigateFallbackDenylist: [/^\/api\//, /^\/mcp/, /^\/powersync\//, /^\/\.well-known\//],
        cleanupOutdatedCaches: true,
      },
    }),
  ],
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
