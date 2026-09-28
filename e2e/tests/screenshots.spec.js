// Five screenshots of the app on a phone, one per page, against the mock
// backend in ../mock. They are what the README shows, so they are checked in
// and this spec is how they are regenerated: `./e2e/run.sh`.
//
// Each page is waited on by something only the synced fixtures can put there,
// so a screenshot is never taken of a half-filled screen.

import { test, expect } from '@playwright/test';
import { featuredRecipeId } from '../mock/fixtures.js';

const shot = (page, name) => page.screenshot({ path: `screenshots/${name}.png` });

/// The app opens on a "waiting for your pantry" screen until the first sync
/// lands. Getting past it means the stream worked; everything else follows.
const synced = async (page) => {
  await expect(page.getByRole('link', { name: 'Focaccia' }).first()).toBeVisible({ timeout: 30_000 });
};

test.beforeEach(async ({ page }) => {
  // A failing mock shows up as an empty page and a useless timeout otherwise.
  page.on('pageerror', (err) => console.error('[page error]', err.message));
});

test('recipes', async ({ page }) => {
  await page.goto('/');
  await synced(page);
  await expect(page.getByRole('link', { name: 'Weeknight Beef Chili' })).toBeVisible();
  await shot(page, 'recipes');
});

test('recipe detail, Cooklang tab', async ({ page }) => {
  await page.goto('/');
  await synced(page);
  await page.goto(`/recipe/${featuredRecipeId}`);

  await page.getByRole('tab', { name: 'Cooklang' }).click();
  // CodeMirror mounts the text a tick after the tab switches.
  await expect(page.locator('.cm-content')).toContainText('@bread flour{500%g}');
  await shot(page, 'recipe-cooklang');
});

test('shopping list', async ({ page }) => {
  await page.goto('/');
  await synced(page);
  await page.goto('/shopping-list');

  await expect(page.getByText('700 g baby potatoes')).toBeVisible();
  await shot(page, 'shopping-list');
});

test('menu', async ({ page }) => {
  await page.goto('/');
  await synced(page);
  await page.goto('/menu');

  await expect(page.getByRole('link', { name: 'Sheet-pan Chicken Thighs' })).toBeVisible();
  await shot(page, 'menu');
});

test('settings', async ({ page }) => {
  await page.goto('/');
  await synced(page);
  await page.goto('/settings');

  await expect(page.getByText('Dana Whitfield')).toBeVisible();
  // The QR code arrives a tick after the page: it is drawn to a data URL and
  // then set as the image's src, so until it lands the box says "Drawing…".
  await expect(page.getByRole('img', { name: /QR code for pantry/ })).toBeVisible();
  await shot(page, 'settings');
});
