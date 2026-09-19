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
