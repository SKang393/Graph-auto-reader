# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""P2 binds the P1 train recipe except for per-image top-32 BCE negatives."""

from pathlib import Path
import unittest

from ml.ocr.stroke_seeded_xtick_v1 import train_gpu


P2_CONFIG = train_gpu.ROOT / "training" / "p2.json"


class StrokeSeededP2Tests(unittest.TestCase):
    def test_only_recipe_loss_identity_and_output_change_from_p1(self):
        p1, _ = train_gpu.load_config()
        p2, _ = train_gpu.load_config(P2_CONFIG)
        permitted = {"recipe_id", "model_identity", "balanced_objectness_loss",
                     "hard_negative_top_k_per_image", "planned_output_root", "expected_runner_sha256"}
        self.assertEqual({key for key in set(p1) | set(p2) if p1.get(key) != p2.get(key)}, permitted)
        self.assertEqual(p2["hard_negative_top_k_per_image"], 32)
        self.assertEqual(p2["expected_runner_sha256"], train_gpu.digest(Path(train_gpu.__file__).read_bytes()))

    def test_top32_loss_selects_highest_scoring_negatives_per_image(self):
        import torch
        logits = torch.arange(2 * 12 * 32, dtype=torch.float32).reshape(2, 1, 12, 32)
        target = torch.zeros_like(logits)
        target[0, 0, 11, 31] = 1.0  # Highest logit must be excluded from negative mining.
        per_cell = logits.clone()
        loss, positive_loss, negative_loss, selected = train_gpu.balanced_objectness_loss(
            logits, target, per_cell, top_k=32)
        self.assertEqual(selected.flatten(1).sum(dim=1).tolist(), [32, 32])
        self.assertFalse(bool((selected & target.bool()).any()))
        self.assertFalse(bool(selected[0, 0, 11, 31]))
        self.assertEqual(float(positive_loss), float(per_cell[target.bool()].mean()))
        self.assertEqual(float(negative_loss), float(per_cell[selected].mean()))
        self.assertEqual(float(loss), 0.5 * (float(positive_loss) + float(negative_loss)))
        self.assertTrue(bool(torch.isfinite(loss)))


if __name__ == "__main__":
    unittest.main()
