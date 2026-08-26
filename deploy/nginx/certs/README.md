# nginx/certs (generated, git-ignored)

Run `./generate-dev-certs.sh` from this directory (or `deploy/nginx/certs/generate-dev-certs.sh`
from anywhere) to produce `tls.crt` and `tls.key` here (renamed from the old
`dev-cert.pem`/`dev-key.pem` in issue #844 — `deploy/nginx/conf.d/default.conf`
now references the generic names, so the same config serves either the
dev-generated pair or an operator's real certificates with no config edit).
Both are git-ignored by the repo root `.gitignore` — `tls.key` via `*.key`,
`tls.crt` via an explicit `deploy/nginx/certs/tls.crt` entry — and must never
be committed.

These are self-signed, `CN=localhost`, dev-only certificates for the compose
stack's nginx TLS listener (ADR-0003). In production, the operator supplies
real certificates from their internal CA — this script has no bearing on
that path.
