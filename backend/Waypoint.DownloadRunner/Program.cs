// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.SystemState;
using Waypoint.DownloadRunner;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.DependencyInjection;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Runner.Jobs;
using Waypoint.Runner.Resources;
using ExecutionServiceCollectionExtensions = Waypoint.Infrastructure.Execution.DependencyInjection.ServiceCollectionExtensions;

// The container health probe (see HealthCheckProbe): reads the readiness marker
// ReadinessReportingHostedService writes and exits 0/1. Handled before any host or
// logging setup, mirroring Waypoint.Api's --health-check convention (see that type's
// doc comment) -- this must stay a cheap, side-effect-free, short-lived invocation.
if (HealthCheckProbe.IsHealthCheckInvocation(args))
{
	// A bespoke minimal configuration build, not the full host builder below: the
	// probe process must not stand up the dispatcher, PowerShell pool, or database
	// connections it is merely checking on.
	IConfigurationRoot probeConfiguration = new ConfigurationBuilder()
		.AddJsonFile("appsettings.json", optional: true)
		.AddEnvironmentVariables()
		.Build();
	DownloadRunnerOptions probeOptions = new();
	probeConfiguration.GetSection(DownloadRunnerOptions.SectionName).Bind(probeOptions);
	return HealthCheckProbe.Run(probeOptions.ReadinessFilePath, probeOptions.ReadinessMaxAge);
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<DownloadRunnerOptions>()
	.Bind(builder.Configuration.GetSection(DownloadRunnerOptions.SectionName));

// Issue #443: this host's composition is now two calls instead of one host-kind
// argument -- AddWaypointInfrastructure wires the control-plane repositories every
// host needs (Waypoint.Infrastructure, no PowerShell dependency), then
// AddWaypointExecution (Waypoint.Infrastructure.Execution, the project split #443
// introduced) adds the PowerShell host, job dispatcher, lease recovery, and event
// writer this runner actually executes with. Waypoint.Api calls only the first.
// Issue #843: resolve ConnectionStrings:Waypoint from its non-secret base value plus
// an optional mounted password file exactly once, before AddWaypointInfrastructure/
// AddWaypointExecution below (and the workerRegistryConnectionString read further
// down) ever read it -- see DatabaseConnectionStringResolver's doc comment for why one
// call site upstream of every reader is what makes the queue components, the readiness
// reporter, and the worker-registry writer all receive the identical resolved value.
// Nothing below may add a configuration source: the resolved value is written into
// the provider chain as it stands here, and adding a source rebuilds that chain and
// silently discards the write (see ResolveAndApply's doc comment).
DatabaseConnectionStringResolver.ResolveAndApply(builder.Configuration);

builder.Services.AddWaypointInfrastructure(builder.Configuration);
ExecutionServiceCollectionExtensions.AddWaypointExecution(builder.Services, builder.Configuration);

// Issue #441's mandatory capability registration (see JobHandlerRegistry's doc
// comment): overrides AddWaypointExecution's default JobCapabilities.All
// registration with this host's actual M1 allowlist. Registered after the call
// above so DI resolves this narrower registration last -- AddWaypointExecution still
// registers every IJobHandler (including the compliance-only ones), but the
// dispatcher only ever claims a job whose type is in AllowedJobTypes, so an
// unclaimed handler for a job type this host does not own is simply never resolved.
builder.Services.AddSingleton(serviceProvider => new JobHandlerRegistry(
	serviceProvider.GetServices<IJobHandler>().Where(handler => DownloadRunnerJobTypes.Allowed.Contains(handler.JobType)),
	DownloadRunnerJobTypes.Allowed));

// Issue #443: this runner's own worker_registry write side (see migration 0027 --
// only the reader is wired unconditionally by AddWaypointInfrastructure; a runner
// process is the only kind of host that ever writes this table).
string? workerRegistryConnectionString = builder.Configuration.GetConnectionString(
	Waypoint.Infrastructure.DependencyInjection.ServiceCollectionExtensions.ConnectionStringName);
if (!string.IsNullOrWhiteSpace(workerRegistryConnectionString))
{
	builder.Services.AddSingleton<IWorkerRegistryWriter>(new WorkerRegistryRepository(workerRegistryConnectionString));
}

builder.Services.AddHostedService<ReadinessReportingHostedService>();

IHost host = builder.Build();

// Fail fast and loud on a misconfigured managed-tool mount rather than starting a
// dispatcher that will only ever produce tool-absent failures for every download job
// -- this validates configuration shape, not tool presence (ADR-0015: presence is
// legitimately optional at startup and the tool-gated handler already fails each
// job clearly when it is missing).
ManagedToolOptions managedToolOptions = host.Services.GetRequiredService<IOptions<ManagedToolOptions>>().Value;
if (string.IsNullOrWhiteSpace(managedToolOptions.ToolStatePath))
{
	throw new InvalidOperationException(
		"ManagedTool:ToolStatePath must be configured (even if the directory is currently empty) -- " +
		"see ADR-0015 and ManagedToolOptions.");
}

// ADR-0018 (issue #555): fail readiness at startup rather than starting a runner that
// advertises a job type its own effective resource budget can never admit. Only
// evaluated when AddWaypointExecution actually wired execution (a runner started with
// no connection string registers neither of these singletons -- see that method's
// "no-op... no connection string" guard -- and has nothing to dispatch from anyway).
JobHandlerRegistry? jobHandlerRegistry = host.Services.GetService<JobHandlerRegistry>();
ResourceAdmissionController? resourceAdmission = host.Services.GetService<ResourceAdmissionController>();
if (jobHandlerRegistry is not null && resourceAdmission is not null)
{
	ResourceAdmissionInvariant.Validate(jobHandlerRegistry.AllowedJobTypes, resourceAdmission.EffectiveBudget);
}

host.Run();
return 0;
