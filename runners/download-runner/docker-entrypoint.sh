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
	# Offline depot share -- matches Catalog:DepotPath. A real depot is
	# frequently bind-mounted read-only (an NFS/SMB vendor export); catalog-
	# index only ever reads it, so chown it only when a write actually
	# succeeds -- `-w` is unreliable for root/read-only bind mounts, so probe
	# with a real write instead.
	# why: docs/rationale/deploy.md#depot-chown-write-probe
	if [ -d /vcf ]; then
		if touch /vcf/.waypoint-write-probe 2>/dev/null; then
			rm -f /vcf/.waypoint-write-probe
			chown app:app /vcf
		else
			echo 'docker-entrypoint: /vcf (depot) is read-only; skipping chown' >&2
		fi
	fi
	# Content-library registry: its own volume, nested at /vcf/ContentLibrary
	# so the runner's existing store-path conventions are unchanged -- a
	# distinct mount point, so it arrives root-owned independently of /vcf.
	# why: docs/rationale/deploy.md#content-libraries-own-volume
	[ -d /vcf/ContentLibrary ] && chown app:app /vcf/ContentLibrary
	# Operator-installed managed-tool state (ADR-0015 decision 3) -- matches
	# ManagedTool:ToolStatePath. A future install flow writes the
	# vcf-download-tool executable here; download jobs only read it, but the
	# mount itself must be writable for that future install step.
	[ -d /var/lib/waypoint/managed-tool ] && chown app:app /var/lib/waypoint/managed-tool

	# shellcheck disable=SC2016 # $0/$@ are the inner sh's positional args, deliberately unexpanded here
	exec su -s /bin/sh app -c 'exec "$0" "$@"' -- "$@"
fi

exec "$@"
