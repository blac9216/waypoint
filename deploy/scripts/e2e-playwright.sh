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
# ALSO provisions a persistent Keycloak-realm dev-admin user via #846's
# keycloak-dev-admin service (issue #848), seeds a site/target/credential via
# the local-auth API so the Playwright suite's Start-a-Scan wizard has
# something to select, points Playwright's E2E_BASE_URL/E2E_ADMIN_USERNAME/
# E2E_ADMIN_PASSWORD (the Keycloak dev-admin identity, for the browser's real
# PKCE login -- issue #848)/E2E_SITE_NAME/E2E_CREDENTIAL_NAME at it, ensures
# frontend/ has its own node_modules and a Chromium binary before running
# `npm run test:e2e` from frontend/, and tears the stack down fully
# (trap-based, same as the smoke script).
#
# Requires: docker compose v2, curl, openssl, python3, Node 22 (nvm). This
# script ensures its own frontend/ dependencies (npm ci, playwright install
# chromium) if missing -- browser binaries are still never committed, per
# this script's own README note below.
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
	# Issue #847: everything the generator wrote for this run -- secrets,
	# TLS, the admin-password hash, the override/env files -- lives ONLY
	# under this one slug-scoped directory, so a full run's throwaway state
	# is exactly this one `rm -rf`.
	rm -rf "${DEPLOY_DIR}/.generated/${SLUG}" "${DEPLOY_DIR}/.generated/${SLUG}.hash-stage"
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

# Issue #847: no TLS staging here -- deploy/scripts/generate-dev-stack.sh
# --mode agent generates its own SAN-correct self-signed pair under
# deploy/.generated/${SLUG}/tls/ and binds it directly at compose.yaml's
# per-file targets (/etc/nginx/certs/tls.{crt,key}), replacing the base's
# mandatory-but-missing deploy/config/tls/ mounts -- see the generator call
# below. deploy/config/tls/ (the production/persistent-mode location) is
# never touched by an agent-mode run.

if [[ ! -f "${FRONTEND_DIR}/dist/index.html" ]]; then
	log "frontend/dist missing -- building it first (Node 22 required)"
	(
		use_node22
		cd "${FRONTEND_DIR}"
		npm ci
		npm run build
	)
fi

# Issue #847: deploy/scripts/generate-dev-stack.sh --mode agent replaces this
# script's former hand-rolled devcontainer bind-mount translation, secrets,
# subnet-collision override, master key, and LocalAuth__*/RunnerResources__
# Fallback* override with one generator call -- see
# fresh-stack-smoke-test.sh's identical refactor comment for the full
# rationale. Everything lands under deploy/.generated/${SLUG}/ only. This
# script still owns computing the admin-password hash itself (a
# backend-specific step) and handing it to the generator.
GENERATED_STATE_DIR="${DEPLOY_DIR}/.generated/${SLUG}"

# The admin-password hash is staged in a SEPARATE scratch directory, never in
# ${GENERATED_STATE_DIR}: this script must not create the generator's state
# directory behind its back (round-2 review of #847 -- the generator refuses a
# foreign stack that has claimed this project name, and a caller that
# pre-creates state must not be able to influence that decision). The
# generator copies the hash into the slug directory itself and mounts its own
# copy.
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

# WAYPOINT_E2E_SUBNET: overrides the generated stack's `edge` subnet when the
# agent-mode default (203.0.113.0/24) collides with a concurrent stack on
# this host (docs/testing.md). Never commit a run with this set.
#
# --keycloak-dev-admin (issue #848, building on #846's keycloak-dev-admin
# service): the local-auth admin hash above still seeds the browser-facing
# runner-parity/CRUD/scan/catalog specs that don't touch login itself and
# still gates this script's own API seeding curl calls (both narrowly
# in-scope local-auth uses issue #848 explicitly leaves alone) -- but the
# LOGIN spec now drives the real Keycloak authorization-code/PKCE flow, which
# needs a real Keycloak-realm user. This flag makes the generated override
# provision one (deploy/keycloak-dev-admin) instead of hand-authoring that
# wiring here a second time.
KEYCLOAK_DEV_USERNAME="developer"
GENERATE_ARGS=(--mode agent --slug "${SLUG}" --public-url "https://localhost:${PORT}" --port "${PORT}" \
	--local-auth-admin-hash-file "${HASH_STAGE_DIR}/admin-password-hash" \
	--keycloak-dev-admin --username "${KEYCLOAK_DEV_USERNAME}")
if [[ -n "${WAYPOINT_E2E_SUBNET:-}" ]]; then
	log "WAYPOINT_E2E_SUBNET=${WAYPOINT_E2E_SUBNET} -- overriding the generated edge subnet"
	GENERATE_ARGS+=(--subnet "${WAYPOINT_E2E_SUBNET}")
fi

log "Generating isolated dev stack (deploy/scripts/generate-dev-stack.sh --mode agent --slug ${SLUG})"
"${SCRIPT_DIR}/generate-dev-stack.sh" "${GENERATE_ARGS[@]}"
# The generator copied the hash into the slug directory; the scratch staging
# copy has no further purpose.
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

# keycloak-dev-admin (issue #846) is a one-shot service with no HEALTHCHECK
# of its own -- the generic loop above treats "no-healthcheck" as success the
# moment the container exists, which for a `restart: "no"` provisioning
# container means "started", not "finished provisioning the dev-admin user".
# The real signal is its exit code, waited for explicitly here so a failed
# provisioning run (e.g. the realm not importing, a bad secret file) fails
# this script loudly instead of surfacing later as an inexplicable Keycloak
# login failure.
KEYCLOAK_DEV_ADMIN_ID="$(${DC} ps -q keycloak-dev-admin 2>/dev/null || true)"
if [[ -n "${KEYCLOAK_DEV_ADMIN_ID}" ]]; then
	log "Waiting for keycloak-dev-admin (issue #846) to finish provisioning the dev Keycloak user"
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

# Issue #848: Playwright's browser-driven login now authenticates through
# Keycloak's real PKCE flow, not the dev-flag local-auth form -- so the
# credential it needs is the keycloak-dev-admin (issue #846) provisioned
# user, not ${ADMIN_PASSWORD} (which stays local-auth-only, used above only
# for this script's own API seeding). Read straight from the generator's own
# secret file -- never echoed, never a CLI argument.
KEYCLOAK_DEV_ADMIN_PASSWORD_FILE="${GENERATED_STATE_DIR}/secrets/dev-admin-password"
if [[ ! -s "${KEYCLOAK_DEV_ADMIN_PASSWORD_FILE}" ]]; then
	echo "error: ${KEYCLOAK_DEV_ADMIN_PASSWORD_FILE} missing or empty -- Playwright's Keycloak login would fail closed" >&2
	exit 1
fi
KEYCLOAK_DEV_ADMIN_PASSWORD="$(cat "${KEYCLOAK_DEV_ADMIN_PASSWORD_FILE}")"

PLAYWRIGHT_JSON_OUTPUT_FILE="${GENERATED_STATE_DIR}/playwright-results.json"
export PLAYWRIGHT_JSON_OUTPUT_FILE
log "Running Playwright against ${PLAYWRIGHT_BASE_URL}"
# `set -euo pipefail` (top of this script) would otherwise terminate the
# whole script the instant the subshell below exits nonzero -- BEFORE
# PLAYWRIGHT_EXIT could be assigned, before issue #500's own
# zero-tests-executed guard further down, and before the final
# `exit "${PLAYWRIGHT_EXIT}"` ever ran. A failed Playwright run was silently
# swallowed as whatever exit code the EXIT trap's cleanup happened to
# produce, not the suite's real result -- found while validating issue #848
# (a genuinely failing suite never printed this script's own "Playwright
# exit code:" log line at all). `set +e`/`set -e` bracket exactly the
# subshell so errexit is suspended only long enough to capture its real exit
# code into PLAYWRIGHT_EXIT.
set +e
(
	use_node22
	cd "${FRONTEND_DIR}"
	# Issue #848 finding #2 (orchestrator comment on #847's validation): this
	# script used to assume node_modules was already present in a fresh
	# sandbox -- an ordering dependency on whatever other script happened to
	# populate frontend/dist first. Ensure this script's own dependencies
	# regardless of what ran before it.
	if [[ ! -d node_modules ]]; then
		log "frontend/node_modules missing -- npm ci"
		npm ci
	fi
	if [[ ! -x node_modules/.bin/playwright ]]; then
		echo "error: node_modules/.bin/playwright missing after npm ci -- @playwright/test did not install correctly" >&2
		exit 1
	fi
	# Playwright's default browser cache dir (PLAYWRIGHT_BROWSERS_PATH unset)
	# -- a simple existence check rather than shelling out to Node to ask
	# playwright-core for its resolved executable path.
	if ! find "${PLAYWRIGHT_BROWSERS_PATH:-${HOME}/.cache/ms-playwright}" -maxdepth 1 -iname 'chromium-*' 2>/dev/null | grep -q .; then
		log "Chromium browser binary not found -- npx playwright install chromium"
		npx playwright install chromium
	fi
	export E2E_BASE_URL="${PLAYWRIGHT_BASE_URL}"
	export E2E_ADMIN_USERNAME="${KEYCLOAK_DEV_USERNAME}"
	export E2E_ADMIN_PASSWORD="${KEYCLOAK_DEV_ADMIN_PASSWORD}"
	export E2E_SITE_NAME="${SITE_NAME}"
	export E2E_CREDENTIAL_NAME="${CREDENTIAL_NAME}"
	npm run test:e2e
)
PLAYWRIGHT_EXIT=$?
set -e

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
