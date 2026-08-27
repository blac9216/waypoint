#!/usr/bin/env bash
#
# Dev-stack generator: creates secrets, TLS, an isolated project identity,
# and a validated Compose override, without starting any container.
#
#   --mode persistent (default)
#     The one recurring human dev loop: deploy/config/secrets/,
#     deploy/compose.override.yaml, deploy/.env. Never overwrites an
#     existing secret or override; `down -v` never removes deploy/config/.
#
#   --mode agent --slug SLUG
#     Throwaway agent/CI bring-up, entirely under deploy/.generated/<slug>/.
#     Never touches deploy/config/ or compose.override.yaml. Idempotent:
#     re-running with the same slug reuses existing secrets/TLS.
#
# Both modes validate the merged config with `docker compose config` and
# print the lifecycle commands to run next.
#
# Usage:
#   deploy/scripts/generate-dev-stack.sh --mode persistent [options]
#   deploy/scripts/generate-dev-stack.sh --mode agent --slug SLUG [options]
#
# Common options:
#   --public-url URL            WAYPOINT_PUBLIC_URL. Persistent default:
#                                https://localhost:<port>. Agent: required.
#   --port PORT                 Host HTTPS port. Persistent: 8443. Agent: 19443.
#   --subnet CIDR                `edge` network subnet. Persistent:
#                                192.168.240.0/24. Agent: 203.0.113.0/24.
#   --username NAME              Dev admin username. Default: developer.
#
# Agent-mode-only options:
#   --local-auth-admin-hash-file PATH   Enables local auth with this
#                                        pre-computed password hash.
#   --runner-resource-fallback / --no-runner-resource-fallback
#                                        cgroup-unreadable sandbox workaround
#                                        on both runners. Default: on.
#   --keycloak-dev-admin                 Also provisions a Keycloak-realm
#                                        dev-admin user.
#
# Collision detection (port/subnet/project) runs before any file is written,
# so a collision leaves nothing behind.
#
# Requires: openssl, docker compose v2, python3.

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
KEYCLOAK_DEV_ADMIN=0

usage() {
	# Prints the header comment block (line 2 through the first non-# line).
	awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "${BASH_SOURCE[0]}"
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
	--keycloak-dev-admin)
		KEYCLOAK_DEV_ADMIN=1
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
	# why: docs/rationale/deploy.md#gen-persistent-project-name
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
# why: docs/rationale/deploy.md#gen-host-path-translation
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
# why: docs/rationale/deploy.md#gen-project-ownership-discriminator
OWN_DEPLOY_DIRS=("${DEPLOY_DIR}" "$(host_path "${DEPLOY_DIR}")")

# Fallback for a container with no working_dir label (a plain `docker run`
# of a Compose-built image inherits the project label from the image but
# not the working_dir): an artifact THIS generator produces and nothing
# else does.
if [[ "${MODE}" == "agent" ]]; then
	OWN_ARTIFACT="${STATE_DIR}/override.yaml"
else
	OWN_ARTIFACT="${STATE_DIR}/secrets/dev-admin-password"
fi

OWNED_STATE=0
OWN_CONTAINER_NAMES=""
FOREIGN_CONTAINERS=""
PROJECT_PS="$(docker ps --filter "label=com.docker.compose.project=${PROJECT}" \
	--format '{{.Names}}\t{{.Image}}\t{{.Label "com.docker.compose.project.working_dir"}}' 2>/dev/null || true)"
if [[ -n "${PROJECT_PS}" ]]; then
	OWNED_STATE=1
	while IFS=$'\t' read -r c_name c_image c_workdir; do
		[[ -n "${c_name}" ]] || continue
		c_owned=0
		if [[ -n "${c_workdir}" ]]; then
			for d in "${OWN_DEPLOY_DIRS[@]}"; do
				if [[ "${c_workdir}" == "${d}" ]]; then
					c_owned=1
					break
				fi
			done
		elif [[ -s "${OWN_ARTIFACT}" ]]; then
			c_owned=1
		fi
		if [[ "${c_owned}" -eq 1 ]]; then
			OWN_CONTAINER_NAMES+="${c_name}"$'\n'
		else
			OWNED_STATE=0
			FOREIGN_CONTAINERS+="  ${c_name} (image ${c_image}; started from ${c_workdir:-<no project-dir label>})"$'\n'
		fi
	done <<<"${PROJECT_PS}"
fi

if [[ "${OWNED_STATE}" -eq 0 && -n "${FOREIGN_CONTAINERS}" ]]; then
	echo "error: Compose project '${PROJECT}' is already claimed by running container(s) this checkout did not start:" >&2
	printf '%s' "${FOREIGN_CONTAINERS}" >&2
	echo "This checkout's deploy directory is ${DEPLOY_DIR} (host-side: ${OWN_DEPLOY_DIRS[1]})." >&2
	if [[ "${MODE}" == "agent" ]]; then
		echo "Pick a different --slug (the project name is 'wp-<slug>'), or stop that stack first." >&2
	else
		echo "Stop that stack first, or use --mode agent --slug SLUG for an isolated one." >&2
	fi
	exit 1
fi

# Host-port collision. Attribution is by container name and image, not the
# (image-inherited, unreliable) compose project label. Self-exemption is by
# the EXACT container names proven owned above.
PORT_OWNER="$(docker ps --format '{{.Names}}\t{{.Image}}\t{{.Label "com.docker.compose.project"}}\t{{.Ports}}' 2>/dev/null |
	awk -F'\t' -v p=":${PORT}->" -v ownlist="${OWN_CONTAINER_NAMES}" '
		BEGIN { n = split(ownlist, a, "\n"); for (i = 1; i <= n; i++) if (a[i] != "") own[a[i]] = 1 }
		$4 ~ p {
			if ($1 in own) next
			printf "  container %s (image %s; compose project label %s)\n", $1, $2, ($3 == "" ? "<none>" : $3)
		}' || true)"
if [[ -n "${PORT_OWNER}" ]]; then
	echo "error: host port ${PORT} is already published on this Docker host by:" >&2
	printf '%s\n' "${PORT_OWNER}" >&2
	echo "(the compose project label is image-inherited and may not name the owning stack)" >&2
	echo "Pick a different --port." >&2
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
# Our own project's edge network (an idempotent re-run) is not a collision,
# but only when ownership was already proven above. Matches the whole name
# field so an unrelated "wp-demo_edge2" is never swallowed.
if [[ "${OWNED_STATE}" -eq 1 ]]; then
	SUBNET_COLLISION="$(printf '%s\n' "${SUBNET_COLLISION}" |
		awk -F'\t' -v self="${PROJECT}_edge" '$1 != self' || true)"
fi
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

	# why: docs/rationale/deploy.md#gen-local-auth-hash-copy
	if [[ -n "${LOCAL_AUTH_HASH_FILE}" ]]; then
		[[ -s "${LOCAL_AUTH_HASH_FILE}" ]] || {
			echo "error: --local-auth-admin-hash-file '${LOCAL_AUTH_HASH_FILE}' is missing or empty." >&2
			exit 1
		}
		LOCAL_AUTH_DIR="${STATE_DIR}/local-auth"
		mkdir -p "${LOCAL_AUTH_DIR}"
		if [[ "${LOCAL_AUTH_HASH_FILE}" -ef "${LOCAL_AUTH_DIR}/admin-password-hash" ]]; then
			echo "Local auth: admin password hash already in place -- ${LOCAL_AUTH_DIR}/admin-password-hash"
		else
			cp "${LOCAL_AUTH_HASH_FILE}" "${LOCAL_AUTH_DIR}/admin-password-hash"
			chmod 644 "${LOCAL_AUTH_DIR}/admin-password-hash"
			echo "Local auth: admin password hash copied into ${LOCAL_AUTH_DIR}/admin-password-hash (value never printed)"
		fi
		LOCAL_AUTH_HASH_FILE="${LOCAL_AUTH_DIR}/admin-password-hash"
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
	# why: docs/rationale/deploy.md#gen-secret-reuse-never-overwrite
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

	# why: docs/rationale/deploy.md#gen-env-file-merge
	ENV_FILE="${DEPLOY_DIR}/.env"
	touch "${ENV_FILE}"
	set_env_key() {
		local key="$1" value="$2"
		if grep -qE "^${key}=" "${ENV_FILE}"; then
			# Portable in-place edit (temp file, not sed -i -- GNU/BSD flag split).
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

	# why: docs/rationale/deploy.md#gen-env-file-list-merge
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
		if [[ "${KEYCLOAK_DEV_ADMIN}" -eq 1 ]]; then
			# Same service/contract as compose.override.example.yaml's own keycloak-dev-admin.
			echo "  keycloak-dev-admin:"
			echo "    build:"
			# why: docs/rationale/deploy.md#gen-build-context-not-host-path
			echo "      context: ./keycloak-dev-admin"
			echo "    depends_on:"
			echo "      keycloak:"
			echo "        condition: service_healthy"
			echo "    environment:"
			echo "      WAYPOINT_DEV_ADMIN_USERNAME: \"${USERNAME}\""
			echo "    volumes:"
			echo "      - type: bind"
			echo "        source: $(host_path "${SECRETS_DIR}/dev-admin-password")"
			echo "        target: /run/secrets/dev-admin-password"
			echo "        read_only: true"
			echo "    secrets:"
			echo "      - keycloak-bootstrap-admin-password"
			echo "    networks:"
			echo "      - internal"
			echo "    restart: \"no\""
		fi
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

# why: docs/rationale/deploy.md#gen-devcontainer-build-up-split
if [[ "${HOST_PREFIX}" != "${REPO_ROOT}" ]]; then
	UP_BLOCK="  Build: ${DC_PRINT} build
  Up:    ${DC_PRINT} --project-directory $(host_path "${DEPLOY_DIR}") up -d --no-build"
else
	UP_BLOCK="  Up:   ${DC_PRINT} up -d --build"
fi

cat <<EOF

Generated stack ready (nothing started). Lifecycle commands, run from deploy/:

${UP_BLOCK}
  Test: https://localhost:${PORT}/api/v1/health  (or, for agent mode inside a
        devcontainer, curl through a helper container on the '${PROJECT}_edge'
        network -- see docs/testing.md)
  Down: ${DC_PRINT} down -v
EOF
