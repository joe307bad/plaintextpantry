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
-- A pantry is the household everything belongs to: it has an owner (its
-- `user_id`, a Keycloak subject), a name, and a pantry_members row per person
-- in it, the owner included. Every other table carries the pantry_id its rows
-- belong to, and the sync rules (infra/docker/powersync/sync-config.yaml) hand
-- a device the rows of the pantries it is an approved member of.
CREATE TABLE pantries (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- One row per person in a pantry. Postgres only ever sees Keycloak subjects,
-- so a member's own client writes their email and name here for the owner to
-- recognise them by. Joining is by scanning the owner's QR code, which lands
-- a 'pending' row; the owner approves it, and it becomes 'approved'.
CREATE TABLE pantry_members (
    id          uuid PRIMARY KEY,
    pantry_id   uuid NOT NULL,
    user_id     text NOT NULL,
    email       text NOT NULL DEFAULT '',
    name        text NOT NULL DEFAULT '',
    status      text NOT NULL DEFAULT 'pending',
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- user_id is the Keycloak subject of whoever made the row; pantry_id is who
-- may see and change it.
CREATE TABLE recipes (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    pantry_id   uuid NOT NULL,
    title       text NOT NULL,
    body        text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- The app adds to the user's newest un-archived list and makes one when
-- they have none; archiving the current list is how a new one starts.
CREATE TABLE shopping_lists (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    pantry_id   uuid NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    archived_at timestamptz
);

-- One row per ingredient line; `quantity` is text so "some" / "few" survive.
CREATE TABLE shopping_items (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    pantry_id   uuid NOT NULL,
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
    pantry_id   uuid NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    archived_at timestamptz
);

CREATE TABLE menu_recipes (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    pantry_id   uuid NOT NULL,
    menu_id     uuid NOT NULL,
    recipe_id   uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- A side dish noted under a menu entry ("rice" under the curry). `done`
-- is checked off from the shopping list, which shows the current menu's
-- sides beneath its own items.
CREATE TABLE menu_sides (
    id              uuid PRIMARY KEY,
    user_id         text NOT NULL,
    pantry_id       uuid NOT NULL,
    menu_recipe_id  uuid NOT NULL,
    name            text NOT NULL,
    done            integer NOT NULL DEFAULT 0,
    created_at      timestamptz NOT NULL DEFAULT now()
);

-- A tag is a name a user gives a recipe ("quick", "Kaitlyn's fav"); one row
-- per name per user, with a recipe_tags row per recipe it is on. Names are
-- free text, and the app treats two that differ only in case as one tag.
CREATE TABLE tags (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    pantry_id   uuid NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE recipe_tags (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    pantry_id   uuid NOT NULL,
    recipe_id   uuid NOT NULL,
    tag_id      uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX pantries_user_id ON pantries (user_id);
CREATE INDEX pantry_members_user_id ON pantry_members (user_id);
CREATE INDEX pantry_members_pantry_id ON pantry_members (pantry_id);
-- One membership per person per pantry: a second scan of the same code is not
-- a second request (the server's upload path relies on this).
CREATE UNIQUE INDEX pantry_members_pantry_user ON pantry_members (pantry_id, user_id);
CREATE INDEX recipes_user_id ON recipes (user_id);
CREATE INDEX shopping_lists_user_id ON shopping_lists (user_id);
CREATE INDEX shopping_items_user_id ON shopping_items (user_id);
CREATE INDEX shopping_items_list_id ON shopping_items (list_id);
CREATE INDEX menus_user_id ON menus (user_id);
CREATE INDEX menu_recipes_user_id ON menu_recipes (user_id);
CREATE INDEX menu_recipes_menu_id ON menu_recipes (menu_id);
CREATE INDEX menu_sides_user_id ON menu_sides (user_id);
CREATE INDEX menu_sides_menu_recipe_id ON menu_sides (menu_recipe_id);
CREATE INDEX tags_user_id ON tags (user_id);
CREATE INDEX recipe_tags_user_id ON recipe_tags (user_id);
CREATE INDEX recipe_tags_recipe_id ON recipe_tags (recipe_id);
CREATE INDEX recipe_tags_tag_id ON recipe_tags (tag_id);
CREATE INDEX recipes_pantry_id ON recipes (pantry_id);
CREATE INDEX shopping_lists_pantry_id ON shopping_lists (pantry_id);
CREATE INDEX shopping_items_pantry_id ON shopping_items (pantry_id);
CREATE INDEX menus_pantry_id ON menus (pantry_id);
CREATE INDEX menu_recipes_pantry_id ON menu_recipes (pantry_id);
CREATE INDEX menu_sides_pantry_id ON menu_sides (pantry_id);
CREATE INDEX tags_pantry_id ON tags (pantry_id);
CREATE INDEX recipe_tags_pantry_id ON recipe_tags (pantry_id);

CREATE PUBLICATION powersync FOR ALL TABLES;
SQL
