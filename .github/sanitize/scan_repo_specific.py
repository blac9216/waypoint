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

Tune false positives here, not by weakening the checks: see ALLOWLIST_FILES
and ALLOWLIST_FINDINGS below, each with a reason.
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

# Files this scan does not apply to, with a reason. Keep this list short and
# specific to one path per entry — never a whole directory or a glob, so a
# new file under the same directory still gets scanned.
ALLOWLIST_FILES: dict[str, str] = {
	"docs/ui/prototype/vcf-ops-console.dc.html": (
		"Pre-existing M0 design mockup (out of scope for #79 — docs/ui/ is "
		"owned by other work) uses a *.corp.local fictional naming scheme "
		"for its mock inventory data instead of the CLAUDE.md-canonical "
		"*.example.internal form. Tracked as issue #86 rather than silently "
		"widening this scanner's TLD allowlist, which would blind it to a "
		"real .corp.local leak anywhere else."
	),
}

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


def is_allowed_ip(candidate: str) -> bool:
	if candidate in ALLOWED_IP_EXACT:
		return True
	try:
		addr = ipaddress.ip_address(candidate)
	except ValueError:
		return False
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


def scan_file(path: Path) -> list[str]:
	rel = path.relative_to(REPO_ROOT).as_posix()
	if rel in ALLOWLIST_FILES:
		return []
	if path.suffix.lower() in SKIPPED_EXTENSIONS:
		return []
	try:
		text = path.read_text(encoding="utf-8")
	except (UnicodeDecodeError, OSError):
		return []

	findings: list[str] = []
	for lineno, line in enumerate(text.splitlines(), start=1):
		for match in IPV4_RE.finditer(line):
			candidate = match.group(0)
			if not is_allowed_ip(candidate):
				findings.append(
					f"{rel}:{lineno}: non-RFC-5737 IP address literal: {candidate}"
				)
		for match in FQDN_RE.finditer(line):
			hostname = match.group(0)
			if not is_allowed_fqdn(hostname):
				findings.append(
					f"{rel}:{lineno}: lab-style FQDN (not *.example.<tld>): {hostname}"
				)
		for match in DEPOT_CONTEXT_RE.finditer(line):
			token = match.group(2)
			if not is_placeholder_token(token):
				findings.append(
					f"{rel}:{lineno}: possible depot/entitlement token: "
					f"{match.group(1)}=<redacted, {len(token)} chars>"
				)
	return findings


def main() -> int:
	all_findings: list[str] = []
	for path in list_tracked_files():
		if path.is_file():
			all_findings.extend(scan_file(path))

	if all_findings:
		print("Repo-specific sanitization scan found the following issues:\n")
		for finding in all_findings:
			print(f"  - {finding}")
		print(
			"\nSee CLAUDE.md's sanitization policy. If this is a genuine false "
			"positive, fix the detector in .github/sanitize/scan_repo_specific.py "
			"with a narrow, documented exception — never widen a TLD/IP range."
		)
		return 1

	print("Repo-specific sanitization scan: clean.")
	return 0


if __name__ == "__main__":
	sys.exit(main())
