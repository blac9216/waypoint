# download-runner build context (ADR-0013/ADR-0015, issue #439).
#
# This Dockerfile installs PowerShell 7 and the project-owned scripts the
# `catalog-index` and `download` job handlers need (issue #439's M1 call
# graph: Invoke-WaypointCatalogIndex / Invoke-WaypointDownload, both shims
# over vcf-download-manager.common.ps1's Get-FileManifest / Save-WebFile).
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
# The .NET runner host that will load this PowerShell in-process
# (Microsoft.PowerShell.SDK, ADR-0013 decision 4) is issue #441 — this image
# is sources-only until that host exists and dedicated-runner Compose wiring
# (#442) replaces today's backend-container bind mount
# (WAYPOINT_VCF_SCRIPTS_DIR, see deploy/docker-compose.yml and
# deploy/README.md). Building this image today is a provenance/build-context
# proof, not a deployed service.
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

# Project-owned PowerShell only: the vendor script this runner's M1 call
# graph requires (vendor/) and the Waypoint-authored shim modules that
# dot-source it (modules/). No vendor installer packages, tokens, or
# credentials belong under powershell/ — see .dockerignore and the header
# comment above.
COPY powershell/ /app/PowerShell/

ENTRYPOINT ["pwsh"]
