# ADR-0016: Personal credentials persist encrypted, run-scoped, terminal/expiry bounded

Status: Accepted

Supersedes the storage model (not the tier split or the "no personal rows in the
reusable credential store" rule) of
[ADR-0011](0011-credential-tiers.md).

## Context

ADR-0011 chose "personal credentials are not stored in v1" — held in the backend
process's memory for the run's lifetime, never written to Postgres. That design
predates [ADR-0013](0013-control-plane-and-runners.md) and
[ADR-0014](0014-runner-job-ownership.md), which split the single backend process into
an ASP.NET control plane and dedicated, long-lived execution runners that claim jobs
from Postgres and decrypt claimed-job credentials locally. Two consequences follow
directly from that split, neither compatible with a process-memory cache:

1. **No shared memory.** `RunsController` (API process) and `ScanJobHandler` (compliance
   runner process) do not share an address space. A cache that lives only in the API's
   memory is invisible to the runner that actually needs the secret.
2. **API restarts are ordinary, not exceptional.** Between `POST /runs` and a runner's
   claim of the fanned-out job, the API may restart (deploy, crash-restart, orchestrator
   reschedule). A memory-only cache loses the secret across that gap, forcing the caller
   to re-enter their credential for a run that already exists — poor UX for something
   that should be transparent, and actively broken once the API and the execution
   environment are different processes by design.

The catch-up architecture (issue #433) needs a durable handoff between "the user
supplied a credential at run creation" and "the runner that eventually executes the
job decrypts it" — without turning personal credentials into reusable credential
records (that would defeat the "personal, ad hoc, not reusable" property ADR-0011
exists to preserve) or inventing a runner-to-API plaintext-secret transport (ADR-0014
already rejected that shape for claimed-job credentials generally).

## Decision

1. **Personal credentials persist, encrypted, in a dedicated run-scoped table.** One
   `run_secrets` row per run (not per job — every target in a scan run's fan-out shares
   the same ad hoc credential, matching the prior in-memory cache's per-run semantics),
   envelope-encrypted with the same AES-256-GCM primitives as
   [ADR-0005](0005-secrets.md)'s `credential_secrets`. `RunsController` writes it at run
   creation; jobs reference it implicitly through their own `run_id` (a `has_run_secret`
   flag marks which jobs should look for one).
2. **ADR-0011's "no personal rows in the reusable credential store" rule is
   unchanged and reaffirmed.** `run_secrets` is not `credentials`/`credential_secrets`,
   has no CRUD API, and is never surfaced through any `/credentials` endpoint. A personal
   credential still cannot be listed, reused across runs, or referenced by
   `credential_id`.
3. **The responsible runner decrypts locally, at the point of use, exactly as it does
   for a stored credential (ADR-0014 §6)** — audited on every decrypt, registered with
   the in-play redactor, and never single-shot: a retried or lease-recovered job simply
   decrypts again while its run is non-terminal, which is what makes this durable across
   the runner-claim gap ADR-0011's original design could not survive.
4. **The row is terminal- and expiry-bounded, not indefinite.** The backend deletes it
   synchronously, in the same transaction as the state write, the instant its run
   reaches a contract-terminal state (`completed`, `completed_with_failures`,
   `aborted`). A periodic cleanup sweep deletes any row whose bounded expiry window
   has passed regardless — the backstop for a run that never reaches a terminal state
   at all (crashed before dispatch, or a runner that claimed the job and died before any
   terminal write).

## Rationale

- Preserves the actual security property ADR-0011 was protecting — personal
  credentials never become reusable, never appear in the credential store, never
  outlive their run — while dropping the incidental property (memory-only) that a
  single-process deployment happened to make free and a multi-process one cannot.
  "Not reusable" and "not persisted at all" were conflated in the original decision;
  this ADR separates them.
- Matches the trust boundary ADR-0014 already expanded to the compliance runner: a
  runner that is trusted to decrypt a claimed job's *stored* credential is not a new
  category of trust when it also decrypts that job's *ad hoc* one.
- Terminal-triggered deletion (rather than TTL-only) keeps the exposure window as
  close to "one run's actual duration" as the old in-memory single-shot take
  achieved, while the expiry sweep restores the old design's other property — nothing
  lingers forever — for the abandoned-run case a synchronous terminal hook cannot
  reach.

## Consequences

- `run_secrets` and its cleanup sweep are new attack surface next to
  `credential_secrets`'s existing one; compensated by the same envelope encryption,
  fail-closed audit-before-decrypt discipline, and in-play redaction ADR-0005 already
  requires, plus explicit terminal/expiry bounds `credential_secrets` (indefinite,
  operator-deleted) does not need.
- Retry/cancel/lease-recovery of a job that references a run secret must keep working
  while the run is non-terminal — a retried job simply decrypts again; only a run that
  reaches terminal (or whose secret already expired) loses access, which is the
  intended fail-closed behavior, unchanged in spirit from ADR-0011's original
  single-shot cache.
- docs/security.md's credential-tier table and residual-risk list, and
  docs/domain-model.md's Credential section, are updated to describe "encrypted,
  run-scoped, terminal/expiry bounded" rather than "not stored" (issue #434).
