# ADR-0013: Separate the control plane from dedicated execution runners

Status: Accepted
Supersedes: 0006
Amends: 0008
Amended-by: 0017
Date: 2026-08-11

Supersedes [ADR-0006](0006-backend-language.md) and the backend-hosted worker portion
of [ADR-0008](0008-job-engine.md).

## Context

ADR-0006 placed the ASP.NET API, job dispatcher, PowerShell runspace pools, and every
execution dependency in one backend container. The M1/M2 implementation proved the
queue, lease, event, and PowerShell-hosting mechanics, but the deployed backend image
remained a minimal ASP.NET image. A functional appliance also needs different and
substantial toolchains: PowerCLI, InSpec, SAF, and compliance content for compliance
work; the download modules, depot storage, and an operator-installed entitled download
tool for content work.

Combining those responsibilities makes the API container both the control plane and
an execution environment, couples unrelated dependency lifecycles, expands its
security boundary, and makes a fresh Compose deployment appear healthy while being
unable to execute its principal workflows. The intended product shape is a control
plane dispatching to execution containers dedicated to their domains.

## Decision Drivers

_Backfilled under ADR-0027 from #431, #432._ #431 requests recording the
owner-approved move from backend-hosted execution to dedicated runner services and
lists the documents whose Context/Decision this ADR's own text draws on; #432 is the
PR that authored this ADR (commit 95163887, "AI: document dedicated runner
architecture"). No separate issue or PR debates the drivers themselves — they are
drawn from this ADR's own Context and Rationale, which #431/#432 record as the
approved outcome.

- The API container must not double as an execution environment: PowerCLI, InSpec,
  SAF, and compliance content for compliance work, and the download modules, depot
  storage, and an operator-installed entitled download tool for content work, are
  substantial and unrelated toolchains that ADR-0006's single combined backend
  container forced into one image.
- Combining those responsibilities couples unrelated dependency lifecycles, expands
  the control plane's security boundary, and lets a freshly deployed Compose stack
  appear healthy while being unable to execute its principal workflows.
- The C# concurrency, lease, cancellation, and database mechanics already
  implemented for the job engine (ADR-0008) should be retained rather than
  rebuilt, while PowerShell — where the product's domain knowledge lives — should
  stay the execution language (per ADR-0006).
- Health/readiness reporting must be able to distinguish control-plane health from
  runner availability and capability, which a single combined container cannot
  express.

## Considered Options

_Backfilled under ADR-0027 from #431, #432._ The sources record the chosen shape and
its rejected predecessor explicitly (Context above); they do not record a distinct
third alternative, so none is invented here.

1. **Separate control plane and dedicated execution runners** (chosen) — `Waypoint.Api`
   keeps REST/SSE, auth, validation, enqueueing, run controls, queries, and result
   serving; `compliance-runner` and `download-runner` are long-lived .NET worker
   services that claim jobs and host PowerShell in-process via
   `Microsoft.PowerShell.SDK`. Each runner's dependency stack (PowerCLI/InSpec/SAF or
   the download tool/depot storage) lives only in that runner's image. Realised by
   #431/#432, which recorded this as the approved architecture, and ADR-0014/ADR-0015,
   which detail runner job ownership and the operator-export packaging model this
   decision assumes.
2. **Keep the single combined backend container** — the ADR-0006/ADR-0008 status quo,
   named in Context above: one ASP.NET image hosting the job dispatcher, PowerShell
   runspace pools, and every execution dependency. Rejected: it couples unrelated
   toolchain lifecycles into one image, expands the control plane's security
   boundary, and allows the container to report healthy without being able to run
   its principal workflows.

## Decision

1. **ASP.NET is the control plane only.** `Waypoint.Api` owns REST/SSE, authentication
   and authorization, validation, enqueueing, run controls, queries, and result
   serving. It does not host PowerShell, claim jobs, or execute domain tools.

2. **Compose includes two long-lived runner services:**
   - `compliance-runner`: `discover`, `credential-test`, `scan`, and later
     `remediate` jobs.
   - `download-runner`: `catalog-index`, `download`, and later content-library,
     repository, and managed-content jobs.

3. **Runners are .NET worker services, not additional web APIs.** A shared C# runner
   library owns reliable worker mechanics; each small executable runner project
   registers its allowed job types, handlers, dependencies, and configuration through
   .NET dependency injection and the Generic Host.

4. **PowerShell remains the domain-operation language.** Each runner hosts PowerShell
   in-process through `Microsoft.PowerShell.SDK`, preserving typed parameter binding,
   real `PSObject` results, runspace pools, stream capture, and cooperative
   cancellation. Project-owned Dockerfiles and PowerShell from the two predecessor
   repositories move into the relevant runner build contexts. A child `pwsh` remains
   permitted where process isolation is required, including remediation code that may
   call `Exit`.

5. **Extensibility is compile-time and additive.** A future execution domain adds a
   runner executable/image that references the shared runner library and registers
   new handlers. Runtime-loaded plugin assemblies are not part of this decision.

6. **The design is replica-safe but starts with one instance of each runner.** Worker
   identities and queue semantics must support multiple identical replicas, but the
   default Compose topology does not multiply services without measured need.

## Rationale

- The API image has one coherent purpose and no execution-tool dependency stack.
- Compliance and download dependencies can evolve, be tested, and fail independently.
- C# retains the difficult concurrency, lease, cancellation, and database mechanics
  already implemented, while PowerShell remains where the product's domain knowledge
  lives.
- The existing ASP.NET choice still fits REST, RBAC, SSE, and shared typed contracts;
  separating deployable processes does not require a new language or a serialized
  C#-to-PowerShell protocol.
- Long-lived Compose services avoid giving the backend a Docker socket and avoid the
  lifecycle, security, and recovery problems of spawning one container per job.

## Consequences

- The existing dispatcher, handler registry, PowerShell executor, and execution
  handlers must move out of `Waypoint.Api`/the backend deployment into shared runner
  infrastructure and domain runner projects.
- Runner images become larger and domain-specific; the API image becomes smaller and
  can be hardened independently.
- Health checks must distinguish API health from runner availability/capability.
- A healthy control plane does not by itself prove that every runner is installed and
  ready; system status must report runner capability and readiness explicitly.
- ADR-0006 remains the historical record of why C# and in-process PowerShell were
  chosen, but its backend-process placement is no longer the design.
