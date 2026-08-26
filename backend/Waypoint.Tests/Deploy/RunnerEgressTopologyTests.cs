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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace Waypoint.Tests.Deploy;

/// <summary>
/// Issue #578 acceptance criterion #5 — committed automated regression guard for
/// the runner-egress network topology introduced in PR #603.
///
/// The runner-egress change gives compliance-runner and download-runner an
/// outbound path to lab/target infrastructure and the depot/internet WITHOUT
/// giving anything a path IN to them, and WITHOUT exposing Postgres on that
/// network. Manual <c>docker inspect</c> proved that true the day it landed;
/// these tests are the guard that a future edit to
/// <c>deploy/compose.yaml</c> can't silently undo it — e.g. detach a
/// runner from runner-egress, attach postgres to it, drop
/// <c>internal: true</c>, publish a runner port, or convert runner-egress to an
/// <c>external:</c> network (which would let concurrent <c>-p</c> stacks collide
/// and could be pre-created with arbitrary options — see the compose file's own
/// warning comment).
///
/// It parses <c>deploy/compose.yaml</c> directly with YamlDotNet (already
/// a Waypoint.Core dependency), so it needs no Docker daemon and runs in the
/// backend <c>dotnet test</c> lane locally and in CI identically.
/// </summary>
public sealed class RunnerEgressTopologyTests
{
	private const string EgressNetwork = "runner-egress";
	private const string InternalNetwork = "internal";

	private static readonly string[] Runners = ["compliance-runner", "download-runner"];

	// Only the download-runner may mount the managed-tool tool store
	// (ADR-0014 §7 / #442 AC5 / #570); the backend must not.
	private static readonly string[] ManagedToolMounters = ["download-runner"];

	private static readonly YamlMappingNode Compose = LoadCompose();
	private static readonly YamlMappingNode Services = Child(Compose, "services");
	private static readonly YamlMappingNode Networks = Child(Compose, "networks");

	[Theory]
	[InlineData("compliance-runner")]
	[InlineData("download-runner")]
	public void Runner_is_attached_to_runner_egress(string runner)
	{
		Assert.Contains(EgressNetwork, ServiceNetworks(runner));
	}

	[Theory]
	[InlineData("compliance-runner")]
	[InlineData("download-runner")]
	public void Runner_publishes_no_ports(string runner)
	{
		YamlNode service = Services.Children[new YamlScalarNode(runner)];
		bool hasPorts = ((YamlMappingNode)service).Children
			.ContainsKey(new YamlScalarNode("ports"));
		Assert.False(hasPorts, $"'{runner}' must not declare ports:");
	}

	[Fact]
	public void Postgres_is_not_on_runner_egress()
	{
		Assert.DoesNotContain(EgressNetwork, ServiceNetworks("postgres"));
	}

	[Fact]
	public void Postgres_is_on_internal_only()
	{
		Assert.Equal(new[] { InternalNetwork }, ServiceNetworks("postgres").ToArray());
	}

	[Fact]
	public void Internal_network_is_marked_internal_true()
	{
		YamlMappingNode net = (YamlMappingNode)Networks.Children[new YamlScalarNode(InternalNetwork)];
		string? value = Scalar(net, "internal");
		Assert.Equal("true", value);
	}

	[Fact]
	public void Runner_egress_is_declared_in_file_not_external()
	{
		Assert.True(
			Networks.Children.ContainsKey(new YamlScalarNode(EgressNetwork)),
			$"network '{EgressNetwork}' must be declared in deploy/compose.yaml");

		YamlMappingNode net = (YamlMappingNode)Networks.Children[new YamlScalarNode(EgressNetwork)];
		string? external = Scalar(net, "external");
		Assert.True(
			external is null or "false",
			$"network '{EgressNetwork}' must be declared in-file, not external (external: {external})");
	}

	// --- Managed-tool store boundary (issue #621 / #630 review) -----------

	/// <summary>
	/// ADR-0014 §7 / issue #442 AC5 / #570: the API-facing backend must never
	/// mount the <c>managed-tool</c> tool store (the verified vcf-download-tool
	/// binary and the RSA release-key trust anchor). Manual uploads go to the
	/// separate <c>tool-upload-staging</c> volume instead. Guard so a future edit
	/// can't silently re-attach the store to the backend.
	/// </summary>
	[Fact]
	public void Backend_does_not_mount_managed_tool_volume()
	{
		Assert.DoesNotContain(
			"managed-tool",
			ServiceNamedVolumeSources("backend"));
	}

	/// <summary>
	/// The staging handoff only works if backend (writes) and download-runner
	/// (reads) mount the SAME dedicated staging volume.
	/// </summary>
	[Theory]
	[InlineData("backend")]
	[InlineData("download-runner")]
	public void Service_mounts_tool_upload_staging_volume(string service)
	{
		Assert.Contains(
			"tool-upload-staging",
			ServiceNamedVolumeSources(service));
	}

	/// <summary>Only download-runner mounts the managed-tool tool store.</summary>
	[Fact]
	public void Only_download_runner_mounts_managed_tool_volume()
	{
		List<string> mounters = Services.Children.Keys
			.OfType<YamlScalarNode>()
			.Select(n => n.Value!)
			.Where(svc => ServiceNamedVolumeSources(svc).Contains("managed-tool"))
			.ToList();

		Assert.Equal(ManagedToolMounters, mounters.ToArray());
	}

	// --- YAML helpers -----------------------------------------------------

	/// <summary>
	/// The named-volume sources a service mounts (the part left of the ':' in the
	/// short <c>name:/path</c> form; bind mounts and long-form entries are ignored
	/// since named volumes always use the short form here).
	/// </summary>
	private static List<string> ServiceNamedVolumeSources(string service)
	{
		YamlMappingNode svc = (YamlMappingNode)Services.Children[new YamlScalarNode(service)];
		if (!svc.Children.TryGetValue(new YamlScalarNode("volumes"), out YamlNode? vols)
			|| vols is not YamlSequenceNode seq)
		{
			return [];
		}

		return seq.Children.OfType<YamlScalarNode>()
			.Select(n => n.Value!)
			.Where(v => v.Contains(':') && !v.StartsWith('/') && !v.StartsWith('.'))
			.Select(v => v.Split(':', 2)[0])
			.ToList();
	}

	private static List<string> ServiceNetworks(string service)
	{
		YamlMappingNode svc = (YamlMappingNode)Services.Children[new YamlScalarNode(service)];
		if (!svc.Children.TryGetValue(new YamlScalarNode("networks"), out YamlNode? nets))
		{
			return [];
		}

		// Compose accepts both the list form (`- internal`) and the map form
		// (`internal:` with per-network options); support both.
		return nets switch
		{
			YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>()
				.Select(n => n.Value!).ToList(),
			YamlMappingNode map => map.Children.Keys.OfType<YamlScalarNode>()
				.Select(n => n.Value!).ToList(),
			_ => [],
		};
	}

	private static string? Scalar(YamlMappingNode node, string key)
	{
		return node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? v)
			&& v is YamlScalarNode scalar
			? scalar.Value
			: null;
	}

	private static YamlMappingNode Child(YamlMappingNode parent, string key)
	{
		return (YamlMappingNode)parent.Children[new YamlScalarNode(key)];
	}

	private static YamlMappingNode LoadCompose()
	{
		string path = ResolveComposePath();
		using var reader = new StreamReader(path);
		var yaml = new YamlStream();
		yaml.Load(reader);
		return (YamlMappingNode)yaml.Documents[0].RootNode;
	}

	// Walk up from the test assembly's location until deploy/compose.yaml
	// is found. Robust to the bin/<config>/<tfm>/ build layout locally and in
	// CI alike, and independent of the process working directory.
	private static string ResolveComposePath()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null)
		{
			string candidate = Path.Combine(dir.FullName, "deploy", "compose.yaml");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			dir = dir.Parent;
		}

		throw new FileNotFoundException(
			"Could not locate deploy/compose.yaml by walking up from "
			+ AppContext.BaseDirectory);
	}
}
