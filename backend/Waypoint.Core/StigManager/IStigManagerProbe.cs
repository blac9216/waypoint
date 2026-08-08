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

namespace Waypoint.Core.StigManager;

/// <summary>
/// The network boundary for <c>POST /stigman/test</c>: OIDC client-credentials token
/// acquisition (mirroring the sibling <c>vmware-stig-docker</c> module.benchmarks.ps1's
/// <c>Get-StigManagerToken</c>, which discovers the token endpoint from
/// <c>{Authority}/.well-known/openid-configuration</c>) followed by one read-only,
/// side-effect-free call to STIG Manager's own API-version endpoint. Isolated behind
/// this interface so integration tests exercise the controller/repository/resolution
/// logic against a stub instead of a live STIG Manager instance -- there is no fake
/// STIG Manager in this repo, and there must never be a real lab hostname in a
/// committed test fixture (see CLAUDE.md). Verifying against a real instance is a
/// manual owner step, not something these tests claim to cover.
/// </summary>
public interface IStigManagerProbe
{
	Task<StigManagerProbeResult> ProbeAsync(ResolvedStigManagerConnection connection, string? clientSecret, CancellationToken cancellationToken);
}

/// <summary>Outcome of one probe attempt. <see cref="ApiVersion"/> is only populated when both <see cref="Reachable"/> and <see cref="AuthOk"/> are true.</summary>
public sealed record StigManagerProbeResult(bool Reachable, bool AuthOk, string? ApiVersion, string? Detail);
