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

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace Waypoint.Tests.Deploy;

/// <summary>
/// Issues #1706/#1647 (design record #16 §1, docs/rationale/deploy.md's
/// <c>content-libraries-own-volume</c> entry): the content-library registry gets its
/// own named volume, mounted read-write on <c>backend</c>, read-only on <c>nginx</c>,
/// and nested at <c>/vcf/ContentLibrary</c> on <c>download-runner</c> (deliberately
/// INSIDE the depot mount so the runner's own store-path conventions are unchanged).
/// Parses <c>deploy/compose.yaml</c> and <c>deploy/nginx/conf.d/default.conf</c>
/// directly (same YamlDotNet technique as <see cref="RunnerEgressTopologyTests"/>) --
/// no Docker daemon required.
/// </summary>
public sealed class ContentLibraryVolumeTopologyTests
{
	private const string VolumeName = "content-libraries";

	private static readonly YamlMappingNode Compose = LoadCompose();
	private static readonly YamlMappingNode Services = (YamlMappingNode)Compose.Children[new YamlScalarNode("services")];
	private static readonly YamlMappingNode Volumes = (YamlMappingNode)Compose.Children[new YamlScalarNode("volumes")];

	[Fact]
	public void Content_libraries_volume_is_declared()
	{
		Assert.True(
			Volumes.Children.ContainsKey(new YamlScalarNode(VolumeName)),
			$"volume '{VolumeName}' must be declared in deploy/compose.yaml");
	}

	[Fact]
	public void Backend_mounts_content_libraries_read_write_at_root_path()
	{
		(string target, bool readOnly)? mount = ShortFormMount("backend", VolumeName);
		Assert.NotNull(mount);
		Assert.Equal("/var/lib/waypoint/content-libraries", mount!.Value.target);
		Assert.False(mount.Value.readOnly, "backend's content-libraries mount must be read-write");
	}

	[Fact]
	public void Download_runner_mounts_content_libraries_nested_under_vcf()
	{
		(string target, bool readOnly)? mount = ShortFormMount("download-runner", VolumeName);
		Assert.NotNull(mount);
		Assert.Equal("/vcf/ContentLibrary", mount!.Value.target);
		Assert.False(mount.Value.readOnly, "download-runner's content-libraries mount must be read-write");
	}

	[Fact]
	public void Nginx_mounts_content_libraries_read_only_at_dedicated_path()
	{
		YamlMappingNode nginx = (YamlMappingNode)Services.Children[new YamlScalarNode("nginx")];
		YamlSequenceNode volumes = (YamlSequenceNode)nginx.Children[new YamlScalarNode("volumes")];
		YamlMappingNode? longFormMount = volumes.Children.OfType<YamlMappingNode>()
			.FirstOrDefault(v => Scalar(v, "source") == VolumeName);

		Assert.NotNull(longFormMount);
		Assert.Equal("/srv/content-libraries", Scalar(longFormMount!, "target"));
		Assert.Equal("true", Scalar(longFormMount!, "read_only"));
	}

	/// <summary>
	/// The backend must never mount `depot` -- the content-library registry gets its
	/// own volume specifically so the backend's storage surface stays disjoint from
	/// the vendor depot tree (docs/rationale/deploy.md#content-libraries-own-volume).
	/// </summary>
	[Fact]
	public void Backend_does_not_mount_depot_volume()
	{
		Assert.Null(ShortFormMount("backend", "depot"));
	}

	[Fact]
	public void Nginx_default_conf_aliases_content_libraries_to_the_dedicated_mount()
	{
		string conf = File.ReadAllText(ResolveRepoPath(Path.Combine("deploy", "nginx", "conf.d", "default.conf")));
		Match location = Regex.Match(
			conf,
			@"location\s+/repo/content-libraries/\s*\{(?<body>[^}]*)\}",
			RegexOptions.Singleline);

		Assert.True(location.Success, "expected a location /repo/content-libraries/ block in default.conf");
		Assert.Contains("alias /srv/content-libraries/;", location.Groups["body"].Value);
	}

	[Fact]
	public void Nginx_depot_denylist_still_names_content_library()
	{
		string conf = File.ReadAllText(ResolveRepoPath(Path.Combine("deploy", "nginx", "conf.d", "default.conf")));
		Match denylist = Regex.Match(conf, @"location\s+~\*\s+\^/repo/depot/\(([^)]*)\)");

		Assert.True(denylist.Success, "expected the /repo/depot/ store-subtree denylist regex in default.conf");
		Assert.Contains("ContentLibrary", denylist.Groups[1].Value.Split('|'));
	}

	// --- YAML helpers -----------------------------------------------------

	private static (string target, bool readOnly)? ShortFormMount(string service, string volumeSource)
	{
		YamlMappingNode svc = (YamlMappingNode)Services.Children[new YamlScalarNode(service)];
		if (!svc.Children.TryGetValue(new YamlScalarNode("volumes"), out YamlNode? volsNode)
			|| volsNode is not YamlSequenceNode volumes)
		{
			return null;
		}

		foreach (YamlScalarNode entry in volumes.Children.OfType<YamlScalarNode>())
		{
			string[] parts = entry.Value!.Split(':');
			if (parts.Length < 2 || parts[0] != volumeSource)
			{
				continue;
			}

			bool readOnly = parts.Length >= 3 && parts[2] == "ro";
			return (parts[1], readOnly);
		}

		return null;
	}

	private static string? Scalar(YamlMappingNode node, string key)
	{
		return node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? v) && v is YamlScalarNode scalar
			? scalar.Value
			: null;
	}

	private static YamlMappingNode LoadCompose()
	{
		using StreamReader reader = new(ResolveRepoPath(Path.Combine("deploy", "compose.yaml")));
		YamlStream yaml = new();
		yaml.Load(reader);
		return (YamlMappingNode)yaml.Documents[0].RootNode;
	}

	// Walk up from the test assembly's location until the repo root (identified by
	// deploy/compose.yaml) is found -- robust to the bin/<config>/<tfm>/ build layout
	// locally and in CI alike, independent of the process working directory.
	private static string ResolveRepoPath(string relativePath)
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null)
		{
			string composeCandidate = Path.Combine(dir.FullName, "deploy", "compose.yaml");
			if (File.Exists(composeCandidate))
			{
				return Path.Combine(dir.FullName, relativePath);
			}

			dir = dir.Parent;
		}

		throw new FileNotFoundException(
			"Could not locate deploy/compose.yaml by walking up from " + AppContext.BaseDirectory);
	}
}
