# Plaintext Pantry

A local-first, open source, minimalist recipe and grocery list manager.
Recipes are plain text in [Cooklang](https://cooklang.org) (`@flour{2%cups}`,
`#pan{}`, `~{10%minutes}`), so ingredients fall out of the recipe for free:
add a recipe to the shopping list, or collect recipes onto a menu and see
which ones the list already covers. Everything works offline and syncs
between devices when it can. An MCP endpoint lets an AI assistant read and
edit your recipes and lists on your behalf.

Live at [plaintextpantry.com](https://plaintextpantry.com).

## Built with

Everything is F#, front to back, with one Cooklang parser shared by both.

| | |
|---|---|
| **Client** | [Fable](https://fable.io) → JavaScript, [Elmish](https://elmish.github.io) + [Feliz](https://zaid-ajaj.github.io/Feliz/) (React 19), Tailwind v4, Vite, CodeMirror 6 for the recipe editor |
| **Local data** | [PowerSync](https://www.powersync.com) — a SQLite database in the browser (wa-sqlite) that syncs with Postgres. The UI reads live queries and writes locally; sync happens behind it |
| **Server** | ASP.NET Core 10 + [Giraffe](https://giraffe.wiki). Signs users in, applies PowerSync's upload queue to Postgres, mints sync tokens, and serves the MCP tools |
| **Shared** | `app/src/Shared`: the Cooklang parser, JSON codecs (Thoth), and the small bits of domain logic; compiled once for .NET and once by Fable |
| **Identity** | Keycloak, brokering Google sign-in; also the OAuth server for MCP clients |
| **Infra** | Postgres 16, Caddy (auto-HTTPS, static files), Docker Compose, Terraform, one arm64 EC2 box |

Layout:

```
app/src/Client     Fable app (App.fs is the whole UI; Db.fs the data layer)
app/src/Server     API, auth, MCP tools, Postgres access
app/src/Shared     Cooklang parser + types shared by both
app/tests          xunit tests for Shared (incl. the Cooklang canonical suite)
infra/docker       Local compose stack, Postgres schema + migrations, PowerSync sync rules, Dockerfiles
infra/keycloak     Realm provisioning and the login theme
infra/terraform    AWS resources
infra/deploy       Production compose stack and the script that applies it on the box
```

## Running locally

You need Docker, the .NET 10 SDK and Node.

```sh
./dev.sh
```

That starts Postgres, PowerSync and Keycloak in Docker (applying
`infra/docker/postgres/migrate.sql` and provisioning the realm), then the F#
server under `dotnet watch` and the client under Fable + Vite, both
reloading on save. Open http://localhost:5173 and press *Login with Google*
— locally that signs you in as the seeded `dev@localhost` user without
leaving the app.

- `./dev.sh down` stops the containers; `./dev.sh reset` also wipes their data.
- Tests: `cd app && dotnet test tests/Shared.Tests`.
- The MCP endpoint is at `http://localhost:5050/mcp` and works with the dev user.

Schema changes go in two places: `infra/docker/postgres/init/01-init.sh`
(fresh databases) and `migrate.sql` (re-runnable, applied on every start).
New tables also need a stream in `infra/docker/powersync/sync-config.yaml`,
a column list in the client's `Db.fs`, and an entry in the server's upload
whitelist (`Server/Db.fs`).

## Deploying to AWS

Production is a single `t4g.small` running the compose stack in
`infra/deploy`; the full story, including one-time setup (Terraform
bootstrap, domain delegation, the Google OAuth client), lives in
[`infra/terraform/README.md`](infra/terraform/README.md).

Day to day, **merging to `main` deploys**. The
[workflow](.github/workflows/deploy.yml) has three stages, each of which
skips itself when nothing relevant changed:

1. **infra** — `terraform plan`; applies only if the plan is non-empty.
2. **build** — builds the `server` and `web` images on the runner, tagged by
   a hash of `app/` and the Dockerfiles, and pushes to ECR unless that tag
   already exists.
3. **deploy** — ships `infra/deploy` to the box over SSM and runs
   `deploy.sh`, which pulls images, runs the migration, re-provisions the
   Keycloak realm and does `docker compose up -d` (only containers whose
   image or config changed are recreated).

To redeploy without a code change: `gh workflow run deploy.yml`. There is
no SSH; for a shell on the box use
`aws ssm start-session --target <instance id>`, then `cd /opt/plaintextpantry`
and `docker compose logs -f server`. Secrets live in SSM Parameter Store
under `/plaintextpantry/*`.
