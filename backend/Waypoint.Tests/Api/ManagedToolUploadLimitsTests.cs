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
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Controllers;
using Xunit;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #641: <c>ManagedToolController.Upload</c> rejected real 383-490 MB
/// <c>vcf-download-tool</c> artifacts with "Multipart body length limit 134217728
/// exceeded" even though nginx (512m, issue #620) and Kestrel
/// (<c>[RequestSizeLimit]</c>) both allowed the request through -- ASP.NET's
/// multipart FORM reader enforces its own, separate cap
/// (<c>FormOptions.MultipartBodyLengthLimit</c>, 128 MiB default) that neither of
/// those touches. A live end-to-end proof would mean streaming a several-hundred-MB
/// multipart body through the full Kestrel/MVC pipeline in a test -- slow and
/// disproportionate to what's actually in question, which is whether the attribute
/// values agree. This test asserts that agreement via reflection instead: fast,
/// deterministic, and it fails the moment any of the three numbers drift apart
/// again. Live confirmation with a real vendor artifact is exactly the manual repro
/// already recorded on the issue and belongs in validation, not CI.
/// </summary>
public sealed class ManagedToolUploadLimitsTests
{
	private static MethodInfo UploadMethod =>
		typeof(ManagedToolController).GetMethod(nameof(ManagedToolController.Upload))
		?? throw new InvalidOperationException($"{nameof(ManagedToolController)}.{nameof(ManagedToolController.Upload)} not found.");

	private static long MaxUploadBytes =>
		(long)(typeof(ManagedToolController)
				.GetField("MaxUploadBytes", BindingFlags.NonPublic | BindingFlags.Static)
				?.GetValue(null)
			?? throw new InvalidOperationException($"{nameof(ManagedToolController)}.MaxUploadBytes constant not found."));

	[Fact]
	public void MaxUploadBytes_is_512_mebibytes()
	{
		// Pins the single source of truth the other two limits (and nginx's
		// client_max_body_size) must track, so a future edit to this constant is
		// caught here rather than silently drifting from nginx's hand-maintained value.
		Assert.Equal(512L * 1024 * 1024, MaxUploadBytes);
	}

	[Fact]
	public void Upload_action_RequestSizeLimit_matches_MaxUploadBytes()
	{
		RequestSizeLimitAttribute? attribute = UploadMethod.GetCustomAttribute<RequestSizeLimitAttribute>();
		Assert.NotNull(attribute);

		// RequestSizeLimitAttribute stores its value in a private "_bytes" field with
		// no public accessor -- reflect it out rather than re-deriving the limit some
		// other way, so this test breaks the moment the attribute's argument changes.
		FieldInfo bytesField = typeof(RequestSizeLimitAttribute)
			.GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new InvalidOperationException("RequestSizeLimitAttribute._bytes field not found (framework internals changed).");
		var bytes = (long)bytesField.GetValue(attribute)!;

		Assert.Equal(MaxUploadBytes, bytes);
	}

	[Fact]
	public void Upload_action_RequestFormLimits_MultipartBodyLengthLimit_matches_MaxUploadBytes()
	{
		// The regression this issue is about: RequestSizeLimit alone does not raise
		// FormOptions.MultipartBodyLengthLimit (128 MiB framework default), which the
		// IFormFile-bound multipart reader enforces independently and BEFORE the file
		// is staged. Without this attribute a real >128 MB artifact 400s here even
		// though RequestSizeLimit and nginx both let the bytes in.
		RequestFormLimitsAttribute? attribute = UploadMethod.GetCustomAttribute<RequestFormLimitsAttribute>();

		Assert.NotNull(attribute);
		Assert.Equal(MaxUploadBytes, attribute!.MultipartBodyLengthLimit);
	}

	[Fact]
	public void Upload_action_is_scoped_not_a_global_FormOptions_change()
	{
		// Issue #641 explicitly prefers an endpoint-scoped override so unrelated form
		// endpoints keep the conservative 128 MiB default. Assert the attribute lives
		// on the action itself (not e.g. copy-pasted onto the whole controller or
		// applied via a global MVC options), which would raise the limit for every
		// action in this controller including the ones that never take a multipart
		// upload today.
		bool controllerHasFormLimitsAttribute = typeof(ManagedToolController)
			.GetCustomAttributes<RequestFormLimitsAttribute>()
			.Any();

		Assert.False(controllerHasFormLimitsAttribute, "RequestFormLimits should be scoped to the Upload action, not the whole controller.");
	}
}
