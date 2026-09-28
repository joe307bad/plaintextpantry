#!/usr/bin/env bash
# Who has signed in, and how often. Runs ON THE EC2 BOX, out of the deployed
# bundle:
#
#   ./user-activity.sh          every sign-in there is a record of
#   ./user-activity.sh 7        only the last 7 days
#
# From a laptop it is `./user-activity.sh` at the top of the repository, which
# sends this one over SSM and prints what comes back.
#
# The records are Keycloak's. It is the only thing that sees a sign-in as a
# person rather than as a number: the usage dashboard counts logins but not
# whose, on purpose, and the app's own database never learns an address that
# isn't already in a pantry. Keycloak does not keep login events unless asked,
# so provision.sh asks (`eventsEnabled`, 90 days) - which also means this only
# knows about sign-ins since that first landed, not the whole history.
#
# `days` is what the second column is really counting for anyone who stays
# signed in: the app spends its offline refresh token once a day, so a day the
# app was opened leaves a REFRESH_TOKEN event even when no login happened.
# Someone who signed in once in March and has used it every day since shows
# one login and a long run of days.
set -euo pipefail
cd "$(dirname "$0")"

SINCE_DAYS="${1:-}"
[ -z "$SINCE_DAYS" ] || [ "$SINCE_DAYS" -gt 0 ] 2>/dev/null || { echo "usage: $0 [days]" >&2; exit 1; }
window=${SINCE_DAYS:+AND e.event_time >= (extract(epoch from now() - interval '$SINCE_DAYS days') * 1000)}

docker compose exec -T postgres psql -U "${PG_USER:-postgres}" -d keycloak -P pager=off -c "
SELECT
  coalesce(u.email, u.username, e.user_id)                          AS who,
  count(*) FILTER (WHERE e.type = 'LOGIN')                          AS logins,
  count(DISTINCT date_trunc('day', to_timestamp(e.event_time/1000))) AS days
FROM event_entity e
-- An event's realm_id has been the realm's id in some Keycloak versions and
-- its name in others, and for this realm the two are not the same string.
JOIN realm r ON (r.id = e.realm_id OR r.name = e.realm_id) AND r.name = 'plaintextpantry'
LEFT JOIN user_entity u ON u.id = e.user_id
WHERE e.type IN ('LOGIN', 'REFRESH_TOKEN')
  AND e.user_id IS NOT NULL
  $window
GROUP BY 1
ORDER BY days DESC, logins DESC;
"
