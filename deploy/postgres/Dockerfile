# Copyright 2026 Justin Black
# Licensed under the Apache License, Version 2.0.

FROM postgres:16-alpine

COPY initdb/ /docker-entrypoint-initdb.d/

# Validate every mounted secret file BEFORE the stock entrypoint can run
# initdb, and assert both initdb scripts completed before reporting healthy.
# why: docs/rationale/deploy.md#postgres-poisoned-volume-fail-closed
COPY docker-entrypoint-wrapper.sh /usr/local/bin/waypoint-postgres-entrypoint.sh
COPY healthcheck.sh /usr/local/bin/waypoint-postgres-healthcheck.sh
RUN chmod 0755 /usr/local/bin/waypoint-postgres-entrypoint.sh \
	/usr/local/bin/waypoint-postgres-healthcheck.sh

# ENTRYPOINT resets any inherited CMD, so the base image's `postgres` command
# has to be restated here -- without it the wrapper would exec
# docker-entrypoint.sh with no arguments.
ENTRYPOINT ["/usr/local/bin/waypoint-postgres-entrypoint.sh"]
CMD ["postgres"]
