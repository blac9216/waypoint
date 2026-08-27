# Copyright 2026 Justin Black
# Licensed under the Apache License, Version 2.0.

FROM quay.io/keycloak/keycloak:25.0

COPY --chown=1000:0 realm/ /opt/keycloak/data/import/
# Loads DB/bootstrap/client-secret passwords from mounted files before
# handing off to the image's own kc.sh entrypoint.
# why: docs/rationale/deploy.md#keycloak-wrapper-file-loading
COPY --chmod=0755 docker-entrypoint-wrapper.sh /opt/keycloak/bin/waypoint-entrypoint-wrapper.sh

ENTRYPOINT ["/opt/keycloak/bin/waypoint-entrypoint-wrapper.sh"]
