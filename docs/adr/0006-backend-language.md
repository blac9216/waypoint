# ADR-0006: ASP.NET Core (C#) backend hosting PowerShell in-process

Status: Superseded
Superseded-by: 0013
Date: 2026-08-02

## Context

The domain logic — PowerCLI transports, InSpec orchestration, download workflows — is
mature PowerShell and stays (rewriting it would discard years of domain knowledge and
the vendor-supported automation surface). The web tier needs one additional language.
Candidates: TypeScript/Node end-to-end, Go, Python, C#/.NET. The maintainer's strongest
language is PowerShell.

## Decision Drivers

_Backfilled under ADR-0027 from #6._ No separate issue or PR debates the language
choice — ADR-0006 was written directly into the repository's foundational commit,
which predates this repository's issue/PR-tracked workflow. The drivers below are
drawn from the ADR's own Context and Rationale text, corroborated by #6 (the issue
that operationalizes "host PowerShell in-process via `System.Management.Automation`"
as a real runspace-pool hosting service):

- The domain logic (PowerCLI transports, InSpec orchestration, download workflows) is
  mature PowerShell that must be retained, not rewritten — the web tier needs exactly
  one additional language, chosen to fit around that constraint.
- The execution layer and the web tier's chosen language should share a runtime, so
  PowerShell can be hosted in-process with real .NET objects flowing between the host
  and PowerShell, rather than parsing child-process stdout for the common path.
- The choice should match the maintainer's strongest existing language, minimizing the
  learning curve.

## Considered Options

_Backfilled under ADR-0027 from #6._ The sources record one alternative compared in
any depth — Node/TypeScript, named in Rationale below. Go and Python are named in
Context above as candidates, but the sources record no further comparison for either,
so they are listed without an invented rationale.

1. **ASP.NET Core (C#), hosting PowerShell in-process — chosen.** PowerShell *is*
   .NET, giving the gentlest learning curve from the maintainer's existing expertise
   and the only option where the execution layer and web tier share a runtime; #6
   confirms this in-process design was carried through into a real runspace-pool
   hosting service.
2. **TypeScript/Node end-to-end, shelling out to `pwsh`** — workable but strictly less
   capable at the PowerShell boundary than in-process hosting (per Rationale below);
   frontend stays TypeScript either way (ADR-0007).
3. **Go** — named as a candidate in Context above. The sources record no further
   comparison.
4. **Python** — named as a candidate in Context above. The sources record no further
   comparison.

## Decision

**ASP.NET Core (C#)** for the backend. The backend hosts PowerShell **in-process** via
the PowerShell SDK (`System.Management.Automation`): the job engine (ADR-0008) manages
runspace pools that execute the existing modules, with real .NET objects flowing between
C# and PowerShell — no child-process stdout parsing for the common path.

Exception: remediation keeps the child-`pwsh` process isolation from the predecessor
design, because project-owned PowerShell scripts imported from sibling repositories
call `Exit`.

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
