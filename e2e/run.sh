#!/usr/bin/env bash
# Takes the five phone screenshots in ./screenshots, from a checkout and
# nothing else:
#
#   ./e2e/run.sh
#
# Docker is the only prerequisite. The client is built from source, served by
# the mock backend in ./mock, and photographed by Playwright on an iPhone SE
# viewport - no database, no Keycloak, no seeding, and nothing left running.
set -euo pipefail
cd "$(dirname "$0")"

RUNNER=plaintextpantry-e2e-run

compose() { docker compose "$@"; }

cleanup() {
  docker rm -f "$RUNNER" >/dev/null 2>&1 || true
  compose down --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker rm -f "$RUNNER" >/dev/null 2>&1 || true

echo "==> building (first run pulls the .NET SDK and Playwright images)"
compose build

echo "==> starting the app on its mock backend"
compose up -d --wait app

echo "==> taking screenshots"
status=0
compose run -T --name "$RUNNER" tests || status=$?

# Copied out of the container rather than bind-mounted, so the files land
# owned by whoever ran this and not by root.
docker cp "$RUNNER:/e2e/screenshots/." ./screenshots/ >/dev/null 2>&1 || true

if [ "$status" -eq 0 ]; then
  echo "==> done:"
  ls -1 screenshots/*.png | sed 's/^/    /'
else
  echo "==> the run failed (exit $status); anything that was taken is in ./screenshots"
fi

exit "$status"
