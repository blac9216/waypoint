#!/usr/bin/env python3
"""Fail the build if measured coverage drops below a committed floor.

This is the mechanical half of issue #102 (backend coverage was collected in
CI but never gated — a regression produced a green check). Issue #79 asked
for a true no-regression gate (compare against the base branch) in
preference to a fixed floor, but this appliance is fully air-gapped
(CLAUDE.md, ADR-0007): no Codecov/Coveralls, no external coverage service to
store a baseline in. A true no-regression check would need either a
persisted baseline artifact across CI runs or a file committed to the repo
and updated on every merge to main — both are real options, deliberately
NOT built here (see "Future option" below).

What IS built: a committed FLOOR, enforced by this self-contained script
(stdlib only, no network, no marketplace action) reading the Cobertura XML
that `dotnet test --collect:"XPlat Code Coverage"` already produces. Fail if
line rate < floor. This is strictly weaker than a no-regression check (it
only catches drops that cross the floor, not any regression above it) but it
is fully air-gap-compatible and catches the failure mode issue #102 was
filed for: a PR that guts coverage and still goes green.

RATCHETING THE FLOOR UP: when coverage legitimately improves, lower the
`--floor` value's distance from measured coverage by re-running this script
locally against a fresh `dotnet test --collect:"XPlat Code Coverage"` run
and updating the constant passed from the workflow. Never lower the floor
to chase a regression — fix the regression instead.

FUTURE OPTION (not built here): a true no-regression gate per issue #79
would commit a `coverage-baseline.json` (line/branch rate) to the repo,
update it in the same PR that raises coverage, and have this script compare
the PR's measured number against the committed baseline instead of a
hardcoded floor. Left as a follow-up; the floor is the pragmatic air-gapped
interim.

Supports two report formats (stdlib only — no third-party parser deps):

  cobertura        backend: dotnet test's coverage.cobertura.xml
                    (line-rate / branch-rate attributes on <coverage>, 0..1)
  vitest-json-summary
                    frontend: vitest coverage-v8's coverage-summary.json
                    (total.lines.pct / total.branches.pct, 0..100)

Usage:
    check-coverage-floor.py --report path/to/coverage.cobertura.xml \
        --format cobertura --floor 89.0 [--metric line|branch]
    check-coverage-floor.py --report coverage/coverage-summary.json \
        --format vitest-json-summary --floor 88.0 [--metric line|branch]
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET


def find_report(pattern: str) -> str:
    matches = sorted(glob.glob(pattern, recursive=True))
    if not matches:
        print(f"error: no coverage report matched pattern: {pattern}", file=sys.stderr)
        sys.exit(2)
    # There may be more than one match (e.g. one Cobertura file per test
    # assembly). Use the most recently written file.
    matches.sort(key=os.path.getmtime)
    return matches[-1]


def measure_cobertura(report_path: str, metric: str) -> float:
    try:
        tree = ET.parse(report_path)
    except ET.ParseError as exc:
        print(f"error: {report_path} is not valid cobertura xml: {exc}", file=sys.stderr)
        sys.exit(2)
    root = tree.getroot()
    attr = "line-rate" if metric == "line" else "branch-rate"
    rate_str = root.get(attr)
    if rate_str is None:
        print(f"error: <coverage> element in {report_path} has no {attr} attribute", file=sys.stderr)
        sys.exit(2)
    return float(rate_str) * 100.0


def measure_vitest_json_summary(report_path: str, metric: str) -> float:
    with open(report_path, encoding="utf-8") as f:
        try:
            data = json.load(f)
        except json.JSONDecodeError as exc:
            print(f"error: {report_path} is not valid json: {exc}", file=sys.stderr)
            sys.exit(2)
    key = "lines" if metric == "line" else "branches"
    try:
        return float(data["total"][key]["pct"])
    except (KeyError, TypeError) as exc:
        print(f"error: {report_path} missing total.{key}.pct: {exc}", file=sys.stderr)
        sys.exit(2)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--report",
        required=True,
        help="Path or glob to a coverage report file",
    )
    parser.add_argument(
        "--format",
        required=True,
        choices=["cobertura", "vitest-json-summary"],
        help="Coverage report format to parse",
    )
    parser.add_argument(
        "--floor",
        required=True,
        type=float,
        help="Minimum acceptable coverage percentage (e.g. 89.0)",
    )
    parser.add_argument(
        "--metric",
        choices=["line", "branch"],
        default="line",
        help="Which coverage metric to gate on (default: line)",
    )
    args = parser.parse_args()

    report_path = find_report(args.report)
    if args.format == "cobertura":
        measured = measure_cobertura(report_path, args.metric)
    else:
        measured = measure_vitest_json_summary(report_path, args.metric)

    print(f"{args.metric} coverage: {measured:.2f}% (report: {report_path})")
    print(f"floor:            {args.floor:.2f}%")

    if measured < args.floor:
        print(
            f"FAIL: {args.metric} coverage {measured:.2f}% is below the floor "
            f"{args.floor:.2f}%",
            file=sys.stderr,
        )
        return 1

    print("PASS: coverage at or above floor")
    return 0


if __name__ == "__main__":
    sys.exit(main())
