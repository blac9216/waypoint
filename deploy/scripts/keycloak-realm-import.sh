#!/usr/bin/env bash
#
# Issue #28 -- substitutes a real client secret into a throwaway copy of a
# realm export and (re)imports it against a running compose stack's Keycloak
# database, proving the export/import round-trip (issue #28 acceptance
# criteria: "realm export/import round-trips").
#
# LOCAL PROCEDURE ONLY (see deploy/scripts/keycloak-realm-export.sh's header
# for why -- ADR-0015 excludes Keycloak from any packaged bundle).
#
# This never edits deploy/keycloak/realm/waypoint-realm.json in place -- the
# committed file keeps its ${WAYPOINT_BACKEND_CLIENT_SECRET} placeholder
# forever (issue #844 renamed this from the older __WAYPOINT_BACKEND_CLIENT_SECRET__
# double-underscore form so the SAME string also resolves via Keycloak's own
# `keycloak.migration.replace-placeholders` substitution on the compose-managed
# boot path -- see deploy/keycloak/docker-entrypoint-wrapper.sh). Substitution
# happens into a gitignored scratch copy here, by this script's own sed, not
# Keycloak's substitution engine (the throwaway container below does not set
# JAVA_OPTS_APPEND).
#
# Mechanism, and why it is NOT `kc.sh import` (verified empirically, issue
# #28): Keycloak 25's standalone `import --override true` CLI logs
# "KC-SERVICES0032: Import finished successfully" and exits 0 against an
# already-initialized database, but the realm silently does not land -- a
# fresh `SELECT` against the `realm`/`client` tables afterward shows no
# change at all, reproduced both against a stopped and a running `keycloak`
# service. Keycloak's own SERVER-BOOT import path (`start-dev
# --import-realm`, `IGNORE_EXISTING` strategy) does not have this problem --
# verified to reliably create the realm and its client, with the substituted
# secret, when the realm does not already exist. This script therefore: (1)
# deletes the existing `waypoint` realm via the admin REST API if present,
# then (2) runs a throwaway `keycloak` container against the SAME database
# with `--import-realm` pointed at the substituted scratch file, which
# recreates the realm the same way `docker compose up` does on a fresh
# stack. This is slower than a bare `kc.sh import` would be, but it is the
# path actually proven to persist.
#
# Usage:
#   deploy/scripts/keycloak-realm-import.sh <project-name> [realm-file]
#
# <project-name> is the `docker compose -p` project (docs/testing.md). If
# [realm-file] is omitted, imports the committed
# deploy/keycloak/realm/waypoint-realm.json (still with a REAL secret
# substituted into the scratch copy -- never the placeholder, so the
# resulting client is actually usable). Requires KEYCLOAK_BACKEND_CLIENT_SECRET
# to be set (deploy/.env, gitignored, or exported in the shell) -- refuses to
# run with an empty/placeholder secret so a round-trip test can never
# silently import the templated placeholder as a real credential.
set -euo pipefail

PROJECT="${1:?usage: keycloak-realm-import.sh <project-name> [realm-file]}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REALM_FILE="${2:-$DEPLOY_DIR/keycloak/realm/waypoint-realm.json}"

: "${KEYCLOAK_BACKEND_CLIENT_SECRET:?Set KEYCLOAK_BACKEND_CLIENT_SECRET (never the placeholder) before running this script.}"

# shellcheck disable=SC2016 # deliberately literal -- comparing against the placeholder string, not expanding it
if [ "$KEYCLOAK_BACKEND_CLIENT_SECRET" = '${WAYPOINT_BACKEND_CLIENT_SECRET}' ]; then
	echo "KEYCLOAK_BACKEND_CLIENT_SECRET must not be the literal template placeholder." >&2
	exit 1
fi

KEYCLOAK_ADMIN="${KEYCLOAK_ADMIN:-admin}"
KEYCLOAK_ADMIN_PASSWORD="${KEYCLOAK_ADMIN_PASSWORD:-waypoint_keycloak_admin_dev_only}"
POSTGRES_KEYCLOAK_PASSWORD="${POSTGRES_KEYCLOAK_PASSWORD:-waypoint_keycloak_dev_only}"

# Scratch dir lives under deploy/, not /tmp (docs/testing.md "Devcontainer
# bind mounts": a bind-mount SOURCE path is resolved by the Docker daemon
# against the HOST filesystem, and container /tmp is never host-shared at
# all -- a /tmp-rooted mktemp here would silently bind-mount an empty
# directory into the throwaway `docker run` below when this script is
# invoked from inside the devcontainer. deploy/ is a real, host-visible
# tree either way (it's the working directory `docker compose` itself
# already runs from). Gitignored via deploy/.keycloak-import-scratch/
# below, never committed.
SCRATCH_DIR="$DEPLOY_DIR/.keycloak-import-scratch"
rm -rf "$SCRATCH_DIR"
mkdir -p "$SCRATCH_DIR"
# A single cleanup trap is installed further down, once it's known whether
# the compose-managed keycloak service needed stopping (it also needs to be
# restarted) -- see the second `trap` call below, which supersedes this
# bare removal the moment the script reaches that point. If the script
# exits before then (e.g. the usage/secret checks above), this simpler one
# still fires.
trap 'rm -rf "$SCRATCH_DIR"' EXIT

SCRATCH_FILE="$SCRATCH_DIR/waypoint-realm.json"
# `|` delimiter (not `/`) so a secret value containing a slash doesn't break
# the substitution (issue #844 rename from the old __..__ placeholder made
# this worth tightening at the same time).
sed "s|\${WAYPOINT_BACKEND_CLIENT_SECRET}|${KEYCLOAK_BACKEND_CLIENT_SECRET}|" \
	"$REALM_FILE" > "$SCRATCH_FILE"

# Translate SCRATCH_DIR to its HOST-side path before using it as a raw
# `docker run -v` source below (docs/testing.md "Devcontainer bind mounts").
# `docker compose` resolves its own bind-mount sources against the compose
# file's directory in a way that works from inside this devcontainer (the
# committed docker-compose.yml's own `./keycloak/realm` mount is proof), but
# a plain `docker run -v <path>:...` given directly to the daemon is NOT
# translated -- the daemon looks for that literal path on the HOST, and a
# container-side /workspaces/... path silently mounts an empty directory
# there (verified empirically while authoring this script: the import
# below reported success while the source directory was provably empty
# inside the container). Only actually translate when running inside a
# devcontainer whose own workspace mount can be inspected; on a real
# appliance host (no devcontainer, no mounted-socket indirection) the
# daemon and this script already share one filesystem, so SCRATCH_DIR is
# already correct and this loop finds no mapping to apply.
HOST_SCRATCH_DIR="$SCRATCH_DIR"
if command -v docker >/dev/null && [ -n "${HOSTNAME:-}" ]; then
	while IFS=$'\t' read -r host_src container_dst; do
		[ -z "$container_dst" ] && continue
		case "$SCRATCH_DIR" in
		"$container_dst"/*)
			HOST_SCRATCH_DIR="${host_src}${SCRATCH_DIR#"$container_dst"}"
			break
			;;
		esac
	done < <(docker inspect "$(hostname)" --format '{{range .Mounts}}{{.Source}}	{{.Destination}}
{{end}}' 2>/dev/null)
fi

DC=(docker compose -p "$PROJECT")
cd "$DEPLOY_DIR"

CONTAINER_ID="$("${DC[@]}" ps -q keycloak)"
if [ -z "$CONTAINER_ID" ]; then
	echo "No running 'keycloak' service found for project '$PROJECT' -- bring the stack up first." >&2
	exit 1
fi

NETWORK="${PROJECT}_internal"

echo "Deleting any existing 'waypoint' realm so the import below recreates it..."
# Issue #534 (live-verified): every Keycloak-served path now lives under
# /auth (KC_HTTP_RELATIVE_PATH=/auth, docker-compose.yml's keycloak service --
# needed so the browser-facing SPA login flow, which goes through nginx's
# /auth/ proxy path, gets self-referential URLs Keycloak itself renders
# correctly). This script talks to Keycloak directly, container-to-container,
# bypassing nginx entirely -- but the /auth prefix is a property of Keycloak's
# OWN URL space now, not just nginx's proxy path, so it applies here too.
ADMIN_TOKEN="$(docker run --rm --network "$NETWORK" curlimages/curl:latest -s -X POST \
	http://keycloak:8080/auth/realms/master/protocol/openid-connect/token \
	-d "grant_type=password&client_id=admin-cli&username=${KEYCLOAK_ADMIN}&password=${KEYCLOAK_ADMIN_PASSWORD}" \
	| sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p')"

if [ -z "$ADMIN_TOKEN" ]; then
	echo "Could not authenticate to Keycloak's admin API -- check KEYCLOAK_ADMIN/KEYCLOAK_ADMIN_PASSWORD." >&2
	exit 1
fi

docker run --rm --network "$NETWORK" curlimages/curl:latest -s -o /dev/null -X DELETE \
	http://keycloak:8080/auth/admin/realms/waypoint \
	-H "Authorization: Bearer ${ADMIN_TOKEN}"

# Stop the compose-managed service before the throwaway boot below (verified
# necessary while authoring this script: running the throwaway import
# alongside a live `keycloak` service against the SAME database silently
# no-ops -- the delete above appears to succeed, but the throwaway boot's
# IGNORE_EXISTING import then also silently does nothing, with no error at
# any layer. Two Keycloak processes clustering against one Infinispan/DB
# backend is not a supported concurrent-write scenario to begin with).
# Restarted at the end either way, success or failure, via the trap below.
echo "Stopping $PROJECT's keycloak service for the re-import..."
"${DC[@]}" stop keycloak >/dev/null
# shellcheck disable=SC2064 # PROJECT/DC intentionally expand now, not at trap time
trap "${DC[*]} start keycloak >/dev/null; rm -rf '$SCRATCH_DIR'" EXIT

echo "Importing realm from $SCRATCH_FILE via a throwaway Keycloak boot against the same database..."

# A throwaway server boot, not `kc.sh import` (see header comment for why).
# --import-realm's IGNORE_EXISTING strategy creates the realm fresh since
# the delete above just removed it.
CONTAINER_NAME="${PROJECT}-keycloak-import-verify"
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
docker run -d --name "$CONTAINER_NAME" \
	--network "$NETWORK" \
	-e KC_DB=postgres \
	-e KC_DB_URL="jdbc:postgresql://postgres:5432/keycloak" \
	-e KC_DB_USERNAME=keycloak \
	-e KC_DB_PASSWORD="$POSTGRES_KEYCLOAK_PASSWORD" \
	-e KEYCLOAK_ADMIN="$KEYCLOAK_ADMIN" \
	-e KEYCLOAK_ADMIN_PASSWORD="$KEYCLOAK_ADMIN_PASSWORD" \
	-e KC_HTTP_ENABLED=true \
	-v "$HOST_SCRATCH_DIR":/opt/keycloak/data/import:ro \
	quay.io/keycloak/keycloak:25.0 \
	start-dev --import-realm --http-enabled=true --hostname-strict=false >/dev/null

IMPORTED=0
for _ in $(seq 1 60); do
	if docker logs "$CONTAINER_NAME" 2>&1 | grep -q "Realm 'waypoint' imported"; then
		IMPORTED=1
		break
	fi
	if ! docker inspect -f '{{.State.Running}}' "$CONTAINER_NAME" >/dev/null 2>&1; then
		break
	fi
	sleep 1
done

docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true

if [ "$IMPORTED" -ne 1 ]; then
	echo "Import did not report success within the timeout -- check the container log above." >&2
	exit 1
fi

echo "Import complete. The keycloak service is restarting (trap on exit) to pick up the change."
