# ADR-0034: Grace-period retention within subscription scope

Status: Accepted
Date: 2026-09-06

## Context

Every acquisition lane accumulates superseded content over time (a new ESX patch
supersedes the last one tracked by a subscription; a new Photon package version
appears in a tracked repo). The predecessor tool has no general retention model —
UMDS's own metadata-reconciliation prune is the closest analog, and even that is a
per-lane special case, not something ad-hoc downloads or other lanes share. The
download-parity design (Epic #16, owner grill decisions 5/17) requires retention that
never deletes content a subscription did not put there, because orphaned or
out-of-scope content may be exactly what a later disconnected-side transfer needs
(ADR-0010) even though nothing on the connected side is currently tracking it.

## Decision Drivers

- Retention must never delete content outside what caused it to exist — an orphan (no
  longer matched by any subscription) or out-of-scope content (never subscribed) must
  survive indefinitely until an operator explicitly acts, because a disconnected-mode
  transfer may still need it and Waypoint cannot know that in advance.
- Superseded content inside a subscription's own tracked scope is exactly what
  retention exists to reclaim — keeping every historical patch/package forever inside
  an actively-tracked scope defeats the point of bounded disk usage.
- An operator must have a safety window (grace period) before superseded content is
  actually removed, and a way to pin specific content past that window when a known
  future need exists (e.g., a host fleet not yet upgraded off the version being
  pruned).
- Manual/ad-hoc downloads (not subscription-driven) need their own, separately
  configurable retention dial — they were never subscription-scoped to begin with, so
  the subscription-scope grace-period model does not naturally apply to them.

## Considered Options

1. **Immediate prune on supersession.** Simplest to implement and keeps disk usage
   minimal, but gives an operator no window to notice a problem (a bad patch, an
   in-flight host upgrade still depending on the old version) before the old content
   is gone, and a purge-now action becomes indistinguishable from the routine case.
2. **No automatic retention; operator-driven cleanup only.** Safest against
   accidental data loss, but reintroduces the predecessor's actual failure mode —
   disk fills silently because nothing ever prunes, and the appliance's own capacity
   admission (ADR-0033) starts refusing new acquisition jobs for a subscription that
   was never asked to keep its own history around.
3. **Grace-period auto-prune scoped strictly to subscription tracking, with alerting,
   pinning, and an immediate-purge escape hatch; a separate configurable dial for
   manual downloads; orphans and out-of-scope content never auto-removed** (this
   decision). Bounded disk usage for actively-tracked content, a safety window before
   deletion, and an explicit, auditable path for the one case (orphans) where
   automation genuinely cannot know whether content is still needed.

## Decision

Content that a subscription superseded within its own tracked scope enters a
grace period (subscription-configurable duration, expressed as days or a count of
refresh cycles); an in-app alert (ADR-0019's alert model, adopted wholesale per
decision R2-9) surfaces content approaching or past its grace period, is pinnable
(exempting specific content from the sweep indefinitely), and offers a purge-now
action to skip the remaining grace period deliberately. Manual/ad-hoc downloads (not
subscription-driven) are governed by a separate, operator-configurable retention dial,
independent of any subscription's grace period. Orphaned content (no longer matched by
any subscription, whether the subscription was deleted or its scope narrowed) and
out-of-scope content (never subscribed at all) are **never** auto-removed by the
retention sweep — they are surfaced for explicit operator deletion only, because
metadata for that content may still arrive later via a disconnected-side transfer
(ADR-0010) even with nothing currently tracking it on the connected side. The
retention sweep is one stage of the global refresh schedule (catalog pull →
subscription evaluation → per-lane syncs → retention sweep), runs as its own
independent job, and composes with (does not replace) any lane-specific retention
shape — the ESX/patch lane's ported XML-first-then-prune reconciliation (ADR-0032)
and content-library version-count retention both implement this ADR's grace-period
semantics within their own lane's mechanics.

## Consequences

- Every subscription needs a grace-period configuration field and a pin flag on its
  tracked content rows; presets (ADR-0028) ship a sane default grace period per
  stack/generation, not an unbounded one.
- The alert model must distinguish "approaching grace-period end" from "past
  grace-period end, pending next sweep" from "pinned, sweep will skip" as three
  visibly different states, or an operator cannot tell an alert they can ignore from
  one that needs a pin or a purge-now decision.
- Orphan/out-of-scope surfacing is permanent UI/alert surface area, not a one-time
  migration concern — every subscription narrowing or deletion event grows this list,
  and nothing in this design ever shrinks it automatically.
- A lane whose retention shape predates this ADR (the ESX/patch lane's ported
  reconciliation) must map its own prune trigger onto "superseded within tracked
  scope" rather than pruning by its own independent rule, or the two retention models
  will disagree about what is safe to remove.
