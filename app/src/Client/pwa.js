// Service-worker registration, wrapped for App.fs. `virtual:pwa-register` is
// vite-plugin-pwa's module (it does nothing in dev, where there is no worker).
//
// The worker is built with registerType 'prompt': a new version installs in
// the background and then waits. `onNeedRefresh` fires with a function that
// tells it to take over and reloads the page - the app shows that as an
// "Update available" bar, so nobody loses an edit to a surprise reload.
// The hourly `update()` makes a long-running installed app notice deploys
// without having to be relaunched.
import { registerSW } from 'virtual:pwa-register';

export function register(onNeedRefresh) {
  const update = registerSW({
    immediate: true,
    onNeedRefresh() {
      onNeedRefresh(() => update(true));
    },
    onRegisteredSW(_url, registration) {
      if (registration) setInterval(() => registration.update(), 60 * 60 * 1000);
    },
  });
}
