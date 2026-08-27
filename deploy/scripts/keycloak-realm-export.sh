#!/usr/bin/env bash
#
# Exports the live `waypoint` realm from a running `keycloak` service, for
# backup or to refresh the committed deploy/keycloak/realm/waypoint-realm.json.
# Local procedure only -- not part of any packaged bundle.
#
# Usage:
#   deploy/scripts/keycloak-realm-export.sh <project-name> <out-file>
#
# <project-name> is the `docker compose -p` project your stack was brought
# up with (docs/testing.md), e.g. `wp-issue28`. <out-file> is where the
# exported realm JSON lands on the host.
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

# why: docs/rationale/deploy.md#realmexport-host-path-translation
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

# why: docs/rationale/deploy.md#realmexport-throwaway-container
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
