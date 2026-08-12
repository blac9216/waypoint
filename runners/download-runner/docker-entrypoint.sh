#!/bin/sh
# Fixes ownership of externally-provisioned mount points before dropping to the
# unprivileged `app` user (issue #442). Compose named volumes (artifacts, depot,
# managed-tool) and host bind mounts (the master key file) are created/owned by
# root regardless of what the image's own Dockerfile chowns at build time --
# only content baked INTO the image (e.g. /app) survives a build-time chown;
# anything mounted at `docker run`/`docker compose up` time arrives root-owned
# every time, on every container (re)start. This container's own entrypoint
# therefore runs as root just long enough to chown the read-write mounts it
# actually needs to write to, then execs the real process as `app` (uid 1654,
# the same uid backend/Dockerfile's aspnet base ships, reused here rather than
# a second account -- see the Dockerfile) so the process itself never runs as
# root.
set -eu

if [ "$(id -u)" = '0' ]; then
	# Download artifact store (ADR-0014 §7 "managed tool/depot/content write
	# access") -- matches Downloads:ArtifactStorePath.
	[ -d /var/lib/waypoint/artifacts ] && chown app:app /var/lib/waypoint/artifacts
	# Offline depot share -- matches Catalog:DepotPath.
	[ -d /vcf ] && chown app:app /vcf
	# Operator-installed managed-tool state (ADR-0015 decision 3) -- matches
	# ManagedTool:ToolStatePath. A future install flow writes the
	# vcf-download-tool executable here; download jobs only read it, but the
	# mount itself must be writable for that future install step.
	[ -d /var/lib/waypoint/managed-tool ] && chown app:app /var/lib/waypoint/managed-tool

	exec su -s /bin/sh app -c 'exec "$0" "$@"' -- "$@"
fi

exec "$@"
