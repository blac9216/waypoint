# deploy/config.example

Safe, non-secret reference for the layout `deploy/scripts/init-config.sh`
(production) and `deploy/scripts/generate-dev-stack.sh --mode persistent`
(development) create under the real, gitignored `deploy/config/`. Nothing
here is read by `deploy/compose.yaml` or any script — it lets a new operator
or agent see the expected file names and shapes without running a generator
first.

**Every value below is an obviously-invented placeholder.** Never copy one
of these files into `deploy/config/` and use it for anything real — always
generate secrets with `deploy/scripts/init-config.sh` (openssl-random, never
printed) or supply your own operator-issued material.

```
deploy/config.example/
├── secrets/
│   ├── postgres-owner-password
│   ├── postgres-compliance-runner-password
│   ├── postgres-download-runner-password
│   ├── postgres-keycloak-password
│   ├── keycloak-bootstrap-admin-password
│   ├── keycloak-backend-client-secret
│   ├── dev-admin-password              # dev-only Keycloak user
│   └── waypoint-master-key             # OPERATOR-PROVIDED, production only
├── tls/
│   └── tls.crt                         # operator-provided, production only
└── local-auth/
    └── admin-password-hash             # local-auth dev flag only, never production
```

Each `secrets/*` and `local-auth/admin-password-hash` file's entire content
(minus a trailing newline) is the raw value — no quoting, no `key=value`
shape. `tls/tls.key` is deliberately not included here: a private key is
exactly the kind of file that stays out even as an invented placeholder, and
the repo's sanitize scanner refuses to certify any `.key` file clean for the
same reason. Real deployments place their own key alongside `tls.crt`.

The master key is the one entry no generator writes into
`deploy/config/secrets/` — it's operator-supplied material for production
only. Both dev paths (`--mode agent`, `--mode persistent`) provision their
own master key automatically elsewhere (see `deploy/README.md`'s
secrets/config-layout table).

Real generation:

```bash
cd deploy
./scripts/init-config.sh                            # production: the six base secrets
./scripts/generate-dev-stack.sh --mode persistent    # development: the above + dev-admin-password
```

See `deploy/README.md` for the full var-reference and secrets/config-layout
tables, and the three quick-start paths.
