#!/bin/sh
# Postgres healthcheck for the Waypoint compose stack.
#
# `pg_isready` alone answers "the server accepts connections", which a
# half-initialized cluster does too. This also asserts that both initdb
# scripts completed: the two runner login roles, and the `keycloak` role
# plus database.
#
# why: docs/rationale/deploy.md#postgres-role-asserting-healthcheck
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
	echo "waypoint: reporting UNHEALTHY rather than letting dependents start against a half-initialized cluster." >&2
	exit 1
fi
