# download-runner build context (ADR-0013/ADR-0014/ADR-0015, issue #439/#442).
#
# This Dockerfile installs PowerShell 7, the .NET host executable
# (Waypoint.DownloadRunner) that claims and executes `catalog-index`/`download`
# jobs (ADR-0013 decision 3/4), and the project-owned scripts those job handlers
# need (issue #439's M1 call graph: Invoke-WaypointCatalogIndex /
# Invoke-WaypointDownload, both shims over vcf-download-manager.common.ps1's
# Get-FileManifest / Save-WebFile).
#
# What this image deliberately does NOT contain (ADR-0015 — entitlement-
# restricted tools are operator-installed managed state, never baked into a
# project-built image):
#   - the account-gated vcf-download-tool executable
#   - the standalone UMDS installer
#   - any depot activation code, download token, or downloadConfig.xml
#   - any operator credential or lab-specific configuration
# Those install through the appliance at runtime onto a persistent volume
# (ADR-0015 decision 3), not through this Dockerfile. Do not add a COPY of
# any vendor installer package or credential file to this build context.
#
# Two-stage build, closing the gap issue #441 deferred (its own header note:
# "Compose wiring is #442's job"): a `dotnet-build` stage compiles
# Waypoint.DownloadRunner the same way backend/Dockerfile compiles Waypoint.Api
# (repo-root context, same COPY/restore/publish shape, issue #92's analyzer-
# posture reasoning applies identically), then its publish output is copied into
# the runtime stage below alongside PowerShell 7. The runtime stage keeps the SDK
# base image (not aspnet/runtime-only) because it hosts PowerShell via
# Microsoft.PowerShell.SDK in-process (ADR-0013 decision 4), which needs the same
# base OS package set this image already installs pwsh onto -- switching to a
# leaner runtime-only .NET base is not being done in this issue to avoid
# reintroducing the two-base-image problem the compliance-runner's own Dockerfile
# comment describes.
#
# Build context is the REPO ROOT (matches backend/Dockerfile and deploy/docker-
# compose.yml's `context: ..` convention):
#   docker build -f runners/download-runner/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build
WORKDIR /src

COPY Directory.Build.props .editorconfig ./
COPY backend/Waypoint.sln backend/
COPY backend/Waypoint.Core/Waypoint.Core.csproj backend/Waypoint.Core/
COPY backend/Waypoint.Runner/Waypoint.Runner.csproj backend/Waypoint.Runner/
COPY backend/Waypoint.Infrastructure/Waypoint.Infrastructure.csproj backend/Waypoint.Infrastructure/
COPY backend/Waypoint.Infrastructure.Execution/Waypoint.Infrastructure.Execution.csproj backend/Waypoint.Infrastructure.Execution/
COPY backend/Waypoint.Api/Waypoint.Api.csproj backend/Waypoint.Api/
COPY backend/Waypoint.ComplianceRunner/Waypoint.ComplianceRunner.csproj backend/Waypoint.ComplianceRunner/
COPY backend/Waypoint.DownloadRunner/Waypoint.DownloadRunner.csproj backend/Waypoint.DownloadRunner/
COPY backend/Waypoint.Tests/Waypoint.Tests.csproj backend/Waypoint.Tests/
WORKDIR /src/backend
RUN dotnet restore Waypoint.sln

WORKDIR /src
COPY backend/ backend/
WORKDIR /src/backend
RUN dotnet publish Waypoint.DownloadRunner/Waypoint.DownloadRunner.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS runtime

RUN apt-get -y update --fix-missing && \
    DEBIAN_FRONTEND=noninteractive apt-get -y install --no-install-recommends \
    ca-certificates apt-transport-https software-properties-common curl && \
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | tee /etc/apt/trusted.gpg.d/microsoft.asc && \
    echo "deb [arch=amd64] https://packages.microsoft.com/repos/microsoft-ubuntu-jammy-prod jammy main" > /etc/apt/sources.list.d/microsoft.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends powershell && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Project-owned PowerShell script only: vcf-download-manager.common.ps1,
# the sibling-repo import this runner's M1 call graph dot-sources (project/). No
# vendor installer packages, tokens, or credentials belong under powershell/ —
# see .dockerignore and the header comment above.
#
# The Waypoint-authored WaypointCatalogIndex/WaypointDownload shim modules are
# NOT copied from here — they live once, under backend/Waypoint.Infrastructure.
# Execution/PowerShell/Modules/, and reach /app/PowerShell/Modules/ only via
# that project's CopyToPublishDirectory (issue #456) in the COPY below. Do not
# reintroduce a second on-disk copy under runners/download-runner/powershell/.
COPY runners/download-runner/powershell/ /app/PowerShell/

# .NET host publish output (issue #442, closing the gap #441 left open -- see
# header comment). Includes Waypoint.DownloadRunner.dll and the Waypoint-owned
# WaypointCatalogIndex/WaypointDownload shim modules Waypoint.Infrastructure.
# Execution.csproj copies into the publish output at PowerShell/Modules/ (the
# single canonical source, issue #456); at runtime they dot-source
# vcf-download-manager.common.ps1 under /app/PowerShell/project (see
# WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH in deploy/compose.yaml).
COPY --from=dotnet-build /app /app

# Non-root: mcr.microsoft.com/dotnet/sdk:8.0 already ships the same `app`
# user (uid 1654) as mcr.microsoft.com/dotnet/aspnet:8.0 (backend/Dockerfile's
# base) -- reuse it rather than useradd a second uid-1654 account, which
# fails outright ("UID 1654 is not unique"). Same uid as backend and
# compliance-runner either way, so the master key mount (deploy/docker-
# compose.yml) works under the one file-permission note in deploy/README.md
# "Bring-up" step 4, extended to all three trusted services by this issue.
RUN chown -R app:app /app

# The container still STARTS as root (no USER here) because Compose named
# volumes and bind mounts always arrive root-owned regardless of what this
# Dockerfile chowns at build time -- docker-entrypoint.sh fixes ownership of
# this service's read-write mounts (artifacts, depot, managed-tool) and then
# drops to `app` via `su` before exec'ing the real process, which is what
# actually runs as non-root. See that script's header comment for the full
# mechanism.
COPY runners/download-runner/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

ENV DOTNET_EnableDiagnostics=0

# The host reports its own readiness via a file sentinel, not a loopback HTTP call
# (ADR-0013 decision 1: a runner has no REST/SSE surface) -- see
# ReadinessReportingHostedService and HealthCheckProbe's doc comments.
# deploy/compose.yaml invokes this exact command as the service's Compose
# healthcheck. Runs as root (no USER switch for HEALTHCHECK), which can always
# read the readiness file the `app`-owned process writes.
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3 \
	CMD ["dotnet", "Waypoint.DownloadRunner.dll", "--health-check"]

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "Waypoint.DownloadRunner.dll"]
