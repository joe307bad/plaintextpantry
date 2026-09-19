#!/usr/bin/env bash
# Provisions the plaintextpantry realm in a running Keycloak. Idempotent: every
# step creates-or-updates, so it runs on every ./dev.sh and every deploy.
#
# It sets up:
#   - the realm, with self-registration and password reset off
#   - client scopes for the MCP server (recipes:read ... shopping:write) plus an
#     `mcp` scope whose audience mapper stamps MCP_RESOURCE into access tokens;
#     that audience is what lets the F# server refuse a web-session token at /mcp
#   - the confidential `plaintextpantry-web` client the F# server signs in with,
#     with the secret WE choose (KEYCLOAK_CLIENT_SECRET) so nothing has to be
#     read back and written into an env file afterwards
#   - Google as the only way in when AUTH_GOOGLE_ID/SECRET are set (prod);
#     otherwise, when KEYCLOAK_DEV_USER=true, a dev@localhost password user
#   - anonymous dynamic client registration for claude.ai, which is how Claude
#     turns "connect to https://plaintextpantry.com/mcp" into a registered
#     OAuth client without anyone touching the admin console
#
# Runs kcadm inside the keycloak container:
#   COMPOSE="docker compose -f infra/docker/docker-compose.yml" infra/keycloak/provision.sh
#
# Env (see infra/docker/.env for the local values):
#   KEYCLOAK_ADMIN KEYCLOAK_ADMIN_PASSWORD KEYCLOAK_REALM KEYCLOAK_PUBLIC_URL
#   KEYCLOAK_CLIENT_ID KEYCLOAK_CLIENT_SECRET APP_URL MCP_RESOURCE
#   optional: AUTH_GOOGLE_ID AUTH_GOOGLE_SECRET KEYCLOAK_DEV_USER
set -euo pipefail

COMPOSE="${COMPOSE:-docker compose}"
REALM="${KEYCLOAK_REALM:?}"
PUBLIC_URL="${KEYCLOAK_PUBLIC_URL:?}"
APP_URL="${APP_URL:?}"
MCP_RESOURCE="${MCP_RESOURCE:?}"
WEB_CLIENT="${KEYCLOAK_CLIENT_ID:?}"
WEB_SECRET="${KEYCLOAK_CLIENT_SECRET:?}"
DEV_USER="${KEYCLOAK_DEV_USER:-false}"

kc() { $COMPOSE exec -T keycloak /opt/keycloak/bin/kcadm.sh "$@"; }

echo "==> keycloak: realm '$REALM' at $PUBLIC_URL"
for i in $(seq 1 60); do
  if kc config credentials --server "http://localhost:${KC_PORT:-8080}" --realm master \
       --user "${KEYCLOAK_ADMIN:?}" --password "${KEYCLOAK_ADMIN_PASSWORD:?}" >/dev/null 2>&1; then
    break
  fi
  [ "$i" -lt 60 ] || { echo "keycloak never answered" >&2; exit 1; }
  sleep 3
done

# --- helpers ------------------------------------------------------------------

# id of a resource in the realm by a field value, or empty
id_of() { # <resource> <field> <value>
  kc get "$1" -r "$REALM" --fields id,"$2" --format csv --noquotes 2>/dev/null \
    | tr -d '\r' | awk -F, -v want="$3" '$2 == want { print $1; exit }'
}

# create-or-update a realm resource identified by <field>=<value>
upsert() { # <resource> <field> <value> -s ... ; prints the id
  local resource="$1" field="$2" value="$3"; shift 3
  local id; id=$(id_of "$resource" "$field" "$value")
  if [ -n "$id" ]; then
    kc update "$resource/$id" -r "$REALM" "$@" >/dev/null
  else
    kc create "$resource" -r "$REALM" "$@" >/dev/null
    id=$(id_of "$resource" "$field" "$value")
  fi
  echo "$id"
}

mapper_if_absent() { # <parent path> <name> -s ...
  local parent="$1" name="$2"; shift 2
  if kc get "$parent/protocol-mappers/models" -r "$REALM" --fields name --format csv --noquotes 2>/dev/null \
       | tr -d '\r' | grep -qFx "$name"; then
    return
  fi
  kc create "$parent/protocol-mappers/models" -r "$REALM" -s "name=$name" -s protocol=openid-connect "$@" >/dev/null
  echo "    mapper $name"
}

# --- realm --------------------------------------------------------------------

REALM_ARGS=(
  -s "displayName=Plaintext Pantry"
  -s registrationAllowed=false -s resetPasswordAllowed=false -s verifyEmail=false
  -s loginWithEmailAllowed=true -s duplicateEmailsAllowed=false -s rememberMe=true
  -s sslRequired=external -s enabled=true
)
if kc get "realms/$REALM" >/dev/null 2>&1; then
  kc update "realms/$REALM" "${REALM_ARGS[@]}"
else
  kc create realms -s "realm=$REALM" "${REALM_ARGS[@]}"
  echo "  created realm"
fi

for action in VERIFY_PROFILE UPDATE_PROFILE VERIFY_EMAIL UPDATE_PASSWORD; do
  kc update "authentication/required-actions/$action" -r "$REALM" -s enabled=false -s defaultAction=false >/dev/null 2>&1 || true
done

# --- client scopes (what an MCP client asks for, what a token carries) ---------

# (No associative arrays: macOS ships bash 3.2.)
SCOPES="recipes:read recipes:write shopping:read shopping:write mcp"
scope_text() {
  case "$1" in
    recipes:read)   echo "Read your recipes" ;;
    recipes:write)  echo "Create, edit and delete your recipes" ;;
    shopping:read)  echo "Read your shopping list" ;;
    shopping:write) echo "Add to and check off your shopping list" ;;
    mcp)            echo "Use Plaintext Pantry from an AI assistant" ;;
  esac
}
scope_id() { id_of client-scopes name "$1"; }

for scope in $SCOPES; do
  text=$(scope_text "$scope")
  id=$(upsert client-scopes name "$scope" \
    -s "name=$scope" -s "description=$text" -s protocol=openid-connect \
    -s 'attributes."include.in.token.scope"=true' \
    -s 'attributes."display.on.consent.screen"=true' \
    -s 'attributes."consent.screen.text"='"$text")
  # Requestable by any client in the realm, including ones Claude registers.
  kc update "default-optional-client-scopes/$id" -r "$REALM" >/dev/null 2>&1 || true
done
echo "  scopes: $SCOPES"

# The audience the F# server validates at /mcp, and the email the tools show.
MCP_SCOPE_ID=$(scope_id mcp)
mapper_if_absent "client-scopes/$MCP_SCOPE_ID" mcp-audience \
  -s protocolMapper=oidc-audience-mapper \
  -s 'config."included.custom.audience"='"$MCP_RESOURCE" \
  -s 'config."access.token.claim"=true' -s 'config."id.token.claim"=false'
mapper_if_absent "client-scopes/$MCP_SCOPE_ID" mcp-email \
  -s protocolMapper=oidc-usermodel-property-mapper \
  -s 'config."user.attribute"=email' -s 'config."claim.name"=email' -s 'config."jsonType.label"=String' \
  -s 'config."access.token.claim"=true' -s 'config."id.token.claim"=false' -s 'config."userinfo.token.claim"=true'

# --- the web client (the F# server's OIDC login) -------------------------------

REDIRECTS="[\"$APP_URL/api/auth/callback\""
[ "$DEV_USER" = "true" ] && REDIRECTS="$REDIRECTS,\"http://localhost:5173/*\",\"http://localhost:5050/*\""
REDIRECTS="$REDIRECTS]"

WEB_ID=$(upsert clients clientId "$WEB_CLIENT" \
  -s "clientId=$WEB_CLIENT" -s "name=Plaintext Pantry" -s enabled=true -s protocol=openid-connect \
  -s publicClient=false -s "secret=$WEB_SECRET" \
  -s standardFlowEnabled=true -s implicitFlowEnabled=false -s serviceAccountsEnabled=false \
  -s "directAccessGrantsEnabled=$DEV_USER" \
  -s "redirectUris=$REDIRECTS" -s "webOrigins=[\"$APP_URL\"]" \
  -s 'attributes."post.logout.redirect.uris"='"$APP_URL/*" \
  -s 'attributes."pkce.code.challenge.method"=S256')
for scope in $SCOPES; do
  kc update "clients/$WEB_ID/optional-client-scopes/$(scope_id "$scope")" -r "$REALM" >/dev/null 2>&1 || true
done
echo "  client $WEB_CLIENT (redirects $REDIRECTS)"

# --- who can sign in ------------------------------------------------------------

if [ -n "${AUTH_GOOGLE_ID:-}" ] && [ -n "${AUTH_GOOGLE_SECRET:-}" ]; then
  IDP_ARGS=(
    -s alias=google -s providerId=google -s enabled=true -s trustEmail=true -s storeToken=false
    -s "config.clientId=$AUTH_GOOGLE_ID" -s "config.clientSecret=$AUTH_GOOGLE_SECRET"
    -s 'config.defaultScope=openid email profile' -s config.syncMode=FORCE
  )
  if kc get identity-provider/instances/google -r "$REALM" >/dev/null 2>&1; then
    kc update identity-provider/instances/google -r "$REALM" "${IDP_ARGS[@]}"
  else
    kc create identity-provider/instances -r "$REALM" "${IDP_ARGS[@]}" >/dev/null
  fi

  # Straight to Google: point the browser flow's redirector at it and switch
  # the username/password form off, so Keycloak's own login page never shows.
  EXECS=$(kc get authentication/flows/browser/executions -r "$REALM")
  REDIRECTOR=$(jq -r '.[] | select(.providerId=="identity-provider-redirector") | "\(.id) \(.authenticationConfig // "")"' <<<"$EXECS")
  FORMS=$(jq -r '.[] | select(.level==0 and (.displayName|ascii_downcase)=="forms") | .id' <<<"$EXECS")
  if [ -n "$FORMS" ]; then
    printf '{"id":"%s","requirement":"DISABLED"}' "$FORMS" \
      | kc update authentication/flows/browser/executions -r "$REALM" -f - >/dev/null
  fi
  R_ID=${REDIRECTOR%% *}; R_CFG=${REDIRECTOR#* }
  if [ -n "$R_CFG" ] && [ "$R_CFG" != "$R_ID" ]; then
    kc update "authentication/config/$R_CFG" -r "$REALM" -s alias=google-only -s config.defaultProvider=google >/dev/null
  else
    kc create "authentication/executions/$R_ID/config" -r "$REALM" -s alias=google-only -s config.defaultProvider=google >/dev/null
  fi
  echo "  sign-in: Google only (redirect URI $PUBLIC_URL/realms/$REALM/broker/google/endpoint)"
elif [ "$DEV_USER" = "true" ]; then
  U_ID=$(upsert users username dev@localhost \
    -s username=dev@localhost -s email=dev@localhost -s firstName=Dev -s lastName=User \
    -s enabled=true -s emailVerified=true)
  kc set-password -r "$REALM" --userid "$U_ID" --new-password dev >/dev/null
  echo "  sign-in: dev@localhost / dev"
else
  echo "  WARNING: no AUTH_GOOGLE_ID/SECRET and KEYCLOAK_DEV_USER!=true - nobody can sign in"
fi

# --- dynamic client registration (how Claude connects) -------------------------

# Keycloak ships an anonymous-registration policy that only trusts hosts it is
# told about. Claude registers from claude.ai and needs its redirect URIs there.
REALM_ID=$(kc get "realms/$REALM" --fields id --format csv --noquotes | tr -d '\r')
TRUSTED=$(kc get components -r "$REALM" -q "parent=$REALM_ID" \
    -q type=org.keycloak.services.clientregistration.policy.ClientRegistrationPolicy \
  | jq -r '.[] | select(.providerId=="trusted-hosts" and .subType=="anonymous") | .id')
if [ -n "$TRUSTED" ]; then
  kc update "components/$TRUSTED" -r "$REALM" \
    -s 'config."trusted-hosts"=["claude.ai","www.claude.ai","claude.com","localhost","127.0.0.1"]' \
    -s 'config."host-sending-registration-request-must-match"=["false"]' \
    -s 'config."client-uris-must-match"=["true"]' >/dev/null
  echo "  dynamic client registration open to claude.ai"
fi

echo "==> keycloak ready: $PUBLIC_URL/realms/$REALM  (admin: $PUBLIC_URL/admin/$REALM/console/)"
