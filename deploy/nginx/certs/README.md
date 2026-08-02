# nginx/certs (generated, git-ignored)

Run `./generate-dev-certs.sh` from this directory (or `deploy/nginx/certs/generate-dev-certs.sh`
from anywhere) to produce `dev-cert.pem` and `dev-key.pem` here. Both are
matched by the repo root `.gitignore` (`*.pem` / `*.key`) and must never be
committed.

These are self-signed, `CN=localhost`, dev-only certificates for the compose
stack's nginx TLS listener (ADR-0003). In production, the operator supplies
real certificates from their internal CA — this script has no bearing on
that path.
