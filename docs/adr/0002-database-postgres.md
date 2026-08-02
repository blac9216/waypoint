# ADR-0002: PostgreSQL for app data, catalogs, job queue, and Keycloak

Status: Accepted

## Context

Waypoint needs storage for: product catalogs and artifact metadata (already JSON-shaped
in the predecessor repos), sites/targets/credentials, runs/jobs/results history,
versioned STIG config documents, and Keycloak's database. SQLite was considered for
appliance simplicity.

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
