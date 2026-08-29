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

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Jobs;
using Waypoint.Core.Runs;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Controller tests for the /runs endpoints: role guards, happy path, 404s, and
/// state-transition validation. Uses <see cref="RunsTestApiFactory"/> to inject a
/// controllable <see cref="FakeJobQueueRepository"/> through the test host.
/// </summary>
public sealed class RunsEndpointTests : IClassFixture<RunsTestApiFactory>
{
	private readonly RunsTestApiFactory _factory;

	public RunsEndpointTests(RunsTestApiFactory factory)
	{
		_factory = factory;
	}

	// -- role guard tests ---------------------------------------------------

	[Theory]
	[InlineData("Viewer")]
	public async Task CreateRun_WithRoleBelowCyber_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		request.Content = new StringContent(
			System.Text.Json.JsonSerializer.Serialize(new { run_type = "scan", scope = "{}" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task CreateRun_WithCyberRole_Returns202()
	{
		// Non-scan run_type: this only proves the [RequireCyberRole] floor lets a
		// Cyber caller through to CreateRunAsync. Scan's own fan-out (site/target
		// validation, per-target JobSpecs) is covered against real Postgres in
		// ScanRunFanOutTests -- FakeJobQueueRepository here has no SiteRepository/
		// TargetRepository-backed data to validate against.
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");
		request.Content = new StringContent(
			System.Text.Json.JsonSerializer.Serialize(new { run_type = "download", scope = "{}" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		// Issue #515/#551: the /runs API surface never sets schedule attribution --
		// only ScheduleDispatchService stamps a scheduleId on its own CreateRunAsync
		// calls (covered against real Postgres in ScheduleDispatchServiceTests).
		Assert.Null(_factory.Repository.LastCreateRunScheduleId);
	}

	// -- issue #208: remediation gate + initiator provenance ----------------

	[Theory]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task CreateRun_RemediateBelowAdmin_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { run_type = "remediate", scope = "{}", confirmation = "REMEDIATE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("remediate")]
	[InlineData("yes")]
	public async Task CreateRun_RemediateWithoutExactConfirmation_Returns400(string? confirmation)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { run_type = "remediate", scope = "{}", confirmation }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task CreateRun_RemediateWithAdminAndConfirmation_Returns202()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { run_type = "remediate", scope = "{}", confirmation = "REMEDIATE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal("remediate", _factory.Repository.LastCreateRun!.Value.RunType);
	}

	// -- issue #594 (epic #577): run purge -----------------------------------

	[Theory]
	[InlineData(null)]
	[InlineData("purge")]
	[InlineData("yes")]
	public async Task PurgeRun_WithoutExactConfirmation_Returns400(string? confirmation)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/purge");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task PurgeRun_BelowAdmin_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/purge");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "PURGE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		// [RequireAdminRole] rejects before the controller action's own confirmation
		// check ever runs -- proves the role gate is the floor, not just the body
		// validation.
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PurgeRun_UnknownRun_Returns404()
	{
		// FakeJobQueueRepository's underlying RunPurgeService resolves the real run
		// lookup through IJobControlRepository (the fake) -- an id never registered
		// via SetRun means GetRunAsync returns null, so RunPurgeService reports
		// RunNotFound and the controller maps that to 404, proving the confirmation
		// gate and the not-found mapping compose correctly end to end.
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/purge");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "PURGE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task PurgeRun_NonTerminalRun_Returns409()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{runId}/purge");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "PURGE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("run_not_terminal", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetPurgeStatus_NeverRequested_Returns404()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/purge");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	// -- issue #784 (epic #726): retention holds ------------------------------

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task PlaceRetentionHold_WithoutReason_Returns400(string? reason)
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));

		HttpResponseMessage response = await SendRetentionHoldAsync(HttpMethod.Post, runId, "Admin", reason);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task RemoveRetentionHold_WithoutReason_Returns400(string? reason)
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));

		HttpResponseMessage response = await SendRetentionHoldAsync(HttpMethod.Delete, runId, "Admin", reason);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task PlaceRetentionHold_BelowAdmin_Returns403(string role)
	{
		// AC2: non-Admins cannot place a hold. [RequireAdminRole] rejects before the
		// action's own reason validation runs, so a well-formed body still gets 403 --
		// the role gate is the floor, not the body check (same proof shape as
		// PurgeRun_BelowAdmin_Returns403). Cyber is included deliberately: section 7's
		// "controls scans it initiated" ownership does NOT extend to retention holds.
		HttpResponseMessage response = await SendRetentionHoldAsync(HttpMethod.Post, Guid.NewGuid(), role, "invented-legal-hold-reason");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task RemoveRetentionHold_BelowAdmin_Returns403(string role)
	{
		HttpResponseMessage response = await SendRetentionHoldAsync(HttpMethod.Delete, Guid.NewGuid(), role, "invented-unhold-reason");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task GetRetentionHold_Unauthenticated_Returns401()
	{
		// The read is [RequireViewerRole] -- the lowest role there is -- so "below
		// Viewer" is "no authenticated principal at all". Proves the read is not
		// anonymous even though it never 404s.
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await client.GetAsync($"/api/v1/runs/{Guid.NewGuid()}/retention-hold");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task PlaceRetentionHold_UnknownRun_Returns404()
	{
		HttpResponseMessage response = await SendRetentionHoldAsync(HttpMethod.Post, Guid.NewGuid(), "Admin", "invented-legal-hold-reason");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task PlaceRetentionHold_NonTerminalRun_Returns409()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));

		HttpResponseMessage response = await SendRetentionHoldAsync(HttpMethod.Post, runId, "Admin", "invented-legal-hold-reason");

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Contains("run_not_terminal", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task PlaceRetentionHold_NonComplianceRunType_Returns409()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));
		_factory.Repository.SetRunType(runId, "download");

		HttpResponseMessage response = await SendRetentionHoldAsync(HttpMethod.Post, runId, "Admin", "invented-legal-hold-reason");

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Contains("unsupported_run_type", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RemoveRetentionHold_NeverHeld_Returns404NotHeld()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));

		HttpResponseMessage response = await SendRetentionHoldAsync(HttpMethod.Delete, runId, "Admin", "invented-unhold-reason");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		// Distinct from the generic run-not-found 404 -- the run exists, the hold does not.
		Assert.Contains("not_held", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetRetentionHold_NeverHeld_Returns200WithActiveFalse()
	{
		// Suggested test step 7: the read NEVER 404s, so a run-details surface can ask
		// "is this run held" unconditionally. An id that was never held -- and indeed
		// no run row at all -- still renders the honest-empty projection.
		Guid runId = Guid.NewGuid();

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/retention-hold");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(runId.ToString(), body.RootElement.GetProperty("run_id").GetString());
		Assert.False(body.RootElement.GetProperty("active").GetBoolean());
		// The API serializer omits null members, so "no reason/actor/time" renders as
		// absent-or-null rather than as an empty string -- either way the surface
		// carries no stale hold detail for a run that is not held.
		AssertAbsentOrNull(body.RootElement, "reason");
		AssertAbsentOrNull(body.RootElement, "placed_by");
		AssertAbsentOrNull(body.RootElement, "placed_at");
	}

	[Fact]
	public async Task PlaceThenGetThenRemoveRetentionHold_ReportsReasonActorAndTimeThenGoesInactive()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));

		HttpResponseMessage placed = await SendRetentionHoldAsync(HttpMethod.Post, runId, "Admin", "invented-legal-hold-reason");
		Assert.Equal(HttpStatusCode.OK, placed.StatusCode);

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage read = new(HttpMethod.Get, $"/api/v1/runs/{runId}/retention-hold");
		read.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage held = await client.SendAsync(read);

		Assert.Equal(HttpStatusCode.OK, held.StatusCode);
		using (JsonDocument body = JsonDocument.Parse(await held.Content.ReadAsStringAsync()))
		{
			Assert.True(body.RootElement.GetProperty("active").GetBoolean());
			Assert.Equal("invented-legal-hold-reason", body.RootElement.GetProperty("reason").GetString());
			// TestAuthHandler's principal -- proves the actor comes from the caller's
			// identity, not from the request body.
			Assert.Equal("test-user", body.RootElement.GetProperty("placed_by").GetString());
			Assert.NotNull(body.RootElement.GetProperty("placed_at").GetString());
		}

		// AC4: removal returns the POST-removal state (active:false), not the removed
		// hold's former reason -- see MapRetentionHold's doc comment.
		HttpResponseMessage removed = await SendRetentionHoldAsync(HttpMethod.Delete, runId, "Admin", "invented-unhold-reason");
		Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
		using (JsonDocument body = JsonDocument.Parse(await removed.Content.ReadAsStringAsync()))
		{
			Assert.False(body.RootElement.GetProperty("active").GetBoolean());
		}

		// And the read agrees afterwards -- still 200, now inactive.
		HttpRequestMessage reread = new(HttpMethod.Get, $"/api/v1/runs/{runId}/retention-hold");
		reread.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage after = await client.SendAsync(reread);
		Assert.Equal(HttpStatusCode.OK, after.StatusCode);
		using (JsonDocument body = JsonDocument.Parse(await after.Content.ReadAsStringAsync()))
		{
			Assert.False(body.RootElement.GetProperty("active").GetBoolean());
		}
	}

	[Fact]
	public async Task PurgeRun_HeldRun_Returns409RunRetentionHeld()
	{
		// The controller-level half of AC3: the hold and the purge endpoint compose --
		// the evidence graph actually surviving is RunPurgeComplianceEvidenceTests' job.
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));
		Assert.Equal(HttpStatusCode.OK, (await SendRetentionHoldAsync(HttpMethod.Post, runId, "Admin", "invented-legal-hold-reason")).StatusCode);

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{runId}/purge");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "PURGE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Contains("run_retention_held", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	private static void AssertAbsentOrNull(JsonElement element, string propertyName) =>
		Assert.True(
			!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null,
			$"expected '{propertyName}' to be absent or null");

	private async Task<HttpResponseMessage> SendRetentionHoldAsync(HttpMethod method, Guid runId, string role, string? reason)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(method, $"/api/v1/runs/{runId}/retention-hold");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { reason }),
			System.Text.Encoding.UTF8, "application/json");
		return await client.SendAsync(request);
	}

	// -- issue #745: GET /runs/{id}/component-results/summary -----------------------

	[Fact]
	public async Task GetComponentResultsSummary_UnknownRun_Returns404()
	{
		Guid runId = Guid.NewGuid();

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/component-results/summary");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetComponentResultsSummary_NoAuthHeader_Returns401()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));

		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await client.GetAsync($"/api/v1/runs/{runId}/component-results/summary");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetComponentResultsSummary_ViewerRole_Returns200WithRollup()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));
		_factory.ComponentResults.NextRollup = new Waypoint.Core.Scans.RunResultRollup(
			runId,
			PlannedComponentCount: 3,
			ByStatus:
			[
				new Waypoint.Core.Scans.RunResultRollupRow("completed", ComponentCount: 2, CatIOpen: 1, CatIIOpen: 0, CatIIIOpen: 2, PassedCount: 10, NotApplicableCount: 1, NotReviewedCount: 0, SkippedCount: 0),
				new Waypoint.Core.Scans.RunResultRollupRow("execution_error", ComponentCount: 1, CatIOpen: 0, CatIIOpen: 0, CatIIIOpen: 0, PassedCount: 0, NotApplicableCount: 0, NotReviewedCount: 1, SkippedCount: 0),
			]);

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/component-results/summary");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = doc.RootElement;
		Assert.Equal(runId.ToString(), root.GetProperty("run_id").GetString());
		Assert.Equal(3, root.GetProperty("planned_component_count").GetInt32());

		JsonElement byStatus = root.GetProperty("by_status");
		Assert.Equal(2, byStatus.GetArrayLength());

		JsonElement completedRow = byStatus.EnumerateArray().Single(e => e.GetProperty("status").GetString() == "completed");
		Assert.Equal(2, completedRow.GetProperty("component_count").GetInt32());
		Assert.Equal(1, completedRow.GetProperty("cat_i_open").GetInt32());
		Assert.Equal(2, completedRow.GetProperty("cat_iii_open").GetInt32());
		Assert.Equal(10, completedRow.GetProperty("passed_count").GetInt32());

		JsonElement errorRow = byStatus.EnumerateArray().Single(e => e.GetProperty("status").GetString() == "execution_error");
		Assert.Equal(1, errorRow.GetProperty("not_reviewed_count").GetInt32());

		Assert.Equal(runId, _factory.ComponentResults.LastRollupRunId);
	}

	[Fact]
	public async Task GetComponentResultsSummary_NoComponentResultsYet_ReturnsEmptyByStatusNot404()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		_factory.ComponentResults.NextRollup = new Waypoint.Core.Scans.RunResultRollup(runId, PlannedComponentCount: 5, ByStatus: []);

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/component-results/summary");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(5, doc.RootElement.GetProperty("planned_component_count").GetInt32());
		Assert.Equal(0, doc.RootElement.GetProperty("by_status").GetArrayLength());
	}

	// -- issue #592 (epic #588, last child): generic operational-history deletion ---

	[Theory]
	[InlineData(null)]
	[InlineData("purge")]
	[InlineData("delete")]
	public async Task DeleteRunHistory_WithoutExactConfirmation_Returns400(string? confirmation)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/runs/{Guid.NewGuid()}/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task DeleteRunHistory_BelowAdmin_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/runs/{Guid.NewGuid()}/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "DELETE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		// [RequireAdminRole] rejects before the controller action's own confirmation
		// check ever runs -- same proof shape as PurgeRun_BelowAdmin_Returns403.
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task DeleteRunHistory_UnknownRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/runs/{Guid.NewGuid()}/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "DELETE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task DeleteRunHistory_NonTerminalRun_Returns409()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		_factory.Repository.SetRunType(runId, "discover");

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/runs/{runId}/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "DELETE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("run_not_terminal", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DeleteRunHistory_UnpurgedComplianceRun_Returns409RequiresDomainPurgeFirst()
	{
		// Default RunType from FakeJobQueueRepository.GetRunAsync is "scan" (a
		// compliance-owned type) unless overridden -- exercises the epic #588 "generic
		// cleanup DEFERS to domain purge" gate without needing SetRunType.
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));
		_factory.HistoryDeletionRepository.SetPurged(runId, purged: false);

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/runs/{runId}/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "DELETE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("requires_domain_purge_first", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DeleteRunHistory_PurgedComplianceRun_Returns200AndWritesTombstone()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));
		_factory.HistoryDeletionRepository.SetPurged(runId, purged: true);

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/runs/{runId}/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "DELETE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Equal("Completed", doc.RootElement.GetProperty("outcome").GetString());
		Assert.NotNull(await _factory.HistoryDeletionRepository.GetTombstoneAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task DeleteRunHistory_NonComplianceRun_Returns200WithoutPurgeGate()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));
		_factory.Repository.SetRunType(runId, "discover");
		// Deliberately never purged -- a discover run has no RunPurgeService scope at
		// all (issue #594 is scan/remediate-only), so the gate must not apply here.

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/runs/{runId}/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { confirmation = "DELETE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task DeleteRunHistory_AlreadyDeleted_IsIdempotentNoOp()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));
		_factory.Repository.SetRunType(runId, "discover");

		HttpClient client = _factory.CreateClient();

		HttpRequestMessage first = new(HttpMethod.Delete, $"/api/v1/runs/{runId}/history");
		first.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		first.Content = new StringContent(JsonSerializer.Serialize(new { confirmation = "DELETE" }), System.Text.Encoding.UTF8, "application/json");
		HttpResponseMessage firstResponse = await client.SendAsync(first);
		Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

		HttpRequestMessage second = new(HttpMethod.Delete, $"/api/v1/runs/{runId}/history");
		second.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		second.Content = new StringContent(JsonSerializer.Serialize(new { confirmation = "DELETE" }), System.Text.Encoding.UTF8, "application/json");
		HttpResponseMessage secondResponse = await client.SendAsync(second);

		Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
		string body = await secondResponse.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Equal("AlreadyDeleted", doc.RootElement.GetProperty("outcome").GetString());
	}

	[Fact]
	public async Task GetRunHistoryDeletionStatus_NeverRequested_Returns404()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("completed", false, false, null, "alice"));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task CreateRun_InitiatedByComesFromIdentity_NotFromBody()
	{
		// Non-scan run_type -- see CreateRun_WithCyberRole_Returns202's comment.
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");
		// initiated_by is no longer part of the contract; a caller supplying it
		// anyway must not influence the recorded initiator.
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { run_type = "download", scope = "{}", initiated_by = "mallory" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal("test-user", _factory.Repository.LastCreateRun!.Value.InitiatedBy);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task GetRun_WithAnyAuthenticatedRole_ReturnsOk(string role)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{_factory.RunId}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task GetJobs_WithAnyAuthenticatedRole_ReturnsOk(string role)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{_factory.RunId}/jobs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task PauseRun_WithRoleBelowCyber_Returns403()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>
	/// Issue #757's "Cyber controls owned live scans" owner decision lowered
	/// pause/resume/abort's floor from Operator+ to Cyber+ (docs/api-contract.md's
	/// role matrix, PR #819) -- a Cyber caller on their OWN run now succeeds.
	/// </summary>
	[Fact]
	public async Task PauseRun_WithCyberRole_ReturnsOk()
	{
		// TestAuthHandler names every principal "test-user"; this run must be owned
		// by the caller for a non-Admin Cyber caller to act on it (issue #209).
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetPauseResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task ResumeRun_WithRoleBelowCyber_Returns403()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/resume");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task ResumeRun_WithCyberRole_ReturnsOk()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", true, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetResumeResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/resume");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task AbortRun_WithRoleBelowCyber_Returns403()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/abort");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task AbortRun_WithCyberRole_ReturnsOk()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/abort");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	// -- issue #209: run-action ownership ------------------------------------
	// docs/api-contract.md: "/runs/{id}/pause · /resume · /abort ... Cyber+ (own
	// runs), Admin any" (floor lowered from Operator+ by issue #757's "Cyber controls
	// owned live scans" decision, PR #819). TestAuthHandler names every principal
	// "test-user".

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_OwnerCyber_ReturnsOk(string action)
	{
		// Paused must match the pre-condition each action needs to succeed: resume
		// requires an already-paused run, pause/abort require an unpaused one.
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetPauseResult(true);
		_factory.Repository.SetResumeResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_NonOwnerCyber_Returns403(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: "another-user"));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
	}

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_Admin_SucceedsOnNonOwnedRun(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: "another-user"));
		_factory.Repository.SetPauseResult(true);
		_factory.Repository.SetResumeResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_CyberOnOwnerlessRun_Returns403(string action)
	{
		// InitiatedBy null simulates a system/scheduled run with no recorded initiator.
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: null));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
	}

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_AdminOnOwnerlessRun_ReturnsOk(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: null));
		_factory.Repository.SetPauseResult(true);
		_factory.Repository.SetResumeResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	// -- issue #757: POST /runs/{id}/jobs/bulk-cancel · bulk-retry -----------
	// Controller-level wiring against FakeJobQueueRepository/FakeComponentJobRepository;
	// real per-item state-machine/audit correctness lives in
	// Waypoint.Tests.Infrastructure.Postgres.BulkJobActionApiTests (real Postgres).

	private async Task<HttpResponseMessage> PostBulkAsync(string action, Guid runId, object body, string role = "Cyber")
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/bulk-{action}")
		{
			Content = System.Net.Http.Json.JsonContent.Create(body),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		return await client.SendAsync(request);
	}

	[Theory]
	[InlineData("cancel")]
	[InlineData("retry")]
	public async Task BulkJobAction_UnknownRun_Returns404(string action)
	{
		HttpResponseMessage response = await PostBulkAsync(action, Guid.NewGuid(), new { job_ids = new[] { Guid.NewGuid().ToString() } });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Theory]
	[InlineData("cancel")]
	[InlineData("retry")]
	public async Task BulkJobAction_BelowCyber_Returns403(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));

		HttpResponseMessage response = await PostBulkAsync(action, _factory.RunId, new { job_ids = new[] { Guid.NewGuid().ToString() } }, role: "Viewer");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[InlineData("cancel")]
	[InlineData("retry")]
	public async Task BulkJobAction_NonOwnerCyber_Returns403(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "another-user"));

		HttpResponseMessage response = await PostBulkAsync(action, _factory.RunId, new { job_ids = new[] { Guid.NewGuid().ToString() } });

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
	}

	[Theory]
	[InlineData("cancel")]
	[InlineData("retry")]
	public async Task BulkJobAction_NeitherJobIdsNorFilter_Returns400(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));

		HttpResponseMessage response = await PostBulkAsync(action, _factory.RunId, new { });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Theory]
	[InlineData("cancel")]
	[InlineData("retry")]
	public async Task BulkJobAction_BothJobIdsAndFilter_Returns400(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));

		string[] failedFilterStates = ["failed"];
		HttpResponseMessage response = await PostBulkAsync(
			action, _factory.RunId,
			new { job_ids = new[] { Guid.NewGuid().ToString() }, filter = new { state = failedFilterStates } });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task BulkJobAction_TooManyExplicitJobIds_Returns400()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		string[] jobIds = [.. Enumerable.Range(0, 501).Select(_ => Guid.NewGuid().ToString())];

		HttpResponseMessage response = await PostBulkAsync("cancel", _factory.RunId, new { job_ids = jobIds });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "too_many_matches");
	}

	[Fact]
	public async Task BulkJobAction_FilterMatchesTooMany_Returns400WithoutMutatingAnything()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		_factory.ComponentJobs.ResolvedJobIds = [.. Enumerable.Range(0, 501).Select(_ => Guid.NewGuid())];
		string[] queuedFilterStates = ["queued"];

		HttpResponseMessage response = await PostBulkAsync("cancel", _factory.RunId, new { filter = new { state = queuedFilterStates } });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "too_many_matches");
	}

	[Fact]
	public async Task BulkJobAction_MalformedFilter_Returns400()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		string[] bogusFilterStates = ["bogus"];

		HttpResponseMessage response = await PostBulkAsync("cancel", _factory.RunId, new { filter = new { state = bogusFilterStates } });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task BulkCancelJobs_ExplicitJobIds_ForwardsToRepositoryAndMapsOutcomes()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		Guid jobA = Guid.NewGuid();
		Guid jobB = Guid.NewGuid();
		_factory.Repository.NextBulkCancelResult = new BulkJobActionResult<JobCancelOutcome>(
		[
			new BulkJobItemResult<JobCancelOutcome>(jobA, JobCancelOutcome.Cancelled),
			new BulkJobItemResult<JobCancelOutcome>(jobB, JobCancelOutcome.NotCancellable),
		]);

		HttpResponseMessage response = await PostBulkAsync("cancel", _factory.RunId, new { job_ids = new[] { jobA.ToString(), jobB.ToString() } });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(2, body.RootElement.GetProperty("resolved_count").GetInt32());
		JsonElement items = body.RootElement.GetProperty("items");
		Assert.Equal(2, items.GetArrayLength());
		Assert.Equal("cancelled", items[0].GetProperty("outcome").GetString());
		Assert.Equal("not_cancellable", items[1].GetProperty("outcome").GetString());
		Assert.Equal([jobA, jobB], _factory.Repository.LastBulkCancelJobIds);
	}

	[Fact]
	public async Task BulkRetryJobs_FilterResolvesJobIdsServerSide_BeforeMutation()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		Guid jobA = Guid.NewGuid();
		_factory.ComponentJobs.ResolvedJobIds = [jobA];
		_factory.Repository.NextBulkRetryResult = new BulkJobActionResult<JobRetryOutcome>(
		[
			new BulkJobItemResult<JobRetryOutcome>(jobA, JobRetryOutcome.Retried),
		]);
		string[] failedFilterStates = ["failed"];

		HttpResponseMessage response = await PostBulkAsync("retry", _factory.RunId, new { filter = new { state = failedFilterStates } });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(1, body.RootElement.GetProperty("resolved_count").GetInt32());
		Assert.Equal("queued", body.RootElement.GetProperty("items")[0].GetProperty("outcome").GetString());
		Assert.Equal(_factory.RunId, _factory.ComponentJobs.LastResolveRunId);
		Assert.Equal(["failed"], _factory.ComponentJobs.LastResolveFilter!.States);
		Assert.Equal([jobA], _factory.Repository.LastBulkRetryJobIds);
	}

	// -- 404 tests ----------------------------------------------------------

	[Fact]
	public async Task GetRun_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{Guid.NewGuid()}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task GetJobs_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{Guid.NewGuid()}/jobs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task PauseRun_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task ResumeRun_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/resume");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task AbortRun_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/abort");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	// -- issue #581: bounded historical event/log reads ----------------------

	private static readonly string[] ExpectedKindFilter = ["job.log", "job.state"];
	private static readonly string[] ExpectedLevelFilter = ["warning", "error"];

	[Fact]
	public async Task GetEventHistory_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{Guid.NewGuid()}/events/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task GetEventHistory_BelowViewer_Returns401Unauthenticated()
	{
		// No X-Test-Role header at all -- TestAuthHandler treats this as unauthenticated
		// (matches the precedent other endpoints' role-guard tests use for the
		// "no floor met" case; Viewer is already this endpoint's floor, so there is no
		// authenticated-but-too-low role to exercise here).
		HttpClient client = _factory.CreateClient();
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/events/history");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetEventHistory_ExistingRunNoEvents_ReturnsEmptyPageNotNotFound()
	{
		HttpClient client = _factory.CreateClient();
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		_factory.EventHistory.NextPage = new JobEventHistoryPage([], null);
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/events/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Empty(doc.RootElement.GetProperty("items").EnumerateArray());
		// WaypointJsonOptions omits null properties entirely (WhenWritingNull) -- a
		// null next_cursor is therefore an absent key, not a JSON null, matching every
		// other nullable field in this API (e.g. RunResponse.blocked_reason).
		Assert.False(doc.RootElement.TryGetProperty("next_cursor", out _));
	}

	[Fact]
	public async Task GetEventHistory_MapsJobIdKindLevelAndLimitToTheQuery()
	{
		HttpClient client = _factory.CreateClient();
		Guid runId = Guid.NewGuid();
		Guid jobId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		_factory.EventHistory.NextPage = new JobEventHistoryPage([], null);
		HttpRequestMessage request = new(
			HttpMethod.Get,
			$"/api/v1/runs/{runId}/events/history?job_id={jobId}&kind=job.log,job.state&level=warning,error&limit=25");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		JobEventHistoryQuery? query = _factory.EventHistory.LastQuery;
		Assert.NotNull(query);
		Assert.Equal(runId, query!.RunId);
		Assert.Equal(jobId, query.JobId);
		Assert.Equal(ExpectedKindFilter, query.EventTypes);
		Assert.Equal(ExpectedLevelFilter, query.Severities);
		Assert.Equal(25, query.Limit);
		Assert.Null(query.AfterSeq);
	}

	[Fact]
	public async Task GetEventHistory_UnknownKind_Returns400ValidationError()
	{
		HttpClient client = _factory.CreateClient();
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/events/history?kind=not.a.real.kind");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task GetEventHistory_UnknownLevel_Returns400ValidationError()
	{
		HttpClient client = _factory.CreateClient();
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/events/history?level=catastrophic");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task GetEventHistory_MalformedJobId_Returns400ValidationError()
	{
		HttpClient client = _factory.CreateClient();
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/events/history?job_id=not-a-guid");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Theory]
	[InlineData("not-base64-!!!")]
	[InlineData("aGVsbG8=")] // valid base64, wrong prefix/content ("hello")
	[InlineData("djE6LTE=")] // "v1:-1" -- negative seq, must be rejected not just non-numeric
	public async Task GetEventHistory_GarbageCursor_Returns400NotServerError(string garbageCursor)
	{
		HttpClient client = _factory.CreateClient();
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/events/history?cursor={Uri.EscapeDataString(garbageCursor)}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task GetEventHistory_ValidCursorFromEncode_RoundTripsToAfterSeq()
	{
		HttpClient client = _factory.CreateClient();
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		_factory.EventHistory.NextPage = new JobEventHistoryPage([], null);
		string cursor = Waypoint.Api.Contracts.JobEventCursor.Encode(42);
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/events/history?cursor={Uri.EscapeDataString(cursor)}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(42, _factory.EventHistory.LastQuery?.AfterSeq);
	}

	[Fact]
	public async Task GetEventHistory_TruncatedPage_EmitsEncodedNextCursor()
	{
		HttpClient client = _factory.CreateClient();
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "alice"));
		_factory.EventHistory.NextPage = new JobEventHistoryPage(
			[new StreamedJobEvent(7, "job.log", null, runId, "{\"severity\":\"information\",\"line\":\"hi\"}", DateTimeOffset.UtcNow)],
			NextCursor: 7);
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{runId}/events/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string? nextCursor = doc.RootElement.GetProperty("next_cursor").GetString();
		Assert.NotNull(nextCursor);
		Assert.True(Waypoint.Api.Contracts.JobEventCursor.TryDecode(nextCursor!, out long decoded));
		Assert.Equal(7, decoded);

		JsonElement item = doc.RootElement.GetProperty("items").EnumerateArray().Single();
		Assert.Equal(7, item.GetProperty("seq").GetInt64());
		Assert.Equal("job.log", item.GetProperty("type").GetString());
		Assert.Equal("information", item.GetProperty("data").GetProperty("severity").GetString());
	}

	// -- issue #708/#689: GET /runs/history -----------------------------------

	[Fact]
	public async Task ListRunHistory_BelowViewer_Returns401Unauthenticated()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs/history");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task ListRunHistory_NoFilters_MapsToDefaultQuery()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		RunHistoryQuery? query = _factory.Repository.LastRunHistoryQuery;
		Assert.NotNull(query);
		Assert.Null(query!.States);
		Assert.Null(query.RunTypes);
		Assert.Null(query.Since);
		Assert.Null(query.Until);
		Assert.Null(query.AfterCreatedAt);
		Assert.Null(query.AfterId);
		Assert.Equal(50, query.Limit);
	}

	[Fact]
	public async Task ListRunHistory_MapsStateRunTypeSinceUntilAndLimit()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(
			HttpMethod.Get,
			"/api/v1/runs/history?state=completed,aborted&run_type=discover,download&since=2026-01-01T00:00:00Z&until=2026-06-01T00:00:00Z&limit=25");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		RunHistoryQuery? query = _factory.Repository.LastRunHistoryQuery;
		Assert.NotNull(query);
		Assert.Equal(["completed", "aborted"], query!.States);
		Assert.Equal(["discover", "download"], query.RunTypes);
		Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), query.Since);
		Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), query.Until);
		Assert.Equal(25, query.Limit);
	}

	[Fact]
	public async Task ListRunHistory_LimitAboveMax_IsClampedTo200()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs/history?limit=99999");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(200, _factory.Repository.LastRunHistoryQuery?.Limit);
	}

	[Fact]
	public async Task ListRunHistory_UnknownState_Returns400ValidationError()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs/history?state=not-a-real-state");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task ListRunHistory_UnknownRunType_Returns400ValidationError()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs/history?run_type=not-a-real-type");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	// The three run types migration 0042 added to runs_run_type_check
	// (credential-test, tool-install, purge) are gate=None non-compliance history
	// (docs/domain-model.md) and MUST be accepted as run_type filter values -- PR #712's
	// review caught them 400ing because RunTypes.All was stale against the constraint.
	[Theory]
	[InlineData("credential-test")]
	[InlineData("tool-install")]
	[InlineData("purge")]
	public async Task ListRunHistory_AcceptsEveryRunTypeAddedByMigration0042(string runType)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/history?run_type={runType}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal([runType], _factory.Repository.LastRunHistoryQuery?.RunTypes);
	}

	[Fact]
	public async Task ListRunHistory_UnparseableSince_Returns400ValidationError()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs/history?since=not-a-timestamp");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Theory]
	[InlineData("not-base64-!!!")]
	[InlineData("aGVsbG8=")] // valid base64, wrong prefix/content ("hello")
	public async Task ListRunHistory_GarbageCursor_Returns400NotServerError(string garbageCursor)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/history?cursor={Uri.EscapeDataString(garbageCursor)}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task ListRunHistory_ValidCursorFromEncode_RoundTripsToKeyset()
	{
		HttpClient client = _factory.CreateClient();
		DateTimeOffset createdAt = DateTimeOffset.Parse("2026-03-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
		Guid id = Guid.NewGuid();
		string cursor = Waypoint.Api.Contracts.RunHistoryCursor.Encode(createdAt, id);
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/history?cursor={Uri.EscapeDataString(cursor)}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(createdAt, _factory.Repository.LastRunHistoryQuery?.AfterCreatedAt);
		Assert.Equal(id, _factory.Repository.LastRunHistoryQuery?.AfterId);
	}

	[Fact]
	public async Task ListRunHistory_TruncatedPage_EmitsEncodedNextCursor()
	{
		HttpClient client = _factory.CreateClient();
		RunSummary item = new(
			Id: Guid.NewGuid(), RunType: "discover", State: "completed", Paused: false, Blocked: false,
			BlockedReason: null, ScopeJson: "{}", CredentialId: null, InitiatedBy: "tester", ScheduleId: null,
			CreatedAt: "2026-03-01T12:00:00Z", StartedAt: null, CompletedAt: null,
			JobCount: 0, JobCountQueued: 0, JobCountRunning: 0, JobCountCompleted: 0, JobCountFailed: 0, JobCountBlocked: 0);
		_factory.Repository.SetRunHistoryPage(new RunHistoryPage([item], HasMore: true));
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string? nextCursor = doc.RootElement.GetProperty("next_cursor").GetString();
		Assert.NotNull(nextCursor);
		Assert.True(Waypoint.Api.Contracts.RunHistoryCursor.TryDecode(nextCursor!, out DateTimeOffset decodedCreatedAt, out Guid decodedId));
		Assert.Equal(item.Id, decodedId);
		Assert.Equal(DateTimeOffset.Parse(item.CreatedAt!, System.Globalization.CultureInfo.InvariantCulture), decodedCreatedAt);

		JsonElement returned = doc.RootElement.GetProperty("items").EnumerateArray().Single();
		Assert.Equal(item.Id.ToString(), returned.GetProperty("id").GetString());
	}

	[Fact]
	public async Task ListRunHistory_NotTruncated_OmitsNextCursor()
	{
		HttpClient client = _factory.CreateClient();
		_factory.Repository.SetRunHistoryPage(new RunHistoryPage([], HasMore: false));
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs/history");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.False(doc.RootElement.TryGetProperty("next_cursor", out _));
	}

	// -- state-transition validation ----------------------------------------

	[Fact]
	public async Task PauseRun_AlreadyPaused_Returns400()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("pending", true, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetPauseResult(false);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task ResumeRun_NotPaused_Returns400()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetResumeResult(false);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/resume");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	// -- response shape tests -----------------------------------------------

	[Fact]
	public async Task PauseRun_ReturnsPostActionState()
	{
		// Pre-action state is "running"/not-paused; after pause the fake updates is_paused.
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetPauseResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		// The controller re-fetches state after the action; the fake updates is_paused=true.
		Assert.Equal("running", doc.RootElement.GetProperty("state").GetString());
	}

	// -- issue #210: GET /runs list ------------------------------------------

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task ListRuns_WithAnyAuthenticatedRole_ReturnsOk(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task ListRuns_WithoutAuthentication_Returns401()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/api/v1/runs");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task ListRuns_ForwardsLimitAndOffsetToRepository()
	{
		_factory.Repository.SetListRunsResult([], totalCount: 0);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs?limit=5&offset=10");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal((5, 10), _factory.Repository.LastListRuns);
	}

	[Fact]
	public async Task ListRuns_SetsXTotalCountHeader_FromRepositoryTotal_NotPageSize()
	{
		RunSummary run = new(
			Id: Guid.NewGuid(), RunType: "scan", State: "running", Paused: false, Blocked: false,
			BlockedReason: null, ScopeJson: "{}", CredentialId: null, InitiatedBy: "test-user", ScheduleId: null,
			CreatedAt: "2026-01-01T00:00:00Z", StartedAt: null, CompletedAt: null,
			JobCount: 0, JobCountQueued: 0, JobCountRunning: 0, JobCountCompleted: 0,
			JobCountFailed: 0, JobCountBlocked: 0);
		// One item on the page, but a much larger total collection -- proves the header
		// reflects the repository's reported total rather than the returned page size.
		_factory.Repository.SetListRunsResult([run], totalCount: 42);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/runs?limit=1&offset=0");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? values));
		Assert.Equal("42", values!.Single());
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Single(doc.RootElement.EnumerateArray());
	}

	// -- issue #210: truthful abort response ---------------------------------

	[Fact]
	public async Task AbortRun_OnRunningRun_ReturnsAbortedStateFromReFetch()
	{
		// The fake now mutates 'running' -> 'aborted' inside AbortRunAsync itself (same
		// no-op semantics as the real repository). This test only passes if the
		// controller re-fetches state AFTER calling AbortRunAsync; re-fetching before
		// the action (or skipping the re-fetch and returning the pre-action state)
		// would observe "running" instead. Verified locally by temporarily swapping the
		// re-fetch above the AbortRunAsync call in RunsController.AbortRun: this test
		// failed (expected "aborted", got "running"), then the swap was reverted.
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/abort");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Equal("aborted", doc.RootElement.GetProperty("state").GetString());
	}

	[Fact]
	public async Task AbortRun_AlreadyTerminal_ReturnsActualStateNotAbortedLiteral()
	{
		// AbortRunAsync is a no-op against a run outside pending/running (see
		// JobQueueRepository.AbortRunAsync); the fake mirrors that by leaving the
		// stored state untouched. The response must report the real ("done") state,
		// not the previous hardcoded "aborted" literal.
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("done", false, false, null, InitiatedBy: "test-user"));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/abort");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Equal("done", doc.RootElement.GetProperty("state").GetString());
	}

	[Fact]
	public async Task CreateRun_ReturnsRunId()
	{
		// Non-scan run_type -- see CreateRun_WithCyberRole_Returns202's comment.
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");
		request.Content = new StringContent(
			System.Text.Json.JsonSerializer.Serialize(new { run_type = "download", scope = "{}" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.NotNull(doc.RootElement.GetProperty("run_id").GetString());
	}
}

/// <summary>
/// Test host that injects a <see cref="FakeJobQueueRepository"/> so /runs endpoints
/// can be exercised without Postgres. Inherits from <see cref="WaypointApiFactory"/>
/// and swaps the auth scheme for <see cref="TestAuthHandler"/> so role injection
/// via <c>X-Test-Role</c> header works (same pattern as <see cref="RoleGuardedApiFactory"/>).
/// </summary>
public sealed class RunsTestApiFactory : WaypointApiFactory
{
	public Guid RunId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
	public FakeJobQueueRepository Repository { get; } = new();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureTestServices(services =>
		{
			// Swap auth scheme for test role injection (same as RoleGuardedApiFactory).
			services
				.AddAuthentication(TestAuthHandler.SchemeName)
				.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

			services.PostConfigure<AuthenticationOptions>(options =>
			{
				options.DefaultScheme = TestAuthHandler.SchemeName;
				options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
				options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
				options.DefaultForbidScheme = TestAuthHandler.SchemeName;
			});

			// Replace the default (no-op or Postgres) job repositories with our fake --
			// issue #415 split IJobQueueRepository into IJobControlRepository and
			// IJobRunnerRepository, so both registrations must be swapped for any
			// controller resolved through this host to see the fake.
			foreach (Type serviceType in new[] { typeof(IJobControlRepository), typeof(IJobRunnerRepository) })
			{
				var descriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);
				if (descriptor != null)
				{
					services.Remove(descriptor);
				}
			}
			services.AddSingleton<IJobControlRepository>(Repository);
			services.AddSingleton<IJobRunnerRepository>(Repository);

			// Issue #594: RunPurgeService (resolved through RunsController) depends on
			// IRunPurgeRepository -- the real Npgsql implementation would otherwise try
			// to connect to the unreachable "postgres" host baked into
			// appsettings.json's default ConnectionStrings:Waypoint, turning every
			// purge-endpoint test into a 500 rather than exercising the controller's
			// own outcome-to-status-code mapping. Same fake-repository swap pattern as
			// IJobControlRepository/IJobRunnerRepository above.
			var purgeRepositoryDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Waypoint.Core.Runs.IRunPurgeRepository));
			if (purgeRepositoryDescriptor != null)
			{
				services.Remove(purgeRepositoryDescriptor);
			}
			services.AddSingleton<Waypoint.Core.Runs.IRunPurgeRepository>(PurgeRepository);

			// Issue #592: same fake-swap pattern as IRunPurgeRepository above, for
			// RunHistoryDeletionService's IRunHistoryDeletionRepository dependency.
			var historyDeletionRepositoryDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Waypoint.Core.Runs.IRunHistoryDeletionRepository));
			if (historyDeletionRepositoryDescriptor != null)
			{
				services.Remove(historyDeletionRepositoryDescriptor);
			}
			services.AddSingleton<Waypoint.Core.Runs.IRunHistoryDeletionRepository>(HistoryDeletionRepository);

			// Issue #784: same fake-swap pattern as IRunPurgeRepository above, for
			// RunPurgeService's new IRunRetentionHoldRepository dependency (the
			// purge-exclusion check) AND RunRetentionHoldService's own dependency.
			var retentionHoldRepositoryDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Waypoint.Core.Runs.IRunRetentionHoldRepository));
			if (retentionHoldRepositoryDescriptor != null)
			{
				services.Remove(retentionHoldRepositoryDescriptor);
			}
			services.AddSingleton<Waypoint.Core.Runs.IRunRetentionHoldRepository>(RetentionHoldRepository);

			// Issue #581: GetEventHistory resolves IJobEventHistoryReader through
			// RunsController -- same fake-swap pattern as every other dependency above,
			// so role-guard/happy-path tests never touch Postgres.
			var eventHistoryDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IJobEventHistoryReader));
			if (eventHistoryDescriptor != null)
			{
				services.Remove(eventHistoryDescriptor);
			}
			services.AddSingleton<IJobEventHistoryReader>(EventHistory);

			// Issue #757: GetComponentJobCounts/ListComponentJobs resolve
			// IComponentJobRepository through RunsController -- same fake-swap pattern
			// as every other dependency above (ComponentJobEndpointTests is the
			// consumer; real SQL correctness lives in
			// Waypoint.Tests.Infrastructure.Postgres.ComponentJobQueryRepositoryTests).
			var componentJobsDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IComponentJobRepository));
			if (componentJobsDescriptor != null)
			{
				services.Remove(componentJobsDescriptor);
			}
			services.AddSingleton<IComponentJobRepository>(ComponentJobs);

			// Issue #745: GetComponentResultsSummary resolves IComponentResultRepository
			// through RunsController -- same fake-swap pattern as every other dependency
			// above, so role-guard/happy-path tests never touch Postgres.
			var componentResultsDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Waypoint.Core.Scans.IComponentResultRepository));
			if (componentResultsDescriptor != null)
			{
				services.Remove(componentResultsDescriptor);
			}
			services.AddSingleton<Waypoint.Core.Scans.IComponentResultRepository>(ComponentResults);
		});
	}

	public FakeRunPurgeRepository PurgeRepository { get; } = new();

	public FakeRunHistoryDeletionRepository HistoryDeletionRepository { get; } = new();

	public FakeRunRetentionHoldRepository RetentionHoldRepository { get; } = new();

	public FakeJobEventHistoryReader EventHistory { get; } = new();

	public FakeComponentJobRepository ComponentJobs { get; } = new();

	public FakeComponentResultRepository ComponentResults { get; } = new();
}

/// <summary>
/// Minimal fake <see cref="IComponentJobRepository"/> for controller-level tests
/// (issue #757): records the last filter/query the controller mapped from the query
/// string and returns canned rows/pages, so <c>ComponentJobEndpointTests</c> can pin
/// the 200/404/400 mapping without Postgres -- SQL correctness lives in
/// <c>Waypoint.Tests.Infrastructure.Postgres.ComponentJobQueryRepositoryTests</c>.
/// </summary>
public sealed class FakeComponentJobRepository : IComponentJobRepository
{
	public Guid? LastCountsRunId { get; private set; }
	public ComponentJobFilter? LastCountsFilter { get; private set; }
	public ComponentJobListQuery? LastListQuery { get; private set; }

	public IReadOnlyList<ComponentJobCountRow> CountRows { get; set; } = [];
	public ComponentJobPage NextPage { get; set; } = new([], null);

	public Task<IReadOnlyList<ComponentJobCountRow>> GetGroupedCountsAsync(Guid runId, ComponentJobFilter filter, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		LastCountsRunId = runId;
		LastCountsFilter = filter;
		return Task.FromResult(CountRows);
	}

	public Task<ComponentJobPage> ListComponentJobsAsync(ComponentJobListQuery query, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		LastListQuery = query;
		return Task.FromResult(NextPage);
	}

	public Guid? LastResolveRunId { get; private set; }
	public ComponentJobFilter? LastResolveFilter { get; private set; }
	public int LastResolveMaxItems { get; private set; }
	public IReadOnlyList<Guid> ResolvedJobIds { get; set; } = [];

	public Task<IReadOnlyList<Guid>> ResolveJobIdsAsync(Guid runId, ComponentJobFilter filter, int maxItems, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		LastResolveRunId = runId;
		LastResolveFilter = filter;
		LastResolveMaxItems = maxItems;
		return Task.FromResult(ResolvedJobIds);
	}
}

/// <summary>
/// Minimal fake <see cref="Waypoint.Core.Scans.IComponentResultRepository"/> for
/// controller-level tests (role guard, 404, response-shape mapping) -- aggregation
/// truthfulness against real seeded data lives in
/// <c>Waypoint.Tests.Infrastructure.Postgres.ComponentResultRepositoryTests</c> against
/// a real database. Returns a canned rollup (empty by default, settable via
/// <see cref="NextRollup"/>); the write-path methods are never exercised by
/// <c>RunsController</c>'s read-only endpoint and return no-op defaults.
/// </summary>
public sealed class FakeComponentResultRepository : Waypoint.Core.Scans.IComponentResultRepository
{
	public Waypoint.Core.Scans.RunResultRollup NextRollup { get; set; } = new(Guid.Empty, 0, []);

	public Guid? LastRollupRunId { get; private set; }

	public Task RecordAsync(Waypoint.Core.Scans.ComponentResultRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

	public Task<int> NextAttemptNumberAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(1);

	public Task<Guid?> GetComponentIdForPlanItemAsync(Guid scanPlanItemId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

	public Task<Waypoint.Core.Scans.RunResultRollup> GetRunRollupAsync(Guid runId, CancellationToken cancellationToken)
	{
		LastRollupRunId = runId;
		return Task.FromResult(NextRollup with { RunId = runId });
	}

	public Waypoint.Core.Scans.ComponentResultFindingsPage NextFindingsPage { get; set; } = new(null, [], 0);

	public Guid? LastFindingsJobId { get; private set; }

	public Task<Waypoint.Core.Scans.ComponentResultFindingsPage> GetLatestFindingsAsync(Guid jobId, int limit, int offset, CancellationToken cancellationToken)
	{
		LastFindingsJobId = jobId;
		return Task.FromResult(NextFindingsPage);
	}

	public Waypoint.Core.Scans.ComponentResultArtifactsList NextArtifactsList { get; set; } = new(null, []);

	public Guid? LastArtifactsJobId { get; private set; }

	public Task<Waypoint.Core.Scans.ComponentResultArtifactsList> GetLatestArtifactsAsync(Guid jobId, CancellationToken cancellationToken)
	{
		LastArtifactsJobId = jobId;
		return Task.FromResult(NextArtifactsList);
	}
}

/// <summary>
/// Minimal fake <see cref="IJobEventHistoryReader"/> for controller-level tests
/// (role guards, 404, request-shape mapping) that don't need real Postgres paging
/// behavior -- that correctness lives in
/// <c>Waypoint.Tests.Infrastructure.Postgres.JobEventStreamServiceTests</c> against a
/// real database. Records the last <see cref="JobEventHistoryQuery"/> it received so
/// a test can assert the controller mapped query-string filters correctly, and
/// returns a canned page (empty by default, settable via <see cref="NextPage"/>).
/// </summary>
public sealed class FakeJobEventHistoryReader : IJobEventHistoryReader
{
	public JobEventHistoryQuery? LastQuery { get; private set; }

	public JobEventHistoryPage NextPage { get; set; } = new([], null);

	public Task<JobEventHistoryPage> ReadHistoryAsync(JobEventHistoryQuery query, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		LastQuery = query;
		return Task.FromResult(NextPage);
	}
}

/// <summary>
/// Minimal fake job repository for RunsController tests. Tracks state per run
/// GUID so that nonexistent run IDs return null (404). Only the methods exercised by
/// the controller are implemented; the rest return no-op defaults. Implements both
/// <see cref="IJobControlRepository"/> and <see cref="IJobRunnerRepository"/> (issue
/// #415's split of the former combined <c>IJobQueueRepository</c>) since this test host
/// swaps both registrations so every controller resolved through it sees one fake.
/// </summary>
public sealed class FakeJobQueueRepository : IJobControlRepository, IJobRunnerRepository
{
	private readonly Dictionary<Guid, RunQueueState> _runs = new();
	// Issue #592: per-run run_type override, defaulting to "scan" (GetRunAsync's
	// prior hardcoded value) so every pre-existing SetRun call site keeps behaving
	// identically -- only tests that need a non-compliance run_type (to exercise
	// RunHistoryDeletionService's compliance-purge gate) call SetRunType.
	private readonly Dictionary<Guid, string> _runTypes = new();
	private bool _pauseResult = true;
	private bool _resumeResult = true;

	public void SetRun(Guid runId, RunQueueState state)
	{
		_runs[runId] = state;
	}

	public void SetRunType(Guid runId, string runType)
	{
		_runTypes[runId] = runType;
	}

	public void SetPauseResult(bool result) => _pauseResult = result;
	public void SetResumeResult(bool result) => _resumeResult = result;

	public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(_runs.TryGetValue(runId, out var state) ? state : null);
	}

	public Task<RunSummary?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (!_runs.TryGetValue(runId, out var state))
		{
			return Task.FromResult<RunSummary?>(null);
		}
		string runType = _runTypes.TryGetValue(runId, out var overriddenType) ? overriddenType : "scan";
		return Task.FromResult<RunSummary?>(new RunSummary(
			Id: runId, RunType: runType, State: state.State, Paused: state.Paused,
			Blocked: state.Blocked, BlockedReason: state.BlockedReason,
			ScopeJson: "{}", CredentialId: null, InitiatedBy: "test-user", ScheduleId: null,
			CreatedAt: "2026-01-01T00:00:00Z", StartedAt: null, CompletedAt: null,
			JobCount: 0, JobCountQueued: 0, JobCountRunning: 0,
			JobCountCompleted: 0, JobCountFailed: 0, JobCountBlocked: 0));
	}

	/// <summary>Arguments of the last <see cref="ListRunsAsync"/> call, for asserting the
	/// controller forwards the bound page's limit/offset values unchanged.</summary>
	public (int Limit, int Offset)? LastListRuns { get; private set; }

	/// <summary>Total count the fake reports for <see cref="ListRunsAsync"/>; independent
	/// of how many <see cref="ListRunsItems"/> are returned, so tests can prove the
	/// header comes from the repository's reported total rather than the page size.</summary>
	public int ListRunsTotalCount { get; set; }

	public IReadOnlyList<RunSummary> ListRunsItems { get; set; } = [];

	public void SetListRunsResult(IReadOnlyList<RunSummary> items, int totalCount)
	{
		ListRunsItems = items;
		ListRunsTotalCount = totalCount;
	}

	public Task<RunListResult> ListRunsAsync(int limit, int offset, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		LastListRuns = (limit, offset);
		return Task.FromResult(new RunListResult(ListRunsItems, ListRunsTotalCount));
	}

	/// <summary>The last <see cref="RunHistoryQuery"/> the controller passed through, for
	/// asserting query-string -> query mapping (filters, cursor, limit clamp).</summary>
	public RunHistoryQuery? LastRunHistoryQuery { get; private set; }

	/// <summary>Canned page <see cref="ListRunHistoryAsync"/> returns -- set via
	/// <see cref="SetRunHistoryPage"/>; defaults to an empty, no-more page.</summary>
	private RunHistoryPage _runHistoryPage = new([], false);

	public void SetRunHistoryPage(RunHistoryPage page) => _runHistoryPage = page;

	public Task<RunHistoryPage> ListRunHistoryAsync(RunHistoryQuery query, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		LastRunHistoryQuery = query;
		return Task.FromResult(_runHistoryPage);
	}

	public Task<IReadOnlyList<JobSummary>> GetJobsForRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult<IReadOnlyList<JobSummary>>([]);
	}

	public Task<JobSummary?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
	{
		_ = (jobId, cancellationToken);
		return Task.FromResult<JobSummary?>(null);
	}

	/// <summary>Arguments of the last <see cref="CreateRunAsync"/> call, for asserting
	/// what the controller actually forwarded (issue #208: initiated_by provenance).</summary>
	public (string RunType, string ScopeJson, Guid? CredentialId, string? InitiatedBy)? LastCreateRun { get; private set; }

	/// <summary>The <c>scheduleId</c> of the last <see cref="CreateRunAsync"/> call (issue #515).</summary>
	public Guid? LastCreateRunScheduleId { get; private set; }

	public Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken, Guid? scheduleId = null)
	{
		_ = cancellationToken;
		LastCreateRun = (runType, scopeJson, credentialId, initiatedBy);
		LastCreateRunScheduleId = scheduleId;
		return Task.FromResult(Guid.NewGuid());
	}

	public Task<IReadOnlyList<Guid>> FanOutJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken)
	{
		_ = (runId, specs, createdBy, cancellationToken);
		return Task.FromResult<IReadOnlyList<Guid>>([]);
	}

	public Task<bool> CompleteEmptyRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (_pauseResult && _runs.TryGetValue(runId, out var state))
		{
			// Simulate the state transition: after a successful pause, is_paused becomes true.
			_runs[runId] = new RunQueueState(state.State, true, state.Blocked, state.BlockedReason, state.InitiatedBy);
		}
		return Task.FromResult(_pauseResult);
	}

	public Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (_resumeResult && _runs.TryGetValue(runId, out var state))
		{
			// Simulate the state transition: after a successful resume, is_paused becomes false.
			_runs[runId] = new RunQueueState(state.State, false, state.Blocked, state.BlockedReason, state.InitiatedBy);
		}
		return Task.FromResult(_resumeResult);
	}

	public Task<AbortRunResult> AbortRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		// Mirrors JobQueueRepository.AbortRunAsync's real no-op semantics: only a
		// 'pending' or 'running' run actually transitions to 'aborted'; anything else
		// (already terminal) is left untouched. Without this the controller's
		// re-fetch-after-action fix would be untested against a fake that never
		// changes state either way.
		if (_runs.TryGetValue(runId, out var state) &&
			(string.Equals(state.State, "pending", StringComparison.Ordinal) ||
			 string.Equals(state.State, "running", StringComparison.Ordinal)))
		{
			_runs[runId] = state with { State = "aborted" };
		}
		return Task.FromResult(new AbortRunResult([], []));
	}

	public Task<JobCancelOutcome> CancelJobAsync(Guid jobId, CancellationToken cancellationToken)
	{
		_ = (jobId, cancellationToken);
		return Task.FromResult(JobCancelOutcome.Cancelled);
	}

	public Task<JobRetryOutcome> RetryJobAsync(Guid jobId, string actor, CancellationToken cancellationToken)
	{
		_ = (jobId, actor, cancellationToken);
		return Task.FromResult(JobRetryOutcome.Retried);
	}

	public BulkJobActionResult<JobCancelOutcome>? NextBulkCancelResult { get; set; }
	public IReadOnlyList<Guid>? LastBulkCancelJobIds { get; private set; }

	public Task<BulkJobActionResult<JobCancelOutcome>> BulkCancelJobsAsync(Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken)
	{
		_ = (runId, actor, cancellationToken);
		LastBulkCancelJobIds = jobIds;
		return Task.FromResult(NextBulkCancelResult ?? new BulkJobActionResult<JobCancelOutcome>(
			[.. jobIds.Select(id => new BulkJobItemResult<JobCancelOutcome>(id, JobCancelOutcome.Cancelled))]));
	}

	public BulkJobActionResult<JobRetryOutcome>? NextBulkRetryResult { get; set; }
	public IReadOnlyList<Guid>? LastBulkRetryJobIds { get; private set; }

	public Task<BulkJobActionResult<JobRetryOutcome>> BulkRetryJobsAsync(Guid runId, IReadOnlyList<Guid> jobIds, string actor, CancellationToken cancellationToken)
	{
		_ = (runId, actor, cancellationToken);
		LastBulkRetryJobIds = jobIds;
		return Task.FromResult(NextBulkRetryResult ?? new BulkJobActionResult<JobRetryOutcome>(
			[.. jobIds.Select(id => new BulkJobItemResult<JobRetryOutcome>(id, JobRetryOutcome.Retried))]));
	}

	public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken)
	{
		_ = (workerId, leaseDuration, allowedJobTypes, cancellationToken);
		return Task.FromResult<ClaimedJob?>(null);
	}

	public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, leaseDuration, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken)
	{
		_ = (jobId, cancellationToken);
		return Task.FromResult(false);
	}

	public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, expectedFromState, toState, note, clearLease, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<bool> RequeueAtStageAsync(Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, expectedFromState, stage, note, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken)
	{
		_ = (batchSize, cancellationToken);
		return Task.FromResult<IReadOnlyList<RecoveredJob>>([]);
	}

	public Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken)
	{
		_ = (credentialId, threshold, cancellationToken);
		return Task.FromResult(new AuthFailureHaltResult(HaltTripped: false, [], []));
	}

	public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<CredentialUnblockResult> UnblockCredentialAsync(Guid credentialId, string? reason, CancellationToken cancellationToken)
	{
		_ = (credentialId, reason, cancellationToken);
		return Task.FromResult(new CredentialUnblockResult(WasHalted: false, [], []));
	}

	public Task<CredentialSwapResult> SwapAndResumeBlockedCredentialAsync(
		Guid runId, Guid replacementCredentialId, string actor, string? reason, CancellationToken cancellationToken)
	{
		_ = (runId, replacementCredentialId, actor, reason, cancellationToken);
		return Task.FromResult(new CredentialSwapResult(CredentialSwapOutcome.RunNotHalted, null, null, []));
	}

	public Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken)
	{
		_ = (jobId, uploadStatus, detail, cancellationToken);
		return Task.CompletedTask;
	}

	public Task RecordUploadAttemptAsync(Guid jobId, string? endpoint, string? collection, string uploadStatus, string? detail, CancellationToken cancellationToken)
	{
		_ = (jobId, endpoint, collection, uploadStatus, detail, cancellationToken);
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<UploadAttemptRecord>> GetUploadAttemptsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UploadAttemptRecord>>([]);

	public Task<IReadOnlyList<JobCredentialBinding>> GetJobCredentialBindingsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobCredentialBinding>>([]);
	public Task<IReadOnlyList<Guid>> FanOutAdditionalJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken) => throw new NotSupportedException("fan-out not exercised by this fake");
}

/// <summary>
/// Issue #594: minimal in-memory fake for <see cref="IRunPurgeRepository"/>, mirroring
/// <see cref="FakeJobQueueRepository"/>'s "track state per run GUID" shape. No test in
/// this file exercises the in-progress/retry/completion flow (that is
/// <c>RunPurgeServiceTests</c>' job, against real Postgres) -- this fake only needs to
/// avoid a live connection attempt so the controller's role/confirmation/not-found/
/// not-terminal mapping can be exercised without Postgres.
/// </summary>
public sealed class FakeRunPurgeRepository : IRunPurgeRepository
{
	public Task<RunPurgeStatus?> GetStatusAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult<RunPurgeStatus?>(null);
	}

	public Task<RunPurgeTombstone?> GetTombstoneAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult<RunPurgeTombstone?>(null);
	}

	public Task<IReadOnlyList<Guid>> ListPendingFinalizeRunIdsAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult<IReadOnlyList<Guid>>([]);
	}

	public Task<Guid?> FindRunIdByArtifactJobIdAsync(Guid artifactJobId, CancellationToken cancellationToken)
	{
		_ = (artifactJobId, cancellationToken);
		return Task.FromResult<Guid?>(null);
	}

	/// <summary>
	/// Issue #784: the artifact-deletion job RunRetentionHoldService cancels when a
	/// hold lands mid-purge. Null by default (no purge in flight); tests that need the
	/// cancel path to fire set it.
	/// </summary>
	public Guid? ArtifactJobId { get; set; }

	public Task<Guid?> GetArtifactJobIdAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult(ArtifactJobId);
	}

	public Task<RunPurgeStatus> CreateAsync(Guid runId, string requestedBy, string priorState, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(new RunPurgeStatus(runId, requestedBy, DateTimeOffset.UtcNow, priorState, false, "pending", 0, 0, null, null));
	}

	public Task MarkDbPhaseDoneAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.CompletedTask;
	}

	public Task MarkArtifactJobEnqueuedAsync(Guid runId, Guid jobId, int artifactsTotal, CancellationToken cancellationToken)
	{
		_ = (runId, jobId, artifactsTotal, cancellationToken);
		return Task.CompletedTask;
	}

	public Task ReportArtifactOutcomeAsync(Guid runId, bool succeeded, int artifactsDeleted, string? lastError, CancellationToken cancellationToken)
	{
		_ = (runId, succeeded, artifactsDeleted, lastError, cancellationToken);
		return Task.CompletedTask;
	}

	public Task<RunPurgeTombstone> CompleteAsync(Guid runId, string runType, string actor, string priorState, int artifactsDeleted, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(new RunPurgeTombstone(Guid.NewGuid(), runId, runType, priorState, actor, "completed", "{}", DateTimeOffset.UtcNow));
	}
}

/// <summary>
/// Issue #592: minimal in-memory fake for <see cref="IRunHistoryDeletionRepository"/>,
/// mirroring <see cref="FakeRunPurgeRepository"/>'s shape -- lets controller-level
/// tests exercise role/confirmation/not-found/not-terminal/compliance-gate mapping
/// without a live Postgres connection. <see cref="SetPurged"/> lets a test simulate a
/// compliance run that has (or has not) already been through <c>POST /runs/{id}/purge</c>.
/// </summary>
public sealed class FakeRunHistoryDeletionRepository : IRunHistoryDeletionRepository
{
	private readonly HashSet<Guid> _purgedRuns = new();
	private readonly Dictionary<Guid, RunHistoryDeletionTombstone> _tombstones = new();

	public void SetPurged(Guid runId, bool purged)
	{
		if (purged)
		{
			_purgedRuns.Add(runId);
		}
		else
		{
			_purgedRuns.Remove(runId);
		}
	}

	public Task<RunHistoryDeletionTombstone?> GetTombstoneAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(_tombstones.TryGetValue(runId, out var tombstone) ? tombstone : null);
	}

	public Task<bool> IsPurgedAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(_purgedRuns.Contains(runId));
	}

	public Task<RunHistoryDeletionTombstone> CompleteAsync(Guid runId, string runType, string actor, string priorState, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		RunHistoryDeletionTombstone tombstone = new(Guid.NewGuid(), runId, runType, priorState, actor, "completed", "{}", DateTimeOffset.UtcNow);
		_tombstones[runId] = tombstone;
		return Task.FromResult(tombstone);
	}

	/// <summary>Issue #708: candidates <see cref="FindRolloffCandidatesAsync"/> returns, set by test setup.</summary>
	public IReadOnlyList<Guid> RolloffCandidates { get; set; } = [];

	/// <summary>The last <c>(olderThan, limit)</c> arguments the sweep passed, for asserting it derives the cutoff from options correctly.</summary>
	public (DateTimeOffset OlderThan, int Limit)? LastRolloffQuery { get; private set; }

	public Task<IReadOnlyList<Guid>> FindRolloffCandidatesAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		LastRolloffQuery = (olderThan, limit);
		return Task.FromResult(RolloffCandidates);
	}
}

/// <summary>
/// Issue #784: minimal in-memory fake for <see cref="IRunRetentionHoldRepository"/>,
/// mirroring <see cref="FakeRunPurgeRepository"/>'s shape -- avoids a live Postgres
/// connection attempt so both <c>RunPurgeService</c>'s new hold-exclusion check and
/// the retention-hold endpoints' own controller-level role/reason/not-found mapping
/// can be exercised without Postgres. The full place/remove/audit/grant-drift/
/// purge-exclusion behavior against a real schema is
/// <c>RunRetentionHoldTests</c>/<c>RunPurgeComplianceEvidenceTests</c>' job.
/// </summary>
public sealed class FakeRunRetentionHoldRepository : IRunRetentionHoldRepository
{
	private readonly Dictionary<Guid, RunRetentionHold> _holds = new();

	public Task<RunRetentionHold?> GetAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(_holds.TryGetValue(runId, out RunRetentionHold? hold) ? hold : null);
	}

	public Task<bool> TryInsertAsync(Guid runId, string reason, string actor, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (_holds.ContainsKey(runId))
		{
			return Task.FromResult(false);
		}

		_holds[runId] = new RunRetentionHold(runId, reason, actor, DateTimeOffset.UtcNow);
		return Task.FromResult(true);
	}

	public Task<bool> TryRemoveAsync(Guid runId, string reason, string actor, CancellationToken cancellationToken)
	{
		_ = (reason, actor, cancellationToken);
		return Task.FromResult(_holds.Remove(runId));
	}
}
