# Waypoint — Download-Domain Screen IA (PROPOSAL)

**Status: PROPOSAL awaiting owner approval on
[issue #1035](https://github.com/blac9216/waypoint/issues/1035).** This document is
Wave 0's answer to decision **R2-11** on
[epic #16](https://github.com/blac9216/waypoint/issues/16) — the owner deliberately
left the download-domain screen IA open for a proposal rather than deciding it in the
2026-08-28 grill. Nothing here is built or scheduled in detail until the owner
comments approval (numbered answers to the Open Questions below are enough) on #1035;
that approval is issue #1035's **second acceptance criterion** and is **not**
satisfied by this document landing — this PR delivers the roadmap reconciliation and
this proposal only. Wave-2+ download UI issues are scoped in detail only after
approval.

This document follows [`design-brief.md`](design-brief.md)'s entity/action-map
convention for each screen (**Entities**, **Actions** with an RBAC tier, then
**Placement**/**Open questions**) rather than restating it. Domain facts here are
normative from [`../domain-model.md`](../domain-model.md)'s "Depot, catalog identity,
subscriptions, and presence sweep" section and the ADRs it cites (0028–0034); wire
facts (endpoints, shipped-vs-planned, RBAC-as-implemented) are cited from
[`../api-contract.md`](../api-contract.md)'s "Depot catalog & downloads" and "Library
& content library" sections and its "RBAC map — download domain" table, reconciled by
issue #1034 via PR [#1747](https://github.com/blac9216/waypoint/pull/1747) (merged
2026-09-06). Where
this document and the existing [`prototype/`](prototype/) disagree, this document
wins for the download domain specifically, exactly as `design-brief.md` already
established for the compliance domain.

## RBAC — download domain (decision R2-10, reconciled by PR #1747)

| Action family | Viewer | Operator | Admin |
|---|---|---|---|
| Read catalog / downloads / enrollment / library / retention / subscription state | ✅ | ✅ | ✅ |
| Ad-hoc download enqueue / cancel | — | ✅ | ✅ |
| Content-library registry create/delete | — | — | ✅ |
| Content-library organize (folders, item→folder assignment) | — | — | ✅ ⚠️ |
| Content-library item upload (planned) | — | 🚧 Operator+ intended | 🚧 |
| Subscriptions & presets (any lane) | — | — | ✅ |
| Retention pin/unpin/purge-now/dial/review-list delete | — | — | ✅ |
| Repo-serving credential binding / serving+auth dials | — | — | ✅ |
| Depot enrollment / tool install state (Software Depot ID, Activation Code) | — | — | ✅ |
| Catalog sync/pull (tool-driven) | — | — | ✅ |
| Alert acknowledge | — | — | ✅ |

⚠️ **Known divergence, not silently resolved here:** R2-10 names "Operator: ad-hoc
downloads, library upload/organize," but the shipped/in-flight content-library folder
surface (issue #1389) gates every organize action Admin-only. PR #1747 filed this as
deferred issue #1746 rather than fixing it in a doc-only PR; this IA proposal follows
the ruling (Operator-tier) for the **Content Library** screen below and flags the gap
again in Open Question 1 — do not read the current Admin-only implementation as this
document's intended end state.

Every RBAC gate below follows the existing repo-wide convention
(`design-brief.md`'s "RBAC — UI role gates"): visible-with-disabled-reason for a
permission gap, never silently hidden. Mode-gating (air-gapped hides the whole
Download Catalog nav item, per `prototype/README.md`) is the only case that removes a
screen entirely, and it is orthogonal to the RBAC table above.

## 1. Download Catalog — catalog browse + ad-hoc download

**Placement:** existing nav item, unchanged (`prototype/README.md` screen 6,
`design-brief.md` screen 5 "Download catalog browser"). Shipped incrementally: #796
(product grouping, PR #1586) and #1479/#1482/#1486 (binaries-download selection →
enqueue → handler → verification) already landed against this screen's data source.

**Entities:** `catalog artifact` (vendor `productVersionCatalog` row — product,
version, size, sha256; single source of artifact identity, `../domain-model.md`) →
`unknown catalog file` (on-disk, catalog does not describe it — surfaced, never
silently dropped, issue #1495/#1488) → `binaries-download run/job` (one run, one job
per selected artifact or resolved release member, `POST /downloads/binaries`) →
`enrollment state` (Software Depot ID / Activation Code health gating `/catalog/pull`).

**Actions:**
- Browse/search/filter the indexed catalog, including whole-release selection
  (Viewer+; already shipped per #796).
- View unknown-catalog-files list (Viewer+, `/catalog/unknown-files`, planned surface
  addition to this screen — not yet wired into the UI as of this proposal).
- Queue an ad-hoc download — individual artifacts or a whole release — via
  `POST /downloads/binaries` (Operator+; shipped).
- Cancel a queued/running download (`DELETE /downloads/{id}`) (Operator+; shipped).
- Trigger a local re-index (`POST /catalog/sync`) or an authenticated vendor
  catalog-pull (`POST /catalog/pull`) (Admin-only; shipped).

**Superseded from the prototype:** the legacy `POST /downloads` flat-artifact queue
flow is retired (ADR-0030); the retired-status column value described in
`prototype/README.md`'s STATUS enum (`not downloaded`/`queued`/`downloading N%`/
`verified`/`failed`) stays visually correct but now reflects `binaries-download`
job/attempt state, not the legacy `download` job type.

## 2. Subscriptions & Presets

**Placement:** new screen, right rail off Download Catalog or its own nav entry
under CONTENT (Open Question 2). No prototype precedent — this is new for the
depot-parity story.

**Entities:** shipped, read-only **preset** (stack — VCF or VVF — × generation,
clone-to-custom, `../domain-model.md`) → **Subscription** (adopted from a preset or
built custom; tracks at subminor/minor/major granularity, never a hardcoded major
version, pulls the whole release when adopted) → per-lane subscription rows —
`EsxAcquisitionSubscription` (shipped: issue #1470, `selected_platforms` validated
against the live `lcm.esx.supported.host.platforms` vocabulary via
`/downloads/esx/platforms`) is the one lane with a subscription model shipped as of
this proposal; Photon/VMTools/VKS subscription models are planned (Wave 2/5, not yet
built).

**Actions:**
- View shipped presets by stack × generation (Viewer+).
- Adopt a preset as-is, or clone it to a custom subscription (Admin-only, matches
  R2-10 "subscriptions/presets").
- Create/edit/disable a subscription per lane — `POST`/`PATCH
  /downloads/esx/subscriptions` for the ESX lane (shipped); equivalent per-lane
  endpoints for Photon/VMTools/VKS are planned (Wave 5).
- View subscription evaluation history once the evaluation job (#1046 design record,
  tracked in #1472, planned) ships — not yet a screen element.

**Open question:** see Open Question 3 (subscription-evaluation history placement)
below.

## 3. Per-lane store views

One store view per lane, each following the same shape (filter/search over the
lane's indexed metadata, presence/status per item, lane-specific dial), reachable
from a per-lane tab or sub-nav under a "Stores" grouping (Open Question 2).

### 3a. ESX patch store

**Entities:** ESX patch-store metadata index (issue #1446/#1447: consolidated-index
walk, content identity by zip-byte SHA-256, missing/orphan detection, both gated on
structural parse health) → generation-scoped **hardlinked view tree** (ADR-0032,
planned, not yet built — mixed-generation consumers get a generated view, never a
clone or an nginx rewrite; perfect generation purity is impossible per-zip filtering).

**Actions:**
- View the indexed patch store, filterable by platform/generation (Viewer+).
- View missing/orphan discrepancies (Viewer+; shipped per #1447).
- Manage the ESX acquisition subscription (see Subscriptions screen above,
  Admin-only).

**Explicitly NOT this screen** (ADR-0032 supersedes owner-grill decisions 9–10 in
full): there is no UMDS install flow, no EULA-acceptance step, and no ephemeral
`-S`-flag token/config UI. `vcf-download-tool` is the only acquisition path across
every supported generation (6.7–9.1); PR #1747's "ESX patch store — what is and is
not built" section is the normative statement that this UMDS-shaped surface will
never exist. Any design document, past or future, describing a UMDS install/EULA
screen for this lane is superseded.

### 3b. Photon store

**Entities:** upstream repo metadata (discovered/indexed by default, no download
without an explicit subscription or ad-hoc request) → mirrored repo laid out
upstream-identical for a straight `tdnf` baseurl swap.

**Actions:** view indexed repos/versions (Viewer+); subscribe or ad-hoc-sync
(Admin-only for subscription, Operator+ for ad-hoc per the general RBAC table).
Planned (Wave 5, #1052 design record, tracked in #1509/#1518/#1521/#1527) — no
shipped surface as of this proposal.

### 3c. VMware Tools store

**Entities:** full upstream-repo index (beyond the sibling's scope) → preset
(latest ISO+EXE, Windows, no ARM, export-to-content-library) → customizable
per-product subscription.

**Actions:** view indexed Tools versions (Viewer+); adopt/customize the preset
(Admin-only); export a specific version to a content library (Operator+, matches
"library upload/organize"). Planned (Wave 5, #1053 design record, tracked in
#1392/#1438/#1458) — no shipped surface.

### 3d. VKS store — dimensioned view

**Entities:** standalone VKS library (never merged into a local content library) →
dual acquisition backend (depot-fed VKR primary + public-library mirror) →
three-class parity alert (depot measured NOT a superset of the public library:
research found 138 public vs 74 catalog entries, 12 public-only newer — see Alerts
below) → time-window + variant/name-filter subscription.

**Actions:** view the VKS library sorted/filtered by **k8s version** and **distro**
dimensions (per owner decision 14 amended and design #16 §5) (Viewer+); subscribe
with a time window and variant filter (Admin-only); view per-item parity class
(depot-only / public-only / both) once the parity alert ships. Planned (Wave 5,
#1054 design record, tracked in #1480/#1492/#1500/#1508) — no shipped surface.

### 3e. Content-library per-type views + virtual folders

Covered as part of the Content Library screen below (section 5) rather than
duplicated here, since it shares that screen's registry/item model.

## 4. Serving & auth dials

**Placement:** new tab or panel, most naturally under Configuration
(`prototype/README.md` screen 9) alongside the existing depot-token panel, since it
is Admin-only persistent configuration, not a content-browsing surface. Open
Question 2 covers exact placement across all the new screens in this document.

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

**Open question:** see Open Question 4 (ESX store's anonymous-by-default posture and
whether its dial should even be editable, since vLCM's own patch URL consumer is
anonymous-only per research).

## 5. Content Library — registry, per-type views, virtual folders

**Placement:** existing "Library" nav item (`prototype/README.md` screen 7,
`design-brief.md` screen 5 group), Content Library tab specifically. The existing
Repository tab (presence per mode) is unaffected by this proposal.

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
- Organize: create/rename/delete a virtual folder, assign an item to a folder
  (**Operator+ per R2-10's ruling** — see the ⚠️ divergence note above; the
  in-flight #1389 branch currently gates this Admin-only, tracked as deferred
  issue #1746, not fixed by this doc).
- Create/delete a library registry entry (Admin-only; shipped).
- Upload an item (chunked/resumable) once built (Operator+ intended; planned, no
  contract yet).
- Automated add-to-library from another store (VMTools export, VCSA ISO, etc.)
  (Operator+ intended; planned).

**Superseded from the prototype:** `prototype/README.md` screen 7's "Copy to vCenter
library…" footer action and its "Only OVF, ISO, and other files — no VM templates
and no publish/subscribe" framing are superseded by decision 16's multiple
operator-picked regular libraries and the per-type virtual-folder view above; the
underlying item-kind restriction (no VM templates, no publish/subscribe — a VCSP
protocol constraint, not a Waypoint choice) still holds.

## 6. Retention review lists

**Placement:** new tab, most naturally alongside Download Catalog (a queue-adjacent,
cross-lane operational list) rather than under Configuration, since it is reviewed
routinely, not configured once. Open Question 2 covers final placement.

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

## 7. Alert surfacing

**Placement:** no new screen — extends the existing ATTENTION sidebar / Alerts
surface (`design-brief.md`'s "Alerts" section, adopted wholesale per R2-9) with new
`kind` values. Acknowledge stays Admin-only and never hides the underlying condition,
per the existing convention.

**Entities (planned, not yet shipped as of this proposal):** `retention_grace_
approaching` and `retention_grace_expired` (ADR-0034's two new alert kinds) → VKS
three-class parity alert (depot-vs-public-library divergence, research finding #6 on
#1026) → existing kinds (`discovery_failure`, `content_sync_failure`, etc.) continue
to apply to download-domain jobs unchanged (they are cross-domain kinds already, not
compliance-specific).

**Actions:** unchanged from the existing Alerts model — view (Viewer+), acknowledge
(Admin-only, audit-only, never resolves/hides).

## 8. Enrollment / tool-install state

**Placement:** existing "Configuration" screen (`prototype/README.md` screen 9), the
existing depot-token panel — this proposal extends it, it does not add a new screen.

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
  #39/epic #558, unaffected by this proposal).

## Cross-links

- Roadmap: [`../roadmap.md`](../roadmap.md#download--depot-parity--open--milestone-design-record-epic-16) — story sequencing,
  Wave 0 status, and implementation progress for the epics this IA proposes screens
  against.
- Domain model: [`../domain-model.md`](../domain-model.md#depot-catalog-identity-subscriptions-and-presence-sweep-planned) —
  normative entities for every screen above.
- ADRs: [0028](../adr/0028-subscription-preset-metadata-indexed-default.md)
  (metadata-indexed-by-default), [0029](../adr/0029-depot-store-volume-topology.md)
  (volume topology), [0030](../adr/0030-retire-legacy-download-job-type.md) (legacy
  download retirement), [0031](../adr/0031-repo-serving-per-location-auth.md)
  (serving auth), [0032](../adr/0032-esx-patch-store-vcfdt-acquisition.md) (ESX
  patch store), [0033](../adr/0033-disk-admission-joins-capacity-model.md) (disk
  admission), [0034](../adr/0034-grace-period-retention.md) (grace-period retention).
- Design record: [issue #16](https://github.com/blac9216/waypoint/issues/16) (owner
  decision record — R2-10 RBAC, R2-11 IA-deferred-to-this-doc).
- API/security reconciliation: [PR #1747](https://github.com/blac9216/waypoint/pull/1747)
  (issue #1034, merged) — the wire-facing source for every ✅/🚧/⏳ marker above, now
  folded into [`../api-contract.md`](../api-contract.md).
- Prototype: [`prototype/README.md`](prototype/README.md) screens 6 (Download
  Catalog), 7 (Library), 9 (Configuration) — visual/interaction reference only; this
  document is normative for the download domain per the same rule
  [`design-brief.md`](design-brief.md) already established for the compliance domain.

## Open questions for the owner

1. **Content-library organize RBAC.** This proposal follows R2-10's ruling
   (Operator+ for folder organize/item→folder assignment). The in-flight #1389
   implementation ships Admin-only instead (deferred issue #1746). Should the IA
   proposal's Operator-tier stand as the target state (and #1746 fixes the
   implementation), or should the ruling be revised to Admin-only to match what
   shipped?
2. **Screen placement/grouping.** This proposal places five new/extended surfaces:
   Subscriptions & Presets, four per-lane store views, Serving & auth dials,
   Retention review lists, and the Alerts extension. Should these live under a new
   top-level "Stores" nav grouping (Download Catalog · Subscriptions · ESX ·
   Photon · VMware Tools · VKS · Library · Retention), or be distributed across the
   existing Download Catalog / Library / Configuration screens as this document's
   per-screen "Placement" notes suggest by default?
3. **Subscription-evaluation history.** Once the evaluation job (#1046 design record,
   tracked in #1472) ships, where should its run history live — a tab on the
   Subscriptions screen, or folded into
   the existing global Live Jobs workspace (per-run detail, same pattern as
   compliance runs)?
4. **ESX store auth dial editability.** Research found vLCM's own patch-URL consumer
   is anonymous-only; ADR-0031/R2-7 defaults the ESX store to anonymous. Should the
   Serving & auth dial screen still expose an editable dial for this store (greyed
   with an explanatory reason, matching the existing visible-with-reason pattern),
   or omit the control entirely for a store where changing it can only break the one
   real consumer?
5. **OCI bundle / push-target consumer screen.** Issue #1403 shipped the domain
   model only (`OciBundle`, `PushTargetConsumer`); no API or UI is planned in the
   waves this proposal covers. Should a placeholder screen be reserved now (so the
   nav/IA does not need to be revisited later), or left entirely out of scope until
   the push-target API (#1441) lands and a follow-up IA issue is filed?
