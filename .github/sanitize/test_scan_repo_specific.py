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

import ipaddress
import re
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


def zero_pad_quad(dotted_quad: str, width: int = 3) -> str:
	"""Zero-pad each octet of a dotted-quad to `width` digits.

	Assembled at runtime (never written as a padded literal) for the same
	reason every other address fixture in this file is: the padded form is
	exactly as address-shaped as the unpadded one, so writing it directly
	into the source would trip the very check being tested here.

	`width` is a parameter because the padding WIDTH was the bug in issue
	#119: the fix for #111 handled 3 and stopped there.
	"""
	return ".".join(part.zfill(width) for part in dotted_quad.split("."))


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


def bracketed(addr: str) -> str:
	"""The `[addr]` URL form for an IPv6 literal, without a port."""
	return f"[{addr}]"


def bracketed_port(addr: str, port: int) -> str:
	"""The `[addr]:port` URL form for an IPv6 literal."""
	return f"{bracketed(addr)}:{port}"


def zoned(addr: str, interface: str = "eth0") -> str:
	"""An IPv6 literal carrying an RFC 4007 zone id."""
	return f"{addr}%{interface}"


def backslash_escape_separators(text: str) -> str:
	"""Insert a `\\` immediately before every `.` and `:` in `text`.

	Builds the issue #137 evasion shape (`10\\.44\\.12\\.7`,
	`host\\.name\\.dom`, `fd00\\:1a2b\\:\\:7`) from an already-assembled
	address/hostname fixture, so the escaped form is never itself a literal
	in this file — same discipline as every other fixture builder here.
	"""
	return text.replace(".", "\\.").replace(":", "\\:")


def with_port(addr: str, port: int) -> str:
	"""An UNBRACKETED `address:port` pair — the shape a log line writes.

	Deliberately its own builder rather than an f-string at each call site:
	this is the shape that scanned clean for a whole review round (PR #115
	round 2, finding 1) because the greedy hex/colon class swallowed the port
	into the candidate and strict parsing then failed.
	"""
	return f"{addr}:{port}"


# The IPv4-mapped form and a zone-carrying link-local. Assembled, never
# written: with the boundary guards fixed the mapped-form prefix is itself a
# valid non-exempt literal, and this file is scanned like any other.
LAB_IPV6_MAPPED = ipv6("", "", "ffff", LAB_IP)
LAB_IPV6_ZONED = zoned(LAB_IPV6_LINK_LOCAL)

# A hex run that is not an address by intent but validates as one anyway —
# the shape issue #118 owns. Eight colon-separated hex groups is a valid
# literal to `ipaddress`, whatever the author meant by them. Assembled like
# every other fixture here, because it IS address-shaped and this file is
# scanned like any other.
EUI64_SHAPED = ipv6("aa", "bb", "cc", "dd", "ee", "ff", "00", "11")


def _hextets(address: str) -> list[int]:
	"""The eight hextet values of an IPv6 literal, however it was spelled."""
	packed = ipaddress.IPv6Address(address).packed
	return [packed[i] * 256 + packed[i + 1] for i in range(0, 16, 2)]


def _parses(text: str) -> bool:
	"""True if the strict parser accepts this text as an IPv6 literal."""
	try:
		ipaddress.IPv6Address(text)
	except ValueError:
		return False
	return True

# The three IPv6 forms CLAUDE.md/issue #112 sanction: the RFC 3849
# documentation prefix, loopback, and the unspecified address.
OK_IPV6_DOC = ipv6("2001", "db8", "", "1")
OK_IPV6_LOOPBACK = ipv6("", "", "1")
OK_IPV6_UNSPECIFIED = ipv6("", "", "")  # three empty parts -> "::" (two colons)

# A zero-padded lab quad and an uppercase lab FQDN: the same address and the
# same hostname as LAB_IP / LAB_FQDN, written a different way. Both live at
# module scope because the delimiter matrix now enumerates spellings as well
# as shapes — see SurroundingContextTests.
LAB_IP_PADDED = zero_pad_quad(LAB_IP)
LAB_FQDN_UPPER = LAB_FQDN.upper()


# --- grammar introspection (see SurroundingContextTests) ------------------
#
# The delimiter matrix has to know how many fixture VARIANTS each detector
# owes, and that number must not be a hand-maintained constant — a
# hand-maintained one is what a reviewer reads as covered while it silently
# lags the detector. Both numbers below are derived from the detector itself.

try:  # Python 3.11+ renamed the stdlib regex parser.
	from re import _parser as _re_parser
except ImportError:  # pragma: no cover - older interpreters
	import sre_parse as _re_parser  # type: ignore[no-redef]


def _op_name(op: object) -> str:
	"""The parser opcode's name, across the 3.11 rename."""
	return getattr(op, "name", str(op))


def _has_structure(sequence) -> bool:
	"""True if this branch of the parse tree is more than plain literals."""
	return any(
		_op_name(op) in ("SUBPATTERN", "BRANCH") or "REPEAT" in _op_name(op)
		for op, _ in sequence
	)


def _named_groups(sequence, index_to_name: dict[int, str]) -> set[str]:
	found: set[str] = set()
	for op, argument in sequence:
		name = _op_name(op)
		if name == "SUBPATTERN":
			if argument[0] in index_to_name:
				found.add(index_to_name[argument[0]])
			found |= _named_groups(argument[3], index_to_name)
		elif name == "BRANCH":
			for alternative in argument[1]:
				found |= _named_groups(alternative, index_to_name)
		elif "REPEAT" in name:
			found |= _named_groups(argument[2], index_to_name)
	return found


def shape_group_names(pattern: re.Pattern[str]) -> set[str]:
	"""Named groups standing for a materially different shape this matches.

	Walks the compiled pattern's own parse tree and collects, as names:

	  - every alternative of an alternation, unless the alternation is
	    between plain literals (a vocabulary, not a shape);
	  - every group that may be absent.

	Anything inside a lookaround is skipped (it never contributes to the
	match), and so is anything nested inside a repetition, where "did it
	match" has no single answer. A construct in either of the first two
	categories that is NOT a named group raises here rather than returning
	quietly — that is the half of the contract which stops a new construct
	from being added invisibly.
	"""
	index_to_name = {index: name for name, index in pattern.groupindex.items()}
	names: set[str] = set()

	def visit(sequence, inside_repeat: bool) -> None:
		for op, argument in sequence:
			name = _op_name(op)
			if name in ("ASSERT", "ASSERT_NOT"):
				continue
			if name == "SUBPATTERN":
				visit(argument[3], inside_repeat)
			elif name == "BRANCH":
				alternatives = argument[1]
				if not inside_repeat and any(
					_has_structure(alternative) for alternative in alternatives
				):
					for alternative in alternatives:
						found = _named_groups(alternative, index_to_name)
						if not found:
							raise AssertionError(
								f"{pattern.pattern!r}: an alternation branch is "
								f"not identifiable — name its group so a "
								f"fixture can be required to exercise it"
							)
						names.update(found)
				for alternative in alternatives:
					visit(alternative, inside_repeat)
			elif "REPEAT" in name:
				minimum, _maximum, body = argument
				if minimum == 0 and not inside_repeat and _has_structure(body):
					if len(body) != 1 or _op_name(body[0][0]) != "SUBPATTERN":
						raise AssertionError(
							f"{pattern.pattern!r}: an optional construct is not "
							f"a single group; wrap and name it"
						)
					index = body[0][1][0]
					if index not in index_to_name:
						raise AssertionError(
							f"{pattern.pattern!r}: an optional group is "
							f"anonymous — name it so a fixture can be "
							f"required to exercise it"
						)
					names.add(index_to_name[index])
				visit(body, True)

	visit(_re_parser.parse(pattern.pattern, pattern.flags), False)
	return names


def _ipv4_spelling(fixture: str) -> tuple[str, str]:
	"""(as written, canonical) for the IPv4 address inside a fixture."""
	match = scanner.IPV4_RE.search(fixture)
	octets = scanner._parse_ipv4_octets(match.group(0))
	return match.group(0), ".".join(str(octet) for octet in octets)


def _ipv6_spelling(fixture: str) -> tuple[str, str]:
	"""(as written, canonical) for the IPv6 address inside a fixture."""
	match = scanner.IPV6_RE.search(fixture)
	written = match.group("bracketed") or match.group("bare")
	written = scanner._trim_delimiter_colons(written).split("%", 1)[0]
	return written, str(scanner._ipv6_address_of(written))


def _fqdn_spelling(fixture: str) -> tuple[str, str]:
	"""(as written, canonical) for the hostname inside a fixture.

	`is_allowed_fqdn` lowercases before deciding, so lower case is this
	detector's canonical spelling in exactly the sense `ipaddress` gives the
	address detectors theirs.
	"""
	written = scanner.FQDN_RE.search(fixture).group(0)
	return written, written.lower()


MATRIX_PATTERN = {
	scanner.CHECK_IP: scanner.IPV4_RE,
	scanner.CHECK_FQDN: scanner.FQDN_RE,
	scanner.CHECK_IPV6: scanner.IPV6_RE,
}

MATRIX_SPELLING = {
	scanner.CHECK_IP: _ipv4_spelling,
	scanner.CHECK_FQDN: _fqdn_spelling,
	scanner.CHECK_IPV6: _ipv6_spelling,
}


# --- guard-character derivation (see GuardCharacterDelimiterTests) --------
#
# Issue #132. `SurroundingContextTests.TRAILING`/`.LEADING` are a hand-written
# list of neighbour characters, and neither list contains a dash — so a
# detector's dash-adjacency behaviour, unlike every OTHER boundary property in
# this file, was never pinned. Demonstrated cost: adding a trailing "not a
# dash" lookahead to `IPV6_RE` takes an IPv6 range from two findings to one,
# with the whole 120-test suite green (the #111 defect, for the other address
# family, silently reopened).
#
# The fixture axis was made derived by #115 (`shape_group_names`, which reads
# a detector's OWN alternations and optional groups off its compiled parse
# tree). The delimiter axis gets the same treatment here: `guard_characters()`
# walks a pattern's own leading/trailing boundary lookarounds (the `ASSERT`/
# `ASSERT_NOT` nodes at the top of its parse tree — exactly the constructs the
# comments above `IPV4_RE`, `FQDN_RE` and `IPV6_RE` argue about at length) and
# reads off which punctuation characters those guards actually test for.
# Alphanumeric RANGES (`A-Za-z0-9`) are deliberately excluded: they mean "more
# of the same token", not "a candidate delimiter", and are already exercised
# by every non-boundary fixture in `DetectorPositiveTests`. A bare `\w`
# category contributes only `_` for the same reason — the alnum half of it is
# not a new delimiter, the underscore half is.
#
# What this buys, concretely: `IPV6_RE` carries no dash anywhere in its own
# guards (that absence is precisely #132's subject), so `guard_characters()`
# alone would never put a dash row on IPv6's own account. The completeness
# test below closes that loophole by checking coverage against the UNION of
# every detector's guard characters, not each detector's own — so IPv4's and
# FQDN's dash guards force a dash row onto IPv6 too, and IPv6's silence on
# that row is exactly what stays pinned as a live behaviour rather than an
# unexamined gap. Add a character to ANY detector's guard and the union grows;
# grow the union with no matching entry in `GUARD_LEADING_EXPECTED` /
# `GUARD_TRAILING_EXPECTED` and `test_every_guard_character_has_a_declared_row`
# fails — "a guard character with no row" is what that test exists to catch.
#
# What is NOT derived, and cannot be: WHETHER a given character suppresses or
# flags for a given detector is a property of the guard's VALUE, not just its
# presence, and has to be read off the code by running it — the same
# "measured, not assumed" discipline `MATRIX_SPELLING` and every comment in
# `scan_repo_specific.py` already applies. `GUARD_LEADING_EXPECTED` /
# `GUARD_TRAILING_EXPECTED` are that hand-authored, measured verdict; they are
# kept in a separate table from the DERIVED character set specifically so the
# derived and the declared halves are never blended into one list that reads
# as derived when it is not (the possible-fix note in #132 itself).

try:  # Same 3.11 rename shape_group_names already handles.
	from re import _parser as _re_parser  # noqa: F811 (re-imported for clarity)
except ImportError:  # pragma: no cover - older interpreters
	import sre_parse as _re_parser  # type: ignore[no-redef]  # noqa: F811


# Every parse-tree node kind that can hold a sub-sequence — i.e. that a guard,
# or a character a guard names, can hide inside. Pinned against the real
# patterns rather than assumed, from both directions:
#   - test_the_guard_walk_descends_every_container_the_detectors_use: no kind
#     present in the real detectors may be missing from this set;
#   - test_the_extractor_descends_every_container_kind_it_names: every kind IN
#     this set must actually be descended into, end to end, by both halves of
#     the derivation.
_GUARD_CONTAINER_OPS = frozenset(
	{
		"ASSERT",
		"ASSERT_NOT",
		"SUBPATTERN",
		"BRANCH",
		"MAX_REPEAT",
		"MIN_REPEAT",
		"ATOMIC_GROUP",
	}
)

# Atomic groups were added to Python's stdlib ``re`` parser in 3.11. This
# suite deliberately supports the repository's generic ``python3`` command,
# including 3.10, so a structural probe must cover only node kinds that the
# running parser can actually construct. On a runtime that supports atomic
# groups the entry remains in the shared descent set and the constructive rail
# below exercises it; on an older runtime it cannot be a skipped parse-tree
# node in the first place.
try:
	re.compile(r"(?>x)")
except re.error:
	_GUARD_CONTAINER_OPS -= frozenset({"ATOMIC_GROUP"})


def _guard_sub_sequences(name: str, argument) -> tuple:
	"""Every sub-sequence a container parse-tree node holds.

	The single descent rule, deliberately shared by BOTH halves of the
	derivation — `_collect_guards` (which assertions exist, and where) and
	`_guard_literal_chars` (which characters a given assertion body names).

	PR #138 round 2, finding 1: those two functions used to carry SEPARATE,
	hand-written node-kind lists, and only the first one was ever checked
	against the real parse trees. `_collect_guards` recursed into five
	container kinds; `_guard_literal_chars` recursed into `SUBPATTERN` alone.
	So a guard whose BODY was a repeat over a character class, or a branch
	with multi-character alternatives, named punctuation that nothing
	derived — the round-1 escape one level over, measured at the same
	141/141-green-while-three-findings-vanish. Two symmetric functions with
	asymmetric protection is the shape that produced it, so there is one
	descent rule now and one set of node kinds (`_GUARD_CONTAINER_OPS`),
	protected in both directions by the two tests named above it.

	Returning `()` for a non-container is the whole "not a container" answer:
	a kind that grows a sub-sequence in some future Python must be added
	here, and the constructive test above fails until it is.
	"""
	if name in ("ASSERT", "ASSERT_NOT"):
		return (argument[1],)
	if name == "SUBPATTERN":
		return (argument[3],)
	if name == "BRANCH":
		return tuple(argument[1])
	if name in ("MAX_REPEAT", "MIN_REPEAT"):
		return (argument[2],)
	if name == "ATOMIC_GROUP":
		return (argument,)
	return ()


def _is_word_category(category) -> bool:
	"""True for `\\w` only — not for `\\W`, which names the complement.

	The first spelling of this test was `"WORD" in str(category)`, which is
	also true of `CATEGORY_NOT_WORD` and so derived an underscore from a
	guard that by definition never matches one. Harmless (over-collection is
	the safe direction) but false, and a false derived character is exactly
	the kind of thing this file publishes sentences about.
	"""
	return str(category).endswith("CATEGORY_WORD")


def _punctuation_in_range(low: int, high: int) -> set[str]:
	"""The printable-ASCII punctuation a character-class RANGE names.

	A range is the third way a guard can name a delimiter, after a literal
	and a class of literals, and it was the third silent hole: `[!-/]` names
	a dot and a dash and derived nothing. Only the printable-ASCII slice is
	considered, so the answer is bounded by 95 code points however wide the
	range is written; alphanumerics and the underscore are excluded for the
	same reason a bare `\\w` contributes only the underscore — an alnum range
	is "more of the same token", not a delimiter candidate, which is why
	`[A-Za-z0-9]` (in IPV4_RE's and FQDN_RE's real guards) contributes
	nothing and this addition is a no-op against today's detectors.
	"""
	return {
		chr(code)
		for code in range(max(low, 0x20), min(high, 0x7E) + 1)
		if not chr(code).isalnum() and chr(code) != "_"
	}


def _guard_literal_chars(sequence) -> set[str]:
	"""Punctuation characters a single lookaround's own sub-pattern tests for.

	Reads a bare literal (`(?<!-)`), a character class possibly mixing
	literals and a `\\w` category (`(?!\\w)`, `[A-Za-z0-9]`), and a bare
	category — at any depth inside the guard body, via `_guard_sub_sequences`.
	`\\d` (CATEGORY_DIGIT) contributes nothing on its own — a bare digit is an
	alnum-range concept, not a punctuation delimiter — so only the literal
	half of a `\\.\\d`-shaped guard shows up here, which is exactly the `.`
	IPV4_RE's own comment says that guard exists to test.

	It descends rather than handling a fixed set of body shapes because the
	fixed set was a silent hole (PR #138 round 2, finding 1 — the argument is
	in `_guard_sub_sequences`). Two spellings measured on the tip that had
	it: a trailing guard over a class of hash, pipe and plus with a
	one-or-more quantifier (body = `MAX_REPEAT`), and one over a
	two-alternative branch with two nodes per alternative (body = `BRANCH`).
	Each left the suite at 141/141 while an address followed by each of those
	three characters went from one finding to none — the middle one an
	address in a markdown table cell.

	A subtlety worth keeping, because it is why only SOME branch spellings
	escaped: the parser hoists a common leading node out of a branch, so
	`(?!<class>\\w|<class>\\d)` parses as class-then-branch and was derived
	correctly even before the fix, while `(?!<class-a>\\w|<class-b>\\w)` was
	not. That is exactly why the protection below is per-node-kind and
	constructive rather than a list of remembered examples.

	Descending into a NESTED assertion's body over-collects on purpose: its
	characters land in the enclosing guard's direction as well as their own
	(`_collect_guards` visits the same node with the correct direction). A
	spurious declared row costs one measured line; a missing one costs a
	silent escape.
	"""
	chars: set[str] = set()
	for op, argument in sequence:
		name = _op_name(op)
		if name in ("LITERAL", "NOT_LITERAL"):
			chars.add(chr(argument))
		elif name == "IN":
			for sub_op, sub_argument in argument:
				sub_name = _op_name(sub_op)
				if sub_name == "LITERAL":
					chars.add(chr(sub_argument))
				elif sub_name == "CATEGORY" and _is_word_category(sub_argument):
					chars.add("_")
				elif sub_name == "RANGE":
					chars |= _punctuation_in_range(*sub_argument)
		elif name == "CATEGORY" and _is_word_category(argument):
			chars.add("_")
		for sub_sequence in _guard_sub_sequences(name, argument):
			chars |= _guard_literal_chars(sub_sequence)
	return chars


def _collect_guards(sequence, leading: set[str], trailing: set[str]) -> None:
	"""Accumulate guard characters from EVERY `ASSERT`/`ASSERT_NOT` in a
	sub-tree, however deeply it is nested.

	PR #138 round 1, finding 2: the first version of this derivation walked
	only the nodes at the TOP LEVEL of the pattern. That was a true statement
	about today's detectors and a false mechanism — a guard placed inside the
	IPv6 bare/bracketed alternation (or any other group) was invisible, so a
	character that no top-level guard mentions AND
	`SurroundingContextTests.TRAILING` does not list was covered by nothing at
	all. Measured cost of the top-level-only version: a trailing guard nested
	in the bare group rejecting three punctuation characters (hash, pipe,
	plus) left the whole suite green while three real findings disappeared —
	one of them an address in a markdown table cell, which this repo's docs
	produce constantly.

	So this recurses. The node kinds are a small closed set — the three
	patterns are built from `IN`, `LITERAL`, `MAX_REPEAT`, `SUBPATTERN`,
	`BRANCH` and the assertions themselves — and every kind that can CONTAIN
	a sub-sequence is descended into, including an assertion's own body (a
	guard inside a guard is still a guard). An unrecognised container would
	be a silent hole of exactly the kind this fix closes, so
	`_GUARD_CONTAINER_OPS` is checked against the real parse trees by
	`test_the_guard_walk_descends_every_container_the_detectors_use` rather
	than trusted.

	The descent itself lives in `_guard_sub_sequences`, shared with
	`_guard_literal_chars`, because keeping two copies of it is precisely how
	PR #138 round 2's finding happened: this walk was protected and its
	sibling was not.

	Direction comes from each assertion's own `direction` field at whatever
	depth it sits, so a nested lookbehind still counts as leading.

	This OVER-collects rather than under-collects on purpose: a lookaround
	that is not really a boundary guard (a per-character assertion inside a
	repeat, say) contributes a character that then REQUIRES a declared row.
	That is the safe direction — a spurious row costs one measured line in
	`GUARD_LEADING_EXPECTED`/`GUARD_TRAILING_EXPECTED`, while a missed one
	costs a silent escape, which is the defect the whole mechanism exists to
	prevent.
	"""
	for op, argument in sequence:
		name = _op_name(op)
		if name in ("ASSERT", "ASSERT_NOT"):
			direction, body = argument
			(leading if direction < 0 else trailing).update(_guard_literal_chars(body))
		for sub_sequence in _guard_sub_sequences(name, argument):
			_collect_guards(sub_sequence, leading, trailing)


def guard_characters(pattern: re.Pattern[str]) -> tuple[frozenset[str], frozenset[str]]:
	"""(leading, trailing) punctuation characters this pattern's own boundary
	guards mention, read from its compiled parse tree.

	Every `ASSERT`/`ASSERT_NOT` node in the tree counts, at any depth — not
	only the ones at the top level (PR #138 round 1, finding 2; the argument
	is in `_collect_guards`). A negative lookbehind (`direction < 0`) is
	leading; a negative lookahead is trailing.

	All three detectors happen to carry their guards at the top level today,
	so recursing changes none of the derived sets. Both halves of that are
	pinned: `test_recursion_leaves_todays_derived_sets_unchanged` (it is a
	no-op now) and `test_a_nested_guard_is_seen_by_the_derivation` (it would
	not be if a guard moved).
	"""
	parsed = _re_parser.parse(pattern.pattern, pattern.flags)
	leading: set[str] = set()
	trailing: set[str] = set()
	_collect_guards(parsed, leading, trailing)
	return frozenset(leading), frozenset(trailing)


GUARD_CHARS = {check: guard_characters(pattern) for check, pattern in MATRIX_PATTERN.items()}

GUARD_CHARS_LEADING_UNION: frozenset[str] = frozenset().union(
	*(leading for leading, _trailing in GUARD_CHARS.values())
)
GUARD_CHARS_TRAILING_UNION: frozenset[str] = frozenset().union(
	*(trailing for _leading, trailing in GUARD_CHARS.values())
)


class PrivateSpaceCidrTests(unittest.TestCase):
	"""The RFC 1918 whole-space CIDR literals (issue #61's forwarded-headers
	defaults) are allowed ONLY as base + exact canonical prefix; every nearby
	shape stays a finding. Candidates are assembled from octets so this test
	file's own scan stays clean."""

	_BASES = {
		".".join(map(str, octets)): prefix
		for octets, prefix in (
			((10, 0, 0, 0), 8),
			((172, 16, 0, 0), 12),
			((192, 168, 0, 0), 16),
		)
	}

	def test_whole_space_cidrs_are_allowed(self) -> None:
		for base, prefix in self._BASES.items():
			with self.subTest(base=base):
				self.assertEqual(
					[], scanner.scan_text("x", f'KnownNetworks: ["{base}/{prefix}"]'))

	def test_base_quad_without_its_prefix_is_still_flagged(self) -> None:
		for base in self._BASES:
			with self.subTest(base=base):
				self.assertTrue(scanner.scan_text("x", f"host {base} here"))

	def test_wrong_or_narrower_prefix_is_still_flagged(self) -> None:
		host = ".".join(map(str, (10, 0, 0, 5)))
		cases = [f"{base}/{prefix + 4}" for base, prefix in self._BASES.items()]
		cases.append(f"{host}/8")
		base10 = ".".join(map(str, (10, 0, 0, 0)))
		cases.append(f"{base10}/81")
		for bad in cases:
			with self.subTest(bad=bad):
				self.assertTrue(scanner.scan_text("x", f"net {bad}"))


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

	It generalises once more, and the second lesson cost a round of review too.
	This matrix originally hard-coded one method per detector, so a NEW
	detector joined the suite by writing new methods — or, in practice, by not
	writing them: `CHECK_IPV6` (issue #112) shipped with no delimiter case at
	all, and the very bug this class exists to catch was sitting in it, in the
	same file, under the same comment (PR #115 round 1). A shared matrix any
	detector can opt out of by omission is how that happens. So the matrix is
	driven by `MATRIX_FIXTURES` + `MATRIX_EXEMPT`, which between them must
	account for every name in `scanner.CHECK_NAMES` — opting out is still
	allowed, but only in writing, with a reason.

	AND IT GENERALISED A THIRD TIME, one level further in, for a third round.
	Every detector had exactly ONE fixture, so a green 14/14 row proved
	"these delimiters are safe for one shape of this address" while reading
	as if it proved more. It did not: `ipv6` passed "colon / port" only
	because its single fixture was a COMPRESSED address, whose port digits
	absorb as a legal eighth group. The fully-expanded spelling of the same
	address — which lived in this very file, and never met a port or this
	matrix — produced a ninth group, failed strict parsing, and scanned
	clean (PR #115 round 2, finding 1). That is the fixture monoculture this
	class was written about, committed by this class.

	So `MATRIX_FIXTURES` maps each check to SEVERAL named variants, and
	`test_every_grammar_shape_a_detector_admits_is_exercised` derives from the
	detector itself — its regex and its validator, not a hand-written list —
	how many variants it owes and fails when one is missing.
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

	# Fixture VARIANTS per detector, keyed by the detector's own check name so
	# this map stays comparable against scanner.CHECK_NAMES rather than
	# against a list of method names nobody can diff. Every variant is run
	# against every delimiter, so the cost of a variant is 24 more scans and
	# the benefit is a whole shape of address that can no longer pass on its
	# neighbour's behalf.
	#
	# Which variants are OWED is not a judgement call and not a list anyone
	# has to remember to extend — see
	# test_every_grammar_shape_a_detector_admits_is_exercised, which derives
	# it from each detector's regex (its optional and alternative constructs)
	# and from its validator (canonical vs non-canonical spelling of the same
	# value).
	MATRIX_FIXTURES = {
		scanner.CHECK_IP: {
			"dotted quad": LAB_IP,
			"zero-padded quad": LAB_IP_PADDED,
		},
		scanner.CHECK_FQDN: {
			"lab tld": LAB_FQDN,
			"uppercase": LAB_FQDN_UPPER,
		},
		scanner.CHECK_IPV6: {
			"compressed": LAB_IPV6,
			# The variant whose absence cost a review round: a compressed
			# address absorbs a trailing port as a legal eighth group, so
			# "colon / port" passed on it while the expanded spelling of the
			# same address produced a ninth group and scanned clean.
			"fully expanded": LAB_IPV6_FULL,
			"link-local compressed": LAB_IPV6_LINK_LOCAL,
			"ipv4-mapped": LAB_IPV6_MAPPED,
			"zone id": LAB_IPV6_ZONED,
			"bracketed": bracketed(LAB_IPV6),
			"bracketed with port": bracketed_port(LAB_IPV6, 443),
			"bracketed with zone id": bracketed_port(LAB_IPV6_ZONED, 443),
		},
	}

	# Which finding message belongs to which detector. The matrix asserts
	# "exactly one finding FROM THIS DETECTOR" rather than "exactly one
	# finding on the line", because the IPv4-mapped variant legitimately
	# trips the IPv4 detector as well and an overlap is not a delimiter bug.
	MATRIX_MESSAGE = {
		scanner.CHECK_IP: "non-RFC-5737 IP address literal",
		scanner.CHECK_FQDN: "lab-style FQDN",
		scanner.CHECK_IPV6: "possible IPv6 address literal",
	}

	# Opting a detector out is legitimate, but it has to be stated and
	# argued here rather than achieved by writing no test.
	MATRIX_EXEMPT = {
		scanner.CHECK_DEPOT_TOKEN: (
			"not a bare token — the detector matches a keyword, a separator "
			"and a value as one unit, so 'what character precedes the "
			"match' is not a property it has. Its context handling is "
			"enumerated instead by test_every_depot_keyword_variant_fires "
			"and test_depot_keyword_is_case_insensitive."
		),
	}

	def test_every_detector_is_in_the_matrix_or_explicitly_exempt(self) -> None:
		"""No detector joins the suite by silently skipping this matrix.

		This is the guard that was missing when the IPv6 detector landed: the
		matrix existed, it was simply never extended, and nothing failed.
		"""
		covered = set(self.MATRIX_FIXTURES) | set(self.MATRIX_EXEMPT)
		self.assertEqual(
			covered,
			set(scanner.CHECK_NAMES),
			"every check in scanner.CHECK_NAMES must appear in "
			"MATRIX_FIXTURES (enumerated against every delimiter) or in "
			"MATRIX_EXEMPT (with a written reason)",
		)
		self.assertEqual(
			set(self.MATRIX_FIXTURES) & set(self.MATRIX_EXEMPT),
			set(),
			"a check cannot be both enumerated and exempt",
		)
		for check, reason in self.MATRIX_EXEMPT.items():
			with self.subTest(check=check):
				self.assertTrue(reason.strip(), check)
		self.assertEqual(
			set(self.MATRIX_MESSAGE),
			set(self.MATRIX_FIXTURES),
			"every enumerated check needs its finding message declared, or "
			"the matrix cannot tell that detector's findings from another's",
		)
		for check, variants in self.MATRIX_FIXTURES.items():
			with self.subTest(check=check):
				self.assertTrue(variants, f"{check} has no fixture variants")

	def findings_for(self, check: str, text: str) -> list[str]:
		"""Only `check`'s findings on `text` — see MATRIX_MESSAGE."""
		message = self.MATRIX_MESSAGE[check]
		return [f for f in scanner.scan_text("f.md", text) if message in f]

	def test_every_detector_is_flagged_after_every_trailing_delimiter(self) -> None:
		for check, variants in sorted(self.MATRIX_FIXTURES.items()):
			for variant, fixture in sorted(variants.items()):
				for name, suffix in self.TRAILING:
					with self.subTest(check=check, variant=variant, delimiter=name):
						findings = self.findings_for(check, f"host {fixture}{suffix}")
						self.assertEqual(
							len(findings), 1, (check, variant, name, findings)
						)

	def test_every_detector_is_flagged_after_every_leading_delimiter(self) -> None:
		for check, variants in sorted(self.MATRIX_FIXTURES.items()):
			for variant, fixture in sorted(variants.items()):
				for name, prefix in self.LEADING:
					with self.subTest(check=check, variant=variant, delimiter=name):
						findings = self.findings_for(check, f"{prefix}{fixture}")
						self.assertEqual(
							len(findings), 1, (check, variant, name, findings)
						)

	def test_address_alone_on_its_line_is_flagged(self) -> None:
		"""No surrounding context at all — the extreme of the above."""
		for check, variants in sorted(self.MATRIX_FIXTURES.items()):
			for variant, fixture in sorted(variants.items()):
				with self.subTest(check=check, variant=variant):
					findings = self.findings_for(check, fixture)
					self.assertEqual(len(findings), 1, (check, variant, findings))

	def test_the_address_is_named_in_its_own_finding(self) -> None:
		"""A finding has to say which token tripped it.

		Kept separate from the delimiter loops because it is a different
		property, and because it is spelled per shape: a bracketed or
		ported form is reported as the address it resolves to, not as the
		surrounding URL syntax.
		"""
		for check, variants in sorted(self.MATRIX_FIXTURES.items()):
			for variant, fixture in sorted(variants.items()):
				with self.subTest(check=check, variant=variant):
					findings = self.findings_for(check, f"host {fixture} today")
					core = fixture.strip("[]").split("]")[0].split("%")[0]
					self.assertIn(core, findings[0], (check, variant, findings))

	def test_every_grammar_shape_a_detector_admits_is_exercised(self) -> None:
		"""A detector with one fixture fails when its grammar admits more.

		The round-2 escape was not "a missing test". Every enumeration was
		green; the fixture behind them just happened to be the one spelling
		of the address whose port absorbed as a legal group. So "how many
		variants does this detector owe" must not be a number anyone
		remembers to raise — it is derived here from the detector itself, on
		two axes:

		STRUCTURE, read off the compiled regex. Every construct that makes
		the pattern match materially different shapes — an alternation, or a
		group that may be absent — has to be a NAMED group, and every one of
		those names has to be observed both matched and unmatched across the
		detector's variants. Adding an optional construct anonymously fails
		the first half; adding it with no fixture that exercises it fails the
		second. An alternation between plain literals (`SUSPICIOUS_TLDS` in
		`FQDN_RE`) is a vocabulary, not a shape, and is exempt — those are
		enumerated by test_every_suspicious_tld_fires_in_both_cases instead.

		SPELLING, read off the validator the detector delegates to. Every
		address detector accepts several spellings of one value — compressed
		and expanded IPv6, padded and unpadded IPv4, upper- and lower-case
		hostnames — and `ipaddress`/`str.lower` already know which spelling
		is canonical. So the variant set must contain at least one fixture
		written canonically and at least one not. This is the axis that was
		missing: compression is not a construct in `IPV6_RE`, it is a
		property of the value, and it is precisely what made one fixture
		cover for the other.

		Known limit, stated rather than left to be discovered: the structure
		half ignores constructs NESTED INSIDE a repetition, because "did this
		optional group match" is not answerable when the group matched a
		different number of times per repetition. `FQDN_RE`'s label-tail
		group is the one current instance; label-length handling is covered
		by the FQDN tests directly.
		"""
		for check, variants in sorted(self.MATRIX_FIXTURES.items()):
			pattern = MATRIX_PATTERN[check]
			shape_groups = shape_group_names(pattern)
			with self.subTest(check=check, axis="structure"):
				matched: set[str] = set()
				unmatched: set[str] = set()
				for fixture in variants.values():
					match = pattern.search(f"host {fixture} today")
					self.assertIsNotNone(match, (check, fixture))
					for name in shape_groups:
						(matched if match.group(name) else unmatched).add(name)
				self.assertEqual(
					shape_groups - matched,
					set(),
					f"{check}: no fixture variant exercises these grammar "
					f"constructs; add one per name",
				)
				self.assertEqual(
					shape_groups - unmatched,
					set(),
					f"{check}: every fixture variant exercises these "
					f"constructs, so their absence is never tested",
				)
			with self.subTest(check=check, axis="spelling"):
				spellings = {
					written == canonical
					for written, canonical in (
						MATRIX_SPELLING[check](fixture)
						for fixture in variants.values()
					)
				}
				self.assertEqual(
					spellings,
					{True, False},
					f"{check}: every variant is spelled the same way "
					f"relative to its validator's canonical form — add a "
					f"variant that is (or is not) canonical",
				)

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


class GuardCharacterDelimiterTests(unittest.TestCase):
	"""Issue #132: the delimiter axis, derived rather than remembered.

	`SurroundingContextTests.TRAILING`/`.LEADING` enumerate delimiters no
	detector's own guards particularly care about (a comma, a close paren, a
	tab — every one of them already terminates a match by construction, and
	the point of exercising them is breadth of coverage, not a specific
	guard). A dash was never in either list, and neither was a bare
	underscore or a leading period — the exact set a detector's OWN boundary
	lookarounds are built to test. `guard_characters()` (above) reads that set
	off the compiled patterns instead of leaving it to be remembered, so this
	class is a SECOND, narrower matrix: not "every safe delimiter", but
	"every character a guard anywhere in this file actually mentions".

	Two tables per direction, kept deliberately separate (per #132's own
	instruction not to blend a derived list with a declared one):

	  - `GUARD_CHARS` / `GUARD_CHARS_*_UNION` — DERIVED, from the compiled
	    regexes. `test_every_guard_character_has_a_declared_row` is the guard:
	    a character newly added to ANY detector's boundary lookaround changes
	    the union, and an entry missing from the tables below then fails this
	    test — "a guard character with no row" from the issue, executed.
	  - `GUARD_LEADING_EXPECTED` / `GUARD_TRAILING_EXPECTED` — DECLARED, a
	    measured verdict (FLAGGED or SUPPRESSED) for every (check, character)
	    pair in the union, including pairs the check's OWN guards say nothing
	    about — IPv4's and FQDN's dash guards force IPv6 to carry a dash row
	    too, which is exactly the point: IPv6 has no dash guard, and that
	    absence is now a tested property (FLAGGED) instead of an unexamined
	    gap. Values were measured against the real detectors with
	    `python3 -B`, not assumed from reading the regex.
	"""

	# The `.` row is exercised with a following digit (`.5`), not a bare
	# trailing period: IPV4_RE's own trailing guard is literally `\.` then a
	# DIGIT category (`(?!\.\d)`), not `\.` then any alnum, so `.5` is the
	# precise shape that guard tests for — a bare trailing period is already
	# covered by SurroundingContextTests.TRAILING's "sentence period" case
	# and is FLAGGED for every detector (none of the guards reject a
	# standalone `.`), which would make it useless as a discriminating row
	# here. FQDN's and IPv6's own trailing dot guards (`\.[A-Za-z0-9]`) also
	# reject `.5`, a digit being alnum, so the same suffix exercises all
	# three detectors' actual guard condition rather than three different
	# ones.
	TRAILING_SUFFIX = {".": ".5", "-": "-", "_": "_"}

	FLAGGED = "flagged"
	SUPPRESSED = "suppressed"

	# Measured, not assumed — see the class docstring. A dash trailing an
	# IPv6 literal is the row #132 is actually about: FLAGGED here is the
	# executable form of "IPv6 has no dash-adjacency guard at all", and it is
	# what turns red the moment that absence is (re)closed, e.g. by the
	# trailing not-a-dash lookahead the issue's own mutation describes.
	GUARD_LEADING_EXPECTED = {
		scanner.CHECK_IP: {".": SUPPRESSED, "-": SUPPRESSED},
		scanner.CHECK_FQDN: {".": SUPPRESSED, "-": SUPPRESSED},
		scanner.CHECK_IPV6: {".": FLAGGED, "-": FLAGGED},
	}
	GUARD_TRAILING_EXPECTED = {
		scanner.CHECK_IP: {".": SUPPRESSED, "-": SUPPRESSED, "_": SUPPRESSED},
		scanner.CHECK_FQDN: {".": SUPPRESSED, "-": SUPPRESSED, "_": SUPPRESSED},
		scanner.CHECK_IPV6: {".": SUPPRESSED, "-": FLAGGED, "_": SUPPRESSED},
	}

	# Which of SurroundingContextTests.MATRIX_FIXTURES' own IPv6 variants a
	# guard-character row can actually be run against. The bracketed forms
	# are excluded because a bracket is its OWN hard boundary (the scanner's
	# own comment: "the `[` cannot be part of a hex/colon run, so there is
	# nothing to re-anchor") — a leading/trailing character next to `[`/`]`
	# never reaches the guard this class is testing at all; measured: the
	# regex simply re-matches the BARE address just inside the brackets and
	# the probe character lands beyond the match entirely, at the ALWAYS-safe
	# `[`/`]` boundary SurroundingContextTests already exercises. The
	# `ipv4-mapped` and `zone id` variants are excluded for a sharper reason:
	# their own optional groups (`mapped_quad`'s `(?:\.\d{1,3}){1,3}`, and
	# `zone`'s `%[\w.-]+`) are THEMSELVES greedy over `.`, `-` and `_` — a
	# probe character glued on gets absorbed into the address's own trailing
	# group instead of ever reaching the outer boundary guard, which is a
	# different (already-covered, by DetectorPositiveTests/IPv6DetectorTests)
	# property than the one this class exists to pin.
	IPV6_GUARD_VARIANTS = ("compressed", "fully expanded", "link-local compressed")

	def _variants_for(self, check: str) -> dict[str, str]:
		if check == scanner.CHECK_IPV6:
			return {
				name: fixture
				for name, fixture in SurroundingContextTests.MATRIX_FIXTURES[check].items()
				if name in self.IPV6_GUARD_VARIANTS
			}
		return dict(SurroundingContextTests.MATRIX_FIXTURES[check])

	def findings_for(self, check: str, text: str) -> list[str]:
		message = SurroundingContextTests.MATRIX_MESSAGE[check]
		return [f for f in scanner.scan_text("f.md", text) if message in f]

	def test_every_guard_character_has_a_declared_row(self) -> None:
		"""The completeness guard: a new guard character fails this, not silently
		passes the suite.

		Two directions, both load-bearing:

		  - every character `guard_characters()` finds in ANY detector's own
		    lookarounds must have a declared expectation for EVERY detector
		    (not just the one whose regex mentions it) — this is what forces
		    IPv6 to carry a dash row despite IPV6_RE never naming one;
		  - the declared tables must not carry a character outside that
		    union either, which would read as derived coverage for a
		    character no guard actually tests for.
		"""
		for check in scanner.CHECK_NAMES - {scanner.CHECK_DEPOT_TOKEN}:
			with self.subTest(check=check, direction="leading"):
				self.assertEqual(
					set(self.GUARD_LEADING_EXPECTED[check]),
					GUARD_CHARS_LEADING_UNION,
					f"{check}: declared leading rows must equal the union of "
					f"every detector's own leading guard characters",
				)
			with self.subTest(check=check, direction="trailing"):
				self.assertEqual(
					set(self.GUARD_TRAILING_EXPECTED[check]),
					GUARD_CHARS_TRAILING_UNION,
					f"{check}: declared trailing rows must equal the union of "
					f"every detector's own trailing guard characters",
				)
		# Each detector's OWN guard characters must be a subset of the union
		# it is checked against — otherwise the union itself is wrong.
		for check, (leading, trailing) in GUARD_CHARS.items():
			self.assertLessEqual(leading, GUARD_CHARS_LEADING_UNION, check)
			self.assertLessEqual(trailing, GUARD_CHARS_TRAILING_UNION, check)
		# A dash is asserted explicitly, per #132's stated minimum: whatever
		# else the derivation finds, a guard-character regression on the dash
		# specifically must be caught for every detector.
		self.assertIn("-", GUARD_CHARS_LEADING_UNION)
		self.assertIn("-", GUARD_CHARS_TRAILING_UNION)
		for check in (scanner.CHECK_IP, scanner.CHECK_FQDN, scanner.CHECK_IPV6):
			self.assertIn("-", self.GUARD_LEADING_EXPECTED[check])
			self.assertIn("-", self.GUARD_TRAILING_EXPECTED[check])

	# --- the derivation's own reach (PR #138 round 1, finding 2) ---------
	#
	# The three tests below are about `guard_characters()` itself rather than
	# about any detector: they pin HOW MUCH OF A PATTERN it can see. The first
	# version saw only top-level assertion nodes, which was true of today's
	# detectors and false as a mechanism — a guard nested one group deeper was
	# invisible, and for a character that no top-level guard mentions and
	# `SurroundingContextTests.TRAILING` does not list, nothing else in this
	# file covered it either.

	# Characters chosen for the nested-guard probes: none of them appears in
	# any detector's real guards, and none is in SurroundingContextTests'
	# hand-written delimiter lists — which is exactly what made them the
	# escape. The middle one is a pipe: an address in a markdown table cell.
	NESTED_PROBE_CHARS = ("#", "|", "+")

	@staticmethod
	def _top_level_only(pattern: re.Pattern[str]) -> tuple[frozenset[str], frozenset[str]]:
		"""The superseded top-level-only walk, kept as an executable control.

		Its whole purpose is to be compared against the real derivation: it
		is what "the suite stayed at 135/135 while three findings vanished"
		looked like in code.
		"""
		leading: set[str] = set()
		trailing: set[str] = set()
		for op, argument in _re_parser.parse(pattern.pattern, pattern.flags):
			if _op_name(op) not in ("ASSERT", "ASSERT_NOT"):
				continue
			direction, body = argument
			(leading if direction < 0 else trailing).update(_guard_literal_chars(body))
		return frozenset(leading), frozenset(trailing)

	def test_recursion_leaves_todays_derived_sets_unchanged(self) -> None:
		"""Recursing is a no-op against the patterns as they stand.

		This is the honesty half of the fix. All three detectors carry their
		guards at the top level today, so the recursive walk must derive
		exactly what the top-level-only walk did — otherwise the change
		quietly altered the expectation tables' meaning rather than only
		extending their reach. If a guard is ever MOVED inside a group, this
		test starts failing, which is the correct signal: the two walks have
		diverged and the nested one is now the truthful one.
		"""
		for check, pattern in sorted(MATRIX_PATTERN.items()):
			with self.subTest(check=check):
				self.assertEqual(guard_characters(pattern), self._top_level_only(pattern))

	def test_a_nested_guard_is_seen_by_the_derivation(self) -> None:
		"""The escape itself, executed — a guard one group deeper is found.

		Two probes, because they fail for different reasons if the recursion
		regresses:

		  1. A standalone fixture pattern whose guards sit inside a named
		     group, inside an alternation, inside a repeat — the three
		     container kinds the real detectors are built from, stacked. It
		     does not depend on any detector's current spelling, so it keeps
		     working when they change.
		  2. The real `IPV6_RE`, surgically given the reviewer's own
		     mutation: one trailing negative lookahead over a three-character
		     class placed INSIDE the bare alternative. The anchor it splices
		     onto is asserted first, so a future pattern edit fails this test
		     loudly instead of silently testing nothing.

		In both, the superseded top-level walk is asserted to MISS what the
		real one finds. That assertion is the finding: without it, "the
		derivation covers nested guards" would be a claim rather than a
		measurement.
		"""
		probe = "".join(self.NESTED_PROBE_CHARS)
		nested_fixture = re.compile(
			r"(?:(?P<a>x+(?<![" + probe + r"])y)|(?P<b>z(?![" + probe + r"])))+"
		)
		leading, trailing = guard_characters(nested_fixture)
		flat_leading, flat_trailing = self._top_level_only(nested_fixture)
		for char in self.NESTED_PROBE_CHARS:
			with self.subTest(probe="fixture", char=char):
				self.assertIn(char, leading)
				self.assertIn(char, trailing)
				self.assertNotIn(char, flat_leading)
				self.assertNotIn(char, flat_trailing)

		anchor = r"(?P<zone>%[\w.-]+)?"
		source = scanner.IPV6_RE.pattern
		self.assertIn(anchor, source, "IPV6_RE's bare alternative no longer ends as expected")
		mutated = re.compile(
			source.replace(anchor, anchor + r"(?!" + "[" + probe + "])", 1)
		)
		_leading, mutated_trailing = guard_characters(mutated)
		_flat_leading, flat_mutated_trailing = self._top_level_only(mutated)
		for char in self.NESTED_PROBE_CHARS:
			with self.subTest(probe="IPV6_RE", char=char):
				self.assertIn(char, mutated_trailing)
				self.assertNotIn(char, flat_mutated_trailing)
		# And the union built from a mutated detector would demand rows the
		# declared tables do not have — i.e. the completeness test would fail,
		# which is the whole point of finding the nested guard.
		self.assertFalse(
			set(mutated_trailing) <= set(self.GUARD_TRAILING_EXPECTED[scanner.CHECK_IPV6]),
			"a nested guard must create an undeclared row",
		)

	def test_the_guard_walk_descends_every_container_the_detectors_use(self) -> None:
		"""No node kind in the real patterns holds a sub-sequence we skip.

		`_collect_guards` descends a named list of container node kinds. A
		kind missing from that list is a silent hole of exactly the sort
		finding 2 was about, so the list is checked against the real parse
		trees structurally: any node whose argument contains a `SubPattern`
		anywhere is a container, and every container found must be one we
		descend into.
		"""
		sub_pattern_type = _re_parser.SubPattern

		def contains_sub_pattern(value) -> bool:
			if isinstance(value, sub_pattern_type):
				return True
			if isinstance(value, (tuple, list)):
				return any(contains_sub_pattern(item) for item in value)
			return False

		seen: set[str] = set()

		def walk(sequence) -> None:
			for op, argument in sequence:
				name = _op_name(op)
				if contains_sub_pattern(argument):
					seen.add(name)
				if isinstance(argument, (tuple, list)):
					for item in argument:
						if isinstance(item, sub_pattern_type):
							walk(item)
						elif isinstance(item, (tuple, list)):
							for inner in item:
								if isinstance(inner, sub_pattern_type):
									walk(inner)

		for pattern in MATRIX_PATTERN.values():
			walk(_re_parser.parse(pattern.pattern, pattern.flags))

		self.assertTrue(seen, "no container nodes found — the walk is not walking")
		self.assertLessEqual(
			seen,
			_GUARD_CONTAINER_OPS,
			f"container node kinds not descended into: {sorted(seen - _GUARD_CONTAINER_OPS)}",
		)

	# --- the EXTRACTOR's reach (PR #138 round 2, finding 1) --------------
	#
	# The two tests below are the same protection one level over. Round 1
	# fixed WHERE the derivation looks for assertions and guarded that with
	# the structural test above; the sibling that extracts characters OUT of
	# an assertion body kept a fixed node-kind list and no guard rail at all,
	# so a guard body that was a repeat or a branch named punctuation nothing
	# derived — measured at 141/141 green while the same three findings
	# vanished. Both functions share one descent rule now
	# (`_guard_sub_sequences`), and both directions of it are pinned.

	@staticmethod
	def _literal_chars_without_container_recursion(sequence) -> set[str]:
		"""The superseded extractor, kept as an executable control.

		Byte-for-byte the pre-fix body shapes: literals, one level of
		character class, a bare category, and recursion into `SUBPATTERN`
		alone. Its only purpose is to be asserted to MISS what the real
		extractor finds, so "the derivation reads a guard body at any depth"
		stays a measurement rather than a claim.
		"""
		chars: set[str] = set()
		for op, argument in sequence:
			name = _op_name(op)
			if name in ("LITERAL", "NOT_LITERAL"):
				chars.add(chr(argument))
			elif name == "IN":
				for sub_op, sub_argument in argument:
					sub_name = _op_name(sub_op)
					if sub_name == "LITERAL":
						chars.add(chr(sub_argument))
					elif sub_name == "CATEGORY" and "WORD" in str(sub_argument):
						chars.add("_")
			elif name == "CATEGORY" and "WORD" in str(argument):
				chars.add("_")
			elif name == "SUBPATTERN":
				chars |= GuardCharacterDelimiterTests._literal_chars_without_container_recursion(
					argument[3]
				)
		return chars

	# The two guard-body spellings the round-2 review exhibited, each as the
	# TAIL of a trailing lookahead over the three probe characters: a repeat
	# over a character class, and a branch with two nodes per alternative.
	# Written as (label, body-source) so the escape is executed, not recalled.
	BODY_SHAPE_SPELLINGS = (
		("repeat over a class", "[{probe}]+"),
		("branch, two nodes per alternative", "[{first}]\\w|[{rest}]\\w"),
	)

	def test_a_guard_body_container_is_read_by_the_extractor(self) -> None:
		"""A guard body that is a repeat, or a branch, still names its characters.

		This is round 1's finding 2 one level over, executed. The recursion
		fix made `guard_characters()` see an assertion wherever it sits; this
		is about what it can read once it is there. Both spellings below were
		measured on the tip that had the fixed extractor list: the suite
		stayed at **141/141 OK** while `manager <address>` followed by each
		of hash, pipe and plus went from one finding to none.

		Each spelling is spliced into the real `IPV6_RE` at the same anchor
		the nested-guard test uses, and the superseded extractor is asserted
		to miss what the real one finds. The union built from the mutated
		detector must also demand rows the declared tables do not have —
		i.e. the completeness test would fail, which is what turns the
		escape red.

		One spelling deliberately NOT in the list, because it is why this
		needs to be per-node-kind rather than per-example: a branch whose
		alternatives share a leading node (`[hash pipe plus]\\w|[hash pipe
		plus]\\d`) is hoisted by the parser into class-then-branch, so its
		characters sat at the top of the body and were derived correctly
		even before the fix. Remembering examples would have "covered" the
		branch case while leaving the escaping spelling open.
		"""
		probe = "".join(self.NESTED_PROBE_CHARS)
		anchor = r"(?P<zone>%[\w.-]+)?"
		source = scanner.IPV6_RE.pattern
		self.assertIn(anchor, source, "IPV6_RE's bare alternative no longer ends as expected")
		for label, spelling in self.BODY_SHAPE_SPELLINGS:
			body = spelling.format(
				probe=probe,
				first=self.NESTED_PROBE_CHARS[0] + self.NESTED_PROBE_CHARS[1],
				rest=self.NESTED_PROBE_CHARS[2],
			)
			mutated = re.compile(source.replace(anchor, anchor + "(?!" + body + ")", 1))
			_leading, trailing = guard_characters(mutated)
			flat = self._literal_chars_without_container_recursion(
				_re_parser.parse("(?!" + body + ")")[0][1][1]
			)
			for char in self.NESTED_PROBE_CHARS:
				with self.subTest(body=label, char=char):
					self.assertIn(char, trailing)
					self.assertNotIn(char, flat)
			with self.subTest(body=label, check="undeclared row"):
				self.assertFalse(
					set(trailing) <= set(self.GUARD_TRAILING_EXPECTED[scanner.CHECK_IPV6]),
					"a guard body the extractor cannot read must create an undeclared row",
				)

	def test_a_guard_naming_punctuation_by_a_range_is_read(self) -> None:
		"""The third spelling of "names a character": a class RANGE.

		`[!-/]` names a dot and a dash without either appearing as a literal
		node, so the pre-fix extractor derived nothing from it — the same
		defect as the container one, in the character half rather than the
		structure half. Closed by `_punctuation_in_range`, bounded to
		printable ASCII and to non-alphanumerics so `[A-Za-z0-9]` (which the
		real IPv4 and FQDN guards use) still contributes nothing, which is
		why closing it changed no derived set.
		"""
		low, high = self.NESTED_PROBE_CHARS[0], self.NESTED_PROBE_CHARS[2]  # hash .. plus
		anchor = r"(?P<zone>%[\w.-]+)?"
		source = scanner.IPV6_RE.pattern
		self.assertIn(anchor, source, "IPV6_RE's bare alternative no longer ends as expected")
		body = "[" + low + "-" + high + "]"
		mutated = re.compile(source.replace(anchor, anchor + "(?!" + body + ")", 1))
		_leading, trailing = guard_characters(mutated)
		flat = self._literal_chars_without_container_recursion(
			_re_parser.parse("(?!" + body + ")")[0][1][1]
		)
		for char in (low, high):
			with self.subTest(char=char):
				self.assertIn(char, trailing)
				self.assertNotIn(char, flat)
		self.assertFalse(
			set(trailing) <= set(self.GUARD_TRAILING_EXPECTED[scanner.CHECK_IPV6]),
			"a guard naming punctuation by a range must create an undeclared row",
		)
		# The bound that keeps this safe rather than explosive: an alnum
		# range still contributes nothing, so today's real guards are
		# unaffected and the derived sets did not move when this landed.
		self.assertEqual(_punctuation_in_range(ord("0"), ord("9")), set())
		self.assertEqual(_punctuation_in_range(ord("a"), ord("z")), set())

	def test_the_disclosed_extraction_limit_is_what_the_doc_says(self) -> None:
		"""The limit that REMAINS, executed rather than asserted in prose.

		`docs/testing.md` states exactly two things the derivation cannot do
		with the characters a guard names, and both are pinned here so the
		published sentence cannot drift from the code:

		  1. A guard that names characters only through a category other
		     than "word" (`\\S`, `\\W`, `\\D`) contributes nothing — those
		     name open-ended sets, not delimiters, and enumerating them would
		     demand a declared row for most of ASCII. Unlike the container
		     and range holes this one cannot escape SILENTLY, which is
		     measured rather than argued: splicing a trailing "not a
		     non-word character" guard into the bare alternative fails 12
		     tests outright, because a guard rejecting every non-word
		     neighbour changes the measured verdict of characters the
		     declared table already carries.
		  2. A guard that names its characters through a back-reference
		     contributes nothing — the characters are not in the guard at
		     all, they are whatever the referenced group captured at match
		     time, which no parse-tree read can know. That one is the real
		     residual: neither derived nor loud.

		If either is ever closed, this test fails and the doc gets re-read —
		which is the only mechanism that has actually kept this file honest.
		"""
		for label, body in (
			("non-word category", r"\S"),
			("negated word category", r"\W"),
			("back-reference", r"(?P=zone)"),
		):
			with self.subTest(spelling=label):
				pattern = re.compile(r"(?P<zone>a)b(?!" + body + r")")
				leading, trailing = guard_characters(pattern)
				self.assertEqual(
					(leading, trailing),
					(frozenset(), frozenset()),
					f"{label} now contributes characters — update the disclosed "
					f"limit in docs/testing.md",
				)

	# One regex spelling per container node kind, used to prove — by
	# construction rather than by reading the code — that a character sitting
	# inside a node of that kind inside a guard reaches the derived set. A
	# kind added to `_GUARD_CONTAINER_OPS` without a spelling here fails the
	# test below rather than being taken on trust.
	CONTAINER_PROBE_BODIES = {
		"ASSERT": "(?<={c})",
		"ASSERT_NOT": "(?!{c})",
		"SUBPATTERN": "(?P<probe>{c})",
		"BRANCH": "{c}y|zx",
		"MAX_REPEAT": "{c}+",
		"MIN_REPEAT": "{c}+?",
	}
	if "ATOMIC_GROUP" in _GUARD_CONTAINER_OPS:
		CONTAINER_PROBE_BODIES["ATOMIC_GROUP"] = "(?>{c})"

	def test_the_extractor_descends_every_container_kind_it_names(self) -> None:
		"""Every container kind is descended into — checked by running it.

		The structural test above closes the other direction: a kind present
		in the real detectors may not be missing from `_GUARD_CONTAINER_OPS`.
		This one closes THIS direction: a kind that IS in that set must
		actually carry a guard's characters out to the derived set, through
		both `_collect_guards` and `_guard_literal_chars`. Listing a kind and
		not descending into it is the exact defect round 2 found, and reading
		the code is how it survived round 1.

		Each kind is probed with a real compiled pattern whose parse tree is
		first asserted to CONTAIN that kind — the parser rewrites some
		spellings (it hoists a branch's common leading node, and drops a
		non-capturing group that wraps nothing), so a spelling that no longer
		produces the node it is named for fails loudly instead of silently
		testing something else.
		"""
		missing = sorted(_GUARD_CONTAINER_OPS - set(self.CONTAINER_PROBE_BODIES))
		self.assertFalse(missing, f"container kinds with no probe spelling: {missing}")
		probe_char = "~"
		self.assertNotIn(
			probe_char,
			GUARD_CHARS_LEADING_UNION | GUARD_CHARS_TRAILING_UNION,
			"probe character must not be one a real detector already names",
		)
		for kind in sorted(_GUARD_CONTAINER_OPS):
			with self.subTest(container=kind):
				body = self.CONTAINER_PROBE_BODIES[kind].format(c=re.escape(probe_char))
				pattern = re.compile(r"a(?!" + body + r")")
				kinds_present: set[str] = set()

				def note(sequence) -> None:
					for op, argument in sequence:
						name = _op_name(op)
						kinds_present.add(name)
						for sub_sequence in _guard_sub_sequences(name, argument):
							note(sub_sequence)

				note(_re_parser.parse(pattern.pattern, pattern.flags))
				self.assertIn(
					kind,
					kinds_present,
					f"probe spelling for {kind} no longer parses to that node kind",
				)
				leading, trailing = guard_characters(pattern)
				self.assertIn(
					probe_char,
					leading | trailing,
					f"a character inside a {kind} inside a guard is not derived",
				)

	def test_leading_guard_characters_match_declared_expectation(self) -> None:
		for check, expected in sorted(self.GUARD_LEADING_EXPECTED.items()):
			for variant, fixture in sorted(self._variants_for(check).items()):
				for char, outcome in sorted(expected.items()):
					text = f"{char}{fixture}"
					with self.subTest(check=check, variant=variant, char=char):
						findings = self.findings_for(check, text)
						want = 1 if outcome == self.FLAGGED else 0
						self.assertEqual(
							len(findings), want, (check, variant, char, outcome, findings)
						)

	def test_trailing_guard_characters_match_declared_expectation(self) -> None:
		for check, expected in sorted(self.GUARD_TRAILING_EXPECTED.items()):
			for variant, fixture in sorted(self._variants_for(check).items()):
				for char, outcome in sorted(expected.items()):
					suffix = self.TRAILING_SUFFIX[char]
					text = f"{fixture}{suffix}"
					with self.subTest(check=check, variant=variant, char=char):
						findings = self.findings_for(check, text)
						want = 1 if outcome == self.FLAGGED else 0
						self.assertEqual(
							len(findings), want, (check, variant, char, outcome, findings)
						)

	def test_ipv6_range_both_endpoints_are_caught(self) -> None:
		"""The regression #132 exists to catch, pinned directly.

		IPv6 has no dash-adjacency guard at all (docs/testing.md states this
		as a property, not an aspiration), so a dash-separated pair of IPv6
		addresses is caught in full — mirroring
		RangeDetectionTests.test_the_exact_range_from_the_issue for IPv4.
		Demonstrated regression (issue #132): adding a trailing "not a dash"
		lookahead to IPV6_RE — described in words, not written, because
		GitHub swallows a lookahead-open immediately followed by a
		character class even inside a fence — takes this from two findings
		to one, silently losing the second endpoint, exactly the #111 defect
		for the other address family. This test is what turns red if that
		regression is reintroduced; test_trailing_guard_characters_match_
		declared_expectation's IPv6/dash row (`FLAGGED`) is what turns red
		first, one line at a time, but this is the literal shape from the
		issue body.
		"""
		second = ipv6("fd00", "1a2b", "3c4d", "", "1")
		findings = scanner.scan_text("f.md", f"range {LAB_IPV6}-{second}")
		self.assertEqual(len(findings), 2, findings)
		self.assertTrue(any(LAB_IPV6 in f for f in findings), findings)
		self.assertTrue(any(second in f for f in findings), findings)

	def test_ipv6_dash_then_word_matches_the_issue_measurement(self) -> None:
		"""The first row measured in issue #132's body, verbatim in shape.

		`<address>-x` is 1 finding today (the dash does not terminate the
		match early, nor does it merge with `x`); the issue's demonstrated
		mutation takes it to 0. Kept separate from the derived matrix row
		above (which uses a bare trailing dash) because this is the exact
		measured input from the issue, not a paraphrase of it.
		"""
		findings = scanner.scan_text("f.md", f"dash {LAB_IPV6}-x")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IPV6, findings[0])


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

	def test_padding_width_is_not_a_bound(self) -> None:
		"""Issue #119: the first fix stopped one digit short.

		`IPV4_RE`'s `\\d{1,3}` and `_parse_ipv4_octets`' `len(part) > 3` both
		capped at three digits, so a four-digit-padded quad never produced a
		candidate and read as "not an address, so allowed" — the #111 bug one
		padding digit further out. Widening only one of the two would have
		changed nothing, so both are exercised here across several widths.
		"""
		for width in (3, 4, 5, 8):
			with self.subTest(width=width):
				padded = zero_pad_quad(LAB_IP, width)
				findings = scanner.scan_text("f.md", f"host {padded}")
				self.assertEqual(len(findings), 1, (width, findings))
				self.assertIn(padded, findings[0])

	def test_mixed_width_padding_from_the_issue_is_flagged(self) -> None:
		"""The octal-styled shape #119 names, padded unevenly per octet."""
		parts = quad(250, 54, 14, 7).split(".")
		padded = ".".join(
			part.zfill(width) for part, width in zip(parts, (4, 3, 3, 2))
		)
		findings = scanner.scan_text("f.md", f"gw {padded}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(padded, findings[0])

	def test_wider_padding_of_an_allowed_address_stays_allowed(self) -> None:
		"""Widening must not turn the sanctioned ranges into findings."""
		for width in (4, 6):
			with self.subTest(width=width):
				self.assertEqual(
					scanner.scan_text(
						"f.md", f"doc {zero_pad_quad(quad(192, 0, 2, 1), width)}"
					),
					[],
				)
				self.assertEqual(
					scanner.scan_text(
						"f.md", f"bind {zero_pad_quad(quad(0, 0, 0, 0), width)}"
					),
					[],
				)

	def test_four_significant_digits_are_not_an_address(self) -> None:
		"""Padding is stripped; significant digits are still bounded at three.

		Otherwise widening the regex would start reading long dotted number
		runs (build numbers, dates) as addresses.
		"""
		for text in (
			f"rel {quad(1000, 1, 1, 1)}",
			f"rel {quad(1, 2, 3, 4096)}",
			f"seq {quad(2026, 8, 3, 1200)}",
		):
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)


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
		# Assembled through ipv6() rather than written as an f-string with
		# the mapped-form prefix spelled out. With the boundary guards fixed
		# (PR #115 round 2) that prefix is, on its own, a valid and
		# non-exempt IPv6 literal — so the old spelling turned this very
		# file, which the scanner reads like every other tracked file, into
		# a finding. Content fixed, detector left alone, per this module's
		# own "fixtures are assembled at runtime" rule.
		mapped = ipv6("", "", "ffff", LAB_IP)
		findings = scanner.scan_text("f.md", f"legacy client at {mapped}")
		self.assertEqual(len(findings), 2, findings)
		self.assertTrue(
			any("IPv6 address literal" in f for f in findings), findings
		)
		self.assertTrue(
			any("IP address literal" in f for f in findings), findings
		)

	def test_the_line_that_defeated_the_gate_a_second_time(self) -> None:
		"""Verbatim regression for the PR #115 round-1 escape.

		`IPV6_RE` shipped with a literal `.` inside its trailing lookahead —
		the same construction the IPv4 comment in the scanner warns against at
		length — so a sentence-final IPv6 literal was never matched. This is
		the line that was appended to a real tracked file and still scanned
		`clean`, exit 0.
		"""
		text = f"The NSX manager answers at {LAB_IPV6}."
		findings = scanner.scan_text("docs/testing.md", text)
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IPV6, findings[0])

	def test_sentence_internal_period_does_not_hide_the_literal(self) -> None:
		findings = scanner.scan_text("f.md", f"host {LAB_IPV6}. Next host is up.")
		self.assertEqual(len(findings), 1, findings)

	def test_ellipsis_does_not_hide_the_literal(self) -> None:
		findings = scanner.scan_text("f.md", f"see {LAB_IPV6}...")
		self.assertEqual(len(findings), 1, findings)

	def test_a_period_that_continues_the_token_still_rejects(self) -> None:
		"""The other half of the trailing guard, and why it isn't just `(?!\\.)`.

		A `.` followed by an alphanumeric continues a name; the literal is not
		standing alone and this must stay quiet, or every dotted hostname with
		a hex-lettered leading label becomes an IPv6 finding.
		"""
		self.assertEqual(
			scanner.scan_text("f.md", f"host {ipv6('fd00', '', '7')}.example.com"), []
		)

	def test_trailing_colon_delimiter_does_not_hide_the_literal(self) -> None:
		"""A single trailing `:` is a delimiter — it cannot be part of a literal."""
		for text in (f"host {LAB_IPV6}:", f"host {LAB_IPV6}: it answers"):
			with self.subTest(text=text):
				findings = scanner.scan_text("f.md", text)
				self.assertEqual(len(findings), 1, findings)

	def test_leading_colon_delimiter_does_not_hide_the_literal(self) -> None:
		"""The `key:<address>` shape, mirroring the IPv4 detector's behaviour."""
		findings = scanner.scan_text("f.md", f"addr:{LAB_IPV6}")
		self.assertEqual(len(findings), 1, findings)

	def test_leading_period_delimiter_does_not_hide_the_literal(self) -> None:
		findings = scanner.scan_text("f.md", f"x.{LAB_IPV6}")
		self.assertEqual(len(findings), 1, findings)

	def test_double_colon_is_not_trimmed_as_a_delimiter(self) -> None:
		"""Trimming must not eat a compression marker.

		A bare `<prefix>::` is a complete literal and stays a finding; the
		sanctioned all-colon forms stay allowed.
		"""
		prefix_only = ipv6("fd00", "", "")
		findings = scanner.scan_text("f.md", f"net {prefix_only}")
		self.assertEqual(len(findings), 1, findings)
		self.assertEqual(scanner.scan_text("f.md", f"bind {OK_IPV6_UNSPECIFIED}"), [])
		self.assertEqual(scanner.scan_text("f.md", f"lo {OK_IPV6_LOOPBACK}"), [])

	def test_mapped_form_with_a_port_is_still_flagged(self) -> None:
		"""A trailing `:port` must end the match, not reject it.

		`IPV6_RE` briefly carried a trailing "not followed by a colon" guard,
		on the reasoning that a greedy hex/colon run can never be followed by
		a colon anyway. That is true only until the match ends in the
		IPv4-mapped dotted quad (or a zone id), where a `:port` does reach the
		guard — and rejecting the match there loses the IPv6 finding entirely.
		The loss was quiet because the IPv4 detector still fired on the
		embedded quad, so the line was not silent, just under-reported.
		"""
		mapped = ipv6("", "", "ffff", LAB_IP)
		findings = scanner.scan_text("f.md", f"legacy {mapped}:8080")
		self.assertEqual(len(findings), 2, findings)
		self.assertTrue(any("IPv6 address literal" in f for f in findings), findings)

	def test_bare_literal_with_a_port_is_flagged(self) -> None:
		"""The unbracketed `address:port` shape, which is genuinely ambiguous.

		The port digits are hex-legal, so they join the literal; that still
		resolves to a unique-local address and is still a finding.
		"""
		findings = scanner.scan_text("f.md", f"host {LAB_IPV6}:8080")
		self.assertEqual(len(findings), 1, findings)

	def test_ipv6_check_is_individually_waivable(self) -> None:
		self.assertEqual(len(scanner.scan_text("f.md", LAB_IPV6)), 1)
		scanner.ALLOWLIST_FINDINGS["f.md"] = {scanner.CHECK_IPV6: "test"}
		try:
			self.assertEqual(scanner.scan_text("f.md", LAB_IPV6), [])
		finally:
			del scanner.ALLOWLIST_FINDINGS["f.md"]


class UnbracketedPortTests(unittest.TestCase):
	"""PR #115 round 2, finding 1 — `address:port` written without brackets.

	The greedy hex/colon class takes the port into the candidate. For a
	COMPRESSED address the port digits absorb as one more legal group, so the
	line is flagged anyway and every existing test and the whole delimiter
	matrix stayed green. For a FULLY-EXPANDED address the port is a ninth
	group, strict parsing fails, and the failure read as "not an address, so
	allowed" — zero findings, exit 0, on the exact shape issue #112's Impact
	paragraph names: log lines, inventory exports, CKL/HDF results.
	"""

	def test_fully_expanded_literal_with_a_port_is_flagged(self) -> None:
		"""The blocker itself, at three ports and on both lab prefixes."""
		for address in (LAB_IPV6_FULL, ipv6(
			"fe80", "0000", "0000", "0000", "1a2b", "3c4d", "5e6f", "7a8b"
		)):
			for port in (443, 8443, 22):
				text = f"vcenter at {with_port(address, port)}"
				with self.subTest(text=text):
					findings = scanner.scan_text("f.md", text)
					self.assertEqual(len(findings), 1, findings)
					self.assertIn(address, findings[0])

	def test_the_controls_that_hid_it_still_pass(self) -> None:
		"""Each of these was already green while the case above was silent."""
		for text in (
			f"vcenter at {LAB_IPV6_FULL}",
			f"vcenter at {with_port(LAB_IPV6, 443)}",
			f"vcenter at {bracketed_port(LAB_IPV6_FULL, 443)}",
		):
			with self.subTest(text=text):
				self.assertEqual(len(scanner.scan_text("f.md", text)), 1, text)

	def test_both_spellings_of_one_address_behave_the_same(self) -> None:
		"""Compressed and expanded are the same address, so the same verdict.

		Written as an equality between the two spellings rather than as two
		separate counts: the defect was precisely that they diverged. Run
		over the sanctioned addresses as well as the lab ones, because the
		allowed set used to compare SPELLINGS — so the fully-expanded
		loopback was a finding while the compressed one was not.
		"""
		for address in (OK_IPV6_DOC, OK_IPV6_LOOPBACK, OK_IPV6_UNSPECIFIED, LAB_IPV6):
			expanded = ipv6(*(f"{group:04x}" for group in _hextets(address)))
			with self.subTest(address=address):
				self.assertEqual(
					len(scanner.scan_text("f.md", f"host {address}")),
					len(scanner.scan_text("f.md", f"host {expanded}")),
					(address, expanded),
				)

	def test_an_unbracketed_port_on_a_sanctioned_address_is_ambiguous(self) -> None:
		"""A disclosed false positive, pinned so it cannot drift silently.

		`<loopback>:<port>` written WITHOUT brackets is not decidable: as
		written it is also a valid, different, non-sanctioned address, and
		nothing on the line says which was meant. The gate resolves it the
		noisy way — it reports — because over-reporting an ambiguous literal
		costs a sentence in a PR and under-reporting one costs a leak. The
		unambiguous spelling is the bracketed URL form, which is what every
		tool that writes such a pair emits, and it stays silent.

		Not the same question as the blocker this class exists for: there the
		address was UNSANCTIONED and the port made it silent. Here the address
		is sanctioned and the port makes it loud.
		"""
		self.assertEqual(
			len(scanner.scan_text("f.md", f"lo {with_port(OK_IPV6_LOOPBACK, 443)}")), 1
		)
		self.assertEqual(
			scanner.scan_text("f.md", f"lo {bracketed_port(OK_IPV6_LOOPBACK, 443)}"), []
		)
		expanded = ipv6(*(f"{group:04x}" for group in _hextets(OK_IPV6_LOOPBACK)))
		self.assertEqual(
			scanner.scan_text("f.md", f"lo {with_port(expanded, 443)}"), []
		)

	def test_the_port_retry_does_not_invent_an_address(self) -> None:
		"""Dropping a trailing digit group must not make a non-address parse."""
		for text in (
			"elapsed 01:02:03",
			"elapsed 01:02:03:04",
			"ports 8443:8443",
			"ports 18443:8443:443",
			"NIC de:ad:be:ef:ca:12",
			"src f.py:123:456: warning",
			"cron 0 2 * * * run:12:34",
		):
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)

	def test_a_port_never_hides_an_address_at_any_width(self) -> None:
		"""No digit bound on the port, for issue #119's reason.

		A second, arbitrary width bound is what #119 cost: the padding cap
		lived in two places and one of them was one digit short. The port is
		not bounded here either — whether the head is an address is settled
		by the parser, at any port width.
		"""
		for port in (0, 22, 443, 65535, 999999):
			with self.subTest(port=port):
				text = f"host {with_port(LAB_IPV6_FULL, port)}"
				self.assertEqual(len(scanner.scan_text("f.md", text)), 1, text)


class MultiGroupPortRetryTests(unittest.TestCase):
	"""Issue #131 — the single retry in `_ipv6_address_of()` closes ONE
	trailing all-digit group; a candidate carrying two still scanned clean.

	The round-3 reviewer judged the original single-group escape a real
	blocker (PR #115 round 2, finding 1 — see `UnbracketedPortTests` above)
	but judged the multi-group case non-blocking: every shape the gate's own
	threat model names (netstat, log lines, inventory exports, URLs) produces
	exactly one trailing numeric group, so this is hardening, not an
	emergency. The fix keeps that framing — it bounds the NUMBER of trailing
	digit groups retried (`_MAX_SWALLOWED_GROUPS`, three) and bounds no
	group's WIDTH at all, which stays deliberately unlimited for the same
	#119 reason `UnbracketedPortTests` already pins.

	The cap arrived in PR #138 round 1, and the REASON is disclosability, not
	false positives: shipping the loop UNBOUNDED made the disclosed #118
	false-positive class unbounded in record length, which is a class no test
	can enumerate and no sentence in docs/testing.md can state truthfully.
	A pin named `..._are_still_only_these` cannot bound an infinite set.

	Three rather than two is a priced trade, not a free win — an earlier
	revision of this class said otherwise on a corpus that happened to
	contain no 11-group record. Three buys one further leak shape (three
	trailing numeric groups, which no producer has been shown to emit) and
	costs three further false-positive families, all 11-group records. The
	cost of stopping at three is disclosed by
	`test_four_trailing_all_digit_groups_are_a_disclosed_residual`; the
	measurement is in `_MAX_SWALLOWED_GROUPS`.
	"""

	def test_two_trailing_all_digit_groups_are_both_retried(self) -> None:
		"""The issue's own repro: a bare port plus a second numeric group."""
		text = f"vcenter at {LAB_IPV6_FULL}:443:8443"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IPV6_FULL, findings[0])

	def test_a_zero_group_ahead_of_the_port_is_also_retried(self) -> None:
		"""The issue's second repro: an all-zero group, still just digits."""
		text = f"vcenter at {LAB_IPV6_FULL}:0:443"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IPV6_FULL, findings[0])

	def test_two_groups_retried_on_a_second_prefix_and_port_width(self) -> None:
		"""Not a one-fixture fluke: a different address, port, and group count."""
		address = ipv6("fe80", "0000", "0000", "0000", "1a2b", "3c4d", "5e6f", "7a8b")
		text = f"manager {address}:22:2222"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(address, findings[0])

	def test_the_bound_is_three_groups_and_is_stated_as_a_number(self) -> None:
		"""The cap is a named constant, not a shape the tests infer.

		PR #138 round 1, finding 1: an UNCAPPED loop makes the disclosed #118
		false-positive class unbounded in record length, which is not a class
		any test can enumerate and not a sentence docs/testing.md can state
		truthfully. The cap is what makes both possible, so the number itself
		is pinned here — a future change to it has to come through this test
		and through `_MAX_SWALLOWED_GROUPS`'s own justification comment,
		which PRICES the choice (one further leak shape bought for three
		further 11-group false-positive families) rather than asserting it.
		"""
		self.assertEqual(scanner._MAX_SWALLOWED_GROUPS, 3)

	def test_three_trailing_all_digit_groups_are_all_retried(self) -> None:
		"""One group past the deepest shape issue #131 itself measured.

		The bound is three, so this closes. It is also the shape PR #138's
		round-1 reviewer independently verified as closed, kept closed
		deliberately rather than by accident — `_MAX_SWALLOWED_GROUPS`
		records what buying it costs on the false-positive side, which is
		every 11-group colon-separated record.
		"""
		for text in (
			f"vcenter at {LAB_IPV6_FULL}:1:2:3",
			f"vcenter at {LAB_IPV6_FULL}:443:8443:9443",
		):
			with self.subTest(text=text):
				findings = scanner.scan_text("f.md", text)
				self.assertEqual(len(findings), 1, (text, findings))
				self.assertIn(LAB_IPV6_FULL, findings[0])

	def test_four_trailing_all_digit_groups_are_a_disclosed_residual(self) -> None:
		"""What the bound costs, pinned rather than left silent.

		Four trailing all-digit groups exhaust `_MAX_SWALLOWED_GROUPS`, so
		the candidate is declared a non-address and the line scans clean.
		This is the second of the loop's two stopping conditions (the first
		is the glued letter below), and it is a deliberate trade, measured
		in both directions:

		  - little gained by going further: no producer in this gate's threat
		    model (netstat, log lines, inventory exports, CKL/HDF, URLs) has
		    ever been shown to emit even a three-group shape, let alone four.
		    Issue #131's own four measured escapes all close at two.
		  - something real given up on the other side: each additional group
		    of slack widens the #118 false-positive class by one more group
		    of colon-separated record, without bound. Going from three to
		    four would add every 12-group record to the class, exactly as
		    going from two to three added every 11-group one. See
		    `_MAX_SWALLOWED_GROUPS` for the corpus measurement.

		Pinned here so a future change either closes it deliberately or has
		to come and edit this test — the same treatment every other residual
		in this file gets.
		"""
		for text in (
			f"vcenter at {LAB_IPV6_FULL}:1:2:3:4",
			f"vcenter at {LAB_IPV6_FULL}:443:8443:9443:9444",
		):
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)

	def test_the_bound_does_not_bound_any_group_s_width(self) -> None:
		"""#119's lesson, re-checked against the new bound.

		A group-COUNT bound and a group-WIDTH bound are different axes, and
		the second is the one #119 cost. Both an absurdly wide single port
		and an absurdly wide pair still resolve.
		"""
		wide = "9" * 30
		for text in (
			f"vcenter at {LAB_IPV6_FULL}:{wide}",
			f"vcenter at {LAB_IPV6_FULL}:12345678901234:{wide}",
		):
			with self.subTest(text=text):
				findings = scanner.scan_text("f.md", text)
				self.assertEqual(len(findings), 1, (text, findings))
				self.assertIn(LAB_IPV6_FULL, findings[0])

	def test_compressed_address_with_two_trailing_groups_is_unaffected(self) -> None:
		"""The compressed spelling already absorbed a single port as a legal
		group (UnbracketedPortTests); a second group does not change that —
		it is still short of the group count that would make the loop
		necessary, so this is a control, not a new behaviour.
		"""
		text = f"host {LAB_IPV6}:443:1"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)

	def test_the_controls_from_131_still_pass(self) -> None:
		"""Every shape #131 explicitly did NOT change: single-group cases."""
		for text in (
			f"vcenter at {LAB_IPV6_FULL}:443",
			f"vcenter at {with_port(LAB_IPV6, 8443)}",
			f"vcenter at {bracketed_port(LAB_IPV6_FULL, 443)}",
		):
			with self.subTest(text=text):
				self.assertEqual(len(scanner.scan_text("f.md", text)), 1, text)

	def test_the_loop_still_does_not_invent_an_address(self) -> None:
		"""UnbracketedPortTests' non-address corpus, extended with runs long
		enough to exercise more than one loop iteration — the loop must still
		terminate at None rather than eventually stumbling onto a valid parse.
		"""
		for text in (
			"elapsed 01:02:03:04:05",
			"ports 8443:8443:8443",
			"cron 0 2 * * * run:12:34:56",
			"src f.py:123:456:789: warning",
		):
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)

	def test_a_glued_letter_on_the_final_group_remains_undetected(self) -> None:
		"""The disclosed residual (issue #131's Root Cause paragraph).

		A trailing group carrying a non-digit character (`443a`) fails the
		all-digit test on its very FIRST retry attempt, so the loop this
		issue adds never starts stripping it — this is a different shape
		from "two numeric groups", not a partially-fixed instance of it, and
		closing it would need a materially different check (is this tail
		alphanumeric-with-a-leading-run-of-digits, rather than is it all
		digits). No shape in the gate's threat model (netstat, log lines,
		inventory exports, CKL/HDF) produces a glued-letter port, so this
		stays open by disclosure rather than by omission — pinned here so a
		future change to the retry either closes it deliberately or has to
		come and edit this test, matching how this file already treats every
		other residual (`test_the_known_residual_false_positives_are_still_
		only_these`, the single-sided dash cases, ...).
		"""
		text = f"vcenter at {LAB_IPV6_FULL}:443a"
		self.assertEqual(scanner.scan_text("f.md", text), [], text)


class MidRunMatchTests(unittest.TestCase):
	"""PR #115 round 2, finding 3 — a match that starts inside a longer run.

	`IPV6_RE`'s leading guard rejects a match glued to an alphanumeric. A
	rejected START is not a rejected LINE: the engine restarts further along,
	inside the same hex/colon run, and reports whatever tail still parses.
	The comment justifying the guard change asserted this could not happen.
	It did: a word whose tail is hex digits, written straight onto the
	sanctioned documentation prefix, reported the tail of that prefix as a
	finding — a false positive introduced by the round-2 fix itself.
	"""

	GLUED_PREFIXES = ("see", "the", "note", "code", "X", "ref")

	def test_a_word_glued_to_the_documentation_prefix_is_not_a_finding(self) -> None:
		for prefix in self.GLUED_PREFIXES:
			for suffix in ("", " is documentation", "."):
				text = f"{prefix}{OK_IPV6_DOC}{suffix}"
				with self.subTest(text=text):
					self.assertEqual(scanner.scan_text("f.md", text), [], text)

	def test_a_word_glued_to_a_lab_literal_reports_the_whole_address(self) -> None:
		"""Re-anchoring relocates the finding; it does not drop it.

		The pre-fix behaviour reported a FRAGMENT of the address here, which
		is both a wrong answer and the same defect seen from the other side.
		"""
		for prefix in self.GLUED_PREFIXES:
			text = f"{prefix}{LAB_IPV6}"
			with self.subTest(text=text):
				findings = scanner.scan_text("f.md", text)
				self.assertEqual(len(findings), 1, findings)
				self.assertIn(LAB_IPV6, findings[0])

	def test_a_delimited_label_before_the_address_still_flags(self) -> None:
		"""`label:<address>` stays a finding whatever the label ends with.

		A label ending in a hex digit (`esxi01`, `vmnic5`) and one ending in a
		letter (`addr`) are the same shape to the regex and must stay the same
		shape to the gate — the address is delimited, not glued.
		"""
		for label in ("addr", "host", "esxi01", "vmnic5", "eth0", "node7"):
			for address in (LAB_IPV6, LAB_IPV6_FULL):
				text = f"{label}:{address}"
				with self.subTest(text=text):
					findings = scanner.scan_text("f.md", text)
					self.assertEqual(len(findings), 1, (text, findings))
					self.assertIn(address, findings[0])

	def test_a_numeric_hostname_suffix_does_not_widen_into_the_reported_token(
		self,
	) -> None:
		"""Issue #133: `_widest_address_start()` must not accept a span that
		only parses because `_ipv6_address_of()`'s port retry drops one of the
		address's OWN trailing groups.

		`node99:<full address>` re-anchored onto `de99:<full address minus its
		own last group>` before this fix — `de99` is the tail of the hostname
		`node99`, not part of any address on the line, and the "address" named
		in the finding was not a string that actually appears there. The
		FULLY-EXPANDED spelling is what makes this decidable: it needs
		exactly 8 groups, so prepending a 9th (the hostname's numeric tail)
		can ONLY parse via the retry that treats the address's own final group
		as a discardable "port" — which is exactly what `_strict_ipv6_literal`
		(no port retry) now refuses. Asserted as EXACT equality, not
		`assertIn`, because a widened-too-far span is a real address SUBSTRING
		of the correct report and `assertIn` would not have caught the bug.
		"""
		for label in ("esxi01", "esxi1", "node99", "vmnic5", "eth0"):
			text = f"{label}:{LAB_IPV6_FULL}"
			with self.subTest(text=text):
				findings = scanner.scan_text("f.md", text)
				self.assertEqual(len(findings), 1, (text, findings))
				reported = findings[0].rsplit(": ", 1)[1]
				self.assertEqual(reported, LAB_IPV6_FULL, (text, findings))

	def test_a_numeric_hostname_suffix_before_a_compressed_address_is_a_disclosed_residual(
		self,
	) -> None:
		"""What issue #133's fix does NOT close, pinned rather than left silent.

		A COMPRESSED address has slack a fully-expanded one does not: `"::"`
		can absorb an extra explicit group without exceeding 8, so a
		colon-delimited numeric hostname suffix (`esxi01:`, `node99:`, ...)
		parses as a wider — different, but ALSO syntactically valid — address
		with no port retry involved at all. This is not the #133 mechanism
		(there is no port anywhere in these lines) and closing it would need
		telling "a foreign, colon-delimited label" apart from "more of the
		same address recovered after a guard rejection" using nothing but the
		surrounding characters — which is genuinely impossible in general:
		`X2001:db8::1` (MidRunMatchTests, doc-prefix recovery, wanted) and an
		`esxi01:`-prefixed lab literal (this test, NOT wanted — see the cases
		below, assembled rather than spelled out here for the same no-literal
		reason this module's docstring states) are the identical shape one
		level up — a single clean group-plus-colon sitting directly
		before an already-independently-valid address — and widest-wins
		correctly favours the FIRST case, which is why it cannot also refuse
		the second. The line is still correctly flagged either way (the
		invariant re-anchoring exists to protect); only the reported token's
		span is imprecise here. Kept pinned, not silent, so a future change
		either closes it deliberately or has to come edit this test.
		"""
		second = ipv6("fd00", "1a2b", "3c4d", "", "1")
		cases = {
			f"esxi01:{LAB_IPV6}": "01:" + LAB_IPV6,
			f"esxi1:{LAB_IPV6}": "1:" + LAB_IPV6,
			f"node99:{LAB_IPV6}": "de99:" + LAB_IPV6,
			f"vmnic5:{second}": "c5:" + second,
		}
		for text, reported_today in cases.items():
			with self.subTest(text=text):
				findings = scanner.scan_text("f.md", text)
				self.assertEqual(len(findings), 1, (text, findings))
				actual = findings[0].rsplit(": ", 1)[1]
				self.assertEqual(actual, reported_today, (text, findings))
				# The real address is still a substring of what is reported —
				# the line is not silent, only wider than it should be.
				address = text.split(":", 1)[1]
				self.assertIn(address, actual, (text, findings))

	def test_scope_resolution_syntax_is_not_re_anchored_into_an_address(self) -> None:
		"""Re-anchoring must not CREATE a finding, only move or drop one.

		`word::hexword` is C++/PowerShell/Rust scope-resolution syntax, and
		the run it sits in ends in something `ipaddress` accepts. It is only
		quiet because re-anchoring runs after the fragment has already been
		judged a finding on its own — reverse that order and every one of
		these lines becomes a false positive.
		"""
		for text in (
			"std::cafe",
			"Color::Fade",
			"Result::Bad",
			"ns::deadbeef",
			"[System.Math]::Abs(1)",
			"std::vector<int> v;",
			"a::before { content: none; }",
		):
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)


class ImpossibilityClaimTests(unittest.TestCase):
	"""Every "this cannot happen" in the scanner, executed instead of read.

	Three claims of impossibility in `scan_repo_specific.py` have now been
	false, in three consecutive rounds of one review: a trailing "not a
	colon" guard called unreachable (it was reachable, and cost a finding), a
	leading-guard change called unable to resurrect a mid-run match (it did,
	and cost a false positive), and — found by auditing the comment that
	corrected the first one — the claim that a greedy hex/colon run can never
	be followed by a colon (backtracking says otherwise). Prose cannot carry
	this kind of statement safely, so each surviving one is pinned here.
	"""

	def test_a_variable_width_lookbehind_is_rejected_by_the_engine(self) -> None:
		"""Why the dash rule is code and not a lookaround.

		`IPV4_RE` cannot decide dash adjacency itself: telling a range from a
		build suffix means looking across the dash at a whole dotted-quad,
		and a lookbehind that wide is variable-width, which Python's `re`
		refuses to compile. A hard engine limit, not a preference.
		"""
		with self.assertRaises(re.error):
			re.compile(r"(?<!(?:\d+\.){3}\d+-)x")

	def test_a_bare_hex_run_can_end_with_a_colon_ahead_of_it(self) -> None:
		"""The corrected half of the "no trailing not-a-colon guard" comment.

		The class is greedy, but the TRAILING GUARDS BACKTRACK: given an
		address, a port and then a letter, the engine gives characters back
		until the guards are satisfied, and what satisfies them is the
		address — with the port's colon immediately after the match. A "not a
		colon" guard would have rejected these too, so the reason for leaving
		it out is broader than the mapped-form case that first exposed it.
		"""
		observed = 0
		for text in (
			f"{with_port(LAB_IPV6, 443)}x",
			f"{with_port(LAB_IPV6_FULL, 8443)}z",
			f"{LAB_IPV6}:x",
		):
			for match in scanner.IPV6_RE.finditer(text):
				if text[match.end():match.end() + 1] == ":":
					observed += 1
		self.assertEqual(observed, 3, "expected every case to end before a colon")

	def test_every_neighbour_reaches_the_dash_helper(self) -> None:
		"""The corrected `_dash_glues_to_non_address` docstring.

		It used to claim that every non-dash neighbour "never reaches here".
		They all reach it; the function simply has nothing to add, because
		`IPV4_RE`'s own lookarounds have already decided them. Enumerated
		over every printable neighbour rather than argued.
		"""
		reached = set()
		for neighbour in (chr(code) for code in range(33, 127)):
			line = f"{neighbour}{LAB_IP}"
			for match in scanner.IPV4_RE.finditer(line):
				reached.add(neighbour)
				scanner._dash_glues_to_non_address(line, match.start(), match.end())
		self.assertIn("-", reached, "the dash case must reach the helper")
		self.assertIn(":", reached, "a delimiter colon reaches it too")
		self.assertIn("_", reached, "the deliberately-allowed underscore reaches it")
		self.assertNotIn("a", reached, "an alphanumeric is rejected by the regex")

	def test_fqdn_matches_never_contain_an_underscore(self) -> None:
		"""Why only FQDN_RE's LEADING guard was narrowed to alphanumerics.

		The comment's reasoning is that a DNS label cannot contain `_`, so an
		underscore before a match is a separator rather than a continuation.
		That is a property of the pattern, so it is checked as one.
		"""
		for text in (
			f"VCENTER_{LAB_FQDN}",
			f"prefix_{LAB_FQDN}_suffix",
			f"{LAB_FQDN}_suffix",
			f"_{LAB_FQDN}",
		):
			with self.subTest(text=text):
				for match in scanner.FQDN_RE.finditer(text):
					self.assertNotIn("_", match.group(0), text)

	def test_trimming_never_turns_an_address_into_a_non_address(self) -> None:
		"""`_trim_delimiter_colons` claims safety in one direction only.

		The claim is that trimming can never LOSE a finding — it can turn an
		unparseable span into an address (the point of it), never the
		reverse. Exhausted over every generated candidate rather than
		asserted: 4 cores x 6 leading x 6 trailing x 3 zone/port tails.
		"""
		cores = (LAB_IPV6, LAB_IPV6_FULL, OK_IPV6_LOOPBACK, OK_IPV6_UNSPECIFIED)
		edges = ("", ":", "::", ":::", "0", "f")
		tails = ("", "%eth0", ":443")
		checked = 0
		for core in cores:
			for lead in edges:
				for trail in edges:
					for tail in tails:
						candidate = f"{lead}{core}{trail}{tail}"
						checked += 1
						before = scanner._ipv6_address_of(candidate)
						after = scanner._ipv6_address_of(
							scanner._trim_delimiter_colons(candidate)
						)
						if before is not None:
							self.assertIsNotNone(after, candidate)
		self.assertEqual(checked, 432)

	def test_the_port_retry_only_ever_returns_what_the_parser_accepts(self) -> None:
		"""`_ipv6_address_of` claims the retry cannot manufacture an address.

		Whatever it returns must be a literal the strict parser accepted, on
		the candidate itself or on the candidate with SOME number of trailing
		all-digit groups removed (issue #131 turned the single retry into a
		loop, so "one trailing group" is no longer the whole story — every
		successively-shortened head is a legitimate source of the answer, not
		just the first one).
		"""
		for candidate in (
			LAB_IPV6,
			with_port(LAB_IPV6_FULL, 443),
			with_port(LAB_IPV6, 8443),
			"01:02:03",
			with_port(EUI64_SHAPED, 22),
			f"{LAB_IPV6_FULL}:443:443",
			f"{LAB_IPV6_FULL}:0:443",
		):
			with self.subTest(candidate=candidate):
				resolved = scanner._ipv6_address_of(candidate)
				if resolved is None:
					continue
				# Reachable heads: the candidate itself, plus every head
				# obtained by stripping trailing ALL-DIGIT groups one at a
				# time — a transcription independent of the implementation,
				# so it stops at the same place the loop must (a separator
				# with a non-digit tail, or no separator left).
				heads = [candidate]
				head = candidate
				while True:
					head, separator, tail = head.rpartition(":")
					if not separator or not tail.isdigit():
						break
					heads.append(head)
				accepted = {
					str(ipaddress.IPv6Address(text)) for text in heads if _parses(text)
				}
				self.assertIn(str(resolved), accepted, candidate)


class FalsePositiveCorpusTests(unittest.TestCase):
	"""The lines this gate must stay silent on, as an executable corpus.

	Kept in the suite rather than run by hand in a PR body: a corpus that
	lives in a review comment is re-derived (differently) by whoever comes
	next, and the round-2 widening of what IPv6 matches is exactly the kind
	of change that needs to be re-measured against the same list every time.
	Every entry is a shape seen in this repo or in the sibling repos' output.
	"""

	CORPUS = (
		# timestamps and durations
		"Scan started at 04:34:01 local time",
		"elapsed 01:02:03.456",
		"duration 00:00:07",
		"stamp 2026-08-03T10:28:05Z",
		# hardware and hashes
		"NIC de:ad:be:ef:ca:fe",
		"NIC aa-bb-cc-dd-ee-ff",
		"image sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
		"uuid 123e4567-e89b-12d3-a456-426614174000",
		# code and markup
		"colour: #aabbcc;",
		"a::before { content: none; }",
		"std::vector<int> v;",
		"[System.Math]::Abs(1)",
		"SELECT id::text FROM t",
		"xpath //child::node()",
		# paths, ports, binds
		r"path C:\Users\example\file.txt",
		"ports 8443:8443",
		"ports 18443:8443",
		"listen 0.0.0.0:8443",
		"bind 127.0.0.1:5432",
		# sanctioned addresses, in both spellings and with ports
		"doc 2001:db8::1",
		"doc 2001:0db8:0000:0000:0000:0000:0000:0001",
		"lo ::1",
		"lo 0:0:0:0:0:0:0:1",
		"any ::",
		"any 0:0:0:0:0:0:0:0",
		"lo [::1]:8443",
		"test 192.0.2.10 and 198.51.100.7 and 203.0.113.9",
		"padded 192.000.002.010",
		# #89's build/version shapes
		"vcf-download-tool-9.0.0.0-24089201.tar.gz",
		"version: '8.18.0.4'",
		"--version 9.0.0.0",
		"app.version=9.0.0.0",
		# repo conventions
		"host esxi-01.example.internal",
		"dotnet_naming_rule.local_functions_should_be_pascalcase",
		"mail user@example.internal",
	)

	def test_the_corpus_is_silent(self) -> None:
		for line in self.CORPUS:
			with self.subTest(line=line):
				self.assertEqual(scanner.scan_text("f.md", line), [], line)

	def test_the_corpus_is_scanned_as_one_file_too(self) -> None:
		"""Line-by-line silence is not the same as whole-file silence."""
		self.assertEqual(scanner.scan_text("f.md", "\n".join(self.CORPUS)), [])

	def test_the_known_residual_false_positives_are_still_only_these(self) -> None:
		"""The lines that DO fire and should not. Pinned, not hidden — and
		the set is BOUNDED, which is what this test's name claims and what
		PR #138 round 1, finding 1 found it no longer doing.

		All are disclosed in docs/testing.md. They are here so a future
		change either keeps them exactly as they are or has to come and edit
		this test — which is the point at which someone has to think about
		them again.

		1. A run of colon-separated hex groups that happens to validate as a
		   literal (issue #118). An 8-group EUI-64-style run already fired
		   before the #131 work. The swallowed-port retry widens that class
		   by the number of groups the retry is allowed to strip: a run of 9,
		   10 or 11 groups whose trailing groups are all digits now resolves
		   to the 8-group address in front of them. `_MAX_SWALLOWED_GROUPS`
		   is what makes "9 to 11" a range rather than "and upwards" — an
		   UNCAPPED loop, which is what this branch carried into round 1,
		   made the class unbounded in record length and made this test's
		   name false.
		2. The unbracketed `<sanctioned address>:<port>` ambiguity, argued in
		   UnbracketedPortTests.

		The negative half is the half that does the bounding: a 12-group run
		is where the class STOPS, and it is asserted here rather than only
		described. Raise the cap and this test fails, in the direction that
		makes someone re-read the disclosure. Lower it and the firing half
		fails, for the same reason.

		The 11-group rows are the ones the cap-2-to-cap-3 change ADDED to
		this class, and they are named rather than folded into a range,
		because they are what that change cost.

		They are also ASSEMBLED rather than written out, and that is not
		style: an 11-group all-digit-tailed run IS the new false-positive
		class, so spelling one out as a literal makes this very file trip the
		gate it tests. That is measured, not anticipated — writing them as
		literals took the full-tree scan from clean to two findings, in this
		file, on the first run after the cap moved. It is the same no-literal
		reason this module's docstring gives, arriving from a new direction,
		and it is the sharpest available demonstration that raising the bound
		has a real cost rather than a theoretical one.
		"""
		eleven_numeric = "counters " + ":".join(str(n) for n in range(1, 12))
		eleven_fingerprint = "SHA1 Fingerprint=" + ":".join(
			("A1", "B2", "C3", "D4", "E5", "F6", "07", "18", "29", "30", "41")
		)
		self.assertEqual(eleven_numeric.split()[1].count(":"), 10, eleven_numeric)
		self.assertEqual(eleven_fingerprint.count(":"), 10, eleven_fingerprint)
		for line in (
			f"cols {EUI64_SHAPED}",
			f"cols {with_port(EUI64_SHAPED, 22)}",
			f"cols {EUI64_SHAPED}:22",
			f"cols {EUI64_SHAPED}:22:2222",
			# 11 groups — added to the class by the cap 2 -> 3 change.
			f"cols {EUI64_SHAPED}:22:2222:1",
			eleven_numeric,
			eleven_fingerprint,
			f"lo {with_port(OK_IPV6_LOOPBACK, 443)}",
		):
			with self.subTest(line=line, direction="fires"):
				self.assertEqual(len(scanner.scan_text("f.md", line)), 1, line)
		# Where the class stops: 12 colon groups and beyond. Several of these
		# are the shapes the round-1 reviewer's corpus turned up as
		# newly-firing under the uncapped loop.
		for line in (
			f"cols {EUI64_SHAPED}:22:2222:1:2",
			"fields 1:2:3:4:5:6:7:8:9:10:11:12",
			"fields 1:2:3:4:5:6:7:8:9:10:11:12:13:14:15:16",
			"eui 00:1a:2b:ff:fe:3c:4d:5e:22:2222:1:2",
			"SHA1 Fingerprint=A1:B2:C3:D4:E5:F6:07:18:29:30:41:52:63:74:85:96:07:18:29:30",
		):
			with self.subTest(line=line, direction="silent"):
				self.assertEqual(scanner.scan_text("f.md", line), [], line)


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


class BackslashSeparatorEvasionTests(unittest.TestCase):
	"""Issue #137: a backslash before a separator must not defeat detection.

	`IPV4_RE`, `FQDN_RE` and `IPV6_RE` all anchor on `.` or `:` sitting
	directly between two components. A backslash inserted immediately before
	that separator split the candidate into fragments too short to satisfy
	any detector's structural floor, so the match never formed at all —
	measured as 0 findings on `10\\.44\\.12\\.7`,
	`vcenter-prod\\.fictionallab\\.corp\\.local` and
	`fd00\\:1a2b\\:3c4d\\:\\:7` before this fix. The fix normalizes the
	escape out of the line before any detector runs (`_unescape_separators`)
	rather than adding a fourth alternative to each regex.
	"""

	def test_backslash_before_dot_no_longer_hides_an_ipv4_literal(self) -> None:
		escaped = backslash_escape_separators(LAB_IP)
		findings = scanner.scan_text("f.md", f"host {escaped}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn("non-RFC-5737 IP address literal", findings[0])
		self.assertIn(LAB_IP, findings[0])

	def test_backslash_before_dot_no_longer_hides_a_lab_fqdn(self) -> None:
		escaped = backslash_escape_separators(LAB_FQDN)
		findings = scanner.scan_text("f.md", f"host {escaped}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn("lab-style FQDN", findings[0])
		self.assertIn(LAB_FQDN, findings[0])

	def test_backslash_before_colon_no_longer_hides_an_ipv6_literal(self) -> None:
		escaped = backslash_escape_separators(LAB_IPV6)
		findings = scanner.scan_text("f.md", f"host {escaped}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn("possible IPv6 address literal", findings[0])
		self.assertIn(LAB_IPV6, findings[0])

	def test_fully_expanded_ipv6_survives_backslash_escaping_too(self) -> None:
		escaped = backslash_escape_separators(LAB_IPV6_FULL)
		findings = scanner.scan_text("f.md", f"host {escaped}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn("possible IPv6 address literal", findings[0])

	def test_sanctioned_addresses_stay_silent_even_when_backslash_escaped(self) -> None:
		"""The normalization pass must not manufacture new findings either."""
		for addr in (
			quad(192, 0, 2, 1),
			quad(198, 51, 100, 1),
			quad(203, 0, 113, 1),
			quad(127, 0, 0, 1),
			OK_FQDN,
			OK_IPV6_DOC,
			OK_IPV6_LOOPBACK,
		):
			with self.subTest(addr=addr):
				escaped = backslash_escape_separators(addr)
				self.assertEqual(scanner.scan_text("f.md", f"see {escaped}"), [])

	def test_ordinary_backslash_conventions_are_unaffected(self) -> None:
		"""A backslash with no separator immediately after it is left alone.

		Windows paths, shell-quoted output and regex source all use `\\` as
		an escape character with no `.`/`:` glued to it; normalizing every
		backslash away (rather than only the ones directly before a
		separator) would risk inventing findings out of those instead of
		closing this issue's false negative.
		"""
		for line in (
			r"path C:\Users\example\file.txt",
			r"regex \d+\.\d+ matches a version number",
			r"escaped newline in a log line\n",
		):
			with self.subTest(line=line):
				self.assertEqual(scanner.scan_text("f.md", line), [])

	def test_version_suppression_still_applies_through_backslash_escaping(self) -> None:
		"""The #89 version-key suppression must survive the normalization pass."""
		escaped = backslash_escape_separators(quad(9, 0, 0, 0))
		self.assertEqual(
			scanner.scan_text("f.md", f"version: '{escaped}'"), []
		)

	def test_dash_range_detection_still_applies_through_backslash_escaping(self) -> None:
		"""The #111 range fix must survive the normalization pass too."""
		start = backslash_escape_separators(RANGE_START)
		end = backslash_escape_separators(LAB_IP)
		findings = scanner.scan_text("f.md", f"range {start}-{end}")
		self.assertEqual(len(findings), 2, findings)

	def test_multiple_finding_classes_still_all_fire_when_escaped(self) -> None:
		text = (
			f"{backslash_escape_separators(LAB_FQDN)} at "
			f"{backslash_escape_separators(LAB_IP)}"
		)
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 2, findings)


class FileHandlingTests(unittest.TestCase):
	"""scan_file's IO behaviour: skip cleanly, never raise."""

	def setUp(self) -> None:
		import tempfile

		self.tmp = tempfile.TemporaryDirectory()
		self.addCleanup(self.tmp.cleanup)
		self.root = Path(self.tmp.name)

	def test_known_safe_binary_extensions_are_still_scan_file_skipped(self) -> None:
		"""KNOWN_SAFE_BINARY_EXTENSIONS is scan_file's skip list now, not the
		old SKIPPED_EXTENSIONS — but the behaviour for the types it still
		names (icons, fonts, wasm) is unchanged: scan_file() never reads
		their bytes as text."""
		for ext in (".png", ".woff2"):
			with self.subTest(ext=ext):
				path = self.root / f"asset{ext}"
				path.write_bytes(b"\x89PNG\r\n\x1a\n" + b"\x00" * 8)
				self.assertEqual(scanner.scan_file(path, rel=path.name), [])

	def test_undecodable_file_is_skipped_without_raising(self) -> None:
		"""scan_file() itself never raises on undecodable content — the loud
		refusal for an un-inspectable *tracked* file lives in main() via
		_find_uninspectable_tracked_files(), not in scan_file()."""
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


class UninspectableFileTests(unittest.TestCase):
	"""Issue #101: an un-inspectable tracked file must never pass as clean.

	Covers the acceptance criteria directly: a .pem/.key with a fake key
	body is caught (refused, loudly, before any detector runs), a .zip/.gz
	is refused the same way, and a genuinely-safe text file still passes
	untouched. All fixtures are invented — a fake PEM body, not a real key —
	per this repo's own sanitization mandate.
	"""

	def setUp(self) -> None:
		import tempfile

		self.tmp = tempfile.TemporaryDirectory()
		self.addCleanup(self.tmp.cleanup)
		self.root = Path(self.tmp.name)
		# _find_uninspectable_tracked_files() reports paths relative to
		# scanner.REPO_ROOT, so tests need it pointed at the tempdir — same
		# pattern ExitCodeTests uses below.
		self._saved_root = scanner.REPO_ROOT
		self.addCleanup(setattr, scanner, "REPO_ROOT", self._saved_root)
		scanner.REPO_ROOT = self.root

	def _write_binary(self, name: str, content: bytes) -> Path:
		path = self.root / name
		path.write_bytes(content)
		return path

	def test_undecodable_pem_is_flagged_uninspectable(self) -> None:
		"""A .pem whose bytes are not valid UTF-8 (a real DER-encoded or
		otherwise binary cert/key blob) cannot be read as text at all, so it
		must be refused rather than silently passed."""
		path = self._write_binary(
			"fixture.pem", b"\x30\x82\x01\x0a\x02\x82\x01\x01\x00\xc9\xfe not-utf8 \xff"
		)
		found = scanner._find_uninspectable_tracked_files([path])
		self.assertEqual(found, ["fixture.pem"])

	def test_utf8_pem_with_invented_body_is_flagged_uninspectable(self) -> None:
		"""A .pem that HAPPENS to decode as UTF-8 (an invented, fake PEM body
		here — never a real key) is still refused: .pem is not in
		KNOWN_SAFE_BINARY_EXTENSIONS, so its extension alone is enough to
		route it to the loud refusal path regardless of whether this
		particular instance decodes."""
		# Armor delimiters are assembled from fragments so the full PEM
		# begin/end marker lines never appear as literals in this source file —
		# otherwise gitleaks' private-key rule (which matches on the armor
		# alone, ignoring the body) would flag this test fixture and fail the
		# very sanitize gate this file hardens. The runtime string is still a
		# UTF-8-decodable, PEM-armor-shaped, wholly invented body.
		marker = "-----"
		fake_pem = (
			f"{marker}BEGIN PRIVATE " + f"KEY{marker}\n"
			"MIIBINVENTEDFAKEKEYMATERIALFORTESTINGPURPOSESONLYNOTREAL==\n"
			f"{marker}END PRIVATE " + f"KEY{marker}\n"
		)
		path = self.root / "fixture.pem"
		path.write_text(fake_pem, encoding="utf-8")
		found = scanner._find_uninspectable_tracked_files([path])
		self.assertEqual(found, ["fixture.pem"])

	def test_key_pfx_p12_are_all_flagged_uninspectable(self) -> None:
		for ext in (".key", ".pfx", ".p12"):
			with self.subTest(ext=ext):
				path = self._write_binary(f"fixture{ext}", b"\x00\x01binary\xffcontent")
				found = scanner._find_uninspectable_tracked_files([path])
				self.assertEqual(found, [f"fixture{ext}"])

	def test_zip_and_gz_are_flagged_uninspectable(self) -> None:
		# Real gzip/zip magic bytes, but arbitrary invented "payload" after
		# them — no real archive contents, just enough to be non-UTF-8.
		gzip_magic = b"\x1f\x8b\x08\x00" + b"\x00" * 4 + b"invented-payload\xff"
		zip_magic = b"PK\x03\x04" + b"\x00" * 4 + b"invented-payload\xff"
		for name, content in (("fixture.gz", gzip_magic), ("fixture.zip", zip_magic)):
			with self.subTest(name=name):
				path = self._write_binary(name, content)
				found = scanner._find_uninspectable_tracked_files([path])
				self.assertEqual(found, [name])

	def test_known_safe_binary_extensions_are_not_flagged(self) -> None:
		"""The icon/font/wasm types this repo actually ships stay skipped,
		not escalated — they are the KNOWN_SAFE_BINARY_EXTENSIONS bucket,
		not the un-inspectable one."""
		png_bytes = b"\x89PNG\r\n\x1a\n" + b"\x00" * 16
		woff_bytes = b"wOF2" + b"\x00" * 16
		for name, content in (("icon.png", png_bytes), ("font.woff2", woff_bytes)):
			with self.subTest(name=name):
				path = self._write_binary(name, content)
				found = scanner._find_uninspectable_tracked_files([path])
				self.assertEqual(found, [])

	def test_genuinely_safe_text_file_still_passes(self) -> None:
		"""A normal, clean, UTF-8 text file — regardless of extension — is
		never flagged as uninspectable, and still scans clean end to end."""
		path = self.root / "notes.md"
		path.write_text(
			f"See {fqdn('esxi-01', 'example', 'internal')} for details.",
			encoding="utf-8",
		)
		self.assertEqual(scanner._find_uninspectable_tracked_files([path]), [])
		self.assertEqual(scanner.scan_file(path, rel="notes.md"), [])

	def test_a_text_file_with_an_unrecognised_extension_is_still_scanned(self) -> None:
		"""An extension nobody has taught this scanner about (neither
		KNOWN_SAFE_BINARY_EXTENSIONS nor a historically-skipped one) is not
		itself grounds for refusal if the content decodes as text — only
		genuinely undecodable content, or a content type this scanner cannot
		vouch for by name, is refused. Here: decodable content, so it is
		scanned normally rather than refused."""
		path = self.root / f"weird{'.newformat'}"
		path.write_text(f"host {LAB_IP}", encoding="utf-8")
		self.assertEqual(scanner._find_uninspectable_tracked_files([path]), [])
		findings = scanner.scan_file(path, rel=path.name)
		self.assertEqual(len(findings), 1, findings)


class MainRefusesUninspectableFilesTests(unittest.TestCase):
	"""main()'s exit-code contract for the uninspectable-file path."""

	@staticmethod
	def _run_main() -> int:
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

	def _track_binary(self, name: str, content: bytes) -> None:
		path = self.root / name
		path.write_bytes(content)
		files = sorted(p for p in self.root.iterdir() if p.is_file())
		scanner.list_tracked_files = lambda: files

	def test_an_uninspectable_tracked_pem_fails_the_run(self) -> None:
		self._track_binary("secret.pem", b"\x00\x01 not utf-8 \xff\xfe")
		self.assertEqual(self._run_main(), 1)

	def test_an_uninspectable_file_is_reported_before_text_detectors_run(self) -> None:
		"""The refusal check runs first: even a tree with no text findings
		still fails if it carries an un-inspectable file, and the scan does
		not proceed to report a false "clean"."""
		self._track_binary("archive.zip", b"PK\x03\x04" + b"\x00" * 8 + b"\xff")
		self.assertEqual(self._run_main(), 1)


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


class VersionExtensionTests(unittest.TestCase):
	"""Issue #113: a version quad followed by a file extension is not an IP.

	The round-2 trailing-guard fix (`(?!\\.\\d)`) bounded the over-correction
	to NUMERIC continuations only, so a `.` followed by letters still ended
	the token — a bare four-part version immediately followed by a file
	extension newly matched as a dotted-quad. This class pins the false
	positive closed and, just as importantly, pins that the round-2 fix's own
	case (a real address ending a sentence) is untouched by closing it.
	"""

	def test_version_followed_by_a_single_extension_is_not_flagged(self) -> None:
		for version, ext in (
			(quad(5, 2, 1, 0), "ovf"),
			(quad(9, 0, 0, 0), "zip"),
			(quad(8, 18, 0, 4), "iso"),
		):
			text = f"Image {version}.{ext}"
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)

	def test_version_followed_by_a_chained_extension_is_not_flagged(self) -> None:
		"""The `.tar.gz`-style chained extension from the issue body."""
		text = f"file {quad(9, 0, 0, 0)}.tar.gz"
		self.assertEqual(scanner.scan_text("f.md", text), [])

	def test_bundle_filename_in_a_table_cell_is_not_flagged(self) -> None:
		"""The issue's own repro: a bare filename with no dash-joined prefix."""
		text = f"| bundle | {quad(8, 18, 0, 4)}.zip |"
		self.assertEqual(scanner.scan_text("f.md", text), [])

	def test_sentence_final_address_is_still_flagged(self) -> None:
		"""The round-2 fix's own case must survive closing this one.

		A real address ending a sentence has nothing alphabetic after its
		trailing period, so the new extension guard never engages.
		"""
		findings = scanner.scan_text("f.md", f"Upgrade to {LAB_IP}.")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IP, findings[0])

	def test_sentence_final_address_followed_by_more_prose_is_still_flagged(
		self,
	) -> None:
		findings = scanner.scan_text("f.md", f"Upgrade to {LAB_IP} now.")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IP, findings[0])

	def test_five_part_version_stays_suppressed(self) -> None:
		"""The round-2 fix's numeric case must also survive closing this one."""
		self.assertEqual(scanner.scan_text("f.md", f"x {quad(9, 0, 0, 0)}.5"), [])

	def test_extension_guard_does_not_widen_past_a_short_alphabetic_run(self) -> None:
		"""A long alphabetic run after the dot is a hostname label, not an
		extension — the guard is capped so it does not start suppressing a
		real address glued to a dotted hostname continuation.
		"""
		long_label = "a" * 9  # past the {1,8} cap
		findings = scanner.scan_text("f.md", f"host {LAB_IP}.{long_label}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IP, findings[0])

	def test_version_key_suppression_still_applies_ahead_of_an_extension(self) -> None:
		"""The two suppressions (version-key, extension) are independent and
		compose rather than fight — a version-keyed quad with an extension
		after it stays suppressed for either reason.
		"""
		text = f"version: '{quad(8, 18, 0, 4)}.ovf'"
		self.assertEqual(scanner.scan_text("f.md", text), [])


class HexLetteredIdentifierTests(unittest.TestCase):
	"""Issue #118: a hex-letters-only `word::word` identifier is not an IPv6.

	`ipaddress.IPv6Address` remains the strict arbiter of validity; the fix
	here is a narrow, digit-based exception on top of it, not a loosening of
	that arbiter. `test_real_ipv6_addresses_are_still_flagged` is the load-
	bearing negative: it proves the exception is scoped to the disclosed
	false-positive shape and does not weaken real-address detection.
	"""

	def test_hex_lettered_two_part_identifier_is_not_flagged(self) -> None:
		"""The issue's own example shape: both halves hex-letters-only."""
		for left, right in (("cafe", "babe"), ("face", "feed"), ("bad", "beef")):
			text = f"link {left}::{right} seen"
			with self.subTest(text=text):
				self.assertEqual(scanner.scan_text("f.md", text), [], text)

	def test_single_hex_lettered_word_after_double_colon_is_not_flagged(self) -> None:
		"""The `::word`-only shape the issue's follow-up comment names too."""
		self.assertEqual(scanner.scan_text("f.md", "scope ::deadbeef end"), [])

	def test_real_ipv6_address_is_still_flagged(self) -> None:
		"""The load-bearing negative: a real, digit-bearing literal is
		unaffected by the digit-based exception.
		"""
		findings = scanner.scan_text("f.md", f"host {LAB_IPV6}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IPV6, findings[0])

	def test_fully_expanded_real_address_is_still_flagged(self) -> None:
		findings = scanner.scan_text("f.md", f"host {LAB_IPV6_FULL}")
		self.assertEqual(len(findings), 1, findings)
		self.assertIn(LAB_IPV6_FULL, findings[0])

	def test_hex_lettered_group_with_a_digit_elsewhere_is_still_flagged(self) -> None:
		"""Per-candidate, not per-group: one digit anywhere in the span is
		enough to keep the whole candidate reportable.
		"""
		text = f"host {ipv6('fd00', '', 'cafe')}"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 1, findings)

	def test_sanctioned_addresses_are_unaffected(self) -> None:
		"""Every sanctioned spelling already carries a digit, so the new guard
		never has to be the reason one of these stays silent — but it must
		not accidentally flip one to a finding either.
		"""
		for address in (OK_IPV6_DOC, OK_IPV6_LOOPBACK, OK_IPV6_UNSPECIFIED):
			with self.subTest(address=address):
				self.assertEqual(scanner.scan_text("f.md", f"addr {address}"), [])

	def test_eui64_shaped_residual_is_unaffected(self) -> None:
		"""The disclosed, deliberately-deferred #118 residual (an 8-group hex
		run with digits present) is a DIFFERENT shape from this fix's target
		and must keep firing exactly as documented in docs/testing.md and
		FalsePositiveCorpusTests — this fix narrows the false-positive class,
		it does not eliminate the whole issue.
		"""
		findings = scanner.scan_text("f.md", f"cols {EUI64_SHAPED}")
		self.assertEqual(len(findings), 1, findings)


class ZeroPaddedMappedIpv6Tests(unittest.TestCase):
	"""Issue #123: a zero-padded IPv4-mapped IPv6 literal keeps its finding.

	Same root cause as #119 for the plain IPv4 detector, in the mapped form:
	`ipaddress.IPv6Address` rejects leading zeros in the embedded quad, and
	the failure used to read as "not an address" — losing the IPv6 finding
	while the IPv4 detector still caught the embedded quad on its own
	(under-reported, not silent, per the issue).
	"""

	def test_unpadded_mapped_literal_is_still_flagged_both_ways(self) -> None:
		"""Baseline: the shape that already worked must keep working."""
		mapped = ipv6("", "", "ffff", LAB_IP)
		findings = scanner.scan_text("f.md", f"host {mapped}")
		self.assertEqual(len(findings), 2, findings)
		self.assertTrue(any("IPv6 address literal" in f for f in findings), findings)
		self.assertTrue(any("IP address literal" in f for f in findings), findings)

	def test_three_digit_padded_mapped_literal_keeps_its_ipv6_finding(self) -> None:
		mapped = ipv6("", "", "ffff", zero_pad_quad(LAB_IP))
		findings = scanner.scan_text("f.md", f"host {mapped}")
		self.assertEqual(len(findings), 2, findings)
		ipv6_findings = [f for f in findings if "IPv6 address literal" in f]
		self.assertEqual(len(ipv6_findings), 1, findings)
		self.assertIn(mapped, ipv6_findings[0])

	def test_four_digit_padded_mapped_literal_keeps_its_ipv6_finding(self) -> None:
		"""The issue's own widest example: padding wide enough that the old
		per-part regex cap (`\\d{1,3}`) couldn't even produce a `mapped_quad`
		candidate, so the reported span used to be truncated to the prefix.
		"""
		mapped = ipv6("", "", "ffff", zero_pad_quad(LAB_IP, 4))
		findings = scanner.scan_text("f.md", f"host {mapped}")
		self.assertEqual(len(findings), 2, findings)
		ipv6_findings = [f for f in findings if "IPv6 address literal" in f]
		self.assertEqual(len(ipv6_findings), 1, findings)
		self.assertIn(mapped, ipv6_findings[0], "IPv6 finding must name the full literal, not a truncated fragment")

	def test_padded_mapped_literal_with_a_port_keeps_its_ipv6_finding(self) -> None:
		"""Padding and the unbracketed-port swallow (#115 round 2) are
		independent fixes and must compose rather than fight.
		"""
		mapped = ipv6("", "", "ffff", zero_pad_quad(LAB_IP))
		findings = scanner.scan_text("f.md", f"legacy {mapped}:8080")
		self.assertEqual(len(findings), 2, findings)
		self.assertTrue(any("IPv6 address literal" in f for f in findings), findings)

	def test_padded_rfc5737_mapped_quad_stays_ip_allowed(self) -> None:
		"""The embedded quad's own allowlist check is untouched by padding
		normalization — only which octets it denotes changed, not whether
		those octets are sanctioned.
		"""
		mapped = ipv6("", "", "ffff", zero_pad_quad(quad(192, 0, 2, 10)))
		findings = scanner.scan_text("f.md", f"host {mapped}")
		self.assertFalse(any("IP address literal" in f for f in findings), findings)

	def test_padding_width_is_not_a_bound(self) -> None:
		"""Mirrors ZeroPaddedQuadTests' IPv4 case: the fix must not stop one
		digit short the way #111's original IPv4 fix did (#119).
		"""
		for width in (3, 4, 5, 8):
			with self.subTest(width=width):
				mapped = ipv6("", "", "ffff", zero_pad_quad(LAB_IP, width))
				findings = scanner.scan_text("f.md", f"host {mapped}")
				ipv6_findings = [f for f in findings if "IPv6 address literal" in f]
				self.assertEqual(len(ipv6_findings), 1, (width, findings))
				self.assertIn(mapped, ipv6_findings[0])


class ThreeIssueInteractionTests(unittest.TestCase):
	"""#113 and #118 narrow what is flagged; #123 widens it. This class pins
	that the three do not fight: each issue's example still behaves per its
	own AC when all three fixes are applied together, on one shared line
	where that is meaningful.
	"""

	def test_version_extension_and_hex_identifier_do_not_interact(self) -> None:
		"""A version+extension (#113) and a hex-lettered identifier (#118) on
		the same line: both stay quiet, independently.
		"""
		text = f"Image {quad(5, 2, 1, 0)}.ovf built near cafe::babe"
		self.assertEqual(scanner.scan_text("f.md", text), [])

	def test_padded_mapped_literal_alongside_a_version_extension(self) -> None:
		"""#123's restored finding and #113's suppression on the same line:
		the version+extension stays quiet, the padded mapped literal is still
		fully reported on both axes.
		"""
		mapped = ipv6("", "", "ffff", zero_pad_quad(LAB_IP, 4))
		text = f"Image {quad(5, 2, 1, 0)}.ovf reachable at {mapped}"
		findings = scanner.scan_text("f.md", text)
		self.assertEqual(len(findings), 2, findings)
		self.assertTrue(any("IPv6 address literal" in f for f in findings), findings)
		self.assertTrue(any("IP address literal" in f for f in findings), findings)
		ipv6_findings = [f for f in findings if "IPv6 address literal" in f]
		self.assertIn(mapped, ipv6_findings[0])

	def test_no_new_false_negative_on_the_full_corpus(self) -> None:
		"""Guard test: every real-address fixture this suite relies on
		elsewhere is still flagged after all three fixes land together.
		"""
		real_addresses = (
			LAB_IP,
			LAB_IP_2,
			LAB_IP_PADDED,
			LAB_IPV6,
			LAB_IPV6_FULL,
			LAB_IPV6_LINK_LOCAL,
		)
		for address in real_addresses:
			with self.subTest(address=address):
				findings = scanner.scan_text("f.md", f"host {address} live")
				self.assertEqual(len(findings), 1, (address, findings))


if __name__ == "__main__":
	unittest.main(verbosity=2)
