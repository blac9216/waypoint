#!/bin/sh
# Creates the two dedicated runner login roles (issue #442, ADR-0014 SS7) the first
# time this Postgres data directory is initialized. Runs via the postgres:16-alpine
# image's docker-entrypoint-initdb.d convention (docker-entrypoint.sh:
# docker_process_init_files /docker-entrypoint-initdb.d/*), which only fires against
# a FRESH data directory -- an existing ../pgdata volume never re-runs this, exactly
# like POSTGRES_USER/POSTGRES_PASSWORD/POSTGRES_DB themselves.
#
# Table-level GRANTs for these roles live in a backend schema migration
# (backend/Waypoint.Infrastructure/Data/Migrations/0025_runner_db_roles.sql),
# applied by Waypoint.Api (the sole migrator, see NpgsqlSchemaMigrator) using the
# owning POSTGRES_USER connection -- NOT here. That keeps privilege boundaries
# versioned alongside schema changes (a later migration adding a table also grants
# it there), while ROLE CREATION happens here because it is the one step that
# needs each role's actual password, and passwords must never appear in a
# committed .sql migration file (CLAUDE.md sanitization policy). Splitting role
# creation from grants this way also means an operator who already has a live
# pgdata volume from before this issue landed does not need to recreate it: the
# migration alone still grants a role created by hand or by a future one-time
# admin step, and a truly fresh stack gets both automatically.
#
# Password source (issue #844): file-backed, not an inline env var --
# POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE / POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE
# on the postgres service in docker-compose.yml, each pointing at a Compose
# `secrets:`-mounted file under deploy/config/secrets/ (gitignored -- see
# .gitignore's /deploy/config/ entry). A MISSING file is caught by the Docker
# daemon at container-create time (`docker compose config` renders fine and
# exits 0 -- the enforcement point is create, not parse); an EMPTY or
# unreadable one is caught by deploy/postgres/docker-entrypoint-wrapper.sh
# BEFORE the stock entrypoint runs initdb, so this script never even gets the
# chance to run against a bad file. The `[ -s ... ]` check below is a
# last-ditch defence-in-depth layer only -- by the time it could fire, the
# data directory would already exist, which is precisely the poisoned-volume
# failure the wrapper exists to prevent (see its header comment). Never
# echoed, never logged -- psql -v substitution keeps the value out of this
# script's own output and out of shell history inside the container, same as
# before.
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

# NOTE: psql's `:'var'` client-side substitution does not expand inside a
# dollar-quoted PL/pgSQL body (`DO $$ ... $$`) -- verified empirically: psql
# treats the whole `$$...$$` span as an opaque token, the same way it treats a
# single-quoted string, so a `DO` block was tried first and failed with
# "syntax error at or near ':'" against a real Postgres container. `\gset` +
# `\if`/`\else`/`\endif` (both plain psql meta-commands, not PL/pgSQL) keep
# every substitution at the top level where it actually expands.
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

	-- Neither role may create objects, databases, or other roles -- least privilege
	-- (ADR-0014 SS7). Table-level GRANTs are applied later by the 0025 migration,
	-- which runs as $POSTGRES_USER (this database's owner) and therefore has
	-- authority to grant on objects it owns without either runner role being an
	-- owner itself.
	ALTER ROLE waypoint_compliance_runner NOSUPERUSER NOCREATEDB NOCREATEROLE;
	ALTER ROLE waypoint_download_runner NOSUPERUSER NOCREATEDB NOCREATEROLE;
EOSQL
