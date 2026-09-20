#!/usr/bin/env bash
# Runs once on first start of the postgres volume.
set -euo pipefail

# Separate databases for PowerSync's bucket storage and for Keycloak.
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<SQL
CREATE DATABASE "$PG_STORAGE_DB";
CREATE DATABASE keycloak;
SQL

# Application schema + the publication PowerSync replicates from.
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<'SQL'
-- user_id is the Keycloak subject; every row belongs to exactly one user and
-- the sync rules (infra/docker/powersync/sync-config.yaml) filter on it.
CREATE TABLE recipes (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    title       text NOT NULL,
    body        text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- Just a name for now; the app adds to the user's newest list and makes
-- one when they have none.
CREATE TABLE shopping_lists (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- One row per ingredient line; `quantity` is text so "some" / "few" survive.
CREATE TABLE shopping_items (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    list_id     uuid NOT NULL,
    name        text NOT NULL,
    quantity    text NOT NULL DEFAULT '',
    unit        text NOT NULL DEFAULT '',
    done        integer NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- A menu is a shopping list's twin for recipes: a name, plus one
-- menu_recipes row per recipe added (the same recipe may be added twice).
-- No foreign keys, like the rest of the schema: rows arrive from the sync
-- upload queue in client order and deletes are cascaded by the app.
CREATE TABLE menus (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE menu_recipes (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    menu_id     uuid NOT NULL,
    recipe_id   uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX recipes_user_id ON recipes (user_id);
CREATE INDEX shopping_lists_user_id ON shopping_lists (user_id);
CREATE INDEX shopping_items_user_id ON shopping_items (user_id);
CREATE INDEX shopping_items_list_id ON shopping_items (list_id);
CREATE INDEX menus_user_id ON menus (user_id);
CREATE INDEX menu_recipes_user_id ON menu_recipes (user_id);
CREATE INDEX menu_recipes_menu_id ON menu_recipes (menu_id);

CREATE PUBLICATION powersync FOR ALL TABLES;
SQL
