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
        # jacoco-pass.xml's <package>/<class> LINE counters (missed=1,
        # covered=9) are deliberately different from the report-level LINE
        # counter (missed=12, covered=88); if the reader summed nested
        # counters instead of taking the report-level one it would not
        # return 88.0.
        pct = cf.measure_jacoco(fixture("jacoco-pass.xml"), "line")
        self.assertAlmostEqual(pct, 88.0)

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
        import tempfile

        with tempfile.NamedTemporaryFile(
            mode="w", suffix=".xml", delete=False
        ) as tmp:
            tmp.write(
                "<report name=\"invented-runner\">"
                '<counter type="LINE" missed="0" covered="0"/>'
                "</report>"
            )
            path = tmp.name
        try:
            with self.assertRaises(SystemExit) as ctx:
                cf.measure_jacoco(path, "line")
            self.assertNotEqual(ctx.exception.code, 0)
        finally:
            os.remove(path)


class CoberturaReaderSmokeTest(unittest.TestCase):
    def test_line_rate_percentage(self):
        pct = cf.measure_cobertura(fixture("cobertura-sample.xml"), "line")
        self.assertAlmostEqual(pct, 90.0)

    def test_branch_rate_percentage(self):
        pct = cf.measure_cobertura(fixture("cobertura-sample.xml"), "branch")
        self.assertAlmostEqual(pct, 85.0)


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
    sys.path.insert(0, os.path.dirname(SCRIPT_PATH))
    unittest.main()
