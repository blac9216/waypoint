#!/usr/bin/env bash
#
# Issue #468 -- Playwright live-stack coverage for the operator-visible
# M1/M2 parity items (docs/testing.md "Fresh-stack M1/M2 parity matrix"
# disclosed this as a gap: the API-level smoke test in
# fresh-stack-smoke-test.sh proves the same backend behavior the UI calls
# into, but no browser-driven check existed).
#
# Brings up an ISOLATED `docker compose` project (same bring-up recipe as
# fresh-stack-smoke-test.sh: unique -p, unique host port, dev admin
# password/master-key self-provisioning, devcontainer bind-mount host-path
# translation, compliance-profiles pre-seeding, cgroup-fallback override),
# seeds a site/target/credential via the API so the Playwright suite's
# Start-a-Scan wizard has something to select, points Playwright's
# E2E_BASE_URL/E2E_ADMIN_PASSWORD/E2E_SITE_NAME/E2E_CREDENTIAL_NAME at it,
# runs `npm run test:e2e` from frontend/, and tears the stack down fully
# (trap-based, same as the smoke script).
#
# Requires: docker compose v2, curl, openssl, python3, Node 22 (nvm) with
# `frontend/node_modules/.bin/playwright` installed
# (`cd frontend && npm ci && npx playwright install chromium` -- browser
# binaries are never committed, per this script's own README note below).
#
# Usage:
#   deploy/scripts/e2e-playwright.sh [slug] [port]

set -euo pipefail

# Load nvm and select Node 22 for the current shell. Defined (and NVM_DIR
# exported) once at the top level rather than inside each `(...)` subshell,
# so the assignment is not flagged as lost to a subshell (SC2030/SC2031).
export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"
use_node22() {
	if [[ -s "${NVM_DIR}/nvm.sh" ]]; then
		# shellcheck disable=SC1091
		. "${NVM_DIR}/nvm.sh"
	fi
	if command -v nvm >/dev/null 2>&1; then
		nvm use 22 >/dev/null 2>&1 || true
	fi
}

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
DEPLOY_DIR="$(cd -- "${SCRIPT_DIR}/.." &>/dev/null && pwd)"
REPO_ROOT="$(cd -- "${DEPLOY_DIR}/.." &>/dev/null && pwd)"
FRONTEND_DIR="${REPO_ROOT}/frontend"

SLUG="${1:-e2e-$(date +%s)-$$}"
PORT="${2:-19543}"
PROJECT="wp-${SLUG}"
BASE="https://127.0.0.1:${PORT}"
NET_BASE="https://nginx"
HELPER_NAME="${PROJECT}-e2e-helper"
HELPER_STARTED=""
# Set below if this script's own process needs to join the stack's `edge`
# network directly to reach it (devcontainer/remote-daemon environment where
# the published host port is unreachable from this namespace -- see the
# "Playwright base URL reachability" section below). Recorded here so
# cleanup can always disconnect it, even on early failure.
SELF_JOINED_EDGE_NETWORK=""

# Issue #844: secret files this run generated (see the "File-backed secrets"
# block below) -- tracked so cleanup removes exactly those and never an
# operator's own.
GENERATED_SECRET_FILES=()
SECRET_LABEL="e2e"

log() { printf '\n=== %s ===\n' "$*"; }

# shellcheck disable=SC2317,SC2329  # invoked indirectly via `trap cleanup EXIT`
cleanup() {
	log "Tearing down ${PROJECT} (docs/testing.md: always your own project, always -v)"
	if [[ -n "${HELPER_STARTED}" ]]; then
		docker rm -f "${HELPER_NAME}" >/dev/null 2>&1 || true
	fi
	if [[ -n "${SELF_JOINED_EDGE_NETWORK}" ]]; then
		docker network disconnect "${SELF_JOINED_EDGE_NETWORK}" "$(hostname)" >/dev/null 2>&1 || true
	fi
	(cd "${DEPLOY_DIR}" && ${DC:-docker compose -p "${PROJECT}"} down -v) || true
	rm -f "${SCRATCH_KEY_DIR:-/nonexistent}"/* 2>/dev/null || true
	if [[ ${#GENERATED_SECRET_FILES[@]} -gt 0 ]]; then
		rm -f "${GENERATED_SECRET_FILES[@]}" 2>/dev/null || true
	fi
}
trap cleanup EXIT

log "Isolation: project=${PROJECT} port=${PORT}"
echo "docker ps (containers NOT belonging to this run -- do not touch them):"
docker ps --format '{{.Names}}' | grep -v "^${PROJECT}-" || echo "  (none currently running)"

# --- Prerequisites -----------------------------------------------------
#
# Issue #500: these used to be soft (e.g. a `command -v dotnet` guard around
# the admin-hash step) -- missing a prerequisite silently skipped work
# further down and the script still exited 0, reporting a false "verified".
# Fail fast, before any stack comes up, so a missing tool is loud and cheap
# to diagnose instead of surfacing as a confusing downstream failure (or no
# failure at all).

missing=()
command -v docker >/dev/null 2>&1 || missing+=("docker")
command -v openssl >/dev/null 2>&1 || missing+=("openssl")
command -v python3 >/dev/null 2>&1 || missing+=("python3")
command -v dotnet >/dev/null 2>&1 || missing+=("dotnet (expected on PATH, e.g. \$HOME/.dotnet)")
if [[ ! -s "${NVM_DIR}/nvm.sh" ]]; then
	missing+=("nvm (expected at \${NVM_DIR}/nvm.sh, NVM_DIR=${NVM_DIR})")
else
	# shellcheck disable=SC1091
	. "${NVM_DIR}/nvm.sh"
	if ! nvm which 22 >/dev/null 2>&1; then
		missing+=("Node 22 (expected installed via nvm -- run: nvm install 22)")
	fi
fi

if [[ ${#missing[@]} -gt 0 ]]; then
	echo "error: missing prerequisite(s), refusing to bring up a stack for a run that cannot succeed:" >&2
	for m in "${missing[@]}"; do
		echo "  - ${m}" >&2
	done
	exit 1
fi

cd "${DEPLOY_DIR}"

# Issue #844 renamed this pair to the generic tls.crt/tls.key names
# deploy/nginx/conf.d/default.conf now references.
if [[ ! -f nginx/certs/tls.crt || ! -f nginx/certs/tls.key ]]; then
	log "Generating dev TLS cert"
	nginx/certs/generate-dev-certs.sh
fi

if [[ ! -f "${FRONTEND_DIR}/dist/index.html" ]]; then
	log "frontend/dist missing -- building it first (Node 22 required)"
	(
		use_node22
		cd "${FRONTEND_DIR}"
		npm ci
		npm run build
	)
fi

# Same devcontainer bind-mount host-path translation as
# fresh-stack-smoke-test.sh -- see that script's comment block for the full
# explanation. Duplicated rather than sourced: the two scripts are meant to
# stay independently readable end to end.
HOST_PREFIX="${REPO_ROOT}"
if [[ -S /var/run/docker.sock ]]; then
	SELF_MOUNTS="$(docker inspect "$(hostname)" --format '{{range .Mounts}}{{.Source}}|{{.Destination}}{{"\n"}}{{end}}' 2>/dev/null || true)"
	while IFS='|' read -r src dst; do
		if [[ "${dst}" == "${REPO_ROOT}" || "${REPO_ROOT}" == "${dst}"/* ]]; then
			HOST_PREFIX="${src}${REPO_ROOT#"${dst}"}"
			break
		fi
	done <<< "${SELF_MOUNTS}"
fi

WAYPOINT_E2E_OVERRIDE_FILE="${WAYPOINT_E2E_OVERRIDE_FILE:-}"
if [[ -z "${WAYPOINT_E2E_OVERRIDE_FILE}" ]] && [[ "${HOST_PREFIX}" != "${REPO_ROOT}" ]]; then
	log "devcontainer bind-mount translation: this container's ${REPO_ROOT} is the host's ${HOST_PREFIX} -- generating a scratch override"
	GENERATED_OVERRIDE="$(mktemp)"
	cat > "${GENERATED_OVERRIDE}" <<-EOF
	services:
	  nginx:
	    volumes:
	      - type: bind
	        source: ${HOST_PREFIX}/deploy/nginx/conf.d
	        target: /etc/nginx/conf.d
	        read_only: true
	      - type: bind
	        source: ${HOST_PREFIX}/deploy/nginx/certs
	        target: /etc/nginx/certs
	        read_only: true
	      - type: bind
	        source: ${HOST_PREFIX}/frontend/dist
	        target: /usr/share/nginx/html
	        read_only: true
	        bind:
	          create_host_path: false
	  postgres:
	    volumes:
	      - ${HOST_PREFIX}/deploy/postgres/initdb:/docker-entrypoint-initdb.d:ro
	secrets:
	  postgres-owner-password:
	    file: ${HOST_PREFIX}/deploy/config/secrets/postgres-owner-password
	  postgres-compliance-runner-password:
	    file: ${HOST_PREFIX}/deploy/config/secrets/postgres-compliance-runner-password
	  postgres-download-runner-password:
	    file: ${HOST_PREFIX}/deploy/config/secrets/postgres-download-runner-password
	  postgres-keycloak-password:
	    file: ${HOST_PREFIX}/deploy/config/secrets/postgres-keycloak-password
	  keycloak-bootstrap-admin-password:
	    file: ${HOST_PREFIX}/deploy/config/secrets/keycloak-bootstrap-admin-password
	  keycloak-backend-client-secret:
	    file: ${HOST_PREFIX}/deploy/config/secrets/keycloak-backend-client-secret
	EOF
	WAYPOINT_E2E_OVERRIDE_FILE="${GENERATED_OVERRIDE}"
fi

# --- File-backed secrets (issue #844) ----------------------------------
#
# The stack's Postgres bootstrap/runner/Keycloak passwords and the
# waypoint-backend realm client secret are Compose `secrets:` file sources
# under deploy/config/secrets/ (gitignored). They are MANDATORY: with them
# absent, `docker compose up` dies at container creation ("bind source path
# does not exist") -- `docker compose config` still exits 0, so nothing
# warns earlier. Generate obviously-invented, per-run values for any that
# are not already there; an operator's own real files (if any) are left
# untouched, and only files this run created are removed on teardown.
#
# #847 replaces this plumbing with a first-class generator; until then each
# bring-up script grows its own minimal copy.
SECRETS_DIR="${DEPLOY_DIR}/config/secrets"
mkdir -p "${SECRETS_DIR}"
for secret_name in postgres-owner-password postgres-compliance-runner-password \
	postgres-download-runner-password postgres-keycloak-password \
	keycloak-bootstrap-admin-password keycloak-backend-client-secret; do
	if [[ ! -s "${SECRETS_DIR}/${secret_name}" ]]; then
		printf 'invented-%s-%s-%s\n' "${SECRET_LABEL}" "${secret_name}" "$(openssl rand -hex 6)" \
			> "${SECRETS_DIR}/${secret_name}"
		# 0644, not 0600: Compose bind-mounts a `file:` secret source
		# verbatim (host uid/mode preserved -- no 0444 re-materialization),
		# and postgres's initdb scripts read it as the in-container
		# `postgres` user, which is neither root nor this script's uid. A
		# 0600 file here fails closed in postgres's entrypoint wrapper --
		# correct, but not what this script wants. Same 0644 convention the
		# master-key file above already uses.
		chmod 644 "${SECRETS_DIR}/${secret_name}"
		GENERATED_SECRET_FILES+=("${SECRETS_DIR}/${secret_name}")
	fi
done
log "File-backed secrets: ${#GENERATED_SECRET_FILES[@]} generated in ${SECRETS_DIR} (pre-existing files left alone)"

# docs/testing.md: the shipped `edge` network's pinned 192.168.240.0/24 can
# collide with a concurrent stack's identically-pinned network on a shared
# host (this compose file always requests that exact subnet, so two runs at
# once will collide unless one is overridden). WAYPOINT_E2E_SUBNET lets a
# caller pick an alternate /24 for this run; when set, the backend's
# ForwardedHeaders__KnownNetworks__0 is moved to match in the SAME override
# (docs/testing.md: "the two must move together, since the backend only
# trusts forwarded headers from that exact subnet"). Never commit a run with
# this set -- it exists purely to dodge a shared-host collision, and the
# shipped subnet is correct on a real appliance host with no concurrent
# stacks.
if [[ -n "${WAYPOINT_E2E_SUBNET:-}" ]]; then
	log "WAYPOINT_E2E_SUBNET=${WAYPOINT_E2E_SUBNET} -- overriding the edge network subnet + ForwardedHeaders:KnownNetworks together"
	SUBNET_OVERRIDE="$(mktemp)"
	cat > "${SUBNET_OVERRIDE}" <<-EOF
	services:
	  backend:
	    environment:
	      ForwardedHeaders__KnownNetworks__0: "${WAYPOINT_E2E_SUBNET}"
	networks:
	  edge:
	    driver: bridge
	    ipam:
	      config:
	        - subnet: ${WAYPOINT_E2E_SUBNET}
	EOF
	if [[ -n "${WAYPOINT_E2E_OVERRIDE_FILE}" ]]; then
		WAYPOINT_E2E_OVERRIDE_FILE="${WAYPOINT_E2E_OVERRIDE_FILE} ${SUBNET_OVERRIDE}"
	else
		WAYPOINT_E2E_OVERRIDE_FILE="${SUBNET_OVERRIDE}"
	fi
fi

SCRATCH_KEY_DIR="$(mktemp -d)"
openssl rand -hex 32 > "${SCRATCH_KEY_DIR}/master.key"
chmod 644 "${SCRATCH_KEY_DIR}/master.key"

CONFIG_DIR="${REPO_ROOT}/config"
mkdir -p "${CONFIG_DIR}/secrets" "${CONFIG_DIR}/local-auth"
cp "${SCRATCH_KEY_DIR}/master.key" "${CONFIG_DIR}/secrets/master.key"
chmod 644 "${CONFIG_DIR}/secrets/master.key"

ADMIN_PASSWORD="invented-e2e-password-$(openssl rand -hex 4)"
log "Computing admin password hash via backend --hash-password"
(
	cd "${REPO_ROOT}/backend"
	dotnet build Waypoint.Api >/dev/null
	printf '%s\n' "${ADMIN_PASSWORD}" | dotnet run --project Waypoint.Api --no-launch-profile --no-build -- --hash-password \
		| tail -1 > "${SCRATCH_KEY_DIR}/admin-hash"
)
if [[ ! -s "${SCRATCH_KEY_DIR}/admin-hash" ]]; then
	echo "error: could not compute admin password hash locally -- Playwright login step would fail closed" >&2
	exit 1
fi
cp "${SCRATCH_KEY_DIR}/admin-hash" "${CONFIG_DIR}/local-auth/admin-password-hash"

MASTER_KEY_HOST_PATH="${HOST_PREFIX}/config/secrets/master.key"
ADMIN_HASH_HOST_PATH="${HOST_PREFIX}/config/local-auth/admin-password-hash"
MASTER_KEY_OVERRIDE="$(mktemp)"
{
	echo "services:"
	echo "  backend:"
	echo "    volumes:"
	echo "      - type: bind"
	echo "        source: ${MASTER_KEY_HOST_PATH}"
	echo "        target: /run/secrets/waypoint-master-key"
	echo "        read_only: true"
	echo "      - type: bind"
	echo "        source: ${ADMIN_HASH_HOST_PATH}"
	echo "        target: /run/secrets/local-auth-admin-password-hash"
	echo "        read_only: true"
	echo "  compliance-runner:"
	echo "    volumes:"
	echo "      - type: bind"
	echo "        source: ${MASTER_KEY_HOST_PATH}"
	echo "        target: /run/secrets/waypoint-master-key"
	echo "        read_only: true"
	echo "    environment:"
	# See fresh-stack-smoke-test.sh's identical override for why: a
	# nested-Docker/devcontainer host with unreadable cgroup limits falls
	# back to 1 CPU core, below scan's 2.0-core weight, so scan jobs never
	# get admitted without raising the fallback floor.
	echo "      RunnerResources__FallbackCpuCores: \"4\""
	echo "      RunnerResources__FallbackMemoryBytes: \"4294967296\""
	echo "  download-runner:"
	echo "    volumes:"
	echo "      - type: bind"
	echo "        source: ${MASTER_KEY_HOST_PATH}"
	echo "        target: /run/secrets/waypoint-master-key"
	echo "        read_only: true"
} > "${MASTER_KEY_OVERRIDE}"

COMPOSE_FILES="-f docker-compose.yml"
if [[ -n "${WAYPOINT_E2E_OVERRIDE_FILE}" ]]; then
	# WAYPOINT_E2E_OVERRIDE_FILE may carry more than one path (space-separated
	# -- the devcontainer bind-mount override and the subnet override above
	# can both be set at once), so each gets its own `-f`.
	for f in ${WAYPOINT_E2E_OVERRIDE_FILE}; do
		COMPOSE_FILES="${COMPOSE_FILES} -f ${f}"
	done
fi
COMPOSE_FILES="${COMPOSE_FILES} -f ${MASTER_KEY_OVERRIDE}"
DC="docker compose -p ${PROJECT} ${COMPOSE_FILES}"

docker volume create "${PROJECT}_compliance-profiles" >/dev/null
docker run --rm -v "${PROJECT}_compliance-profiles:/x" alpine \
	sh -c "mkdir -p /x/vsphere /x/nsx /x/srg" >/dev/null

# --- Bring-up (real build) ----------------------------------------------

log "docker compose up --build -d (fresh build, isolated project/port)"
WAYPOINT_HTTPS_PORT="${PORT}" ${DC} up --build -d

log "Waiting for every container to report healthy"
for id in $(${DC} ps -q); do
	name="$(docker inspect -f '{{.Name}}' "${id}")"
	for i in $(seq 1 90); do
		status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}no-healthcheck{{end}}' "${id}" 2>/dev/null || echo "gone")"
		[[ "${status}" == "healthy" || "${status}" == "no-healthcheck" ]] && break
		if [[ "${i}" == 90 ]]; then
			echo "  ${name} never became healthy (last status: ${status}) -- docker logs ${id}:" >&2
			docker logs --tail 50 "${id}" >&2 || true
		fi
		sleep 2
	done
done
echo "Final container health:"
${DC} ps

# --- Helper container + API helpers (mirrors fresh-stack-smoke-test.sh) --

EDGE_NETWORK="${PROJECT}_edge"
docker run -d --rm --name "${HELPER_NAME}" --network "${EDGE_NETWORK}" \
	--entrypoint sh curlimages/curl -c "while true; do sleep 3600; done" >/dev/null
HELPER_STARTED=1

net_curl() { docker exec "${HELPER_NAME}" curl -k -sS "$@" || true; }
json_field() {
	python3 -c "
import json,sys
data = json.load(sys.stdin)
path = sys.argv[1].split('.')
for p in path:
    if isinstance(data, list):
        data = data[int(p)]
    else:
        data = data[p]
print(data)
" "$1"
}
api_post_body() { net_curl -X POST -H "Content-Type: application/json" -H "Authorization: Bearer ${ADMIN_TOKEN}" -d "$2" "${NET_BASE}$1"; }

for i in $(seq 1 30); do
	code="$(net_curl -o /dev/null -w '%{http_code}' --max-time 2 "${NET_BASE}/api/v1/health")"
	[[ "${code}" == "200" ]] && break
	sleep 1
done

log "Logging in as admin, seeding a site/target/service-credential via the API"
LOGIN_BODY="$(net_curl -X POST -H 'Content-Type: application/json' \
	-d "{\"username\":\"admin\",\"password\":\"${ADMIN_PASSWORD}\"}" \
	"${NET_BASE}/api/v1/auth/login")"
ADMIN_TOKEN="$(printf '%s' "${LOGIN_BODY}" | json_field token 2>/dev/null || true)"
if [[ -z "${ADMIN_TOKEN}" ]]; then
	echo "error: admin login did not return a token: ${LOGIN_BODY}" >&2
	exit 1
fi

SITE_NAME="e2e-site-$(date +%s)"
CREDENTIAL_NAME="e2e-cred-$(date +%s)"
SITE_RESPONSE="$(api_post_body /api/v1/sites "{\"name\":\"${SITE_NAME}\"}")"
SITE_ID="$(printf '%s' "${SITE_RESPONSE}" | json_field id 2>/dev/null || true)"
[[ -n "${SITE_ID}" ]] || { echo "error: seed site creation failed: ${SITE_RESPONSE}" >&2; exit 1; }

# Invented, obviously-fictional hostname -- never a real lab host (CLAUDE.md).
TARGET_RESPONSE="$(api_post_body "/api/v1/sites/${SITE_ID}/targets" \
	'{"kind":"ssh","name":"srg-e2e-01","connection":{"host":"srg-e2e-01.example.internal"}}')"
TARGET_ID="$(printf '%s' "${TARGET_RESPONSE}" | json_field id 2>/dev/null || true)"
[[ -n "${TARGET_ID}" ]] || { echo "error: seed target creation failed: ${TARGET_RESPONSE}" >&2; exit 1; }

CRED_RESPONSE="$(api_post_body /api/v1/credentials \
	"{\"name\":\"${CREDENTIAL_NAME}\",\"credential_type\":\"ssh\",\"owner\":\"shared\",\"secret\":\"invented-e2e-seed-canary\"}")" # gitleaks:allow — invented seed canary, never a real secret
CREDENTIAL_ID="$(printf '%s' "${CRED_RESPONSE}" | json_field id 2>/dev/null || true)"
[[ -n "${CREDENTIAL_ID}" ]] || { echo "error: seed credential creation failed: ${CRED_RESPONSE}" >&2; exit 1; }

# Best-effort catalog sync so 04-catalog-download.spec.ts has something to
# queue; the test itself tolerates an empty catalog (a legitimate fresh-stack
# state, matching fresh-stack-smoke-test.sh step 5's own accommodation).
net_curl -o /dev/null -X POST -H "Authorization: Bearer ${ADMIN_TOKEN}" -H 'Content-Type: application/json' -d '{}' "${NET_BASE}/api/v1/catalog/sync"
sleep 5

log "Seeded: site=${SITE_ID} (${SITE_NAME}), target=${TARGET_ID}, credential=${CREDENTIAL_ID} (${CREDENTIAL_NAME})"

# --- Playwright base URL reachability -------------------------------------
#
# Unlike fresh-stack-smoke-test.sh's curl checks (routed through a helper
# container on the stack's own edge network), Playwright's browser runs as a
# real process on THIS script's own host/network namespace -- there is no
# equivalent "run every request inside a container" trick available, so the
# base URL it navigates to must actually be reachable from here. In a
# devcontainer/remote-daemon environment the published host port is not
# reachable from this namespace at all (docs/testing.md's Postgres-fixture
# section documents the identical failure mode; verified again here: a
# throwaway container on the stack's own edge network reaches nginx fine,
# while a direct probe from this process's namespace to 127.0.0.1:$PORT
# times out/ECONNREFUSEDs). When the direct probe fails, this script joins
# ITS OWN container to the stack's edge network (`docker network connect`,
# undone in cleanup) so `https://nginx` resolves via Docker's embedded DNS
# from this process directly -- then Playwright navigates to that instead of
# the published port. On a real appliance host (no devcontainer/remote-daemon
# indirection) the direct probe succeeds and this block is a no-op.
PLAYWRIGHT_BASE_URL="${BASE}"
if ! curl -k -sS -o /dev/null --max-time 3 "${BASE}/api/v1/health" 2>/dev/null; then
	log "${BASE} not reachable from this process's own network namespace -- joining ${PROJECT}_edge directly"
	EDGE_NETWORK="${PROJECT}_edge"
	if docker network connect "${EDGE_NETWORK}" "$(hostname)" 2>/dev/null; then
		SELF_JOINED_EDGE_NETWORK="${EDGE_NETWORK}"
		sleep 1
		if curl -k -sS -o /dev/null --max-time 3 "${NET_BASE}/api/v1/health" 2>/dev/null; then
			PLAYWRIGHT_BASE_URL="${NET_BASE}"
			log "Joined edge network -- Playwright will navigate to ${NET_BASE}"
		else
			echo "warning: joined ${EDGE_NETWORK} but ${NET_BASE} still unreachable -- Playwright will likely fail to navigate" >&2
		fi
	else
		echo "warning: could not join ${EDGE_NETWORK} (already attached to another network, or lacking permission) -- falling back to ${BASE} and letting Playwright fail loudly if unreachable" >&2
	fi
fi

# --- Run Playwright against this stack -----------------------------------

PLAYWRIGHT_JSON_OUTPUT_FILE="${SCRATCH_KEY_DIR}/playwright-results.json"
export PLAYWRIGHT_JSON_OUTPUT_FILE
log "Running Playwright against ${PLAYWRIGHT_BASE_URL}"
(
	use_node22
	cd "${FRONTEND_DIR}"
	export E2E_BASE_URL="${PLAYWRIGHT_BASE_URL}"
	export E2E_ADMIN_PASSWORD="${ADMIN_PASSWORD}"
	export E2E_SITE_NAME="${SITE_NAME}"
	export E2E_CREDENTIAL_NAME="${CREDENTIAL_NAME}"
	npm run test:e2e
)
PLAYWRIGHT_EXIT=$?

# Issue #500: a bare exit code cannot distinguish "the suite ran and passed"
# from "the suite never ran" (e.g. `tsc` failing before `playwright test`
# even starts still exits nonzero, but a differently-broken invocation could
# exit 0 with zero tests executed). Parse the JSON reporter's stats and
# require at least one test to have actually run before trusting a 0 exit.
EXECUTED=0
if [[ -s "${PLAYWRIGHT_JSON_OUTPUT_FILE}" ]]; then
	EXECUTED="$(python3 -c "
import json
with open('${PLAYWRIGHT_JSON_OUTPUT_FILE}') as f:
    data = json.load(f)
stats = data.get('stats', {})
print(int(stats.get('expected', 0)) + int(stats.get('unexpected', 0)) + int(stats.get('flaky', 0)))
" 2>/dev/null || echo 0)"
fi

log "Playwright exit code: ${PLAYWRIGHT_EXIT}, tests executed: ${EXECUTED}"

if [[ "${PLAYWRIGHT_EXIT}" -eq 0 && "${EXECUTED}" -eq 0 ]]; then
	echo "error: Playwright reported success but executed zero tests -- treating as a failed run (issue #500)" >&2
	exit 1
fi

exit "${PLAYWRIGHT_EXIT}"
