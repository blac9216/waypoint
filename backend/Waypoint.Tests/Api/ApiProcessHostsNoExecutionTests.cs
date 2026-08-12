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

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Jobs;
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #443's central acceptance criterion, proved two ways at once: "The API
/// process has no dispatcher, lease-recovery loop, PowerShell host, or domain job
/// handlers." A composition-registration test alone (like the ones in
/// <c>Waypoint.Tests.Infrastructure.JobEngineWiringTests</c>) only proves nothing is
/// *wired*, which config drift could reverse. This test proves the stronger, load-bearing
/// property the issue explicitly calls out -- "reactivation must be structurally
/// impossible".
/// </summary>
public sealed class ApiProcessHostsNoExecutionTests
{
	/// <summary>
	/// The shipped-output proof, and the one that actually matters: even though
	/// Waypoint.Api.csproj declares no ProjectReference to Waypoint.Infrastructure.Execution,
	/// a naive assembly-reference check (<see cref="Assembly.GetReferencedAssemblies"/>)
	/// is NOT sufficient to prove that -- MSBuild's transitive-reference-copy behavior
	/// puts a referenced project's DLL into the OUTPUT directory regardless of whether
	/// any IL in the referencing assembly actually calls into it (a project reference
	/// with zero type usage still copies; verified empirically while writing this test:
	/// temporarily adding the ProjectReference back left `GetReferencedAssemblies()`
	/// unchanged but put Waypoint.Infrastructure.Execution.dll and the PowerShell SDK's
	/// DLLs straight into Waypoint.Api's own bin/ directory). The Docker image is built
	/// from that output directory (`dotnet publish`), not from CLR reflection metadata --
	/// so THIS is the check that actually reflects what ships. It walks the API project's
	/// own build output folder (not the shared Waypoint.Tests output, which legitimately
	/// carries every project including Execution) and fails if the execution assembly or
	/// any PowerShell SDK assembly is present there at all.
	/// </summary>
	[Fact]
	public void ApiOutputDirectory_ContainsNoExecutionAssemblyOrPowerShellSdk()
	{
		string apiOutputDirectory = ResolveApiOutputDirectory();
		Assert.True(Directory.Exists(apiOutputDirectory), $"Expected the Waypoint.Api build output directory to exist at '{apiOutputDirectory}'.");

		string[] files = Directory.GetFiles(apiOutputDirectory, "*.dll");
		Assert.DoesNotContain(files, path => Path.GetFileNameWithoutExtension(path) == "Waypoint.Infrastructure.Execution");
		Assert.DoesNotContain(files, path => Path.GetFileNameWithoutExtension(path).StartsWith("Microsoft.PowerShell", StringComparison.Ordinal));
		Assert.DoesNotContain(files, path => Path.GetFileNameWithoutExtension(path).StartsWith("System.Management.Automation", StringComparison.Ordinal));
	}

	/// <summary>
	/// Waypoint.Api.dll's own directory, derived the same way <c>WaypointApiEntryAssembly</c>
	/// resolves it for the process-launch tests (Waypoint.Tests.csproj's
	/// <c>AssemblyMetadataAttribute</c>) -- both need the API's OWN bin/ output, distinct
	/// from wherever the currently-executing test assembly lives.
	/// </summary>
	private static string ResolveApiOutputDirectory()
	{
		string? entryAssemblyPath = typeof(WaypointApiFactory).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(attribute => attribute.Key == "WaypointApiEntryAssembly")?.Value;
		Assert.False(string.IsNullOrWhiteSpace(entryAssemblyPath), "WaypointApiEntryAssembly metadata was not found on the test assembly.");
		return Path.GetDirectoryName(Path.GetFullPath(entryAssemblyPath!))!;
	}

	/// <summary>
	/// The DI-registration proof, the same shape <c>JobEngineWiringTests</c> uses:
	/// spin up the real API host (full <c>Program.cs</c> composition, same as every
	/// other <c>WaypointApiFactory</c>-based test) and assert its container has no
	/// <see cref="IJobHandler"/>, <see cref="JobHandlerRegistry"/>, or
	/// <c>JobDispatcherHostedService</c>/<c>LeaseRecoveryHostedService</c> hosted
	/// service registered at all -- not merely "not started".
	/// </summary>
	[Fact]
	public async Task ApiHost_RegistersNoJobHandlersOrDispatcher()
	{
		await using WaypointApiFactory factory = new();
		using IServiceScope scope = factory.Services.CreateScope();

		Assert.Empty(scope.ServiceProvider.GetServices<IJobHandler>());
		Assert.Null(scope.ServiceProvider.GetService<JobHandlerRegistry>());

		IEnumerable<Microsoft.Extensions.Hosting.IHostedService> hostedServices =
			scope.ServiceProvider.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
		Assert.DoesNotContain(hostedServices, service =>
			service.GetType().FullName is "Waypoint.Runner.Jobs.JobDispatcherHostedService"
				or "Waypoint.Runner.Jobs.LeaseRecoveryHostedService");
	}
}
