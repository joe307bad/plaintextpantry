#!/bin/bash
# First-boot setup for the plaintextpantry box (Amazon Linux 2023, arm64).
# Installs Docker + the compose plugin and mounts the data volume. The app
# itself is deployed afterwards by CI over SSM (see infra/deploy/deploy.sh);
# nothing here runs again on an existing instance.
set -euo pipefail
exec > >(tee /var/log/user-data.log) 2>&1
echo "bootstrap start $(date -Is)"

dnf install -y docker jq

# Compose v2 plugin (not packaged in AL2023).
COMPOSE_VERSION=$(curl -fsSL https://api.github.com/repos/docker/compose/releases/latest | jq -r .tag_name)
mkdir -p /usr/local/lib/docker/cli-plugins
curl -fsSL "https://github.com/docker/compose/releases/download/$${COMPOSE_VERSION}/docker-compose-linux-aarch64" \
  -o /usr/local/lib/docker/cli-plugins/docker-compose
chmod +x /usr/local/lib/docker/cli-plugins/docker-compose

# Data volume: attached as /dev/xvdf, shows up as an NVMe device on Nitro.
DEV=""
for _ in $(seq 1 60); do
  for candidate in /dev/nvme1n1 /dev/xvdf; do
    [ -b "$candidate" ] && DEV="$candidate" && break 2
  done
  sleep 2
done
[ -n "$DEV" ] || { echo "data volume never attached"; exit 1; }

if ! blkid "$DEV" | grep -q 'TYPE='; then
  mkfs.xfs "$DEV"
fi
UUID=$(blkid -s UUID -o value "$DEV")
mkdir -p /data
grep -q "$UUID" /etc/fstab || echo "UUID=$UUID /data xfs defaults,nofail 0 2" >> /etc/fstab
mountpoint -q /data || mount /data
mkdir -p /data/postgres /data/caddy/data /data/caddy/config
chown 999:999 /data/postgres

# Containers use restart: unless-stopped, so enabling docker is what brings
# the stack back after a reboot.
systemctl enable --now docker

mkdir -p /opt/plaintextpantry
echo "bootstrap done $(date -Is) region=${aws_region}"
