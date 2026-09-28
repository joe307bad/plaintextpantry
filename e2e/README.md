# Screenshots

The five phone screenshots in the project README, taken by a browser driving
the real client:

```sh
./e2e/run.sh
```

Docker is the only prerequisite, and nothing else on your machine is touched -
no Postgres, no PowerSync, no Keycloak, no F# server, no database to seed, and
no dev stack running alongside. The run builds the client from source, serves
it behind a mock backend, photographs five pages at iPhone SE size, writes them
to `screenshots/`, and tears everything down.

## How the data gets there

The app is local-first: the UI reads a SQLite database in the browser, which
PowerSync fills from a sync stream. So the one thing worth faking is that
stream, and `mock/server.js` is the whole backend:

| | |
|---|---|
| `GET /api/auth/me` | signed in, always, as Sam Reyes |
| `GET /api/sync/credentials` | a JWT-shaped token and this same origin as the sync endpoint |
| `POST /sync/stream` | one checkpoint containing every fixture row, then held open |
| `POST /api/sync/upload` | accepts local writes and drops them |
| `POST /api/usage/pageview` | 204; a screenshot run is not usage |
| everything else | the built client, with the SPA fallback Caddy does in production |

The rows come from `mock/fixtures.js` — a household called Maple Street, two
people in it, six recipes in Cooklang, a shopping list, a menu with sides, and
four tags. Dates are relative to the moment the run starts, so "2 days ago"
always reads right. Ids are fixed, so `/recipe/<id>` is the same recipe every
time.

The client is not modified or stubbed in any way: it is the production build,
and it syncs, queries and renders exactly as it does against the real stack.
What it never learns is that the pantry on the other end was invented.

## The pieces

```
Dockerfile          the client build (dotnet + Fable + Vite), the app image, the Playwright image
docker-compose.yml  app + tests; tests share the app's network, so it is on localhost
mock/server.js      static files, the three /api endpoints, and the sync stream
mock/fixtures.js    the pantry the screenshots are of
tests/              one test per screenshot
playwright.config.js  iPhone SE: 375x667 at 2x, touch, Chromium
screenshots/        the output, committed, shown in the project README
```

## Running it by hand

`run.sh` leaves nothing behind, so to poke at the app yourself:

```sh
docker compose -f e2e/docker-compose.yml up app    # http://localhost:8099
```

Or without Docker at all, if you have the .NET SDK and Node:

```sh
(cd app/src/Client && npm run build)
WEB=app/src/Client/dist PORT=8099 node e2e/mock/server.js
(cd e2e && npm install && npx playwright install chromium && APP_URL=http://localhost:8099 npx playwright test)
```

Add `DEBUG=1` to the mock to log every request, including the sync stream's.

## Changing a screenshot

Edit `mock/fixtures.js` for what is in it, `tests/screenshots.spec.js` for
which page and what is waited for, `playwright.config.js` for the device. Each
test waits on something only the synced fixtures can put on the page, so a
screenshot is never of a half-drawn screen. Then re-run `./e2e/run.sh` and
commit what changed.
