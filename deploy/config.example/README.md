# deploy/config.example

Safe, non-secret reference for the layout `deploy/scripts/init-config.sh`
(production) and `deploy/scripts/generate-dev-stack.sh --mode persistent`
(development) create under the real, gitignored `deploy/config/` (issues
\#844/#845/#847, epic #841). Nothing under this directory is read by
`deploy/compose.yaml` or any script -- it exists purely so a new operator or
agent can see the expected file names and shapes without having to run a
generator first or reverse-engineer them from compose.yaml's comments.

**Every value below is an obviously-invented placeholder. Never copy one of
these files into `deploy/config/` and use it for anything real** -- always
generate real secrets with `deploy/scripts/init-config.sh` (openssl-random,
never printed) or supply your own operator-issued material.

```
deploy/config.example/
├── secrets/
│   ├── postgres-owner-password              # backend's Postgres role
│   ├── postgres-compliance-runner-password  # compliance-runner's least-privilege role
│   ├── postgres-download-runner-password    # download-runner's least-privilege role
│   ├── postgres-keycloak-password           # Keycloak's own Postgres role/database
│   ├── keycloak-bootstrap-admin-password    # Keycloak master-realm bootstrap admin
│   ├── keycloak-backend-client-secret       # waypoint-backend realm client secret
│   ├── dev-admin-password                   # development-only Keycloak user (issue #846)
│   └── master.key                           # AES-256-GCM envelope key (issue #405, ADR-0005)
│                                            # -- OPERATOR-PROVIDED: no generator creates it
├── tls/
│   └── tls.crt                              # operator-provided certificate (production only)
└── local-auth/
    └── admin-password-hash                  # local-auth dev flag only (issue #29/#333) -- never production
```

Each `secrets/*` and `local-auth/admin-password-hash` file's ENTIRE content
(minus a trailing newline) is the raw value -- no quoting, no `key=value`
shape, matching `deploy/compose.yaml`'s own `secrets:` block comment.
`tls/tls.crt` shows the certificate's shape (a public artifact, safe to
publish even faked); `tls/tls.key` is deliberately **not** included here --
a private key is exactly the kind of file CLAUDE.md's "when in doubt, leave
it out" rule targets, even as an obviously-invented placeholder, and the
repo's own sanitize scanner (`.github/sanitize/scan_repo_specific.py`)
refuses to certify any `.key` file clean for the same reason (it cannot
prove a key file carries no embedded secret). Real deployments place their
own key at `deploy/config/tls/tls.key`, alongside `tls.crt`, matching
`deploy/compose.yaml`'s `tls-key-file` anchor.

**The master key is the one entry above that no generator produces.**
`init-config.sh` and `--mode persistent` create the seven `secrets/*`
password files only; `deploy/config/secrets/master.key` is operator-supplied
material, and `deploy/compose.yaml`'s bind for it (`source:
./config/secrets/master.key`, `target: /run/secrets/waypoint-master-key`)
ships commented out until the operator creates the file. The only automatic
master key in this repo is agent-mode's: `--mode agent --slug SLUG` writes a
random one to `deploy/.generated/<slug>/secrets/waypoint-master-key` (named
after its in-container target) and mounts it in the override it generates.
That path is throwaway per-slug state and never appears under
`deploy/config/`.

Real generation:

```bash
cd deploy
./scripts/init-config.sh                    # production: the six base secrets
./scripts/generate-dev-stack.sh --mode persistent   # development: the above + dev-admin-password
```

See `deploy/README.md` "File-backed secrets" and "Bring-up" for the full
manual reference, and `docs/testing.md` for the isolated agent-mode path
(`--mode agent`, everything under `deploy/.generated/<slug>/` instead of
`deploy/config/`).
