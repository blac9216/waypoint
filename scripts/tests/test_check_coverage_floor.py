#!/usr/bin/env python3
"""Unit tests for scripts/check-coverage-floor.py.

Covers the jacoco reader added for issue #1354 (line pass/fail, branch
metric, malformed XML, missing counter) plus one smoke test each for the
pre-existing cobertura and vitest-json-summary readers so this module is
the test file of record for the whole script.

Run from the repo root:
    python3 -m unittest discover -s scripts/tests -v

All fixtures under scripts/tests/fixtures/ are invented (no exported data),
per AGENTS.md's sanitization rules.
"""

from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
import unittest

FIXTURES_DIR = os.path.join(os.path.dirname(__file__), "fixtures")
SCRIPT_PATH = os.path.join(
    os.path.dirname(os.path.dirname(__file__)), "check-coverage-floor.py"
)


def _load_module():
    spec = importlib.util.spec_from_file_location("check_coverage_floor", SCRIPT_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


cf = _load_module()


def fixture(name: str) -> str:
    return os.path.join(FIXTURES_DIR, name)


class JacocoReaderTests(unittest.TestCase):
    def test_line_metric_pass(self):
        pct = cf.measure_jacoco(fixture("jacoco-pass.xml"), "line")
        self.assertAlmostEqual(pct, 88.0)

    def test_line_metric_fail_below_floor(self):
        pct = cf.measure_jacoco(fixture("jacoco-fail.xml"), "line")
        self.assertAlmostEqual(pct, 50.0)
        self.assertLess(pct, 88.0)

    def test_branch_metric(self):
        pct = cf.measure_jacoco(fixture("jacoco-pass.xml"), "branch")
        self.assertAlmostEqual(pct, 80.0)

    def test_report_level_counter_not_summed_with_class_counters(self):
        # jacoco-nested-mismatch.xml has two <class> LINE counters
        # (missed=5/covered=5 and missed=3/covered=2) that sum to
        # missed=8/covered=7 (~46.67%), deliberately far from the
        # report-level LINE counter (missed=10, covered=90 -> 90.0%). If the
        # reader summed nested counters instead of taking the report-level
        # one, this would return ~46.67 instead of 90.0.
        pct = cf.measure_jacoco(fixture("jacoco-nested-mismatch.xml"), "line")
        self.assertAlmostEqual(pct, 90.0)

    def test_malformed_xml_exits_nonzero(self):
        with self.assertRaises(SystemExit) as ctx:
            cf.measure_jacoco(fixture("jacoco-malformed.xml"), "line")
        self.assertNotEqual(ctx.exception.code, 0)

    def test_missing_line_counter_exits_nonzero(self):
        with self.assertRaises(SystemExit) as ctx:
            cf.measure_jacoco(fixture("jacoco-missing-line-counter.xml"), "line")
        self.assertNotEqual(ctx.exception.code, 0)

    def test_missing_line_counter_branch_still_readable(self):
        # Sanity check the fixture is otherwise well-formed: BRANCH is
        # present even though LINE is not.
        pct = cf.measure_jacoco(fixture("jacoco-missing-line-counter.xml"), "branch")
        self.assertAlmostEqual(pct, 80.0)

    def test_zero_total_exits_nonzero(self):
        with self.assertRaises(SystemExit) as ctx:
            cf.measure_jacoco(fixture("jacoco-zero-total.xml"), "line")
        self.assertNotEqual(ctx.exception.code, 0)


class CoberturaReaderSmokeTest(unittest.TestCase):
    def test_line_rate_percentage(self):
        pct = cf.measure_cobertura(fixture("cobertura-sample.xml"), "line")
        self.assertAlmostEqual(pct, 90.0)

    def test_branch_rate_percentage(self):
        pct = cf.measure_cobertura(fixture("cobertura-sample.xml"), "branch")
        self.assertAlmostEqual(pct, 85.0)


class JacocoCliContractTests(unittest.TestCase):
    """Exercise --format jacoco through the real CLI entry point (main()),
    not just the measure_jacoco() function, so that deleting the "jacoco"
    argparse choice or its dispatch branch in main() fails this suite even
    though the reader function itself would still work if called directly.
    """

    def _run(self, report: str, floor: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [
                sys.executable,
                SCRIPT_PATH,
                "--report",
                report,
                "--format",
                "jacoco",
                "--floor",
                floor,
                "--metric",
                "line",
            ],
            capture_output=True,
            text=True,
        )

    def test_cli_jacoco_pass_exits_zero(self):
        result = self._run(fixture("jacoco-pass.xml"), "50.0")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("PASS", result.stdout)

    def test_cli_jacoco_fail_exits_nonzero(self):
        result = self._run(fixture("jacoco-fail.xml"), "88.0")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("FAIL", result.stderr)


class VitestJsonSummaryReaderSmokeTest(unittest.TestCase):
    def test_lines_pct(self):
        pct = cf.measure_vitest_json_summary(
            fixture("vitest-coverage-summary.json"), "line"
        )
        self.assertAlmostEqual(pct, 91.0)

    def test_branches_pct(self):
        pct = cf.measure_vitest_json_summary(
            fixture("vitest-coverage-summary.json"), "branch"
        )
        self.assertAlmostEqual(pct, 82.5)


if __name__ == "__main__":
    unittest.main()
