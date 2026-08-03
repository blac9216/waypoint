#!/usr/bin/env python3
"""Repo-specific sanitization scan for the public waypoint repo.

Complements gitleaks (generic secret shapes) with four checks CLAUDE.md
requires and gitleaks does not know about:

  1. IPv4 literals that are not RFC 5737 test-net addresses, loopback, or the
     wildcard bind address — i.e. a candidate real lab/host IP.
  2. FQDNs ending in a lab/home/corp-style TLD (.local, .lan, .corp, ...)
     that are not of the CLAUDE.md-sanctioned form `*.example.<tld>`.
  3. Broadcom/VMware depot-token-shaped values: a depot/activation/entitlement
     keyword sitting next to a long opaque token that isn't an obvious
     placeholder.
  4. IPv6 literals that are not the RFC 3849 documentation prefix
     (2001:db8::/32), loopback (::1), or unspecified (::) — the same
     candidate-lab-address concept as (1), for the other address family
     (issue #112).

Scans every git-tracked file at HEAD (not just the diff) so the whole tree is
re-validated on every push, matching the "hard gate on every PR + push" rule
in issue #79. Exits non-zero (and prints every finding) if anything trips.

Tune false positives here, not by weakening the checks: see ALLOWLIST_FINDINGS
below, where every entry names both a path and the specific check(s) waived on
it, with a reason. That makes an exemption explicit and individually
justified; it does not make a broad one impossible — see that constant's
comment for exactly what it does and does not buy.

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

# The four checks below, named so an exemption can waive one without
# switching off the others.
CHECK_IP = "ip"
CHECK_FQDN = "fqdn"
CHECK_DEPOT_TOKEN = "depot-token"
CHECK_IPV6 = "ipv6"
CHECK_NAMES = frozenset({CHECK_IP, CHECK_FQDN, CHECK_DEPOT_TOKEN, CHECK_IPV6})

# Exemptions, keyed by exact repo-relative path, then by the specific check
# being waived, with a reason for each. Both halves are mandatory: an entry
# cannot say "exempt this file", only "exempt these named checks on this
# file", and each waived check carries its own justification.
#
# BE PRECISE ABOUT WHAT THAT BUYS. It does NOT make a whole-file exemption
# impossible: CHECK_NAMES has four members, so naming all four in one entry
# silences every detector on that path and _validate_allowlist() accepts it.
# An earlier revision of this comment claimed such an entry was "inexpressible
# by construction", which was simply false (PR #83 round 2 demonstrated it).
# What the mechanism actually buys is that a broad exemption has to be
# WRITTEN OUT — every check enumerated, every one justified — instead of a
# bare path in a list that reads like a naming nit while switching off three
# detectors. It converts a silent hole into a visible one. The enforcement is
# the reviewer who reads the entry, not the data structure.
#
# That distinction matters because the whole-file switch-off is the exact
# shape that has failed open repeatedly in this repo (the frontend air-gap
# guard's extension allowlist in PR #65 round 1, compressed artifacts in #77,
# compressed formats in #81). The predecessor of this constant exempted a
# 204 KB UI mockup from all three checks in order to waive one FQDN naming
# nit, leaving the IP and depot-token detectors dark on the most likely file
# in the repo for someone to paste real lab inventory into. That file was
# re-sanitized instead (issue #86), which is the preferred resolution: fix the
# content, do not exempt the path.
#
# One path per entry — never a directory or a glob, so a new file alongside an
# exempted one is still fully scanned. Empty is the correct steady state, and
# a non-empty ALLOWLIST_FINDINGS must also be disclosed in docs/testing.md
# under "What CI covers — and does not".
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
#
# The trailing guard is deliberately NOT `(?![\w.-])`. That earlier form put a
# literal `.` inside the class, so an address that ENDS A SENTENCE was never
# matched at all — "answers at <quad>." scanned clean, and a real lab IP walked
# through the hard gate (PR #83 round 2). A following `.` now terminates the
# token; only a `.` that begins another numeric component still rejects the
# match, which is what keeps a five-part version from being read as a
# four-part address. Written as single-character lookarounds rather than one
# character class so each guard states which neighbour it is rejecting and why.
#
# Two more narrowings, both issue #111:
#
# - The LEADING word-character guard now names `[A-Za-z0-9]` explicitly
#   instead of `\w` (which is `[A-Za-z0-9_]`) — but only the leading one.
#   An underscore-prefixed quad (an env-var-style key glued directly to its
#   value, say) needs the character immediately BEFORE the quad to stop
#   blocking on `_`, because that underscore is a separator, not a
#   continuation: whatever precedes it is a different token from the address
#   that follows. The TRAILING guard stays `\w` (underscore still blocks) on
#   purpose — narrowing it too was tried and reopened a real false positive
#   in this repo's own `.editorconfig` (see EditorconfigRegressionTests in
#   the test suite for the pinned case): a dotted `.editorconfig` key ending
#   in one of `SUSPICIOUS_TLDS` matched as a complete FQDN whenever the next
#   underscore-joined identifier segment followed immediately, because that
#   TLD word followed by "_" would no longer be rejected as a continuation
#   of the same key. On the trailing side "_" reads as MORE of the same
#   word, not a boundary — the opposite of what it means on the leading
#   side. A letter or digit glued on either side is still exactly the
#   build-suffix shape these guards exist to reject (a version quad with a
#   trailing or leading letter).
#
# - The blanket `(?<!-)`/`(?!-)` dash guards are gone from the regex itself.
#   A dash is still, by default, a rejection — that is what
#   `_dash_glues_to_non_address()` below implements — but a bare regex
#   lookaround cannot tell "glued to a build suffix" apart from "the
#   separator in a dash-joined range" (two dotted-quads back to back), because
#   both are just "a dash". Making that call needs to look at what is on the
#   OTHER side of the dash, which needs code, not a lookaround: a range has a
#   second dotted-quad there, a build suffix does not, and the lookbehind that
#   would have to span that second quad is variable-width, which Python's `re`
#   rejects outright — so this is a hard limit of the engine, not a preference
#   (pinned in ImpossibilityClaimTests). Moving the dash rule
#   out of the regex and into `_dash_glues_to_non_address()` is what lets the
#   range case through while a bare "-" with nothing quad-shaped across it
#   (`10.44.12.7-primary`, `vcenter -10.44.12.7`, ...
#   `vcf-download-tool-9.0.0.0-24089201.tar.gz`) stays exactly as suppressed
#   as before (see test_build_suffixed_version_is_allowed and
#   RangeDetectionTests). Single-sided dash adjacency is deliberately left
#   guarded — this is the narrow fix the issue asks for, not a general
#   loosening of the dash rule.
#
# The per-part digit count is deliberately UNBOUNDED (`\d+`, not `\d{1,3}`),
# issue #119. A bounded shape puts the padding question in two places at once
# — the regex and _parse_ipv4_octets() — and both were previously capped at 3,
# so a four-digit-padded quad never even produced a candidate and the
# zero-padding fix from #111 stopped one digit short of its own stated
# rationale. Which digit strings denote a real address is a question about
# VALUE, and it is answered in exactly one place now: _parse_ipv4_octets()
# strips the padding and rejects anything above 255 or carrying more than
# three significant digits. Widening the regex alone would change nothing, and
# narrowing it again would silently re-cap the parser.
IPV4_RE = re.compile(
	r"(?<![A-Za-z0-9])(?<!\.)"       # not continuing an alnum/dotted run
	r"(?:\d+\.){3}\d+"
	r"(?!\w)"                        # not followed by more token characters
	r"(?!\.\d)"                      # ...but a trailing sentence period is fine
)

# A bare dotted-quad with none of IPV4_RE's boundary guards, used only to
# answer "is there an address-shaped run touching this exact position" for
# the dash-glue check below. It has no business being matched against on its
# own — always go through IPV4_RE (or _dash_glues_to_non_address) for that.
_BARE_QUAD_RE = re.compile(r"(?:\d+\.){3}\d+")


def _quad_ends_at(line: str, pos: int) -> bool:
	"""True if some dotted-quad in `line` ends exactly at index `pos`."""
	return any(match.end() == pos for match in _BARE_QUAD_RE.finditer(line, 0, pos))


def _quad_starts_at(line: str, pos: int) -> bool:
	"""True if a dotted-quad starts exactly at index `pos` in `line`."""
	return _BARE_QUAD_RE.match(line, pos) is not None


def _dash_glues_to_non_address(line: str, start: int, end: int) -> bool:
	"""True if a `-` touching this IPV4_RE match glues it to non-address text.

	IPV4_RE no longer rejects dash adjacency on its own (see the comment
	above it), so every match reaching this function still needs the dash
	case resolved: a `-` on either side is a rejection UNLESS the token
	immediately across it is itself a full dotted-quad — the range case from
	issue #111.

	Every OTHER neighbour reaches this function too, and is deliberately left
	alone here rather than being unable to arrive: IPV4_RE's own lookarounds
	have already decided it (a letter or digit rejected the match, `_` was
	deliberately allowed on the leading side, everything else terminates the
	token), so this function has nothing left to add and returns False. An
	earlier revision of this docstring claimed such neighbours "never reach
	here", which was simply untrue — they all do. The neighbour-by-neighbour
	outcome is enumerated in ImpossibilityClaimTests rather than asserted.
	"""
	if start > 0 and line[start - 1] == "-" and not _quad_ends_at(line, start - 1):
		return True
	if end < len(line) and line[end] == "-" and not _quad_starts_at(line, end + 1):
		return True
	return False

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
# Suppress ONLY when the word "version"/"versions" sits immediately before the
# quad, with nothing between them but optional whitespace, an OPTIONAL
# colon/equals, and an optional opening quote.
#
# The separator being optional is deliberate but wider than a "version: 'A.B.C.D'"
# key/value shape, and the difference is worth stating rather than leaving a
# reader to derive it: "version A.B.C.D", "--version A.B.C.D", "x-version
# A.B.C.D" and "app.version=A.B.C.D" are all suppressed too, because release
# notes, CLI help text and changelogs write versions that way and a hard gate
# that false-positives on them gets muted. `\b` bounds the word, so
# "mgmt_version" (underscore is a word character) does NOT suppress, and
# neither does a backtick before the quad. (Placeholders, not literals: this
# comment keeps the no-dotted-quad discipline stated above, and the concrete
# spellings are pinned in test_separator_is_optional_as_documented.)
#
# What stays excluded is the thing that matters: a mention of "version" ELSEWHERE
# on the line does not suppress, because \Z anchors the match to the text
# immediately preceding the quad. Otherwise a sentence that merely said "the
# vCenter version at the site is" ahead of a real lab address would waive a real
# leak. Immediate context or nothing — that bound, not the separator, is what
# keeps this from becoming a line-wide bypass, and it is what the tests pin.
VERSION_KEY_BEFORE_RE = re.compile(r"(?i)\bversions?\b\s*[:=]?\s*['\"]?\Z")


def is_version_string(line: str, start: int) -> bool:
	"""True if the quad at `start` is introduced by a version key."""
	return VERSION_KEY_BEFORE_RE.search(line[:start]) is not None


def _parse_ipv4_octets(candidate: str) -> list[int] | None:
	"""Parse a dotted-quad candidate into its four octet values, or None.

	None means the candidate is not actually a valid IPv4 literal — an octet
	over 255 (a product-version quad, e.g. "2024.1.300.5") or a part carrying
	more than three SIGNIFICANT digits once its padding is removed. IPV4_RE
	matches `\\d+` per part and enforces neither, so this is the single place
	where "do these digits denote a real address" is decided.

	Leading zeros ("010") parse here even though Python's `ipaddress` module
	rejects them outright as ambiguous octal notation (issue #111): CLAUDE.md
	cares whether the digits denote a real address, not whether the author
	zero-padded it, and a zero-padded lab quad is exactly as real a leak as
	an unpadded one.

	That rationale is padding-WIDTH-insensitive, so the length test counts
	significant digits rather than characters (issue #119). An earlier version
	rejected `len(part) > 3`, which meant a four-digit-padded quad
	("0010.0044...") read as "not an address at all, so allowed" — the
	original bug one padding digit further out. Any padding width now
	normalises to the same address.
	"""
	parts = candidate.split(".")
	if len(parts) != 4:
		return None
	octets: list[int] = []
	for part in parts:
		if not part.isdigit() or len(part.lstrip("0")) > 3:
			return None
		value = int(part)
		if value > 255:
			return None
		octets.append(value)
	return octets


def is_allowed_ip(candidate: str) -> bool:
	"""True if this dotted-quad is a sanctioned address, or not an IP at all.

	A quad with an octet above 255 (e.g. a 2024.1.300.5 version) is not a
	valid IPv4 literal, so it cannot be the lab address this check exists to
	catch — it is not a finding. (VersionStringTests and ZeroPaddedQuadTests
	pin that "cannot" as an executed case rather than an argued one, at every
	padding width.)
	"""
	octets = _parse_ipv4_octets(candidate)
	if octets is None:
		return True
	normalized = ".".join(str(octet) for octet in octets)
	if normalized in ALLOWED_IP_EXACT:
		return True
	addr = ipaddress.ip_address(normalized)
	return any(addr in net for net in ALLOWED_IP_NETWORKS)


# --- 2. Lab-style FQDNs --------------------------------------------------

# Same trailing-delimiter fix as IPV4_RE — a hostname ending a sentence
# ("Host <fqdn>.") was previously invisible — plus a case-insensitive TLD
# alternation. The alternation used to be case-sensitive while
# is_allowed_fqdn() lowercases its input, so case-insensitive matching was
# clearly the intent but the regex never reached it: any upper- or mixed-case
# spelling of a lab TLD scanned clean. Uppercase FQDNs are the normal shape in
# AD/Windows material, exported inventories, CKL/HDF results and certificate
# CNs — exactly the artifacts CLAUDE.md forbids committing. The `(?i:...)`
# group scopes the flag to the TLD so it does not widen the rest of the
# pattern. (Like the version comment above, this one carries no lab-shaped
# literal of its own: the scanner reads its own source like any other tracked
# file, and the case fixtures live in the test suite where they are assembled
# at runtime.)
#
# LEADING guard names `[A-Za-z0-9]` rather than `\w`, same reasoning and same
# issue (#111) as IPV4_RE above, and leading-only for the same reason:
# FQDN_RE's label characters never include `_` (DNS labels can't), so an
# underscore directly before the match start cannot be continuing the
# hostname — it is a separator (an environment-variable-style name prefixed
# straight onto a hostname, with no space), and the old `\w` guard swallowed
# the whole hostname by reading that underscore as "more token". That "never"
# is a property of the pattern, so it is checked as one:
# test_fqdn_matches_never_contain_an_underscore executes it instead of
# trusting the reading.
#
# The TRAILING guard stays `\w` (still blocks `_`): narrowing it too matched
# a real, unmodified line inside this repo's own `.editorconfig` as a
# complete FQDN — a dotted key ending in one of `SUSPICIOUS_TLDS`, followed
# immediately by another underscore-joined identifier segment, because that
# TLD word followed by "_" would no longer be read as a continuation of the
# same key. On the trailing side `_` means MORE of the word that was just
# matched, not a boundary — see EditorconfigRegressionTests for the pinned
# case (built from the same naming convention rather than quoting the real
# file, so this comment carries no matchable literal of its own either).
#
# The `-` guards stay exactly as they were: this is the narrow,
# underscore-only fix, not the dash-adjacency question that IPV4_RE resolves
# separately (a "range" of two hostnames isn't a shape this repo's content
# has ever produced, so there is nothing here for the dash-glue mechanism to
# be load-bearing against — see the PR body for what that means for the two
# remaining dash-adjacent FQDN shapes from the issue).
FQDN_RE = re.compile(
	r"(?<![A-Za-z0-9])(?<!\.)(?<!-)"
	r"(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+"
	r"(?i:local|lan|home|corp|arpa|intra|lab|internal)"
	r"(?!\w)(?!-)"
	r"(?!\.[a-zA-Z0-9])"             # trailing sentence period is fine
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


# --- 4. IPv6 addresses (issue #112) ---------------------------------------
#
# No detector at all previously covered this address family. The hard part
# here is not the policy (same "candidate real lab address" concept as IPv4)
# but the regex: a naive hextet-and-colon pattern false-positives readily on
# SHA hashes, base64, MAC addresses, CSS/hex colors, Windows drive paths, and
# — the specific case named in the issue — timestamps (an hours:minutes:
# seconds run is shape-identical to three short hextet groups).
#
# The approach: keep the regex loose (find any plausible run of hex digits,
# colons, and an optional trailing dotted-quad for the IPv4-mapped form),
# then let `ipaddress.IPv6Address` be the actual arbiter of validity, exactly
# as `is_allowed_ip` already lets `ipaddress.ip_address` decide for IPv4.
# That single check is what rejects the false-positive sources above without
# hand-enumerating every non-address shape a regex would otherwise need to
# reject:
#   - a timestamp has too few groups and no "::" compression — a literal
#     needs exactly 8 groups without it, so a 3-group HH:MM:SS run never
#     parses;
#   - a MAC address is 6 groups with no "::" — also short of the 8 groups a
#     literal needs without compression, so it never parses either;
#   - a SHA hash, base64 blob, CSS hex color, or a Windows drive-letter path
#     carries at most one colon, so `_is_ipv6_finding()` discards it before
#     validation is even attempted.
#
# Both "never parses" claims survive the swallowed-port retry in
# _ipv6_address_of(), and that is checked rather than assumed: the retry drops
# ONE trailing all-digit group, which takes a 3-group timestamp to 2 groups and
# a 6-group MAC to 5 — further from the 8 an uncompressed literal needs, never
# closer. What the retry does reach is a NINE-group run whose last group is all
# digits, which is the address-plus-port shape it exists for; the corresponding
# false-positive question ("what else is nine colon-separated hex groups?") is
# enumerated in FalsePositiveCorpusTests rather than dismissed.
#
# The bracketed alternative handles the URL form with a port (an address in
# brackets with a `:<port>` suffix appended, RFC 3986 style); zone IDs (a
# `%` followed by an interface name) are matched but stripped before
# validation, since `ipaddress.IPv6Address` does not accept them.
#
# THE BOUNDARY GUARDS ARE THE PART THAT KEEPS GETTING THIS WRONG. The first
# version of this detector shipped `(?<![\w:.])` / `(?![\w:.])` — a literal
# `.` inside the class — which is the exact defect the IPv4 comment above
# spends two paragraphs warning about, reintroduced for the other address
# family: an IPv6 literal that ENDS A SENTENCE was never matched at all, so
# "the manager answers at <ula>." scanned clean and walked through the hard
# gate (PR #115 round 1, the same escape shape as PR #83 round 2). Written
# as single-character lookarounds so each guard states which neighbour it is
# rejecting and why:
#
#   trailing (?!\w)            - a letter/digit/underscore glued on is more of
#                                the same token, not an address boundary.
#   trailing (?!\.[A-Za-z0-9]) - a `.` that CONTINUES the token (the dotted
#                                quad of the IPv4-mapped form) still rejects;
#                                a `.` that ends a sentence does not.
#   leading  (?<![A-Za-z0-9])  - same rule mirrored, and `_` is deliberately
#                                allowed here exactly as it is on IPV4_RE's
#                                leading side (issue #111): before the match an
#                                underscore separates two tokens.
#
# The leading guard no longer rejects `:` or `.` either. Those rejections
# hid `addr:<ula>` and `x.<ula>` — in both, the character is the delimiter
# between a key/label and the address that follows, exactly as it is for
# IPV4_RE (whose leading guard has never rejected `:`).
#
# THAT CHANGE DOES resurrect mid-run matches, and an earlier revision of this
# comment claimed it could not ("matches are non-overlapping and the leftmost
# one greedily consumes the whole hex/colon run"). The claim was false and the
# falsehood was a live defect: when the leftmost start is REJECTED BY THE
# LEADING GUARD there is no match to consume the run, so the engine restarts
# INSIDE it, and a word whose tail is hex digits glued straight onto a
# sanctioned literal reported the tail of that literal as a finding — a false
# positive on the documentation prefix (PR #115 round 2, finding 3). A
# mid-run restart is now resolved in code by _widest_address_start() below,
# not asserted away in a comment; see that function for the rule and
# MidRunMatchTests for the pinned cases.
#
# There is deliberately NO trailing "not a colon" guard. The argument for
# adding one is that it would be free, since a greedy hex/colon class supposedly
# cannot stop with a colon still ahead of it. THAT ARGUMENT IS WRONG TWICE OVER,
# and both halves were measured rather than reasoned (the second was found by
# auditing this comment in PR #115 round 3, after two of its neighbours had
# already turned out to be false):
#
#   1. The match need not end in the hex/colon class at all. When it ends in the
#      IPv4-mapped dotted quad or a zone id, a following `:port` DOES reach the
#      guard, and the guard then rejects the whole match instead of ending it —
#      the IPv4-mapped form written with a port lost its IPv6 finding that way,
#      quietly, because the IPv4 detector still fired on the embedded quad.
#   2. Even a plain hex/colon run can end with a colon ahead of it, because the
#      class is greedy but the TRAILING GUARDS BACKTRACK. Given a run followed by
#      a port and then a letter, the regex gives back characters until the guards
#      are satisfied, and what satisfies them is the address — with the `:` of
#      the port sitting immediately after the match. A "not a colon" guard would
#      reject that too.
#
# (Spelled out in prose rather than as an example: this scanner reads its own
# source like any other tracked file, and the mapped-form prefix is itself a
# valid literal — the same reason the IPv4 and FQDN comments above carry no
# address-shaped literal either. Both cases are pinned in IPv6DetectorTests and
# ImpossibilityClaimTests.) A delimiter colon is handled
# by _trim_delimiter_colons() below, which is where the "a colon next to a
# literal is not part of it" rule belongs, and a swallowed `:port` by
# _ipv6_address_of().
#
# EVERY OPTIONAL OR ALTERNATIVE CONSTRUCT BELOW IS A NAMED GROUP, and that is
# a test contract rather than a style choice. `bracketed`/`bare` are the two
# alternatives; `bracket_zone`, `bracket_port`, `mapped_quad` and `zone` are
# the optional parts. Naming them makes "which shape of address did this line
# actually exercise" observable from a match object, which is what lets the
# test suite enumerate the delimiter matrix over one fixture per shape the
# grammar admits instead of one fixture per detector — the fixture monoculture
# that hid the unbracketed-port escape for a full review round (PR #115
# round 2, finding 1). test_every_grammar_shape_a_detector_admits_is_exercised
# walks this pattern and fails if a new optional construct is added
# anonymously, or if no fixture exercises one both ways.
IPV6_RE = re.compile(
	r"(?<![A-Za-z0-9])"
	r"(?:"
	r"\[(?P<bracketed>[0-9A-Fa-f:]+(?P<bracket_zone>%[\w.-]+)?)\]"
	r"(?P<bracket_port>:\d+)?"
	r"|"
	r"(?P<bare>[0-9A-Fa-f:]+(?P<mapped_quad>(?:\.\d{1,3}){1,3})?"
	r"(?P<zone>%[\w.-]+)?)"
	r")"
	r"(?!\w)"
	r"(?!\.[A-Za-z0-9])"
)

# The characters IPV6_RE's bare alternative is built from. A match that begins
# with one of these to its immediate left began in the MIDDLE of a longer run
# of them — see _widest_address_start().
_HEX_OR_COLON = frozenset("0123456789abcdefABCDEF:")


def _trim_delimiter_colons(candidate: str) -> str:
	"""Drop a leading/trailing `:` that is a delimiter, not part of the address.

	`[0-9A-Fa-f:]+` is greedy, so a colon that merely sits NEXT TO a literal is
	swallowed into the candidate — `<ula>:` at the end of a clause, or a
	`key:<ula>` where the leading guard let the match start at the colon. The
	swallowed colon then makes the candidate fail strict parsing, and
	is_allowed_ipv6() reads that failure as "not an address" — the same
	boundary-character-eats-the-finding shape as the trailing `.` bug above.

	Trimming is safe by construction rather than by taste: a valid IPv6
	literal can only begin or end with a colon as part of `::`, so a SINGLE
	leading or trailing colon is never part of one. Doubled colons are left
	alone, which is what keeps `::1`, `::` and a bare `<prefix>::` intact.

	"Safe" means one direction only, and only that direction is claimed:
	trimming can never turn a parseable address into an unparseable one, so
	it can never LOSE a finding. It can (and does) turn an unparseable span
	into an address, which is the point. That direction is not left as an
	assertion — test_trimming_never_turns_an_address_into_a_non_address
	exhausts it over every generated candidate shape.
	"""
	if candidate.endswith(":") and not candidate.endswith("::"):
		candidate = candidate[:-1]
	if candidate.startswith(":") and not candidate.startswith("::"):
		candidate = candidate[1:]
	return candidate


# RFC 3849's documentation prefix (CLAUDE.md's IPv6 analogue of RFC 5737),
# plus loopback and the unspecified address. The RFC 4291 link-local prefix
# and the RFC 4193 unique-local prefix (the common case sets its 8th bit,
# which is why lab addresses in that space so often start the same way) are
# deliberately NOT here — those are exactly the lab-address shapes issue
# #112 exists to catch, the IPv6 equivalent of RFC 1918 space being
# unexempted for IPv4. (Same no-literal discipline as the IPv4/FQDN comments
# above: this note names the RFCs rather than quoting the prefixes, since a
# quoted prefix is itself an address-shaped string this scanner would catch.)
ALLOWED_IPV6_NETWORKS = [
	ipaddress.ip_network("2001:db8::/32"),
]
# Compared as ADDRESSES, not as strings: loopback and the unspecified address
# have a fully-expanded spelling too, and a gate that allows the compressed
# spelling while flagging the expanded one is a false positive waiting for the
# first person who writes the long form. Same reasoning as _parse_ipv4_octets()
# normalising zero-padding before deciding — the question is which address the
# digits denote, never how the author chose to spell it.
ALLOWED_IPV6_EXACT = frozenset(
	ipaddress.IPv6Address(text) for text in ("::1", "::")
)


def _parse_ipv6(text: str) -> ipaddress.IPv6Address | None:
	"""Strict parse, or None. `ipaddress` is the arbiter; this just softens it."""
	try:
		return ipaddress.IPv6Address(text)
	except ValueError:
		return None


def _ipv6_address_of(candidate: str) -> ipaddress.IPv6Address | None:
	"""The address this candidate denotes, or None if it denotes none.

	Two things stand between a matched span and an address.

	A zone id (`%eth0`) is stripped: `ipaddress` does not accept one, and it
	says nothing about which address is on the line.

	A SWALLOWED PORT is the harder one, and it is why this function exists at
	all (issue #112's Impact paragraph, PR #115 round 2, finding 1). Written
	unbracketed, `<address>:<port>` is one unbroken run of hex digits and
	colons, so IPV6_RE's greedy class takes the port too. For a COMPRESSED
	address the port then absorbs as one more legal group and the line is
	flagged anyway, by luck; for a FULLY-EXPANDED one it is a ninth group,
	strict parsing fails, and the failure used to read as "not an address, so
	allowed" — a lab address in the shape a log line, a netstat, or an
	inventory export writes it walked the gate, exit 0. So a candidate that
	does not parse is retried with a trailing all-digit group removed, and
	only then declared a non-address.

	The retry is a LOOP, not a single attempt (issue #131). One retry closes
	one trailing numeric group; a candidate carrying TWO of them (a
	zero-padded fully-expanded literal followed by a bare port, e.g.
	`...:0007:443:8443`, or the same literal with an all-zero group ahead of
	the port, `...:0007:0:443`) still failed strict parsing after a single
	retry and read as "not an address, so allowed" — the same escape PR #115
	round 2 closed for the one-group case, one group further out. The bound
	that matters is the NUMBER of trailing all-digit groups stripped, not how
	wide any one of them is: each iteration removes exactly one whole group
	and re-parses, so the loop terminates the instant a strict parse succeeds
	or the tail is no longer all-digit — there is nothing here for an
	attacker to widen. The port's digit count stays deliberately unbounded
	within each group, for the same #119 reason as before: a second, arbitrary
	width bound is what that issue cost, and bounding the group WIDTH here
	would be exactly that mistake with a new coat of paint.

	The retry still cannot manufacture an address out of a non-address: every
	iteration hands the shortened text to the same strict parser, so what
	finally comes back is a real literal or nothing
	(test_the_port_retry_only_ever_returns_what_the_parser_accepts).

	Deliberately NOT closed here: a trailing group carrying a non-digit
	character glued on (`...:0007:443a`) fails `tail.isdigit()` on its very
	first iteration, so the loop never starts stripping it. That is a
	different shape — the group is not a port at all, digit or otherwise —
	and no shape in this gate's threat model (netstat, log lines, inventory
	exports, URLs) produces it; disclosed as a residual in
	UnbracketedPortTests and docs/testing.md rather than silently left for
	the next reviewer to re-discover.
	"""
	base = candidate.split("%", 1)[0]
	addr = _parse_ipv6(base)
	if addr is not None:
		return addr
	head = base
	while True:
		head, separator, tail = head.rpartition(":")
		if not separator or not tail.isdigit():
			return None
		addr = _parse_ipv6(head)
		if addr is not None:
			return addr


def is_allowed_ipv6(candidate: str) -> bool:
	"""True if this is a sanctioned IPv6 address, or not a valid literal at all.

	Mirrors is_allowed_ip(): a span that denotes no address is not the lab
	address this check exists to catch, so it is not a finding either.
	"""
	addr = _ipv6_address_of(candidate)
	if addr is None:
		return True
	if addr in ALLOWED_IPV6_EXACT:
		return True
	return any(addr in net for net in ALLOWED_IPV6_NETWORKS)


def _widest_address_start(line: str, run_start: int, match_start: int, end: int) -> int:
	"""Where the address ending at `end` really begins, re-anchored leftward.

	IPV6_RE's leading guard rejects a match glued to an alphanumeric, but a
	rejected START is not a rejected LINE: the regex engine simply restarts
	further along, INSIDE the same hex/colon run, and reports whatever tail
	still parses. A word ending in hex digits written straight onto an
	address is enough to trigger it, and the reported tail can be a different
	address from the one on the line — including a sanctioned one reported as
	a finding (PR #115 round 2, finding 3).

	So a mid-run match is re-anchored: of every span that ends at `end` and
	starts at or before `match_start` but no earlier than the run does, the
	WIDEST one that still parses as an address wins. The fragment the engine
	found is only used if no wider span parses.

	Widest-wins is what makes the two cases come out differently:

	  - a sanctioned literal with a word glued to its front re-anchors onto
	    the whole literal, which is sanctioned, so nothing is reported;
	  - a lab literal with a hostname glued to its front re-anchors onto the
	    widest parseable span, which still contains the lab address, so it is
	    still reported.

	This never CREATES a finding: scan_text only calls it once the fragment
	is already a finding on its own, so re-anchoring can widen, suppress, or
	leave a finding alone, never invent one. That ordering is load-bearing —
	without it, `word::hexword` scope-resolution syntax would re-anchor into
	a parseable address (see MidRunMatchTests).
	"""
	for start in range(run_start, match_start):
		if _ipv6_address_of(_trim_delimiter_colons(line[start:end])) is not None:
			return start
	return match_start


def _hex_run_start(line: str, match_start: int) -> int:
	"""Index at which the maximal hex/colon run containing `match_start` begins."""
	start = match_start
	while start > 0 and line[start - 1] in _HEX_OR_COLON:
		start -= 1
	return start


def _is_ipv6_finding(candidate: str) -> bool:
	"""True if this candidate is a reportable IPv6 literal.

	The two-colon floor is a signal-to-noise floor, not a syntax rule: a SHA
	hash, a base64 blob, a CSS hex colour and a Windows drive path all carry
	at most one colon, so requiring two discards them before `ipaddress` is
	asked anything. A real literal always has at least two (the shortest
	spellings are `::` and `::1`).
	"""
	return candidate.count(":") >= 2 and not is_allowed_ipv6(candidate)


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
				if _dash_glues_to_non_address(line, match.start(), match.end()):
					continue
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
		if CHECK_IPV6 not in waived:
			for match in IPV6_RE.finditer(line):
				if match.group("bracketed") is not None:
					# Brackets are their own boundary: the `[` cannot be part
					# of a hex/colon run, so there is nothing to re-anchor.
					candidate = _trim_delimiter_colons(match.group("bracketed"))
					reported = match.group(0)
				else:
					candidate = _trim_delimiter_colons(match.group("bare"))
					if not _is_ipv6_finding(candidate):
						continue
					# Re-anchor a match that began mid-run, and only then —
					# so re-anchoring can never invent a finding the fragment
					# did not already produce. See _widest_address_start().
					start = _hex_run_start(line, match.start())
					if start < match.start():
						start = _widest_address_start(
							line, start, match.start(), match.end()
						)
					candidate = _trim_delimiter_colons(line[start:match.end()])
					reported = candidate
				if not _is_ipv6_finding(candidate):
					continue
				findings.append(
					f"{rel}:{lineno}: possible IPv6 address literal: {reported}"
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
