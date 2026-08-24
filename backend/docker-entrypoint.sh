#!/bin/sh
# Fixes ownership of the backend's one externally-provisioned read-write mount
# point before dropping to the unprivileged `app` user (issue #621, extending
# the same pattern runners/download-runner/docker-entrypoint.sh and
# runners/compliance-runner/docker-entrypoint.sh already use). Compose named
# volumes are created/owned by root regardless of what the image's own
# Dockerfile chowns at build time -- only content baked INTO the image
# survives a build-time chown; anything mounted at `docker compose up` time
# arrives root-owned every time, on every container (re)start. This
# container's own entrypoint therefore runs as root just long enough to chown
# the managed-tool staging mount, then execs the real process as `app` (uid
# 1654, the same uid the runners use) so the process itself never runs as
# root, exactly like backend/Dockerfile's previous plain `USER app` did before
# this issue -- that worked because the backend had no read-write mount at
# all; ManagedToolController.Upload's staging directory is its first one.
set -eu

if [ "$(id -u)" = '0' ]; then
	# Managed-tool staging (ADR-0015 decision 3, issue #621): the same
	# `managed-tool` named volume download-runner already mounts read-write
	# (deploy/docker-compose.yml) -- shared so a file this controller stages
	# under .../managed-tool/uploads is readable by the download-runner's
	# tool-install job once it claims the install job this controller queues.
	# Matches ManagedToolOptions.UploadStagingPath's parent.
	mkdir -p /var/lib/waypoint/managed-tool/uploads
	chown -R app:app /var/lib/waypoint/managed-tool

	exec su -s /bin/sh app -c 'exec "$0" "$@"' -- "$@"
fi

exec "$@"
