#!/bin/sh
# Validates every mounted Postgres secret file before the stock
# postgres:16-alpine entrypoint is allowed to run.
#
# why: docs/rationale/deploy.md#postgres-poisoned-volume-fail-closed
# why: docs/rationale/deploy.md#postgres-wrapper-validates-before-initdb
set -eu

: "${POSTGRES_PASSWORD_FILE:?POSTGRES_PASSWORD_FILE must be set}"
: "${POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE:?POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE must be set}"
: "${POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE:?POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE must be set}"
: "${POSTGRES_KEYCLOAK_PASSWORD_FILE:?POSTGRES_KEYCLOAK_PASSWORD_FILE must be set}"

# why: docs/rationale/deploy.md#postgres-runtime-user-readability-check
RUNTIME_USER=""
if [ "$(id -u)" = "0" ] && command -v su-exec >/dev/null 2>&1; then
	RUNTIME_USER="${PGUSER_RUNTIME:-postgres}"
fi

# $1 = human label for the error message only -- never prints file content.
# $2 = path to the mounted secret file.
require_secret_file() {
	if [ ! -r "$2" ] || [ ! -s "$2" ]; then
		echo "waypoint: missing, empty, or unreadable secret file for $1: $2" >&2
		echo "waypoint: refusing to start Postgres without it (fail-closed)." >&2
		echo "waypoint: the data directory has NOT been touched -- fix the file and this" >&2
		echo "waypoint: container will initialize the same volume correctly on its next restart." >&2
		exit 1
	fi
	# shellcheck disable=SC2016 # $1 is the inner sh's positional arg (the path), deliberately unexpanded here
	if [ -n "$RUNTIME_USER" ] && ! su-exec "$RUNTIME_USER" sh -c 'test -r "$1"' _ "$2"; then
		echo "waypoint: secret file for $1 is not readable by the '$RUNTIME_USER' user: $2" >&2
		echo "waypoint: the initdb scripts run as that user, so this would fail AFTER the data" >&2
		echo "waypoint: directory was created. Refusing to start (fail-closed)." >&2
		echo "waypoint: fix the HOST file's mode/ownership -- Compose bind-mounts it verbatim," >&2
		echo "waypoint: it does not re-materialize it as 0444. 0644 is the convention this repo" >&2
		echo "waypoint: uses for mounted secret material (see deploy/README.md)." >&2
		exit 1
	fi
}

require_secret_file 'Postgres owner password (POSTGRES_PASSWORD_FILE)' "$POSTGRES_PASSWORD_FILE"
require_secret_file 'compliance-runner DB password (POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE)' "$POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE"
require_secret_file 'download-runner DB password (POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE)' "$POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE"
require_secret_file 'keycloak DB password (POSTGRES_KEYCLOAK_PASSWORD_FILE)' "$POSTGRES_KEYCLOAK_PASSWORD_FILE"

exec docker-entrypoint.sh "$@"
