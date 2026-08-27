#!/bin/sh
# Creates Keycloak's own login role and database the first time this
# Postgres data directory is initialized. Runs via the same
# docker-entrypoint-initdb.d convention as 01-runner-roles.sh -- fires only
# against a FRESH data directory.
#
# Deliberately NOT the 01-runner-roles.sh grants pattern: Keycloak manages
# its own schema via Liquibase migrations it runs itself, so it needs to OWN
# a database outright, not receive table-level grants into one Waypoint owns.
#
# why: docs/rationale/deploy.md#postgres-secret-file-password-source
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

# CREATE DATABASE cannot run inside the multi-statement heredoc above (no DDL
# inside a transaction block) -- kept as a separate, idempotent invocation.
KEYCLOAK_DB_EXISTS=$(psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --tuples-only --no-align -c "SELECT 1 FROM pg_database WHERE datname = 'keycloak'")

if [ "$KEYCLOAK_DB_EXISTS" != "1" ]; then
	psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
		-c "CREATE DATABASE keycloak OWNER keycloak;"
fi
