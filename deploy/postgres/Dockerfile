# Copyright 2026 Justin Black
# Licensed under the Apache License, Version 2.0.

FROM postgres:16-alpine

COPY initdb/ /docker-entrypoint-initdb.d/

# Issue #844: validate every mounted secret file BEFORE the stock entrypoint
# can run initdb, and assert both initdb scripts actually completed before
# reporting healthy. See each script's header comment for why the initdb
# scripts' own checks were not enough (they fire after the data directory
# already exists, which poisoned the pgdata volume).
COPY docker-entrypoint-wrapper.sh /usr/local/bin/waypoint-postgres-entrypoint.sh
COPY healthcheck.sh /usr/local/bin/waypoint-postgres-healthcheck.sh
RUN chmod 0755 /usr/local/bin/waypoint-postgres-entrypoint.sh \
	/usr/local/bin/waypoint-postgres-healthcheck.sh

# ENTRYPOINT resets any inherited CMD, so the base image's `postgres` command
# has to be restated here -- without it the wrapper would exec
# docker-entrypoint.sh with no arguments.
ENTRYPOINT ["/usr/local/bin/waypoint-postgres-entrypoint.sh"]
CMD ["postgres"]
