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

using Waypoint.Core.ConfigDocs;
using Xunit;

namespace Waypoint.Tests.Core;

/// <summary>
/// Issue #266 (second slice of #22): the pure Global -> Site -> Target
/// most-specific-wins merge, and expired-attestation-falls-through semantics
/// (docs/domain-model.md "STIG configuration documents").
/// </summary>
public sealed class ConfigDocResolverTests
{
	private static readonly DateTimeOffset Now = new(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);
	private static readonly Guid SiteId = Guid.Parse("00000000-0000-0000-0000-000000000001");
	private static readonly Guid TargetId = Guid.Parse("00000000-0000-0000-0000-000000000002");

	[Fact]
	public void Resolve_TargetLayerPresent_TargetWinsOverSiteAndGlobal()
	{
		ConfigDocWithLatestVersion global = Doc(ConfigDocLayers.Global, null, "global: 1\n");
		ConfigDocWithLatestVersion site = Doc(ConfigDocLayers.Site, SiteId, "site: 1\n");
		ConfigDocWithLatestVersion target = Doc(ConfigDocLayers.Target, TargetId, "target: 1\n");

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Input, "esxi-stig", global, site, target, Now);

		Assert.Equal($"target:{TargetId}", resolution.Layer);
		Assert.Equal("target: 1\n", resolution.Body);
	}

	[Fact]
	public void Resolve_NoTargetLayer_SiteWinsOverGlobal()
	{
		ConfigDocWithLatestVersion global = Doc(ConfigDocLayers.Global, null, "global: 1\n");
		ConfigDocWithLatestVersion site = Doc(ConfigDocLayers.Site, SiteId, "site: 1\n");

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Input, "esxi-stig", global, site, null, Now);

		Assert.Equal($"site:{SiteId}", resolution.Layer);
		Assert.Equal("site: 1\n", resolution.Body);
	}

	[Fact]
	public void Resolve_OnlyGlobal_GlobalSupplies()
	{
		ConfigDocWithLatestVersion global = Doc(ConfigDocLayers.Global, null, "global: 1\n");

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Input, "esxi-stig", global, null, null, Now);

		Assert.Equal(ConfigDocLayers.Global, resolution.Layer);
		Assert.Equal("global: 1\n", resolution.Body);
	}

	[Fact]
	public void Resolve_NoLayerHasADoc_ResolvesToNothing()
	{
		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Input, "esxi-stig", null, null, null, Now);

		Assert.Null(resolution.Layer);
		Assert.Null(resolution.Body);
		Assert.False(resolution.AttestationExpired);
	}

	[Fact]
	public void Resolve_NotTightenOnly_SiteValueDiffersFromGlobal_SiteStillWinsOutright()
	{
		// docs/domain-model.md: "A lower layer may set a genuinely *different* value; it is
		// not a tighten-only relationship." The site doc here does not restate/narrow the
		// global value -- it is a wholly different body -- and still wins because it is more
		// specific, confirming resolution never merges field-by-field.
		ConfigDocWithLatestVersion global = Doc(ConfigDocLayers.Global, null, "syslog_host: syslog.example.internal\nsyslog_protocol: udp\n");
		ConfigDocWithLatestVersion site = Doc(ConfigDocLayers.Site, SiteId, "syslog_host: photon-log-01a.example.internal\n");

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Input, "esxi-stig", global, site, null, Now);

		Assert.Equal($"site:{SiteId}", resolution.Layer);
		Assert.Equal("syslog_host: photon-log-01a.example.internal\n", resolution.Body);
	}

	[Fact]
	public void Resolve_ExpiredTargetAttestation_FallsThroughToSiteAndReportsExpired()
	{
		ConfigDocWithLatestVersion site = Doc(ConfigDocLayers.Site, SiteId, "status: Not_A_Finding\njustification: site waiver\nexpires: 2030-01-01\n");
		ConfigDocWithLatestVersion target = Doc(ConfigDocLayers.Target, TargetId, "status: Not_A_Finding\njustification: expired waiver\nexpires: 2026-01-01\n");

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Attestation, "esxi-stig", null, site, target, Now);

		Assert.Equal($"site:{SiteId}", resolution.Layer);
		Assert.True(resolution.AttestationExpired);
		Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), resolution.AttestationExpiresAt);
	}

	[Fact]
	public void Resolve_AllAttestationLayersExpired_ResolvesToNothingButStillReportsExpired()
	{
		ConfigDocWithLatestVersion target = Doc(ConfigDocLayers.Target, TargetId, "status: Not_A_Finding\njustification: expired waiver\nexpires: 2020-01-01\n");

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Attestation, "esxi-stig", null, null, target, Now);

		Assert.Null(resolution.Layer);
		Assert.Null(resolution.Body);
		Assert.True(resolution.AttestationExpired);
	}

	[Fact]
	public void Resolve_AttestationExpiresExactlyNow_TreatedAsExpired()
	{
		ConfigDocWithLatestVersion target = Doc(ConfigDocLayers.Target, TargetId, "status: Not_A_Finding\njustification: boundary\nexpires: 2026-08-08\n");

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Attestation, "esxi-stig", null, null, target, Now);

		Assert.True(resolution.AttestationExpired);
		Assert.Null(resolution.Layer);
	}

	[Fact]
	public void Resolve_AttestationWithNoExpiresKey_NeverExpires()
	{
		ConfigDocWithLatestVersion target = Doc(ConfigDocLayers.Target, TargetId, "status: Not_A_Finding\njustification: permanent waiver, no expiry\n");

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Attestation, "esxi-stig", null, null, target, Now);

		Assert.False(resolution.AttestationExpired);
		Assert.Equal($"target:{TargetId}", resolution.Layer);
	}

	[Fact]
	public void Resolve_NonAttestationKindWithExpiresLikeKey_NeverTreatedAsExpiry()
	{
		// "expires" only means something for kind == attestation -- an input doc that
		// happens to have a similarly-named key is not subject to expiry semantics.
		ConfigDocWithLatestVersion target = Doc(ConfigDocLayers.Target, TargetId, "expires: 2020-01-01\n");

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(ConfigDocKinds.Input, "esxi-stig", null, null, target, Now);

		Assert.False(resolution.AttestationExpired);
		Assert.Equal($"target:{TargetId}", resolution.Layer);
	}

	private static ConfigDocWithLatestVersion Doc(string layerType, Guid? layerRef, string body)
	{
		Guid id = Guid.NewGuid();
		ConfigDoc doc = new(id, "attestation", "esxi-stig", layerType, layerRef, 1, Now, Now);
		ConfigDocVersion version = new(Guid.NewGuid(), id, 1, "test-author", body, Now);
		return new ConfigDocWithLatestVersion(doc, version);
	}
}
