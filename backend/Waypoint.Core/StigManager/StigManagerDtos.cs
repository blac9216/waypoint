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
/// The global STIG Manager connection (docs/domain-model.md "STIG Manager connection",
/// docs/api-contract.md "STIG Manager": "Global default + per-site override; endpoint,
/// oidc client, collection, reachability/token TTL (secret write-only)"). Shape mirrors
/// the sibling <c>vmware-stig-docker</c> module.benchmarks.ps1's
/// <c>Get-StigManagerConnection</c>: endpoint (ApiBase), authority (OIDC issuer),
/// collection, client id/scope. <see cref="CredentialId"/> is a reference into the
/// existing typed credential store (a <c>token</c>-type credential, #249) that carries
/// the OIDC client secret -- there is no secret column here (issue #310 AC: "no new
/// secret path").
/// </summary>
public sealed record StigManagerConnection(
	string Endpoint,
	string Authority,
	string Collection,
	string ClientId,
	string Scope,
	Guid? CredentialId,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>Create/replace request for the global connection. All fields except <see cref="CredentialId"/> and <see cref="Scope"/> are required (the upsert has no prior row to fall back to).</summary>
public sealed record StigManagerConnectionRequest(
	string? Endpoint,
	string? Authority,
	string? Collection,
	string? ClientId,
	string? Scope,
	Guid? CredentialId);

/// <summary>
/// A connection resolved for use against a specific site (or the bare global default
/// when no site is in play): the per-site override (<c>sites.stigman_override</c>)
/// wins over the global row when present, matching the three-layer "most specific
/// wins" convention the rest of the config-doc model uses. <see cref="Source"/> tells
/// the caller which layer actually supplied the value -- #311's upload stage and #312's
/// Config tab both need this, not just the resolved values.
/// </summary>
public sealed record ResolvedStigManagerConnection(
	string Endpoint,
	string Authority,
	string Collection,
	string ClientId,
	string Scope,
	Guid? CredentialId,
	StigManagerConnectionSource Source);

public enum StigManagerConnectionSource
{
	/// <summary>Supplied by <c>sites.stigman_override</c>.</summary>
	SiteOverride,

	/// <summary>Supplied by the global <c>stigman_connections</c> row.</summary>
	Global,
}

/// <summary>
/// Response for <c>POST /stigman/test</c> (and the per-site variant). Reachability and
/// auth are checked without any side effect on the target STIG Manager -- no asset,
/// collection membership, or upload is created (issue #310 AC). The live network check
/// only ever runs against a real instance the operator has connected the appliance to;
/// automated tests substitute <see cref="IStigManagerProbe"/>.
/// </summary>
public sealed record StigManagerTestResponse(bool Reachable, bool AuthOk, string Outcome, string Message, string? ApiVersion);

/// <summary>The closed set of <see cref="StigManagerTestResponse.Outcome"/> values.</summary>
public static class StigManagerTestOutcomes
{
	public const string Ok = "ok";
	public const string Unreachable = "unreachable";
	public const string AuthFailed = "auth_failed";
	public const string NotConfigured = "not_configured";
}
