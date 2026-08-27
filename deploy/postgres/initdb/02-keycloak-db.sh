#!/bin/sh
# Creates Keycloak's own login role and database the first time this Postgres
# data directory is initialized (issue #28, ADR-0004). Runs via the
# postgres:16-alpine image's docker-entrypoint-initdb.d convention (same
# mechanism 01-runner-roles.sh uses) -- fires only against a FRESH data
# directory, never against an existing ../pgdata volume.
#
# Deliberately NOT the 01-runner-roles.sh grants pattern (validation notes on
# issue #28, 2026-08-19): the two runner roles are grants-only logins against
# the shared `waypoint` database that `backend`'s own migration (0025) owns
# and grants into. Keycloak is a different shape entirely -- it manages its
# own schema via Liquibase migrations it runs itself on every boot, so it
# needs to OWN a database outright, not receive table-level grants into one
# Waypoint owns. There is no corresponding "0025-style" backend migration for
# Keycloak and there should never be one: Keycloak's schema is Keycloak's to
# manage.
#
# Password source (issue #844): file-backed, not an inline env var --
# POSTGRES_KEYCLOAK_PASSWORD_FILE, set on the postgres service in
# docker-compose.yml, points at the SAME Compose `secrets:`-mounted file
# (deploy/config/secrets/postgres-keycloak-password, gitignored) the
# `keycloak` service itself reads via KC_DB_PASSWORD_FILE -- one file, one
# password, both sides of the same login. A missing file is rejected by the
# Docker daemon at container-create time and an empty/unreadable one by
# deploy/postgres/docker-entrypoint-wrapper.sh before initdb runs at all --
# see 01-runner-roles.sh's header for the precise enforcement points and why
# the `[ -s ... ]` check below is only a last-ditch layer. Never echoed,
# never logged --
# psql -v substitution keeps the value out of this script's own output and
# out of shell history inside the container (same technique
# 01-runner-roles.sh uses; see that file's header comment for why a
# DO $$ ... $$ block doesn't work for this).
set -eu

: "${POSTGRES_KEYCLOAK_PASSWORD_FILE:?POSTGRES_KEYCLOAK_PASSWORD_FILE must be set}"

# $1 = human label for the error message only -- never prints file content.
# $2 = path to the mounted secret file.
read_secret_file() {
	if [ ! -s "$2" ]; then
		echo "waypoint: missing or empty secret file for $1: $2" >&2
		exit 1
	fi
	cat "$2"
}

POSTGRES_KEYCLOAK_PASSWORD="$(read_secret_file 'keycloak DB password' "$POSTGRES_KEYCLOAK_PASSWORD_FILE")"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v keycloak_pw="$POSTGRES_KEYCLOAK_PASSWORD" <<-'EOSQL'
	SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'keycloak') AS keycloak_role_exists \gset
	\if :keycloak_role_exists
		ALTER ROLE keycloak PASSWORD :'keycloak_pw';
	\else
		CREATE ROLE keycloak LOGIN PASSWORD :'keycloak_pw';
	\endif

	-- Keycloak may not create further databases or roles, but it DOES own
	-- (and must be able to freely migrate) its own database -- unlike the
	-- two runner roles in 01-runner-roles.sh, which own nothing.
	ALTER ROLE keycloak NOSUPERUSER NOCREATEDB NOCREATEROLE;
EOSQL

# CREATE DATABASE cannot run inside the multi-statement heredoc above (it
# cannot execute inside a transaction block, and psql wraps -f/heredoc input
# in one implicitly in some contexts) -- kept as a separate, idempotent
# invocation. "SELECT ... WHERE NOT EXISTS" can't drive CREATE DATABASE
# either (still DDL, still no subquery-driven conditional), so the existence
# check happens client-side in shell instead.
KEYCLOAK_DB_EXISTS=$(psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --tuples-only --no-align -c "SELECT 1 FROM pg_database WHERE datname = 'keycloak'")

if [ "$KEYCLOAK_DB_EXISTS" != "1" ]; then
	psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
		-c "CREATE DATABASE keycloak OWNER keycloak;"
fi
