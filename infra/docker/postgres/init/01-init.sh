#!/usr/bin/env bash
# Runs once on first start of the postgres volume.
set -euo pipefail

# Separate database for PowerSync's bucket storage.
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<SQL
CREATE DATABASE "$PG_STORAGE_DB";
SQL

# Application schema + the publication PowerSync replicates from.
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<'SQL'
CREATE TABLE recipes (
    id          uuid PRIMARY KEY,
    title       text NOT NULL,
    body        text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE PUBLICATION powersync FOR ALL TABLES;
SQL
