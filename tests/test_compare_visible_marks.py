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

    def audit(self, name, rows):
        target = str(uuid4())
        path = self.directory / (name + ".audit.json")
        content = dict(self.header, intervention_series_id=target, row_count=len(rows),
                       rows=[dict(row, target_intervention_series_id=target) for row in rows])
        path.write_text(json.dumps(content), encoding="utf-8")
        return path

    def compare(self, *files, phase="a", visible=2):
        return subprocess.run([sys.executable, str(TOOL), *files, "--series", "Response",
                               "--phase", phase, "--visible", str(visible)], capture_output=True, text=True)

    def test_same_count_wrong_visit_and_repeated_shared_baseline(self):
        # Baseline in phase-restart-coverage.png has marks at visits 1 and 3.
        visible = {1, 3}
        first, wrong = self.row(1), self.row(2)
        self.assertNotEqual(visible, {first["x_value"], wrong["x_value"]})
        audits = [self.audit(name, [first, wrong]) for name in ("blue", "green")]
        before = [path.read_bytes() for path in audits]
        result = self.compare(*audits)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Visible marks = 2\nDistinct audited points = 2\nAudited minus visible = +0\nCounts agree.", result.stdout)
        self.assertIn("Source plot completeness is unverified", result.stdout)
        self.assertEqual(before, [path.read_bytes() for path in audits])

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
        self.header["run_id"] = str(uuid4())
        self.assertIn("one export", self.compare(audit, self.audit("edited", [first])).stderr)


if __name__ == "__main__":
    unittest.main()
