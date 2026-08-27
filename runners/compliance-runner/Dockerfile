# compliance-runner build context (issue #438/#442, ADR-0013, ADR-0014, ADR-0015).
#
# Scope: this Dockerfile provisions the toolchain the M2 discover/credential-test/
# scan call graph needs (see runners/compliance-runner/powershell/README.md for the
# exact sibling-function inventory): PowerShell 7 + PowerCLI/SsoAdmin for vCenter
# discovery and credential tests, and cinc-auditor (InSpec) + SAF CLI for scan/
# attest/convert -- AND the .NET host executable (Waypoint.ComplianceRunner) that
# claims/executes those jobs (ADR-0013 decision 3/4). It deliberately does NOT
# include the sibling vmware-stig-docker repo's remediation (Ansible), interactive
# shell (-Shell/glow), or STIG Manager watcher toolchains -- those are out of scope
# for this issue and belong to whichever later issue imports the remediate job
# (ADR-0013 lists remediate as "later").
#
# Two-stage build (issue #442 closes the gap #440 deferred): the runtime stage's OS
# is ubuntu:20.04, not a .NET SDK/runtime image, so the .NET host is compiled in a
# separate `dotnet-build` stage using the same mcr.microsoft.com/dotnet/sdk:8.0 image
# and repo-root build context backend/Dockerfile uses (issue #92's analyzer-posture
# reasoning applies identically here), then its publish output is copied into the
# ubuntu:20.04 runtime stage alongside the PowerShell/InSpec/SAF toolchain. This is
# the same "COPY --from=build" shape backend/Dockerfile already uses; the difference
# here is the runtime base is NOT a .NET image, so the published app runs on the
# self-contained-less framework-dependent publish plus the .NET runtime installed
# below via Microsoft's apt feed (already added for PowerShell).
#
# Build context is the REPO ROOT (matches backend/Dockerfile and deploy/docker-
# compose.yml's `context: ..` convention):
#   docker build -f runners/compliance-runner/Dockerfile .
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
RUN dotnet publish Waypoint.ComplianceRunner/Waypoint.ComplianceRunner.csproj -c Release -o /app --no-restore

FROM ubuntu:20.04

RUN apt-get -y update --fix-missing && \
    DEBIAN_FRONTEND=noninteractive apt-get -y install \
    ca-certificates wget bash-completion less curl gnupg2 \
    apt-transport-https software-properties-common openssh-client git

# PowerShell 7
RUN curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | apt-key add - && \
    echo "deb [arch=amd64] https://packages.microsoft.com/repos/microsoft-ubuntu-focal-prod focal main" \
    > /etc/apt/sources.list.d/microsoft.list && \
    apt-get update && \
    apt-get install -y powershell

# Disable pwsh's interactive-startup update-check/telemetry network calls -- the
# right posture for an air-gapped compliance tool (carried forward from the sibling
# repo's Dockerfile, issue #343).
ENV POWERSHELL_TELEMETRY_OPTOUT=1 \
    POWERSHELL_UPDATECHECK=Off

# Node.js LTS (for the SAF CLI)
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - && \
    apt-get install -y nodejs

# SAF CLI (MITRE Security Automation Framework) -- Invoke-WaypointAttest/-WaypointConvert
# shell out to `saf attest apply` / `saf convert hdf2ckl`.
RUN npm install -g @mitre/saf && \
    cd $(npm root -g)/@mitre/saf/node_modules/@mitre/emass_client && npm install axios

# Cinc-Auditor (InSpec-compatible scanner) + inspec alias -- Invoke-WaypointScan/
# -WaypointNsxScan/-WaypointSrgScan shell out to `inspec exec`.
RUN curl -L https://omnitruck.cinc.sh/install.sh | bash -s -- -P cinc-auditor && \
    ln -sf /opt/cinc-auditor/bin/cinc-auditor /usr/local/bin/inspec

# train-vmware InSpec transport plugin (extends the built-in vmware:// transport with
# VMware.vSphere.SsoAdmin support for SSO STIG controls). The sibling repo vendors a
# pinned 0.2.0 gem binary under bin/; that exact build is not published on rubygems.org
# (only 0.1.4 is), so rather than commit an unreproducible binary into this public repo
# this installs the latest publicly published release directly from the plugin's own
# gem source at build time (consistent with ADR-0015: operators build from public
# sources, the project does not vendor binaries it cannot reproduce).
RUN cinc-auditor plugin install train-vmware

# PowerCLI + SsoAdmin modules -- Connect-StigVIServer (module.transport.vmware.ps1)
# and the discovery/credential-test shims depend on these.
RUN pwsh -NoProfile -NonInteractive -Command " \
    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted; \
    Install-Module -Name VMware.PowerCLI -Scope AllUsers -Force -AcceptLicense; \
    Install-Module -Name VMware.vSphere.SsoAdmin -Scope AllUsers -Force -AcceptLicense; \
    Set-PowerCLIConfiguration -Scope AllUsers -ParticipateInCeip \$false -Confirm:\$false; \
    Set-PowerCLIConfiguration -Scope AllUsers -InvalidCertificateAction Ignore -Confirm:\$false"

# Fail the build outright (not silently at runtime) if the installed PowerCLI module
# set is broken or partial -- same guard the sibling repo's Dockerfile applies
# (issue #307): cmdlet AUTOLOAD only pulls in the handful of submodules touched by the
# first invoked cmdlet, so a partial install can still import cleanly and only fail
# later, at scan time, on an untouched submodule.
RUN pwsh -NoProfile -NonInteractive -Command " \
    Import-Module VMware.PowerCLI -ErrorAction Stop; \
    if ((Get-Module VMware.*).Count -lt 50) { throw 'PowerCLI import incomplete' }"

# .NET 8 ASP.NET Core runtime -- the dotnet-build stage's publish output is
# framework-dependent, not self-contained, so the runtime (not the SDK) must be
# present here too. Reuses the Microsoft apt feed already added above for
# PowerShell; no extra key/source needed.
RUN apt-get install -y --no-install-recommends aspnetcore-runtime-8.0 && \
    rm -rf /var/lib/apt/lists/*

# Project-owned PowerShell imported from the sibling vmware-stig-docker repo (see
# powershell/README.md for provenance and exactly which files/functions this M2 slice
# needs). Mounted at /vmware-stig-docker-scripts by deploy/compose.yaml so the
# WAYPOINT_VMWARE_STIG_DOCKER_*_PATH env vars below match; also copied here under
# /powershell/ so the image is self-verifying in isolation (`docker build` alone,
# no compose) for the toolchain-provenance check this file has always run.
COPY runners/compliance-runner/powershell/ /powershell/

# .NET host publish output (issue #442, closing the gap #440 left open -- see header
# comment). Includes Waypoint.ComplianceRunner.dll and the Waypoint-owned shim
# modules Waypoint.Infrastructure.csproj copies into the publish output
# (PowerShell/Modules/Waypoint{Discovery,Scan,CredentialTest}/*.psm1) -- those dot-
# source the project-owned .ps1 files under /vmware-stig-docker-scripts at runtime (see the
# WAYPOINT_VMWARE_STIG_DOCKER_*_PATH env vars deploy/compose.yaml sets).
COPY --from=dotnet-build /app /app

WORKDIR /app

# Non-root: create a dedicated app user rather than relying on a base-image default
# (ubuntu:20.04 has none), matching backend/Dockerfile's non-root posture. The master
# key mount (deploy/compose.yaml) must be readable by this uid -- see
# deploy/README.md's master-key file-permission note, extended to all three trusted
# services by this issue.
RUN useradd --system --create-home --shell /usr/sbin/nologin --uid 1654 waypoint && \
    chown -R waypoint:waypoint /app

# The container still STARTS as root (no USER here) because Compose named
# volumes and bind mounts always arrive root-owned regardless of what this
# Dockerfile chowns at build time -- docker-entrypoint.sh fixes ownership of
# this service's read-write mounts (artifacts) and then drops to `waypoint`
# via `su` before exec'ing the real process, which is what actually runs as
# non-root. See that script's header comment for the full mechanism.
COPY runners/compliance-runner/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

ENV DOTNET_EnableDiagnostics=0

# The host reports its own readiness via a file sentinel, not a loopback HTTP call
# (ADR-0013 decision 1: a runner has no REST/SSE surface) -- see RunnerHealthReportingHostedService
# and RunnerHealthCheckProbe's doc comments. deploy/compose.yaml invokes this
# exact command as the service's Compose healthcheck. Runs as root (no USER switch
# for HEALTHCHECK), which can always read the report file the `waypoint`-owned
# process writes.
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3 \
	CMD ["dotnet", "Waypoint.ComplianceRunner.dll", "--health-check"]

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "Waypoint.ComplianceRunner.dll"]
