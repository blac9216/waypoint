#!/bin/sh
# Development-only Keycloak user provisioning.
#
# Creates-or-finds a normal Waypoint-realm user (NOT the Keycloak master-realm
# bootstrap admin -- see deploy/keycloak/README.md "Role groups, not realm
# roles"), sets its password as non-temporary, and ensures Admin-group
# membership. Runs once per `up` as a one-shot service and reconciles the
# password/group membership on EVERY run, so re-running never duplicates
# anything and always restores drift.
#
# why: docs/rationale/deploy.md#kcdevadmin-rename-semantics
# why: docs/rationale/deploy.md#kcdevadmin-secret-passing-design
set -eu

umask 077

# LC_ALL=C: urlencode() below walks the string one byte at a time; under a
# UTF-8 locale, shell glob matching and printf's byte-encoding disagree for
# multi-byte characters.
LC_ALL=C
export LC_ALL

KC_BASE="${WAYPOINT_KEYCLOAK_INTERNAL_URL:-http://keycloak:8080/auth}"
REALM="${WAYPOINT_DEV_ADMIN_REALM:-waypoint}"
GROUP_NAME="${WAYPOINT_DEV_ADMIN_GROUP:-Admin}"
DEV_USERNAME="${WAYPOINT_DEV_ADMIN_USERNAME:-developer}"
# why: docs/rationale/deploy.md#kcdevadmin-verify-profile-requirement
# why: docs/rationale/deploy.md#kcdevadmin-default-email-derivation
case "$DEV_USERNAME" in
*@*) DEFAULT_DEV_EMAIL="$DEV_USERNAME" ;;
*) DEFAULT_DEV_EMAIL="${DEV_USERNAME}@waypoint.example.internal" ;;
esac
DEV_EMAIL="${WAYPOINT_DEV_ADMIN_EMAIL:-$DEFAULT_DEV_EMAIL}"

# Fail early on a username that cannot produce a valid address (reject-list,
# not allow-list -- non-ASCII usernames are supported).
username_error=""
case "$DEV_USERNAME" in
'') username_error="it is empty" ;;
*[[:space:]]*) username_error="it contains whitespace" ;;
*@*@*) username_error="it contains more than one '@'" ;;
@* | *@) username_error="it starts or ends with '@'" ;;
*[]\"\\,\;:\<\>\(\)[]*) username_error="it contains one of the characters \" \\ , ; : < > ( ) [ ]" ;;
esac
if [ -n "$username_error" ]; then
	echo "waypoint-keycloak-dev-admin: refusing to provision: WAYPOINT_DEV_ADMIN_USERNAME is not usable because $username_error." >&2
	echo "waypoint-keycloak-dev-admin: the username must be either a bare local-part (a placeholder email is derived from it) or a complete email address; Keycloak rejects anything else. Set WAYPOINT_DEV_ADMIN_USERNAME to a valid value and re-run." >&2
	exit 1
fi
# why: docs/rationale/deploy.md#kcdevadmin-verify-profile-requirement
DEV_FIRST_NAME="${WAYPOINT_DEV_ADMIN_FIRST_NAME:-Waypoint}"
DEV_LAST_NAME="${WAYPOINT_DEV_ADMIN_LAST_NAME:-Developer}"
ADMIN_USERNAME="${KEYCLOAK_ADMIN:-admin}"
ADMIN_PASSWORD_FILE="${KEYCLOAK_ADMIN_PASSWORD_FILE:-/run/secrets/keycloak-bootstrap-admin-password}"
DEV_PASSWORD_FILE="${WAYPOINT_DEV_ADMIN_PASSWORD_FILE:-/run/secrets/dev-admin-password}"

CFG_DIR="$(mktemp -d)"
trap 'rm -rf "$CFG_DIR"' EXIT

# $1 = human label for the error message only -- never prints file content.
# $2 = path to the mounted secret file.
require_secret_file() {
	if [ ! -s "$2" ] || [ -z "$(tr -d '[:space:]' <"$2")" ]; then
		echo "waypoint-keycloak-dev-admin: missing or empty secret file for $1: $2" >&2
		echo "waypoint-keycloak-dev-admin: refusing to provision without it." >&2
		exit 1
	fi
}

read_secret_file() {
	require_secret_file "$1" "$2"
	cat "$2"
}

ADMIN_PASSWORD="$(read_secret_file 'Keycloak bootstrap admin password (KEYCLOAK_ADMIN_PASSWORD_FILE)' "$ADMIN_PASSWORD_FILE")"
# The dev password is deliberately NEVER read into a shell variable -- it goes
# straight from its file into jq's `--rawfile` at reset-password time below.
require_secret_file 'Waypoint dev-admin password (WAYPOINT_DEV_ADMIN_PASSWORD_FILE)' "$DEV_PASSWORD_FILE"

# Percent-encodes $1 for safe use as a URL query-string value.
# why: docs/rationale/deploy.md#kcdevadmin-urlencode-semantics
urlencode() {
	_rest="$1"
	_enc=""
	while [ -n "$_rest" ]; do
		_ch="${_rest%"${_rest#?}"}"
		_rest="${_rest#?}"
		case "$_ch" in
		[a-zA-Z0-9.~_-]) _enc="$_enc$_ch" ;;
		*) _enc="$_enc$(printf '%%%02X' "'$_ch")" ;;
		esac
	done
	printf '%s' "$_enc"
}

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
# grant against the bootstrap admin).
# why: docs/rationale/deploy.md#kcdevadmin-secret-passing-design
TOKEN_CFG="$CFG_DIR/token.cfg"

write_urlencoded_field() {
	# $1 = target cfg file (appended to), $2 = field name, $3 = field value.
	# curl's `--data-urlencode name@filename` form reads ONLY the value from
	# the file and applies the "name=" prefix itself -- the file must hold
	# just the value, never "name=value".
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
# curl has read them; drop the bootstrap-admin credential from disk now
# rather than waiting for the EXIT trap.
rm -f "$TOKEN_CFG" "$CFG_DIR"/field.*
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

# Split a response body from admin_api's trailing "\n<status>" line.
response_body() { sed '$d'; }
response_status() { tail -n1; }

# Keycloak reports validation and conflict failures as a small JSON object.
# Sanitized on the way out: control characters stripped and truncated.
api_error_summary() {
	printf '%s' "$1" | jq -r '.errorMessage // .error // .error_description // empty' 2>/dev/null |
		tr -d '\000-\037' | cut -c1-200
}

# Explain a failed create/update in operator terms. $1 = HTTP status,
# $2 = response body, $3 = the operation ("creation"/"update", for wording).
explain_user_write_failure() {
	_summary="$(api_error_summary "$2")"
	if [ -n "$_summary" ]; then
		echo "waypoint-keycloak-dev-admin: Keycloak said: $_summary" >&2
	fi
	case "$1" in
	400)
		echo "waypoint-keycloak-dev-admin: HTTP 400 means Keycloak rejected the user representation as invalid. Most likely cause: the email address '$DEV_EMAIL' is not acceptable to this realm's user profile (set WAYPOINT_DEV_ADMIN_EMAIL to a valid address), or a realm user-profile attribute is required but unset." >&2
		;;
	409)
		echo "waypoint-keycloak-dev-admin: most likely cause: another user in realm '$REALM' already holds email '$DEV_EMAIL'. Keycloak enforces email uniqueness; set WAYPOINT_DEV_ADMIN_EMAIL to a value not already in use, or remove the conflicting user." >&2
		;;
	esac
}

# --- find-or-create the user -------------------------------------------
FIND_RESP="$(admin_api GET "/realms/$REALM/users?username=$(urlencode "$DEV_USERNAME")&exact=true")"
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
		explain_user_write_failure "$CREATE_STATUS" "$(printf '%s' "$CREATE_RESP" | response_body)"
		exit 1
	fi
	FIND_RESP="$(admin_api GET "/realms/$REALM/users?username=$(urlencode "$DEV_USERNAME")&exact=true")"
	USER_ID="$(printf '%s' "$FIND_RESP" | response_body | jq -r '.[0].id // empty')"
	if [ -z "$USER_ID" ]; then
		echo "waypoint-keycloak-dev-admin: created user '$DEV_USERNAME' but could not look it back up." >&2
		exit 1
	fi
else
	echo "waypoint-keycloak-dev-admin: user '$DEV_USERNAME' already exists (id=$USER_ID)."
fi

# --- reconcile profile completeness, every run ---------------------------
PROFILE_BODY="$(jq -n --arg e "$DEV_EMAIL" --arg fn "$DEV_FIRST_NAME" --arg ln "$DEV_LAST_NAME" \
	'{enabled: true, email: $e, emailVerified: true,
	  firstName: $fn, lastName: $ln, requiredActions: []}')"
PROFILE_RESP="$(admin_api PUT "/realms/$REALM/users/$USER_ID" "$PROFILE_BODY")"
PROFILE_STATUS="$(printf '%s' "$PROFILE_RESP" | response_status)"
if [ "$PROFILE_STATUS" != "204" ]; then
	echo "waypoint-keycloak-dev-admin: profile reconciliation failed (HTTP $PROFILE_STATUS)" >&2
	explain_user_write_failure "$PROFILE_STATUS" "$(printf '%s' "$PROFILE_RESP" | response_body)"
	exit 1
fi

# --- reconcile the password, every run (non-temporary) ------------------
# why: docs/rationale/deploy.md#kcdevadmin-secret-passing-design
CRED_BODY="$(jq -n --rawfile pw "$DEV_PASSWORD_FILE" \
	'{type: "password", value: ($pw | sub("[\r\n]+$"; "")), temporary: false}')"
CRED_RESP="$(admin_api PUT "/realms/$REALM/users/$USER_ID/reset-password" "$CRED_BODY")"
CRED_STATUS="$(printf '%s' "$CRED_RESP" | response_status)"
if [ "$CRED_STATUS" != "204" ]; then
	echo "waypoint-keycloak-dev-admin: password reconciliation failed (HTTP $CRED_STATUS)" >&2
	exit 1
fi
echo "waypoint-keycloak-dev-admin: password reconciled (non-temporary)."

# --- ensure Admin-group membership, every run ----------------------------
GROUP_RESP="$(admin_api GET "/realms/$REALM/groups?search=$(urlencode "$GROUP_NAME")&exact=true")"
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
