#!/bin/sh
# Postgres healthcheck for the Waypoint compose stack (issue #844, round-2
# review of PR #860).
#
# `pg_isready` alone is not enough: it answers "the server accepts
# connections", which a HALF-INITIALIZED cluster does too. If initdb ran but
# docker-entrypoint-initdb.d aborted part way (the poisoned-volume trap the
# entrypoint wrapper now prevents going forward, and which an operator may
# already have on disk from before that fix), pg_isready reports healthy
# while none of the roles or databases the rest of the stack depends on
# exist -- backend's migration 0025 then fails on a missing role and Keycloak
# cannot log in at all, both AFTER `depends_on: service_healthy` said go.
#
# So health also asserts that both initdb scripts actually completed: the two
# runner login roles from 01-runner-roles.sh, and the `keycloak` role plus
# the `keycloak` database from 02-keycloak-db.sh. Four catalog lookups over
# the local unix socket -- cheap enough for a 10s interval.
#
# Note for an operator with a pgdata volume predating issue #442/#28: those
# init scripts never re-run against an existing volume, so this healthcheck
# will (correctly, and visibly) report unhealthy until the roles/database are
# created by hand. See deploy/README.md "Database roles".
set -eu

pg_isready -U "${POSTGRES_USER:-waypoint}" -d "${POSTGRES_DB:-waypoint}" >/dev/null

missing="$(psql -U "${POSTGRES_USER:-waypoint}" -d "${POSTGRES_DB:-waypoint}" \
	--tuples-only --no-align --quiet -v ON_ERROR_STOP=1 <<-'EOSQL'
	SELECT string_agg(expected, ', ' ORDER BY expected)
	FROM (
		SELECT 'role waypoint_compliance_runner' AS expected
		WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner')
		UNION ALL
		SELECT 'role waypoint_download_runner'
		WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner')
		UNION ALL
		SELECT 'role keycloak'
		WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'keycloak')
		UNION ALL
		SELECT 'database keycloak'
		WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'keycloak')
	) AS m;
	EOSQL
)"

if [ -n "$missing" ]; then
	echo "waypoint: postgres is accepting connections but initdb never completed -- missing: ${missing}" >&2
	echo "waypoint: reporting UNHEALTHY rather than letting dependents start against a half-initialized cluster (issue #844)." >&2
	exit 1
fi
