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
using Waypoint.ComplianceRunner.Readiness;
using Waypoint.Core.Jobs;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.DependencyInjection;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Runner.Jobs;
using ExecutionServiceCollectionExtensions = Waypoint.Infrastructure.Execution.DependencyInjection.ServiceCollectionExtensions;

// The container health probe (mirrors Waypoint.Api's --health-check, see
// RunnerHealthCheckProbe's doc comment): handled before any host/logging setup so it
// stays cheap, side-effect free, and does not require booting the dispatcher just to
// answer a liveness question.
if (RunnerHealthCheckProbe.IsHealthCheckInvocation(args))
{
	IConfiguration probeConfiguration = new ConfigurationBuilder()
		.AddJsonFile("appsettings.json", optional: true)
		.AddEnvironmentVariables()
		.Build();

	RunnerHealthOptions healthOptions = new();
	probeConfiguration.GetSection(RunnerHealthOptions.SectionName).Bind(healthOptions);

	return RunnerHealthCheckProbe.Run(healthOptions.ReportFilePath, healthOptions.MaxReportAge);
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// ADR-0013 §1/§2, issue #443: this process is the compliance-runner -- it hosts
// PowerShell, claims jobs, and executes discover/credential-test/scan directly. It is
// not the control plane (that is Waypoint.Api, which #443 removed execution from).
// AddWaypointInfrastructure wires the control-plane repositories and secret stores
// (Waypoint.Infrastructure, no PowerShell dependency); AddWaypointExecution
// (Waypoint.Infrastructure.Execution, the project #443 split out) adds the PowerShell
// runspace pool and IJobHandler registrations this process actually executes with. It
// also registers the tool-gated download handler, which this host simply never
// resolves: the JobHandlerRegistry override below narrows the allowlist to
// JobCapabilities.Compliance, so a download handler is excluded from what this
// process can claim (see that override).
builder.Services.AddWaypointInfrastructure(builder.Configuration);
ExecutionServiceCollectionExtensions.AddWaypointExecution(builder.Services, builder.Configuration);

builder.Services.AddOptions<RunnerHealthOptions>()
	.Bind(builder.Configuration.GetSection(RunnerHealthOptions.SectionName));

// Issue #443: this runner's own worker_registry write side (see migration 0027 --
// only the reader is wired unconditionally by AddWaypointInfrastructure; a runner
// process is the only kind of host that ever writes this table).
string? workerRegistryConnectionString = builder.Configuration.GetConnectionString(
	Waypoint.Infrastructure.DependencyInjection.ServiceCollectionExtensions.ConnectionStringName);
if (!string.IsNullOrWhiteSpace(workerRegistryConnectionString))
{
	builder.Services.AddSingleton<IWorkerRegistryWriter>(new WorkerRegistryRepository(workerRegistryConnectionString));
}

// Issue #440: this host's own mandatory capability registration (JobHandlerRegistry
// fails closed on an empty allowlist or a duplicate handler -- see its doc comment).
// AddWaypointInfrastructure (regardless of host kind) registered a JobHandlerRegistry
// keyed to JobCapabilities.All; this replaces that registration with the narrower
// JobCapabilities.Compliance allowlist
// and only the IJobHandler instances whose JobType belongs to it, so this process
// claims (ADR-0014 §1) and can resolve a handler for exactly discover/
// credential-test/scan/remediate -- never catalog-index or download.
builder.Services.AddSingleton(serviceProvider =>
{
	IEnumerable<IJobHandler> complianceHandlers = serviceProvider
		.GetServices<IJobHandler>()
		.Where(handler => JobCapabilities.Compliance.Contains(handler.JobType));
	return new JobHandlerRegistry(complianceHandlers, JobCapabilities.Compliance);
});

builder.Services.AddSingleton<ComplianceReadinessCheck>();
builder.Services.AddHostedService<RunnerHealthReportingHostedService>();

IHost host = builder.Build();

// This host claims and executes jobs directly (ADR-0014 §2); it never runs schema
// migrations -- Waypoint.Api's ApplyAsync-before-serving-traffic path
// (Waypoint.Api/Program.cs) remains the sole migrator so two processes never race the
// advisory lock/migration state on the same fresh deployment.
await host.RunAsync();

return 0;
