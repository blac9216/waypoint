# Copyright 2026 Justin Black
# Licensed under the Apache License, Version 2.0.

FROM alpine:3.20

RUN apk add --no-cache curl jq

COPY --chmod=0755 provision.sh /usr/local/bin/waypoint-keycloak-dev-admin

ENTRYPOINT ["/usr/local/bin/waypoint-keycloak-dev-admin"]
