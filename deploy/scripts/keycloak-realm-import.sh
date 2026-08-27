#!/usr/bin/env bash
#
# Substitutes a real client secret into a throwaway copy of a realm export
# and (re)imports it against a running compose stack's Keycloak database,
# proving the export/import round-trip. Local procedure only.
#
# Never edits deploy/keycloak/realm/waypoint-realm.json in place -- the
# committed file keeps its ${WAYPOINT_BACKEND_CLIENT_SECRET} placeholder
# forever; substitution happens into a gitignored scratch copy here.
#
# Mechanism, and why it is not `kc.sh import`:
# why: docs/rationale/deploy.md#realmimport-server-boot-not-cli
#
# Usage:
#   deploy/scripts/keycloak-realm-import.sh <project-name> [realm-file]
#
# <project-name> is the `docker compose -p` project (docs/testing.md). If
# [realm-file] is omitted, imports the committed waypoint-realm.json (with a
# real secret substituted into the scratch copy). Requires
# KEYCLOAK_BACKEND_CLIENT_SECRET to be set -- refuses to run with an
# empty/placeholder secret.
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

# why: docs/rationale/deploy.md#realmimport-host-path-translation
SCRATCH_DIR="$DEPLOY_DIR/.keycloak-import-scratch"
rm -rf "$SCRATCH_DIR"
mkdir -p "$SCRATCH_DIR"
# Superseded by the second `trap` call below once keycloak needs stopping;
# fires as-is for an earlier exit (e.g. the usage/secret checks above).
trap 'rm -rf "$SCRATCH_DIR"' EXIT

SCRATCH_FILE="$SCRATCH_DIR/waypoint-realm.json"
# why: docs/rationale/deploy.md#realmimport-python-not-sed
command -v python3 >/dev/null 2>&1 || {
	echo "error: python3 is required for the realm placeholder substitution." >&2
	exit 1
}
# shellcheck disable=SC2016 # the python3 -c body below is python source, not shell -- must stay unexpanded
KEYCLOAK_BACKEND_CLIENT_SECRET="$KEYCLOAK_BACKEND_CLIENT_SECRET" \
	WAYPOINT_REALM_SRC="$REALM_FILE" WAYPOINT_REALM_DST="$SCRATCH_FILE" \
	python3 -c '
import json, os

src = os.environ["WAYPOINT_REALM_SRC"]
dst = os.environ["WAYPOINT_REALM_DST"]
# json.dumps(...)[1:-1] yields the JSON-escaped BODY of the string, without
# the surrounding quotes -- the placeholder is already inside quotes.
value = json.dumps(os.environ["KEYCLOAK_BACKEND_CLIENT_SECRET"])[1:-1]

with open(src, encoding="utf-8") as fh:
    content = fh.read()
placeholder = "${WAYPOINT_BACKEND_CLIENT_SECRET}"
if placeholder not in content:
    raise SystemExit("error: %s contains no %s placeholder" % (src, placeholder))
with open(dst, "w", encoding="utf-8") as fh:
    fh.write(content.replace(placeholder, value))
'

# why: docs/rationale/deploy.md#realmimport-host-path-translation
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
# Every Keycloak-served path lives under /auth (KC_HTTP_RELATIVE_PATH), a
# property of Keycloak's own URL space, so it applies to this direct
# container-to-container call too, not just nginx's proxy path.
ADMIN_TOKEN="$(docker run --rm --network "$NETWORK" curlimages/curl:latest -s -X POST \
	http://keycloak:8080/auth/realms/master/protocol/openid-connect/token \
	-d "grant_type=password&client_id=admin-cli&username=${KEYCLOAK_ADMIN}&password=${KEYCLOAK_ADMIN_PASSWORD}" \
	| sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p')"

if [ -z "$ADMIN_TOKEN" ]; then
	echo "Could not authenticate to Keycloak's admin API -- check KEYCLOAK_ADMIN/KEYCLOAK_ADMIN_PASSWORD." >&2
	exit 1
fi

# why: docs/rationale/deploy.md#realmimport-delete-then-reboot-strategy
docker run --rm --network "$NETWORK" curlimages/curl:latest -s -o /dev/null -X DELETE \
	http://keycloak:8080/auth/admin/realms/waypoint \
	-H "Authorization: Bearer ${ADMIN_TOKEN}"

# why: docs/rationale/deploy.md#realmimport-stop-before-reimport
echo "Stopping $PROJECT's keycloak service for the re-import..."
"${DC[@]}" stop keycloak >/dev/null
# shellcheck disable=SC2064 # PROJECT/DC intentionally expand now, not at trap time
trap "${DC[*]} start keycloak >/dev/null; rm -rf '$SCRATCH_DIR'" EXIT

echo "Importing realm from $SCRATCH_FILE via a throwaway Keycloak boot against the same database..."

# A throwaway server boot, not `kc.sh import` -- see header comment.
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
