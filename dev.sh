#!/usr/bin/env bash
# One command to run everything locally:
#   ./dev.sh
# Starts Postgres + PowerSync (docker), the F# server (dotnet watch) and the
# Fable/Vite dev server. Ctrl-C stops the dev servers; containers keep running
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

echo "==> infra"
compose up -d --wait

# Same values the containers use, exported for the F# server.
set -a; source infra/docker/.env; set +a

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
echo
wait
