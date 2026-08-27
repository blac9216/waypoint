# Rationale Index — deploy/

This file is the evicted "why" for `deploy/`. Per epic #933, deploy/ code
carries only short section markers and terse one-line warnings — no
issue/ADR/history references live in code. When a warning needs a "why", it
points here: `# why: docs/rationale/deploy.md#<kebab-slug>`. This doc is the
one durable home for that provenance.

## Format

- One `##` section per source file (or small grouped directory) under
  `deploy/`.
- Within a section, one `###` entry per kebab-case slug. The slug is the
  exact anchor a code comment points at.
- **Slugs are unique across the whole file, not just within a section.**
  GitHub anchors are file-global: a duplicate `###` heading anywhere in this
  file silently becomes `#slug-1`, and every `# why:` pointer written against
  the second entry then resolves to the *first* one instead — a failure that
  looks correct in review. Disambiguate by prefixing the slug with the
  service/file it belongs to, e.g. `postgres-healthcheck-start-period` and
  `keycloak-healthcheck-start-period`, never a bare `healthcheck-start-period`
  repeated across sections.
- Entry body: 2–6 lines explaining the why (the reasoning, trade-off, or
  constraint — not a restatement of the code).
- Entry ends with a `Refs:` line carrying provenance: issue numbers, ADRs,
  PRs. This is the only place that provenance lives — do not duplicate it
  in code comments or in deploy/ markdown.

### Example entry

A filled entry under a `## compose.yaml` section looks like this:

```markdown
### compose-healthcheck-start-period-30s

The backend's first boot runs pending EF Core migrations before it starts
accepting connections, which can take longer than a typical healthcheck
window under cold cache. A short `start_period` produced flapping
"unhealthy" states during normal first-run migrations, not real failures.

Refs: #000 (invented placeholder — not a real issue)
```

The `compose-` prefix disambiguates this slug from, say, a similarly-named
`postgres-healthcheck-start-period-30s` entry under a different section —
see the file-global uniqueness rule above.

And the matching code comment, in `compose.yaml` itself:

```yaml
# why: docs/rationale/deploy.md#compose-healthcheck-start-period-30s
start_period: 30s
```

---

Add a new entry by appending a `###` slug under the relevant `##` file
section, writing the 2–6 line why, and closing with `Refs:`. Point to it
from code with `# why: docs/rationale/deploy.md#<slug>`. If the source file
has no `##` section yet, create one — append it in `deploy/`-tree order —
rather than leaving the entry homeless. This index is deploy-scoped for now;
a repo-wide pointer-integrity check that verifies every `# why:` comment
resolves to a real anchor is tracked separately (#939) and is not built here.

## README.md

## config.example/

## compose.yaml

## compose.override.example.yaml

## scripts/generate-dev-stack.sh

## scripts/init-config.sh

## scripts/fresh-stack-smoke-test.sh

## scripts/e2e-playwright.sh

## scripts/keycloak-realm-import.sh

## scripts/keycloak-realm-export.sh

## nginx/

## postgres/

## keycloak/

## keycloak-dev-admin/

## dev-bootstrap/
