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

A tracked file this scanner cannot read as UTF-8 text is NOT silently skipped
(issue #101): unless its extension is in KNOWN_SAFE_BINARY_EXTENSIONS (a short,
named list of asset types this repo actually ships — icons, fonts, wasm — that
cannot carry the kind of text payload these detectors look for), an
uninspectable file fails the run outright and must be justified by a human,
the same "fail loud on the unrecognised, never fall through to a silent pass"
model frontend/scripts/check-no-external-assets.mjs already uses for the
build-output guard. See KNOWN_SAFE_BINARY_EXTENSIONS's own comment for the
full reasoning and _find_uninspectable_tracked_files() for the check itself.

Tune false positives here, not by weakening the checks: see ALLOWLIST_FINDINGS
below, where every entry names both a path and the specific check(s) waived on
it, with a reason. That makes an exemption explicit and individually
justified; it does not make a broad one impossible — see that constant's
comment for exactly what it does and does not buy. ALLOWLIST_FINDINGS only
waives one of the four text detectors on a file that IS scanned; it has no
bearing on KNOWN_SAFE_BINARY_EXTENSIONS, which is a separate, extension-keyed
mechanism for the un-inspectable-content problem.

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

# Issue #101: a binary file this scanner cannot read as text used to be
# skipped and counted as clean in the SAME step as a file that was read and
# found nothing — a green run could not tell the two apart. A `.pem`'s
# Subject/SAN carries a hostname verbatim, and an archive can carry anything;
# silently passing those is exactly the failure CLAUDE.md's sanitization
# mandate exists to prevent.
#
# frontend/scripts/check-no-external-assets.mjs already solved this shape for
# the frontend build guard, and its header comment is the read: a DEnylist of
# "known opaque, therefore skip" fails open forever, because the set of opaque
# formats is unbounded (that file documents five separate times it failed open
# this way). The fix there was to invert the question from "did we remember
# this format" to "is this a format we affirmatively recognise as inert" —
# and default to FAIL, not SKIP, for everything else.
#
# This scanner draws that same line, but the two buckets are narrower than the
# `.mjs` guard's, because this scanner's job is narrower (repo-relative text
# patterns, not "is there a URL anywhere in these bytes"):
#
#   - KNOWN_SAFE_BINARY_EXTENSIONS: image/font/wasm asset types this repo
#     actually ships (frontend/public/icons/*.png today). These are opaque,
#     but nothing in this repo's build ever hand-edits their bytes to embed a
#     hostname or token, and a raster/font/wasm payload is not a text
#     container the four detectors above could meaningfully run against
#     anyway. Skipped, exactly as before.
#
#   - Everything else un-inspectable — certs/keys (`.pem`, `.key`, `.pfx`,
#     `.p12`), archives (`.zip`, `.gz`, `.tgz`), and any other extension this
#     list has never seen — FAILS the scan the moment a tracked file matches
#     it. `_find_uninspectable_tracked_files()` in main() is what enforces
#     that; scan_file() no longer special-cases these paths at all, they are
#     simply files whose extension was never taught to KNOWN_SAFE_BINARY_
#     EXTENSIONS and so are caught before scan_file ever gets a path.
#     A human adding a new binary asset type must either name it in
#     KNOWN_SAFE_BINARY_EXTENSIONS (with a reason, reviewable in the diff —
#     the same "deliberate, reviewable act" the `.mjs` guard's escape hatch
#     is) or justify the file's presence some other way; there is no
#     fallthrough bucket a novel extension can land in unnoticed.
KNOWN_SAFE_BINARY_EXTENSIONS = {
	# App/PWA icons under frontend/public/icons/ (issue #101). Raster image
	# bytes, not a text container; nothing in this repo's build process embeds
	# identifying strings in them.
	".png", ".jpg", ".jpeg", ".gif", ".ico",
	# Web fonts and the one wasm artifact class this repo could ship. Binary
	# formats with no free-text field this repo's tooling ever populates.
	".woff", ".woff2", ".ttf", ".eot", ".wasm",
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
ALLOWLIST_FINDINGS: dict[str, dict[str, str]] = {
	# module.transport.vmware.ps1:111 uses VMware's factory-default SSO domain
	# (the `administrator@` account on the `vsphere` `.local` domain — spelled
	# split here so this comment does not itself trip the FQDN detector; this
	# scanner reads its own source like any other tracked file). That domain is
	# baked into every vCenter deployment and appears verbatim in the unmodified
	# project-owned source imported from vmware-stig-docker (issue #438). It is a
	# product constant, not lab data: it identifies nothing about the author's
	# environment. Only the .local FQDN detector is waived on that one file;
	# IP/IPv6/depot-token detectors stay live.
	"runners/compliance-runner/powershell/module.transport.vmware.ps1": {
		CHECK_FQDN: (
			"VMware default SSO domain (product constant) on the imported "
			"module; present in the unmodified upstream source, not lab data."
		),
	},
}


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


# --- 0. Escape normalization (issue #137) --------------------------------
#
# All three address detectors below anchor on a separator character sitting
# directly between two components (`.` for IPv4/FQDN, `:` for IPv6). A
# backslash inserted immediately before that separator (see issue #137 for
# the exact fictional examples measured against each detector) does not
# trip any leading/trailing guard (a backslash is not in `[A-Za-z0-9]`,
# `\w`, or `-`, the only classes any guard rejects), but it DOES sit between
# the separator and the digits or hex on either side, so the digits/hex
# fragments on each side are too short to independently satisfy `IPV4_RE`'s
# three-dot requirement, `FQDN_RE`'s multi-label requirement, or `IPV6_RE`'s
# two-colon floor. The candidate never becomes a match at all — not a
# suppression, an absence. (This comment deliberately carries no
# backslash-escaped address literal of its own, the same discipline as
# every other detector comment in this file: this scanner reads its own
# source like any other tracked file, and the escaped form is exactly as
# address-shaped to a human reader as the bare one is to a machine.)
#
# This is the same disease #110 names in the frontend air-gap guard's
# `URL_PATTERN` (a JSON-escaped solidus, `https:\/\/`, defeats a
# literal-character regex the same way), and the fix follows the same
# direction rather than adding a fourth alternative to three already-dense
# regexes per Possible Fix 2 in #137: normalize the escape out of the text
# BEFORE any detector sees it, once, at the model level, instead of teaching
# every detector's grammar to admit an optional backslash of its own.
#
# The backslash must be DELETED, not replaced with a same-width filler. A
# filler character sitting where the backslash was still sits BETWEEN the
# separator and the digits/hex on either side, so it still breaks the
# contiguous `\d+` run IPV4_RE needs, the contiguous label FQDN_RE needs, or
# the contiguous hex/colon run IPV6_RE needs — adjacency, not guard
# rejection, is what actually defeats detection here (measured: an
# underscore filler still produced zero IPv4/FQDN findings and only a
# truncated IPv6 fragment; deletion is what closes it). That makes this
# check's normalized line a DIFFERENT LENGTH from the raw line, which is why
# it is a self-contained view: every position-based helper below
# (`_dash_glues_to_non_address`, `_hex_run_start`, `_widest_address_start`,
# `_trim_delimiter_colons`, ...) is always called with the SAME string it
# took its `match.start()`/`match.end()` from, inside `scan_text`'s per-check
# block, and never mixes an offset from one string with a slice of the
# other. Nothing here reports a raw-line slice keyed by a normalized-line
# offset; the reported text is always `match.group(...)` from whichever
# string produced the match.
#
# Deliberately narrow: only a backslash immediately before `.` or `:` is
# deleted. A backslash before anything else (`\Users`, `\n`, `\d`) is left
# alone — that is an ordinary escape convention (Windows paths, shell
# quoting, regex source, `.properties` files) with no separator adjacent to
# it, and deleting every backslash would risk manufacturing false positives
# out of those (`C:\Users` losing its backslash reads differently), not
# close a false negative. See FalsePositiveCorpusTests'
# `C:\Users\example\file.txt` row, which has no backslash immediately before
# `.` or `:` and is unaffected by this pass either way (measured, not just
# argued: no corpus line changes under `_unescape_separators`).
_ESCAPED_SEPARATOR_RE = re.compile(r"\\(?=[.:])")


def _unescape_separators(line: str) -> str:
	"""Drop a backslash sitting immediately before `.` or `:`.

	NOT length-preserving — see the block comment above for why deletion,
	not same-width substitution, is required, and why that is still safe:
	callers must use the STRING THIS RETURNS consistently for both matching
	and any position-based re-inspection of that same match, never mix its
	offsets with the original line's.
	"""
	return _ESCAPED_SEPARATOR_RE.sub("", line)


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
# That round-2 fix bounded the over-correction to NUMERIC continuations only
# (`(?!\.\d)`), which left a DOTTED-EXTENSION continuation open: a `.` followed
# by letters still ended the token, so a four-part version immediately followed
# by a file extension newly matched as a bare quad, invisible only when the
# filename happened to carry a `-`/`_`-joined product prefix (issue #113). A
# subsequent revision (PR #360 round 1) tried to close that with a fourth
# lookahead rejecting a `.`+short-alpha run, but that opened a FALSE NEGATIVE:
# a version quad and an IPv4 literal are byte-for-byte identical, so the same
# lookahead suppressed a REAL, non-doc address glued to an extension in the
# same position, walking it through the hard gate. Per CLAUDE.md that is the
# one unacceptable direction, so #113 falls back to its own documented Option
# B: NO syntactic extension guard here. A keyless four-part version glued to an
# extension is now a disclosed false POSITIVE (a spurious CI fail, renamed
# away), pinned in VersionExtensionTests and docs/testing.md; the only
# structural exemption that survives is a PRECEDING version key, applied
# downstream by is_version_string(), not here. Do NOT reintroduce a lookahead
# that encodes an extension list — that shape has failed open before. The
# five-part-version case stays handled by `(?!\.\d)` exactly as before.
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
# NOTE (#113, revised after PR #360 round-1 review): there is deliberately NO
# extension-suppressing lookahead here. An earlier revision added a fourth
# lookahead of the shape `(?!\.[A-Za-z]{1,8}...)` so a bare version quad glued
# to a file extension would not match as an address; it was removed because it
# opened a FALSE NEGATIVE on the hard secret gate. See the block above IPV4_RE
# and docs/testing.md for the full rationale; the short version is that a
# version quad and an IPv4 literal are byte-for-byte identical, so any such
# lookahead also suppresses a real, non-doc address glued to an extension. The
# only surviving version exemption is a PRECEDING version key, applied by
# is_version_string() below — never a trailing-extension shape.
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
# quad, with nothing between them but optional whitespace, a REQUIRED
# colon/equals, and an optional opening quote.
#
# The separator is mandatory (issue #361): a version FIELD is written
# "version: A.B.C.D" or "version=A.B.C.D" — a `:`/`=` is what marks the word
# "version" as a key rather than prose. Without that requirement, a bare
# "version A.B.C.D" with only whitespace between them waived a real routable
# IPv4 literal that merely followed the word "version" in a sentence (e.g.
# "the appliance version <real address> host"), which is a false negative on
# the load-bearing secret gate — the unacceptable direction per CLAUDE.md.
# `\b` bounds the word, so "mgmt_version" (underscore is a word character)
# does NOT suppress, and neither does a backtick before the quad. (Placeholders,
# not literals: this comment keeps the no-dotted-quad discipline stated above,
# and the concrete spellings are pinned in
# test_separator_is_required_as_documented / test_bare_version_word_no_longer_
# waives_a_real_ip.)
#
# What stays excluded is the thing that matters: a mention of "version" ELSEWHERE
# on the line does not suppress, because \Z anchors the match to the text
# immediately preceding the quad. Otherwise a sentence that merely said "the
# vCenter version at the site is" ahead of a real lab address would waive a real
# leak. Immediate context or nothing — that bound, together with the now-mandatory
# separator, is what keeps this from becoming a line-wide bypass, and it is what
# the tests pin.
VERSION_KEY_BEFORE_RE = re.compile(r"(?i)\bversions?\b\s*[:=]\s*['\"]?\Z")


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


# The three canonical RFC 1918 whole-space CIDR literals, and ONLY as base
# address + exact canonical prefix (the ten-slash-eight, the seventeen-two-
# slash-twelve, and the one-nine-two-one-six-eight-slash-sixteen). A whole-
# space range names every private network in existence and can identify no
# host and no lab; the base quad alone, any other host, or any narrower
# prefix stays a finding. Narrow contextual exception for issue #61's
# forwarded-headers defaults, not a range widening. Assembled from octets so
# this file's own scan stays clean.
_PRIVATE_SPACE_CIDRS = {
	".".join(map(str, octets)): f"/{prefix}"
	for octets, prefix in (
		((10, 0, 0, 0), 8),
		((172, 16, 0, 0), 12),
		((192, 168, 0, 0), 16),
	)
}


def _is_private_space_cidr(line: str, candidate: str, end: int) -> bool:
	suffix = _PRIVATE_SPACE_CIDRS.get(candidate)
	if suffix is None or line[end:end + len(suffix)] != suffix:
		return False
	# The prefix must END there: "/8" followed by another digit is /81 or /800,
	# not the canonical whole-space prefix (PR #190 round 1, finding 4).
	after = end + len(suffix)
	return after >= len(line) or not line[after].isdigit()


# Issue #191: the deploy/compose.yaml `edge` network's pinned subnet,
# and ONLY as this exact base address + exact /24 prefix. This is NOT a range
# widening of _PRIVATE_SPACE_CIDRS above -- it names one specific /24 this
# repo's own compose file assigns to a bridge network, the same way a fixture
# IP would be sanctioned, not "every private network" the whole-space
# exception above covers. The base quad alone, any other host in the range,
# or any other prefix on this base still stays a finding.
_PINNED_EDGE_SUBNET_BASE = ".".join(map(str, (192, 168, 240, 0)))
_PINNED_EDGE_SUBNET_SUFFIX = "/24"


def _is_pinned_edge_subnet_cidr(line: str, candidate: str, end: int) -> bool:
	if candidate != _PINNED_EDGE_SUBNET_BASE:
		return False
	if line[end:end + len(_PINNED_EDGE_SUBNET_SUFFIX)] != _PINNED_EDGE_SUBNET_SUFFIX:
		return False
	# Same "/24 followed by another digit is /240+" guard as
	# _is_private_space_cidr above (PR #190 round 1, finding 4).
	after = end + len(_PINNED_EDGE_SUBNET_SUFFIX)
	return after >= len(line) or not line[after].isdigit()


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

# This repo's closed-set credential *type* strings (the RHS of the well-known
# type constants in Waypoint.Core/Secrets/CredentialTypes-style declarations —
# `DepotActivationCode = "depot-activation-code"`, etc.). These are fixed
# domain vocabulary that this codebase names in source on purpose; they are the
# NAME of a credential kind, never a credential's secret value. The
# depot-keyword regex necessarily fires on such a declaration (the keyword
# "activation-code" sits next to a quoted `"depot-activation-code"`), so those
# lines are structural false positives.
#
# Kept as an EXACT closed set, not a substring/prefix rule: a real leak whose
# value merely CONTAINED one of these words (e.g. a token that happened to end
# in "-depot-token") must still fail. Membership is exact-match only, so the
# suppression cannot be widened by an attacker appending real secret material.
KNOWN_CREDENTIAL_TYPE_LITERALS = frozenset({
	"depot-token",
	"depot-activation-code",
	"legacy-download-token",
	"entitlement-id",
	"support-contract",
	"broadcom-token",
})

# A bare program identifier: one unbroken run of ASCII letters with NO digits
# and NONE of the `+ / = _ -` separators that base64/opaque-token material
# carries. `useDepotActivationCode`, `UseDepotTokenResult` and similar
# camelCase/PascalCase names match; a real depot activation code or download
# token does not, because Broadcom's are alphanumeric blobs that carry digits
# (and often `-`/`=`) for entropy — the property `_looks_like_code_identifier`
# leans on below and the negative tests pin.
_CODE_IDENTIFIER_RE = re.compile(r"^[A-Za-z]+$")


def _looks_like_code_identifier(value: str) -> bool:
	"""True if `value` is a multi-word camelCase/PascalCase code identifier.

	NARROW BY CONSTRUCTION — this is a security gate and must not be blunted:

	  * all-alphabetic only (`_CODE_IDENTIFIER_RE`): a digit or any of
	    `+ / = _ -` in the value takes it straight back to the flagged path,
	    which is why a genuine `activationCode=<real-looking-secret>` (real
	    tokens carry digits/symbols) STILL fails. Pinned by
	    test_a_real_looking_secret_after_the_keyword_still_fails.
	  * a lower->upper case transition somewhere inside it, so the value is a
	    concatenation of words (a code identifier) rather than a single opaque
	    lowercase or uppercase run. A single word is left to the flagged path.

	It does NOT look at length or entropy — those are fuzzy thresholds a real
	secret can be tuned to slip under. The structural "no digits, no token
	separators, camelCase words" shape is what distinguishes a source-level
	identifier reference from secret material.
	"""
	if not _CODE_IDENTIFIER_RE.match(value):
		return False
	return any(a.islower() and b.isupper() for a, b in zip(value, value[1:]))


def is_placeholder_token(value: str) -> bool:
	lowered = value.lower()
	if lowered in KNOWN_CREDENTIAL_TYPE_LITERALS:
		# The keyword-adjacent "value" is one of this repo's own credential-TYPE
		# strings, i.e. the name of a credential kind declared in source, not a
		# secret. See KNOWN_CREDENTIAL_TYPE_LITERALS.
		return True
	if _looks_like_code_identifier(value):
		# A bare camelCase/PascalCase identifier (a variable/type/hook name),
		# not secret material. See _looks_like_code_identifier for why this
		# cannot swallow a real token.
		return True
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
# _ipv6_address_of(), and that is checked rather than assumed: the retry only
# ever REMOVES trailing groups (up to `_MAX_SWALLOWED_GROUPS` of them), which
# takes a 3-group timestamp to nothing and a 6-group MAC to 3 — further from
# the 8 an uncompressed literal needs, never closer. What the retry does reach
# is a NINE-, TEN- or ELEVEN-group run whose trailing groups are all digits,
# which is the address-plus-port shape it exists for; the corresponding
# false-positive question ("what else is nine to eleven colon-separated hex
# groups?") is enumerated in FalsePositiveCorpusTests rather than dismissed,
# and bounding the loop is what keeps that question answerable at all — see
# `_MAX_SWALLOWED_GROUPS`.
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
#
# `mapped_quad`'s per-part digit count is unbounded (`\d+`, not `\d{1,3}`),
# the same #119 lesson IPV4_RE's own per-part count already learned, applied
# here for issue #123. A bounded part cap put the padding question in two
# places at once for the plain-IPv4 detector (the regex AND
# _parse_ipv4_octets), and it did the same here for the mapped form's
# embedded quad: a 4-digit-padded octet never even produced a `mapped_quad`
# match (the optional group backtracked out entirely), so the candidate fell
# back to the bare hex/colon prefix and the finding named a truncated
# fragment rather than the address — or, with only three digits of padding,
# parsed as an ordinary-looking dotted quad but still failed strict IPv6
# parsing (leading zeros) and read as "not an address, so allowed", losing
# the IPv6 finding while the IPv4 detector still caught the embedded quad on
# its own. `_normalize_ipv4_mapped_tail()` is the single place that then
# decides which digit strings denote a real octet, via the same
# `_parse_ipv4_octets()` the plain IPv4 detector uses — exactly the "answered
# in exactly one place" structure #119's own comment on IPV4_RE describes,
# extended across both address families instead of re-derived for this one.
IPV6_RE = re.compile(
	r"(?<![A-Za-z0-9])"
	r"(?:"
	r"\[(?P<bracketed>[0-9A-Fa-f:]+(?P<bracket_zone>%[\w.-]+)?)\]"
	r"(?P<bracket_port>:\d+)?"
	r"|"
	r"(?P<bare>[0-9A-Fa-f:]+(?P<mapped_quad>(?:\.\d+){1,3})?"
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


# How many trailing all-digit groups _ipv6_address_of() will strip before it
# gives up (PR #138 round 1, finding 1).
#
# WHY THERE IS A BOUND AT ALL: disclosability. UNBOUNDED, this loop flags ANY
# colon-separated run whose first eight groups parse and whose every remaining
# group is all-digit — so the disclosed #118 false-positive class becomes
# unbounded in RECORD LENGTH, not merely a group or two wider. That is a class
# no test can enumerate and no sentence in docs/testing.md can state truthfully,
# which is exactly how three separate places in this repo came to assert a
# bound of "exactly one group" that the loop no longer had. A pin named
# `..._are_still_only_these` cannot bound what it claims to bound unless the
# class is finite. Bounding is what makes an accurate disclosure possible; the
# false-positive count is a consequence, not the argument.
#
# WHY THREE, AND WHAT IT COSTS. Three is NOT free relative to two, and an
# earlier revision of this comment implied it was. Measured against a corpus
# with even coverage from 9 to 13 colon groups (the first corpus had a gap at
# exactly 11, which made two and three look tied):
#
#   cap 2 -> 4 of 10 leak shapes missed, 10 of 22 false positives
#   cap 3 -> 3 of 10 leak shapes missed, 13 of 22 false positives
#
# So three buys ONE further leak shape (a literal with three trailing numeric
# groups, which no producer in this gate's threat model — netstat, log lines,
# inventory exports, CKL/HDF, URLs — has ever been shown to emit) and costs
# THREE further false-positive families, all of them 11-group records: EUI-64
# with three trailing numeric fields, a certificate fingerprint with the same,
# and an 11-field numeric counter record. It is a deliberate trade in favour of
# the leak direction, not a dominant choice: a missed lab address is an
# incident under CLAUDE.md, a false positive is a visibly red gate. Three also
# keeps closed the three-group shape PR #138's round-1 reviewer independently
# verified as closed.
#
# DOWNWARD, one is where the retry already was, and one is exactly what #131
# was filed about, so the bound cannot be lower than two; two is rejected above
# on the leak direction.
#
# What is deliberately NOT bounded is any group's WIDTH — a 30-digit trailing
# group still resolves. Issue #119's lesson was against arbitrary width bounds
# (digit counts), and it is untouched here; a group-COUNT bound is a different
# axis, and it is the axis that makes the residual class finite.
#
# The residual this creates is disclosed and pinned, not silent:
# MultiGroupPortRetryTests.test_four_trailing_all_digit_groups_are_a_
# disclosed_residual, and the false-positive side is enumerated in
# FalsePositiveCorpusTests.
_MAX_SWALLOWED_GROUPS = 3


def _parse_ipv6(text: str) -> ipaddress.IPv6Address | None:
	"""Strict parse, or None. `ipaddress` is the arbiter; this just softens it."""
	try:
		return ipaddress.IPv6Address(text)
	except ValueError:
		return None


# A trailing dotted-quad, unbounded per part like IPV6_RE's own `mapped_quad`
# group above — deliberately the same shape, matched independently here
# because this runs on the CANDIDATE STRING (already split off any zone id),
# not on the original line, and needs its own anchor at the end of that
# string (`\Z`) rather than IPV6_RE's line-position lookarounds.
_MAPPED_QUAD_TAIL_RE = re.compile(r"(?:\d+\.){3}\d+\Z")


def _normalize_ipv4_mapped_tail(text: str) -> str:
	"""Strip zero-padding from a trailing IPv4-mapped dotted quad, if present.

	Same root cause as issue #119 for the plain IPv4 detector, in the other
	address family (issue #123): `ipaddress.IPv6Address` rejects leading
	zeros in the embedded quad of the IPv4-mapped form (a zero-padded octet
	after the `::ffff:` prefix, e.g. `010` instead of `10`) as ambiguous
	octal notation. This scanner reads its own source like any other tracked
	file, so this comment names the shape rather than spelling out a padded
	mapped literal — the same no-address-shaped-literal discipline every
	other detector comment in this file already follows. A
	padded-but-otherwise-ordinary literal then reads as "does not parse, so
	not an address" — the IPv6 finding is lost while the IPv4 detector still
	catches the embedded quad on its own,
	so the line is under-reported rather than silent (PR #115 did not carry
	the #119 treatment across when it added this address family).

	Delegates the actual digit-validity question to `_parse_ipv4_octets` —
	the SAME function the plain IPv4 detector uses — so "which digit strings
	denote a real octet" is answered in exactly one place for both address
	families, at any padding width (`_parse_ipv4_octets` already dropped its
	own 3-digit cap for #119). If the tail is not a valid dotted-quad at all
	(an octet over 255, or something that merely looks dotted), this returns
	`text` unchanged and lets the normal strict-parse/retry path in
	`_ipv6_address_of` decide — it never invents a quad that was not there.
	"""
	match = _MAPPED_QUAD_TAIL_RE.search(text)
	if match is None:
		return text
	octets = _parse_ipv4_octets(match.group(0))
	if octets is None:
		return text
	normalized_tail = ".".join(str(octet) for octet in octets)
	return text[: match.start()] + normalized_tail


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
	round 2 closed for the one-group case, one group further out.

	The loop is BOUNDED, at `_MAX_SWALLOWED_GROUPS` (three) — see that
	constant for the argument and the measured cost. In short: unbounded,
	the disclosed #118 false-positive class becomes unbounded in record
	length, which is a class no test can enumerate and no disclosure can
	state truthfully. Three rather than two is a deliberate trade in favour
	of the leak direction, priced there rather than asserted free.

	The axis that is bounded is the NUMBER of trailing all-digit groups
	stripped, never how WIDE any one of them is. Each iteration removes
	exactly one whole group and re-parses; the port's digit count inside a
	group stays deliberately unbounded, for the same #119 reason as before —
	an arbitrary width bound is what that issue cost, and bounding the group
	WIDTH here would be that mistake with a new coat of paint. A 30-digit
	trailing group still resolves (UnbracketedPortTests pins the widths).

	The retry still cannot manufacture an address out of a non-address: every
	iteration hands the shortened text to the same strict parser, so what
	finally comes back is a real literal or nothing
	(test_the_port_retry_only_ever_returns_what_the_parser_accepts).

	Two residuals, both disclosed and pinned rather than left for the next
	reviewer to re-discover — they are the loop's two stopping conditions:

	  - a trailing group carrying a non-digit character glued on
	    (`...:0007:443a`) fails `tail.isdigit()` on its very first iteration,
	    so the loop never starts stripping it. A materially different shape:
	    the group is not a port at all, digit or otherwise. Pinned by
	    MultiGroupPortRetryTests.test_a_glued_letter_on_the_final_group_
	    remains_undetected.
	  - FOUR or more trailing all-digit groups (`...:0007:1:2:3:4`) exhaust
	    the bound. Pinned by MultiGroupPortRetryTests.test_four_trailing_
	    all_digit_groups_are_a_disclosed_residual.

	Both are in docs/testing.md as well.

	One more normalization happens before any of the above: a trailing
	IPv4-mapped dotted quad has its zero-padding stripped first
	(`_normalize_ipv4_mapped_tail`, issue #123), so a padded mapped literal
	parses on the FIRST attempt rather than being misread by the port retry
	below as a run of trailing numeric groups to strip. Padding never LOOKS
	like a port — it is dots, not colons — but stripping it first is still
	the right order: `_parse_ipv4_octets` is the single place both address
	families already agree padding width is not the question, so asking it
	before the retry loop even starts is more direct than allowing the loop
	to eventually strip its way past a group boundary that was never a port.
	"""
	base = _normalize_ipv4_mapped_tail(candidate.split("%", 1)[0])
	addr = _parse_ipv6(base)
	if addr is not None:
		return addr
	head = base
	for _attempt in range(_MAX_SWALLOWED_GROUPS):
		head, separator, tail = head.rpartition(":")
		if not separator or not tail.isdigit():
			return None
		addr = _parse_ipv6(head)
		if addr is not None:
			return addr
	return None


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


def _strict_ipv6_literal(candidate: str) -> ipaddress.IPv6Address | None:
	"""Strict parse after stripping a zone id, but WITHOUT the port retry.

	Used only by _widest_address_start() (issue #133). `_ipv6_address_of()`'s
	contract is deliberately loose — "does this span DENOTE an address,
	possibly with a port swallowed" — which is exactly right for judging the
	final candidate scan_text reports. It is the wrong question for
	re-anchoring: there the candidate spans are competing to BE the address,
	and a span that only parses by discarding one of its own trailing groups
	through the port retry is not really the address, it just contains one
	glued to something else. Calling `_ipv6_address_of()` here let widest-wins
	pick a span one hostname character too wide whenever the character right
	before the real address happened to complete a digit group the retry
	could then drop — `node99:<address>` re-anchored onto `de99:<address>`,
	naming a token that never appears on the line, because `de99:<the address
	minus its own last group>` parsed once the retry stripped that last group
	as if it were a port.

	A zone id is still stripped here, same as `_ipv6_address_of()` — that
	strip is unconditionally safe (it only ever removes a suffix `ipaddress`
	was never going to accept anyway) and has nothing to do with the port
	retry this function exists to keep out of re-anchoring. The same is true
	of the mapped-quad padding normalization (issue #123): it only changes how
	an already-present trailing dotted quad is spelled, never which characters
	are part of the candidate, so it carries no port-retry-style risk of
	widening a span onto characters that were not already in it.
	"""
	return _parse_ipv6(_normalize_ipv4_mapped_tail(candidate.split("%", 1)[0]))


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
	WIDEST one that STRICTLY parses as an address (`_strict_ipv6_literal()`,
	not `_ipv6_address_of()` — issue #133) wins. The fragment the engine
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

	This is precision, not correctness: judging candidates strictly changes
	only WHICH SPAN wins widest, never whether the line is flagged.
	`_is_ipv6_finding()` — which does use the port-retry-enabled
	`_ipv6_address_of()` — is still what scan_text calls on the final
	candidate this function returns, so an address that genuinely needs the
	port retry to be recognised still is; it just cannot win widest by
	borrowing part of a real address to do it.
	"""
	for start in range(run_start, match_start):
		if _strict_ipv6_literal(_trim_delimiter_colons(line[start:end])) is not None:
			return start
	return match_start


def _hex_run_start(line: str, match_start: int) -> int:
	"""Index at which the maximal hex/colon run containing `match_start` begins."""
	start = match_start
	while start > 0 and line[start - 1] in _HEX_OR_COLON:
		start -= 1
	return start


def _is_hex_lettered_identifier_shape(candidate: str) -> bool:
	"""True if this candidate has no digit anywhere (issue #118).

	A two-part identifier joined by `::`, where both parts are spelled
	entirely with hex letters (`a`-`f`), is syntactically indistinguishable
	from a real compressed IPv6 literal — `ipaddress.IPv6Address` cannot tell
	a `cafe`/`babe`-style placeholder identifier from an address, because
	there isn't a syntactic difference to tell. Every SANCTIONED spelling this
	scanner allows carries at least one digit (the RFC 3849 doc prefix starts
	`2001:`, loopback and unspecified are literally digits/nothing), so this
	cannot suppress an allowlisted address into a false negative — it only
	ever removes candidates from the reportable set, which by construction
	were already going to be either a false positive (this issue) or a real
	lab literal, and every real lab literal this repo's threat model produces
	(inventory exports, netstat, logs, CKL/HDF, URLs) is hex-and-DIGITS, not
	hex-letters-only prose. Deliberately checked on the WHOLE candidate, not
	per-group: a real address with one digit-free group and a digit elsewhere
	in a LATER group must still be caught, and is — this only fires when NO
	digit appears anywhere in the span. (Like the mapped-form comment above,
	this docstring names that shape in prose rather than spelling out a
	concrete digit-plus-hex-letters literal: this file is scanned like any
	other tracked file, and a hex-lettered group next to a digit-bearing one
	is itself address-shaped enough to trip this very check.)

	This is the same one-directional trade `_dash_glues_to_non_address` and
	the trailing-guard fixes above make explicit: it can only turn a
	would-be finding into a non-finding, never the reverse, so it cannot
	create a new false negative on top of what the regex already matched.
	IPv6DetectorTests pins that a real, digit-bearing literal — including one
	whose OTHER group is hex-letter-heavy — is unaffected.
	"""
	return not any(char.isdigit() for char in candidate)


def _is_ipv6_finding(candidate: str) -> bool:
	"""True if this candidate is a reportable IPv6 literal.

	The two-colon floor is a signal-to-noise floor, not a syntax rule: a SHA
	hash, a base64 blob, a CSS hex colour and a Windows drive path all carry
	at most one colon, so requiring two discards them before `ipaddress` is
	asked anything. A real literal always has at least two (the shortest
	spellings are `::` and `::1`).

	The digit floor (issue #118) is the same idea one level up: a candidate
	spelled entirely in hex LETTERS, with no digit anywhere, is more likely a
	word-shaped identifier that happens to validate than a lab address — see
	_is_hex_lettered_identifier_shape for why that can only shrink the
	reportable set, never hide a real leak.
	"""
	if candidate.count(":") < 2:
		return False
	if _is_hex_lettered_identifier_shape(candidate):
		return False
	return not is_allowed_ipv6(candidate)


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
	for lineno, raw_line in enumerate(text.splitlines(), start=1):
		# The three address detectors below read the ESCAPE-NORMALIZED line
		# (see _unescape_separators), so a backslash immediately before `.`
		# or `:` cannot fragment a candidate below each detector's structural
		# floor (issue #137). This line is used consistently for matching
		# AND for every position-based helper call in this block — never
		# mixed with `raw_line`'s offsets, which belong to a different-length
		# string once a backslash has been dropped. The depot-token check
		# below is unaffected by this issue (an opaque token has no
		# structural separator to fragment) and deliberately keeps reading
		# `raw_line`, narrowing this change to the checks the issue names.
		line = _unescape_separators(raw_line)
		if CHECK_IP not in waived:
			for match in IPV4_RE.finditer(line):
				candidate = match.group(0)
				if _dash_glues_to_non_address(line, match.start(), match.end()):
					continue
				if is_allowed_ip(candidate):
					continue
				if is_version_string(line, match.start()):
					continue
				if _is_private_space_cidr(line, candidate, match.end()):
					continue
				if _is_pinned_edge_subnet_cidr(line, candidate, match.end()):
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
			for match in DEPOT_CONTEXT_RE.finditer(raw_line):
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
	"""Scan one tracked file's text for the four detectors above.

	Callers are expected to have already routed anything matching
	`KNOWN_SAFE_BINARY_EXTENSIONS` away from this function (see
	`_find_uninspectable_tracked_files` and `main`) — this only decides
	whether the given path's *content* can be read as UTF-8 text, not
	whether its extension is trusted. A file that still fails to decode here
	(a genuinely-unexpected binary that happens to carry an extension not on
	either list, e.g. a mislabeled `.txt`) is skipped rather than crashing
	the run; `main()`'s uninspectable-extension check is the loud half of
	this contract, this is the quiet fallback for content that surprises the
	extension it was filed under.
	"""
	if rel is None:
		rel = path.relative_to(REPO_ROOT).as_posix()
	try:
		text = path.read_text(encoding="utf-8")
	except (UnicodeDecodeError, OSError):
		return []
	return scan_text(rel, text)


# Extensions that name a format this scanner refuses outright, regardless of
# whether a given instance happens to decode as UTF-8. A PEM certificate or an
# ASCII-armored key IS valid UTF-8 text — the reason a naive "can this be read
# as text" test is the wrong question for this bucket. What makes `.pem` and
# friends dangerous is not that they are binary, it is that they are a format
# whose *legitimate* content (a Subject/SAN, an archive member's path/text) is
# exactly the kind of thing CLAUDE.md forbids, and this scanner's four
# detectors were never built to parse that structure. So this list is named by
# format, not discovered by a decode attempt: certs/keys and archives, the
# sharpest cases from issue #101.
UNINSPECTABLE_EXTENSIONS = {
	".pem", ".key", ".pfx", ".p12",  # certs/keys — a Subject/SAN carries a
	# hostname in plain ASCII; gitleaks covers key MATERIAL, not this.
	".zip", ".gz", ".tgz", ".pdf",  # archives and PDF — contents/text this
	# scanner has no parser for, at all.
}


def _find_uninspectable_tracked_files(paths: list[Path]) -> list[str]:
	"""Repo-relative paths of tracked files this scanner refuses to pass clean.

	Issue #101: a tracked file matching UNINSPECTABLE_EXTENSIONS (a named,
	closed list of cert/key/archive formats — see that constant) is refused
	outright, never scanned and never silently passed. `main()` fails the run
	and a human must either remove the file or, if it is a deliberately-kept
	format this scanner should learn to trust, extend the relevant allowlist
	with a reason — never widen this by dropping an extension silently.

	Anything else that fails to decode as UTF-8 — a format nobody has named
	either safe (KNOWN_SAFE_BINARY_EXTENSIONS) or forbidden
	(UNINSPECTABLE_EXTENSIONS) — is refused too, on the same "when in doubt,
	leave it out" principle: an unrecognised binary blob is exactly the
	shape check-no-external-assets.mjs's header comment describes failing
	open on five separate times when it was allowed to fall through a gap
	between two enumerated lists. There is no such gap here: recognised-safe,
	recognised-forbidden, or undecodable are the only three outcomes, and
	only the first one scans clean without a finding.
	"""
	uninspectable: list[str] = []
	for path in paths:
		if not path.is_file():
			continue
		ext = path.suffix.lower()
		if ext in KNOWN_SAFE_BINARY_EXTENSIONS:
			continue
		if ext in UNINSPECTABLE_EXTENSIONS or _looks_binary(path):
			uninspectable.append(path.relative_to(REPO_ROOT).as_posix())
	return sorted(uninspectable)


def _looks_binary(path: Path) -> bool:
	"""True if `path` cannot be read as UTF-8 text.

	This is the fallback dividing line for anything not named by either
	extension list: a plain-text file with an unfamiliar extension (a new
	docs format, say) still gets scanned normally, because it decodes fine.
	Only content that genuinely cannot be read as text — or a format named
	in UNINSPECTABLE_EXTENSIONS regardless of whether it happens to decode —
	is escalated to the loud, fail-the-run path.
	"""
	try:
		path.read_text(encoding="utf-8")
	except (UnicodeDecodeError, OSError):
		return True
	return False


def main() -> int:
	_validate_allowlist()
	tracked = list_tracked_files()

	uninspectable = _find_uninspectable_tracked_files(tracked)
	if uninspectable:
		print(
			"Repo-specific sanitization scan refuses to pass the following "
			"file(s) clean:\n"
		)
		for rel in uninspectable:
			print(f"  - {rel}")
		print(
			"\nEach of these cannot be read as UTF-8 text, so none of this "
			"scanner's detectors can inspect its content — and CLAUDE.md's "
			"sanitization mandate ('when in doubt, leave it out') means that is "
			"a reason to refuse the file, not to pass it clean. A certificate's "
			"Subject/SAN, a key file, or an archive's contents can all carry a "
			"lab hostname or a secret that gitleaks and this scanner's own "
			"text detectors would never see.\n\n"
			"If this file genuinely does not belong in the repo (a real cert, "
			"key, or exported artifact), remove it — CLAUDE.md forbids "
			"committing those regardless of what this scanner does. If it is a "
			"deliberately-invented, known-safe binary asset (an app icon, a "
			"font, a wasm build artifact), add its extension to "
			"KNOWN_SAFE_BINARY_EXTENSIONS in "
			".github/sanitize/scan_repo_specific.py with a one-line reason — "
			"that is a reviewable, deliberate act, not a silent exemption."
		)
		return 1

	all_findings: list[str] = []
	for path in tracked:
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
