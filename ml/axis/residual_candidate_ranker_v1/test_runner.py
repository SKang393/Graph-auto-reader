# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import json
from pathlib import Path
import tempfile
import unittest

from ml.axis.residual_candidate_ranker_v1 import runner


class ResidualRankerRunnerTests(unittest.TestCase):
    def test_fixed_config_keeps_device_scope_and_step_budget(self):
        config = runner.fixed_config("a" * 64)
        self.assertEqual(372, config["optimizer_steps"])
        self.assertEqual("native-score-plus-tanh-residual-17-32-1", config["architecture"])
        self.assertEqual(runner.GROUP_SHA, config["groups_sha256"])
        self.assertFalse(config["dev_read"])
        self.assertFalse(config["reserved_read"])
        self.assertFalse(config["production_approved"])

    def test_journal_requires_order_and_refuses_a_second_run(self):
        root = runner.ROOT / "artifacts/test-temp"
        root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root) as temporary:
            directory = Path(temporary)
            journal = runner.StepJournal(directory, 1)
            with self.assertRaises(RuntimeError):
                journal.commit()
            journal.begin()
            with self.assertRaises(RuntimeError):
                journal.begin()
            journal.commit()
            with self.assertRaises(RuntimeError):
                journal.begin()
            with self.assertRaises(FileExistsError):
                runner.StepJournal(directory, 1)
            events = [json.loads(line)["event"] for line in journal.path.read_text().splitlines()]
            self.assertEqual(["run_opened", "step_intent", "step_completed"], events)

    def test_journal_rejects_invalid_step_budgets(self):
        root = runner.ROOT / "artifacts/test-temp"
        root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root) as temporary:
            for invalid in (0, -1, 1.5, True):
                with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                    runner.StepJournal(Path(temporary), invalid)


if __name__ == "__main__":
    unittest.main()
