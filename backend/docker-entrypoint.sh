#!/bin/sh
# Fixes ownership of the backend's one externally-provisioned read-write mount
# point before dropping to the unprivileged `app` user (issue #621, re-scoped
# per #630 review; extends the same pattern
# runners/download-runner/docker-entrypoint.sh and
# runners/compliance-runner/docker-entrypoint.sh already use). Compose named
# volumes are created/owned by root regardless of what the image's own
# Dockerfile chowns at build time -- only content baked INTO the image
# survives a build-time chown; anything mounted at `docker compose up` time
# arrives root-owned every time, on every container (re)start. This
# container's own entrypoint therefore runs as root just long enough to chown
# the upload-staging mount, then execs the real process as `app` (uid 1654,
# the same uid the runners use) so the process itself never runs as root,
# exactly like backend/Dockerfile's previous plain `USER app` did before this
# issue -- that worked because the backend had no read-write mount at all;
# ManagedToolController.Upload's staging directory is its first one.
#
# The backend deliberately mounts ONLY the dedicated `tool-upload-staging`
# volume, never the `managed-tool` tool store -- so this chown never touches
# the verified tool binary or the RSA release-key trust anchor (ADR-0014 §7,
# issue #442 AC5, #570).
set -eu

if [ "$(id -u)" = '0' ]; then
	# Manual-upload staging (issue #621): the `tool-upload-staging` named
	# volume, mounted read-write into BOTH this backend and the download-runner
	# (deploy/compose.yaml) -- shared so a file this controller stages
	# under it is readable by the download-runner's tool-install job once it
	# claims the install job this controller queues. Matches
	# ManagedToolOptions.UploadStagingPath. Scoped to the staging mount only.
	mkdir -p /var/lib/waypoint/tool-upload-staging
	chown -R app:app /var/lib/waypoint/tool-upload-staging

	exec su -s /bin/sh app -c 'exec "$0" "$@"' -- "$@"
fi

exec "$@"
