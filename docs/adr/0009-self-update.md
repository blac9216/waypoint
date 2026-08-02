# ADR-0009: Signed update bundles applied by a dedicated updater sidecar

Status: Accepted

## Context

The appliance should update itself from the UI. Half the deployments are air-gapped, so
registry polling (Watchtower-style) is out; updates arrive as operator-uploaded bundles.
Anything holding the Docker socket is root-equivalent on the host — a fact the
STIG-literate audience will scrutinize.

## Decision

- **Update bundle**: signed tarball — images (`docker save`), compose file, manifest
  with versions/checksums. Shares its signing/manifest format with transfer bundles
  (ADR-0010).
- **Flow**: UI upload → backend verifies signature + version compatibility → re-auth
  required → `docker load` → **updater sidecar** recreates changed services
  (`compose up -d` semantics) → per-service health-check gate → previous tags retained
  for one-command rollback. DB migrations run before the new backend takes traffic.
- **Privilege containment**: only the updater sidecar reaches the Docker socket, via a
  socket proxy allowlisting the needed API calls. The backend *requests* updates over
  an internal API; it never touches the socket.
- **Updater self-update**: the updater spawns a transient one-shot runner container to
  replace it (nothing being replaced performs the replacing).
- **Connected mode** may additionally check for update availability online; air-gapped
  instances only see version + upload.
- In the eventual OVA (ADR-0001), the same bundle is applied by a host-side systemd
  unit instead of the sidecar.

## Consequences

- Brief per-service downtime during updates — accepted appliance behavior (ADR-0001).
- The bundle format is load-bearing and versioned; build it before the in-UI apply
  (an `upgrade.sh` that consumes the same bundle is the v1 milestone).
- Key management for bundle signing (who signs releases, how keys are distributed to
  verify air-gapped) must be specified before first release.
