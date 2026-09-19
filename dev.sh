#!/usr/bin/env bash
# One command to run everything locally:
#   ./dev.sh
# Starts Postgres + PowerSync + Keycloak (docker), the F# server (dotnet watch)
# and the Fable/Vite dev server. Sign in as dev@localhost / dev. Ctrl-C stops the dev servers; containers keep running
# (use `./dev.sh down` to stop them, `./dev.sh reset` to also wipe data).
set -euo pipefail
cd "$(dirname "$0")"

compose() { docker compose -f infra/docker/docker-compose.yml "$@"; }

case "${1:-up}" in
  down)  compose down; exit 0 ;;
  reset) compose down -v; exit 0 ;;
  up)    ;;
  *)     echo "usage: $0 [up|down|reset]"; exit 1 ;;
esac

# Same values the containers use, exported for the F# server and the scripts.
set -a; source infra/docker/.env; set +a
export KEYCLOAK_ISSUER="$KEYCLOAK_PUBLIC_URL/realms/$KEYCLOAK_REALM"

echo "==> infra"
# Postgres first: the schema/side databases must exist before Keycloak and
# PowerSync start, and the init script only runs on a fresh volume.
compose up -d --wait postgres
COMPOSE="docker compose -f infra/docker/docker-compose.yml" infra/docker/postgres/migrate.sh
compose up -d --wait
# Realm, client, scopes and the dev user. Idempotent.
COMPOSE="docker compose -f infra/docker/docker-compose.yml" infra/keycloak/provision.sh

echo "==> dependencies"
(cd app && dotnet tool restore >/dev/null)
[ -d app/src/Client/node_modules ] || (cd app/src/Client && npm install)

echo "==> dev servers"
trap 'kill 0 2>/dev/null' EXIT INT TERM

(cd app && dotnet watch run --project src/Server --non-interactive) &
(cd app/src/Client && npm run dev) &

echo
echo "  app:       http://localhost:5173"
echo "  api:       http://localhost:5050"
echo "  powersync: http://localhost:8080"
echo "  keycloak:  $KEYCLOAK_PUBLIC_URL  (dev@localhost / dev)"
echo "  mcp:       $MCP_RESOURCE"
echo
wait
