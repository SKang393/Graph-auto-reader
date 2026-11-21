# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from uuid import uuid4


TOOL = Path(__file__).resolve().parents[1] / "tools" / "compare_visible_marks.py"


class VisibleMarkTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.directory = Path(temp.name)
        self.header = {k: str(uuid4()) for k in ("project_id", "panel_id", "run_id")}
        self.series = str(uuid4())
        self.phase = str(uuid4())

    def row(self, visit):
        return dict(source_series_id=self.series, series_name="Response", phase_id=self.phase,
                    phase="a", point_id=str(uuid4()), x_value=visit)

    def audit(self, name, rows, target=None):
        target = target or str(uuid4())
        path = self.directory / (name + ".audit.json")
        content = dict(self.header, intervention_series_id=target, row_count=len(rows),
                       rows=[dict(row, target_intervention_series_id=target) for row in rows])
        path.write_text(json.dumps(content), encoding="utf-8")
        return path

    def compare(self, *files, phase="a", visible=2, sessions=None):
        return subprocess.run([sys.executable, str(TOOL), *files, "--series", "Response",
                               "--phase", phase, "--visible", str(visible),
                               *(["--sessions", sessions] if sessions is not None else [])], capture_output=True, text=True)

    def test_same_count_wrong_visit_and_repeated_shared_baseline(self):
        # Baseline in phase-restart-coverage.png has marks at visits 1 and 3.
        visible = {1, 3}
        first = dict(self.row(1), inclusion="shared_baseline")
        wrong = dict(self.row(2), inclusion="shared_baseline")
        self.assertNotEqual(visible, {first["x_value"], wrong["x_value"]})
        audits = []
        for name in ("blue", "green"):
            target = str(uuid4())
            own = dict(self.row(2), source_series_id=target, series_name=name, inclusion="intervention")
            audits.append(self.audit(name, [first, wrong, own], target))
        before = [path.read_bytes() for path in audits]
        result = self.compare(*audits)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Visible marks = 2\nDistinct audited points = 2\nAudited minus visible = +0\nCounts agree.", result.stdout)
        self.assertIn("Source plot completeness is unverified", result.stdout)
        self.assertIn("unchanged files from one export action", result.stdout)
        self.assertIn("Audited minus visible = -1\nCounts differ.", self.compare(*audits, visible=3).stdout)
        self.assertEqual(before, [path.read_bytes() for path in audits])

    def test_printed_sessions_with_shared_copies_and_a_phase_restart(self):
        self.header["export_mode"] = "printed_session"
        for second, common, analyst_only, audit_only in [(2, "1", "3", "2"), (3, "1, 3", "none", "none")]:
            with self.subTest(second=second):
                rows = [dict(self.row(x), export_mode="printed_session", x_source="printed") for x in (1, second)]
                restart = dict(rows[0], phase="b", phase_id=str(uuid4()), point_id=str(uuid4()))
                files = [self.audit(name, [*rows, restart]) for name in ("blue", "green")]
                before = [path.read_bytes() for path in files]
                result = self.compare(*files, sessions="[1,3]")
                self.assertEqual(result.returncode, 0, result.stderr)
                lines = result.stdout.splitlines()
                self.assertIn("Counts agree.", lines)
                self.assertIn("Printed values appearing once on each side: " + common, lines)
                self.assertIn("Analyst values without a comparable printed audit value: " + analyst_only, lines)
                self.assertIn("Printed audit values absent from the analyst list: " + audit_only, lines)
                self.assertNotIn("Repeated value", result.stdout)
                self.assertEqual(before, [path.read_bytes() for path in files])
                self.header["run_id"] = str(uuid4())
                mixed = self.compare(*files, self.audit("other", rows), sessions="[1,3]")
                self.assertNotEqual(mixed.returncode, 0)
                self.assertEqual("", mixed.stdout)

    def test_duplicates_and_unresolved_values_stay_visible(self):
        self.header["export_mode"] = "printed_session"
        def reading(x, source="printed", mode="printed_session"):
            return dict(self.row(x), x_source=source, export_mode=mode)
        rows = [reading(1), reading(1), reading(3, "estimated"), reading(4, mode="observation_order"),
                reading("5"), reading(True), self.row(7), reading(8, "unknown")]
        result = self.compare(self.audit("duplicates", rows), sessions="[1,1,3,4,5,6,7,8]", visible=len(rows))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Printed values appearing once on each side: none", result.stdout)
        self.assertIn("Repeated value in analyst list: 1 (2 entries)", result.stdout)
        self.assertIn("Repeated value in printed audit: 1 (2 point IDs)", result.stdout)
        for unresolved in rows[2:]:
            self.assertIn("Unresolved audit point " + unresolved["point_id"], result.stdout)
        self.header["export_mode"] = "observation_order"
        result = self.compare(self.audit("ordinal", rows[:2]), sessions="[1,3]")
        self.assertEqual(result.stdout.count("Unresolved audit point"), 2)

    def test_session_values_use_exact_json_numbers(self):
        self.header["export_mode"] = "printed_session"
        path = self.audit("decimal", [dict(self.row(1.25), x_source="printed", export_mode="printed_session")])
        path.write_text(path.read_text().replace("1.25", "1.2500000000000001"))
        result = self.compare(path, visible=1, sessions="[1.25]")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Printed values appearing once on each side: none", result.stdout)
        self.assertIn("Printed audit values absent from the analyst list: 1.2500000000000001", result.stdout)
        for invalid in ('["1.25"]', "[true]", "[NaN]", "[null]", "[]"):
            with self.subTest(invalid=invalid):
                result = self.compare(path, visible=1, sessions=invalid)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("--sessions", result.stderr)
                self.assertEqual("", result.stdout)

    def test_scoped_counts_and_ambiguous_labels(self):
        first = self.row(1)
        other_phase = dict(self.row(1), phase_id=str(uuid4()))
        other_series = dict(self.row(1), source_series_id=str(uuid4()), series_name="Comparison",
                            phase_id=str(uuid4()), phase="b")
        audit = self.audit("both", [first, other_phase, other_series])
        self.assertIn("Ambiguous phase", self.compare(audit).stderr)
        scoped = self.compare(audit, phase=self.phase)
        self.assertEqual(scoped.returncode, 0, scoped.stderr)
        self.assertIn("Distinct audited points = 1\nAudited minus visible = -1\nCounts differ.", scoped.stdout)
        self.assertIn("Audited minus visible = +1\nCounts differ.", self.compare(audit, phase=self.phase, visible=0).stdout)
        empty_pair = self.compare(audit, phase="b")
        self.assertIn("Distinct audited points = 0", empty_pair.stdout)
        self.assertIn("No rows for this pair", empty_pair.stdout)
        self.assertNotEqual(self.compare(audit, phase="unlisted").returncode, 0)

    def test_unrelated_project_panel_or_run_rejected_before_tally(self):
        first = self.row(1)
        original = self.audit("original", [first])
        for key in self.header:
            with self.subTest(key=key):
                previous = self.header[key]
                self.header[key] = str(uuid4())
                mixed = self.compare(original, self.audit("edited", [first]))
                self.assertNotEqual(mixed.returncode, 0)
                self.assertIn("one export", mixed.stderr)
                self.assertEqual("", mixed.stdout)
                self.header[key] = previous

    def test_reused_headers_cannot_pool_inconsistent_shared_baselines(self):
        first = self.row(1)
        blue = self.audit("blue", [first])
        for change, rows in (("replacement", [self.row(2)]),
                             ("addition", [first, self.row(2)]),
                             ("moved", [dict(first, x_value=3)])):
            with self.subTest(change=change):
                mixed = self.compare(blue, self.audit("green", rows))
                self.assertNotEqual(mixed.returncode, 0)
                self.assertIn("Conflicting source series snapshots", mixed.stderr)
                self.assertEqual("", mixed.stdout)

    def test_disjoint_sources_require_known_export_origin(self):
        target = str(uuid4())
        blue = self.audit("blue", [self.row(1)], target)
        other = dict(self.row(1), source_series_id=str(uuid4()), series_name="Other")
        mixed_target = self.compare(blue, self.audit("edited-blue", [other], target))
        self.assertNotEqual(mixed_target.returncode, 0)
        self.assertIn("Conflicting target series contents", mixed_target.stderr)

        separate_targets = self.compare(blue, self.audit("green", [other]), visible=1)
        self.assertEqual(separate_targets.returncode, 0, separate_targets.stderr)
        self.assertIn("Distinct audited points = 1", separate_targets.stdout)
        self.assertIn("Combined tally assumes unchanged files from one export action.", separate_targets.stdout)


if __name__ == "__main__":
    unittest.main()
