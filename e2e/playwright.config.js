import { defineConfig } from '@playwright/test';

// One browser, one phone, five screenshots. Serial: they share an origin, and
// so the OPFS database PowerSync keeps there - two of them syncing at once is
// two tabs fighting over one file for no gain.
export default defineConfig({
  testDir: './tests',
  outputDir: './.playwright',
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.APP_URL ?? 'http://localhost:8080',
    // An iPhone SE: 375x667 CSS pixels at 2x, and a touch screen, which is
    // what the app's phone layout keys off.
    viewport: { width: 375, height: 667 },
    deviceScaleFactor: 2,
    isMobile: true,
    hasTouch: true,
    userAgent:
      'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1',
    // Chromium: WebKit has no OPFS worker support in headless, which PowerSync
    // needs, and the app's layout is not browser-specific.
    browserName: 'chromium',
    trace: 'off',
  },
});
