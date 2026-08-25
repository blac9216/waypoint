# ADR-0022: Closed compliance catalog and atomic content lifecycle

Status: Accepted (planned; implementation tracked by epic
[#726](https://github.com/blac9216/waypoint/issues/726))

## Context

The shipped compliance slice indexes mutable profile directories and lets callers
select a profile path. The transitional `vmware-stig-docker` catalog contains richer
product/component behavior, but its paths and orchestration cannot become a Waypoint
runtime contract. Waypoint also needs vendor profile and XCCDF updates to cross an air
gap without changing active scan content before review.

## Decision

### Catalog authority and exact baselines

Waypoint will maintain a versioned execution catalog as reviewed product code in this
public repository. Its closed vocabulary defines supported products and exact product
versions, component selectors, transports, credential purposes, configuration
requirements, priority, output semantics, and qualification. Unknown capabilities or
layouts are quarantined and not executable. Operators cannot upload executable
plugins, scripts, or catalog mappings; support expansion requires an appliance update.
The sibling repository will be neither a runtime nor maintenance dependency.

Each baseline binds one exact product version to one exact immutable profile version.
There are no ranges, nearest-version fallback, or cross-version test equivalence. A
compatible component has at most one active baseline, selected deterministically by
the catalog; scan callers do not select profiles. The normalized initial coverage is
the [parity contract](../compliance-parity.md).

Sibling product-family keys and paths are retained only as source provenance. They
are not product versions, catalog compatibility claims, baseline candidates, or
activation identities, and Waypoint never expands them into exact versions by
inference. Before any component executes, discovery or Admin configuration must
supply an exact product version, the Waypoint-owned catalog must contain an exact
entry for that same version, and an exact approved baseline for that entry must be
active. STIG execution additionally requires the exact approved compatible XCCDF
baseline. A missing, ambiguous, or mismatched exact identity fails closed as
unsupported coverage.

### Additive acquisition and provenance

Content acquisition will be strictly additive. Connected instances synchronize
catalog-compatible vendor profile revisions and import XCCDF from every eligible STIG
Manager; Admins may manually upload XCCDF. Waypoint will not independently download
XCCDF from DISA. A global daily schedule may be overridden per source, and manual sync
remains available. Success stages candidates and raises a review alert; failure raises
a diagnostic alert. Neither can mutate active content, and multiple staged versions
may coexist.

Immutable source observations, artifacts, digests, parse results, and provenance will
be retained. Identical content is deduplicated. Different complete artifacts claiming
the same identity/version create a blocking conflict: an Admin selects one artifact
with a recorded reason, actor, and time. Waypoint never merges fragments or prefers
arrival time.

### Semantic equivalence and control gates

Review and decisions occur per stable logical control. Diffs classify added, removed,
changed, remapped, severity/input/attestation-impacting, metadata-only, and unchanged
controls. Metadata-only automatic approval is allowed only when a versioned algorithm
proves equivalence across the complete execution closure: implementation; mapped
XCCDF rule/check/severity/identifiers; declared and consumed input contracts; shared
libraries, resources, profile files, and dependencies; transport, capability, and
selector assumptions; and every other executable dependency. An incomplete, dynamic,
ambiguous, or unsupported analysis is `unknown` and therefore functionally changed.
The algorithm version and closure evidence are provenance.

Each changed or unknown control requires one Cyber-or-higher approval and normally a
successful isolated candidate execution. Admin-only candidate execution may use any
compatible configured target after explicit confirmation; it is unscheduled,
non-posture evidence and never an official CKL/upload. Evidence has no age expiry and
applies only to the exact control, closure, baseline, and product version. An Admin may
waive the test per control with a reason. Admin inherits approval authority and may
approve and activate the same baseline; approval and activation remain separate audit
events, with no quorum or two-person rule.

### Atomic readiness, activation, and rollback

Control approval never creates a mixed executable baseline. A STIG baseline becomes
active only as one compatible, completely mapped vendor profile + XCCDF pair after all
controls, dependencies, approvals, tests/waivers, integrity checks, and baseline-wide
validation pass. There is no force-activation of an unmatched or partial pair. An SRG
has no XCCDF or CKL and activates only after the equivalent whole-profile/dependency
closure gates pass. Until activation, scans continue using the prior complete baseline.

Only Admin activates. Activation atomically records the profile, optional XCCDF,
mapping set, dependency closure, approval ledger, digests, and provenance. Admin may
atomically reactivate any retained, previously approved complete baseline that remains
integrity- and capability-compatible. Existing plans remain bound to their original
baseline; future dispatches resolve the one active baseline. History and referenced
rollback candidates cannot be overwritten in place.

### Signed-transfer staging

Compliance content will use ADR-0015's planned signed transfer envelope, not an
unrelated zip or folder-copy activation path. Export may include staged/approved
profiles, XCCDF, mappings, compatibility metadata, digests, and provenance, while the
Waypoint-owned executable catalog remains appliance-update content. Import verifies
signature, schema/capability versions, manifest, digests, and provenance before
entering the same candidate/diff/approval lifecycle. Unsupported capability stays
quarantined; import never overwrites history or auto-activates functional change.

## Consequences

- Issues #728–#731 implement catalog, ingestion, XCCDF, and lifecycle persistence;
  #748 integrates the signed transfer path. All behavior in this ADR remains planned
  until those changes land.
- Mutable directory replacement, leaf-name identity, scan-time profile paths, and the
  deprecated fixed-path fallback in #650 cannot survive the migration. #595, #625,
  and #567 remain implementation work under the constraints above.
- Content and appliance updates are distinct. A candidate requiring a newer catalog
  can be transferred and staged, but cannot run until the appliance supplies support.
- Candidate and retained revisions require content-addressed storage and garbage
  collection that preserves plans, evidence, active baselines, and rollback choices.

## Alternatives rejected

- Treat every discovered `inspec.yml` or operator mapping as runnable: execution
  semantics would be ambiguous and operator uploads could introduce code.
- Activate per-control revisions independently: official output could mix baseline
  versions and misrepresent compliance.
- Use newest-arrival, profile hash alone, or version proximity: none proves functional
  equivalence or exact benchmark compatibility.
