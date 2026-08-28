#!/bin/sh
# Reconciles the waypoint realm's origin-bearing client fields
# (rootUrl/redirectUris/webOrigins/post.logout.redirect.uris) to the
# CURRENT WAYPOINT_PUBLIC_URL on every `up`, regardless of whether
# Keycloak's own `--import-realm` actually ran this boot.
#
# why: docs/rationale/deploy.md#kcrealmreconcile-why-needed
# why: docs/rationale/deploy.md#kcdevadmin-secret-passing-design
set -eu

umask 077

KC_BASE="${WAYPOINT_KEYCLOAK_INTERNAL_URL:-http://keycloak:8080/auth}"
REALM="${WAYPOINT_REALM_RECONCILE_REALM:-waypoint}"
PUBLIC_URL="${WAYPOINT_PUBLIC_URL:?WAYPOINT_PUBLIC_URL must be set}"
ADMIN_USERNAME="${KEYCLOAK_ADMIN:-admin}"
ADMIN_PASSWORD_FILE="${KEYCLOAK_ADMIN_PASSWORD_FILE:-/run/secrets/keycloak-bootstrap-admin-password}"

CFG_DIR="$(mktemp -d)"
trap 'rm -rf "$CFG_DIR"' EXIT

# $1 = human label for the error message only -- never prints file content.
# $2 = path to the mounted secret file.
require_secret_file() {
	if [ ! -s "$2" ] || [ -z "$(tr -d '[:space:]' <"$2")" ]; then
		echo "waypoint-keycloak-realm-reconcile: missing or empty secret file for $1: $2" >&2
		echo "waypoint-keycloak-realm-reconcile: refusing to reconcile without it." >&2
		exit 1
	fi
}

read_secret_file() {
	require_secret_file "$1" "$2"
	cat "$2"
}

ADMIN_PASSWORD="$(read_secret_file 'Keycloak bootstrap admin password (KEYCLOAK_ADMIN_PASSWORD_FILE)' "$ADMIN_PASSWORD_FILE")"

echo "waypoint-keycloak-realm-reconcile: waiting for realm '$REALM' to be ready at $KC_BASE ..."
i=0
while :; do
	http_code="$(curl -sS -o /dev/null -w '%{http_code}' "$KC_BASE/realms/$REALM/.well-known/openid-configuration" 2>/dev/null || true)"
	[ "$http_code" = "200" ] && break
	i=$((i + 1))
	if [ "$i" -ge 90 ]; then
		echo "waypoint-keycloak-realm-reconcile: realm '$REALM' never became ready after $((i * 2))s" >&2
		exit 1
	fi
	sleep 2
done
echo "waypoint-keycloak-realm-reconcile: realm '$REALM' is ready."

# Admin token (master realm, admin-cli public client, resource-owner
# password grant against the bootstrap admin). Same per-call curl-config
# discipline as keycloak-dev-admin/provision.sh: a secret never appears in
# this process's argv or in `docker top`.
TOKEN_CFG="$CFG_DIR/token.cfg"

write_urlencoded_field() {
	# $1 = target cfg file (appended to), $2 = field name, $3 = field value.
	field_file="$(mktemp "$CFG_DIR/field.XXXXXX")"
	printf '%s' "$3" >"$field_file"
	printf 'data-urlencode = "%s@%s"\n' "$2" "$field_file" >>"$1"
}

: >"$TOKEN_CFG"
write_urlencoded_field "$TOKEN_CFG" "grant_type" "password"
write_urlencoded_field "$TOKEN_CFG" "client_id" "admin-cli"
write_urlencoded_field "$TOKEN_CFG" "username" "$ADMIN_USERNAME"
write_urlencoded_field "$TOKEN_CFG" "password" "$ADMIN_PASSWORD"

TOKEN_RESPONSE="$(curl -sS -X POST -K "$TOKEN_CFG" "$KC_BASE/realms/master/protocol/openid-connect/token" || true)"
rm -f "$TOKEN_CFG" "$CFG_DIR"/field.*
ADMIN_TOKEN="$(printf '%s' "$TOKEN_RESPONSE" | jq -r '.access_token // empty')"
if [ -z "$ADMIN_TOKEN" ]; then
	echo "waypoint-keycloak-realm-reconcile: failed to obtain an admin token (response withheld -- may carry retry hints only, no secret)." >&2
	exit 1
fi

# admin_api METHOD PATH [json-body]
admin_api() {
	method="$1"
	path="$2"
	body="${3:-}"
	cfg="$(mktemp "$CFG_DIR/api-cfg.XXXXXX")"
	{
		printf 'header = "Authorization: Bearer %s"\n' "$ADMIN_TOKEN"
		printf 'header = "Content-Type: application/json"\n'
		printf 'request = "%s"\n' "$method"
	} >"$cfg"
	if [ -n "$body" ]; then
		body_file="$(mktemp "$CFG_DIR/body.XXXXXX")"
		printf '%s' "$body" >"$body_file"
		printf 'data-binary = "@%s"\n' "$body_file" >>"$cfg"
	fi
	api_out="$(curl -sS -K "$cfg" -w '\n%{http_code}' "$KC_BASE/admin$path" || true)"
	rm -f "$cfg"
	if [ -n "$body" ]; then
		rm -f "$body_file"
	fi
	printf '%s' "$api_out"
}

response_body() { sed '$d'; }
response_status() { tail -n1; }

# reconcile_client CLIENT_ID -- fetches the client, and if any origin field
# has drifted from PUBLIC_URL, PUTs the corrected representation back.
# Idempotent: a client already matching PUBLIC_URL is left untouched (no
# PUT issued), so a healthy re-run is a no-op.
reconcile_client() {
	client_id="$1"
	find_resp="$(admin_api GET "/realms/$REALM/clients?clientId=$(printf '%s' "$client_id")&exact=true")"
	find_status="$(printf '%s' "$find_resp" | response_status)"
	find_body="$(printf '%s' "$find_resp" | response_body)"
	if [ "$find_status" != "200" ]; then
		echo "waypoint-keycloak-realm-reconcile: lookup of client '$client_id' failed (HTTP $find_status)" >&2
		exit 1
	fi
	internal_id="$(printf '%s' "$find_body" | jq -r '.[0].id // empty')"
	if [ -z "$internal_id" ]; then
		echo "waypoint-keycloak-realm-reconcile: client '$client_id' does not exist in realm '$REALM' -- is the imported realm current?" >&2
		exit 1
	fi

	current="$(printf '%s' "$find_body" | jq '.[0]')"
	desired="$(printf '%s' "$current" | jq --arg url "$PUBLIC_URL" '
		.rootUrl = $url
		| .redirectUris = [$url + (if (.redirectUris[0] // "") | endswith("/oidc/callback") then "/oidc/callback" else "/*" end)]
		| .webOrigins = [$url]
		| (if .attributes["post.logout.redirect.uris"] then .attributes["post.logout.redirect.uris"] = $url else . end)
	')"

	if [ "$(printf '%s' "$current" | jq -S .)" = "$(printf '%s' "$desired" | jq -S .)" ]; then
		echo "waypoint-keycloak-realm-reconcile: client '$client_id' already advertises '$PUBLIC_URL' -- no change."
		return 0
	fi

	update_resp="$(admin_api PUT "/realms/$REALM/clients/$internal_id" "$desired")"
	update_status="$(printf '%s' "$update_resp" | response_status)"
	if [ "$update_status" != "204" ]; then
		echo "waypoint-keycloak-realm-reconcile: update of client '$client_id' failed (HTTP $update_status): $(printf '%s' "$update_resp" | response_body)" >&2
		exit 1
	fi
	echo "waypoint-keycloak-realm-reconcile: client '$client_id' reconciled to '$PUBLIC_URL' (rootUrl/redirectUris/webOrigins updated)."
}

reconcile_client "waypoint-backend"
reconcile_client "waypoint-frontend"

echo "waypoint-keycloak-realm-reconcile: reconciliation complete."
