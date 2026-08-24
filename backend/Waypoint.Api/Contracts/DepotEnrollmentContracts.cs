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

using System.Text.Json.Serialization;
using Waypoint.Core.Downloads;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for <c>GET /api/v1/downloads/enrollment</c> and every enrollment
/// mutation (issue #691). Never contains the Activation Code value -- only its
/// decoded, non-secret <c>asset_id</c> once paired, and the state machine itself.
/// </summary>
public sealed record DepotEnrollmentResponse(
	[property: JsonPropertyName("state")]
	string State,

	[property: JsonPropertyName("depot_id")]
	string? DepotId,

	[property: JsonPropertyName("depot_id_generated_at")]
	DateTimeOffset? DepotIdGeneratedAt,

	[property: JsonPropertyName("paired_at")]
	DateTimeOffset? PairedAt,

	[property: JsonPropertyName("activation_code_configured")]
	bool ActivationCodeConfigured,

	[property: JsonPropertyName("last_validation_failure")]
	string? LastValidationFailure,

	[property: JsonPropertyName("reset_at")]
	DateTimeOffset? ResetAt,

	// The registration-instructions URL, always the corrected .com host (issue #691:
	// "Detect the VCFDT 9.1 .net registration-link typo ... and present the corrected
	// .com URL rather than blindly trusting tool prose") -- server-supplied so the
	// frontend never hardcodes or re-derives it.
	[property: JsonPropertyName("registration_url")]
	string RegistrationUrl)
{
	public const string CorrectedRegistrationUrl = "https://vcf.broadcom.com";

	public static DepotEnrollmentResponse FromDomain(DepotEnrollment enrollment, bool activationCodeConfigured)
	{
		ArgumentNullException.ThrowIfNull(enrollment);
		return new DepotEnrollmentResponse(
			enrollment.State,
			enrollment.DepotId,
			enrollment.DepotIdGeneratedAt,
			enrollment.PairedAt,
			activationCodeConfigured,
			enrollment.LastValidationFailure,
			enrollment.ResetAt,
			CorrectedRegistrationUrl);
	}
}

/// <summary>Response body for both enrollment job-trigger endpoints (202 Accepted) -- mirrors <see cref="ManagedToolInstallQueuedResponse"/>'s shape.</summary>
public sealed record DepotEnrollmentJobQueuedResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("job_id")]
	string JobId);

/// <summary>Request body for <c>POST /api/v1/downloads/enrollment/activation-code</c>. Never logged or echoed back.</summary>
public sealed record AcceptActivationCodeRequest(
	[property: JsonPropertyName("activation_code")]
	string ActivationCode);

/// <summary>Request body for <c>POST /api/v1/downloads/enrollment/reset</c> -- requires an explicit, literal <c>true</c> confirmation (issue #691 AC: "requires explicit confirmation").</summary>
public sealed record ResetEnrollmentRequest(
	[property: JsonPropertyName("confirm")]
	bool Confirm);
