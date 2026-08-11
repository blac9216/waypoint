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
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.DependencyInjection;
using Xunit;

namespace Waypoint.Tests.Infrastructure;

/// <summary>
/// The composition root's "no connection string, no wiring" guard, both ways.
///
/// The negative half is the one that matters and the one nothing covered: the
/// <c>WebApplicationFactory</c> suite runs without a connection string, so every existing
/// test exercised only the branch where the job engine is *not* registered. A regression
/// that wired the engine unconditionally would therefore have gone unnoticed until a
/// deployment without a database crashed on startup -- and no test would have moved.
/// </summary>
public sealed class JobEngineWiringTests
{
	[Fact]
	public void WithAConnectionString_TheQueueRepositoryAndEventPublisherAreRegistered()
	{
		using ServiceProvider provider = BuildProvider("Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p");

		Assert.NotNull(provider.GetService<IJobControlRepository>());
		Assert.NotNull(provider.GetService<IJobRunnerRepository>());
		Assert.NotNull(provider.GetService<IJobEventPublisher>());
	}

	[Fact]
	public void WithoutAConnectionString_NeitherIsRegistered()
	{
		using ServiceProvider provider = BuildProvider(connectionString: null);

		Assert.Null(provider.GetService<IJobControlRepository>());
		Assert.Null(provider.GetService<IJobRunnerRepository>());
		Assert.Null(provider.GetService<IJobEventPublisher>());
	}

	/// <summary>
	/// A blank connection string is treated as absent rather than as a value to hand to
	/// Npgsql, which would fail later and further away.
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void WithABlankConnectionString_NeitherIsRegistered(string connectionString)
	{
		using ServiceProvider provider = BuildProvider(connectionString);

		Assert.Null(provider.GetService<IJobControlRepository>());
		Assert.Null(provider.GetService<IJobRunnerRepository>());
		Assert.Null(provider.GetService<IJobEventPublisher>());
	}

	/// <summary>
	/// <see cref="JobEngineOptions"/> binds from the <c>JobEngine</c> configuration
	/// section. Pinned here because the section name is a string that appears in
	/// <c>appsettings.json</c> and in code, and a mismatch between them fails silently:
	/// every option quietly keeps its default and the engine looks configured.
	/// </summary>
	[Fact]
	public void JobEngineOptions_BindFromTheJobEngineSection()
	{
		using ServiceProvider provider = BuildProvider(
			connectionString: null,
			("JobEngine:MaxConcurrency", "11"),
			("JobEngine:ConsecutiveAuthFailureThreshold", "5"),
			("JobEngine:EventCommandTimeoutSeconds", "9"));

		JobEngineOptions options = provider.GetRequiredService<IOptions<JobEngineOptions>>().Value;

		Assert.Equal(11, options.MaxConcurrency);
		Assert.Equal(5, options.ConsecutiveAuthFailureThreshold);
		Assert.Equal(9, options.EventCommandTimeoutSeconds);
	}

	private static ServiceProvider BuildProvider(string? connectionString, params (string Key, string Value)[] settings)
	{
		List<KeyValuePair<string, string?>> values = [.. settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value))];
		if (connectionString is not null)
		{
			values.Add(new KeyValuePair<string, string?>("ConnectionStrings:Waypoint", connectionString));
		}

		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		return services.BuildServiceProvider();
	}
}
