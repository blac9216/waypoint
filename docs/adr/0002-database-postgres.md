# ADR-0002: PostgreSQL for app data, catalogs, job queue, and Keycloak

Status: Accepted
Date: 2026-08-02

## Context

Waypoint needs storage for: product catalogs and artifact metadata (already JSON-shaped
in the predecessor repos), sites/targets/credentials, runs/jobs/results history,
versioned STIG config documents, and Keycloak's database. SQLite was considered for
appliance simplicity.

## Decision Drivers

_Backfilled under ADR-0027 from #4, #104._

- Keycloak (ADR-0004) requires a real database and supports Postgres best, so Postgres
  is in the stack regardless of what the catalog/job-queue data ends up using —
  consolidating on it beats running SQLite alongside Postgres.
- JSONB needs to fit the existing `catalog.json` / VCSP `lib.json`/`items.json` shapes
  without forcing rigid schemas on vendor-controlled formats; #4/#104 (schema v1)
  confirm this in the implementation, with `depot_artifacts.metadata` built as JSONB
  citing this ADR.
- Minimising the operational surface — one database engine to back up, monitor, and
  STIG-harden — rather than two.

## Considered Options

_Backfilled under ADR-0027 from #4, #104._

1. **One PostgreSQL instance for app data, catalogs, job queue, and Keycloak** (this
   decision) — one engine to operate, JSONB fits the existing catalog/artifact shapes,
   and consolidates with the database Keycloak already requires (ADR-0004); realised in
   #4/#104, which built `depot_artifacts.metadata` as JSONB and the relational tables
   for sites/targets/runs/jobs/users/credential metadata/config versions against this
   ADR.
2. **SQLite for appliance simplicity** — named in Context as considered, for the
   single-appliance deployment model. The sources record no issue or PR that compares
   it against Postgres on its own merits; the ADR's own Rationale explains the rejection
   only in terms of the drivers above (Keycloak already needing Postgres, and JSONB
   fitting the catalog shapes) rather than any specific weakness of SQLite that was
   evaluated.

## Decision

One PostgreSQL instance (16+), with separate databases for the app and Keycloak.

- **JSONB** columns for catalog/artifact metadata and other externally-shaped JSON.
- Relational tables for entities with real relationships (sites, targets, runs, jobs,
  users, credential metadata, config versions).
- The **job queue lives in Postgres** (`FOR UPDATE SKIP LOCKED`) — see ADR-0008.
- Encrypted secret blobs live in Postgres — see ADR-0005.

## Rationale

- Keycloak (ADR-0004) requires a real database and supports Postgres best, so Postgres
  is in the stack regardless; consolidating on it beats running SQLite + Postgres.
- JSONB indexes/queries fit the existing `catalog.json` / VCSP `lib.json`/`items.json`
  shapes without forcing rigid schemas on vendor-controlled formats.
- One database engine to back up, monitor, and STIG-harden.

## Consequences

- Backup/restore story = Postgres dump + volume snapshots; must be part of the
  appliance docs from day one.
- Queue-in-Postgres caps throughput far above our needs (dozens of concurrent targets,
  not millions of messages); if that ever changes, a broker is a new ADR.
