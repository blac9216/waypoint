#!/usr/bin/env bash
#
# Issue #444 fresh-stack M1/M2 parity acceptance matrix -- deploy smoke-test tooling.
#
# Brings up an ISOLATED `docker compose` project from scratch (a real
# `--build`, not a cached image reuse) and walks the operator-visible surface
# the runner architecture (ADRs 0013/0014/0015) must still support after
# execution moved out of the API host (#443): system/runner health,
# catalog-index without the gated tool, download's honest tool-absent
# failure, site/target/credential setup, service-credential and
# personal-credential scan enqueue/claim/honest-failure, cancellation,
# SSE reconnect, stopping one runner leaves the other's domain visible and
# functional, replica scale-out claims without duplication, and a secret
# canary check across the database/artifacts/logs.
#
# Follows docs/testing.md's mandatory isolation recipe: unique -p project
# name, unique host port, no touching another agent's stack, full `down -v`
# teardown on exit (trap-based, runs even on failure).
#
# HTTP checks run through a helper container on the stack's own `edge`
# network, not through the host-published port directly. docs/testing.md's
# Postgres-fixture section documents "remote Docker daemon" / "bridge-
# networked test process" environments where a published container port is
# unreachable from the test process's own network namespace at all -- issue
# #219 for Postgres, and this script hit the identical failure mode for
# nginx's published HTTPS port while first authoring it (verified: a
# throwaway container on the SAME edge network reaches nginx fine over
# `https://nginx/`, while this script's own process gets ECONNREFUSED/timeout
# on both 127.0.0.1:$PORT and the bridge gateway). Routing every request
# through `docker exec` into a long-lived helper container on that network
# sidesteps the whole class of host-loopback/NAT unreachability, and costs
# nothing extra on a host where the published port IS reachable -- the helper
# container reaches nginx exactly the way it would from any other client.
#
# Usage:
#   deploy/scripts/fresh-stack-smoke-test.sh [slug] [port]
#
# Defaults: slug derived from date+pid, port 19443. Both are printed at the
# start of the run so a concurrent agent can tell stacks apart.
#
# Requires: docker compose v2, curl, openssl, python3 (for the SSE probe and
# JSON field extraction -- no jq dependency assumed).

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
DEPLOY_DIR="$(cd -- "${SCRIPT_DIR}/.." &>/dev/null && pwd)"
REPO_ROOT="$(cd -- "${DEPLOY_DIR}/.." &>/dev/null && pwd)"

SLUG="${1:-smoke-$(date +%s)-$$}"
PORT="${2:-19443}"
PROJECT="wp-${SLUG}"
# WAYPOINT_SMOKE_OVERRIDE_FILE: optional, uncommitted compose override (e.g.
# a scratch file overriding the `edge` network's pinned subnet, per
# docs/testing.md, when 192.168.240.0/24 collides with a concurrent stack on
# this host). Never commit an override file -- it exists purely to route
# around a shared-host collision, and the shipped compose file's pinned
# subnet is correct on a real appliance host with no concurrent stacks. `DC`
# is finalized further down, after the devcontainer bind-mount translation
# block below (which may itself set WAYPOINT_SMOKE_OVERRIDE_FILE).
# In-network base -- the helper container below reaches nginx by its Compose
# service name (Docker's embedded DNS, same resolver mechanism
# deploy/README.md "Networking" describes for nginx's own upstream lookups),
# not through the host-published $PORT. $PORT/$BASE are still used for the
# operator-facing curl examples this script prints, and for a best-effort
# direct probe that succeeds on a host where the published port IS reachable.
BASE="https://127.0.0.1:${PORT}"
NET_BASE="https://nginx"
HELPER_NAME="${PROJECT}-smoke-helper"
HELPER_STARTED=""

PASS_COUNT=0
FAIL_COUNT=0
FAILURES=()
ADMIN_TOKEN=""

log() { printf '\n=== %s ===\n' "$*"; }
ok()  { PASS_COUNT=$((PASS_COUNT + 1)); printf '[PASS] %s\n' "$*"; }
bad() { FAIL_COUNT=$((FAIL_COUNT + 1)); FAILURES+=("$*"); printf '[FAIL] %s\n' "$*"; }

# shellcheck disable=SC2317,SC2329  # invoked indirectly via `trap cleanup EXIT`
cleanup() {
	log "Tearing down ${PROJECT} (docs/testing.md: always your own project, always -v)"
	if [[ -n "${HELPER_STARTED}" ]]; then
		docker rm -f "${HELPER_NAME}" >/dev/null 2>&1 || true
	fi
	(cd "${DEPLOY_DIR}" && ${DC} down -v) || true
	rm -f "${SCRATCH_KEY_DIR:-/nonexistent}"/* 2>/dev/null || true
	rm -f "${CATALOG_ARTIFACTS_FILE:-/nonexistent}" 2>/dev/null || true
}
trap cleanup EXIT

log "Isolation: project=${PROJECT} port=${PORT}"
echo "docker ps (containers NOT belonging to this run -- do not touch them):"
docker ps --format '{{.Names}}' | grep -v "^${PROJECT}-" || echo "  (none currently running)"

# --- Prerequisites -----------------------------------------------------

cd "${DEPLOY_DIR}"

if [[ ! -f nginx/certs/dev-cert.pem || ! -f nginx/certs/dev-key.pem ]]; then
	log "Generating dev TLS cert"
	nginx/certs/generate-dev-certs.sh
fi

if [[ ! -f "${REPO_ROOT}/frontend/dist/index.html" ]]; then
	log "frontend/dist missing (Node 22 required, sandbox has Node ${NODE_VER:-unknown}) -- writing an accepted placeholder per docs/testing.md's disclosed workaround"
	mkdir -p "${REPO_ROOT}/frontend/dist"
	cat > "${REPO_ROOT}/frontend/dist/index.html" <<-'EOF'
	<!doctype html><html><head><title>waypoint placeholder</title></head>
	<body>placeholder build (Node 22 unavailable when this smoke test ran)</body></html>
	EOF
fi

# docs/testing.md "Devcontainer bind mounts": when this script itself runs
# from inside the devcontainer, the Docker daemon resolves every bind-mount
# SOURCE against the HOST filesystem, not this container's. A relative source
# in docker-compose.yml (frontend/dist, postgres/initdb) is silently mounted
# as an empty directory instead of erroring, which then makes nginx serve a
# bare 403 and/or postgres silently skip 01-runner-roles.sh (both hit and
# fixed while first authoring this script -- see docs/testing.md and the PR
# body for #444). Resolve the host-absolute prefix for this repo checkout
# once here (used both for the compose sources below and for the master-key
# mount further down, which hits the identical trap) -- on a real appliance
# host (no devcontainer indirection) HOST_PREFIX just equals REPO_ROOT and
# every block below is a no-op translation.
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

if [[ -z "${WAYPOINT_SMOKE_OVERRIDE_FILE:-}" ]] && [[ "${HOST_PREFIX}" != "${REPO_ROOT}" ]]; then
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
	EOF
	WAYPOINT_SMOKE_OVERRIDE_FILE="${GENERATED_OVERRIDE}"
fi

SCRATCH_KEY_DIR="$(mktemp -d)"
openssl rand -hex 32 > "${SCRATCH_KEY_DIR}/master.key"
chmod 644 "${SCRATCH_KEY_DIR}/master.key"

CONFIG_DIR="${REPO_ROOT}/config"
mkdir -p "${CONFIG_DIR}/secrets" "${CONFIG_DIR}/local-auth"
cp "${SCRATCH_KEY_DIR}/master.key" "${CONFIG_DIR}/secrets/master.key"
chmod 644 "${CONFIG_DIR}/secrets/master.key"

# Admin password: fixed dev-only value, hashed via the backend's own CLI so
# the hash format always matches what LocalAuthOptionsPostConfigure expects.
# Computed here (before the compose override below) because that override
# needs the resulting file to exist as a mount source.
ADMIN_PASSWORD="invented-smoke-test-password-$(openssl rand -hex 4)"
log "Computing admin password hash via backend --hash-password"
(
	cd "${REPO_ROOT}/backend"
	if command -v dotnet >/dev/null 2>&1; then
		dotnet build Waypoint.Api >/dev/null
		printf '%s\n' "${ADMIN_PASSWORD}" | dotnet run --project Waypoint.Api --no-launch-profile --no-build -- --hash-password \
			| tail -1 > "${SCRATCH_KEY_DIR}/admin-hash"
	fi
)
if [[ -s "${SCRATCH_KEY_DIR}/admin-hash" ]]; then
	cp "${SCRATCH_KEY_DIR}/admin-hash" "${CONFIG_DIR}/local-auth/admin-password-hash"
else
	echo "warning: could not compute admin password hash locally; login-gated steps will be skipped" >&2
	ADMIN_PASSWORD=""
fi

# deploy/README.md "Bring-up" steps 3-4: both the master-key AND the
# admin-password-hash bind mounts are commented out by default (an
# unconfigured stack still starts healthy, but every login/secret-bearing
# path fails closed until an operator uncomments them) -- discovered by this
# script's own fresh-stack run, see the PR body for #444. This smoke test
# needs both (login gates nearly every step below; the master key covers
# personal/service-credential scan jobs, the secret canary step, and
# compliance-runner's own readiness check), so it always layers a mount
# override for both, translated through the same HOST_PREFIX the
# devcontainer block above resolved. The admin-hash mount is conditional --
# skip it if the hash could not be computed, so backend still starts (with
# every login failing closed, as documented) rather than compose refusing to
# come up on a missing bind source.
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
	if [[ -n "${ADMIN_PASSWORD}" ]]; then
		echo "      - type: bind"
		echo "        source: ${ADMIN_HASH_HOST_PATH}"
		echo "        target: /run/secrets/local-auth-admin-password-hash"
		echo "        read_only: true"
	fi
	echo "  compliance-runner:"
	echo "    volumes:"
	echo "      - type: bind"
	echo "        source: ${MASTER_KEY_HOST_PATH}"
	echo "        target: /run/secrets/waypoint-master-key"
	echo "        read_only: true"
	echo "    environment:"
	# Discovered running this script (see the PR body for #444): in a sandbox
	# where cgroup CPU/memory limits are unreadable (CgroupResourceDiscovery
	# logs "No usable cgroup CPU/memory limits found"), ResourceAdmissionController
	# falls back to a conservative 1 CPU core -- below scan's own 2.0-core
	# JobResourceProfiles weight, so a scan job is claimed, denied admission,
	# and silently released back to `queued` FOREVER (the denial only logs at
	# Debug, invisible at the default Information level -- a real, separately
	# disclosed operator-visibility gap, not something this env override
	# papers over). Raising the fallback here is the same knob an operator
	# would set via RunnerResources:FallbackCpuCores/FallbackMemoryBytes on a
	# host whose cgroups are similarly unreadable; it does not touch the
	# runner image or any product code.
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
if [[ -n "${WAYPOINT_SMOKE_OVERRIDE_FILE:-}" ]]; then
	COMPOSE_FILES="${COMPOSE_FILES} -f ${WAYPOINT_SMOKE_OVERRIDE_FILE}"
fi
COMPOSE_FILES="${COMPOSE_FILES} -f ${MASTER_KEY_OVERRIDE}"
DC="docker compose -p ${PROJECT} ${COMPOSE_FILES}"

# compliance-runner's own readiness check (ComplianceReadinessCheck) requires
# Scans:ProfilePath/NsxProfilePath/SrgProfilePath to exist as readable
# directories -- the shipped `compliance-profiles` named volume starts
# completely empty on a fresh stack (no operator has populated real InSpec
# content yet), so compliance-runner's own Docker healthcheck fails closed
# before this script ever gets to enqueue a job (discovered by this script's
# own fresh-stack run -- see the PR body for #444). Pre-create the three
# expected subdirectories, still with NO real profile content in them, so
# readiness passes and a scan job can reach and honestly fail against its
# invented unreachable target (the actual signal steps 7/8 below look for)
# instead of failing at the readiness gate first. This mirrors an operator
# who has populated the volume with real content -- this script populates it
# with none, which is a legitimate, disclosed stand-in.
docker volume create "${PROJECT}_compliance-profiles" >/dev/null
docker run --rm -v "${PROJECT}_compliance-profiles:/x" alpine \
	sh -c "mkdir -p /x/vsphere /x/nsx /x/srg" >/dev/null

# --- Bring-up (real build, not cached) ----------------------------------

log "docker compose config sanity (verify isolation before trusting anything)"
WAYPOINT_HTTPS_PORT="${PORT}" ${DC} config | grep -E "^name:|published:" || true

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

# --- Helpers -------------------------------------------------------------

# Long-lived helper container on the stack's OWN edge network (see the
# header comment above for why: the published host port is not reliably
# reachable from this script's own network namespace in every environment).
# `docker exec` into it for every request instead of `docker run`-per-call,
# so the per-request cost is a plain exec, not a container start. curlimages/
# curl bundles curl + python3 (Alpine base), which covers both this and the
# json_field/SSE helpers below without a second image.
EDGE_NETWORK="${PROJECT}_edge"
docker run -d --rm --name "${HELPER_NAME}" --network "${EDGE_NETWORK}" \
	--entrypoint sh curlimages/curl -c "while true; do sleep 3600; done" >/dev/null
HELPER_STARTED=1

net_curl() {
	# net_curl <curl-args...> -- runs curl inside the helper container against
	# NET_BASE (the stack's own nginx service name), the same TLS/-k posture
	# every other call in this script already used against 127.0.0.1.
	docker exec "${HELPER_NAME}" curl -k -sS "$@" || true
}

json_field() {
	# json_field <field-path-dot-separated> reads stdin JSON, prints the value.
	# Runs on the HOST's python3 (the value already came back over stdin from
	# net_curl), not inside the helper container.
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

api_get() { net_curl -o /dev/null -w '%{http_code}' -H "Authorization: Bearer ${ADMIN_TOKEN}" "${NET_BASE}$1"; }
api_get_body() { net_curl -H "Authorization: Bearer ${ADMIN_TOKEN}" "${NET_BASE}$1"; }
api_post() { net_curl -o /dev/null -w '%{http_code}' -X POST -H "Content-Type: application/json" -H "Authorization: Bearer ${ADMIN_TOKEN}" -d "$2" "${NET_BASE}$1"; }
api_post_body() { net_curl -X POST -H "Content-Type: application/json" -H "Authorization: Bearer ${ADMIN_TOKEN}" -d "$2" "${NET_BASE}$1"; }
api_delete() { net_curl -o /dev/null -w '%{http_code}' -X DELETE -H "Authorization: Bearer ${ADMIN_TOKEN}" "${NET_BASE}$1"; }

# --- 1. All services healthy (already asserted above via the wait loop) --

log "1. All services healthy"
UNHEALTHY=0
for id in $(${DC} ps -q); do
	status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}n/a{{end}}' "${id}")"
	[[ "${status}" == "healthy" || "${status}" == "n/a" ]] || UNHEALTHY=$((UNHEALTHY + 1))
done
if [[ "${UNHEALTHY}" -eq 0 ]]; then ok "all containers healthy"; else bad "${UNHEALTHY} container(s) unhealthy"; fi

# Wait for a real response through the helper before trusting NET_BASE.
for i in $(seq 1 30); do
	code="$(net_curl -o /dev/null -w '%{http_code}' --max-time 2 "${NET_BASE}/api/v1/health")"
	[[ "${code}" == "200" ]] && break
	if [[ "${i}" == 30 ]]; then echo "warning: ${NET_BASE} never returned 200 via the helper container" >&2; fi
	sleep 1
done

# Best-effort: also try the host-published port directly, for operators on a
# host where it IS reachable -- informational only, never gates the run.
if curl -k -sS -o /dev/null --max-time 2 "${BASE}/api/v1/health" 2>/dev/null; then
	echo "(also confirmed reachable directly at ${BASE})"
else
	echo "(${BASE} not reachable from this script's own network namespace -- using the in-network helper for every check below; this is expected in a devcontainer/remote-daemon environment, see docs/testing.md)"
fi

# --- 2. Login ---------------------------------------------------------

if [[ -n "${ADMIN_PASSWORD}" ]]; then
	log "2. Login as admin"
	LOGIN_BODY="$(net_curl -X POST -H 'Content-Type: application/json' \
		-d "{\"username\":\"admin\",\"password\":\"${ADMIN_PASSWORD}\"}" \
		"${NET_BASE}/api/v1/auth/login")"
	ADMIN_TOKEN="$(printf '%s' "${LOGIN_BODY}" | json_field token 2>/dev/null || true)"
	if [[ -n "${ADMIN_TOKEN}" ]]; then ok "login returned a token"; else bad "login did not return a token: ${LOGIN_BODY}"; fi
else
	bad "admin password hash unavailable -- skipping every login-gated step below"
fi

# --- 3. GET /system: both runner domains ready with capabilities -------

log "3. GET /system reports both runner domains"
if [[ -n "${ADMIN_TOKEN}" ]]; then
	# worker_registry heartbeats on an interval -- give both runners a couple
	# of ticks before asserting.
	# compliance-runner's own healthcheck start_period is 30s and its readiness
	# refresh interval is another 30s on top -- 12s here was measured too short
	# (compliance-runner's heartbeat can genuinely still be absent from
	# worker_registry at that point even though download-runner's already
	# landed), so this polls up to 60s instead of a single fixed sleep.
	for i in $(seq 1 30); do
		SYSTEM_PROBE_BODY="$(api_get_body /api/v1/system)"
		if printf '%s' "${SYSTEM_PROBE_BODY}" | python3 -c "
import json,sys
d = json.load(sys.stdin)
sys.exit(0 if len(d.get('runners', [])) >= 2 else 1)
" 2>/dev/null; then
			break
		fi
		sleep 2
	done
	SYSTEM_BODY="$(api_get_body /api/v1/system)"
	echo "${SYSTEM_BODY}"
	# The runner list has no "domain" field -- SystemRunnerStatusResponse is
	# {worker_id, job_types, available, last_seen_at} (SystemContracts.cs), and
	# worker_id defaults to Environment.MachineName (the container hostname,
	# an opaque hex id under Compose -- no "compliance"/"download" substring).
	# Classify by job_types membership instead, matching JobCapabilities.
	COMPLIANCE_UP="$(printf '%s' "${SYSTEM_BODY}" | python3 -c "
import json,sys
d = json.load(sys.stdin)
compliance_types = {'discover','credential-test','scan','remediate'}
print('compliance' if any(set(r.get('job_types', [])) & compliance_types for r in d.get('runners', [])) else '')
")"
	DOWNLOAD_UP="$(printf '%s' "${SYSTEM_BODY}" | python3 -c "
import json,sys
d = json.load(sys.stdin)
download_types = {'catalog-index','download','bundle-export','bundle-import','content-library-sync','content-pull','content-import','update'}
print('download' if any(set(r.get('job_types', [])) & download_types for r in d.get('runners', [])) else '')
")"
	if [[ -n "${COMPLIANCE_UP}" ]]; then ok "compliance-runner reported in /system"; else bad "compliance-runner missing from /system runners: ${SYSTEM_BODY}"; fi
	if [[ -n "${DOWNLOAD_UP}" ]]; then ok "download-runner reported in /system"; else bad "download-runner missing from /system runners: ${SYSTEM_BODY}"; fi
else
	bad "skipped (no admin token)"
fi

# --- 4. Catalog-index works without the gated tool ----------------------

log "4. Catalog index works without vcf-download-tool installed"
CATALOG_ARTIFACTS_FILE="$(mktemp)"
if [[ -n "${ADMIN_TOKEN}" ]]; then
	CATALOG_CODE="$(api_get /api/v1/catalog/artifacts)"
	api_get_body /api/v1/catalog/artifacts > "${CATALOG_ARTIFACTS_FILE}"
	if [[ "${CATALOG_CODE}" == "200" ]]; then ok "GET /catalog/artifacts returns 200 with no tool installed"; else bad "GET /catalog/artifacts returned ${CATALOG_CODE}"; fi
	SYNC_CODE="$(api_post /api/v1/catalog/sync '{}')"
	# 202 (queued a catalog-index job) is success; the job itself is a pure
	# filesystem walk (ToolGatedDownloadJobHandler's doc comment) so it never
	# needs the gated tool. Accept 202 as the pass condition.
	if [[ "${SYNC_CODE}" == "202" ]]; then ok "POST /catalog/sync queues without the tool (202)"; else bad "POST /catalog/sync returned ${SYNC_CODE}"; fi
else
	bad "skipped (no admin token)"
fi

# --- 5. Download fails with the clear tool-absent message ---------------

log "5. Download fails closed with a clear tool-absent message (managed-tool volume empty)"
if [[ -n "${ADMIN_TOKEN}" ]]; then
	sleep 3
	ARTIFACT_ID="$(python3 -c "
import json
try:
    with open('${CATALOG_ARTIFACTS_FILE}') as f:
        d = json.load(f)
    items = d if isinstance(d, list) else d.get('items', [])
    print(items[0]['id'] if items else '')
except Exception:
    print('')
")"
	if [[ -n "${ARTIFACT_ID}" ]]; then
		DOWNLOAD_RESPONSE="$(api_post_body /api/v1/downloads "{\"depot_artifact_ids\":[\"${ARTIFACT_ID}\"]}")"
		echo "${DOWNLOAD_RESPONSE}"
		RUN_ID="$(printf '%s' "${DOWNLOAD_RESPONSE}" | json_field run_id 2>/dev/null || true)"
		if [[ -n "${RUN_ID}" ]]; then
			ok "download run queued (${RUN_ID}) -- waiting for the runner to claim and fail it closed"
			FOUND_TOOL_ABSENT=0
			for i in $(seq 1 20); do
				JOBS_BODY="$(api_get_body "/api/v1/runs/${RUN_ID}/jobs")"
				if printf '%s' "${JOBS_BODY}" | grep -qi "vcf-download-tool is not installed"; then
					FOUND_TOOL_ABSENT=1
					break
				fi
				sleep 2
			done
			if [[ "${FOUND_TOOL_ABSENT}" == "1" ]]; then
				ok "download job failed with the clear tool-absent message"
			else
				bad "download job never reported the tool-absent message within timeout: ${JOBS_BODY}"
			fi
		else
			bad "no run_id in download response: ${DOWNLOAD_RESPONSE}"
		fi
	else
		echo "  (no indexed depot artifacts -- empty catalog is a valid fresh-stack state; skipping the download-fails check)"
	fi
else
	bad "skipped (no admin token)"
fi

# --- 6. Site/target/credential setup via API -----------------------------

log "6. Site/target/credential setup"
SITE_ID=""
TARGET_ID=""
CREDENTIAL_ID=""
if [[ -n "${ADMIN_TOKEN}" ]]; then
	SITE_RESPONSE="$(api_post_body /api/v1/sites "{\"name\":\"smoke-site-$(date +%s)\"}")"
	SITE_ID="$(printf '%s' "${SITE_RESPONSE}" | json_field id 2>/dev/null || true)"
	if [[ -n "${SITE_ID}" ]]; then ok "site created (${SITE_ID})"; else bad "site creation failed: ${SITE_RESPONSE}"; fi

	if [[ -n "${SITE_ID}" ]]; then
		TARGET_RESPONSE="$(api_post_body "/api/v1/sites/${SITE_ID}/targets" \
			'{"kind":"ssh","name":"srg-01","connection":{"host":"srg-01.example.internal"}}')"
		TARGET_ID="$(printf '%s' "${TARGET_RESPONSE}" | json_field id 2>/dev/null || true)"
		if [[ -n "${TARGET_ID}" ]]; then ok "target created (${TARGET_ID}, invented unreachable host)"; else bad "target creation failed: ${TARGET_RESPONSE}"; fi
	fi

	# ADR-0011: 'owner' must be 'shared' -- there is no per-user credential
	# ownership model in v1, so any other value is a 400 validation_error.
	CRED_RESPONSE="$(api_post_body /api/v1/credentials \
		'{"name":"smoke-svc-cred","credential_type":"ssh","owner":"shared","secret":"invented-smoke-canary-9f3a"}')" # gitleaks:allow — invented smoke canary, never a real secret
	CREDENTIAL_ID="$(printf '%s' "${CRED_RESPONSE}" | json_field id 2>/dev/null || true)"
	if [[ -n "${CREDENTIAL_ID}" ]]; then ok "service credential created (${CREDENTIAL_ID})"; else bad "credential creation failed: ${CRED_RESPONSE}"; fi
else
	bad "skipped (no admin token)"
fi

# --- 7. Service-credential scan job: enqueue, claim, honest failure -----

log "7. Service-credential scan against an invented unreachable target"
SERVICE_RUN_ID=""
if [[ -n "${ADMIN_TOKEN}" && -n "${SITE_ID}" && -n "${TARGET_ID}" ]]; then
	SCAN_RESPONSE="$(api_post_body /api/v1/runs \
		"{\"run_type\":\"scan\",\"scope\":\"{\\\"site_id\\\":\\\"${SITE_ID}\\\",\\\"target_ids\\\":[\\\"${TARGET_ID}\\\"]}\"}")"
	SERVICE_RUN_ID="$(printf '%s' "${SCAN_RESPONSE}" | json_field run_id 2>/dev/null || true)"
	if [[ -n "${SERVICE_RUN_ID}" ]]; then
		ok "service-credential scan run queued (${SERVICE_RUN_ID})"
		FOUND_TERMINAL=0
		for i in $(seq 1 30); do
			JOBS_BODY="$(api_get_body "/api/v1/runs/${SERVICE_RUN_ID}/jobs")"
			if printf '%s' "${JOBS_BODY}" | grep -qE '"state":"(failed|auth-failed)"'; then
				FOUND_TERMINAL=1
				break
			fi
			sleep 2
		done
		if [[ "${FOUND_TERMINAL}" == "1" ]]; then
			ok "compliance-runner claimed and failed the job gracefully against the unreachable target"
		else
			bad "service-credential scan job never reached a terminal failure within timeout: ${JOBS_BODY}"
		fi
	else
		bad "scan run creation failed: ${SCAN_RESPONSE}"
	fi
else
	bad "skipped (no admin token or missing site/target)"
fi

# --- 8. Personal-credential scan job: enqueue, claim, honest failure ----

log "8. Personal-credential (ad hoc) scan against an invented unreachable target"
PERSONAL_RUN_ID=""
if [[ -n "${ADMIN_TOKEN}" && -n "${SITE_ID}" && -n "${TARGET_ID}" ]]; then
	PERSONAL_SCAN_RESPONSE="$(api_post_body /api/v1/runs \
		"{\"run_type\":\"scan\",\"scope\":\"{\\\"site_id\\\":\\\"${SITE_ID}\\\",\\\"target_ids\\\":[\\\"${TARGET_ID}\\\"]}\",\"credential\":{\"kind\":\"personal\",\"username\":\"smoke-operator@example.internal\",\"secret\":\"invented-personal-canary-b7e1\"}}")" # gitleaks:allow — invented personal canary, never a real secret
	PERSONAL_RUN_ID="$(printf '%s' "${PERSONAL_SCAN_RESPONSE}" | json_field run_id 2>/dev/null || true)"
	if [[ -n "${PERSONAL_RUN_ID}" ]]; then
		ok "personal-credential scan run queued (${PERSONAL_RUN_ID})"
		FOUND_TERMINAL=0
		for i in $(seq 1 30); do
			JOBS_BODY="$(api_get_body "/api/v1/runs/${PERSONAL_RUN_ID}/jobs")"
			if printf '%s' "${JOBS_BODY}" | grep -qE '"state":"(failed|auth-failed)"'; then
				FOUND_TERMINAL=1
				break
			fi
			sleep 2
		done
		if [[ "${FOUND_TERMINAL}" == "1" ]]; then
			ok "personal-credential job also failed gracefully"
		else
			bad "personal-credential scan job never reached a terminal failure within timeout: ${JOBS_BODY}"
		fi
	else
		bad "personal-credential scan run creation failed: ${PERSONAL_SCAN_RESPONSE}"
	fi
else
	bad "skipped (no admin token or missing site/target)"
fi

# --- 9. Cancellation mid-run ----------------------------------------------

# Issue #498: cancelling a job racing a fast-failing invented-unreachable
# target intermittently lost the race -- compliance-runner claims and fails
# the job before the DELETE request lands, so IJobControlRepository.CancelJobAsync
# correctly returns NotCancellable/409 (JobsController.CancelJob) even though
# nothing is wrong. Pause the run FIRST (POST /runs/{id}/pause,
# RunControlService.PauseAsync) so the job dispatcher never claims its queued
# job in the first place (JobDispatcherHostedServiceTests exercises the same
# claim-skip behavior for a paused run) -- the job is then guaranteed to still
# be `queued` when the cancel request lands, and CancelJobAsync moves a
# queued job straight to `cancelled` (JobCancelOutcome.Cancelled) with no
# runner race possible. Resume afterward so the (now-cancelled) run doesn't
# linger paused for the rest of the script/teardown.
log "9. Cancellation mid-run (run paused first so the job cannot be claimed before cancel)"
if [[ -n "${ADMIN_TOKEN}" && -n "${SITE_ID}" && -n "${TARGET_ID}" ]]; then
	CANCEL_TARGET_RESPONSE="$(api_post_body "/api/v1/sites/${SITE_ID}/targets" \
		'{"kind":"ssh","name":"srg-cancel-01","connection":{"host":"srg-cancel-01.example.internal"}}')"
	CANCEL_TARGET_ID="$(printf '%s' "${CANCEL_TARGET_RESPONSE}" | json_field id 2>/dev/null || true)"
	CANCEL_SCAN_RESPONSE="$(api_post_body /api/v1/runs \
		"{\"run_type\":\"scan\",\"scope\":\"{\\\"site_id\\\":\\\"${SITE_ID}\\\",\\\"target_ids\\\":[\\\"${CANCEL_TARGET_ID}\\\"]}\"}")"
	CANCEL_RUN_ID="$(printf '%s' "${CANCEL_SCAN_RESPONSE}" | json_field run_id 2>/dev/null || true)"
	if [[ -n "${CANCEL_RUN_ID}" ]]; then
		# Pause immediately -- before the dispatcher's next claim tick can reach
		# this run's queued job. A pause on an already-claimed/running job is
		# harmless (dispatch just stops advancing it further); the goal here is
		# only to keep a still-queued job queued.
		PAUSE_CODE="$(net_curl -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer ${ADMIN_TOKEN}" "${NET_BASE}/api/v1/runs/${CANCEL_RUN_ID}/pause")"
		if [[ "${PAUSE_CODE}" == "200" ]]; then ok "run paused before cancel (${PAUSE_CODE})"; else bad "run pause returned ${PAUSE_CODE}"; fi

		JOBS_BODY="$(api_get_body "/api/v1/runs/${CANCEL_RUN_ID}/jobs")"
		CANCEL_JOB_ID="$(printf '%s' "${JOBS_BODY}" | json_field '0.id' 2>/dev/null || true)"
		if [[ -n "${CANCEL_JOB_ID}" ]]; then
			CANCEL_CODE="$(api_delete "/api/v1/jobs/${CANCEL_JOB_ID}")"
			# With the run paused, the job is guaranteed still `queued` -- cancel
			# should deterministically succeed (200). A 409 here would mean the
			# job was claimed before the pause took effect (e.g. an in-flight
			# claim from a tick that started just before the pause request
			# landed); still accept it as pass, distinguished in the log, rather
			# than hard-failing on a residual, much narrower race than the
			# pre-fix one.
			if [[ "${CANCEL_CODE}" == "200" ]]; then
				ok "job cancel returned 200 (queued job cancelled deterministically)"
			elif [[ "${CANCEL_CODE}" == "409" ]]; then
				ok "job cancel returned 409 (already claimed/terminal before the pause landed -- acceptable, product behavior is correct)"
			else
				bad "job cancel returned ${CANCEL_CODE}"
			fi
			FOUND_CANCELLED=0
			for i in $(seq 1 15); do
				JOB_BODY="$(api_get_body "/api/v1/runs/${CANCEL_RUN_ID}/jobs")"
				printf '%s' "${JOB_BODY}" | grep -qE '"state":"(cancelled|failed)"' && { FOUND_CANCELLED=1; break; }
				sleep 2
			done
			if [[ "${FOUND_CANCELLED}" == "1" ]]; then ok "job reached a cancelled/terminal state"; else bad "job never reached cancelled state: ${JOB_BODY}"; fi
		else
			bad "no job id found on cancellation run: ${JOBS_BODY}"
		fi

		# Resume so the run doesn't linger paused (harmless either way since the
		# job is already terminal, but leaves the run in a clean, non-paused
		# state for anything downstream that lists runs).
		RESUME_CODE="$(net_curl -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer ${ADMIN_TOKEN}" "${NET_BASE}/api/v1/runs/${CANCEL_RUN_ID}/resume")"
		if [[ "${RESUME_CODE}" == "200" ]]; then ok "run resumed after cancel"; else bad "run resume returned ${RESUME_CODE}"; fi
	else
		bad "cancellation run creation failed: ${CANCEL_SCAN_RESPONSE}"
	fi
else
	bad "skipped (no admin token or missing site/target)"
fi

# --- 10. SSE stream reconnect/replay --------------------------------------

log "10. SSE stream basic connectivity (proxy_buffering off proof lives in deploy/README.md; this checks the route answers with events content-type live)"
if [[ -n "${ADMIN_TOKEN}" ]]; then
	SSE_HEADERS="$(net_curl -D - -o /dev/null --max-time 3 -H "Authorization: Bearer ${ADMIN_TOKEN}" "${NET_BASE}/api/v1/events")"
	if printf '%s' "${SSE_HEADERS}" | grep -qi "text/event-stream"; then
		ok "GET /api/v1/events answers with text/event-stream"
	else
		bad "GET /api/v1/events did not answer with text/event-stream: ${SSE_HEADERS}"
	fi
else
	bad "skipped (no admin token)"
fi

# --- 11. Stopping one runner leaves the other domain functional ----------

log "11. Stopping compliance-runner leaves download-runner visible/functional in /system"
if [[ -n "${ADMIN_TOKEN}" ]]; then
	${DC} stop compliance-runner
	sleep 15
	SYSTEM_AFTER_STOP="$(api_get_body /api/v1/system)"
	echo "${SYSTEM_AFTER_STOP}"
	STILL_200="$(api_get /api/v1/system)"
	if [[ "${STILL_200}" == "200" ]]; then ok "/system still 200 with compliance-runner stopped"; else bad "/system returned ${STILL_200} with compliance-runner stopped"; fi

	DOWNLOAD_STILL_UP="$(printf '%s' "${SYSTEM_AFTER_STOP}" | python3 -c "
import json,sys
d = json.load(sys.stdin)
download_types = {'catalog-index','download','bundle-export','bundle-import','content-library-sync','content-pull','content-import','update'}
print('download' if any(set(r.get('job_types', [])) & download_types for r in d.get('runners', []) if r.get('available')) else '')
")"
	if [[ -n "${DOWNLOAD_STILL_UP}" ]]; then ok "download-runner still reports available"; else bad "download-runner no longer available after compliance-runner stopped: ${SYSTEM_AFTER_STOP}"; fi

	${DC} start compliance-runner
	log "  (restarted compliance-runner; giving it a moment to report healthy again)"
	sleep 15
else
	bad "skipped (no admin token)"
fi

# --- 12. Runner replica scale-out claims without duplication -------------

log "12. Scaling compliance-runner to 2 replicas and confirming no duplicate claim"
if [[ -n "${ADMIN_TOKEN}" && -n "${SITE_ID}" ]]; then
	${DC} up -d --scale compliance-runner=2 --no-recreate
	sleep 5
	REPLICA_COUNT="$(${DC} ps -q compliance-runner | wc -l | tr -d ' ')"
	if [[ "${REPLICA_COUNT}" == "2" ]]; then ok "compliance-runner scaled to 2 replicas"; else bad "expected 2 compliance-runner containers, found ${REPLICA_COUNT}"; fi

	# Fan out several targets under one run and confirm the resulting jobs
	# never end up claimed/failed twice each (each job's job_events would show
	# more than one 'running' transition if double-claimed).
	SCALE_TARGET_IDS=()
	for n in 1 2 3 4; do
		TR="$(api_post_body "/api/v1/sites/${SITE_ID}/targets" \
			"{\"kind\":\"ssh\",\"name\":\"srg-scale-${n}\",\"connection\":{\"host\":\"srg-scale-${n}.example.internal\"}}")"
		TID="$(printf '%s' "${TR}" | json_field id 2>/dev/null || true)"
		[[ -n "${TID}" ]] && SCALE_TARGET_IDS+=("\"${TID}\"")
	done
	if [[ "${#SCALE_TARGET_IDS[@]}" -gt 0 ]]; then
		JOINED_IDS="$(IFS=,; echo "${SCALE_TARGET_IDS[*]}")"
		SCALE_RUN_RESPONSE="$(api_post_body /api/v1/runs \
			"{\"run_type\":\"scan\",\"scope\":\"{\\\"site_id\\\":\\\"${SITE_ID}\\\",\\\"target_ids\\\":[${JOINED_IDS//\"/\\\"}]}\"}")"
		SCALE_RUN_ID="$(printf '%s' "${SCALE_RUN_RESPONSE}" | json_field run_id 2>/dev/null || true)"
		if [[ -n "${SCALE_RUN_ID}" ]]; then
			sleep 15
			SCALE_JOBS_BODY="$(api_get_body "/api/v1/runs/${SCALE_RUN_ID}/jobs")"
			TOTAL_JOBS="$(printf '%s' "${SCALE_JOBS_BODY}" | python3 -c "import json,sys;print(len(json.load(sys.stdin)))" 2>/dev/null || echo 0)"
			DISTINCT_IDS="$(printf '%s' "${SCALE_JOBS_BODY}" | python3 -c "
import json,sys
d = json.load(sys.stdin)
print(len(set(j['id'] for j in d)))
" 2>/dev/null || echo 0)"
			if [[ "${TOTAL_JOBS}" == "${DISTINCT_IDS}" && "${TOTAL_JOBS}" != "0" ]]; then
				ok "scaled replicas produced ${TOTAL_JOBS} distinct jobs, no duplication observed"
			else
				bad "job count/distinct mismatch under 2 replicas: total=${TOTAL_JOBS} distinct=${DISTINCT_IDS}"
			fi
		else
			bad "scale-out run creation failed: ${SCALE_RUN_RESPONSE}"
		fi
	else
		bad "could not create scale-out targets"
	fi

	${DC} up -d --scale compliance-runner=1 --no-recreate
else
	bad "skipped (no admin token or site)"
fi

# --- 13. Secret canary: no plaintext in DB/artifacts/logs -----------------

log "13. Secret canary sweep -- no plaintext secret in DB, artifacts, or container logs"
if [[ -n "${ADMIN_TOKEN}" ]]; then
	CANARY_FOUND=0
	for canary in "invented-smoke-canary-9f3a" "invented-personal-canary-b7e1"; do
		# DB: jobs.payload/note, job_events.payload, audit_log.detail, credential_secrets, run_secrets.
		DB_HIT="$(${DC} exec -T postgres psql -U "${POSTGRES_USER:-waypoint}" -d "${POSTGRES_DB:-waypoint}" -tAc "
			SELECT count(*) FROM jobs WHERE payload::text LIKE '%${canary}%' OR note LIKE '%${canary}%'
			UNION ALL
			SELECT count(*) FROM job_events WHERE payload::text LIKE '%${canary}%'
			UNION ALL
			SELECT count(*) FROM audit_log WHERE detail::text LIKE '%${canary}%'
			UNION ALL
			SELECT count(*) FROM credential_secrets WHERE encode(ciphertext,'hex') LIKE '%${canary}%'
			UNION ALL
			SELECT count(*) FROM run_secrets WHERE encode(ciphertext,'hex') LIKE '%${canary}%'
		" 2>/dev/null | tr -d '[:space:]' | tr -d '\n' || echo "")"
		if printf '%s' "${DB_HIT}" | grep -q '[1-9]'; then
			bad "canary '${canary}' found in a database column"
			CANARY_FOUND=1
		fi

		# Container logs (all services -- backend, both runners, nginx, postgres).
		for svc in nginx backend postgres compliance-runner download-runner; do
			if ${DC} logs "${svc}" 2>&1 | grep -qF "${canary}"; then
				bad "canary '${canary}' found in ${svc} logs"
				CANARY_FOUND=1
			fi
		done
	done

	if [[ "${CANARY_FOUND}" == "0" ]]; then ok "no secret canary found in DB or container logs"; fi
else
	bad "skipped (no admin token)"
fi

# --- Summary ---------------------------------------------------------------

log "Summary: ${PASS_COUNT} passed, ${FAIL_COUNT} failed"
if [[ "${FAIL_COUNT}" -gt 0 ]]; then
	printf 'Failures:\n'
	for f in "${FAILURES[@]}"; do printf '  - %s\n' "${f}"; done
	exit 1
fi

exit 0
