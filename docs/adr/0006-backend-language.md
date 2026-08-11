# ADR-0006: ASP.NET Core (C#) backend hosting PowerShell in-process

Status: Superseded by [ADR-0013](0013-control-plane-and-runners.md)

## Context

The domain logic — PowerCLI transports, InSpec orchestration, download workflows — is
mature PowerShell and stays (rewriting it would discard years of domain knowledge and
the vendor-supported automation surface). The web tier needs one additional language.
Candidates: TypeScript/Node end-to-end, Go, Python, C#/.NET. The maintainer's strongest
language is PowerShell.

## Decision

**ASP.NET Core (C#)** for the backend. The backend hosts PowerShell **in-process** via
the PowerShell SDK (`System.Management.Automation`): the job engine (ADR-0008) manages
runspace pools that execute the existing modules, with real .NET objects flowing between
C# and PowerShell — no child-process stdout parsing for the common path.

Exception: remediation keeps the child-`pwsh` process isolation from the predecessor
design, because unmodified vendor scripts call `Exit`.

## Rationale

- PowerShell *is* .NET — the gentlest possible learning curve from the maintainer's
  existing expertise, and the only option where the parallelism engine and the
  execution layer share a runtime.
- The alternative (Node backend shelling out to `pwsh`) is workable but strictly less
  capable at the PowerShell boundary; frontend remains TypeScript either way (ADR-0007).

## Consequences

- Two languages in the repo (C# + TS) plus retained PowerShell; CI needs all three
  toolchains.
- Runspace lifecycle management (pool sizing, module load, session state, PowerCLI
  connection reuse per site) becomes a core backend competency — design it early.
