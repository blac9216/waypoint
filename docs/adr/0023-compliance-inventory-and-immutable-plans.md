# ADR-0023: Stable compliance inventory and immutable component plans

Status: Accepted (planned; implementation tracked by epic
[#726](https://github.com/blac9216/waypoint/issues/726))

## Context

The shipped M2 scan slice treats configured top-level targets as jobs, caches only
vSphere cluster/host/VM rows, and accepts a caller-selected profile. The parity model
in ADR-0022 instead requires exact product-version catalog matches and exact active
baselines for concrete components. Selection must survive refresh without relying on
names, while partial discovery and temporary absence must never be reported as full
coverage. Plans must also remain reproducible after content, configuration, or
inventory changes.

## Decision

### Identity and provenance

Configured top-level targets are connection and policy boundaries. Stable components
are the executable subjects beneath them. Component identity combines the parent
target, catalog component key, and authoritative vendor object identity. For a
catalog-declared service with no independent upstream object, parent identity plus
catalog component key is authoritative. Names, addresses, paths, tree positions, and
sibling family keys never establish sameness.

Discovery and Admin configuration supply catalog-declared facts as independent,
timestamped provenance. Exact product version is mandatory. Waypoint records missing
or conflicting facts and fails closed; it never guesses a winner or expands a family
key. Maintenance mode is informational and does not exclude otherwise selected work.

### Refresh and lifecycle

Recurring inventory discovery inherits one Admin-configured appliance schedule,
initially daily; a per-top-level-target Admin override wins. Manual refresh remains
available. Every scan performs a mandatory pre-scan refresh as a planning barrier,
independent of cache age and recurring timing.

Refresh is assessed per source boundary. A successful complete boundary reconciles
observations. A failed or partial boundary raises an alert, retains earlier rows only
as unverified cache, and neither claims completeness nor advances absence. On a
successful boundary, an unobserved component becomes `absent` but retains identity and
configuration. Rediscovery reconnects the same vendor identity. Continuous absence
reaching one global Admin setting, initially seven days, becomes `retired` and leaves
normal active selection. Admin may explicitly and auditably purge retired
configuration; historical plan references survive.

### Scope, readiness, and conflicts

Requested scope is immutable and is either top-level `all` expansion or an explicit
stable-component set. Pre-scan refresh resolves every positively validated reachable
component from each successful boundary to a concrete immutable set. `all` includes
newly discovered compatible components on those boundaries; explicit scope never
widens. For either mode, requested identities or boundaries that refresh cannot
validate become explicit coverage omissions. The confirmed independent subset runs,
the run is marked incomplete with a prominent warning, and the planner never fills a
gap from stale cache or presents the smaller resolved set as complete coverage. Scan
initiation fails only when refresh validates no runnable component and therefore no
honest runnable plan can exist; readiness omissions after successful validation can
still produce the honest zero-execution plan described below.

When configured and discovered exact versions conflict, any Cyber-or-higher
interactive initiator chooses one for this run after seeing both provenances; the
choice mutates neither source. A scheduled run cannot choose: it skips that component,
continues independent work, raises/surfaces the conflict, remains enabled, and
re-evaluates next dispatch. Schedules never float to another product/profile.
They do resolve the currently active exact baseline for that same exact product version
at each dispatch, so later activation affects a new scheduled plan but not an existing
one.

Each component is ready only with one exact catalog product-version entry and exactly
one active, approved compatible baseline under ADR-0022. Unsupported, conflicted,
unreachable, absent, retired, purged, refresh-unverified, missing-fact, or baseline-
unready items remain explicit coverage omissions; none is silently dropped or
described as successful coverage. Missing facts/baselines skip only the affected
component. An explicit scope containing only unsupported components produces an
honest plan with no executable items rather than widening.

### Immutable plans

After refresh and readiness evaluation, one plan freezes requested and resolved scope,
refresh coverage, component/parent identities, fact provenance/conflicts, exact catalog
and active-baseline identities and digests, complete dependency closure,
selector/transport/priority/output semantics, the exact resolved-configuration
snapshot identity/digest, and references to credential, trust, and capability inputs.
Planned items and coverage omissions are
append-only. Later inventory, content activation, target edits, retirement, or purge
cannot rewrite them. A retry reuses its original item; resolving current state creates
a new run and plan.

ADR-0012 stage markers, ADRs 0013/0014 runner ownership, ADRs 0018/0020 admission, and
ADR-0019 global operational projection remain unchanged. Jobs, attempts, credential
and per-control settings are decided by #807; trust, SSH, evidence, and full legacy
retirement by #808; API/RBAC shapes by #785; roadmap/UI by #786.

### Legacy disposition

The profile picker and `scope.profile_id` are transitional and have no end-state role.
#651 is architecturally superseded by catalog-derived baseline selection. #653 retains
its requirement that invalid schedule dispatch be operator-visible, but validation
becomes immutable scope/readiness resolution rather than legacy profile presence.
#649 remains a distinct unresolved duplicate/concurrent-scan policy and implementation
issue. Immutable plans preserve each run's intent but do not decide duplicate intent,
simultaneous endpoint load, credential lockout/rate-limit risk, or whether overlapping
runs should reject, queue, or warn. Existing issues remain open until their policy or
implementation/UI cleanup is reconciled by the owning work.

## Consequences

- Issues #732–#734 implement component identity, requested/resolved scope, and plan
  compilation. All behavior in this ADR remains planned until those changes land.
- Discovery can add scan-start latency. Partial explicit and `all` scopes run every
  confirmed independent component while prominently reporting incomplete coverage;
  only the absence of any honest runnable refresh result fails initiation.
- Inventory and plan storage grow because identity, provenance, omissions, and
  historical references are retained. Purging live inventory never purges history.
- Callers select assets, not profiles. Exact baselines are deterministic catalog
  results, and profile/version drift requires a new plan.

## Alternatives rejected

- Use the cache after a failed refresh: it cannot prove current existence or complete
  `all` coverage.
- Join by name/address or prefer configured/discovered version: both can silently scan
  the wrong subject or baseline.
- Rewrite plan rows on rediscovery/content change: retries and audit would cease to be
  reproducible.
