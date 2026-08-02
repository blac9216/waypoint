# Handoff: Waypoint — DoD VCF Toolkit (on-prem compliance & depot appliance)

## Overview
Waypoint is the web UI for a self-hosted, on-prem appliance that unifies two existing PowerShell/Docker
tools for VMware environments: a **STIG compliance scanner/remediator** and a **VCF software
download/repository manager**. Users are VMware admins and cyber teams in DoD-style networks.

The appliance ships in two deployment modes, always shown as a badge in the top bar:

- **MODE · INTERNET-ENABLED** — can reach the Broadcom depot and GitHub; all features; builds signed export bundles.
- **MODE · AIR-GAPPED** — no external network; consumes imported bundles; download/catalog features hidden or explained-away.

Ships as a self-hosted PWA with **zero external assets** — no CDN fonts, no icon fonts, no remote images.
Everything is system font stacks and a handful of hand-drawn inline SVG nav glyphs.

## About the Design Files
The file in this bundle (`vcf-ops-console.dc.html`) is a **design reference created in HTML** — a working
prototype that shows intended look, information density, and behavior. It is **not production code to copy
directly**. It is authored in a streaming-template format (a `<x-dc>` template plus a `class Component`
logic block) that exists only in the design tool; do not try to port that runtime.

The task is to **recreate these designs in the target codebase's existing environment** — React, Vue,
Svelte, whatever the appliance's web tier already uses — following its established component patterns,
router, data layer, and styling conventions. If no frontend exists yet, choose the framework that best
fits the appliance's stack and implement there. Lift the *values* (hex/oklch colors, spacing, type sizes,
column widths, copy) from this document and the prototype; do not lift the markup.

Open the prototype in a browser to explore it. Everything is interactive: the mode badge toggles
deployment mode, the role select changes permissions live, the Live Run view actually streams.

## Fidelity
**High-fidelity.** Final colors, typography, spacing, density, states, and copy. Recreate pixel-closely
using the codebase's existing primitives. Two caveats:

- All data is realistic **placeholder** data (real-shaped FQDNs, benchmark IDs, artifact names and sizes) — not live.
- Screens are laid out for a desktop operations console. Minimum comfortable width is ~1280px; the design
  degrades gracefully to ~900px but is not intended for mobile.

---

## Global Chrome

### Top bar — 46px tall, `--panel` background, 1px `--line` bottom border
Left to right, 14px gap, 14px horizontal padding:
1. **Mark** — 20×20 square rotated 45°, 1.5px `--acc` border, with a solid `--acc` inner square inset 4px.
2. **Wordmark** — "WAYPOINT", 13px / 650 weight / .14em letter-spacing. Product name is configurable.
3. **Qualifier** — "DoD VCF Toolkit", 10px, `--txt3`, .1em letter-spacing, 2px top padding.
4. 1px × 20px `--line` divider.
5. **Screen title** — 13px, `--txt2`, 500 weight.
6. Spacer.
7. **STIG Manager status** — 6px green dot + "STIG Manager" 11px, in a `--panel2` pill with `--line` border, 3px radius. Tooltip carries endpoint + collection.
8. **Mode badge** — button. 5px/11px padding, 3px radius, 11px / 650 / .09em. Colors below. Contains a 6px dot pulsing on a 2.4s ease-in-out loop. Click toggles mode (this is a demo affordance; in production the mode is fixed at deploy time and the badge is read-only).
   - Internet-enabled: text/dot `--ok`, background `--okd`, border `--ok`.
   - Air-gapped: text/dot `--warn`, background `--warnd`, border `--warn`.
   - Tooltip explains what the mode allows.
9. **Role select** — native `<select>`, `--panel2` background, 11px. Options are "j.moreno · Viewer/Cyber/Operator/Admin".
10. **Theme toggle** — 28×28 button containing an 11px half-filled circle.

### Left rail — 212px expanded / 56px collapsed, 160ms width transition
`--panel` background, 1px `--line` right border, 8px vertical padding.

Nav items: 8px/17px padding, 12px gap, 15px inline SVG icon (1.4px stroke, currentColor, no fill),
12.5px label. Active item: `--accd` background, 2px `--acc` left border, `--txt` label.
Inactive: transparent background, transparent left border, `--txt2` label.

Groups, separated by a 1px `--line` rule with 9px/12px margins. Group labels are
9px / 650 / .14em / `--txt3`, shown only when expanded; collapsed mode shows a 5px spacer instead so the
separators still read as clusters.

| Group | Items |
|---|---|
| _(ungrouped)_ | **Dashboard** — 4-square grid icon |
| COMPLIANCE | **Live Run** (pulse-line icon, amber count badge for active runs) · **Start a Scan** (magnifier) · **Results** (3 lines) · **Benchmarks** (document with lines) |
| CONTENT | **Download Catalog** (down arrow over a baseline; hidden entirely in air-gapped mode; badge shows queue depth) · **Library** (bar-chart-ish shelf icon; amber badge shows the missing-artifact count when air-gapped) · **Transfer** (two opposed arrows) |
| CONFIGURE | **Configuration** (concentric circles) |

Footer, expanded only: version "v2.4.1 · build 24817" 10px mono `--txt3`, and "2.5.0 available" in `--warn`.
Below that, a Collapse button with a chevron that rotates 180° when collapsed.

### Global job log drawer — bottom of the main column, present on every screen
**Defaults closed.** Collapsed it is a single 7px/20px bar: chevron (rotates 180° when closed),
"JOB LOG" label (10.5px / 650 / .1em / `--txt3`), a pulsing `--acc` dot with the active job count,
a mono summary of running job names (the only shrinking element — `min-width:0` + ellipsis), and the
line count. Whole bar is the click target.

Opened it adds, above the bar, a 7px drag handle (`cursor:ns-resize`, 38×2px grip) and, below it, the log
body: `--bg` background, mono 11.5px, 1.6 line-height, 8px/20px padding. Each line is a flex row of
timestamp (`--txt3`), a 44px fixed level column (INFO `--txt3` / OK `--ok` / WARN `--warn` / ERROR `--bad`),
and the message (`--txt2`). Follow-tail and Download buttons appear in the bar when open.

**Critical layout constraint:** the log body is `height: <logH>px; max-height: 26vh` and the drawer root is
`flex: 0 0 auto`. Drag-resize clamps to `[96px, 40% of window height]`. Without those caps the drawer
starves the screen above it. Auto-scrolls to bottom on update when follow-tail is on (set `scrollTop =
scrollHeight`; never `scrollIntoView`).

---

## Roles & Permissions

Four roles, strictly increasing:

| Role | Can |
|---|---|
| **Viewer** | Read-only dashboards and results |
| **Cyber** | Viewer + initiate scans using assigned service credentials + export/audit results. No config, credentials, downloads, or remediation |
| **Operator** | Cyber + ad hoc scans and remediation with their own stored credentials + downloads and content library management |
| **Admin** | Everything — sites, credentials, users, remediation, updates |

**Treatment:** actions a role cannot take stay **visible but disabled**, at `opacity: 0.42`, with a
`title` giving the reason — e.g. "Requires Admin — configuration is not available to Viewer",
"Requires Operator or Admin — downloads are not available to Cyber". Never silently hidden. (Mode-gating
is different: air-gapped genuinely *removes* the Download Catalog nav item, because the feature does not exist.)

**Screen-level guard:** changing role while inside a screen the new role cannot access redirects to
Dashboard. Do not rely on gating the nav entry point alone.

---

## Screens

### 1. Live Run — the hero screen
A scan run fanning out across ~40 targets in priority queues. 18px/20px header on `--panel`.

**Header:** pulsing `--acc` dot · run id (mono 13px/600, nowrap) · "SCAN · READ-ONLY" pill
(`--accd` bg/border, `--acc` text, nowrap) · a describing sentence that is the only shrinking element
(ellipsis). Second line: 6px progress bar (`--acc` fill, 400ms linear transition) with a nowrap mono
readout "N/40 complete · N% · elapsed Nm NNs". Right side: three big mono counters — PASS `--ok`,
FAIL `--bad`, N/A `--na` at 22px/300 — then Pause queue and Abort run buttons.

**Blocked banner** (when a queue halts): `--badd` background, `--bad` border, explains the halt and offers
"Change credential & resume" (Admin only).

**Layout switcher** — three segmented buttons, active gets `--panel2` background and `--txt`:

- **Priority queues** (default). Five sticky queue headers — `P1 NSX MANAGERS`, `P2 VCSA COMPONENTS`,
  `P3 VCENTER APPLIANCES`, `P4 ESXI HOSTS`, `P5 GUEST / SSH TARGETS` — each with the benchmark id and a
  "N / M complete" or "HALTED — credential failure" status. Under each, a `table-layout:fixed` table:
  target name (flexible, mono, ellipsis, with a state dot that pulses while in flight) · state pill 104px ·
  progress bar 110px · controls 62px · pass 46px · fail 38px · N/A 38px · note 148px (ellipsis).
  Row vertical padding comes from `--rowpad` (5px).
- **State board.** Six columns — QUEUED, RUNNING, ATTESTING, CONVERTING, UPLOADED, FAILED / BLOCKED —
  as auto-fit cards with a count and small target chips (2px colored left border).
- **Log-first.** Narrow target list (380px, own scroll) beside a full-height log pane.

**Target state machine:** `queued → running → attesting → converting → uploaded`, plus `failed`,
`auth failed`, and `blocked`. Colors: queued `--txt3`, in-flight `--acc`, uploaded `--ok`, failed `--bad`.
Progress is `stage × 25%`.

**Failure story to preserve:**
- `vcsa-01a / sts` fails at convert — "hdf→ckl failed — control V-259142 has no matching rule id in V1R2".
- `esx-04a` fails at attest — "alpha-esxi-attest.yml:41 — unknown key 'justifcation'".
- The P5 guest queue halts after three consecutive `svc-stig-vm` SSH auth failures. This one failure
  surfaces in four places: the run banner, the dashboard ATTENTION card, a red credential row in Config,
  and "credential error" discovery status on the SSH targets. Keep that thread intact.

**Simulation:** concurrency 5; queues dispatch strictly in priority order (a queue does not start until all
higher-priority targets are complete); each tick advances in-flight targets one stage with ~50%
probability; counts accumulate on completion.

### 2. Dashboard
Four KPI tiles across the top (auto-fit, 140px floor): **Fleet compliance** 87.4% with a delta and a
progress bar · **Open findings** split CAT I / II / III · **Targets** 152 with type chips · **Repository**
2.41 TB of 4.00 TB with a segmented bar (depot / library / photon).

Below, an auto-fit two-column grid (360px floor, so the sidebar drops below rather than squeezing):

- **SITE POSTURE** table — 4 sites, `table-layout:fixed`, columns SITE 24% / TARGETS 10% / COMPLIANCE 21%
  (bar + percentage, colored `--ok` ≥90, `--warn` ≥82, else `--bad`) / CAT I 8% / CAT II 8% / CAT III 8% /
  LAST SCAN 21% (short `08-02 04:12Z` form — the full ISO stamp does not fit).
- **RECENT RUNS** — 6 rows: status dot, run id (never shrinks), kind pill (scan `--acc` / remediate `--bad`),
  site + target count (shrinks), status, relative time.
- Sidebar: **APPLIANCE** (version, deployment, uptime, depot sync, plus an amber update callout) ·
  **SCHEDULES** (per-site scan schedules with next-run and last result, depot index sync, download window,
  benchmark sync; entries go grey "paused" in air-gapped mode) · **ATTENTION** (4 alerts with a 3px colored
  spine — the credential failure, unattested CAT Is, the expiring depot token, the stale inventory cache).

### 3. Start a Scan
Five-step stepper across the top (site → scope → credentials → schedule → confirm), each showing its
current value; the active step has an `--acc` bottom border.

Step 2 is built out: product filters with counts on the left, plus the list of InSpec profiles that will
apply; on the right a checkbox tree of cached inventory (vCenter → cluster → hosts/VMs) with tri-state
parents (a 7px dash for partial, a filled box for on), each row showing build info and "maintenance mode"
for excluded hosts. Header notes the inventory cache time with a refresh link. Footer shows the estimate
("est. 11m 20s · 8,412 controls").

Steps 3 and 4 render below at 62% opacity as previews: credential choice (service vs. personal, radio
cards) and schedule (Run now / Schedule), with the rule stated plainly — **scans are read-only and
schedulable; remediation is destructive, Admin-only, requires typed confirmation, and can never be
scheduled.**

### 4. Results & History
Left rail 330px (can compress to 240px): searchable run list, each row with status dot, run id, kind pill,
site/targets/duration, timestamp and initiator. Selected row gets an `--acc` left border and `--accd` background.

Detail pane: title block with an Export CKL bundle button and a red "Remediate findings…" button (Admin only,
tooltip notes the typed confirmation). Five KPI tiles (auto-fit, 140px floor): compliance, CAT I/II/III open,
attested N/A.

**PER-TARGET ARTIFACTS** table — `table-layout:fixed`, TARGET 24% / BENCHMARK 24% / I, II, III 8% each
(4px horizontal padding — 8px does not fit) / ARTIFACTS 14% / STIG MANAGER 14%. Upload status is a pill,
`inline-block; max-width:100%` with ellipsis so it truncates inside its cell.

Sidebar: **ATTESTATIONS APPLIED** — read-only summary of the waivers that fired during this run (control id,
scope pill, coverage, justification, author/version) with an "Open in Benchmarks" button. Attestations are
*authored* in Benchmarks; Results only reports what was applied. Then **UPLOAD STATUS** (endpoint,
collection, 50/52 uploaded, 2 failed 409 conflict, retry).

### 5. Benchmarks
The content model matters here and is easy to get wrong:

- **InSpec profiles** come from the VMware DoD Compliance Automation repo. They are the unit of execution.
- A profile is either **STIG**-backed — married to an XCCDF benchmark synced from STIG Manager or uploaded
  manually — or an **SRG** (STIG Readiness Guide) profile, which has *no* published STIG benchmark but still
  needs inputs and attestations.
- **Inputs** are values the *scan* needs in order to evaluate a control — the expected syslog host, the NTP
  server list, the approved TLS profile.
- **Attestations** are *waivers* applied after the fact: a control would report Open but is satisfied another
  way, or needs an auditor-facing explanation.
- **Remediation inputs** control what a remediation run may change, and with what values.
- All three resolve **Global → Site → Target**, most specific wins. A lower layer may set a genuinely
  *different* value; it is not a tighten-only relationship.

Left rail 280px: profile list, each with a STIG/SRG badge, profile version, and its benchmark mapping (or
"no published STIG"). Footer buttons: Sync benchmarks from STIG Manager (Admin + connected) and
Upload XCCDF / STIG zip.

Main column is a single vertical scroll container. Header: profile name and version; a benchmark-mapping
strip showing the linked XCCDF, its source badge, coverage ("171 of 171 controls mapped to rule ids"), and a
Change mapping button; then a one-line stat strip (`6 CAT I · 129 CAT II · 36 CAT III | 24 inputs set ·
11 attested · 3 missing an input`).

Two panes below, wrapping when narrow, each capped at `70vh` with its own scroll:
- **Control table** — CONTROL 19% / SEV 8% (bare right-aligned `I`/`II`/`III` in the severity color, no pill
  — a bordered badge does not fit and silently truncated "CAT II" to "CAT I", which is dangerous) /
  TITLE 34% / INPUT 24% (value plus the scope that supplied it; amber "not set" when a control needs an
  input and none exists) / ATTEST 15%.
- **Control detail** with three tabs — **Input / Attestation / Remediation** — identical treatment in each:
  an `--acc` section heading, a short explanation, three layer cards (Global / Site / Target, each with a
  "defined / overrides global / overrides site / not defined / none" tag, the YAML fragment, and
  author + timestamp), then a highlighted **EFFECTIVE FOR <target>** card in `--acc`/`--accd` showing the
  resolved value and where it came from. Site layers tint `--info`, target layers `--na`, undefined layers stay muted.

Versioning is mandatory: every layer records author and timestamp, and files carry a version (`@v7`) — auditors
will ask who changed an attestation and when.

### 6. Download Catalog (internet-enabled only)
Filter bar (wraps): search, product, version, status selects, and the index sync time.
Table `table-layout:fixed`: checkbox 7% (`7px 6px 7px 14px` padding — a 5% column cannot hold a 13px box
plus 26px of padding) / ARTIFACT 30% with sha256 subline / PRODUCT 15% / VERSION 12% / SIZE 12% / STATUS 24%.
Status is `not downloaded` `--txt3` · `queued` `--warn` · `downloading 43%` `--acc` with an inline progress
bar · `verified` `--ok` · `failed — checksum mismatch` `--bad`. Selected rows get `--accd`.
Sticky footer: selection count, total size, transfer estimate, Clear, and "Queue N downloads" (Operator+).

Right rail 330px: **DOWNLOAD QUEUE** (per-item progress, rate, ETA, retry counts) · **LOCAL STORES**
(depot mirror / content library / photon repo with usage bars) · **SCHEDULE** (depot index sync, download
window, auto-fetch rule, bandwidth cap — Edit is Operator+).

### 7. Library
Two tabs.

**Repository** — what artifacts are present on this appliance. Product-family rail with have/missing counts,
then a table: ITEM 32% / VERSION 11% / KIND 10% / SIZE 9% / PRESENCE 16% / SOURCE 22%. Presence is
`present` `--ok` · `superseded` `--warn` · `in depot` `--acc` (connected) · `missing` `--bad` (air-gapped).
Source names the provenance: "depot · 2026-07-11", "bundle xfer-2026W31".

The callout above the table changes with mode. Connected: items marked "in depot" are entitled and indexed
but not downloaded. Air-gapped: **presence is evaluated against manifest metadata carried in the last
imported bundle**, missing items are called out, and the footer action becomes "Export request manifest"
instead of "Queue missing in catalog".

**Content Library** — a real local content library living on the appliance, like a vSphere library.
Upload item / Import from repository / New folder. Table: ITEM 32% / TYPE 14% / SIZE 10% / ADDED 20% /
SOURCE 24%. **Only OVF, ISO, and other files** — no VM templates and no publish/subscribe; those are not
possible from a custom repo. Footer action is "Copy to vCenter library…".

### 8. Transfer
**Connected** — compose an export bundle. Grouped checkbox tree (scan artifacts, depot artifacts, photon
repo delta, appliance update) with tri-state group checkboxes and per-item sizes. Sidebar summarises the
bundle: name, item count, uncompressed and estimated compressed size, signing key, media split
("4 × 8 GB"), and a "Build & sign bundle" button. Below, recent bundles with where and when they were applied.

**Air-gapped** — import and validate. Header states the signature verified against the named key, item
count, size, and origin appliance. A green strip confirms signature, checksums, and schema. Then a
**contents diff against local state**: `+` new (`--ok`), `~` replaces (`--warn`), `=` identical (`--txt3`),
each with the action and size. Sidebar totals new/replaced/unchanged and disk-after-apply, with "Apply import".

### 9. Configuration
Six tabs.

- **Sites & Targets** — targets table (TARGET 32% / KIND 16% / CREDENTIAL 18% / DISCOVERY 17% /
  LAST REFRESHED 17%) with discovery counts for vCenters and "credential error" in `--bad` for the failing
  SSH targets; a footer line stating how many ESXi hosts and VMs were discovered, with a Refresh inventory
  action. Sidebar lists sites with target counts and their STIG Manager binding (inherit or override).
- **Credentials** — name, owner (personal/shared), type, used-by count, rotation date, status pill
  (`valid` / `auth failing` / `rotate in 6d`).
- **Depot & Tokens** — Broadcom Support Portal token (account, masked token, Replace, expiry warning, Test —
  disabled in air-gapped mode); the separate **VCF Download Tool depot token** (token only, *no URL field*),
  shown unconfigured; and the **download tool binary**. Licensing prevents shipping the tool in the appliance
  image, so it can be installed three ways: **from the local indexed repository** (primary — works in both
  modes, shows the on-disk artifact and where it came from), **fetched from the depot** (connected only), or
  **uploaded** (drag-drop zone, accepted versions, signature verified against the Broadcom release key).
  Until it is installed the catalog still works as a **browsable indexed depot** — only fetching is
  unavailable; the state chip is amber, not red. Install history includes a rejected-signature attempt.
- **Compliance Content** — manages the VMware DoD compliance-and-automation repo that provides the InSpec
  profiles: repository, pinned tag vs. tracked branch, commit, last pull and by whom, profile counts, an
  update banner with changelog, Check for updates / Import content bundle / Pull. Beside it a profile
  inventory (profile, STIG or SRG, version, state: current / update pending / local override — pinned).
  Air-gapped replaces the pull with content-bundle import and explains why.
- **Users & Roles** — user, role pill, site scope, auth method (PIV/CAC, LDAP), last seen, plus a one-line
  restatement of what each role can do.
- **STIG Manager** — global default endpoint, OIDC client, default collection, reachability with API version
  and token TTL, Test button; beside it per-site overrides (three inherit, Charlie Vault points at a
  separate SCIF instance).
- **Updater** — current version and install date, an available-update callout with changelog and required
  maintenance window, Check for updates (connected only) / Upload update bundle / Apply, five pre-flight
  health checks, and update history including a rollback.

---

## Interactions & Behavior

- **Navigation** — single-page, screen state only; no URL routing in the prototype. Use the app's router.
- **Mode toggle** — switches connected/disconnected. Hides the Download Catalog nav item; if the user is on
  the catalog when switching, redirects to Transfer. Flips Library to the air-gapped presence model, pauses
  depot schedules, disables depot-dependent buttons with explanatory tooltips. In production the mode is a
  deploy-time fact, not a user control.
- **Role change** — see Roles above; includes the redirect guard.
- **Live run simulation** — 800ms tick; the run is pre-advanced ~58 ticks at mount so it opens mid-flight.
  Pause/resume halts the tick. Log lines are appended on every state transition and capped at 260 lines.
- **Log follow-tail** — when on, set the pane's `scrollTop = scrollHeight` on update.
- **Drawer resize** — mousedown on the handle, track `mousemove` on window, release on `mouseup`; clamp to
  `[96px, 40% of window height]`.
- **Animations** — two only: `blip` (opacity 1 → .3 → 1, 1.4s for in-flight targets, 2.4s for the mode dot)
  and 400ms linear width transitions on progress bars. Nothing else moves.

## State Management

| State | Purpose |
|---|---|
| `screen` | active screen key: dash / run / scan / results / bench / catalog / lib / transfer / config |
| `mode` | 'connected' \| 'disconnected' |
| `role` | 'Viewer' \| 'Cyber' \| 'Operator' \| 'Admin' |
| `theme` | 'dark' \| 'light' |
| `railOpen` | left rail expanded |
| `runLayout` | 'queues' \| 'board' \| 'log' |
| `benchTab` | 'input' \| 'attest' \| 'remed' |
| `libTab` | 'repo' \| 'content' |
| `cfgTab` | sites / creds / depot / content / users / stigman / updater |
| `drawerOpen`, `logH`, `follow` | job log drawer |
| `run` | targets[], logs[], pass/fail/na counts, blocked flag, running flag, tick |

**Real data the implementation will need:** sites and targets with discovery state; credentials with
ownership and health; runs with per-target artifacts and severity counts; STIG Manager connection and
per-site overrides; the compliance-content repo state and profile inventory; benchmark/profile mappings;
three-layer input/attestation/remediation documents with version history; depot index; local repository
and content library inventories; download queue; transfer bundles; schedules; appliance/update state.

## Design Tokens

Defined as CSS custom properties on `:root`, overridden under `[data-theme="light"]`. All colors are
**oklch** so the two themes stay in the same perceptual family.

### Dark (primary)
| Token | Value | Use |
|---|---|---|
| `--bg` | `oklch(0.165 0.008 255)` | app background, log panes, inputs |
| `--panel` | `oklch(0.205 0.009 255)` | bars, rails, cards |
| `--panel2` | `oklch(0.238 0.010 255)` | table headers, group headers, chips |
| `--line` | `oklch(0.295 0.012 255)` | primary borders |
| `--soft` | `oklch(0.252 0.010 255)` | row separators |
| `--txt` | `oklch(0.93 0.004 255)` | primary text |
| `--txt2` | `oklch(0.725 0.008 255)` | secondary text |
| `--txt3` | `oklch(0.575 0.010 255)` | labels, meta |
| `--acc` | `oklch(0.70 0.12 235)` | accent / in-flight / selection |
| `--accd` | `oklch(0.315 0.055 235)` | accent fill |
| `--ok` | `oklch(0.745 0.14 158)` | pass, healthy, verified |
| `--bad` | `oklch(0.66 0.18 25)` | fail, CAT I, destructive |
| `--warn` | `oklch(0.80 0.13 78)` | CAT II, degraded, pending |
| `--info` | `oklch(0.72 0.10 250)` | site-layer emphasis |
| `--na` | `oklch(0.58 0.02 255)` | not-applicable, attested |
| `--okd` / `--badd` / `--warnd` | `oklch(0.30 0.06 158)` / `oklch(0.31 0.08 25)` / `oklch(0.32 0.06 78)` | status fills |
| `--rowpad` | `5px` | dense table row padding |

### Light
`--bg oklch(0.972 0.003 255)` · `--panel oklch(1 0 0)` · `--panel2 oklch(0.958 0.004 255)` ·
`--line oklch(0.875 0.006 255)` · `--soft oklch(0.928 0.005 255)` · `--txt oklch(0.255 0.012 255)` ·
`--txt2 oklch(0.455 0.012 255)` · `--txt3 oklch(0.605 0.012 255)` · `--acc oklch(0.52 0.13 245)` ·
`--accd oklch(0.925 0.035 245)` · `--ok oklch(0.545 0.13 158)` · `--bad oklch(0.525 0.19 25)` ·
`--warn oklch(0.60 0.13 70)` · `--info oklch(0.55 0.11 250)` · `--na oklch(0.62 0.015 255)` ·
`--okd oklch(0.94 0.04 158)` · `--badd oklch(0.945 0.04 25)` · `--warnd oklch(0.95 0.05 78)`

### Typography
`--sans: ui-sans-serif, -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif`
`--mono: ui-monospace, "SF Mono", SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace`

Base 13px. Scale in use: 9px (collapsed-rail labels) · 9.5px (layer tags, meta) · 10px (group labels,
sublines) · 10.5px (status pills, dense mono) · 11px (secondary) · 11.5px (body, buttons) · 12px
(table body, card titles) · 12.5px (nav labels) · 13px (screen title, wordmark, section headings) ·
15px (run detail title) · 17px (compact stats) · 22–30px / weight 300 (KPI numerals).

Weights: 400 body, 550 emphasis, 600 semibold, 650 for all-caps section labels. Letter-spacing: .04em on
card titles, .09em on table headers, .1em on small caps labels, .14em on group labels and the wordmark.

**Monospace is reserved for logs and identifiers** — FQDNs, run ids, rule ids, file names, hashes,
timestamps, sizes, versions. Never for prose.

### Spacing, radius, misc
Radius: 2px pills/chips · 3px buttons, inputs, callouts · 4px cards. No shadows anywhere.
Borders are always 1px `--line` or `--soft`; 2px only for active-state left borders and tab underlines.
Padding: 13–16px cards · 7–9px table cells · 5–7px pills · 14px/20px screen gutters. Grid gaps 11–18px.
Icons: 15px in the rail, 11–13px inline, 1.4–1.6px stroke, currentColor.

## Layout Rules Learned the Hard Way
These caused real, repeated bugs in the prototype. Carry them into the implementation:

1. Any dense table with percentage columns **must** be `table-layout:fixed`, and the percentages must sum to
   100%. With `auto`, percentages are ignored and min-content sizing wins.
2. A percentage column cannot be narrower than its own horizontal padding — the cell will not honour the
   width and the whole table grows.
3. Cells with nowrap content need `overflow:hidden` on the `td`; without it the content paints over the next
   column. Badges inside such cells need `display:inline-block; max-width:100%; text-overflow:ellipsis`.
4. Never let a status label truncate silently — "CAT II" clipped to "CAT I" is a correctness bug. Abbreviate
   deliberately (`I`/`II`/`III`, `GLB`/`SITE`/`TGT`) and put the full value in a `title`.
5. Two-column layouts use `repeat(auto-fit, minmax(min(100%, N), 1fr))`. A bare `minmax(N, 1fr)` is a hard
   floor and overflows instead of collapsing; a fixed-px sidebar squeezes the data column to nothing.
6. A fixed-height flex child (the log drawer) must be capped in viewport units, and the scroll boundary must
   be a single unambiguous container per screen.

## Assets
**None.** No external fonts, no icon library, no images. Nav icons are seven hand-drawn inline SVGs
(simple rects, lines, circles, polylines). The brand mark is a rotated bordered square with an inner fill.
This is deliberate — the appliance is air-gapped and must not reference anything remote.

## Files
- `vcf-ops-console.dc.html` — the complete interactive prototype: all nine screens, both deployment modes,
  all four roles, the live run simulation, and both themes. Open it in a browser and click through it.

## Open Questions for the Team
1. Should the Download Catalog stay browsable (index-only) when the download tool is missing? The design
   assumes yes.
2. When mode is fixed at deploy time, does the badge become non-interactive, or is there a supported
   transition path between modes?
3. Remediation currently has entry points only — no typed-confirmation modal is designed yet.
4. Attestation expiry is modeled (`expires: 2027-03-01`) but there is no designed workflow for what happens
   when one lapses mid-run.
