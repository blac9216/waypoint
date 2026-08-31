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

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// Shared, honest classification (issue #791) for a <c>vcf-download-tool</c> invocation
/// that RAN and exited nonzero. The tool is validation-by-use: the same
/// <c>metadata download</c> that pulls the catalog is also what proves an Activation
/// Code, so both <see cref="DepotIdentityTool.ValidateActivationCodeAsync"/> and
/// <see cref="ManagedToolMetadataPuller"/> need the same rule -- a network-unreachable
/// environment must produce network-classified guidance, never <c>auth_failing</c>, and
/// only signals that genuinely indicate the credential was rejected mark auth failure.
/// An ambiguous exit is classified conservatively as a non-auth failure carrying the
/// tool's own message, so a bad code is never claimed on evidence the tool did not give.
///
/// Issue #1482 extends this with <see cref="FailureClass.Disk"/> and
/// <see cref="FailureClass.Throttle"/>: a real <c>binaries download</c> writes
/// (potentially large) artifacts to local disk and runs under the unbounded-concurrency
/// model the 2026-08-28 grill decision (R2-8) fixed, so local disk exhaustion and
/// vendor-side rate limiting are both distinct, actionable failure kinds a binary
/// transfer can hit that the read-only metadata/validate calls above never could.
/// Checked in Network -&gt; Disk -&gt; Throttle -&gt; Auth order: a disk-full message never
/// contains an auth phrase, and a throttle response ("429", "too many requests") is
/// checked before the generic "401"/"403" auth substrings so it is never miscategorized
/// as a credential rejection.
/// </summary>
internal static class DownloadToolFailureClassifier
{
	/// <summary>Substrings that genuinely indicate credential rejection by Broadcom -- only these mark auth failure.</summary>
	private static readonly string[] AuthFailurePhrases =
	[
		"activation code", "authentication", "unauthenticated", "authorization", "unauthorized",
		"forbidden", "invalid token", "expired", "revoked", "not entitled", "entitlement",
		"permission", "portal role", "403", "401",
	];

	/// <summary>Substrings that indicate the tool could not reach Broadcom -- these are network problems, never auth failure.</summary>
	private static readonly string[] NetworkFailurePhrases =
	[
		"could not resolve", "name resolution", "temporary failure in name resolution",
		"connection refused", "connection reset", "connection timed out", "timed out",
		"unreachable", "no route to host", "network is unreachable", "could not connect",
		"failed to connect", "connect timeout", "proxy", "tls handshake", "ssl handshake",
		"handshake failed", "certificate verify failed", "unknownhost", "socket",
	];

	/// <summary>Substrings that indicate local disk exhaustion or a filesystem write failure writing into the depot store -- never a vendor-side rejection.</summary>
	private static readonly string[] DiskFailurePhrases =
	[
		"no space left on device", "disk full", "disk quota exceeded", "read-only file system",
		"insufficient disk space", "not enough space", "device out of space",
	];

	/// <summary>Substrings that indicate Broadcom rate-limited/throttled this identity -- distinct from a hard auth rejection (issue #1482 AC: throttle-detection observability).</summary>
	private static readonly string[] ThrottleFailurePhrases =
	[
		"429", "too many requests", "rate limit", "rate-limit", "throttl", "slow down",
		"try again later", "quota exceeded",
	];

	/// <summary>Outcome class for a completed-but-nonzero invocation.</summary>
	internal enum FailureClass
	{
		/// <summary>Unreachable/unresolvable/refused connectivity -- surface as a network problem, never auth.</summary>
		Network,

		/// <summary>Local disk exhaustion or a filesystem write failure -- never a vendor-side rejection.</summary>
		Disk,

		/// <summary>Broadcom rate-limited/throttled this identity -- retry-later guidance, never a hard credential rejection.</summary>
		Throttle,

		/// <summary>Explicit credential rejection (bad/expired/revoked code, missing portal role).</summary>
		Auth,

		/// <summary>Ambiguous -- the tool did not clearly differentiate. Classified conservatively as non-auth.</summary>
		Unknown,
	}

	/// <summary>
	/// Classifies a completed-but-nonzero tool message. Checked in Network -&gt; Disk -&gt;
	/// Throttle -&gt; Auth order (see class doc comment) so a timeout/connect failure whose
	/// surrounding prose happens to mention a code is still treated as connectivity, and a
	/// throttle response is never miscategorized as a hard credential rejection.
	/// </summary>
	internal static FailureClass Classify(string toolMessage)
	{
		if (string.IsNullOrWhiteSpace(toolMessage))
		{
			return FailureClass.Unknown;
		}

		if (ContainsAny(toolMessage, NetworkFailurePhrases))
		{
			return FailureClass.Network;
		}

		if (ContainsAny(toolMessage, DiskFailurePhrases))
		{
			return FailureClass.Disk;
		}

		if (ContainsAny(toolMessage, ThrottleFailurePhrases))
		{
			return FailureClass.Throttle;
		}

		return ContainsAny(toolMessage, AuthFailurePhrases) ? FailureClass.Auth : FailureClass.Unknown;
	}

	private static bool ContainsAny(string haystack, string[] needles)
	{
		foreach (string needle in needles)
		{
			if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}
}
