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
# RENAMING THE DEV USER: the find-or-create step keys on the USERNAME, so
# changing WAYPOINT_DEV_ADMIN_USERNAME provisions a NEW user rather than
# renaming the existing one. The previously provisioned user stays behind,
# still enabled, still in the Admin group and still holding the reconciled
# dev password. That is by design for a dev-only provisioner -- this script
# never deletes accounts -- so cleaning up the old one is a manual operator
# step (Keycloak admin console, or `kcadm.sh delete users/<id> -r <realm>`),
# or reset the whole stack with `docker compose down -v`.
#
# curl+jq against the Admin REST API directly, not kcadm.sh: kcadm's own
# `config credentials` step takes --password only as a CLI flag (no file-based
# or stdin-based indirection in this image), which is exactly the argv/
# `docker top` exposure this script exists to avoid for the bootstrap admin
# and dev-admin passwords. Every curl call that carries a secret (the admin
# password grant, the dev password reset) goes through a curl `-K` config
# file instead of `-d`/`-H` on the command line, and the one place a secret
# has to reach an external binary (jq, building the reset-password JSON)
# uses `--rawfile` so only the FILE PATH is an argument. `printf` is a shell
# builtin in this image, so writing secrets into those config/body files is
# likewise argv-free. Net effect: neither password nor the short-lived
# bearer token ever appears in this container's argv (`ps`/`docker top`).
set -eu

umask 077

# Force the C locale for the whole script. urlencode() below walks a string
# one shell "character" at a time via `${_rest#?}` -- in this shell (busybox
# ash), `?` in a glob/parameter-expansion pattern is locale-aware and matches
# a whole multi-byte character under a UTF-8 locale, while `printf '%%%02X'
# "'$_ch"` is POSIX-defined to yield only the numeric value of the first BYTE
# of its argument. Under a UTF-8 locale those two disagree for any non-ASCII
# character: `${_rest#?}` consumes all of its bytes but printf only encodes
# the first one, silently dropping the rest (issue #890). Under LC_ALL=C,
# `?` matches exactly one byte, so the two agree and every byte round-trips.
# This has no other effect here -- curl/jq do their own encoding/decoding
# independent of the shell's locale.
LC_ALL=C
export LC_ALL

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
#
# The default is DERIVED from DEV_USERNAME rather than a fixed literal
# (issue #890, case 2): this script's whole design is "reconcile every field
# on every run so re-running never duplicates anything and always restores
# drift" (see file header). An operator-driven username rename is exactly
# that kind of drift, and a default email tied to the OLD username would
# make the reconcile-on-every-run guarantee false for the one field that
# happens to double as Keycloak's uniqueness key -- the create call would
# collide with the still-present old user on a stale email instead of
# provisioning the renamed one. Deriving the default keeps rename
# idempotent with zero operator action, matching every other field's
# behavior. An operator who sets WAYPOINT_DEV_ADMIN_EMAIL explicitly is
# unaffected -- that value always wins over this default.
#
# RENAME SEMANTICS (read this before changing WAYPOINT_DEV_ADMIN_USERNAME):
# find-or-create keys on the USERNAME, so changing it provisions a BRAND NEW
# user. The previously provisioned user is left exactly as it was -- still
# enabled, still a member of the Admin group, still holding the reconciled
# dev password -- and can still log in. This script never deletes users; for
# a dev-only one-shot provisioner that is deliberate (deleting accounts on a
# config change would be a surprising, unrecoverable side effect). Cleaning
# up the old account is a manual operator step: remove it via the Keycloak
# admin console or `kcadm.sh delete users/<id> -r <realm>`, or reset the
# whole stack with `docker compose down -v`, which drops the realm database
# and leaves only the currently configured user on the next `up`.
#
# The derived default must also be a VALID address for any username Keycloak
# accepts. A username that is itself email-shaped (`dev@waypoint.example
# .internal` -- a common convention, and reachable straight from
# `generate-dev-stack.sh --username`) would otherwise derive
# `dev@waypoint.example.internal@waypoint.example.internal`, which Keycloak
# rejects with a bare HTTP 400. An email-shaped username IS a valid value for
# the email field, so use it as-is in that case; only a bare local-part gets
# the placeholder domain appended.
case "$DEV_USERNAME" in
*@*) DEFAULT_DEV_EMAIL="$DEV_USERNAME" ;;
*) DEFAULT_DEV_EMAIL="${DEV_USERNAME}@waypoint.example.internal" ;;
esac
DEV_EMAIL="${WAYPOINT_DEV_ADMIN_EMAIL:-$DEFAULT_DEV_EMAIL}"

# Fail early, and by name, on a username that cannot produce a valid address
# (and that Keycloak would reject anyway) rather than letting it reach the
# API and come back as an opaque HTTP 400 several seconds later. Deliberately
# a narrow reject-list, not an allow-list: non-ASCII usernames are supported
# and live-verified (issue #890, case 1), so only characters that are
# structurally illegal in an addr-spec -- whitespace, the RFC 5322 specials,
# and a second `@` -- are refused.
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
#
# Fails closed on missing, zero-byte AND whitespace-only files. The
# whitespace-only case matters because every consumer below strips trailing
# newlines: a file holding just "\n" would otherwise sail past a bare `-s`
# test and only blow up much later as an opaque HTTP 400 from Keycloak's
# password endpoint. `tr` only ever sees a constant argument (the content
# arrives on stdin via redirect), so this check is argv-safe too.
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
# Validate it up front anyway so an unusable file fails before any API call.
require_secret_file 'Waypoint dev-admin password (WAYPOINT_DEV_ADMIN_PASSWORD_FILE)' "$DEV_PASSWORD_FILE"

# Percent-encodes $1 for safe use as a URL query-string value. Pure shell --
# `printf` is a builtin in this image, so the value never becomes an external
# command's argument. Used for the operator-settable username/group names,
# which would otherwise silently look up the wrong thing if they contained a
# space, `&`, or `#`.
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
# grant against the bootstrap admin -- the same credential the Keycloak
# service itself uses at boot, see deploy/keycloak/docker-entrypoint-wrapper.sh).
# Written to a curl config file so neither ADMIN_USERNAME's value nor
# ADMIN_PASSWORD is ever a literal argv token.
TOKEN_CFG="$CFG_DIR/token.cfg"

# curl's -K format does not support multi-line quoting the way a shell string
# does, so the password/username go through --data-urlencode read from a
# per-field temp file instead -- still never on the command line. The field
# files (like every other temp file here) live only inside the umask-077
# mktemp dir and are removed as soon as the curl call that reads them
# returns; the EXIT trap is the backstop, not the primary cleanup.
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
	api_out="$(curl -sS -K "$cfg" -w '\n%{http_code}' "$KC_BASE/admin$path" || true)"
	# Remove the bearer-token config (and any secret-bearing body) as soon as
	# curl is done with it; the EXIT trap is only the backstop.
	rm -f "$cfg"
	if [ -n "$body" ]; then
		rm -f "$body_file"
	fi
	printf '%s' "$api_out"
}

# Split a response body from admin_api's trailing "\n<status>" line.
response_body() { sed '$d'; }
response_status() { tail -n1; }

# Keycloak reports validation and conflict failures as a small JSON object --
# {"errorMessage": "..."} for user-profile/validation errors, {"error": "..."}
# for a few others. Surface that text so no bare "HTTP 400" is ever the only
# thing an operator gets. Sanitized on the way out: control characters
# stripped (a response body is untrusted input to this log) and truncated, so
# a large or hostile body cannot flood or corrupt the container log. $1 = the
# response body. Prints nothing if the body carries no recognizable message.
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
		# Reached only for values this script cannot pre-validate (a
		# WAYPOINT_DEV_ADMIN_EMAIL the operator set by hand, a realm with a
		# custom user profile adding required attributes, a first/last name
		# failing a profile validator). The username's own shape is already
		# checked before any API call, above.
		echo "waypoint-keycloak-dev-admin: HTTP 400 means Keycloak rejected the user representation as invalid. Most likely cause: the email address '$DEV_EMAIL' is not acceptable to this realm's user profile (set WAYPOINT_DEV_ADMIN_EMAIL to a valid address), or a realm user-profile attribute is required but unset." >&2
		;;
	409)
		# The default DEV_EMAIL now tracks DEV_USERNAME (see its derivation
		# above), so this should only fire when an operator has set
		# WAYPOINT_DEV_ADMIN_EMAIL explicitly to a value that collides with
		# an existing user -- name it rather than leaving them to guess.
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
	explain_user_write_failure "$PROFILE_STATUS" "$(printf '%s' "$PROFILE_RESP" | response_body)"
	exit 1
fi

# --- reconcile the password, every run (non-temporary) ------------------
# `--rawfile` (not `--arg`): jq is an external binary, so an `--arg pw <value>`
# would put the dev password straight into this process's argv where `ps` and
# `docker top` can read it. With `--rawfile` only the PATH is an argument and
# jq reads the value itself. `sub` strips the trailing newline a
# `printf '%s\n' ... > secret-file` style generator leaves behind.
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
