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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Configuration for the download-runner's managed-tool state mount (ADR-0015
/// decision 3, issue #441): the account-gated <c>vcf-download-tool</c> is never
/// baked into the runner image -- it is operator-installed onto this persistent
/// volume through the appliance (a future UI install flow, not this slice) and
/// travels in operator-created air-gap bundles. This slice establishes the
/// volume/interface a download job's tool-presence gate checks before it runs;
/// it introduces no install flow of its own.
/// </summary>
public sealed class ManagedToolOptions
{
	public const string SectionName = "ManagedTool";

	/// <summary>
	/// Root directory of the managed-tool state volume. Matches
	/// <c>deploy/compose.yaml</c>'s eventual <c>managed-tool</c> volume mount
	/// (wired by #442); tests point this at a temp directory.
	/// </summary>
	public string ToolStatePath { get; set; } = "/var/lib/waypoint/managed-tool";

	/// <summary>
	/// File name of the installed <c>vcf-download-tool</c> executable, resolved under
	/// <see cref="ExecutableRelativePath"/> inside the active installation directory
	/// (<see cref="ToolStatePath"/>/<see cref="ActiveDirectoryName"/>). The issue #686
	/// install flow (<c>ManagedToolInstallJobHandler</c> /
	/// <c>ManagedToolDistributionInstaller</c>) is responsible for placing it there once
	/// a candidate distribution archive passes verification, safe extraction, layout
	/// validation, and a smoke-test execution; this option also names where the
	/// tool-presence gate looks.
	/// </summary>
	public string ExecutableName { get; set; } = "vcf-download-tool";

	/// <summary>
	/// Path, relative to the active installation directory, at which
	/// <see cref="ExecutableName"/> is expected -- matches the sibling
	/// <c>../vcf-docker-download/Dockerfile</c> layout, which extracts the vendor
	/// archive to a root and exposes <c>&lt;root&gt;/bin</c> on <c>PATH</c>.
	/// </summary>
	public string ExecutableRelativePath { get; set; } = "bin/vcf-download-tool";

	/// <summary>
	/// Directory, relative to the active installation directory, containing the shared
	/// libraries the executable needs at runtime -- matches the sibling Dockerfile's
	/// <c>&lt;root&gt;/lib</c>, exposed there through <c>LD_LIBRARY_PATH</c>. Required to
	/// exist (may be empty) for a distribution to activate.
	/// </summary>
	public string LibraryRelativePath { get; set; } = "lib";

	/// <summary>
	/// Name of the subdirectory under <see cref="ToolStatePath"/> that holds the
	/// currently active extracted distribution. Activation replaces this directory
	/// atomically (directory rename over the prior one, same filesystem) so a download
	/// job never observes a partially extracted installation, and the prior-good
	/// installation is preserved until the new one has passed every check.
	/// </summary>
	public string ActiveDirectoryName { get; set; } = "active";

	/// <summary>
	/// Name of the subdirectory under <see cref="ToolStatePath"/> used as same-volume
	/// scratch space for extracting and smoke-testing a candidate distribution before
	/// atomic activation. Always same-volume as <see cref="ActiveDirectoryName"/> so the
	/// final activation step is a same-filesystem directory rename, not a copy.
	/// </summary>
	public string StagingDirectoryName { get; set; } = "staging";

	/// <summary>
	/// Hard cap on the number of entries a candidate distribution archive may contain --
	/// part of the issue #686 "unbounded expansion" guard alongside
	/// <see cref="MaxExtractedTotalBytes"/>.
	/// </summary>
	public int MaxArchiveEntries { get; set; } = 20_000;

	/// <summary>
	/// Hard cap on the total decompressed size (sum of all entries) a candidate
	/// distribution archive may expand to -- a tar/gzip "bomb" guard, independent of the
	/// compressed upload/depot-fetch size caps that already apply before extraction
	/// starts.
	/// </summary>
	public long MaxExtractedTotalBytes { get; set; } = 2L * 1024 * 1024 * 1024;

	/// <summary>
	/// Wall-clock budget for the bounded noninteractive smoke-test execution of the
	/// extracted candidate executable, run before atomic activation -- an archive whose
	/// "executable" is not really runnable (issue #686's <c>Exec format error</c>
	/// regression) or that hangs waiting on input must fail fast rather than block the
	/// install job indefinitely.
	/// </summary>
	public TimeSpan SmokeTestTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Argument passed to the extracted executable for the bounded smoke-test
	/// invocation. <c>--help</c> is universally supported by well-behaved CLI tools,
	/// requires no credentials or network access, and never prompts interactively.
	/// </summary>
	public string SmokeTestArgument { get; set; } = "--help";

	/// <summary>
	/// Root directory of the operator-provisioned local indexed repository the "install
	/// from local repository" path (issue #39, ADR-0015 decision 3's "operator-provided
	/// local/manual source") reads a candidate tool artifact from. Distinct from
	/// <see cref="Waypoint.Core.Catalog.CatalogOptions.DepotPath"/> (the VCF artifact
	/// depot share): this is wherever the operator has staged the vcf-download-tool
	/// distribution and Broadcom's signed product-version catalog for offline install.
	/// </summary>
	public string LocalRepositoryPath { get; set; } = "/vcf";

	/// <summary>Catalog path, relative to <see cref="LocalRepositoryPath"/>.</summary>
	public string ProductVersionCatalogPath { get; set; } = "PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json";

	/// <summary>Broadcom catalog signature-envelope path, relative to <see cref="LocalRepositoryPath"/>.</summary>
	public string ProductVersionCatalogSignaturePath { get; set; } = "PROD/metadata/productVersionCatalog/v1/productVersionCatalog.sig";

	/// <summary>
	/// Independently provisioned VMware/Broadcom certificate used to trust the
	/// certificate embedded in the catalog signature envelope.
	/// </summary>
	public string CatalogTrustCertificatePath { get; set; } = "/var/lib/waypoint/managed-tool/catalog-trust.cert";

	/// <summary>
	/// Directory manual uploads (issue #39's third install path) are staged into by
	/// <c>POST /downloads/tool/upload</c> before the <c>tool-install</c> job picks them
	/// up. Lives on its OWN dedicated volume (<c>tool-upload-staging</c> in
	/// <c>deploy/compose.yaml</c>), NOT under <see cref="ToolStatePath"/>: the
	/// backend mounts only this staging volume read-write and never the
	/// <c>managed-tool</c> tool store, so the API-facing process cannot write the
	/// verified tool binary or the <see cref="ReleasePublicKeyPath"/> trust anchor
	/// (ADR-0014 §7, issue #442 AC5, #570 -- re-scoped per #630 review). The
	/// download-runner mounts the SAME staging volume read-write so it can read the
	/// staged artifact/signature when it claims the install job (and write the
	/// depot-fetch subdir). Keeping staging off the tool store also means a
	/// staged-but-not-yet-verified upload can never be mistaken for an installed tool.
	/// </summary>
	public string UploadStagingPath { get; set; } = "/var/lib/waypoint/tool-upload-staging";

	/// <summary>
	/// PEM-encoded RSA public key file used to verify every candidate artifact's
	/// detached signature against the Broadcom release key (issue #39's "signature-
	/// verified against the Broadcom release key before activation" requirement) before
	/// it is copied into <see cref="ToolStatePath"/>. The project never ships this key
	/// file (ADR-0015: the project never ships the gated tool or anything that would let
	/// it be mistaken for project-endorsed); an operator provisions it out of band.
	/// </summary>
	public string ReleasePublicKeyPath { get; set; } = "/var/lib/waypoint/managed-tool/release-public-key.pem";

	/// <summary>
	/// Depot-fetch install path (issue #39/#671, ADR-0015 decision 3's "fetch from its
	/// authorized upstream repository using operator-supplied credentials"),
	/// connected-mode only. An operator-provisioned URL template for the
	/// <c>vcf-download-tool</c> artifact; <c>{version}</c> is substituted with the
	/// payload's requested version when present (an empty/omitted placeholder means
	/// the URL always resolves to the depot's "latest" endpoint). Null/blank
	/// (the default -- the project ships no depot URL, ADR-0015) fails the depot-fetch
	/// path cleanly rather than attempting a request with nothing configured.
	/// </summary>
	public string? DepotFetchUrlTemplate { get; set; }

	/// <summary>
	/// Issue #671: operator-provisioned URL for Broadcom's signed product-version
	/// catalog (the same document <see cref="ProductVersionCatalogPath"/> names for
	/// the local-repository install path), fetched over the connected depot-fetch
	/// path with the same bearer Activation Code as <see cref="DepotFetchUrlTemplate"/>.
	/// The real vendor no longer publishes a per-artifact <c>.sig</c> -- verification
	/// authenticates this catalog instead (issue #669's
	/// <see cref="IManagedToolCatalogVerifier"/>). Null/blank (the default) fails the
	/// depot-fetch path cleanly before any network attempt.
	/// </summary>
	public string? DepotCatalogUrl { get; set; }

	/// <summary>
	/// Issue #671: operator-provisioned URL for the detached signature envelope over
	/// <see cref="DepotCatalogUrl"/>'s exact bytes (the connected equivalent of
	/// <see cref="ProductVersionCatalogSignaturePath"/>). Fetched with the same bearer
	/// Activation Code. Null/blank (the default) fails the depot-fetch path cleanly
	/// before any network attempt.
	/// </summary>
	public string? DepotCatalogSignatureUrl { get; set; }

	/// <summary>
	/// Wall-clock budget for the depot-fetch HTTP path (artifact + catalog + catalog
	/// signature GETs, combined) -- an unreachable or hanging depot must fail the job
	/// rather than block the download-runner's job slot indefinitely.
	/// </summary>
	public TimeSpan DepotFetchTimeout { get; set; } = TimeSpan.FromMinutes(10);

	/// <summary>
	/// Hard cap on each depot-fetched object's size (artifact, catalog, or catalog
	/// signature), matching <c>ManagedToolController</c>'s manual-upload cap
	/// (<c>MaxUploadBytes</c> = 512 MiB) for the artifact leg -- the same ceiling
	/// applies regardless of which path delivered the candidate. The catalog and its
	/// signature are ordinarily far smaller than this but share the one cap rather
	/// than adding two more knobs for objects that are already bounded well under it.
	/// </summary>
	public long DepotFetchMaxBytes { get; set; } = 512L * 1024 * 1024;

	/// <summary>
	/// Directory, under <see cref="ToolStatePath"/> (the same persistent volume, so it
	/// survives container rebuilds -- issue #691 AC), that the assisted enrollment job
	/// uses as the invoked tool's <c>HOME</c>/<c>XDG_DATA_HOME</c>. This is where
	/// <c>vcf-download-tool</c> persists its own identity state (the sibling reference
	/// writes <c>~/.local/share/vmware/vdt/machine_id</c>) -- isolating it here means
	/// the Depot ID stays stable across restarts/rebuilds while never touching a
	/// container-global root home the way the sibling Dockerfile's build-time seeding
	/// does.
	/// </summary>
	public string IdentityStatePath { get; set; } = "identity";

	/// <summary>Wall-clock budget for a bounded noninteractive <c>vcf-download-tool</c> Depot ID query/generation call -- neither may prompt interactively or hang the job indefinitely. Activation-code validation uses <see cref="ActivationCodeValidationTimeout"/> instead, since it is a real WAN metadata fetch.</summary>
	public TimeSpan EnrollmentCommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Wall-clock budget for the bounded validation-by-use <c>metadata download</c>
	/// invocation (issue #791). The real 9.1.0.0400 tool has no lightweight "check code"
	/// subcommand, so a genuine code is validated by running <c>metadata download</c>
	/// against a throwaway scratch depot-store; that reaches out to Broadcom over the WAN
	/// and is meaningfully slower than the local <see cref="EnrollmentCommandTimeout"/>
	/// identity calls, so it gets a pull-class budget rather than the short enrollment one.
	/// A timeout is classified as a network problem, never an Activation Code rejection.
	/// </summary>
	public TimeSpan ActivationCodeValidationTimeout { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Name of the throwaway scratch depot-store subdirectory, under
	/// <see cref="ToolStatePath"/> (same persistent volume, so the tool's own atomic
	/// rename/temp behaviour stays same-filesystem), that the validation-by-use
	/// <c>metadata download</c> writes into (issue #791). A fresh per-validation
	/// subdirectory is created under this and removed on every path in <c>finally</c> --
	/// nothing the validation fetches is ever promoted into the operator-facing depot; it
	/// exists only so <c>metadata download</c> has a <c>--depot-store</c> to point at while
	/// the tool authenticates the code.
	/// </summary>
	public string ActivationCodeValidationScratchDirectoryName { get; set; } = "validate-scratch";

	/// <summary>
	/// Wall-clock budget for the connected <c>catalog-pull</c> job's <c>metadata
	/// download</c> invocation (issue #687) -- a hung or unreachable Broadcom depot
	/// must fail the job rather than block the download-runner's job slot
	/// indefinitely. Longer than <see cref="EnrollmentCommandTimeout"/> because a real
	/// vendor metadata catalog is a meaningfully larger download than an enrollment
	/// identity check.
	/// </summary>
	public TimeSpan CatalogPullTimeout { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Scratch directory, under <see cref="ToolStatePath"/> (same persistent volume),
	/// that a <c>catalog-pull</c> job's <c>metadata download</c> writes into before
	/// the downloaded metadata is authenticated and atomically promoted into
	/// <see cref="Waypoint.Core.Catalog.CatalogOptions.DepotPath"/> (issue #687). Kept
	/// off the operator-facing depot share so a failed/partial pull never corrupts the
	/// prior-good on-disk catalog the local re-index and download queue both read.
	/// </summary>
	public string CatalogPullStagingDirectoryName { get; set; } = "catalog-pull-staging";
}
