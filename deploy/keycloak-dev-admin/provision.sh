#!/bin/sh
# Development-only Keycloak user provisioning (issue #846; epic #841).
#
# Creates-or-finds a normal Waypoint-realm user (NOT the Keycloak master-realm
# bootstrap admin -- see deploy/keycloak/README.md "Role groups, not realm
# roles" for why membership in the Admin GROUP, not a realm role assignment,
# is what actually grants the Admin role claim), sets its password as
# non-temporary, and ensures Admin-group membership. Runs once per `up` as a
# one-shot service (compose.override.example.yaml's `restart: "no"`) and
# reconciles the password/group membership on EVERY run, so re-running never
# duplicates anything and always restores drift.
#
# curl+jq against the Admin REST API directly, not kcadm.sh: kcadm's own
# `config credentials` step takes --password only as a CLI flag (no file-based
# or stdin-based indirection in this image), which is exactly the argv/
# `docker top` exposure this script exists to avoid for the bootstrap admin
# and dev-admin passwords. Every curl call that carries a secret (the admin
# password grant, the dev password reset) goes through a curl `-K` config
# file instead of `-d`/`-H` on the command line, so neither password nor the
# short-lived bearer token ever appears in this container's argv.
set -eu

umask 077

KC_BASE="${WAYPOINT_KEYCLOAK_INTERNAL_URL:-http://keycloak:8080/auth}"
REALM="${WAYPOINT_DEV_ADMIN_REALM:-waypoint}"
GROUP_NAME="${WAYPOINT_DEV_ADMIN_GROUP:-Admin}"
DEV_USERNAME="${WAYPOINT_DEV_ADMIN_USERNAME:-developer}"
# Keycloak's default declarative user profile marks `email` required; a user
# missing it gets a silent VERIFY_PROFILE required-action injected at the
# NEXT login (live-verified against this stack -- the user representation
# itself shows requiredActions: [] even though login redirects to a
# profile-completion form), which would break the direct-login acceptance
# criterion. Placeholder domain only (RFC 2606-style, matching this repo's
# own sanitization convention) -- never a real address.
DEV_EMAIL="${WAYPOINT_DEV_ADMIN_EMAIL:-developer@waypoint.example.internal}"
# Same reason as DEV_EMAIL above -- the default user profile also requires
# firstName/lastName (live-verified: email alone was NOT sufficient, login
# still redirected to VERIFY_PROFILE until these two were set as well).
DEV_FIRST_NAME="${WAYPOINT_DEV_ADMIN_FIRST_NAME:-Waypoint}"
DEV_LAST_NAME="${WAYPOINT_DEV_ADMIN_LAST_NAME:-Developer}"
ADMIN_USERNAME="${KEYCLOAK_ADMIN:-admin}"
ADMIN_PASSWORD_FILE="${KEYCLOAK_ADMIN_PASSWORD_FILE:-/run/secrets/keycloak-bootstrap-admin-password}"
DEV_PASSWORD_FILE="${WAYPOINT_DEV_ADMIN_PASSWORD_FILE:-/run/secrets/dev-admin-password}"

CFG_DIR="$(mktemp -d)"
trap 'rm -rf "$CFG_DIR"' EXIT

# $1 = human label for the error message only -- never prints file content.
# $2 = path to the mounted secret file.
read_secret_file() {
	if [ ! -s "$2" ]; then
		echo "waypoint-keycloak-dev-admin: missing or empty secret file for $1: $2" >&2
		echo "waypoint-keycloak-dev-admin: refusing to provision without it." >&2
		exit 1
	fi
	cat "$2"
}

ADMIN_PASSWORD="$(read_secret_file 'Keycloak bootstrap admin password (KEYCLOAK_ADMIN_PASSWORD_FILE)' "$ADMIN_PASSWORD_FILE")"
DEV_PASSWORD="$(read_secret_file 'Waypoint dev-admin password (WAYPOINT_DEV_ADMIN_PASSWORD_FILE)' "$DEV_PASSWORD_FILE")"

echo "waypoint-keycloak-dev-admin: waiting for realm '$REALM' to be ready at $KC_BASE ..."
i=0
while :; do
	http_code="$(curl -sS -o /dev/null -w '%{http_code}' "$KC_BASE/realms/$REALM/.well-known/openid-configuration" 2>/dev/null || true)"
	[ "$http_code" = "200" ] && break
	i=$((i + 1))
	if [ "$i" -ge 90 ]; then
		echo "waypoint-keycloak-dev-admin: realm '$REALM' never became ready after $((i * 2))s" >&2
		exit 1
	fi
	sleep 2
done
echo "waypoint-keycloak-dev-admin: realm '$REALM' is ready."

# Admin token (master realm, admin-cli public client, resource-owner password
# grant against the bootstrap admin -- the same credential the Keycloak
# service itself uses at boot, see deploy/keycloak/docker-entrypoint-wrapper.sh).
# Written to a curl config file so neither ADMIN_USERNAME's value nor
# ADMIN_PASSWORD is ever a literal argv token.
TOKEN_CFG="$CFG_DIR/token.cfg"

# curl's -K format does not support multi-line quoting the way a shell string
# does, so the password/username go through --data-urlencode read from a
# per-field temp file instead -- still never on the command line, and each
# field file is removed immediately after curl reads it.
write_urlencoded_field() {
	# $1 = target cfg file (appended to), $2 = field name, $3 = field value.
	# curl's `--data-urlencode name@filename` form reads ONLY the value from
	# the file and applies the "name=" prefix itself -- the file must hold
	# just the value, never "name=value" (that whole string would otherwise
	# be percent-encoded, including the "=").
	field_file="$(mktemp "$CFG_DIR/field.XXXXXX")"
	printf '%s' "$3" >"$field_file"
	printf 'data-urlencode = "%s@%s"\n' "$2" "$field_file" >>"$1"
}

: >"$TOKEN_CFG"
write_urlencoded_field "$TOKEN_CFG" "grant_type" "password"
write_urlencoded_field "$TOKEN_CFG" "client_id" "admin-cli"
write_urlencoded_field "$TOKEN_CFG" "username" "$ADMIN_USERNAME"
write_urlencoded_field "$TOKEN_CFG" "password" "$ADMIN_PASSWORD"

TOKEN_RESPONSE="$(curl -sS -X POST -K "$TOKEN_CFG" "$KC_BASE/realms/master/protocol/openid-connect/token")"
ADMIN_TOKEN="$(printf '%s' "$TOKEN_RESPONSE" | jq -r '.access_token // empty')"
if [ -z "$ADMIN_TOKEN" ]; then
	echo "waypoint-keycloak-dev-admin: failed to obtain an admin token (response withheld -- may carry retry hints only, no secret)." >&2
	exit 1
fi

# admin_api METHOD PATH [json-body] -- always via a per-call curl config file
# so the bearer token (and any body containing the dev password) never
# appears in this process's argv.
admin_api() {
	method="$1"
	path="$2"
	body="${3:-}"
	# busybox mktemp requires the X run to be the template's final
	# characters (no trailing ".cfg" suffix allowed), unlike GNU mktemp.
	cfg="$(mktemp "$CFG_DIR/api-cfg.XXXXXX")"
	# curl's -K config-file lines are never process argv (unlike -H/-d on the
	# command line), so the bearer token is safe to write directly here --
	# curl's -H option itself has no @filename indirection to route through.
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
	curl -sS -K "$cfg" -w '\n%{http_code}' "$KC_BASE/admin$path"
}

# Split a response body from admin_api's trailing "\n<status>" line.
response_body() { sed '$d'; }
response_status() { tail -n1; }

# --- find-or-create the user -------------------------------------------
FIND_RESP="$(admin_api GET "/realms/$REALM/users?username=$DEV_USERNAME&exact=true")"
FIND_STATUS="$(printf '%s' "$FIND_RESP" | response_status)"
FIND_BODY="$(printf '%s' "$FIND_RESP" | response_body)"
if [ "$FIND_STATUS" != "200" ]; then
	echo "waypoint-keycloak-dev-admin: user lookup failed (HTTP $FIND_STATUS)" >&2
	exit 1
fi

USER_ID="$(printf '%s' "$FIND_BODY" | jq -r '.[0].id // empty')"

if [ -z "$USER_ID" ]; then
	echo "waypoint-keycloak-dev-admin: creating user '$DEV_USERNAME' in realm '$REALM'."
	CREATE_BODY="$(jq -n --arg u "$DEV_USERNAME" --arg e "$DEV_EMAIL" \
		--arg fn "$DEV_FIRST_NAME" --arg ln "$DEV_LAST_NAME" \
		'{username: $u, enabled: true, email: $e, emailVerified: true,
		  firstName: $fn, lastName: $ln, requiredActions: []}')"
	CREATE_RESP="$(admin_api POST "/realms/$REALM/users" "$CREATE_BODY")"
	CREATE_STATUS="$(printf '%s' "$CREATE_RESP" | response_status)"
	if [ "$CREATE_STATUS" != "201" ] && [ "$CREATE_STATUS" != "204" ]; then
		echo "waypoint-keycloak-dev-admin: user creation failed (HTTP $CREATE_STATUS)" >&2
		exit 1
	fi
	FIND_RESP="$(admin_api GET "/realms/$REALM/users?username=$DEV_USERNAME&exact=true")"
	USER_ID="$(printf '%s' "$FIND_RESP" | response_body | jq -r '.[0].id // empty')"
	if [ -z "$USER_ID" ]; then
		echo "waypoint-keycloak-dev-admin: created user '$DEV_USERNAME' but could not look it back up." >&2
		exit 1
	fi
else
	echo "waypoint-keycloak-dev-admin: user '$DEV_USERNAME' already exists (id=$USER_ID)."
fi

# --- reconcile profile completeness, every run ---------------------------
# Restores email/enabled/requiredActions even if something else (an admin,
# a manual test) drifted them -- same reconcile-on-every-run guarantee as
# the password and group membership below.
PROFILE_BODY="$(jq -n --arg e "$DEV_EMAIL" --arg fn "$DEV_FIRST_NAME" --arg ln "$DEV_LAST_NAME" \
	'{enabled: true, email: $e, emailVerified: true,
	  firstName: $fn, lastName: $ln, requiredActions: []}')"
PROFILE_RESP="$(admin_api PUT "/realms/$REALM/users/$USER_ID" "$PROFILE_BODY")"
PROFILE_STATUS="$(printf '%s' "$PROFILE_RESP" | response_status)"
if [ "$PROFILE_STATUS" != "204" ]; then
	echo "waypoint-keycloak-dev-admin: profile reconciliation failed (HTTP $PROFILE_STATUS)" >&2
	exit 1
fi

# --- reconcile the password, every run (non-temporary) ------------------
CRED_BODY="$(jq -n --arg pw "$DEV_PASSWORD" '{type: "password", value: $pw, temporary: false}')"
CRED_RESP="$(admin_api PUT "/realms/$REALM/users/$USER_ID/reset-password" "$CRED_BODY")"
CRED_STATUS="$(printf '%s' "$CRED_RESP" | response_status)"
if [ "$CRED_STATUS" != "204" ]; then
	echo "waypoint-keycloak-dev-admin: password reconciliation failed (HTTP $CRED_STATUS)" >&2
	exit 1
fi
echo "waypoint-keycloak-dev-admin: password reconciled (non-temporary)."

# --- ensure Admin-group membership, every run ----------------------------
GROUP_RESP="$(admin_api GET "/realms/$REALM/groups?search=$GROUP_NAME&exact=true")"
GROUP_STATUS="$(printf '%s' "$GROUP_RESP" | response_status)"
GROUP_BODY="$(printf '%s' "$GROUP_RESP" | response_body)"
if [ "$GROUP_STATUS" != "200" ]; then
	echo "waypoint-keycloak-dev-admin: group lookup failed (HTTP $GROUP_STATUS)" >&2
	exit 1
fi
GROUP_ID="$(printf '%s' "$GROUP_BODY" | jq -r --arg n "$GROUP_NAME" '.[] | select(.name == $n) | .id' | head -n1)"
if [ -z "$GROUP_ID" ]; then
	echo "waypoint-keycloak-dev-admin: group '$GROUP_NAME' does not exist in realm '$REALM' -- is the imported realm current?" >&2
	exit 1
fi

MEMBERSHIP_RESP="$(admin_api PUT "/realms/$REALM/users/$USER_ID/groups/$GROUP_ID")"
MEMBERSHIP_STATUS="$(printf '%s' "$MEMBERSHIP_RESP" | response_status)"
if [ "$MEMBERSHIP_STATUS" != "204" ]; then
	echo "waypoint-keycloak-dev-admin: group membership PUT failed (HTTP $MEMBERSHIP_STATUS)" >&2
	exit 1
fi
echo "waypoint-keycloak-dev-admin: user '$DEV_USERNAME' is a member of group '$GROUP_NAME'."

echo "waypoint-keycloak-dev-admin: provisioning complete."
