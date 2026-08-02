#!/usr/bin/env python3
"""Repo-specific sanitization scan for the public waypoint repo.

Complements gitleaks (generic secret shapes) with three checks CLAUDE.md
requires and gitleaks does not know about:

  1. IPv4 literals that are not RFC 5737 test-net addresses, loopback, or the
     wildcard bind address — i.e. a candidate real lab/host IP.
  2. FQDNs ending in a lab/home/corp-style TLD (.local, .lan, .corp, ...)
     that are not of the CLAUDE.md-sanctioned form `*.example.<tld>`.
  3. Broadcom/VMware depot-token-shaped values: a depot/activation/entitlement
     keyword sitting next to a long opaque token that isn't an obvious
     placeholder.

Scans every git-tracked file at HEAD (not just the diff) so the whole tree is
re-validated on every push, matching the "hard gate on every PR + push" rule
in issue #79. Exits non-zero (and prints every finding) if anything trips.

Tune false positives here, not by weakening the checks: see ALLOWLIST_FINDINGS
below, where every entry names both a path and the specific check(s) waived on
it, with a reason. There is deliberately no whole-file exemption mechanism —
see that constant's comment for why.

The detectors themselves are tested by test_scan_repo_specific.py, which the
sanitize workflow runs before this scan. Running this script against a clean
tree proves only the absence of findings; the tests prove the presence of
detection (issue #90).
"""

from __future__ import annotations

import ipaddress
import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

# Extensions that are binary or pure noise for a text/secret scan. Same
# "denylist, not allowlist" philosophy as frontend/scripts/check-no-external-
# assets.mjs: excluding a short list of known-binary/known-noise types beats
# an allowlist of "text" extensions that silently exempts anything new.
SKIPPED_EXTENSIONS = {
	".png", ".jpg", ".jpeg", ".gif", ".ico", ".woff", ".woff2", ".ttf", ".eot",
	".wasm", ".zip", ".gz", ".tgz", ".pdf", ".pem", ".key", ".pfx", ".p12",
}

# The three checks below, named so an exemption can waive one without
# switching off the others.
CHECK_IP = "ip"
CHECK_FQDN = "fqdn"
CHECK_DEPOT_TOKEN = "depot-token"
CHECK_NAMES = frozenset({CHECK_IP, CHECK_FQDN, CHECK_DEPOT_TOKEN})

# Exemptions, keyed by exact repo-relative path, then by the specific check
# being waived, with a reason. Both halves are mandatory by construction:
# there is no way to express "exempt this file" — only "exempt this check on
# this file" — because a whole-file switch-off is the exact shape that has
# failed open repeatedly in this repo (the frontend air-gap guard's extension
# allowlist in PR #65 round 1, compressed artifacts in #77, compressed formats
# in #81). The predecessor of this constant exempted a 204 KB UI mockup from
# all three checks in order to waive one FQDN naming nit, leaving the IP and
# depot-token detectors dark on the most likely file in the repo for someone
# to paste real lab inventory into. That file was re-sanitized instead
# (issue #86), which is the preferred resolution: fix the content, do not
# exempt the path.
#
# One path per entry — never a directory or a glob, so a new file alongside an
# exempted one is still fully scanned. Empty is the correct steady state.
ALLOWLIST_FINDINGS: dict[str, dict[str, str]] = {}


def _validate_allowlist() -> None:
	"""Fail loudly on an exemption naming a check that does not exist.

	A typo'd check name would otherwise be a silently inert entry that reads
	like a live exemption, which is how an allowlist drifts out of sync with
	the thing it is exempting.
	"""
	for path, waived in ALLOWLIST_FINDINGS.items():
		unknown = set(waived) - CHECK_NAMES
		if unknown:
			raise ValueError(
				f"ALLOWLIST_FINDINGS['{path}'] names unknown check(s) "
				f"{sorted(unknown)}; valid checks are {sorted(CHECK_NAMES)}"
			)


def exempt_checks(rel: str) -> set[str]:
	"""Return the set of check names waived for this repo-relative path."""
	return set(ALLOWLIST_FINDINGS.get(rel, {}))


# --- 1. IPv4 addresses -------------------------------------------------

# A bare dotted-quad, not embedded in a longer dash/dot run (so version
# strings like "vcf-download-tool-9.0.0.0-24089201.tar.gz" don't match).
IPV4_RE = re.compile(r"(?<![\w.-])(?:\d{1,3}\.){3}\d{1,3}(?![\w.-])")

# RFC 5737 documentation ranges (CLAUDE.md mandates these for all example
# data) plus loopback (127.0.0.0/8, including Docker's embedded DNS at
# 127.0.0.11) and the IPv4 wildcard bind address.
ALLOWED_IP_NETWORKS = [
	ipaddress.ip_network("192.0.2.0/24"),
	ipaddress.ip_network("198.51.100.0/24"),
	ipaddress.ip_network("203.0.113.0/24"),
	ipaddress.ip_network("127.0.0.0/8"),
]
ALLOWED_IP_EXACT = {"0.0.0.0"}

# A four-part product version standing alone in a field is shape-identical to
# an IPv4 literal, and Broadcom/VMware versions are routinely four-part, so
# this repo keeps producing them (issue #89 has the observed examples; this
# comment deliberately carries no dotted-quad literal of its own, since this
# scanner reads its own source like any other tracked file). The lookarounds
# on IPV4_RE only suppress the build-suffixed form
# (vcf-download-tool-9.0.0.0-24089201.tar.gz, where the trailing dash keeps
# the quad from standing alone); a version key followed by a bare quoted quad
# still matched, and was invisible only because the file carrying it was
# wholesale-exempted.
#
# Suppress ONLY when a version key sits immediately before the quad, with
# nothing between them but a colon/equals, optional whitespace and an opening
# quote. A mention of the word "version" elsewhere on the line deliberately
# does NOT suppress: otherwise a sentence that merely said "the vCenter
# version at the site is" ahead of a real lab address would waive a real leak.
# Anchored with \Z against the text preceding the match, so it is the
# immediate context or nothing.
VERSION_KEY_BEFORE_RE = re.compile(r"(?i)\bversions?\b\s*[:=]?\s*['\"]?\Z")


def is_version_string(line: str, start: int) -> bool:
	"""True if the quad at `start` is introduced by a version key."""
	return VERSION_KEY_BEFORE_RE.search(line[:start]) is not None


def is_allowed_ip(candidate: str) -> bool:
	"""True if this dotted-quad is a sanctioned address, or not an IP at all.

	A quad with an octet above 255 (e.g. a 2024.1.300.5 version) is not a
	valid IPv4 literal, so it cannot be the lab address this check exists to
	catch — it is not a finding.
	"""
	if candidate in ALLOWED_IP_EXACT:
		return True
	try:
		addr = ipaddress.ip_address(candidate)
	except ValueError:
		return True
	return any(addr in net for net in ALLOWED_IP_NETWORKS)


# --- 2. Lab-style FQDNs --------------------------------------------------

FQDN_RE = re.compile(
	r"(?<![\w.-])"
	r"(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+"
	r"(?:local|lan|home|corp|arpa|intra|lab|internal)"
	r"(?![\w.-])"
)

# TLDs that read as a real internal/home-lab network rather than public
# infrastructure. "internal" is included because CLAUDE.md sanctions it only
# in the compound form example.internal, never bare.
SUSPICIOUS_TLDS = {"local", "lan", "home", "corp", "arpa", "intra", "lab", "internal"}


def is_allowed_fqdn(hostname: str) -> bool:
	labels = hostname.lower().split(".")
	if labels[-1] not in SUSPICIOUS_TLDS:
		return True
	# CLAUDE.md's sanctioned placeholder shape: *.example.<tld>
	return len(labels) >= 2 and labels[-2] == "example"


# --- 3. Depot/activation token shapes ------------------------------------

DEPOT_CONTEXT_RE = re.compile(
	r"(?i)(depot[-_ ]?token|activation[-_ ]?code|entitlement[-_ ]?id|"
	r"support[-_ ]?contract|broadcom[-_ ]?token)\s*[:=]\s*['\"]?"
	r"([A-Za-z0-9+/_=-]{16,})"
)

PLACEHOLDER_MARKERS = (
	"example", "fictional", "changeme", "placeholder", "xxxx", "replace",
	"dev-only", "dev_only", "fake", "your-", "<", "todo", "invented",
)


def is_placeholder_token(value: str) -> bool:
	lowered = value.lower()
	return any(marker in lowered for marker in PLACEHOLDER_MARKERS)


def list_tracked_files() -> list[Path]:
	result = subprocess.run(
		["git", "ls-files"],
		cwd=REPO_ROOT,
		check=True,
		capture_output=True,
		text=True,
	)
	return [REPO_ROOT / line for line in result.stdout.splitlines() if line]


def scan_text(rel: str, text: str) -> list[str]:
	"""Run every non-exempt check over one file's text.

	Split out from scan_file so the detectors are testable without touching
	the filesystem or the git index (issue #90).
	"""
	waived = exempt_checks(rel)
	findings: list[str] = []
	for lineno, line in enumerate(text.splitlines(), start=1):
		if CHECK_IP not in waived:
			for match in IPV4_RE.finditer(line):
				candidate = match.group(0)
				if is_allowed_ip(candidate):
					continue
				if is_version_string(line, match.start()):
					continue
				findings.append(
					f"{rel}:{lineno}: non-RFC-5737 IP address literal: {candidate}"
				)
		if CHECK_FQDN not in waived:
			for match in FQDN_RE.finditer(line):
				hostname = match.group(0)
				if not is_allowed_fqdn(hostname):
					findings.append(
						f"{rel}:{lineno}: lab-style FQDN (not *.example.<tld>): {hostname}"
					)
		if CHECK_DEPOT_TOKEN not in waived:
			for match in DEPOT_CONTEXT_RE.finditer(line):
				token = match.group(2)
				if not is_placeholder_token(token):
					findings.append(
						f"{rel}:{lineno}: possible depot/entitlement token: "
						f"{match.group(1)}=<redacted, {len(token)} chars>"
					)
	return findings


def scan_file(path: Path, rel: str | None = None) -> list[str]:
	if rel is None:
		rel = path.relative_to(REPO_ROOT).as_posix()
	if path.suffix.lower() in SKIPPED_EXTENSIONS:
		return []
	try:
		text = path.read_text(encoding="utf-8")
	except (UnicodeDecodeError, OSError):
		return []
	return scan_text(rel, text)


def main() -> int:
	_validate_allowlist()
	all_findings: list[str] = []
	for path in list_tracked_files():
		if path.is_file():
			all_findings.extend(scan_file(path))

	if all_findings:
		print("Repo-specific sanitization scan found the following issues:\n")
		for finding in all_findings:
			print(f"  - {finding}")
		print(
			"\nSee CLAUDE.md's sanitization policy. The first question is whether "
			"the content should be fixed, not the scanner. If it is a genuine "
			"false positive, fix the detector in "
			".github/sanitize/scan_repo_specific.py with a narrow, tested "
			"exception, or add an ALLOWLIST_FINDINGS entry naming the exact path "
			"and the exact check — never widen a TLD/IP range, and never waive a "
			"check you did not have to."
		)
		return 1

	print("Repo-specific sanitization scan: clean.")
	return 0


if __name__ == "__main__":
	sys.exit(main())
