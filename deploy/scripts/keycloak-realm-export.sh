#!/usr/bin/env bash
#
# Issue #28 -- exports the live `waypoint` realm from a running `keycloak`
# service in this compose stack, for backup or to refresh the committed
# deploy/keycloak/realm/waypoint-realm.json after an admin-console change.
#
# LOCAL PROCEDURE ONLY (issue #28 validation notes, 2026-08-19): this script
# is not part of any packaged bundle. ADR-0015 excludes Keycloak from the
# transfer/update bundle format -- that lands with the M6 bundle format (see
# the comment on issue #43). Nothing this script writes is consumed by any
# other part of this repository automatically.
#
# Usage:
#   deploy/scripts/keycloak-realm-export.sh <project-name> <out-file>
#
# <project-name> is the `docker compose -p` project your stack was brought up
# with (docs/testing.md's isolation recipe) -- e.g. `wp-issue28`. <out-file>
# is where the exported realm JSON lands on the host.
set -euo pipefail

PROJECT="${1:?usage: keycloak-realm-export.sh <project-name> <out-file>}"
OUT_FILE="${2:?usage: keycloak-realm-export.sh <project-name> <out-file>}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

DC=(docker compose -p "$PROJECT")
cd "$DEPLOY_DIR"

CONTAINER_ID="$("${DC[@]}" ps -q keycloak)"
if [ -z "$CONTAINER_ID" ]; then
	echo "No running 'keycloak' service found for project '$PROJECT' -- bring the stack up first." >&2
	exit 1
fi

echo "Exporting realm 'waypoint' from $PROJECT's keycloak container..."

OUT_DIR="$(dirname "$(readlink -f "$OUT_FILE" 2>/dev/null || echo "$OUT_FILE")")"
mkdir -p "$OUT_DIR"

# Translate OUT_DIR to its HOST-side path before using it as a raw
# `docker run -v` source below (docs/testing.md "Devcontainer bind mounts":
# a bind-mount SOURCE is resolved by the Docker daemon against the HOST
# filesystem, and `docker run -v` -- unlike `docker compose`'s own bind
# mounts -- gets no translation at all when invoked from inside a
# devcontainer whose workspace is itself a bind mount. Verified empirically
# while authoring this issue: an un-translated container-side path here
# silently exports into an empty directory the daemon creates on the host,
# and the export command still reports success). On a real appliance host
# (no devcontainer, no mounted-socket indirection) this loop finds no
# mapping and OUT_DIR is used as-is.
HOST_OUT_DIR="$OUT_DIR"
if command -v docker >/dev/null && [ -n "${HOSTNAME:-}" ]; then
	while IFS=$'\t' read -r host_src container_dst; do
		[ -z "$container_dst" ] && continue
		case "$OUT_DIR" in
		"$container_dst"/*)
			HOST_OUT_DIR="${host_src}${OUT_DIR#"$container_dst"}"
			break
			;;
		esac
	done < <(docker inspect "$(hostname)" --format '{{range .Mounts}}{{.Source}}	{{.Destination}}
{{end}}' 2>/dev/null)
fi

# kc.sh export requires the server to be stopped (it needs exclusive DB
# access via its own embedded connection, not the running server's) --
# run it as a one-shot `docker exec` against a SEPARATE short-lived
# invocation is not possible while start-dev owns the process, so this
# runs `export` in a throwaway container against the SAME database instead,
# which Keycloak's docs support (export/import both connect to KC_DB_URL
# directly, independent of whether a server is already running against it).
docker run --rm \
	--network "${PROJECT}_internal" \
	-e KC_DB=postgres \
	-e KC_DB_URL="jdbc:postgresql://postgres:5432/keycloak" \
	-e KC_DB_USERNAME=keycloak \
	-e KC_DB_PASSWORD="${POSTGRES_KEYCLOAK_PASSWORD:-waypoint_keycloak_dev_only}" \
	-v "$HOST_OUT_DIR":/export-out \
	quay.io/keycloak/keycloak:25.0 \
	export --dir /export-out --realm waypoint --users skip

EXPORTED_FILE="$(dirname "$OUT_FILE")/waypoint-realm.json"
if [ -f "$EXPORTED_FILE" ] && [ "$EXPORTED_FILE" != "$OUT_FILE" ]; then
	mv "$EXPORTED_FILE" "$OUT_FILE"
fi

echo "Exported to $OUT_FILE"
echo
echo "REMINDER before committing any change based on this export:"
echo "  - Replace the live client 'secret' value with the literal placeholder"
echo "    \${WAYPOINT_BACKEND_CLIENT_SECRET} (see deploy/keycloak/README.md)."
echo "  - Diff against deploy/keycloak/realm/waypoint-realm.json and confirm no"
echo "    real hostname, IP, or credential was introduced (CLAUDE.md)."
