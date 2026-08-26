#!/bin/sh
# Validates EVERY mounted Postgres secret file before the stock
# postgres:16-alpine entrypoint is allowed to run (issue #844, round-2 review
# of PR #860).
#
# Why this exists rather than relying on the initdb scripts' own `[ -s ... ]`
# checks: those scripts run from docker_process_init_files, which the stock
# entrypoint invokes AFTER `initdb` has already created and populated
# /var/lib/postgresql/data. An empty (or unreadable) secret file therefore
# used to fail at a point where the damage was already done -- live-verified
# on a fresh volume: 01-runner-roles.sh printed its refusal and exited 1, the
# entrypoint aborted, `restart: unless-stopped` restarted the container, the
# second boot found a non-empty data directory and logged "Skipping
# initialization", and postgres then reported HEALTHY with no runner roles,
# no `keycloak` role and no `keycloak` database. The pgdata volume was
# permanently poisoned (initdb never re-runs) and only `down -v` recovered
# it. That is the opposite of fail-closed.
#
# Validating here -- before `exec`ing the stock entrypoint -- means a bad
# secret file aborts the container BEFORE initdb touches the data directory.
# The container then restart-loops on the same clean error, never reports
# healthy, and the volume stays pristine: fix the file, and the very next
# restart initializes the SAME volume correctly. No down -v needed.
#
# Same shape (and same never-print-the-value discipline) as
# deploy/keycloak/docker-entrypoint-wrapper.sh. Note POSTGRES_PASSWORD_FILE
# is the base image's OWN convention and the image would read it itself; it
# is validated here too so all four files fail closed identically and at the
# same, earliest, moment.
set -eu

: "${POSTGRES_PASSWORD_FILE:?POSTGRES_PASSWORD_FILE must be set}"
: "${POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE:?POSTGRES_COMPLIANCE_RUNNER_PASSWORD_FILE must be set}"
: "${POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE:?POSTGRES_DOWNLOAD_RUNNER_PASSWORD_FILE must be set}"
: "${POSTGRES_KEYCLOAK_PASSWORD_FILE:?POSTGRES_KEYCLOAK_PASSWORD_FILE must be set}"

# Compose `secrets:` with a `file:` source is a plain bind mount of the HOST
# file, so the container sees the HOST's ownership and mode verbatim -- there
# is no 0444 re-materialization the way Swarm secrets do it. The stock
# entrypoint drops to the `postgres` user before running
# docker-entrypoint-initdb.d, so a host file that only root (or only the
# operator's own uid) can read is readable HERE, in this root-run wrapper,
# and NOT readable by the scripts that actually consume it. Live-observed
# exactly that: `cat: can't open '/run/secrets/postgres-compliance-runner-password':
# Permission denied` from 01-runner-roles.sh -- after initdb had already
# created the data directory, i.e. the poisoned-volume trap all over again.
# So readability is checked as the user the server will actually run as.
RUNTIME_USER=""
if [ "$(id -u)" = "0" ] && command -v su-exec >/dev/null 2>&1; then
	RUNTIME_USER="${PGUSER_RUNTIME:-postgres}"
fi

# $1 = human label for the error message only -- never prints file content.
# $2 = path to the mounted secret file.
require_secret_file() {
	if [ ! -r "$2" ] || [ ! -s "$2" ]; then
		echo "waypoint: missing, empty, or unreadable secret file for $1: $2" >&2
		echo "waypoint: refusing to start Postgres without it (issue #844 fail-closed)." >&2
		echo "waypoint: the data directory has NOT been touched -- fix the file and this" >&2
		echo "waypoint: container will initialize the same volume correctly on its next restart." >&2
		exit 1
	fi
	# shellcheck disable=SC2016 # $1 is the inner sh's positional arg (the path), deliberately unexpanded here
	if [ -n "$RUNTIME_USER" ] && ! su-exec "$RUNTIME_USER" sh -c 'test -r "$1"' _ "$2"; then
		echo "waypoint: secret file for $1 is not readable by the '$RUNTIME_USER' user: $2" >&2
		echo "waypoint: the initdb scripts run as that user, so this would fail AFTER the data" >&2
		echo "waypoint: directory was created. Refusing to start (issue #844 fail-closed)." >&2
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
