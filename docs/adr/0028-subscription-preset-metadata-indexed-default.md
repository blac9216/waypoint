# ADR-0028: Metadata indexed by default; subscriptions and presets move bytes

Status: Accepted
Date: 2026-09-06

## Context

The download-parity design (Epic #16, owner grill 2026-08-28, decisions 5/12–14) covers
five acquisition lanes beyond the VCFDT-driven depot itself: ESX patches, Photon,
VMware Tools, VKS, and content libraries. The predecessor (`vcf-docker-download`) has
no notion of "what exists upstream but is not downloaded" — a lane either has content
on disk or it is invisible. That forces every lane's operator to download speculatively
just to browse what versions exist, and gives no way to express "keep me current on
the next VCF 9.1.x patch" without a cron job re-running a full sync.

## Decision Drivers

- Every lane must let an operator see what is available before committing disk/network
  to it — the catalog/repo metadata is cheap to fetch and index; the artifacts are not.
- Recurring acquisition (new patch releases, new Photon package versions) must be
  expressible declaratively, not reimplemented per lane as a bespoke poller.
- Presets must be usable out of the box (VCF/VVF × generation is a known, finite matrix)
  without locking an operator out of a narrower or wider tracking scope.
- No lane may download anything the operator did not ask for, directly or via an
  adopted subscription — a "default subscription" must never itself trigger bytes.

## Considered Options

1. **Per-lane bespoke sync jobs, no shared model.** Fastest to build lane-by-lane, but
   reimplements "what's new since last time" five times with five different bug
   surfaces (this is exactly the shape of the pre-existing `#572` version-comparator
   class of bugs), and gives operators five different mental models for the same
   underlying question.
2. **Always-on mirroring (download everything indexed).** Simplest operator story
   ("it's all just there") but defeats the point of metadata-first browsing on a
   bandwidth- and disk-constrained appliance, and silently pulls content nobody asked
   for onto disconnected-mode-bound instances.
3. **Metadata indexed by default; bytes move only by explicit choice — ad-hoc
   (Operator role) or Subscription (Admin role)** (this decision). Every lane indexes
   its own metadata (catalog rows, repo listings) unconditionally and cheaply; a
   Subscription is a durable, evaluated-on-schedule expression of "keep this scope
   current," built from a shared version-tracking primitive (ADR pending on the
   comparator itself, issue #1039) so every lane's supersession/retention logic uses
   the same product-aware ordering.

## Decision

Metadata indexing is unconditional and default-on for every lane; downloading bytes
always requires an explicit trigger — an ad-hoc request (Operator role) or a
Subscription (Admin role, evaluated on the global refresh schedule with per-lane
overrides, ADR-0018's admission gating what actually runs). Subscriptions are backed by
shipped, read-only **presets** keyed by stack (VCF/VVF) × generation, each
clone-to-custom; tracking granularity is subminor/minor/major, never a hardcoded major
version, and adopting a subscription pulls the whole release (every bundle/binary), not
a filtered subset. A "default" subscription is an adoptable template — creating or
shipping one never itself causes a download. Presets are curated in-repo content,
refreshed by appliance updates, not fetched at runtime.

## Consequences

- Every acquisition lane (ESX/VCFDT, Photon, VMTools, VKS, content-library sync) must
  implement metadata indexing before or alongside its download path, even where the
  predecessor conflated the two.
- Subscription evaluation depends on the shared version comparator (issue #1039); a
  lane cannot correctly evaluate "is there something newer in my tracked scope" without
  it, which is why Wave 1 sequences the comparator ahead of Wave 2's subscription
  engine (Epic #16's dependency order).
- Presets are appliance-shipped content: a new VCF/VVF generation ships a preset update
  with the appliance itself, not a runtime catalog fetch — this is a maintenance
  obligation on every release, not a one-time cost.
- An operator can now audit "what would this subscription download" before committing,
  since indexing already ran; this removes the speculative-download workaround
  operators of the predecessor tool used to answer that question.
