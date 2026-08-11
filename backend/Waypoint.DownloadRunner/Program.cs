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
using Waypoint.DownloadRunner;
using Waypoint.Infrastructure.DependencyInjection;
using Waypoint.Runner.Jobs;

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

// Issue #441: reuse Waypoint.Infrastructure's whole composition root
// (repositories, PowerShell host, job dispatcher, lease recovery, event writer) via
// the ExecutionOnly host kind, which skips the two hosted services that are
// ADR-0013 §1 control-plane concerns this runner has no surface for (SSE fan-out,
// run-secret cleanup) -- see ServiceCollectionExtensions' doc comment.
builder.Services.AddWaypointInfrastructure(builder.Configuration, WaypointInfrastructureHostKind.ExecutionOnly);

// Issue #441's mandatory capability registration (see JobHandlerRegistry's doc
// comment): overrides AddWaypointInfrastructure's default JobCapabilities.All
// registration with this host's actual M1 allowlist. Registered after the call
// above so DI resolves this narrower registration last -- AddWaypointInfrastructure
// still registers every IJobHandler (including the compliance-only ones), but the
// dispatcher only ever claims a job whose type is in AllowedJobTypes, so an
// unclaimed handler for a job type this host does not own is simply never resolved.
builder.Services.AddSingleton(serviceProvider => new JobHandlerRegistry(
	serviceProvider.GetServices<IJobHandler>().Where(handler => DownloadRunnerJobTypes.Allowed.Contains(handler.JobType)),
	DownloadRunnerJobTypes.Allowed));

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

host.Run();
return 0;
