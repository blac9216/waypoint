# compliance-runner build context (issue #438, ADR-0013, ADR-0015).
#
# Scope: this Dockerfile provisions ONLY the toolchain the M2 discover/credential-test/
# scan call graph needs (see runners/compliance-runner/powershell/README.md for the
# exact vendor-function inventory): PowerShell 7 + PowerCLI/SsoAdmin for vCenter
# discovery and credential tests, and cinc-auditor (InSpec) + SAF CLI for scan/
# attest/convert. It deliberately does NOT include the sibling vmware-stig-docker
# repo's remediation (Ansible), interactive shell (-Shell/glow), or STIG Manager
# watcher toolchains -- those are out of scope for this issue and belong to whichever
# later issue imports the remediate job (ADR-0013 lists remediate as "later").
#
# This is a standalone Dockerfile, not yet wired into deploy/docker-compose.yml
# (issue #438 explicitly does not rewire runtime consumption -- see #440/#442).
#
# Issue #440 added the .NET host executable this image will eventually run
# (backend/Waypoint.ComplianceRunner, referencing the shared Waypoint.Runner library
# and Waypoint.Infrastructure's discover/credential-test/scan handlers) but did not
# extend this Dockerfile with a `dotnet publish` stage: this build context uses
# ubuntu:20.04 as its base (the compliance toolchain's own OS/package requirements,
# not a .NET SDK/runtime image), so adding the host means either a multi-stage build
# with a second base image or restructuring this image's base entirely -- a decision
# that belongs with #442's Compose/image-topology wiring, not this issue's handler
# composition-root scope. Building this image today remains a toolchain provenance
# proof; running `Waypoint.ComplianceRunner.dll` inside it lands with #442.
FROM ubuntu:20.04

RUN apt-get -y update --fix-missing && \
    DEBIAN_FRONTEND=noninteractive apt-get -y install \
    ca-certificates wget bash-completion less curl gnupg2 \
    apt-transport-https software-properties-common openssh-client

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

# Project-owned PowerShell imported from the sibling vmware-stig-docker repo (see
# powershell/README.md for provenance and exactly which files/functions this M2 slice
# needs) plus the Waypoint-owned shim modules that dot-source them
# (backend/Waypoint.Infrastructure/PowerShell/Modules/Waypoint{Discovery,Scan,
# CredentialTest}) are NOT copied into this image yet -- deploy/docker-compose.yml
# still mounts the shim modules from the backend image and the vendor scripts from a
# host bind mount (issue #395/#427). Wiring this build context into that runtime path
# is later work (#440/#442); this Dockerfile only proves the toolchain builds clean.
COPY powershell/ /powershell/

RUN rm -rf /tmp/* /var/lib/apt/lists/*
