#!/bin/sh
# Creates the two dedicated runner login roles the first time this Postgres
# data directory is initialized. Runs via the postgres:16-alpine image's
# docker-entrypoint-initdb.d convention -- fires only against a FRESH data
# directory, never against an existing pgdata volume.
#
# Table-level GRANTs for these roles live in a backend schema migration, NOT
# here -- role creation happens here because it needs each role's actual
# password, which must never appear in a committed migration file.
#
# why: docs/rationale/deploy.md#postgres-secret-file-password-source
set -eu

: "${POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE:?POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE must be set}"
: "${POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE:?POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE must be set}"

# $1 = human label for the error message only -- never prints file content.
# $2 = path to the mounted secret file.
read_secret_file() {
	if [ ! -s "$2" ]; then
		echo "waypoint: missing or empty secret file for $1: $2" >&2
		exit 1
	fi
	cat "$2"
}

POSTGRES_COMPLIANCE_RUNNER_PASSWORD="$(read_secret_file 'compliance-runner DB password' "$POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE")"
POSTGRES_DOWNLOAD_RUNNER_PASSWORD="$(read_secret_file 'download-runner DB password' "$POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE")"

# `\gset` + `\if`/`\else`/`\endif` are used instead of a `DO $$ ... $$` block:
# psql's `:'var'` client-side substitution does not expand inside a
# dollar-quoted PL/pgSQL body.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v compliance_pw="$POSTGRES_COMPLIANCE_RUNNER_PASSWORD" \
  -v download_pw="$POSTGRES_DOWNLOAD_RUNNER_PASSWORD" <<-'EOSQL'
	SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') AS compliance_exists \gset
	\if :compliance_exists
		ALTER ROLE waypoint_compliance_runner PASSWORD :'compliance_pw';
	\else
		CREATE ROLE waypoint_compliance_runner LOGIN PASSWORD :'compliance_pw';
	\endif

	SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') AS download_exists \gset
	\if :download_exists
		ALTER ROLE waypoint_download_runner PASSWORD :'download_pw';
	\else
		CREATE ROLE waypoint_download_runner LOGIN PASSWORD :'download_pw';
	\endif

	-- Neither role may create objects, databases, or other roles -- least privilege.
	-- Table-level GRANTs are applied later by the 0025 migration,
	-- which runs as $POSTGRES_USER (this database's owner) and therefore has
	-- authority to grant on objects it owns without either runner role being an
	-- owner itself.
	ALTER ROLE waypoint_compliance_runner NOSUPERUSER NOCREATEDB NOCREATEROLE;
	ALTER ROLE waypoint_download_runner NOSUPERUSER NOCREATEDB NOCREATEROLE;
EOSQL
