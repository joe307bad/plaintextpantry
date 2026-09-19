#!/usr/bin/env bash
# Brings a running Postgres up to date: creates the side databases if they
# are missing and applies migrate.sql. Safe to run on every start.
#
#   COMPOSE="docker compose -f ..." ./migrate.sh
#
# `COMPOSE` defaults to plain `docker compose` in the current directory, which
# is what the prod box uses; dev.sh passes its -f.
set -euo pipefail
# No cd: $COMPOSE may name its file relative to the caller's directory.
HERE="$(cd "$(dirname "$0")" && pwd)"
COMPOSE="${COMPOSE:-docker compose}"

# client_min_messages: the IF NOT EXISTS clauses are the point, not noise.
psql() { $COMPOSE exec -T -e PGOPTIONS="-c client_min_messages=warning" postgres psql -v ON_ERROR_STOP=1 -U "${PG_USER:-postgres}" "$@"; }

for db in "${PG_STORAGE_DB:-powersync_storage}" keycloak; do
  if [ "$(psql -d postgres -Atc "SELECT 1 FROM pg_database WHERE datname = '$db'")" != "1" ]; then
    psql -d postgres -c "CREATE DATABASE \"$db\""
    echo "created database $db"
  fi
done

psql -d "${PG_APP_DB:-pantry}" -q < "$HERE/migrate.sql"
echo "schema up to date"
