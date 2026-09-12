# Waypoint — Download-Domain Screen IA

Kind: explanation

**Status: approved.** This document is Wave 0's answer to decision **R2-11** on
[epic #16](https://github.com/blac9216/waypoint/issues/16) — the owner deliberately
left the download-domain screen IA open for a proposal rather than deciding it in the
2026-08-28 grill. The owner ruled all five open questions on
[issue #1035](https://github.com/blac9216/waypoint/issues/1035) (2026-09-07); this
document reflects those rulings and is the normative download-domain screen IA.
Wave-2+ download UI issues are scoped in detail against this document.

This document follows [`ui-design-brief.md`](ui-design-brief.md)'s entity/action-map
convention for each screen (**Entities**, **Actions** with an RBAC tier, then
**Placement**/**Open questions**) rather than restating it. Domain facts here are
normative from [`domain-model.md`](domain-model.md)'s "Depot, catalog identity,
subscriptions, and presence sweep" section and the ADRs it cites (0028–0034); wire
facts (endpoints, shipped-vs-planned, RBAC-as-implemented) are cited from
[`../reference/api-contract.md`](../reference/api-contract.md)'s "Depot catalog & downloads" and "Library
& content library" sections and its "RBAC map — download domain" table, reconciled by
issue #1034 via PR [#1747](https://github.com/blac9216/waypoint/pull/1747) (merged
2026-09-06). Where
this document and the existing [`prototype/`](../ui/prototype/) disagree, this document
wins for the download domain specifically, exactly as `ui-design-brief.md` already
established for the compliance domain.

## RBAC — download domain (decision R2-10, reconciled by PR #1747)

| Action family | Viewer | Operator | Admin |
|---|---|---|---|
| Read catalog / downloads / enrollment / library / retention / subscription state | ✅ | ✅ | ✅ |
| Ad-hoc download enqueue / cancel | — | ✅ | ✅ |
| Content-library registry create/delete | — | — | ✅ |
| Content-library organize (create/rename/move/reassign) | — | ✅ | ✅ |
| Content-library organize (delete) | — | — | ✅ |
| Content-library item upload (planned) | — | 🚧 Operator+ intended | 🚧 |
| Subscriptions & presets (any lane) | — | — | ✅ |
| Retention pin/unpin/purge-now/dial/review-list delete | — | — | ✅ |
| Repo-serving credential binding / serving+auth dials | — | — | ✅ |
| Depot enrollment / tool install state (Software Depot ID, Activation Code) | — | — | ✅ |
| Catalog sync/pull (tool-driven) | — | — | ✅ |
| Alert acknowledge | — | — | ✅ |

**Content-library organize RBAC (owner ruling on #1035, item 1):** Operator+ for
create/rename/move/reassign, Admin for delete — exactly as PR #1770 shipped. The
divergence flagged in an earlier draft of this document (issue #1746, tracking the
in-flight #1389 folder surface as Admin-only-for-everything) is resolved by that
ruling and by #1770 landing; there is no remaining gap between this document and the
shipped implementation.

Every RBAC gate below follows the existing repo-wide convention
(`ui-design-brief.md`'s "RBAC — UI role gates"): visible-with-disabled-reason for a
permission gap, never silently hidden. Mode-gating (air-gapped hides the whole
Download Catalog nav item, per `../how-to/ui-prototype.md`) is the only case that removes a
screen entirely, and it is orthogonal to the RBAC table above.

## 1. Download Catalog — the acquisition control plane

**Placement:** existing nav item, unchanged (`../how-to/ui-prototype.md` screen 6,
`ui-design-brief.md` screen 5 "Download catalog browser"). Per the owner's ruling on
#1035 (item 2), **Download Catalog is the download domain's acquisition control
plane** — the download-domain counterpart of the compliance *Live Run* screen. It
owns the download queue, schedules, subscriptions and presets (every lane), and
ad-hoc download, alongside the vendor catalog browse it already shipped. There is no
separate "Stores" nav grouping and no separate Subscriptions & Presets screen; both
live here. Shipped incrementally: #796 (product grouping, PR #1586) and
#1479/#1482/#1486 (binaries-download selection → enqueue → handler → verification)
already landed against this screen's data source.

**Entities:** `catalog artifact` (vendor `productVersionCatalog` row — product,
version, size, sha256; single source of artifact identity, `domain-model.md`) →
`unknown catalog file` (on-disk, catalog does not describe it — surfaced, never
silently dropped, issue #1495/#1488) → `binaries-download run/job` (one run, one job
per selected artifact or resolved release member, `POST /downloads/binaries`) →
`enrollment state` (Software Depot ID / Activation Code health gating `/catalog/pull`)
→ shipped, read-only **preset** (stack — VCF or VVF — × generation, clone-to-custom,
`domain-model.md`) → **Subscription** (adopted from a preset or built custom; tracks
at subminor/minor/major granularity, never a hardcoded major version, pulls the whole
release when adopted) → per-lane subscription rows — `EsxAcquisitionSubscription`
(shipped: issue #1470, `selected_platforms` validated against the live
`lcm.esx.supported.host.platforms` vocabulary via `/downloads/esx/platforms`) is the
one lane with a subscription model shipped as of this writing; Photon/VMTools/VKS
subscription models are planned (Wave 2/5, not yet built).

**Actions:**
- Browse/search/filter the indexed catalog, including whole-release selection
  (Viewer+; already shipped per #796).
- View unknown-catalog-files list (Viewer+, `/catalog/unknown-files`, planned surface
  addition to this screen — not yet wired into the UI as of this writing).
- Queue an ad-hoc download — individual artifacts or a whole release — via
  `POST /downloads/binaries` (Operator+; shipped).
- Cancel a queued/running download (`DELETE /downloads/{id}`) (Operator+; shipped).
- Trigger a local re-index (`POST /catalog/sync`) or an authenticated vendor
  catalog-pull (`POST /catalog/pull`) (Admin-only; shipped).
- View shipped presets by stack × generation (Viewer+).
- Adopt a preset as-is, or clone it to a custom subscription (Admin-only, matches
  R2-10 "subscriptions/presets").
- Create/edit/disable a subscription per lane — `POST`/`PATCH
  /downloads/esx/subscriptions` for the ESX lane (shipped); equivalent per-lane
  endpoints for Photon/VMTools/VKS are planned (Wave 5).
- View subscription-evaluation results and history once the evaluation job (#1046
  design record, tracked in #1472, planned) ships (Viewer+, not yet a screen
  element).

**Owner ruling on #1035 (item 3) — subscription-evaluation history and the global
Jobs relationship:** all download-domain detail — subscription evaluation results,
the queue, what was fetched — lives on this screen. The global **Jobs** view only
records the fact that a job ran, as it does for every job type, and a job row there
points back here for detail. There is no separate evaluation-history tab and no
download-specific detail screen under Jobs; this mirrors the compliance Live Run
relationship exactly.

**Superseded from the prototype:** the legacy `POST /downloads` flat-artifact queue
flow is retired (ADR-0030); the retired-status column value described in
`../how-to/ui-prototype.md`'s STATUS enum (`not downloaded`/`queued`/`downloading N%`/
`verified`/`failed`) stays visually correct but now reflects `binaries-download`
job/attempt state, not the legacy `download` job type.

## 2. Library — home for current content, including the lane stores

**Placement:** existing "Library" nav item (`../how-to/ui-prototype.md` screen 7,
`ui-design-brief.md` screen 5 group). Per the owner's ruling on #1035 (item 2),
**Library is the home for all current content** — the depot's own artifacts and each
of the four lane stores (ESX patch store, VMTools, VKS, Photon) are browsed and
managed inside Library, each with its own index, presence status, and lane dial.
There is no separate "Stores" nav grouping; that concept lives inside Library and
always did. Library keeps showing present-vs-absent so items the metadata knows
about but which are not on disk can be queued from there (queuing itself happens on
Download Catalog, above). The existing Repository tab (presence per mode) and the
Content Library tab (registry, virtual folders, per-type views) are both part of
this screen and are unaffected by this rewrite.

### 2a. Content Library — registry, per-type views, virtual folders

**Entities:** content-library **registry** row (shipped: issue #1391, `POST`/`GET`/
`DELETE /content-libraries`, path-confined, one safe-path-segment name, no cascading
delete while non-empty) → flat-on-disk VCSP item set, per library (`lib.json`/
`items.json` writer, issue #1393, both known sibling defects fixed non-inverted) →
**virtual folder** (DB-only, never on disk, owner decision 16; in-flight, issue
#1389) → **per-type view** (OVA / ISO / files split, family-view pattern per owner
ratification finding #9, issue #1056 design record, tracked in #1429, planned) →
item upload (planned, chunked/resumable, UpdateSession + fleet-depot resumable
APIs are the parity reference, decision 16/research #1055 design record, tracked
in #1520/#1526/#1530 — no endpoint exists yet, so no upload UI can be built
against a real contract today).

**Actions:**
- View a library's items, per-type (OVA/ISO/files) (Viewer+).
- Organize: create/rename/move/reassign a virtual folder or item→folder assignment
  (Operator+, per the owner's ruling on #1035 item 1 and as PR #1770 shipped);
  delete a virtual folder (Admin-only).
- Create/delete a library registry entry (Admin-only; shipped).
- Upload an item (chunked/resumable) once built (Operator+ intended; planned, no
  contract yet).
- Automated add-to-library from another store (VMTools export, VCSA ISO, etc.)
  (Operator+ intended; planned).

**Superseded from the prototype:** `../how-to/ui-prototype.md` screen 7's "Copy to vCenter
library…" footer action and its "Only OVF, ISO, and other files — no VM templates
and no publish/subscribe" framing are superseded by decision 16's multiple
operator-picked regular libraries and the per-type virtual-folder view above; the
underlying item-kind restriction (no VM templates, no publish/subscribe — a VCSP
protocol constraint, not a Waypoint choice) still holds.

### 2b. ESX patch store

**Entities:** ESX patch-store metadata index (issue #1446/#1447: consolidated-index
walk, content identity by zip-byte SHA-256, missing/orphan detection, both gated on
structural parse health) → generation-scoped **hardlinked view tree** (ADR-0032,
planned, not yet built — mixed-generation consumers get a generated view, never a
clone or an nginx rewrite; perfect generation purity is impossible per-zip filtering).

**Actions:**
- View the indexed patch store, filterable by platform/generation (Viewer+).
- View missing/orphan discrepancies (Viewer+; shipped per #1447).
- Manage the ESX acquisition subscription (see Download Catalog above,
  Admin-only).

**Explicitly NOT this screen** (ADR-0032 supersedes owner-grill decisions 9–10 in
full): there is no UMDS install flow, no EULA-acceptance step, and no ephemeral
`-S`-flag token/config UI. `vcf-download-tool` is the only acquisition path across
every supported generation (6.7–9.1); PR #1747's "ESX patch store — what is and is
not built" section is the normative statement that this UMDS-shaped surface will
never exist. Any design document, past or future, describing a UMDS install/EULA
screen for this lane is superseded.

### 2c. Photon store

**Entities:** upstream repo metadata (discovered/indexed by default, no download
without an explicit subscription or ad-hoc request) → mirrored repo laid out
upstream-identical for a straight `tdnf` baseurl swap.

**Actions:** view indexed repos/versions (Viewer+); subscribe or ad-hoc-sync (see
Download Catalog above; Admin-only for subscription, Operator+ for ad-hoc per the
general RBAC table). Planned (Wave 5, #1052 design record, tracked in
#1509/#1518/#1521/#1527) — no shipped surface as of this writing.

### 2d. VMware Tools store

**Entities:** full upstream-repo index (beyond the sibling's scope) → preset
(latest ISO+EXE, Windows, no ARM, export-to-content-library) → customizable
per-product subscription.

**Actions:** view indexed Tools versions (Viewer+); adopt/customize the preset (see
Download Catalog above; Admin-only); export a specific version to a content library
(Operator+, matches "library upload/organize"). Planned (Wave 5, #1053 design
record, tracked in #1392/#1438/#1458) — no shipped surface.

### 2e. VKS store — dimensioned view

**Entities:** standalone VKS library (never merged into a local content library) →
dual acquisition backend (depot-fed VKR primary + public-library mirror) →
three-class parity alert (depot measured NOT a superset of the public library:
research found 138 public vs 74 catalog entries, 12 public-only newer — see Alerts
below) → time-window + variant/name-filter subscription.

**Actions:** view the VKS library sorted/filtered by **k8s version** and **distro**
dimensions (per owner decision 14 amended and design #16 §5) (Viewer+); subscribe
with a time window and variant filter (see Download Catalog above; Admin-only); view
per-item parity class (depot-only / public-only / both) once the parity alert
ships. Planned (Wave 5, #1054 design record, tracked in #1480/#1492/#1500/#1508) —
no shipped surface.

## 3. Serving & auth dials

**Placement:** new tab or panel under Configuration (`../how-to/ui-prototype.md`
screen 9) alongside the existing depot-token panel, since it is Admin-only
persistent configuration, not a content-browsing surface.

**Entities:** one appliance nginx, per-store `location` (ADR-0029) → repo-serving
credential (`RepoCredentialsController`, shipped: create/read/rotate/delete, all
Admin-only) → per-store/per-location auth dial (anonymous → Basic → mTLS, ADR-0031,
amended R2-7: configurable on every store/location, warning badges where the
consumer cannot authenticate, Basic ⇒ HTTPS is a blocking rule, thumbprint +
cert-chain surfaced) → legacy Download Token (demoted, not retired; kept for ad-hoc
7.x/8.x flows only, reported independently and never gates readiness).

**Actions:**
- View per-store auth dial + warning badges (Viewer+, read-only visibility of a
  security-relevant setting).
- Set/change a store's auth dial, subject to the Basic⇒HTTPS blocking rule
  (Admin-only).
- Create/rotate/delete a repo-serving credential (Admin-only; shipped).
- View legacy Download Token health independently of Activation Code health
  (Viewer+; shipped at `/downloads/readiness`).

**Owner ruling on #1035 (item 4) — ESX store auth dial:** already decided by R2-7
(ADR-0031 amended) — the dial is configurable on every serving surface, the ESX
store included, with a warning badge where the intended consumer cannot
authenticate (vLCM's own patch-URL consumer is anonymous-only per research, so the
ESX store's dial defaults to anonymous and shows that warning badge if changed away
from it, exactly like any other store). There is no separate ESX-only exception and
no omitted control for this store.

## 4. Retention review lists

**Placement:** new tab, most naturally alongside Download Catalog (a queue-adjacent,
cross-lane operational list) rather than under Configuration, since it is reviewed
routinely, not configured once.

**Entities:** grace-period tracked item (ADR-0034: approaching-grace / past-grace /
pinned, three distinct states, per-subscription-scope) → manual/ad-hoc download
retention dial (separate, independently configured from any subscription's grace
period) → review-list row = union of **orphaned** (no subscription still matches it)
and **out-of-scope** (never subscribed) content, per ADR-0034 — never auto-removed by
the sweep, surfaced here for explicit deletion only.

**Actions:**
- View per-item retention state (Viewer+; endpoint `/download-retention/state` in
  flight, issue #1453, no PR yet).
- Pin / unpin an item to exempt/restore it from the sweep indefinitely (Admin-only).
- Purge-now to skip the remaining grace period deliberately (Admin-only).
- Set the manual/ad-hoc retention dial (Admin-only).
- View and explicitly delete review-list rows (orphaned/out-of-scope) (Admin-only
  delete; Viewer+ read).

## 5. Alert surfacing

**Placement:** no new screen — extends the existing ATTENTION sidebar / Alerts
surface (`ui-design-brief.md`'s "Alerts" section, adopted wholesale per R2-9) with new
`kind` values. Acknowledge stays Admin-only and never hides the underlying condition,
per the existing convention.

**Entities (planned, not yet shipped as of this writing):** `retention_grace_
approaching` and `retention_grace_expired` (ADR-0034's two new alert kinds) → VKS
three-class parity alert (depot-vs-public-library divergence, research finding #6 on
#1026) → existing kinds (`discovery_failure`, `content_sync_failure`, etc.) continue
to apply to download-domain jobs unchanged (they are cross-domain kinds already, not
compliance-specific).

**Actions:** unchanged from the existing Alerts model — view (Viewer+), acknowledge
(Admin-only, audit-only, never resolves/hides).

## 6. Enrollment / tool-install state

**Placement:** existing "Configuration" screen (`../how-to/ui-prototype.md` screen 9), the
existing depot-token panel — this document extends it, it does not add a new screen.

**Entities:** enrollment state machine (`tool_unavailable` → `depot_id_unavailable` →
`awaiting_portal_registration` → `activation_code_stored` → `validated`/
`auth_failing`, shipped, issue #691) → Depot ID / Activation Code pairing (identity
follows the code — no asset_id cross-check as of the 2026-08-25 owner correction
recorded in PR #1747) → managed-tool-installed state (`tool_installed`, null until a
download-runner has heartbeated at least once).

**Actions:**
- View enrollment state and tool-installed state (Viewer+; shipped).
- Generate a Software Depot ID, accept/replace an Activation Code, validate it, or
  reset the enrollment identity (Admin-only; all shipped).
- Install the download tool through the existing local-repo/depot-fetch/manual-upload
  flow (Admin-only per R2-10's "tool install"; this flow already shipped via issue
  #39/epic #558, unaffected by this document).

**Owner ruling on #1035 (item 5) — OCI push-target screen:** left out until #1441's
PR adds the push-target API; no placeholder screen is reserved. #1403 shipped only
the domain model (`OciBundle`, `PushTargetConsumer`); a follow-up IA issue is filed
once #1441 lands.

## Cross-links

- Roadmap: [`roadmap.md`](roadmap.md#download--depot-parity--open--milestone-design-record-epic-16) — story sequencing,
  Wave 0 status, and implementation progress for the epics this IA covers.
- Domain model: [`domain-model.md`](domain-model.md#depot-catalog-identity-subscriptions-and-presence-sweep-planned) —
  normative entities for every screen above.
- ADRs: [0028](../adr/0028-subscription-preset-metadata-indexed-default.md)
  (metadata-indexed-by-default), [0029](../adr/0029-depot-store-volume-topology.md)
  (volume topology), [0030](../adr/0030-retire-legacy-download-job-type.md) (legacy
  download retirement), [0031](../adr/0031-repo-serving-per-location-auth.md)
  (serving auth), [0032](../adr/0032-esx-patch-store-vcfdt-acquisition.md) (ESX
  patch store), [0033](../adr/0033-disk-admission-joins-capacity-model.md) (disk
  admission), [0034](../adr/0034-grace-period-retention.md) (grace-period retention).
- Design record: [issue #16](https://github.com/blac9216/waypoint/issues/16) (owner
  decision record — R2-10 RBAC, R2-11 IA, ruled on #1035).
- API/security reconciliation: [PR #1747](https://github.com/blac9216/waypoint/pull/1747)
  (issue #1034, merged) — the wire-facing source for every ✅/🚧/⏳ marker above, now
  folded into [`../reference/api-contract.md`](../reference/api-contract.md).
- Prototype: [`ui-prototype.md`](../how-to/ui-prototype.md) screens 6 (Download
  Catalog), 7 (Library), 9 (Configuration) — visual/interaction reference only; this
  document is normative for the download domain per the same rule
  [`ui-design-brief.md`](ui-design-brief.md) already established for the compliance domain.

## Owner rulings on #1035 (2026-09-07)

All five open questions this document originally posed were ruled by the owner on
[issue #1035](https://github.com/blac9216/waypoint/issues/1035); this document now
reflects each ruling directly at its point of relevance rather than restating them
here as open. Summary, for traceability:

1. **Content-library organize RBAC** — Operator+ for create/rename/move/reassign,
   Admin for delete, exactly as PR #1770 shipped (§ RBAC — download domain, § 2a).
2. **Screen placement** — Library is the home for all current content, including
   the four lane stores; Download Catalog is the acquisition control plane (queue,
   schedules, subscriptions/presets, ad-hoc download); there is no separate
   "Stores" nav grouping (§ 1, § 2).
3. **Subscription-evaluation history** — lives in Download Catalog; the global Jobs
   view records only the job fact/link (§ 1).
4. **ESX store auth dial** — stays configurable per ADR-0031/R2-7, with a warning
   badge where the intended consumer cannot authenticate (§ 3).
5. **OCI push-target screen** — left out until #1441's PR adds it; no placeholder
   (§ 6).
