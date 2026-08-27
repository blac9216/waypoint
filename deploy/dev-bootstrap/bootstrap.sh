#!/bin/sh

set -eu

umask 077

if [ ! -s /run/waypoint-dev-secrets/waypoint-master-key ]; then
	openssl rand -hex 32 > /run/waypoint-dev-secrets/waypoint-master-key.tmp
	mv /run/waypoint-dev-secrets/waypoint-master-key.tmp /run/waypoint-dev-secrets/waypoint-master-key
fi

# Issue #844: generic tls.crt/tls.key filenames (not dev-cert.pem/dev-key.pem)
# -- deploy/nginx/conf.d/default.conf references these same generic names, so
# an operator overriding this dev-only pair with real certificates (see the
# commented-out bind mount on the nginx service in docker-compose.yml) needs
# no config change, only real file content at the same two names.
if [ ! -s /run/waypoint-dev-tls/tls.crt ] || [ ! -s /run/waypoint-dev-tls/tls.key ]; then
	rm -f /run/waypoint-dev-tls/tls.crt /run/waypoint-dev-tls/tls.key
	openssl req -x509 -nodes -newkey rsa:2048 \
		-keyout /run/waypoint-dev-tls/tls.key \
		-out /run/waypoint-dev-tls/tls.crt \
		-days 365 \
		-subj "/C=US/ST=Dev/L=Dev/O=Waypoint Dev/CN=localhost" \
		-addext "subjectAltName=DNS:localhost,IP:127.0.0.1" >/dev/null 2>&1
fi

mkdir -p \
	/opt/waypoint/profiles/vsphere \
	/opt/waypoint/profiles/nsx \
	/opt/waypoint/profiles/srg

# The application containers run as uid 1654. Secrets stay out of environment
# variables and logs, while remaining readable through their read-only mounts.
chown 1654:1654 /run/waypoint-dev-secrets/waypoint-master-key
chmod 600 /run/waypoint-dev-secrets/waypoint-master-key
chmod 600 /run/waypoint-dev-tls/tls.key
chmod 644 /run/waypoint-dev-tls/tls.crt
