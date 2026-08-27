#!/usr/bin/env bash
#
# Generates a throwaway self-signed TLS certificate/key pair for the dev
# compose stack's nginx service (ADR-0003: nginx terminates TLS with
# operator-provided certs; in production those come from the operator's
# internal CA — this script exists only to stand up a local dev loop).
#
# Output lands next to this script and is git-ignored (repo root .gitignore
# matches *.key at any depth, plus an explicit deploy/nginx/certs/tls.crt
# entry -- *.pem no longer covers the cert since issue #844 renamed the pair
# to the generic tls.crt/tls.key names deploy/nginx/conf.d/default.conf and
# dev-bootstrap both use). Never commit it, never reuse it outside a
# disposable local dev environment.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
CERT_FILE="${SCRIPT_DIR}/tls.crt"
KEY_FILE="${SCRIPT_DIR}/tls.key"
DAYS="${1:-365}"

if [[ -f "${CERT_FILE}" && -f "${KEY_FILE}" ]]; then
	echo "Dev cert already exists:" >&2
	echo "  ${CERT_FILE}" >&2
	echo "  ${KEY_FILE}" >&2
	echo "Remove both files first if you want to regenerate them." >&2
	exit 0
fi

if ! command -v openssl &>/dev/null; then
	echo "error: openssl is required but was not found on PATH." >&2
	exit 1
fi

openssl req -x509 -nodes -newkey rsa:2048 \
	-keyout "${KEY_FILE}" \
	-out "${CERT_FILE}" \
	-days "${DAYS}" \
	-subj "/C=US/ST=Dev/L=Dev/O=Waypoint Dev/CN=localhost" \
	-addext "subjectAltName=DNS:localhost,IP:127.0.0.1"

chmod 600 "${KEY_FILE}"

echo "Generated a dev-only self-signed certificate (${DAYS} days):"
echo "  ${CERT_FILE}"
echo "  ${KEY_FILE}"
echo
echo "These are git-ignored. Never commit them, and never reuse them"
echo "anywhere other than this disposable local dev stack."
