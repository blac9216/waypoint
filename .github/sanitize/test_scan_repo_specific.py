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


class AllowlistTests(unittest.TestCase):
	"""Exemptions are per-file AND per-check — never a whole-file switch-off."""

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
