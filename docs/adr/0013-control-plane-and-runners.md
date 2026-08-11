# ADR-0013: Separate the control plane from dedicated execution runners

Status: Accepted

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
