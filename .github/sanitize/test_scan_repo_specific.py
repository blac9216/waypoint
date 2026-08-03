#!/usr/bin/env python3
"""Tests for scan_repo_specific.py — the sanitization gate's own logic.

Issue #90. Running the scanner against a clean tree proves the *absence* of
findings and never the *presence* of detection; that asymmetry is what let the
frontend air-gap guard fail open three times (#77, PR #65 round 1, #81). These
tests assert the other half: that each detector actually fires on the pattern
class it exists to catch, and that it stays quiet on the conventions CLAUDE.md
mandates.

Stdlib `unittest` on purpose — no new toolchain, no dependency to install, so
the gate keeps working in a minimal runner.

**Fixtures are assembled at runtime, never written as literals.** This file is
itself a tracked file, so it is scanned by scan_repo_specific.py and by
gitleaks like any other. A literal lab FQDN or dotted-quad here would either
fail the very gate it is testing or force an allowlist entry for this path —
and the whole point of this round is that the tree carries no exemptions at
all. Assembling each fixture from parts keeps that property true.
"""

from __future__ import annotations

import unittest
from pathlib import Path

import scan_repo_specific as scanner


# --- fixture builders (see module docstring) -----------------------------

def quad(a: int, b: int, c: int, d: int) -> str:
	"""Assemble a dotted-quad without writing one into this file."""
	return f"{a}.{b}.{c}.{d}"


def fqdn(*labels: str) -> str:
	"""Assemble a dotted hostname without writing one into this file."""
	return ".".join(labels)


def opaque_token(length: int = 40) -> str:
	"""Deterministic high-entropy-shaped hex string.

	Arithmetic, not random, so failures are reproducible; assembled rather
	than literal so no token-shaped string is ever committed. Contains no
	PLACEHOLDER_MARKERS substring (hex has no 'x', 'k', 't' or 'o'), so the
	detector must treat it as a real token shape.
	"""
	return "".join(f"{(i * 7 + 3) % 16:x}" for i in range(length))


# A lab-style FQDN of the shape CLAUDE.md forbids, and the sanctioned form.
LAB_FQDN = fqdn("vcenter-prod", "fictionallab", "corp", "local")
OK_FQDN = fqdn("esxi-01", "example", "internal")

# A routable-looking address of the shape CLAUDE.md forbids.
LAB_IP = quad(10, 44, 12, 7)
LAB_IP_2 = quad(172, 22, 9, 31)


def lab_host(tld: str) -> str:
	"""A forbidden-shape hostname under an arbitrary lab TLD."""
	return fqdn("vcenter-prod", "fictionallab", tld)


# A second dash-separated endpoint sharing LAB_IP's /24, for issue #111's
# range-detection tests. Both are fictional (RFC 5737 would be the sanctioned
# choice for prose, but the *shape* being pinned here is "two lab addresses
# joined by a bare dash", which needs two non-exempt quads either side).
RANGE_START = quad(10, 44, 12, 1)


def ipv6(*groups: str) -> str:
	"""Assemble a colon-separated IPv6 literal from hextet parts.

	Joining with ":" means an empty group renders as the "::" compression
	marker for free — an empty group between two non-empty ones collapses
	to a double colon, and two empty groups in a row (three empty parts
	total) render as a bare double colon. Matches the module docstring's
	discipline for IPv4/FQDN fixtures: no contiguous address-shaped literal
	ever appears as source text in this file, so this file scans as clean
	as everything else it is testing against.
	"""
	return ":".join(groups)


# A unique-local IPv6 literal (RFC 4193; the common fd-prefixed case) of the
# shape CLAUDE.md forbids, in compressed and fully-expanded form, plus its
# bracketed-with-port URL form and an RFC 4291 link-local sibling — the
# three shapes issue #112 names explicitly.
LAB_IPV6 = ipv6("fd00", "1a2b", "3c4d", "", "7")
LAB_IPV6_FULL = ipv6("fd00", "1a2b", "3c4d", "0000", "0000", "0000", "0000", "0007")
LAB_IPV6_LINK_LOCAL = ipv6("fe80", "", "1")


def bracketed_port(addr: str, port: int) -> str:
	"""The `[addr]:port` URL form for an IPv6 literal."""
	return f"[{addr}]:{port}"


# The three IPv6 forms CLAUDE.md/issue #112 sanction: the RFC 3849
# documentation prefix, loopback, and the unspecified address.
OK_IPV6_DOC = ipv6("2001", "db8", "", "1")
OK_IPV6_LOOPBACK = ipv6("", "", "1")
OK_IPV6_UNSPECIFIED = ipv6("", "", "")  # three empty parts -> "::" (two colons)


class DetectorPositiveTests(unittest.TestCase):
	"""Each detector fires on the pattern class it exists to catch."""

	def test_lab_fqdn_is_flagged(self) -> None:
		findings = scanner.scan_text("f.md", f"Lab vCenter at {LAB_FQDN} today.")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn("lab-style FQDN", findings[0])
		self.assertIn(LAB_FQDN, findings[0])

	def test_non_rfc5737_ip_is_flagged(self) -> None:
		findings = scanner.scan_text("f.md", f"Host reachable at {LAB_IP} over TLS.")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn("non-RFC-5737 IP address literal", findings[0])
		self.assertIn(LAB_IP, findings[0])

	def test_depot_token_is_flagged(self) -> None:
		token = opaque_token()
		findings = scanner.scan_text("f.md", f"depot_token: {token}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn("possible depot/entitlement token", findings[0])

	def test_depot_token_value_is_redacted_not_echoed(self) -> None:
		"""A finding must not reprint the secret into a public CI log."""
		token = opaque_token()
		findings = scanner.scan_text("f.md", f"activation-code = {token}")
		self.assertEqual(len(findings), 1, findings)
		self.assertNotIn(token, findings[0])
		self.assertIn("<redacted, 40 chars>", findings[0])

	def test_every_depot_keyword_variant_fires(self) -> None:
		token = opaque_token()
		for keyword in (
			"depot_token", "depot-token", "depot token",
			"activation_code", "entitlement_id",
			"support_contract", "broadcom_token",
		):
			with self.subTest(keyword=keyword):
				findings = scanner.scan_text("f.md", f"{keyword}: {token}")
				self.assertEqual(len(findings), 1, findings)

	def test_findings_carry_path_and_line_number(self) -> None:
		text = "\n".join(["clean line", "clean line", f"leak {LAB_IP}"])
		findings = scanner.scan_text("docs/thing.md", text)
		self.assertEqual(len(findings), 1, findings)
		self.assertTrue(findings[0].startswith("docs/thing.md:3:"), findings[0])

	def test_multiple_finding_classes_on_one_line(self) -> None:
		text = f"{LAB_FQDN} at {LAB_IP} depot_token: {opaque_token()}"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 3, findings)


class SurroundingContextTests(unittest.TestCase):
	"""An address must be detected wherever it sits on the line.

	Regression cover for the round-2 blocker on PR #83, and for the fixture
	monoculture that hid it. Every fixture in the original suite placed the
	address mid-line with a word before it and a word after it, so the suite
	proved only that the detectors fire on *that* shape. Both regexes ended in
	`(?![\\w.-])` with a literal `.` in the class, which meant an address
	ENDING A SENTENCE was never matched — a lab FQDN and a lab IP went through
	the hard gate on the real tree, exit 0, in a single appended line.

	The lesson generalises past the one bug: a detector's delimiter handling is
	part of the detector, so the delimiters get enumerated rather than assumed.
	"""

	# Characters that legitimately end a token in prose, markup, config and
	# URLs. `_` and `-` are deliberately absent: they continue a token, and
	# treating them as terminators is what would let a real bypass in.
	TRAILING = [
		("end of line", ""),
		("sentence period", "."),
		("comma", ","),
		("semicolon", ";"),
		("colon / port", ":443"),
		("question mark", "?"),
		("close paren", ")"),
		("close bracket", "]"),
		("double quote", '"'),
		("single quote", "'"),
		("angle bracket", ">"),
		("url path", "/ui"),
		("space then word", " today"),
		("tab", "\t"),
	]

	LEADING = [
		("start of line", ""),
		("space", "at "),
		("open paren", "("),
		("open bracket", "["),
		("double quote", '"'),
		("single quote", "'"),
		("angle bracket", "<"),
		("equals", "="),
		("url scheme", "https://"),
		("at sign", "@"),
	]

	def test_ip_is_flagged_after_every_trailing_delimiter(self) -> None:
		for name, suffix in self.TRAILING:
			with self.subTest(delimiter=name):
				findings = scanner.scan_text("f.md", f"host {LAB_IP}{suffix}")
				self.assertEqual(len(findings), 1, (name, findings))
				self.assertIn(LAB_IP, findings[0])

	def test_fqdn_is_flagged_after_every_trailing_delimiter(self) -> None:
		for name, suffix in self.TRAILING:
			with self.subTest(delimiter=name):
				findings = scanner.scan_text("f.md", f"host {LAB_FQDN}{suffix}")
				self.assertEqual(len(findings), 1, (name, findings))
				self.assertIn(LAB_FQDN, findings[0])

	def test_ip_is_flagged_after_every_leading_delimiter(self) -> None:
		for name, prefix in self.LEADING:
			with self.subTest(delimiter=name):
				findings = scanner.scan_text("f.md", f"{prefix}{LAB_IP}")
				self.assertEqual(len(findings), 1, (name, findings))

	def test_fqdn_is_flagged_after_every_leading_delimiter(self) -> None:
		for name, prefix in self.LEADING:
			with self.subTest(delimiter=name):
				findings = scanner.scan_text("f.md", f"{prefix}{LAB_FQDN}")
				self.assertEqual(len(findings), 1, (name, findings))

	def test_address_alone_on_its_line_is_flagged(self) -> None:
		"""No surrounding context at all — the extreme of the above."""
		self.assertEqual(len(scanner.scan_text("f.md", LAB_IP)), 1)
		self.assertEqual(len(scanner.scan_text("f.md", LAB_FQDN)), 1)

	def test_the_exact_line_that_defeated_the_gate(self) -> None:
		"""Verbatim regression for the round-2 escape.

		An uppercase lab FQDN and a sentence-final lab IP in one line, which
		the pre-fix detectors reported as `clean` with exit 0.
		"""
		text = f"Lab vCenter {LAB_FQDN.upper()} answers at {LAB_IP}."
		findings = scanner.scan_text("docs/testing.md", text)
		self.assertEqual(len(findings), 2, findings)
		self.assertTrue(any("IP address literal" in f for f in findings), findings)
		self.assertTrue(any("lab-style FQDN" in f for f in findings), findings)

	def test_two_addresses_on_one_line_each_report(self) -> None:
		"""Every original fixture carried at most one address of each kind."""
		findings = scanner.scan_text("f.md", f"pair {LAB_IP} and {LAB_IP_2}.")
		self.assertEqual(len(findings), 2, findings)

	def test_trailing_period_does_not_resurrect_version_false_positives(self) -> None:
		"""The fix must not over-correct into the #89 false positives.

		A dotted run longer than four parts is still not an address, even when
		it ends a sentence, and neither is a build-suffixed version.
		"""
		for text in (
			f"release {quad(1, 2, 3, 4)}.5.",
			f"build {quad(4, 2, 1, 0)}.0.24304122.",
			f"vcf-download-tool-{quad(9, 0, 0, 0)}-24089201.tar.gz.",
			f"version: '{quad(8, 18, 0, 4)}'.",
		):
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)


class CaseSensitivityTests(unittest.TestCase):
	"""Lab TLDs are matched in any case (round-2 blocker, part b).

	`FQDN_RE`'s TLD alternation had no case-insensitive flag while
	`is_allowed_fqdn()` lowercases its input — so case-insensitivity was
	plainly intended and never reached. Every fixture in the original suite
	was lowercase, so nothing failed. Uppercase FQDNs are the normal shape in
	AD/Windows material, exported inventories, CKL/HDF results and certificate
	CNs, which are exactly the artifacts CLAUDE.md forbids committing.
	"""

	def test_uppercase_lab_fqdn_is_flagged(self) -> None:
		host = LAB_FQDN.upper()
		findings = scanner.scan_text("f.md", f"host {host} today")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(host, findings[0])

	def test_mixed_case_lab_fqdn_is_flagged(self) -> None:
		base = fqdn("vcenter-prod", "fictionallab", "corp")
		for host in (
			f"{base}.{'local'.upper()}",          # lowercase host, uppercase TLD
			f"{base.upper()}.{'local'}",          # uppercase host, lowercase TLD
			f"{base.title()}.{'local'.title()}",  # title case throughout
		):
			with self.subTest(host=host):
				findings = scanner.scan_text("f.md", f"host {host} today")
				self.assertEqual(len(findings), 1, (host, findings))

	def test_every_suspicious_tld_fires_in_both_cases(self) -> None:
		"""Pins the regex alternation to SUSPICIOUS_TLDS.

		The two are separate declarations, so they can drift. The original
		suite exercised only four of the eight TLDs, and only through the
		*allowed* `*.example.<tld>` form — dropping `lan`, `home`, `arpa` and
		`intra` from the alternation passed all 34 assertions silently.
		"""
		self.assertTrue(scanner.SUSPICIOUS_TLDS)
		for tld in sorted(scanner.SUSPICIOUS_TLDS):
			for spelling in (tld, tld.upper(), tld.capitalize()):
				with self.subTest(tld=spelling):
					host = lab_host(spelling)
					findings = scanner.scan_text("f.md", f"host {host}.")
					self.assertEqual(len(findings), 1, (host, findings))
					self.assertIn("lab-style FQDN", findings[0])

	def test_case_insensitivity_does_not_widen_past_the_sanctioned_form(self) -> None:
		"""`*.example.<tld>` stays allowed in any case, not just lowercase."""
		for tld in sorted(scanner.SUSPICIOUS_TLDS):
			for host in (
				fqdn("esxi-01", "example", tld),
				fqdn("esxi-01", "example", tld).upper(),
				fqdn("ESXi-01", "Example", tld.capitalize()),
			):
				with self.subTest(host=host):
					self.assertEqual(scanner.scan_text("f.md", f"see {host}."), [])

	def test_two_label_lab_fqdn_is_flagged(self) -> None:
		"""Every original FQDN fixture had three or more labels."""
		host = fqdn("fictionallab", "local")
		self.assertEqual(len(scanner.scan_text("f.md", f"host {host}.")), 1)

	def test_depot_keyword_is_case_insensitive(self) -> None:
		"""Every original depot fixture spelled the keyword in lowercase."""
		token = opaque_token()
		for keyword in ("DEPOT_TOKEN", "Depot-Token", "ACTIVATION CODE", "Broadcom_Token"):
			with self.subTest(keyword=keyword):
				findings = scanner.scan_text("f.md", f"{keyword}: {token}")
				self.assertEqual(len(findings), 1, (keyword, findings))


class LegitimateConventionTests(unittest.TestCase):
	"""The repo's own sanctioned conventions must never fire.

	A false positive on a hard gate is the failure mode most likely to get
	the gate muted, so each of these is a documented, tested exemption.
	"""

	def test_rfc5737_documentation_ranges_are_allowed(self) -> None:
		for addr in (
			quad(192, 0, 2, 1), quad(192, 0, 2, 254),
			quad(198, 51, 100, 1), quad(198, 51, 100, 254),
			quad(203, 0, 113, 1), quad(203, 0, 113, 254),
		):
			with self.subTest(addr=addr):
				self.assertEqual(scanner.scan_text("f.md", f"host {addr}"), [])

	def test_loopback_and_docker_embedded_dns_are_allowed(self) -> None:
		for addr in (quad(127, 0, 0, 1), quad(127, 0, 0, 11), quad(127, 1, 2, 3)):
			with self.subTest(addr=addr):
				self.assertEqual(scanner.scan_text("f.md", f"resolver {addr}"), [])

	def test_wildcard_bind_address_is_allowed(self) -> None:
		self.assertEqual(scanner.scan_text("f.md", f"bind {quad(0, 0, 0, 0)}"), [])

	def test_example_dot_lab_tld_fqdns_are_allowed(self) -> None:
		for host in (
			fqdn("esxi-01", "example", "internal"),
			fqdn("vcsa-01", "example", "local"),
			fqdn("nsx-mgr-01", "example", "lab"),
			fqdn("user", "example", "corp"),
		):
			with self.subTest(host=host):
				self.assertEqual(scanner.scan_text("f.md", f"see {host}"), [])

	def test_email_at_sanctioned_domain_is_allowed(self) -> None:
		self.assertEqual(
			scanner.scan_text("f.md", f"contact a.okafor@{fqdn('example', 'internal')}"),
			[],
		)

	def test_public_domains_are_not_flagged(self) -> None:
		for host in ("github.com", "registry.npmjs.org", "example.com"):
			with self.subTest(host=host):
				self.assertEqual(scanner.scan_text("f.md", f"fetch {host}"), [])

	def test_placeholder_tokens_are_allowed(self) -> None:
		for marker in scanner.PLACEHOLDER_MARKERS:
			with self.subTest(marker=marker):
				value = f"{marker}aaaaaaaaaaaaaaaaaaaa"
				self.assertEqual(
					scanner.scan_text("f.md", f"depot_token: {value}"), []
				)

	def test_short_token_is_not_flagged(self) -> None:
		"""Under the 16-char floor is not a token shape."""
		self.assertEqual(scanner.scan_text("f.md", "depot_token: abc123"), [])


class VersionStringTests(unittest.TestCase):
	"""Four-part product versions are not IP addresses (issue #89)."""

	def test_bare_four_part_version_behind_version_key_is_allowed(self) -> None:
		version = quad(8, 18, 0, 4)
		for line in (
			f"version:'{version}'",
			f'version: "{version}"',
			f"version = {version}",
			f"Version: {version}",
			f"versions: {version}",
		):
			with self.subTest(line=line):
				self.assertEqual(scanner.scan_text("f.md", line), [], line)

	def test_build_suffixed_version_is_allowed(self) -> None:
		"""The originally-documented case: quad inside a longer dash run."""
		text = f"vcf-download-tool-{quad(9, 0, 0, 0)}-24089201.tar.gz"
		self.assertEqual(scanner.scan_text("f.md", text), [])

	def test_version_word_elsewhere_on_line_does_not_waive_a_real_ip(self) -> None:
		"""The suppression is immediate-context-bound, not line-wide.

		This is the bypass that a naive /\\bversion\\b/i line test would open.
		"""
		text = f"The vCenter version at the site is fine; host {LAB_IP} is not."
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IP, findings[0])

	def test_version_key_does_not_waive_a_later_ip_on_the_same_line(self) -> None:
		text = f"version: '{quad(8, 18, 0, 4)}' deployed to {LAB_IP}"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IP, findings[0])

	def test_invalid_quad_is_not_an_ip_literal(self) -> None:
		"""An octet above 255 cannot be the lab address this check catches."""
		self.assertEqual(scanner.scan_text("f.md", f"rel {quad(2024, 1, 300, 5)}"), [])

	def test_separator_is_optional_as_documented(self) -> None:
		"""The suppression is wider than a `version: 'A.B.C.D'` key/value shape.

		`[:=]?` is optional, so the separator-less and prefixed spellings that
		release notes and CLI help text actually use are suppressed too. This
		is documented behaviour, not an accident — pinned here so the code
		comment and the implementation cannot drift apart.
		"""
		version = quad(8, 18, 0, 4)
		for line in (
			f"version {version}",
			f"--version {version}",
			f"x-version {version}",
			f"app.version={version}",
		):
			with self.subTest(line=line):
				self.assertEqual(scanner.scan_text("f.md", line), [], line)

	def test_version_word_must_be_whole_and_immediately_before(self) -> None:
		"""The documented bounds of the suppression, stated as failures.

		These are the spellings that deliberately do NOT waive, and they are
		what keeps the optional separator above from being a bypass.
		"""
		for line in (
			f"mgmt_version: {LAB_IP}",       # underscore defeats the \\b
			f"version `{LAB_IP}`",           # backtick is not in ['\"]?
			f"revision {LAB_IP}",            # not the word "version"
			f"version of the host; see {LAB_IP}",  # not immediately before
		):
			with self.subTest(line=line):
				findings = scanner.scan_text("f.md", line)
				self.assertEqual(len(findings), 1, (line, findings))
				self.assertIn(LAB_IP, findings[0])

	def test_version_suppression_does_not_carry_across_lines(self) -> None:
		"""The check is per-line; a version key above does not waive below."""
		text = f"version:\n{LAB_IP}"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)
		self.assertTrue(findings[0].startswith("f.md:2:"), findings[0])


class RangeDetectionTests(unittest.TestCase):
	"""Issue #111: a dash between two addresses is a range, not a build suffix.

	`test_the_exact_range_from_the_issue` is the literal repro from the issue
	body; the rest characterise the boundary of the fix — what starts working
	and, just as importantly, what deliberately keeps not working because it
	is not the shape the fix targets.
	"""

	def test_the_exact_range_from_the_issue(self) -> None:
		"""Verbatim regression for the issue's own failing input.

		Before the fix this was 0 findings (both endpoints invisible); the
		issue requires >= 2 after. This asserts the precise count and that
		both endpoints are individually named.
		"""
		findings = scanner.scan_text("f.md", f"range {RANGE_START}-{LAB_IP}")
		self.assertEqual(len(findings), 2, findings)
		self.assertTrue(any(RANGE_START in f for f in findings), findings)
		self.assertTrue(any(LAB_IP in f for f in findings), findings)

	def test_range_endpoints_are_still_individually_allow_checked(self) -> None:
		"""A range with one RFC 5737 endpoint only flags the non-exempt one."""
		doc_addr = quad(192, 0, 2, 1)
		findings = scanner.scan_text("f.md", f"{doc_addr}-{LAB_IP}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IP, findings[0])

	def test_three_way_chain_catches_the_touching_pair(self) -> None:
		"""Not a full chain-walk, but the fix must not regress on one.

		Each end of a three-address chain touches exactly one real quad
		across its dash, so both are still caught; only the truly interior
		endpoint (quad-shaped on one side, chain-interior on the other) is
		not asserted here either way.
		"""
		third = quad(10, 44, 12, 9)
		findings = scanner.scan_text(
			"f.md", f"{RANGE_START}-{LAB_IP}-{third}"
		)
		self.assertGreaterEqual(len(findings), 2, findings)
		self.assertTrue(any(RANGE_START in f for f in findings), findings)

	def test_build_suffixed_version_still_suppressed_by_the_range_fix(self) -> None:
		"""The load-bearing negative: the fix must not reopen issue #89.

		Same fixture as VersionStringTests.test_build_suffixed_version_is_
		allowed, re-asserted here under the name of the mechanism that now
		has to keep it suppressed (_dash_glues_to_non_address), not the one
		that used to (the blanket regex guard).
		"""
		text = f"vcf-download-tool-{quad(9, 0, 0, 0)}-24089201.tar.gz"
		self.assertEqual(scanner.scan_text("f.md", text), [], text)

	def test_single_sided_dash_adjacency_is_still_suppressed(self) -> None:
		"""The fix is deliberately narrow: only BOTH sides quad-shaped opens
		the guard. A dash with an address on only one side stays exactly as
		invisible as before — these are residual, disclosed gaps (see the PR
		body), not a regression, and not the shape #111's fix claims to
		close.
		"""
		for text in (
			f"{LAB_IP}-primary",
			f"vcenter -{LAB_IP}",
			f"trailing-{LAB_IP}",
		):
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)

	def test_cidr_form_is_unaffected(self) -> None:
		"""CIDR was already caught before #111; the fix must not touch it."""
		findings = scanner.scan_text("f.md", f"{LAB_IP}/24")
		self.assertEqual(len(findings), 1, findings)


class UnderscoreAdjacencyTests(unittest.TestCase):
	"""Issue #111: `_` is a separator, not a token-continuation character.

	`\\w` (used by the pre-fix guards) is `[A-Za-z0-9_]`; a letter or digit
	glued to a quad/hostname is still the build-suffix shape the guards exist
	to reject, but `_` is how identifiers *separate* words, and treating it
	as a continuation hid the address outright.
	"""

	def test_underscore_prefixed_ip_is_flagged(self) -> None:
		findings = scanner.scan_text("f.md", f"host_{LAB_IP}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IP, findings[0])

	def test_underscore_prefixed_fqdn_is_flagged(self) -> None:
		text = f"vcenter_{LAB_FQDN}"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_FQDN, findings[0])

	def test_letter_adjacency_still_suppressed_for_ip(self) -> None:
		"""The load-bearing negative: only `_` was narrowed, not `\\w` at large.

		A letter directly glued to a quad is exactly the build-suffix shape
		the guard exists to reject, and must stay suppressed. (There is no
		FQDN analogue of this case: letters are themselves valid DNS label
		characters, so a letter glued to a hostname is simply absorbed into
		the label rather than rejected — it was never the guard doing
		anything there, before or after this fix. A glued *digit* on the IP
		side is a different case again, covered separately below: it does
		not test the guard at all, because a digit is a character a real
		octet is made of.)
		"""
		for text in (f"a{LAB_IP}", f"{LAB_IP}a"):
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)

	def test_glued_digit_changes_the_address_rather_than_defeating_the_guard(
		self,
	) -> None:
		"""A digit glued to a quad is not a guard question at all.

		Unlike a letter or `-`, a digit is a character a real octet is made
		of, so gluing one on either forms a different, equally real-looking
		address (still correctly flagged) or overflows an octet past 255
		(correctly allowed via the octet-range check, not the boundary
		guard). Neither outcome exercises `_`-vs-`\\w`; this test exists so
		that distinction is explicit rather than assumed.
		"""
		still_an_address = f"{LAB_IP}9"  # last octet 7 -> 79, still <= 255
		findings = scanner.scan_text("f.md", still_an_address)
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(still_an_address, findings[0])

		overflows_the_octet = f"9{LAB_IP}"  # first octet 10 -> 910, > 255
		self.assertEqual(scanner.scan_text("f.md", overflows_the_octet), [])

	def test_trailing_underscore_still_suppressed(self) -> None:
		"""The other load-bearing negative: only the LEADING guard narrowed.

		A trailing `_` must keep blocking, for both detectors — this is what
		EditorconfigRegressionTests pins against the real false positive that
		a symmetric (leading-and-trailing) fix produced in this repo's own
		`.editorconfig`.
		"""
		for text in (f"{LAB_IP}_build", f"{LAB_FQDN}_build"):
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)


class EditorconfigRegressionTests(unittest.TestCase):
	"""Pins the real false positive a leading-AND-trailing underscore fix
	produced against this repo's own tracked `.editorconfig`.

	A real, unmodified `.editorconfig` naming-rule key was scanning clean
	before #111: a dotted key segment ending in a suspicious TLD word,
	immediately followed by an underscore-joined continuation of the same
	identifier. Narrowing the trailing guard too — not just the leading one
	the issue actually asks for — made that TLD-ending segment match as a
	complete FQDN, because the TLD word immediately followed by "_" was no
	longer read as a continuation of one identifier. `test_dotnet_naming_rule_
	ending_in_local_is_not_flagged` below assembles the exact key from parts
	rather than quoting it whole, for the same reason this docstring
	describes the shape instead of naming it: quoting it here would trip the
	very check being pinned. Assembled from the naming convention rather than
	copy-pasted, so this test keeps meaning what it says if the real file's
	specific rule names ever change.
	"""

	def test_dotnet_naming_rule_ending_in_local_is_not_flagged(self) -> None:
		line = (
			"dotnet_naming_rule."
			+ "local_functions_should_be_pascalcase"
			+ ".severity = suggestion"
		)
		self.assertEqual(scanner.scan_text(".editorconfig", line), [], line)

	def test_every_suspicious_tld_as_a_snake_case_prefix_is_not_flagged(self) -> None:
		"""Generalises the pinned case across every TLD this scanner knows,
		not just "local" — the mechanism is the trailing guard, not a
		"local"-specific carve-out, and this is what proves that."""
		for tld in sorted(scanner.SUSPICIOUS_TLDS):
			with self.subTest(tld=tld):
				line = f"dotnet_naming_rule.{tld}_functions_should_be_pascalcase"
				self.assertEqual(scanner.scan_text("f.md", line), [], line)


def zero_pad_quad(dotted_quad: str) -> str:
	"""Zero-pad each octet of a dotted-quad to 3 digits.

	Assembled at runtime (never written as a padded literal) for the same
	reason every other address fixture in this file is: the padded form is
	exactly as address-shaped as the unpadded one, so writing it directly
	into the source would trip the very check being tested here.
	"""
	return ".".join(part.zfill(3) for part in dotted_quad.split("."))


class ZeroPaddedQuadTests(unittest.TestCase):
	"""Issue #111: a zero-padded octet is still a real address.

	Python's `ipaddress` module rejects leading zeros outright (ambiguous
	octal notation), so the pre-fix `is_allowed_ip` took that as "not an IP
	at all" and let a zero-padded lab quad through.
	"""

	def test_zero_padded_lab_quad_is_flagged(self) -> None:
		padded = zero_pad_quad(LAB_IP)
		findings = scanner.scan_text("f.md", f"host {padded}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(padded, findings[0])

	def test_zero_padded_rfc5737_address_is_still_allowed(self) -> None:
		padded = zero_pad_quad(quad(192, 0, 2, 1))
		self.assertEqual(scanner.scan_text("f.md", f"host {padded}"), [])

	def test_zero_padded_wildcard_is_still_allowed(self) -> None:
		padded = zero_pad_quad(quad(0, 0, 0, 0))
		self.assertEqual(scanner.scan_text("f.md", f"bind {padded}"), [])

	def test_octet_over_255_is_still_not_a_finding(self) -> None:
		"""An invalid octet is not an address regardless of padding."""
		self.assertEqual(scanner.scan_text("f.md", f"rel {quad(2024, 1, 300, 5)}"), [])


class IPv6DetectorTests(unittest.TestCase):
	"""Issue #112: no detector previously covered this address family at all."""

	def test_compressed_ula_is_flagged(self) -> None:
		findings = scanner.scan_text("f.md", f"reachable at {LAB_IPV6} today")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn("possible IPv6 address literal", findings[0])
		self.assertIn(LAB_IPV6, findings[0])

	def test_full_uncompressed_ula_is_flagged(self) -> None:
		findings = scanner.scan_text("f.md", f"host {LAB_IPV6_FULL}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IPV6_FULL, findings[0])

	def test_link_local_is_flagged(self) -> None:
		findings = scanner.scan_text("f.md", f"neighbor {LAB_IPV6_LINK_LOCAL}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IPV6_LINK_LOCAL, findings[0])

	def test_bracketed_with_port_is_flagged(self) -> None:
		url = f"https://{bracketed_port(LAB_IPV6, 8443)}/ui"
		findings = scanner.scan_text("f.md", url)
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IPV6, findings[0])
		self.assertIn("8443", findings[0])

	def test_zone_id_is_stripped_before_validation(self) -> None:
		text = f"link-local {LAB_IPV6_LINK_LOCAL}%eth0 seen"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)

	def test_documentation_prefix_is_allowed(self) -> None:
		self.assertEqual(scanner.scan_text("f.md", f"example {OK_IPV6_DOC}"), [])

	def test_loopback_is_allowed(self) -> None:
		self.assertEqual(scanner.scan_text("f.md", f"resolver {OK_IPV6_LOOPBACK}"), [])

	def test_unspecified_is_allowed(self) -> None:
		self.assertEqual(scanner.scan_text("f.md", f"bind {OK_IPV6_UNSPECIFIED}"), [])

	def test_timestamp_is_not_flagged(self) -> None:
		"""The exact false-positive shape named in the issue."""
		self.assertEqual(
			scanner.scan_text("f.md", "Scan started at 04:34:01 local time"), []
		)

	def test_mac_address_is_not_flagged(self) -> None:
		"""Six groups with no `::` can never validate as IPv6 (needs 8)."""
		mac = ":".join(("aa", "bb", "cc", "dd", "ee", "ff"))
		self.assertEqual(scanner.scan_text("f.md", f"NIC {mac}"), [])

	def test_sha_hash_is_not_flagged(self) -> None:
		sha = opaque_token(64)
		self.assertEqual(scanner.scan_text("f.md", f"commit {sha}"), [])

	def test_windows_path_is_not_flagged(self) -> None:
		self.assertEqual(
			scanner.scan_text("f.md", r"log at C:\Users\svc\AppData\Local"), []
		)

	def test_css_hex_color_is_not_flagged(self) -> None:
		self.assertEqual(
			scanner.scan_text("f.md", "border: 1px solid #fe8080;"), []
		)

	def test_base64_blob_is_not_flagged(self) -> None:
		# Not "token:" or similar — that keyword-plus-base64 shape is exactly
		# what gitleaks' own generic-api-key rule exists to catch, and this
		# fixture has nothing to do with secrets; it is here to prove the
		# IPv6 detector stays quiet, not to trip a different scanner.
		blob = "YWJjZGVmMDEyMzQ1Njc4OQ=="
		self.assertEqual(scanner.scan_text("f.md", f"cached payload {blob}"), [])

	def test_docker_digest_is_not_flagged(self) -> None:
		"""A single colon (image@sha256:<hex>) is below the 2-colon floor."""
		digest = "sha256:" + opaque_token(64)
		self.assertEqual(scanner.scan_text("f.md", f"image@{digest}"), [])

	def test_ipv4_mapped_form_also_trips_the_ipv4_detector(self) -> None:
		"""Documented overlap, not a bug: both detectors independently fire."""
		mapped = f"::ffff:{LAB_IP}"
		findings = scanner.scan_text("f.md", f"legacy client at {mapped}")
		self.assertEqual(len(findings), 2, findings)
		self.assertTrue(
			any("IPv6 address literal" in f for f in findings), findings
		)
		self.assertTrue(
			any("IP address literal" in f for f in findings), findings
		)

	def test_ipv6_check_is_individually_waivable(self) -> None:
		self.assertEqual(len(scanner.scan_text("f.md", LAB_IPV6)), 1)
		scanner.ALLOWLIST_FINDINGS["f.md"] = {scanner.CHECK_IPV6: "test"}
		try:
			self.assertEqual(scanner.scan_text("f.md", LAB_IPV6), [])
		finally:
			del scanner.ALLOWLIST_FINDINGS["f.md"]


class AllowlistTests(unittest.TestCase):
	"""Exemptions are per-file AND per-check.

	The property that holds is that an exemption must be *enumerated* — every
	waived check named, each with its own reason. The property that does NOT
	hold, despite an earlier claim in this repo, is that a whole-file
	switch-off is impossible: there are four checks (issue #112 added ipv6
	as the fourth), so naming all four is a whole-file exemption.
	`test_naming_every_check_is_a_whole_file_exemption` pins that honestly
	rather than leaving the docs asserting otherwise.
	"""

	def setUp(self) -> None:
		self._saved = dict(scanner.ALLOWLIST_FINDINGS)
		self.addCleanup(self._restore)

	def _restore(self) -> None:
		scanner.ALLOWLIST_FINDINGS.clear()
		scanner.ALLOWLIST_FINDINGS.update(self._saved)

	def test_repo_ships_with_no_exemptions(self) -> None:
		"""Steady state. If this fails, the new entry needs a reason in review."""
		self.assertEqual(scanner.ALLOWLIST_FINDINGS, {})

	def test_waiving_one_check_leaves_the_others_live(self) -> None:
		"""The exact defect this mechanism replaces: waiving the FQDN nit
		must not switch off the IP and depot-token detectors."""
		scanner.ALLOWLIST_FINDINGS["mock.html"] = {scanner.CHECK_FQDN: "naming nit"}
		text = f"{LAB_FQDN} at {LAB_IP} depot_token: {opaque_token()}"
		findings = scanner.scan_text("mock.html", text)
		self.assertEqual(len(findings), 2, findings)
		self.assertFalse(any("lab-style FQDN" in f for f in findings), findings)
		self.assertTrue(any("IP address literal" in f for f in findings), findings)
		self.assertTrue(any("depot/entitlement token" in f for f in findings), findings)

	def test_exemption_applies_only_to_the_exact_path(self) -> None:
		scanner.ALLOWLIST_FINDINGS["docs/ui/a.html"] = {scanner.CHECK_FQDN: "r"}
		self.assertEqual(scanner.scan_text("docs/ui/a.html", LAB_FQDN), [])
		# Sibling in the same directory is still scanned.
		self.assertEqual(len(scanner.scan_text("docs/ui/b.html", LAB_FQDN)), 1)
		# A path that merely shares a prefix is still scanned.
		self.assertEqual(len(scanner.scan_text("docs/ui/a.html.bak", LAB_FQDN)), 1)

	def test_every_check_is_individually_waivable(self) -> None:
		cases = {
			scanner.CHECK_IP: f"host {LAB_IP}",
			scanner.CHECK_FQDN: f"host {LAB_FQDN}",
			scanner.CHECK_DEPOT_TOKEN: f"depot_token: {opaque_token()}",
		}
		for check, text in cases.items():
			with self.subTest(check=check):
				self.assertEqual(len(scanner.scan_text("f.md", text)), 1)
				scanner.ALLOWLIST_FINDINGS["f.md"] = {check: "test"}
				self.assertEqual(scanner.scan_text("f.md", text), [])
				del scanner.ALLOWLIST_FINDINGS["f.md"]

	def test_unknown_check_name_is_rejected_loudly(self) -> None:
		"""A typo'd check name must not read as a live exemption."""
		scanner.ALLOWLIST_FINDINGS["f.md"] = {"fqdns": "typo"}
		with self.assertRaises(ValueError) as ctx:
			scanner._validate_allowlist()
		self.assertIn("fqdns", str(ctx.exception))

	def test_valid_allowlist_passes_validation(self) -> None:
		scanner.ALLOWLIST_FINDINGS["f.md"] = {scanner.CHECK_IP: "reason"}
		scanner._validate_allowlist()

	def test_there_is_no_whole_file_allowlist_constant(self) -> None:
		"""The predecessor mechanism must stay gone, not merely unused."""
		self.assertFalse(hasattr(scanner, "ALLOWLIST_FILES"))

	def test_naming_every_check_is_a_whole_file_exemption(self) -> None:
		"""The honest limit of this mechanism, asserted rather than claimed.

		PR #83 round 2 found the code comment, docs/testing.md and the PR body
		all asserting that a whole-file exemption was "inexpressible by
		construction". It is not: CHECK_NAMES has four members (issue #112
		added ipv6 as the fourth), so an entry naming all four silences every
		detector on that path, and _validate_allowlist() accepts it without
		complaint. What the mechanism buys is that such an entry has to be
		spelled out check by check with a reason each, where a reviewer will
		see it — not that it cannot be written. This test exists so the
		limitation stays documented in executable form; if a future change
		really does make it impossible, this test fails and the docs get
		corrected with it.
		"""
		entry = {check: "documented limitation" for check in scanner.CHECK_NAMES}
		self.assertEqual(len(entry), 4, entry)
		text = f"{LAB_FQDN} at {LAB_IP} depot_token: {opaque_token()} host {LAB_IPV6}"
		self.assertEqual(len(scanner.scan_text("elsewhere.html", text)), 4)

		scanner.ALLOWLIST_FINDINGS["mock.html"] = entry
		scanner._validate_allowlist()  # accepted, no raise
		self.assertEqual(scanner.scan_text("mock.html", text), [])


class FileHandlingTests(unittest.TestCase):
	"""scan_file's IO behaviour: skip cleanly, never raise."""

	def setUp(self) -> None:
		import tempfile

		self.tmp = tempfile.TemporaryDirectory()
		self.addCleanup(self.tmp.cleanup)
		self.root = Path(self.tmp.name)

	def test_binary_extensions_are_skipped(self) -> None:
		for ext in (".png", ".woff2", ".pem", ".key", ".zip"):
			with self.subTest(ext=ext):
				path = self.root / f"asset{ext}"
				path.write_text(f"host {LAB_FQDN} at {LAB_IP}", encoding="utf-8")
				self.assertEqual(scanner.scan_file(path, rel=path.name), [])

	def test_undecodable_file_is_skipped_without_raising(self) -> None:
		path = self.root / "blob.txt"
		path.write_bytes(b"\xff\xfe\x00\x01 not utf-8 \xc3\x28")
		self.assertEqual(scanner.scan_file(path, rel="blob.txt"), [])

	def test_missing_file_is_skipped_without_raising(self) -> None:
		path = self.root / "gone.md"
		self.assertEqual(scanner.scan_file(path, rel="gone.md"), [])

	def test_text_file_is_scanned(self) -> None:
		path = self.root / "notes.md"
		path.write_text(f"host {LAB_IP}", encoding="utf-8")
		findings = scanner.scan_file(path, rel="notes.md")
		self.assertEqual(len(findings), 1, findings)
		self.assertTrue(findings[0].startswith("notes.md:1:"), findings[0])


class ExitCodeTests(unittest.TestCase):
	"""main() is the gate's actual contract with CI."""

	@staticmethod
	def _run_main() -> int:
		"""Call main() with its stdout swallowed.

		main() prints its findings report, and a passing test that echoes
		"sanitization scan found the following issues" into a green job's log
		reads like a broken gate to whoever scrolls past it.
		"""
		import contextlib
		import io

		with contextlib.redirect_stdout(io.StringIO()):
			return scanner.main()

	def setUp(self) -> None:
		import tempfile

		self.tmp = tempfile.TemporaryDirectory()
		self.addCleanup(self.tmp.cleanup)
		self.root = Path(self.tmp.name)
		self._saved_lister = scanner.list_tracked_files
		self.addCleanup(setattr, scanner, "list_tracked_files", self._saved_lister)
		self._saved_root = scanner.REPO_ROOT
		self.addCleanup(setattr, scanner, "REPO_ROOT", self._saved_root)
		scanner.REPO_ROOT = self.root

	def _track(self, name: str, content: str) -> None:
		path = self.root / name
		path.write_text(content, encoding="utf-8")
		files = sorted(p for p in self.root.iterdir() if p.is_file())
		scanner.list_tracked_files = lambda: files

	def test_clean_tree_exits_zero(self) -> None:
		self._track("ok.md", f"host {fqdn('esxi-01', 'example', 'internal')}")
		self.assertEqual(self._run_main(), 0)

	def test_findings_exit_one(self) -> None:
		self._track("bad.md", f"host {LAB_IP}")
		self.assertEqual(self._run_main(), 1)

	def test_invalid_allowlist_fails_the_run(self) -> None:
		self._track("ok.md", "nothing here")
		scanner.ALLOWLIST_FINDINGS["ok.md"] = {"not-a-check": "typo"}
		self.addCleanup(scanner.ALLOWLIST_FINDINGS.pop, "ok.md", None)
		with self.assertRaises(ValueError):
			self._run_main()


if __name__ == "__main__":
	unittest.main(verbosity=2)
