#!/bin/sh
# Loads Keycloak's database password, bootstrap admin password, and the
# waypoint-backend realm client secret from mounted files before handing off
# to the image's own entrypoint (issue #844).
#
# Why a wrapper and not Keycloak's own vault: live-verified (issue #844
# validation, against quay.io/keycloak/keycloak:25.0) that a --vault=file
# --vault-dir setup with KC_DB_PASSWORD='${vault.db-password}' still fails
# datasource startup with "The server requested SCRAM-based authentication,
# but no password was provided" -- kc.sh's vault substitution does not apply
# to db-password (or, by the same server-bootstrap-ordering reasoning, to the
# bootstrap admin password) in this version. `--db-password`/
# `KEYCLOAK_ADMIN_PASSWORD` have no built-in `_FILE` indirection either
# (unlike the postgres:16-alpine image's POSTGRES_PASSWORD_FILE) -- confirmed
# against `kc.sh start --help-all`. This wrapper is therefore the only
# available fail-closed mechanism for those two values.
#
# The realm client secret is different: waypoint-realm.json's `secret` field
# is `${WAYPOINT_BACKEND_CLIENT_SECRET}`, using Keycloak's REAL
# `keycloak.migration.replace-placeholders` substitution (already proven
# working for rootUrl/redirectUris/webOrigins via WAYPOINT_PUBLIC_URL, issue
# #842, JAVA_OPTS_APPEND on the keycloak service) -- this wrapper's only job
# for that one is to export WAYPOINT_BACKEND_CLIENT_SECRET into the
# environment Keycloak's own substitution engine reads at import time, from
# the same kind of mounted file as the other two.
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
WAYPOINT_BACKEND_CLIENT_SECRET="$(read_secret_file 'waypoint-backend realm client secret (WAYPOINT_BACKEND_CLIENT_SECRET_FILE)' "$WAYPOINT_BACKEND_CLIENT_SECRET_FILE")"
export KC_DB_PASSWORD KEYCLOAK_ADMIN_PASSWORD WAYPOINT_BACKEND_CLIENT_SECRET

exec /opt/keycloak/bin/kc.sh "$@"
