#!/bin/sh
# Loads Keycloak's database password, bootstrap admin password, and the
# waypoint-backend realm client secret from mounted files before handing off
# to the image's own entrypoint.
#
# why: docs/rationale/deploy.md#keycloak-wrapper-file-loading
set -eu

: "${KC_DB_PASSWORD_FILE:?KC_DB_PASSWORD_FILE must be set}"
: "${KEYCLOAK_ADMIN_PASSWORD_FILE:?KEYCLOAK_ADMIN_PASSWORD_FILE must be set}"
: "${WAYPOINT_BACKEND_CLIENT_SECRET_FILE:?WAYPOINT_BACKEND_CLIENT_SECRET_FILE must be set}"

# $1 = human label for the error message only -- never prints file content.
# $2 = path to the mounted secret file.
read_secret_file() {
	if [ ! -s "$2" ]; then
		echo "waypoint: missing or empty secret file for $1: $2" >&2
		echo "waypoint: refusing to start Keycloak without it (issue #844 fail-closed)." >&2
		exit 1
	fi
	cat "$2"
}

KC_DB_PASSWORD="$(read_secret_file 'Keycloak database password (KC_DB_PASSWORD_FILE)' "$KC_DB_PASSWORD_FILE")"
KEYCLOAK_ADMIN_PASSWORD="$(read_secret_file 'Keycloak bootstrap admin password (KEYCLOAK_ADMIN_PASSWORD_FILE)' "$KEYCLOAK_ADMIN_PASSWORD_FILE")"
# why: docs/rationale/deploy.md#keycloak-realm-placeholder-substitution
WAYPOINT_BACKEND_CLIENT_SECRET="$(read_secret_file 'waypoint-backend realm client secret (WAYPOINT_BACKEND_CLIENT_SECRET_FILE)' "$WAYPOINT_BACKEND_CLIENT_SECRET_FILE")"
export KC_DB_PASSWORD KEYCLOAK_ADMIN_PASSWORD WAYPOINT_BACKEND_CLIENT_SECRET

exec /opt/keycloak/bin/kc.sh "$@"
