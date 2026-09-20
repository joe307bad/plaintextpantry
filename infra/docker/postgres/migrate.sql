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
