# Copyright 2026 Justin Black
# Licensed under the Apache License, Version 2.0.

FROM alpine:3.20

RUN apk add --no-cache openssl

COPY bootstrap.sh /usr/local/bin/waypoint-dev-bootstrap

ENTRYPOINT ["/usr/local/bin/waypoint-dev-bootstrap"]
