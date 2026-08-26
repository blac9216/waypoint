# ADR-0021: Credential-purpose matrix — explicit purposes, not numbered slots

Status: Accepted; §§4–7 target-only defaulting, whole-run missing-binding rejection,
and schedule-carried overrides superseded by
[ADR-0024](0024-compliance-execution-attempts-credentials-and-settings.md)

Part of epic [#582](https://github.com/blac9216/waypoint/issues/582), first sub-issue
[#583](https://github.com/blac9216/waypoint/issues/583). Does not supersede any prior
ADR; it establishes a model that [#584](https://github.com/blac9216/waypoint/issues/584)
(persistence), [#585](https://github.com/blac9216/waypoint/issues/585)/[#586](https://github.com/blac9216/waypoint/issues/586)
(execution resolution, run-scoped secrets), and [#587](https://github.com/blac9216/waypoint/issues/587)
(wizard UI) build on top of.

## Context

`targets.credential_id` and `runs.credential_id` are both single-valued
(`backend/Waypoint.Core/Sites/TargetDtos.cs`, `backend/Waypoint.Core/Secrets/CredentialDtos.cs`).
`RunCreationService.CreateScanRunAsync` resolves `effectiveCredentialId = credentialId ??
target.CredentialId` — one credential per target, full stop
(`backend/Waypoint.Infrastructure/Runs/RunCreationService.cs`).

That model was adequate while every target kind's job handlers needed exactly one
credential. It stopped being adequate the moment `vsphere` targets grew a second,
independent authentication concern:

- **`vsphere-api`** — the vSphere SSO session (`Connect-VIServer` /
  `Connect-StigVIServer` in `runners/compliance-runner/powershell/module.transport.vmware.ps1`).
  Every vCenter/ESXi/VM InSpec scan target uses the `vmware://` train transport
  authenticated with this one credential (`Build-VCenterTarget`, `Build-ESXiTarget`,
  `Build-VMTarget` all attach `$ConnectionResult.VSphereCredential`).
- **`vcsa-ssh`** — VCSA appliance root SSH (`VCSACredential` in the same file), used
  only to scan the VCSA's own OS-level STIG components (`Build-VCSATarget`, `ssh://`
  transport). `Connect-StigVIServer` gained a `-SkipVCSACredential` switch
  (issue [#580](https://github.com/blac9216/waypoint/issues/580), PR
  [#606](https://github.com/blac9216/waypoint/pull/606)) specifically because
  discovery and vSphere API credential-testing were prompting for a VCSA credential
  they never use — proof by production bug that the two purposes are genuinely
  independent, not a single "vsphere credential" that happens to cover two transports.

Two more target kinds each need their own, unrelated credential:

- **`nsx-api`** targets authenticate to NSX Manager's REST API
  (`Get-NsxSessionToken`, `module.transport.nsxapi.ps1`) — a session token/cookie
  pair, not a vSphere or SSH credential.
- **`ssh`** (SRG) targets — Photon, Aria Operations/Automation/Lifecycle, vIDM — scan
  over `ssh://` directly (`Invoke-WaypointSrgScan`, `WaypointScan.psm1`), with an
  optional sudo elevation reusing the same login's password
  (`ScanOptions`/credential `sudo_enabled`, migration 0012-era).

A fifth candidate credential — a VM **guest** login used by the sibling repo's
`guest-ops` SSH-access-toggle provider (`module.sshaccess.providers.ps1`, PowerCLI
`Invoke-VMScript` against an appliance VM) — is deliberately **not** included as a
purpose here: nothing in Waypoint's own `WaypointScan`/`WaypointDiscovery`/
`WaypointCredentialTest` modules calls that provider today. Per issue #583's own
"Risks" section — do not encode a credential requirement the underlying transport does
not actually use — inventing a `vm-guest` purpose ahead of any wired consumer would be
exactly the mistake this ADR exists to avoid. If the SSH-access-toggle flow is ever
imported, it gets its own purpose and its own sub-issue then.

STIG Manager's connection (`sites.stigman_override` / global `stigman_connections`,
`backend/Waypoint.Core/StigManager/StigManagerDtos.cs`) is also out of scope: it is a
site-level OIDC client-credentials connection used only for CKL upload, resolved
independently of any target, and already has its own `token`-type credential slot. It
is not a target-kind × operation binding and this ADR does not touch it.

The system currently has no vocabulary for any of this beyond "the target's one
credential." This ADR defines that vocabulary before #584 touches persistence.

## Decision

### 1. Credential purposes are explicit, named identifiers — never numbered slots

```
vsphere-api   vSphere SSO session (vCenter/ESXi/VM API access via PowerCLI/vmware:// transport)
vcsa-ssh      VCSA appliance root SSH (VCSA OS-level STIG components only)
nsx-api       NSX Manager REST API session
srg-ssh       SRG product SSH login (Photon/Aria Operations/Aria Lifecycle/vIDM), sudo-capable
```

Each purpose is a stable string (the wire/DB value), a superset of nothing else, and
never reused to mean two different things. `vsphere-api` and `vcsa-ssh` are always
distinct purposes, even though both apply to the same `vsphere` target kind and the
same physical vCenter/VCSA pair — this is the acceptance criterion issue #583 leads
with, and it is a direct consequence of `-SkipVCSACredential` already proving the two
are independently satisfiable.

`srg-ssh` is deliberately **not** split further by SRG product (Photon vs. Aria vs.
vIDM): all four authenticate the same way (ssh login, optional sudo), differing only
in `sudo_enabled`/`sudo_requires_password` — a credential-level flag
(`CredentialTypes.Ssh`, `sudo_enabled`), not a distinct transport or purpose. There is
also no separate `esxi-ssh` purpose: nothing in the current transport code opens an
authenticated SSH session directly to an ESXi host for scanning — ESXi hosts are
scanned over `vmware://` via `vsphere-api`, exactly like vCenter and VM targets.

### 2. Credential *types* satisfy purposes — a many-to-one compatibility map

Purposes are what an operation *needs*; credential types
(`Waypoint.Core.Secrets.CredentialTypes`) are what a stored credential *is*. A purpose
is satisfiable only by specific types:

| Purpose | Satisfying credential type(s) | Why |
|---|---|---|
| `vsphere-api` | `vcenter` | vSphere SSO username/password |
| `vcsa-ssh` | `ssh` | VCSA root SSH username/password |
| `nsx-api` | `nsx` | NSX Manager REST username/password |
| `srg-ssh` | `ssh` | SRG product SSH username/password (+ optional sudo) |

`vcsa-ssh` and `srg-ssh` both accept the generic `ssh` credential type — they are
different *purposes* (different targets, different scan contexts) that happen to be
satisfiable by the same credential *type* today. A credential of type `ssh` is not
automatically valid for both purposes at once: compatibility is per-binding (§4), so a
VCSA SSH credential is never silently offered as a default for an unrelated SRG
target's `srg-ssh` binding, and vice versa. `token` and the depot credential types satisfy no
target-operation purpose in this matrix — `token` remains reserved for STIG Manager's
OIDC client secret (out of scope, see Context), and the well-known depot credentials
(`depot-token`, retained as a deprecated legacy alias, plus its issue-#690 successors
`depot-activation-code` and `legacy-download-token`) are already excluded from every
credential picker (issues #571, #690).

### 3. Target kind × operation → required/optional purpose matrix

Operations: **discovery** (`POST /targets/{id}/discover`), **credential-test**
(`POST /credentials/{id}/test`), **scan** (per scan component), and
**remediation-ready planning** (validating a future remediation run has every
credential it would need, without executing one — remediation itself stays
Admin-confirmed and non-schedulable, ADR unaffected).

| Target kind | Operation / component | Required purposes | Optional purposes |
|---|---|---|---|
| `vsphere` | discovery | `vsphere-api` | — |
| `vsphere` | credential-test (vCenter API) | `vsphere-api` | — |
| `vsphere` | credential-test (VCSA SSH) | `vcsa-ssh` | — |
| `vsphere` | scan: vCenter component | `vsphere-api` | — |
| `vsphere` | scan: ESXi component | `vsphere-api` | — |
| `vsphere` | scan: VM component | `vsphere-api` | — |
| `vsphere` | scan: VCSA component(s) | `vsphere-api`, `vcsa-ssh` | — |
| `vsphere` | remediation-ready planning | `vsphere-api` | `vcsa-ssh` (required only if the plan includes a VCSA component) |
| `nsx-api` | discovery | *(not inventory-capable — no discovery operation exists for this kind)* | — |
| `nsx-api` | credential-test | `nsx-api` | — |
| `nsx-api` | scan | `nsx-api` | — |
| `nsx-api` | remediation-ready planning | `nsx-api` | — |
| `ssh` (SRG) | discovery | *(not inventory-capable)* | — |
| `ssh` (SRG) | credential-test | `srg-ssh` | — |
| `ssh` (SRG) | scan | `srg-ssh` | — |
| `ssh` (SRG) | remediation-ready planning | `srg-ssh` | — |

Notes:

- **Discovery requires only `vsphere-api`**, never `vcsa-ssh` — issue #583's explicit
  acceptance criterion, and the exact defect issue #580/PR #606 fixed. `nsx-api` and
  `ssh` targets have no discovery operation at all today
  (`INVENTORY_CAPABLE_TARGET_KINDS` in `frontend/src/screens/configuration/sites.ts`
  and the backend's `DiscoveryController.Discover` vsphere-only guard) — the matrix
  reflects that as "not applicable," not as a silently-optional purpose.
- **Credential-test is purpose-scoped, not target-scoped**: `WaypointCredentialTest.psm1`
  already exports three distinct functions (`Invoke-WaypointVCenterCredentialTest`,
  `Invoke-WaypointNsxCredentialTest`, `Invoke-WaypointSshCredentialTest`) — a `vsphere`
  target can be tested against `vsphere-api` and `vcsa-ssh` independently, matching two
  matrix rows rather than one.
- **VCSA scan components are optional at the target level, required when selected**: a
  scan that includes no VCSA component never needs `vcsa-ssh` (`Get-StigTargets`
  already skips VCSA targets gracefully when no VCSA credential resolves, logging a
  `Warning` and continuing the rest of the row) — but a scan that *does* select a VCSA
  component makes `vcsa-ssh` required for that job, not optional.

### 4. Defaulting, override, and compatibility

- **Default**: every purpose a selected target/component combination requires resolves
  to that target's own assigned binding for that purpose (epic #582's "use credentials
  assigned to each target"). No cross-target or cross-purpose fallback — a `vsphere`
  target with no `vcsa-ssh` binding does not borrow another target's VCSA credential.
- **Override**: a caller may substitute a different saved credential (or, for the
  personal tier, an ad hoc one — ADR-0011/ADR-0016) for a specific
  `(target, purpose)` pair. An override is only accepted when the substituted
  credential's type is in that purpose's compatibility set (§2) — an `nsx` credential
  can never be offered as an override for `vcsa-ssh`, and the API layer rejects the
  substitution rather than silently ignoring the type mismatch.
- **No purpose-crossing overrides**: overriding `vsphere-api` for a target never
  implicitly overrides that same target's `vcsa-ssh` — each `(target, purpose)` pair is
  independently overridable, consistent with them being independently satisfiable
  (§1).

### 5. Snapshot behavior at run creation

At `POST /runs`, every required purpose for the run's resolved scope (site/targets ×
selected components) is resolved — defaults plus any accepted overrides — into
job-scoped, immutable bindings *at that moment*. A later edit to a target's assigned
credential (re-pointing `credential_id`, rotating a secret) never changes an in-flight
or already-created run's bindings, matching the "later target edits cannot change an
in-flight run" property epic #582 states directly. This generalizes
`RunCreationService`'s existing single-`effectiveCredentialId` snapshot
(`credentialId ?? target.CredentialId`, captured once at run creation) to one snapshot
per `(job, purpose)` instead of one per run.

### 6. Missing-binding behavior

A required purpose with no resolvable binding (no target default, no accepted
override, ad hoc declined) is a **validation error at run/schedule creation**, before
any job is queued — never a partial run that discovers the gap mid-execution. This
extends `Get-StigTargets`' existing "VCSA-only request with no VCSA credential throws"
behavior (§3 note) to every purpose, and generalizes it from "throws inside the
runner" to "rejected by the API before the run exists," matching how `RunsController`
already validates `scope.profile_id` before persisting a run (api-contract.md).

### 7. Scheduling behavior

A scheduled run resolves purpose bindings **the same way** an interactive run does —
target defaults plus whatever compatible overrides the schedule itself carries — at
the moment the schedule fires, not at schedule-creation time. There is no interactive
prompt at fire time: domain-model.md's existing rule ("scheduled runs execute under
the target's service credential") extends unchanged to "scheduled runs execute under
each purpose's target-assigned service credential," and the ADR-0011 personal tier
(interactive-only, never persisted for reuse) is never a valid binding source for a
scheduled run — the same missing-binding validation (§6) applies if a schedule's scope
requires a purpose with no service-tier binding.

### 8. Audit behavior

Every purpose binding actually used by a job — default or override, saved or ad hoc —
is attributable after the fact: which credential (or, for ad hoc, that a personal
credential was used and by whom) satisfied which purpose for which target in which
job. This is the same audit obligation ADR-0014 §6 already places on every claimed-job
decrypt (audited on every decrypt, registered with the in-play redactor) and
ADR-0016 §3 places on ad hoc `run_secrets` decrypts — extended here to be per-purpose
rather than per-job, since one job may now decrypt more than one credential (e.g. a
VCSA-component job needs both `vsphere-api`, to reach the vCenter, and `vcsa-ssh`, to
SSH the appliance).

### 9. What this ADR does not do

This ADR defines the model only. It makes **no persistence or migration change**
(#584), **no change to how discovery/credential-test/scan/remediation actually
execute** (#585/#586), and **no UI change** (#587). The shared backend/frontend
constants and matrix data this ADR ships alongside it (§10) are inert: nothing outside
their own unit tests imports or consumes them yet.

### 10. Machine-testable closed contract

Backend (`Waypoint.Core.Secrets`): `CredentialPurposes` (closed string-constant set,
same shape as `CredentialTypes`/`TargetKinds`) plus a static matrix — target kind ×
operation → `(required purposes, optional purposes)`, and purpose → satisfying
credential types — as plain data, covered by unit tests asserting every current target
kind (`TargetKinds.All`) and every applicable operation is represented, and every
purpose referenced has at least one satisfying credential type from
`CredentialTypes.All`.

Frontend (`frontend/src/screens/configuration/`): a mirrored `CredentialPurpose` union
and `CREDENTIAL_PURPOSES` constant with wire values identical to the backend, plus a
test asserting the two sides' value sets match — the same "closed set + explicit wire
values" convention `TargetKinds`/`TargetKind` and `CredentialTypes`/`CredentialType`
already use, extended with an explicit sync assertion (see PR for issue #583).

## Consequences

- `targets` gains, in #584, a reusable-binding shape keyed by `(target_id, purpose)`
  instead of a single `credential_id` column — this ADR is what makes that schema
  change well-defined rather than another guess at "how many slots."
- `runs`/`jobs` gain, in #585/#586, per-purpose resolved/snapshotted references instead
  of the single `credential_id ?? target.credential_id` pattern
  `RunCreationService` uses today; `ADR-0016`'s `run_secrets` model extends from
  one ad hoc secret per run to one keyed by `(run, target, purpose)`, exactly as epic
  #582's Design section states.
- The wizard (#587) can compute, client-side, exactly which purposes a given
  target/component selection requires and show coverage/gaps before submit, using the
  same matrix data this ADR ships (mirrored, not re-derived).
- Every future target kind or scan component addition must extend this matrix
  explicitly — the completeness tests this ADR's PR adds will fail the build the same
  day a new kind/component is wired without a matrix entry, closing the "generic
  numbered slot" failure mode issue #583 was filed to prevent.
