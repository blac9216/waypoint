#!/bin/sh
# Fixes ownership of externally-provisioned mount points before dropping to the
# unprivileged `waypoint` user (issue #442). Compose named volumes (artifacts,
# compliance-profiles) and host bind mounts (the master key file) are created/
# owned by root regardless of what the image's own Dockerfile chowns at build
# time -- only content baked INTO the image (e.g. /app) survives a build-time
# chown; anything mounted at `docker run`/`docker compose up` time arrives
# root-owned every time, on every container (re)start. This container's own
# entrypoint therefore runs as root just long enough to chown the read-write
# mount(s) it actually needs to write to, then execs the real process as
# `waypoint` (uid 1654, matching backend/download-runner) so the process itself
# never runs as root.
#
# Read-only mounts (compliance-profiles content) are deliberately left alone --
# chowning something the process never writes to would silently mask a real
# "not actually mounted read-write" misconfiguration.
set -eu

if [ "$(id -u)" = '0' ]; then
	# Scan artifact store (ADR-0014 §7 "scan-artifact write access") --
	# read-write for this service specifically. ScanOptions.ArtifactStorePath
	# defaults to a `scans/` subdirectory of the shared `artifacts` named
	# volume (deploy/docker-compose.yml), which does not exist until something
	# creates it -- `mkdir -p` here rather than relying on the app to create
	# its own working directory, so ComplianceReadinessCheck's writability
	# probe (which requires the directory to already exist) passes on a fresh
	# volume instead of reporting "does not exist or is not mounted" forever.
	mkdir -p /var/lib/waypoint/artifacts/scans
	chown -R waypoint:waypoint /var/lib/waypoint/artifacts

	exec su -s /bin/sh waypoint -c 'exec "$0" "$@"' -- "$@"
fi

exec "$@"
