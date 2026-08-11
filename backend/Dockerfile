# Waypoint.Api — multi-stage build. Build args populate the version/build metadata
# GET /api/v1/health reports; the compose stack (deploy/) supplies real values, unset
# args fall back to the WaypointBuildOptions "dev" defaults.
#
# Build context is the REPO ROOT, not backend/ (issue #92): .editorconfig and
# Directory.Build.props live at the repo root so a future non-backend .NET project can
# inherit the same analyzer/format posture (issue #55), and MSBuild discovers both by
# walking upward from the project directory. A backend/-scoped context put that walk's
# root at /src with no parent to climb to, so the containerized compile silently ran
# with SDK-default analyzer settings instead of this repo's posture — see issue #92 for
# the full history. Build from the repo root:
#   docker build -f backend/Dockerfile .
# `deploy/docker-compose.yml`'s backend service context is set the same way.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props .editorconfig ./
COPY backend/Waypoint.sln backend/
COPY backend/Waypoint.Core/Waypoint.Core.csproj backend/Waypoint.Core/
COPY backend/Waypoint.Runner/Waypoint.Runner.csproj backend/Waypoint.Runner/
COPY backend/Waypoint.Infrastructure/Waypoint.Infrastructure.csproj backend/Waypoint.Infrastructure/
COPY backend/Waypoint.Api/Waypoint.Api.csproj backend/Waypoint.Api/
COPY backend/Waypoint.ComplianceRunner/Waypoint.ComplianceRunner.csproj backend/Waypoint.ComplianceRunner/
COPY backend/Waypoint.DownloadRunner/Waypoint.DownloadRunner.csproj backend/Waypoint.DownloadRunner/
COPY backend/Waypoint.Tests/Waypoint.Tests.csproj backend/Waypoint.Tests/
WORKDIR /src/backend
RUN dotnet restore Waypoint.sln

WORKDIR /src
COPY backend/ backend/
WORKDIR /src/backend
RUN dotnet publish Waypoint.Api/Waypoint.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ARG BUILD_VERSION=0.0.0-dev
ARG BUILD_SHA=unknown
ARG BUILD_DATE=unknown
ENV Build__Version=${BUILD_VERSION} \
	Build__Sha=${BUILD_SHA} \
	Build__BuiltAt=${BUILD_DATE} \
	ASPNETCORE_URLS=http://+:8080 \
	ASPNETCORE_ENVIRONMENT=Production \
	DOTNET_EnableDiagnostics=0

# Runs as the non-root user the base image already provides.
USER app

COPY --from=build /app .

EXPOSE 8080

# The image reports its own health — the orchestrator only invokes it (see
# backend/README.md "Container health"). The aspnet runtime image ships no curl and no
# wget, so a shell-based probe cannot run here; `--health-check` makes the app itself do
# a loopback GET of /api/v1/health and exit 0 (healthy) or 1 (unhealthy), using nothing
# that is not already in the image.
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=3 \
	CMD ["dotnet", "Waypoint.Api.dll", "--health-check"]

ENTRYPOINT ["dotnet", "Waypoint.Api.dll"]
