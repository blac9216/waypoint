#!/usr/bin/env bash
#
# Issue #847 (epic #841): idempotent production-config initializer.
#
# Creates the six file-backed secrets `deploy/compose.yaml` requires (issue
# #844's Compose `secrets:` block) under `deploy/config/secrets/` (gitignored
# -- see `.gitignore`'s anchored `/deploy/config/` entry), and validates
# operator-provided TLS if it is already present at `deploy/config/tls/`.
#
# NEVER starts containers, NEVER overwrites an existing secret, and NEVER
# prints a secret value to stdout/stderr. Safe to re-run: every secret file
# already present is left byte-for-byte untouched and reported as "reused".
#
# Usage:
#   deploy/scripts/init-config.sh [--config-dir DIR] [--public-url URL]
#
#   --config-dir DIR   Where secrets/TLS live. Default: deploy/config
#                       (relative to this script's own deploy/ directory).
#   --public-url URL   The appliance's WAYPOINT_PUBLIC_URL. When given AND
#                       config-dir/tls/tls.crt already exists, the
#                       certificate's subjectAltName is checked for that
#                       URL's hostname -- a mismatch fails closed (an
#                       operator-supplied cert that does not cover the
#                       configured hostname is a production misconfiguration
#                       worth catching before bring-up, not after).
#
# Requires: openssl. python3 only used for the --public-url hostname parse
# (stdlib urllib, no network access).

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
DEPLOY_DIR="$(cd -- "${SCRIPT_DIR}/.." &>/dev/null && pwd)"

CONFIG_DIR="${DEPLOY_DIR}/config"
PUBLIC_URL=""

usage() {
	sed -n '2,26p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
	case "$1" in
	--config-dir)
		CONFIG_DIR="$2"
		shift 2
		;;
	--public-url)
		PUBLIC_URL="$2"
		shift 2
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

if ! command -v openssl >/dev/null 2>&1; then
	echo "error: openssl is required but was not found on PATH." >&2
	exit 1
fi

SECRETS_DIR="${CONFIG_DIR}/secrets"
mkdir -p "${SECRETS_DIR}"

# The six file-backed secrets deploy/compose.yaml's top-level `secrets:`
# block declares (issue #844). Order matches that block's own listing.
SECRET_NAMES=(
	postgres-owner-password
	postgres-compliance-runner-password
	postgres-download-runner-password
	postgres-keycloak-password
	keycloak-bootstrap-admin-password
	keycloak-backend-client-secret
)

GENERATED=0
REUSED=0
for name in "${SECRET_NAMES[@]}"; do
	target="${SECRETS_DIR}/${name}"
	if [[ -s "${target}" ]]; then
		REUSED=$((REUSED + 1))
		continue
	fi
	# 32 random bytes, hex-encoded -- same shape fresh-stack-smoke-test.sh
	# and e2e-playwright.sh already generate ad hoc; this script is what
	# #847 replaces that duplicated plumbing with.
	umask 077
	tmp="$(mktemp "${SECRETS_DIR}/.${name}.XXXXXX")"
	openssl rand -hex 32 >"${tmp}"
	mv "${tmp}" "${target}"
	# 0644, not 0600: Compose bind-mounts a `file:` secret source verbatim
	# (host uid/mode preserved, no re-materialization -- see compose.yaml's
	# own `secrets:` block comment), and postgres's initdb scripts read
	# their three files as the in-container `postgres` user, neither root
	# nor this script's uid. 0644 is the convention this repo already uses
	# for every mounted secret file.
	chmod 644 "${target}"
	GENERATED=$((GENERATED + 1))
done

echo "Secrets: ${GENERATED} generated, ${REUSED} reused (values never printed) -- ${SECRETS_DIR}"

# --- TLS presence/SAN validation (operator-provided, never generated here) -

TLS_DIR="${CONFIG_DIR}/tls"
TLS_CERT="${TLS_DIR}/tls.crt"
TLS_KEY="${TLS_DIR}/tls.key"

if [[ ! -f "${TLS_CERT}" && ! -f "${TLS_KEY}" ]]; then
	echo "TLS: no certificate at ${TLS_CERT} yet -- required before a real"
	echo "  bring-up (deploy/compose.yaml's nginx service has no self-signed"
	echo "  fallback). Place operator-provided certificate/key material at:"
	echo "    ${TLS_CERT}"
	echo "    ${TLS_KEY}"
	exit 0
fi

if [[ ! -f "${TLS_CERT}" || ! -f "${TLS_KEY}" ]]; then
	echo "error: only one of tls.crt/tls.key is present under ${TLS_DIR} -- both are required." >&2
	exit 1
fi

if ! openssl x509 -in "${TLS_CERT}" -noout -checkend 0 >/dev/null 2>&1; then
	echo "error: ${TLS_CERT} is not a valid, currently-unexpired X.509 certificate." >&2
	exit 1
fi
echo "TLS: ${TLS_CERT} is a valid, unexpired certificate."

SAN="$(openssl x509 -in "${TLS_CERT}" -noout -ext subjectAltName 2>/dev/null | tail -n +2 | tr -d ' ')"
if [[ -z "${SAN}" ]]; then
	echo "warning: ${TLS_CERT} carries no subjectAltName -- most modern clients (including browsers) reject a cert with no SAN regardless of its Common Name." >&2
else
	echo "TLS: subjectAltName = ${SAN}"
fi

if [[ -n "${PUBLIC_URL}" ]]; then
	HOST="$(python3 -c "import sys, urllib.parse as u; h = u.urlparse(sys.argv[1]).hostname; print(h or '')" "${PUBLIC_URL}")"
	if [[ -z "${HOST}" ]]; then
		echo "error: could not parse a hostname out of --public-url '${PUBLIC_URL}'." >&2
		exit 1
	fi
	if [[ -z "${SAN}" ]] || ! printf '%s' "${SAN}" | tr ',' '\n' | grep -qiE "^(DNS|IP Address):${HOST}$"; then
		echo "error: ${TLS_CERT}'s subjectAltName does not cover '${HOST}' (from --public-url ${PUBLIC_URL})." >&2
		echo "  SAN entries found: ${SAN:-<none>}" >&2
		exit 1
	fi
	echo "TLS: subjectAltName covers configured host '${HOST}'."
fi
