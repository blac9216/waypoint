#!/usr/bin/env bash
#
# Playwright live-stack browser coverage: brings up an isolated `docker
# compose` project (same recipe as fresh-stack-smoke-test.sh), also
# provisions a persistent Keycloak-realm dev-admin user, seeds a
# site/target/credential via the local-auth API, points Playwright's
# E2E_BASE_URL/E2E_ADMIN_USERNAME/E2E_ADMIN_PASSWORD/E2E_SITE_NAME/
# E2E_CREDENTIAL_NAME at it, ensures frontend/ deps + Chromium are present,
# runs `npm run test:e2e`, and tears the stack down fully.
#
# Requires: docker compose v2, curl, openssl, python3, Node 22 (nvm).
# Ensures its own frontend/ dependencies if missing; browser binaries are
# never committed.
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

# why: docs/rationale/deploy.md#e2e-npm-ci-stamp
# Caller must be cd'd into ${FRONTEND_DIR} with Node 22 selected. Sets
# NPM_CI_RAN=1 when an install actually ran; returns nonzero on failure.
NPM_CI_STAMP="node_modules/.waypoint-npm-ci-stamp"
NPM_CI_RAN=0
frontend_npm_ci_if_needed() {
	local lockfile_hash
	lockfile_hash="$(sha256sum package-lock.json | awk '{print $1}')"
	NPM_CI_RAN=0
	if [[ ! -d node_modules ]]; then
		log "frontend/node_modules missing -- npm ci"
	elif [[ ! -f "${NPM_CI_STAMP}" ]] || [[ "$(cat "${NPM_CI_STAMP}")" != "${lockfile_hash}" ]]; then
		log "frontend/node_modules stale (package-lock.json does not match the last successful npm ci) -- npm ci"
	else
		return 0
	fi
	NPM_CI_RAN=1
	# Remove any pre-existing stamp before installing so an interrupted run
	# cannot leave a stale-but-matching stamp behind.
	rm -f "${NPM_CI_STAMP}"
	if ! npm ci; then
		echo "error: npm ci failed in ${FRONTEND_DIR} -- frontend dependencies are NOT installed (no stamp written; the next run will retry the install). Fix the install itself (registry/network reachability, disk space, or package-lock.json) and re-run." >&2
		return 1
	fi
	echo "${lockfile_hash}" >"${NPM_CI_STAMP}"
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
# Set once the reachability-probe helper container is running, so cleanup()
# can always reap it even on early failure.
PROBE_STARTED=""
# Set if this process joins the stack's `edge` network directly (see
# "Playwright base URL reachability" below); lets cleanup always disconnect it.
SELF_JOINED_EDGE_NETWORK=""

log() { printf '\n=== %s ===\n' "$*"; }

# shellcheck disable=SC2317,SC2329  # invoked indirectly via `trap cleanup EXIT`
cleanup() {
	log "Tearing down ${PROJECT} (docs/testing.md: always your own project, always -v)"
	if [[ -n "${HELPER_STARTED}" ]]; then
		docker rm -f "${HELPER_NAME}" >/dev/null 2>&1 || true
	fi
	if [[ -n "${PROBE_STARTED}" ]]; then
		docker rm -f "${PROBE_NAME}" >/dev/null 2>&1 || true
	fi
	if [[ -n "${SELF_JOINED_EDGE_NETWORK}" ]]; then
		docker network disconnect "${SELF_JOINED_EDGE_NETWORK}" "$(hostname)" >/dev/null 2>&1 || true
	fi
	(cd "${DEPLOY_DIR}" && ${DC:-docker compose -p "${PROJECT}"} down -v) || true
	rm -rf "${DEPLOY_DIR}/.generated/${SLUG}" "${DEPLOY_DIR}/.generated/${SLUG}.hash-stage"
}
trap cleanup EXIT

log "Isolation: project=${PROJECT} port=${PORT}"
echo "docker ps (containers NOT belonging to this run -- do not touch them):"
docker ps --format '{{.Names}}' | grep -v "^${PROJECT}-" || echo "  (none currently running)"

# --- Prerequisites -----------------------------------------------------
# Fail fast, before any stack comes up, rather than a confusing downstream
# failure or a false "verified" exit 0.

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

# TLS is generated by generate-dev-stack.sh --mode agent below, not staged
# here; deploy/config/tls/ (the persistent-mode location) is never touched.

if [[ ! -f "${FRONTEND_DIR}/dist/index.html" ]]; then
	log "frontend/dist missing -- building it first (Node 22 required)"
	(
		use_node22
		cd "${FRONTEND_DIR}"
		frontend_npm_ci_if_needed
		npm run build
	)
fi

# generate-dev-stack.sh --mode agent owns secrets/TLS/override generation;
# this script only computes the admin-password hash and hands it in.
GENERATED_STATE_DIR="${DEPLOY_DIR}/.generated/${SLUG}"

# why: docs/rationale/deploy.md#smoke-hash-stage-separation
HASH_STAGE_DIR="${DEPLOY_DIR}/.generated/${SLUG}.hash-stage"
mkdir -p "${HASH_STAGE_DIR}"

ADMIN_PASSWORD="invented-e2e-password-$(openssl rand -hex 4)"
log "Computing admin password hash via backend --hash-password"
(
	cd "${REPO_ROOT}/backend"
	dotnet build Waypoint.Api >/dev/null
	printf '%s\n' "${ADMIN_PASSWORD}" | dotnet run --project Waypoint.Api --no-launch-profile --no-build -- --hash-password \
		| tail -1 > "${HASH_STAGE_DIR}/admin-password-hash"
)
if [[ ! -s "${HASH_STAGE_DIR}/admin-password-hash" ]]; then
	echo "error: could not compute admin password hash locally -- Playwright login step would fail closed" >&2
	exit 1
fi

# --- Published-port reachability -----------------------------------------
# why: docs/rationale/deploy.md#e2e-reachability-probe
PROBE_NAME="${PROJECT}-reachability-probe"
docker run -d --rm --name "${PROBE_NAME}" -p 127.0.0.1::7777 --entrypoint sh curlimages/curl \
	-c 'nc -lk -p 7777' >/dev/null
PROBE_STARTED=1
PROBE_HOST_PORT="$(docker port "${PROBE_NAME}" 7777/tcp | head -1 | cut -d: -f2)"
# Bounded retry: with `userland-proxy=false`, a connect landing before `nc`
# finishes binding is refused even though the port genuinely works once
# listening.
DIRECT_REACHABLE=0
for attempt in 1 2 3; do
	if timeout 2 bash -c "echo >/dev/tcp/127.0.0.1/${PROBE_HOST_PORT}" 2>/dev/null; then
		DIRECT_REACHABLE=1
		break
	fi
	[[ "${attempt}" -lt 3 ]] && sleep 0.3
done
docker rm -f "${PROBE_NAME}" >/dev/null 2>&1 || true
PROBE_STARTED=""

if [[ "${DIRECT_REACHABLE}" -eq 1 ]]; then
	PUBLIC_URL="${BASE}"
else
	log "Published ports not reachable from this process's own network namespace -- generating with public-url=${NET_BASE}"
	PUBLIC_URL="${NET_BASE}"
fi

# WAYPOINT_E2E_SUBNET: overrides the generated stack's `edge` subnet on a
# collision with a concurrent stack (docs/testing.md). Never commit with this set.
#
# --keycloak-dev-admin: the LOGIN spec drives the real Keycloak PKCE flow, so
# it needs a real Keycloak-realm user; other specs still use the local-auth
# admin hash above.
KEYCLOAK_DEV_USERNAME="developer"
GENERATE_ARGS=(--mode agent --slug "${SLUG}" --public-url "${PUBLIC_URL}" --port "${PORT}" \
	--local-auth-admin-hash-file "${HASH_STAGE_DIR}/admin-password-hash" \
	--keycloak-dev-admin --username "${KEYCLOAK_DEV_USERNAME}")
if [[ -n "${WAYPOINT_E2E_SUBNET:-}" ]]; then
	log "WAYPOINT_E2E_SUBNET=${WAYPOINT_E2E_SUBNET} -- overriding the generated edge subnet"
	GENERATE_ARGS+=(--subnet "${WAYPOINT_E2E_SUBNET}")
fi

log "Generating isolated dev stack (deploy/scripts/generate-dev-stack.sh --mode agent --slug ${SLUG})"
"${SCRIPT_DIR}/generate-dev-stack.sh" "${GENERATE_ARGS[@]}"
# The generator copied the hash into the slug directory.
rm -rf "${HASH_STAGE_DIR}"

DC="docker compose -p ${PROJECT} -f compose.yaml -f ${GENERATED_STATE_DIR}/override.yaml --env-file ${GENERATED_STATE_DIR}/.env"
if [[ -n "${WAYPOINT_E2E_OVERRIDE_FILE:-}" ]]; then
	# Rare manual escape hatch, layered LAST so it can override anything the
	# generator wrote. May carry more than one space-separated path.
	for f in ${WAYPOINT_E2E_OVERRIDE_FILE}; do
		DC="${DC} -f ${f}"
	done
fi

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

# keycloak-dev-admin is a one-shot service with no HEALTHCHECK -- the exit
# code is the real "finished provisioning" signal, waited for explicitly.
KEYCLOAK_DEV_ADMIN_ID="$(${DC} ps -q keycloak-dev-admin 2>/dev/null || true)"
if [[ -n "${KEYCLOAK_DEV_ADMIN_ID}" ]]; then
	log "Waiting for keycloak-dev-admin to finish provisioning the dev Keycloak user"
	KEYCLOAK_DEV_ADMIN_EXIT="$(docker wait "${KEYCLOAK_DEV_ADMIN_ID}")"
	if [[ "${KEYCLOAK_DEV_ADMIN_EXIT}" != "0" ]]; then
		echo "error: keycloak-dev-admin exited ${KEYCLOAK_DEV_ADMIN_EXIT} -- Playwright's Keycloak login would fail closed. Logs:" >&2
		docker logs "${KEYCLOAK_DEV_ADMIN_ID}" >&2 || true
		exit 1
	fi
	echo "keycloak-dev-admin: provisioned the dev Keycloak user (exit 0)."
fi

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
# why: docs/rationale/deploy.md#e2e-reachability-probe
PLAYWRIGHT_BASE_URL="${BASE}"
if [[ "${DIRECT_REACHABLE}" -eq 0 ]]; then
	EDGE_NETWORK="${PROJECT}_edge"
	log "Published ports unreachable from this namespace -- joining ${EDGE_NETWORK} directly"
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

# Playwright's login authenticates via Keycloak's real PKCE flow, so it
# needs the keycloak-dev-admin credential, not ${ADMIN_PASSWORD}. Read
# straight from the generator's own secret file -- never echoed.
KEYCLOAK_DEV_ADMIN_PASSWORD_FILE="${GENERATED_STATE_DIR}/secrets/dev-admin-password"
if [[ ! -s "${KEYCLOAK_DEV_ADMIN_PASSWORD_FILE}" ]]; then
	echo "error: ${KEYCLOAK_DEV_ADMIN_PASSWORD_FILE} missing or empty -- Playwright's Keycloak login would fail closed" >&2
	exit 1
fi
KEYCLOAK_DEV_ADMIN_PASSWORD="$(cat "${KEYCLOAK_DEV_ADMIN_PASSWORD_FILE}")"

PLAYWRIGHT_JSON_OUTPUT_FILE="${GENERATED_STATE_DIR}/playwright-results.json"
export PLAYWRIGHT_JSON_OUTPUT_FILE
# Dependency prep runs in its own subshell, before `set +e` below, so a
# failed install aborts with its own message instead of masquerading as a
# Playwright result.
(
	use_node22
	cd "${FRONTEND_DIR}"
	frontend_npm_ci_if_needed
	if [[ ! -x node_modules/.bin/playwright ]]; then
		if [[ "${NPM_CI_RAN}" -eq 1 ]]; then
			echo "error: node_modules/.bin/playwright missing after npm ci -- @playwright/test did not install correctly" >&2
		else
			echo "error: node_modules/.bin/playwright missing, but frontend/node_modules already matched package-lock.json (npm ci was NOT re-run) -- @playwright/test is not declared as a dependency of the installed tree; check frontend/package.json and frontend/package-lock.json, or remove ${NPM_CI_STAMP} to force a reinstall" >&2
		fi
		exit 1
	fi
	# Playwright's default browser cache dir (PLAYWRIGHT_BROWSERS_PATH unset)
	# -- a simple existence check rather than shelling out to Node to ask
	# playwright-core for its resolved executable path.
	if ! find "${PLAYWRIGHT_BROWSERS_PATH:-${HOME}/.cache/ms-playwright}" -maxdepth 1 -iname 'chromium-*' 2>/dev/null | grep -q .; then
		log "Chromium browser binary not found -- npx playwright install chromium"
		npx playwright install chromium
	fi
)

log "Running Playwright against ${PLAYWRIGHT_BASE_URL}"
# why: docs/rationale/deploy.md#e2e-playwright-exit-capture
set +e
(
	use_node22
	cd "${FRONTEND_DIR}"
	export E2E_BASE_URL="${PLAYWRIGHT_BASE_URL}"
	export E2E_ADMIN_USERNAME="${KEYCLOAK_DEV_USERNAME}"
	export E2E_ADMIN_PASSWORD="${KEYCLOAK_DEV_ADMIN_PASSWORD}"
	export E2E_SITE_NAME="${SITE_NAME}"
	export E2E_CREDENTIAL_NAME="${CREDENTIAL_NAME}"
	npm run test:e2e
)
PLAYWRIGHT_EXIT=$?
set -e

# A bare exit code cannot distinguish "ran and passed" from "never ran" --
# require at least one test to have actually executed before trusting a 0 exit.
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
	echo "error: Playwright reported success but executed zero tests -- treating as a failed run" >&2
	exit 1
fi

exit "${PLAYWRIGHT_EXIT}"
