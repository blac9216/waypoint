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

using System.Text.RegularExpressions;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// <c>JobsQueueClaimTests</c> proves, against a real Postgres under 32 concurrent
/// claimers, that a particular claim idiom never double-claims. That proof is only worth
/// anything to production if production actually uses the same idiom -- and the link
/// between the two was, until this file existed, a **prose comment**.
///
/// It was also wrong. PR #126 asserted the two queries were "byte-for-byte" identical;
/// the comment landed on `main` in PR #134, one slice before the production type it
/// named existed, so nobody could check it even in principle. They are not identical:
/// <c>JobsQueueClaimTests</c> carries an extra <c>run_id = $1</c> predicate so it cannot
/// race rows another test class seeded in the shared container, and its positional
/// parameters mean different things.
///
/// So this file replaces the claim with an assertion. It pins exactly the part that is
/// genuinely shared -- the <c>state = 'queued'</c> predicate and the
/// ordering/locking/limit clause, which is the entire content of the concurrency
/// guarantee -- and says nothing about the parts that legitimately differ. Edit either
/// query's clause and this fails.
///
/// No Postgres needed: this is a property of two string literals.
/// </summary>
public sealed class JobQueueClaimSqlParityTests
{
	/// <summary>
	/// The clause the #4 concurrency proof is actually about. `FOR UPDATE SKIP LOCKED` is
	/// what makes concurrent claimers skip a locked row instead of blocking on it and
	/// then re-reading a stale snapshot; `ORDER BY priority, created_at` plus `LIMIT 1`
	/// is what makes the claim deterministic and index-served
	/// (`idx_jobs_queue_claim`).
	/// </summary>
	private const string SharedOrderingAndLockingClause = "ORDER BY priority, created_at FOR UPDATE SKIP LOCKED LIMIT 1";

	private const string SharedPredicate = "state = 'queued'";

	[Fact]
	public void ProductionClaim_UsesTheClauseTheConcurrencyProofCovers()
	{
		string production = Normalize(JobQueueRepository.ClaimSql);

		Assert.Contains(SharedPredicate, production, StringComparison.Ordinal);
		Assert.Contains(SharedOrderingAndLockingClause, production, StringComparison.Ordinal);
	}

	[Fact]
	public void ProvenTestClaim_UsesTheSameClause()
	{
		string proven = Normalize(JobsQueueClaimTests.ClaimSql);

		Assert.Contains(SharedPredicate, proven, StringComparison.Ordinal);
		Assert.Contains(SharedOrderingAndLockingClause, proven, StringComparison.Ordinal);
	}

	/// <summary>
	/// The two claim queries agree on the whole locking CTE except for the test's
	/// run-scoping predicate. Asserting the *whole* CTE rather than only the clause is
	/// what catches a drift the two `Contains` tests above would miss -- for example a
	/// second predicate added to production (`AND attempt_count = 0`) that silently
	/// narrows what a dispatcher will ever claim while leaving the proven clause intact.
	/// </summary>
	[Fact]
	public void TheLockingCtesAgreeOnceTheTestsRunScopingIsRemoved()
	{
		string production = ExtractLockingCte(JobQueueRepository.ClaimSql);
		string proven = ExtractLockingCte(JobsQueueClaimTests.ClaimSql).Replace("run_id = $1 AND ", string.Empty, StringComparison.Ordinal);

		Assert.Equal(production, proven);
	}

	/// <summary>
	/// The guard has to be able to fail, or it is decoration. This drives the same
	/// comparison over a mutated copy of the production query -- the clause with
	/// `SKIP LOCKED` dropped, which is precisely the edit that reintroduces double
	/// claiming -- and asserts the comparison rejects it.
	/// </summary>
	[Fact]
	public void TheParityCheckRejectsAClauseThatLostSkipLocked()
	{
		string mutated = Normalize(JobQueueRepository.ClaimSql.Replace("FOR UPDATE SKIP LOCKED", "FOR UPDATE", StringComparison.Ordinal));

		Assert.DoesNotContain(SharedOrderingAndLockingClause, mutated, StringComparison.Ordinal);
	}

	/// <summary>And able to fail on a narrowed predicate, the subtler of the two drifts.</summary>
	[Fact]
	public void TheParityCheckRejectsANarrowedPredicate()
	{
		string mutated = ExtractLockingCte(
			JobQueueRepository.ClaimSql.Replace("WHERE state = 'queued'", "WHERE state = 'queued' AND attempt_count = 0", StringComparison.Ordinal));
		string proven = ExtractLockingCte(JobsQueueClaimTests.ClaimSql).Replace("run_id = $1 AND ", string.Empty, StringComparison.Ordinal);

		Assert.NotEqual(proven, mutated);
	}

	/// <summary>Collapses all runs of whitespace to single spaces so indentation and line breaks cannot make two equivalent queries look different.</summary>
	private static string Normalize(string sql) =>
		Regex.Replace(sql, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5)).Trim();

	/// <summary>The `claimable` CTE body -- everything between `SELECT id FROM jobs` and the closing paren -- normalized.</summary>
	private static string ExtractLockingCte(string sql)
	{
		string normalized = Normalize(sql);
		int start = normalized.IndexOf("SELECT id FROM jobs", StringComparison.Ordinal);
		Assert.True(start >= 0, "Claim query no longer opens its locking CTE with 'SELECT id FROM jobs'.");

		int end = normalized.IndexOf(')', start);
		Assert.True(end > start, "Claim query's locking CTE is not closed.");

		return normalized[start..end].Trim();
	}
}
