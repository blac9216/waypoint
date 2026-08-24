# ADR-0018: Host-derived capacity discovery, a startup admission invariant, and a shared capacity lease pool

Status: Accepted

Supersedes ADR-0014 §5's per-runner admission model (the "discovered cgroup limit,
intersected with an operator cap, enforced independently by each replica's own
in-process `ResourceAdmissionController`" design) for the multi-runner case. ADR-0014's
other decisions (lease ownership, event writes, the shared runner library, credential
decryption, storage least-privilege) are unaffected.

## Context

Issue #555 found two gaps in ADR-0014 §5's admission model:

1. **The 1-CPU/1-GiB conservative fallback undersells real hardware.** It exists for
   when cgroup limits are missing or report "unlimited" — but "unlimited" is also what
   an operator sees on an uncapped Compose deployment (no `deploy.resources.limits`
   configured), which is the documented default topology (ADR-0001). On real appliance
   hardware, that fallback can admit far fewer jobs than the host can actually run.
2. **No startup check ties a runner's effective budget to what it advertises.** A
   runner narrows its `JobHandlerRegistry` to a `JobCapabilities` allowlist (ADR-0014
   §1), but nothing before this ADR verified that every advertised job type could ever
   be admitted against the runner's own effective budget. A misconfigured operator cap
   (or an unmeasured, oversized `JobResourceProfile`) could start a runner that
   permanently denies admission to a job type it claims to serve — visible only later,
   as a starvation warning (issue #467) an operator has to notice and diagnose.

A third question — what happens when multiple runner replicas of the same type compete
for the same host's spare capacity — remained genuinely undecided. Two designs were
weighed (see the ruling comment on issue #555, 2026-08-23):

- **Option A — static partitions:** give each Compose service an explicit
  `deploy.resources.limits` share of the host; each runner's existing
  `ResourceAdmissionController` continues to admit only within its own static
  partition. Simple, deterministic, no new failure modes — but an idle
  download-runner's headroom can never help a busy compliance-runner, so real appliance
  capacity sits partitioned even when only one domain is under load.
- **Option B — shared DB-coordinated capacity lease pool:** runners atomically claim
  weighted CPU/memory slots from a Postgres-coordinated pool before executing,
  heartbeat their leases, and release/reap on completion or worker loss. Uses real
  appliance capacity dynamically across domains — at the cost of new
  fairness/starvation/lease-recovery machinery and a database-availability dependency
  sitting directly in the admission path.

The owner ruled Option B (2026-08-23 comment on issue #555) and split delivery into two
review-sized issues: this issue (#555) covers host-capacity discovery and the startup
admission invariant that make a single runner's advertised budget honest; the follow-up
(#569) implements the lease pool itself.

## Decision

1. **Host-derived capacity replaces the 1-CPU/1-GiB fallback when cgroup limits are
   unlimited.** `CgroupResourceDiscovery` still prefers an explicit, finite cgroup v2 or
   v1 limit when present — an explicit container limit remains authoritative, never
   overridden by host-derived numbers. Only when cgroup data is missing, unreadable, or
   reports "unlimited" does discovery fall back to the host's own CPU availability
   (`Environment.ProcessorCount`, intersected with process CPU affinity when the OS
   exposes one) and host physical memory, rather than the old fixed conservative
   constants. An explicit operator cap (`RunnerResources:MaxCpuCores`/
   `MaxMemoryBytes`) still intersects on top of whichever source produced the
   discovered numbers, per ADR-0014 §5's existing `min(discovered, cap)` rule. The
   fixed 1-CPU/1-GiB values remain available as `RunnerResourceOptions.FallbackCpuCores`/
   `FallbackMemoryBytes`, used only if host derivation itself cannot produce a usable
   reading.
2. **Every discovered-budget source is named and logged.** `HostResourceLimitSource`
   gains a value distinguishing host-derived capacity from an explicit cgroup limit,
   an operator cap, and the tested-conservative fallback, so the effective-budget log
   line and `RunnerCapacityReport`/`GET /system` always show which of `CgroupV2`/`CgroupV1`,
   `HostDerived`, or `Fallback` produced a runner's numbers — never a number without a
   labeled source.
3. **Startup fails readiness if any advertised job type can never be admitted.** After
   a runner's `JobHandlerRegistry` allowlist and `ResourceAdmissionController` effective
   budget are both known, a startup check evaluates every job type the registry
   advertises against `JobResourceProfiles`. If any type's profile alone exceeds the
   effective CPU or memory budget on that runner, startup fails readiness with a
   diagnostic naming the exact offending job type(s), the profile that does not fit,
   the effective budget, and the specific `RunnerResources__*` configuration keys an
   operator can raise to fix it. A runner must never start and run permanently starved
   for a job type it claims to serve.
4. **The multi-runner capacity question is deferred to #569, not resolved here.** This
   ADR records the Option B ruling as the accepted direction so ADR-0014 §5 is not left
   describing a superseded model in the interim, but the lease pool's schema, atomic
   claim/heartbeat/release protocol, reaping of lost workers, admission-controller
   integration, and fairness/starvation handling are #569's decision and delivery, not
   this one's. Until #569 lands, each runner replica's admission remains governed by
   its own discovered-and-capped budget exactly as ADR-0014 §5 describes — this ADR
   changes how that budget is discovered and validated, not who enforces it.
5. **When #569 lands, a runner's discovered/capped budget remains the authoritative
   upper bound it may ever contribute to or claim from the shared pool.** The lease
   pool coordinates sharing spare capacity across runners; it does not let any runner
   claim more than what this ADR's discovery (intersected with its own operator cap)
   established that runner actually has.

## Rationale

- Host-derived capacity and the startup invariant are correct regardless of which
  multi-runner design was chosen — both single-runner gaps existed independently of the
  Option A/B question, which is why issue #555 asked for a ruling only on the
  multi-runner acceptance criterion and proceeded on these two without one.
- Naming the discovered source explicitly (rather than folding host-derived numbers
  silently into the existing `Fallback` label) keeps ADR-0014 §5's "always logged,
  never silent" guarantee accurate: an operator reading `GET /system` must be able to
  tell "this budget came from the host, not a container limit" apart from "this budget
  came from the tested conservative constant because nothing else was readable."
  Collapsing those into one label would make a legitimate, larger host-derived budget
  look like the degraded case it explicitly is not.
- A startup-time invariant catches a starvation misconfiguration before a job is ever
  silently queued and denied; issue #467's runtime starvation reporting remains the
  correct signal for genuine transient contention, but a permanent, advertised-yet-
  unadmittable job type is a configuration error the runner should refuse to start
  with, not a condition an operator has to notice in logs after the fact.
- Recording the Option B ruling in this ADR (rather than waiting for #569 to write the
  first ADR that mentions it) keeps ADR-0014 §5 from reading as still-current after the
  owner explicitly decided to move away from purely independent per-replica admission.

## Consequences

- `HostResourceLimits`/`HostResourceLimitSource` and `RunnerCapacityReport`'s `Source`
  vocabulary gain new values; anything pattern-matching on the old two-value set
  (`CgroupV2`/`CgroupV1` vs `Fallback`) must be updated to treat the new host-derived
  and explicit-cap sources as "not a bare fallback" for any "is this the degraded case"
  check.
- The Compose dev-only `RunnerResources__FallbackCpuCores`/`FallbackMemoryBytes`
  overrides (issue #561) remain valid — they still govern the tested-conservative
  fallback path — but host derivation may now make an explicit override unnecessary on
  a host where cgroup limits are absent, since discovery derives a comparable-or-better
  number automatically.
- #569's lease-pool design must treat this ADR's per-runner discovered/capped budget as
  a hard input, not a value it renegotiates — the pool changes *sharing*, not *how much
  any one runner has*.
- A runner whose startup now fails the admission invariant is a behavior change:
  previously an oversized `JobResourceProfile` against an under-provisioned cap would
  start and only reveal itself through issue #467's rate-limited runtime warning.
  Operators relying on that runtime-only signal must instead fix the cap or profile
  before the runner starts.
