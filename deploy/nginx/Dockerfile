# Copyright 2026 Justin Black
# Licensed under the Apache License, Version 2.0.

FROM node:22.19-alpine AS frontend-build
WORKDIR /src/frontend

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build

FROM nginx:1.27-alpine

COPY deploy/nginx/conf.d/ /etc/nginx/conf.d/
COPY --from=frontend-build /src/frontend/dist/ /usr/share/nginx/html/
