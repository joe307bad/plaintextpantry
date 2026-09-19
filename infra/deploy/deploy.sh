#!/usr/bin/env bash
# Runs ON THE EC2 BOX (via SSM Run Command, as root) from the extracted deploy
# bundle. Idempotent: writes .env, logs in to ECR, pulls the tagged images and
# lets `docker compose up -d` recreate only the containers whose image or
# config actually changed.
#
# Required env: IMAGE_TAG, ECR_REGISTRY, DOMAIN, AWS_REGION, SSM_PREFIX
set -euo pipefail
cd "$(dirname "$0")"

for v in IMAGE_TAG ECR_REGISTRY DOMAIN AWS_REGION SSM_PREFIX; do
  [ -n "${!v:-}" ] || { echo "missing $v" >&2; exit 1; }
done

echo "==> secrets from SSM ($SSM_PREFIX)"
ssm() {
  aws ssm get-parameter --region "$AWS_REGION" --with-decryption \
    --name "$SSM_PREFIX/$1" --query Parameter.Value --output text
}
PG_PASSWORD=$(ssm PG_PASSWORD)
POWERSYNC_JWT_SECRET=$(ssm POWERSYNC_JWT_SECRET)
POWERSYNC_JWT_SECRET_B64=$(ssm POWERSYNC_JWT_SECRET_B64)

# Bind-mounted config isn't part of compose's change detection; hashing it
# into an env var is (see docker-compose.yml).
CADDY_CONFIG_HASH=$(sha256sum Caddyfile | cut -c1-16)
POWERSYNC_CONFIG_HASH=$(cat powersync/*.yaml | sha256sum | cut -c1-16)

umask 077
cat > .env <<ENV
DOMAIN=$DOMAIN
IMAGE_TAG=$IMAGE_TAG
ECR_REGISTRY=$ECR_REGISTRY
PG_USER=postgres
PG_PASSWORD=$PG_PASSWORD
PG_APP_DB=pantry
PG_STORAGE_DB=powersync_storage
POWERSYNC_JWT_SECRET=$POWERSYNC_JWT_SECRET
POWERSYNC_JWT_SECRET_B64=$POWERSYNC_JWT_SECRET_B64
CADDY_CONFIG_HASH=$CADDY_CONFIG_HASH
POWERSYNC_CONFIG_HASH=$POWERSYNC_CONFIG_HASH
ENV
umask 022

# Bind-mount targets on the persistent volume. Postgres must own its dir
# (uid 999 in the official image); a bind mount to a missing path would be
# created root-owned and initdb would refuse it.
mkdir -p /data/postgres /data/caddy/data /data/caddy/config
chown 999:999 /data/postgres

echo "==> ecr login"
aws ecr get-login-password --region "$AWS_REGION" \
  | docker login --username AWS --password-stdin "$ECR_REGISTRY" >/dev/null

echo "==> pull $IMAGE_TAG"
docker compose pull --quiet

echo "==> up"
docker compose up -d --remove-orphans --wait --wait-timeout 180

echo "==> smoke test"
# Through the compose network: the public hostname may not resolve yet on a
# brand-new stack (GoDaddy NS change pending), so don't depend on it.
docker compose exec -T web wget -qO- http://server:5050/api/sync/credentials \
  | grep -q '"token"' && echo "server: ok"
docker compose exec -T web wget -qO- http://powersync:8080/probes/liveness >/dev/null \
  && echo "powersync: ok"

echo "==> prune old images"
docker image prune -af --filter "until=24h" >/dev/null || true

docker compose ps
