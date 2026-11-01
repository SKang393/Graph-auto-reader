import importlib.util
from pathlib import Path
import unittest

MODULE_PATH = Path(__file__).with_name("train_classifier_p5.py")
SPEC = importlib.util.spec_from_file_location("p5_trainer_test_module", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class P5WeightedLossTests(unittest.TestCase):
    def test_only_selected_negative_examples_receive_threefold_weight(self):
        values = MODULE.weighted_example_values(
            [1, 0, 0], ["pos", "hard", "ordinary"], {"hard"}, 3.0)
        self.assertEqual(values, [1.0, 3.0, 1.0])

    def test_selected_positive_or_conflicting_hash_is_rejected(self):
        with self.assertRaisesRegex(MODULE.P5Error, "non-negative"):
            MODULE.weighted_example_values([1], ["positive"], {"positive"}, 3.0)
        with self.assertRaisesRegex(MODULE.P5Error, "conflict"):
            MODULE.weighted_example_values([0, 1], ["same", "same"], {"same"}, 3.0)

    def test_missing_hard_negative_hash_is_rejected(self):
        with self.assertRaisesRegex(MODULE.P5Error, "missing"):
            MODULE.weighted_example_values([0], ["present"], {"missing"}, 3.0)


if __name__ == "__main__":
    unittest.main()
