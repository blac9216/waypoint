#!/usr/bin/env bash
#
# Issue #847 (epic #841): dev-stack generator. Creates everything a local or
# agent development bring-up needs -- secrets, TLS, an isolated project
# identity, and a validated Compose override -- WITHOUT starting any
# container. Two modes:
#
#   --mode persistent (default)
#     For the one recurring human dev loop. Ensures deploy/config/secrets/
#     (the six base secrets init-config.sh also manages, plus
#     dev-admin-password -- issue #846's keycloak-dev-admin service reads
#     exactly that path/name), deploy/compose.override.yaml (copied from the
#     committed compose.override.example.yaml if not already present -- never
#     overwritten once it exists, since an operator may have hand-edited it),
#     and deploy/.env (only the keys this script owns -- see "deploy/.env
#     merge" below). `down -v` never removes deploy/config/, so the same
#     dev-admin login persists across ordinary reset cycles.
#
#   --mode agent --slug SLUG
#     For a throwaway agent/CI bring-up. Writes EVERYTHING -- secrets,
#     SAN-correct self-signed TLS for the public URL host, and a
#     self-contained override.yaml -- under deploy/.generated/<slug>/ only.
#     Never touches deploy/config/ or deploy/compose.override.yaml. Re-running
#     with the same slug is idempotent: existing secrets/TLS are reused, the
#     override is regenerated (it carries no secret material itself, only
#     paths to files this script also never overwrites).
#
# Neither mode ever starts a container. Both validate the merged
# configuration with `docker compose config` and print the exact lifecycle
# commands to run next.
#
# Usage:
#   deploy/scripts/generate-dev-stack.sh --mode persistent [options]
#   deploy/scripts/generate-dev-stack.sh --mode agent --slug SLUG [options]
#
# Common options:
#   --public-url URL          WAYPOINT_PUBLIC_URL. Persistent default:
#                              https://localhost:<port>. Agent mode: REQUIRED
#                              (its hostname drives the generated cert's SAN).
#   --port PORT                Host HTTPS port. Persistent default: 8443.
#                              Agent default: 19443.
#   --subnet CIDR               `edge` network subnet. Persistent default:
#                              192.168.240.0/24 (compose.yaml's own default).
#                              Agent default: 203.0.113.0/24.
#   --username NAME            Dev admin username. Default: developer.
#
# Agent-mode-only options:
#   --local-auth-admin-hash-file PATH
#     Absolute host or in-repo path to an already-computed local-auth admin
#     password hash (see backend's `--hash-password`). When given, the
#     generated override turns on LocalAuth__Enabled and mounts this file
#     read-only, host-path-translated the same as every other bind source
#     this script emits. Omit to leave local auth off (Keycloak-only, the
#     production posture) in the generated stack.
#   --runner-resource-fallback / --no-runner-resource-fallback
#     Whether to set RunnerResources__Fallback{CpuCores,MemoryBytes} on both
#     runners (the documented cgroup-unreadable sandbox workaround --
#     see fresh-stack-smoke-test.sh's own comment on the same override).
#     Default: on (agent mode targets exactly that kind of sandbox).
#
# Requires: openssl, docker compose v2, python3 (subnet-overlap/collision
# arithmetic only -- stdlib `ipaddress`, no network access).

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
DEPLOY_DIR="$(cd -- "${SCRIPT_DIR}/.." &>/dev/null && pwd)"
REPO_ROOT="$(cd -- "${DEPLOY_DIR}/.." &>/dev/null && pwd)"

MODE="persistent"
SLUG=""
PUBLIC_URL=""
PORT=""
SUBNET=""
USERNAME="developer"
LOCAL_AUTH_HASH_FILE=""
RUNNER_FALLBACK=1

usage() {
	sed -n '2,49p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
	case "$1" in
	--mode)
		MODE="$2"
		shift 2
		;;
	--slug)
		SLUG="$2"
		shift 2
		;;
	--public-url)
		PUBLIC_URL="$2"
		shift 2
		;;
	--port)
		PORT="$2"
		shift 2
		;;
	--subnet)
		SUBNET="$2"
		shift 2
		;;
	--username)
		USERNAME="$2"
		shift 2
		;;
	--local-auth-admin-hash-file)
		LOCAL_AUTH_HASH_FILE="$2"
		shift 2
		;;
	--runner-resource-fallback)
		RUNNER_FALLBACK=1
		shift
		;;
	--no-runner-resource-fallback)
		RUNNER_FALLBACK=0
		shift
		;;
	-h | --help)
		usage
		exit 0
		;;
	*)
		echo "error: unknown argument: $1" >&2
		usage >&2
		exit 2
		;;
	esac
done

if [[ "${MODE}" != "persistent" && "${MODE}" != "agent" ]]; then
	echo "error: --mode must be 'persistent' or 'agent', got '${MODE}'." >&2
	exit 2
fi

if [[ "${MODE}" == "agent" ]]; then
	[[ -n "${SLUG}" ]] || {
		echo "error: --mode agent requires --slug SLUG." >&2
		exit 2
	}
	[[ "${SLUG}" =~ ^[a-z0-9][a-z0-9-]*$ ]] || {
		echo "error: --slug must be lowercase alphanumeric/hyphen, got '${SLUG}'." >&2
		exit 2
	}
	[[ -n "${PUBLIC_URL}" ]] || {
		echo "error: --mode agent requires --public-url (its hostname drives the generated TLS cert's SAN)." >&2
		exit 2
	}
	PORT="${PORT:-19443}"
	SUBNET="${SUBNET:-203.0.113.0/24}"
	PROJECT="wp-${SLUG}"
	STATE_DIR="${DEPLOY_DIR}/.generated/${SLUG}"
else
	PORT="${PORT:-8443}"
	SUBNET="${SUBNET:-192.168.240.0/24}"
	# Issue #885: matches compose.yaml's own `name: waypoint` -- the base no
	# longer stamps a "-dev" identity onto a real deployment, and this
	# script's persistent mode is the ONE recurring human dev loop against
	# that same base, so it uses the identical project name rather than a
	# second, divergent default.
	PROJECT="waypoint"
	STATE_DIR="${DEPLOY_DIR}/config"
	PUBLIC_URL="${PUBLIC_URL:-https://localhost:${PORT}}"
fi

for cmd in openssl python3; do
	command -v "${cmd}" >/dev/null 2>&1 || {
		echo "error: ${cmd} is required but was not found on PATH." >&2
		exit 1
	}
done
if ! docker compose version >/dev/null 2>&1; then
	echo "error: 'docker compose' (v2) is required but was not found." >&2
	exit 1
fi

# --- Devcontainer bind-mount host-path translation ------------------------
#
# docs/testing.md "Devcontainer bind mounts": every bind-mount SOURCE this
# script writes into a compose override is resolved by the Docker daemon
# against the HOST filesystem, not this process's own. Same technique
# fresh-stack-smoke-test.sh/e2e-playwright.sh already use, generalized here
# so both scripts can delegate to this one instead of hand-rolling it twice
# more (issue #847's own risk note).
HOST_PREFIX="${REPO_ROOT}"
if [[ -S /var/run/docker.sock ]]; then
	SELF_MOUNTS="$(docker inspect "$(hostname)" --format '{{range .Mounts}}{{.Source}}|{{.Destination}}{{"\n"}}{{end}}' 2>/dev/null || true)"
	while IFS='|' read -r src dst; do
		if [[ "${dst}" == "${REPO_ROOT}" || "${REPO_ROOT}" == "${dst}"/* ]]; then
			HOST_PREFIX="${src}${REPO_ROOT#"${dst}"}"
			break
		fi
	done <<<"${SELF_MOUNTS}"
fi
host_path() { printf '%s' "${HOST_PREFIX}${1#"${REPO_ROOT}"}"; }

# --- Collision detection (BEFORE any file is written) ----------------------
#
# Checked against every OTHER Compose project currently running on this
# Docker host -- re-running this script for the SAME project/slug (the
# idempotent case) must not trip its own prior state.
OTHER_PROJECTS="$(docker ps -a --format '{{.Label "com.docker.compose.project"}}' 2>/dev/null | sort -u | grep -vFx "${PROJECT}" || true)"

if printf '%s\n' "${OTHER_PROJECTS}" | grep -qFx "${PROJECT}"; then
	echo "error: project '${PROJECT}' collision -- this should be unreachable (self-filtered above)." >&2
	exit 1
fi
# Container-name collision: Compose derives names as <project>-<service>-<n>.
EXISTING_CONTAINERS="$(docker ps -a --format '{{.Names}}' 2>/dev/null | grep -E "^${PROJECT}-" || true)"
if [[ -n "${EXISTING_CONTAINERS}" ]]; then
	# Belongs to this same project (idempotent re-run) -- not a collision.
	:
fi

PORT_OWNER="$(docker ps --format '{{.Label "com.docker.compose.project"}}\t{{.Ports}}' 2>/dev/null | awk -F'\t' -v p=":${PORT}->" '$2 ~ p {print $1}' | sort -u || true)"
PORT_OWNER="$(printf '%s\n' "${PORT_OWNER}" | grep -vFx "${PROJECT}" || true)"
if [[ -n "${PORT_OWNER}" ]]; then
	echo "error: host port ${PORT} is already published by project '${PORT_OWNER}' -- pick a different --port." >&2
	exit 1
fi

SUBNET_COLLISION="$(python3 - "${SUBNET}" <<'PYEOF'
import ipaddress, subprocess, sys, json

wanted = ipaddress.ip_network(sys.argv[1], strict=False)
out = subprocess.run(["docker", "network", "ls", "-q"], capture_output=True, text=True, check=False).stdout.split()
for net_id in out:
    insp = subprocess.run(["docker", "network", "inspect", net_id], capture_output=True, text=True, check=False)
    if insp.returncode != 0:
        continue
    try:
        data = json.loads(insp.stdout)[0]
    except (ValueError, IndexError):
        continue
    name = data.get("Name", "")
    for cfg in data.get("IPAM", {}).get("Config", []) or []:
        subnet = cfg.get("Subnet")
        if not subnet:
            continue
        try:
            other = ipaddress.ip_network(subnet, strict=False)
        except ValueError:
            continue
        if other.overlaps(wanted):
            print(f"{name}\t{subnet}")
PYEOF
)"
# Our own project's edge network (an idempotent re-run) is not a collision.
SUBNET_COLLISION="$(printf '%s\n' "${SUBNET_COLLISION}" | grep -v "^${PROJECT}_edge" || true)"
if [[ -n "${SUBNET_COLLISION}" ]]; then
	echo "error: --subnet ${SUBNET} overlaps an existing Docker network:" >&2
	printf '%s\n' "${SUBNET_COLLISION}" >&2
	echo "Pick a different --subnet." >&2
	exit 1
fi

echo "Collision check passed: project=${PROJECT} port=${PORT} subnet=${SUBNET}"

# --- Secrets ----------------------------------------------------------------

SECRETS_DIR="${STATE_DIR}/secrets"
mkdir -p "${SECRETS_DIR}"

SECRET_NAMES=(
	postgres-owner-password
	postgres-compliance-runner-password
	postgres-download-runner-password
	postgres-keycloak-password
	keycloak-bootstrap-admin-password
	keycloak-backend-client-secret
	dev-admin-password
)
GENERATED=0
REUSED=0
for name in "${SECRET_NAMES[@]}"; do
	target="${SECRETS_DIR}/${name}"
	if [[ -s "${target}" ]]; then
		REUSED=$((REUSED + 1))
		continue
	fi
	umask 077
	tmp="$(mktemp "${SECRETS_DIR}/.${name}.XXXXXX")"
	openssl rand -hex 32 >"${tmp}"
	mv "${tmp}" "${target}"
	chmod 644 "${target}"
	GENERATED=$((GENERATED + 1))
done
echo "Secrets: ${GENERATED} generated, ${REUSED} reused (values never printed) -- ${SECRETS_DIR}"

if [[ "${MODE}" == "agent" ]]; then
	MASTER_KEY="${SECRETS_DIR}/waypoint-master-key"
	if [[ ! -s "${MASTER_KEY}" ]]; then
		umask 077
		tmp="$(mktemp "${SECRETS_DIR}/.waypoint-master-key.XXXXXX")"
		openssl rand -hex 32 >"${tmp}"
		mv "${tmp}" "${MASTER_KEY}"
		chmod 644 "${MASTER_KEY}"
		echo "Secrets: master key generated -- ${MASTER_KEY}"
	else
		echo "Secrets: master key reused -- ${MASTER_KEY}"
	fi
fi

# --- TLS (agent mode only -- persistent mode's dev-bootstrap service
#     already generates a self-signed pair into a named volume) -----------

if [[ "${MODE}" == "agent" ]]; then
	TLS_DIR="${STATE_DIR}/tls"
	mkdir -p "${TLS_DIR}"
	TLS_CERT="${TLS_DIR}/tls.crt"
	TLS_KEY="${TLS_DIR}/tls.key"
	TLS_HOST="$(python3 -c "import sys, urllib.parse as u; print(u.urlparse(sys.argv[1]).hostname or '')" "${PUBLIC_URL}")"
	[[ -n "${TLS_HOST}" ]] || {
		echo "error: could not parse a hostname out of --public-url '${PUBLIC_URL}'." >&2
		exit 1
	}
	# Issue #847 (review finding on the pre-generator staging this replaced,
	# https://github.com/blac9216/waypoint/issues/847): a PARTIAL pair -- one
	# file present/non-empty, the other missing or empty -- must never be
	# silently completed. Regenerating both from scratch would overwrite the
	# survivor, which is exactly the "existing secrets/TLS are reused and
	# never overwritten" acceptance criterion applied to a key pair instead
	# of a single file. Reuse requires BOTH files non-empty; anything else
	# that isn't "neither exists" fails closed instead of guessing.
	if [[ -s "${TLS_CERT}" && -s "${TLS_KEY}" ]]; then
		echo "TLS: reusing existing self-signed pair -- ${TLS_CERT}"
	elif [[ -s "${TLS_CERT}" || -s "${TLS_KEY}" ]]; then
		echo "error: partial TLS pair at ${TLS_DIR} -- only one of tls.crt/tls.key exists (or one is empty)." >&2
		echo "  Refusing to regenerate over the surviving file. Remove the stray file yourself, or restore its pair, then re-run." >&2
		exit 1
	else
		SAN="DNS:${TLS_HOST}"
		if [[ "${TLS_HOST}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
			SAN="IP:${TLS_HOST}"
		fi
		openssl req -x509 -nodes -newkey rsa:2048 \
			-keyout "${TLS_KEY}" \
			-out "${TLS_CERT}" \
			-days 365 \
			-subj "/C=US/ST=Dev/L=Dev/O=Waypoint Agent/CN=${TLS_HOST}" \
			-addext "subjectAltName=${SAN}" >/dev/null 2>&1
		chmod 600 "${TLS_KEY}"
		chmod 644 "${TLS_CERT}"
		echo "TLS: generated SAN-correct self-signed pair for '${TLS_HOST}' -- ${TLS_CERT}"
	fi
fi

# --- Compose override --------------------------------------------------

if [[ "${MODE}" == "persistent" ]]; then
	OVERRIDE_FILE="${DEPLOY_DIR}/compose.override.yaml"
	if [[ -f "${OVERRIDE_FILE}" ]]; then
		echo "Override: ${OVERRIDE_FILE} already exists -- left untouched (never overwritten)."
	else
		cp "${DEPLOY_DIR}/compose.override.example.yaml" "${OVERRIDE_FILE}"
		echo "Override: created ${OVERRIDE_FILE} from compose.override.example.yaml."
	fi

	# deploy/.env merge: only the keys THIS script owns are added/updated;
	# any other line (an operator's own override) is left exactly as-is.
	# Order/formatting of untouched lines is preserved -- only a managed key
	# is replaced in place or appended if missing.
	ENV_FILE="${DEPLOY_DIR}/.env"
	touch "${ENV_FILE}"
	set_env_key() {
		local key="$1" value="$2"
		if grep -qE "^${key}=" "${ENV_FILE}"; then
			# Portable in-place edit (works with both GNU and BSD sed via a
			# throwaway temp file, avoiding sed -i's flag-syntax split).
			grep -vE "^${key}=" "${ENV_FILE}" >"${ENV_FILE}.tmp"
			mv "${ENV_FILE}.tmp" "${ENV_FILE}"
		fi
		printf '%s=%s\n' "${key}" "${value}" >>"${ENV_FILE}"
	}
	set_env_key WAYPOINT_HTTPS_PORT "${PORT}"
	set_env_key WAYPOINT_EDGE_SUBNET "${SUBNET}"
	set_env_key WAYPOINT_PUBLIC_URL "${PUBLIC_URL}"
	set_env_key WAYPOINT_DEV_ADMIN_USERNAME "${USERNAME}"
	echo "Env: WAYPOINT_HTTPS_PORT/WAYPOINT_EDGE_SUBNET/WAYPOINT_PUBLIC_URL/WAYPOINT_DEV_ADMIN_USERNAME written to ${ENV_FILE}"

	COMPOSE_ARGS=(-p "${PROJECT}" -f "${DEPLOY_DIR}/compose.yaml" -f "${OVERRIDE_FILE}" --env-file "${ENV_FILE}")
else
	mkdir -p "${STATE_DIR}"
	OVERRIDE_FILE="${STATE_DIR}/override.yaml"

	# Port/subnet/public-url drive through a slug-scoped --env-file, NOT
	# override.yaml, and are read by compose.yaml's own operator-config
	# anchors (x-operator-config: public-url/https-port/edge-subnet) exactly
	# the way an operator's deploy/.env would. This matters, not just tidier:
	# Compose's own merge semantics APPEND list entries under `ports:` and
	# `networks:.edge.ipam.config` rather than replacing the base's by
	# target/subnet (live-verified while building this script -- an earlier
	# revision that put `ports:`/`networks:` overrides directly in
	# override.yaml produced a merged config with BOTH the base's default
	# port and this one published side by side). Using the same anchors the
	# base already exposes sidesteps the whole class of list-merge surprises.
	# --env-file also means agent mode NEVER auto-loads deploy/.env (Compose
	# only auto-loads the default `.env` when --env-file is not given) -- a
	# persistent-mode operator's own deploy/.env can never leak into an
	# agent-mode bring-up.
	ENV_FILE="${STATE_DIR}/.env"
	{
		echo "WAYPOINT_HTTPS_PORT=${PORT}"
		echo "WAYPOINT_EDGE_SUBNET=${SUBNET}"
		echo "WAYPOINT_PUBLIC_URL=${PUBLIC_URL}"
	} >"${ENV_FILE}"
	echo "Env: WAYPOINT_HTTPS_PORT/WAYPOINT_EDGE_SUBNET/WAYPOINT_PUBLIC_URL written to ${ENV_FILE}"

	MASTER_KEY_HOST="$(host_path "${SECRETS_DIR}/waypoint-master-key")"
	{
		echo "# Generated by deploy/scripts/generate-dev-stack.sh --mode agent --slug ${SLUG}."
		echo "# Regenerated on every run; carries no secret material of its own, only"
		echo "# paths to files this script never overwrites once created. Safe to delete"
		echo "# and re-run this script to recreate it. Port/subnet/public-url are NOT"
		echo "# here -- see the sibling .env file, loaded via --env-file so this stack"
		echo "# never picks up deploy/.env's own persistent-mode values."
		echo "name: ${PROJECT}"
		echo "services:"
		echo "  nginx:"
		echo "    volumes:"
		echo "      - type: bind"
		echo "        source: $(host_path "${STATE_DIR}/tls/tls.crt")"
		echo "        target: /etc/nginx/certs/tls.crt"
		echo "        read_only: true"
		echo "      - type: bind"
		echo "        source: $(host_path "${STATE_DIR}/tls/tls.key")"
		echo "        target: /etc/nginx/certs/tls.key"
		echo "        read_only: true"
		echo "  backend:"
		echo "    environment:"
		echo "      WAYPOINT_MASTER_KEY_FILE: /run/secrets/waypoint-master-key"
		if [[ -n "${LOCAL_AUTH_HASH_FILE}" ]]; then
			echo "      LocalAuth__Enabled: \"true\""
			echo "      LocalAuth__AdminPasswordHashFile: /run/secrets/local-auth-admin-password-hash"
		fi
		echo "    volumes:"
		echo "      - type: bind"
		echo "        source: ${MASTER_KEY_HOST}"
		echo "        target: /run/secrets/waypoint-master-key"
		echo "        read_only: true"
		if [[ -n "${LOCAL_AUTH_HASH_FILE}" ]]; then
			echo "      - type: bind"
			echo "        source: $(host_path "${LOCAL_AUTH_HASH_FILE}")"
			echo "        target: /run/secrets/local-auth-admin-password-hash"
			echo "        read_only: true"
		fi
		echo "  compliance-runner:"
		echo "    environment:"
		echo "      WAYPOINT_MASTER_KEY_FILE: /run/secrets/waypoint-master-key"
		if [[ "${RUNNER_FALLBACK}" -eq 1 ]]; then
			echo "      RunnerResources__FallbackCpuCores: \"4\""
			echo "      RunnerResources__FallbackMemoryBytes: \"4294967296\""
		fi
		echo "    volumes:"
		echo "      - type: bind"
		echo "        source: ${MASTER_KEY_HOST}"
		echo "        target: /run/secrets/waypoint-master-key"
		echo "        read_only: true"
		echo "  download-runner:"
		echo "    environment:"
		echo "      WAYPOINT_MASTER_KEY_FILE: /run/secrets/waypoint-master-key"
		echo "    volumes:"
		echo "      - type: bind"
		echo "        source: ${MASTER_KEY_HOST}"
		echo "        target: /run/secrets/waypoint-master-key"
		echo "        read_only: true"
		echo "secrets:"
		for name in postgres-owner-password postgres-compliance-runner-password \
			postgres-download-runner-password postgres-keycloak-password \
			keycloak-bootstrap-admin-password keycloak-backend-client-secret; do
			echo "  ${name}:"
			echo "    file: $(host_path "${SECRETS_DIR}/${name}")"
		done
	} >"${OVERRIDE_FILE}"
	echo "Override: generated ${OVERRIDE_FILE}"

	COMPOSE_ARGS=(-p "${PROJECT}" -f "${DEPLOY_DIR}/compose.yaml" -f "${OVERRIDE_FILE}" --env-file "${ENV_FILE}")
fi

# --- Validate the merged configuration --------------------------------

if ! docker compose "${COMPOSE_ARGS[@]}" config >/dev/null; then
	echo "error: 'docker compose config' failed against the generated override -- see output above." >&2
	exit 1
fi
echo "Validation: 'docker compose config' passed."

# --- Lifecycle commands (never executed by this script) ---------------

OVERRIDE_REL="${OVERRIDE_FILE#"${DEPLOY_DIR}/"}"
ENV_REL="${ENV_FILE#"${DEPLOY_DIR}/"}"
DC_PRINT="docker compose -p ${PROJECT} -f compose.yaml -f ${OVERRIDE_REL} --env-file ${ENV_REL}"

cat <<EOF

Generated stack ready (nothing started). Lifecycle commands, run from deploy/:

  Up:   ${DC_PRINT} up -d
  Test: https://localhost:${PORT}/api/v1/health  (or, for agent mode inside a
        devcontainer, curl through a helper container on the '${PROJECT}_edge'
        network -- see docs/testing.md)
  Down: ${DC_PRINT} down -v
EOF
