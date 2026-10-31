# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded synthetic-only numerical parity test; no optimizer."""
import json
from copy import deepcopy
from io import BytesIO
import unittest
from unittest.mock import patch
import torch
from . import parity


class V46ParityTests(unittest.TestCase):
    def test_changed_synthetic_image_bytes_are_rejected(self):
        original = parity._open_read_only_no_writers
        def opened(path):
            return BytesIO(b"changed synthetic bytes") if path == parity.IMAGE else original(path)
        with patch.object(parity, "_open_read_only_no_writers", side_effect=opened):
            with self.assertRaisesRegex(ValueError, "image changed"):
                parity._synthetic_input()

    def test_changed_model_and_protocol_hashes_are_rejected_before_inference(self):
        with patch.object(parity, "require_gpu"):
            with patch.object(parity, "MODEL_SHA", "0" * 64):
                with self.assertRaisesRegex(ValueError, "detector hash differs"):
                    parity.run_parity()
            with patch.object(parity, "PROTOCOL_SHA", "0" * 64):
                with self.assertRaisesRegex(ValueError, "protocol changed"):
                    parity.run_parity()

    def test_wrong_synthetic_image_hash_is_rejected(self):
        with patch.object(parity, "IMAGE_SHA", "0" * 64):
            with self.assertRaisesRegex(ValueError, "not exclusive"):
                parity._synthetic_input()

    def test_dataset_manifest_hash_and_split_substitution_rejected(self):
        with patch.object(parity, "DATASET_MANIFEST_SHA", "0" * 64):
            with self.assertRaisesRegex(ValueError, "metadata hash"):
                parity._dataset_binding()
        summary = json.loads((parity.DATASET / "manifest.json").read_bytes())
        seed = json.loads((parity.DATASET / "dataset/seed-manifest.json").read_bytes())
        splits = {name: json.loads((parity.DATASET / "dataset/splits" / f"{name}.json").read_bytes())
                  for name in ("train", "validation", "test")}
        image_rel = parity.IMAGE.relative_to(parity.DATASET / "dataset").as_posix()
        altered = deepcopy(splits)
        altered["test"]["cases"] = [deepcopy(splits["train"]["cases"][0])]
        with self.assertRaisesRegex(ValueError, "split manifest"):
            parity._validate_membership(summary, seed, altered, image_rel, parity.IMAGE_SHA)
        validation_image = summary["splits"]["validation"]["cases"][0]["image"]
        with self.assertRaisesRegex(ValueError, "not exclusive"):
            parity._validate_membership(summary, seed, splits, validation_image["path"], validation_image["sha256"])

    def test_original_model_and_gpu_head_on_synthetic_tensor(self):
        with patch.object(torch.optim.Optimizer, "step", side_effect=AssertionError("optimizer forbidden")):
            result = parity.run_parity()
        print(json.dumps(result, sort_keys=True))
        self.assertEqual(result["status"], "pass")
        self.assertEqual(result["optimizer_steps"], 0)
        self.assertEqual(result["private_reads"], 0)
        self.assertEqual(result["sealed_reads"], 0)
        self.assertFalse(result["production_approved"])
        self.assertEqual(result["comparison_protocol_sha256"], parity.PROTOCOL_SHA)
        self.assertEqual(result["dataset_manifest_sha256"], parity.DATASET_MANIFEST_SHA)
        self.assertEqual(result["probe_script_sha256"], __import__("hashlib").sha256(
            parity.Path(parity.__file__).read_bytes()).hexdigest())
        self.assertEqual(result["probe_script_binding"], "descriptive_sha256_not_preregistered")
        self.assertLess(result["peak_additional_allocated_bytes"], 96 * 1024 * 1024)


if __name__ == "__main__":
    unittest.main()
