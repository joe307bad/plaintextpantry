-- Idempotent schema migration for volumes that predate a change to
-- init/01-init.sh (that script only runs on an empty data directory).
-- Applied by dev.sh and by infra/deploy/deploy.sh on every start.
-- Keep every statement re-runnable.

-- Per-user ownership (Keycloak subject). Rows from before auth existed get
-- '' and are visible to nobody; see infra/terraform/README.md to reassign them.
ALTER TABLE recipes        ADD COLUMN IF NOT EXISTS user_id text NOT NULL DEFAULT '';
ALTER TABLE shopping_items ADD COLUMN IF NOT EXISTS user_id text NOT NULL DEFAULT '';
CREATE INDEX IF NOT EXISTS recipes_user_id        ON recipes (user_id);
CREATE INDEX IF NOT EXISTS shopping_items_user_id ON shopping_items (user_id);

-- Shopping lists as entities. Items that predate them are gathered into one
-- list per user, named after the day the migration ran.
CREATE TABLE IF NOT EXISTS shopping_lists (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS shopping_lists_user_id ON shopping_lists (user_id);
ALTER TABLE shopping_items ADD COLUMN IF NOT EXISTS list_id uuid;
CREATE INDEX IF NOT EXISTS shopping_items_list_id ON shopping_items (list_id);
INSERT INTO shopping_lists (id, user_id, name)
SELECT gen_random_uuid(), i.user_id, 'SL-' || to_char(now(), 'MMDD')
FROM shopping_items i
WHERE i.list_id IS NULL
  AND NOT EXISTS (SELECT 1 FROM shopping_lists l WHERE l.user_id = i.user_id)
GROUP BY i.user_id;
UPDATE shopping_items i
SET list_id = (SELECT id FROM shopping_lists l WHERE l.user_id = i.user_id ORDER BY created_at DESC LIMIT 1)
WHERE i.list_id IS NULL;
ALTER TABLE shopping_items ALTER COLUMN list_id SET NOT NULL;

-- Menus: a name plus one row per recipe added to it.
CREATE TABLE IF NOT EXISTS menus (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS menu_recipes (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    menu_id     uuid NOT NULL,
    recipe_id   uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS menus_user_id        ON menus (user_id);
CREATE INDEX IF NOT EXISTS menu_recipes_user_id ON menu_recipes (user_id);
CREATE INDEX IF NOT EXISTS menu_recipes_menu_id ON menu_recipes (menu_id);

-- Sides under menu entries.
CREATE TABLE IF NOT EXISTS menu_sides (
    id              uuid PRIMARY KEY,
    user_id         text NOT NULL,
    menu_recipe_id  uuid NOT NULL,
    name            text NOT NULL,
    done            integer NOT NULL DEFAULT 0,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS menu_sides_user_id        ON menu_sides (user_id);
CREATE INDEX IF NOT EXISTS menu_sides_menu_recipe_id ON menu_sides (menu_recipe_id);

-- Archiving: the current list/menu is the newest with archived_at NULL.
ALTER TABLE shopping_lists ADD COLUMN IF NOT EXISTS archived_at timestamptz;
ALTER TABLE menus          ADD COLUMN IF NOT EXISTS archived_at timestamptz;

-- Tags: one row per name per user, plus a row per recipe each tag is on.
CREATE TABLE IF NOT EXISTS tags (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS recipe_tags (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    recipe_id   uuid NOT NULL,
    tag_id      uuid NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS tags_user_id           ON tags (user_id);
CREATE INDEX IF NOT EXISTS recipe_tags_user_id    ON recipe_tags (user_id);
CREATE INDEX IF NOT EXISTS recipe_tags_recipe_id  ON recipe_tags (recipe_id);
CREATE INDEX IF NOT EXISTS recipe_tags_tag_id     ON recipe_tags (tag_id);

-- Pantries: the household everything belongs to. A pantry has an owner (its
-- `user_id`), a name, and a row in pantry_members per person in it - the
-- owner included, so membership is one question with one answer. A member's
-- own client writes their email and name onto their row, since Postgres only
-- ever sees Keycloak subjects. Joining is by scanning the owner's QR code,
-- which lands a 'pending' row the owner then approves.
CREATE TABLE IF NOT EXISTS pantries (
    id          uuid PRIMARY KEY,
    user_id     text NOT NULL,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS pantry_members (
    id          uuid PRIMARY KEY,
    pantry_id   uuid NOT NULL,
    user_id     text NOT NULL,
    email       text NOT NULL DEFAULT '',
    name        text NOT NULL DEFAULT '',
    -- 'pending' until the owner approves, 'approved' after.
    status      text NOT NULL DEFAULT 'pending',
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS pantries_user_id             ON pantries (user_id);
CREATE INDEX IF NOT EXISTS pantry_members_user_id       ON pantry_members (user_id);
CREATE INDEX IF NOT EXISTS pantry_members_pantry_id     ON pantry_members (pantry_id);
-- One membership per person per pantry: a second scan of the same code is
-- not a second request (the server's upload path relies on this).
CREATE UNIQUE INDEX IF NOT EXISTS pantry_members_pantry_user ON pantry_members (pantry_id, user_id);

-- Everyone who already has data gets a pantry of their own, named the way a
-- new user's is. The '' owner from before auth existed gets one too, so the
-- NOT NULL below holds for its rows as well.
INSERT INTO pantries (id, user_id, name)
SELECT gen_random_uuid(), u.user_id, 'My Pantry'
FROM (SELECT user_id FROM recipes
      UNION SELECT user_id FROM shopping_lists
      UNION SELECT user_id FROM shopping_items
      UNION SELECT user_id FROM menus
      UNION SELECT user_id FROM menu_recipes
      UNION SELECT user_id FROM menu_sides
      UNION SELECT user_id FROM tags
      UNION SELECT user_id FROM recipe_tags) u
WHERE NOT EXISTS (SELECT 1 FROM pantries p WHERE p.user_id = u.user_id);

-- The owner is a member of their own pantry.
INSERT INTO pantry_members (id, pantry_id, user_id, status)
SELECT gen_random_uuid(), p.id, p.user_id, 'approved'
FROM pantries p
WHERE NOT EXISTS (SELECT 1 FROM pantry_members m WHERE m.pantry_id = p.id AND m.user_id = p.user_id);

-- Every entity now hangs off a pantry rather than off a user: a member of a
-- shared pantry may add to and edit everything in it. Rows that predate
-- pantries go to their owner's own, the oldest one they have.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['recipes', 'shopping_lists', 'shopping_items', 'menus',
                             'menu_recipes', 'menu_sides', 'tags', 'recipe_tags']
    LOOP
        EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS pantry_id uuid', t);
        EXECUTE format(
            'UPDATE %I r SET pantry_id = (SELECT p.id FROM pantries p WHERE p.user_id = r.user_id ORDER BY p.created_at, p.id LIMIT 1) WHERE r.pantry_id IS NULL',
            t);
        EXECUTE format('ALTER TABLE %I ALTER COLUMN pantry_id SET NOT NULL', t);
        EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I (pantry_id)', t || '_pantry_id', t);
    END LOOP;
END $$;
