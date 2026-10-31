# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""No-optimizer V2 admission tests."""

from __future__ import annotations

from types import SimpleNamespace
import unittest
from pathlib import Path

from ml.ocr.fragmented_xtick_detector_v3.gpu_train import REPO_ROOT, TrainingAdmissionError, _bound_read, _config, build_spatial_locator, decode_cell_relative, encode_cell_relative, feature_receptive_field, require_rx_6600_xt, stratified_batches, target_assignments, validate_train_input


class _Device:
    def __init__(self, index: int) -> None:
        self.type, self.index = "cuda", index
    def __str__(self) -> str:
        return f"cuda:{self.index}"


class _Cuda:
    def __init__(self, names: list[str], available: bool = True) -> None:
        self.names, self.available, self.selected = names, available, None
    def is_available(self) -> bool: return self.available
    def device_count(self) -> int: return len(self.names)
    def get_device_properties(self, index: int) -> SimpleNamespace: return SimpleNamespace(name=self.names[index])
    def set_device(self, device: object) -> None: self.selected = device
    def synchronize(self, device: object) -> None:
        if device != self.selected: raise AssertionError("wrong device")


class _Torch:
    def __init__(self, names: list[str], hip: str | None, available: bool = True) -> None:
        self.version, self.cuda = SimpleNamespace(hip=hip), _Cuda(names, available)
    @staticmethod
    def device(value: str) -> _Device: return _Device(int(value.split(":")[1]))
    @staticmethod
    def zeros(shape: tuple[int, ...], *, device: object) -> SimpleNamespace: return SimpleNamespace(device=device)


class FragmentedXTickV3GpuTests(unittest.TestCase):
    def test_bound_v2_input_contains_varied_positive_locations_and_negatives(self) -> None:
        receipt = validate_train_input()
        self.assertEqual((96, 64, 32), (receipt["example_count"], receipt["positive_count"], receipt["background_negative_count"]))
        self.assertGreaterEqual(receipt["target_center_x_bin_count"], 8)
        self.assertGreaterEqual(receipt["target_center_y_bin_count"], 4)
        self.assertFalse(receipt["dev_input_read"])
        self.assertFalse(receipt["sealed_input_read"])
        self.assertFalse(receipt["private_input_read"])

    def test_config_is_authorized_but_remains_unregistered(self) -> None:
        config = _config()
        self.assertTrue(config["optimizer_execution_authorized"])
        self.assertEqual((30, 360), (config["epochs"], config["expected_optimizer_steps"]))
        self.assertEqual("artifacts/fragmented-xtick-detector-v3-p3-rf31-stratified-30epoch-v1", config["planned_output_root"])
        self.assertEqual("fragmented-xtick-spatial-locator-v3-cell-relative-rf31", config["model"])
        self.assertFalse(config["production_approval"])
        self.assertFalse(config["release_eligible"])

    def test_bound_reader_rejects_parent_escape_before_opening_any_file(self) -> None:
        with self.assertRaises(TrainingAdmissionError):
            _bound_read(REPO_ROOT, "../outside.json", "escape test")

    def test_cpu_wrong_and_ambiguous_hardware_are_rejected(self) -> None:
        for torch in (_Torch(["AMD Radeon RX 6600 XT"], None), _Torch(["AMD Radeon RX 6700 XT"], "7.16"), _Torch(["AMD Radeon RX 6600 XT", "AMD Radeon RX 6600 XT"], "7.16")):
            with self.assertRaises(TrainingAdmissionError): require_rx_6600_xt(torch)

    def test_exact_rx_6600_xt_is_selected_without_cpu_fallback(self) -> None:
        receipt = require_rx_6600_xt(_Torch(["AMD Radeon RX 6600 XT"], "7.16"))
        self.assertEqual("cuda:0", receipt["device"])
        self.assertFalse(receipt["cpu_fallback"])

    def test_grid_assignment_keeps_negative_and_spatially_distinct_boxes(self) -> None:
        assignments = target_assignments((None, (0.05, 0.05, 0.15, 0.15), (0.80, 0.75, 0.92, 0.90)))
        self.assertIsNone(assignments[0])
        self.assertNotEqual(assignments[1][:2], assignments[2][:2])
        self.assertNotEqual((0.05, 0.05, 0.15, 0.15), assignments[1][2])

    def test_cell_relative_encoding_decodes_known_box_exactly(self) -> None:
        box = (0.405, 0.318, 0.477, 0.461)
        encoded = encode_cell_relative(box, 4, 14)
        self.assertTrue(all(0.0 <= value <= 1.0 for value in encoded))
        self.assertEqual(box, decode_cell_relative(encoded, 4, 14))

    def test_all_negative_batch_uses_a_gpu_zero_box_loss_branch(self) -> None:
        source = Path(REPO_ROOT / "ml/ocr/fragmented_xtick_detector_v2/gpu_train.py").read_text(encoding="utf-8")
        self.assertIn("if bool(expanded.any().item())", source)
        self.assertIn("predicted.sum() * 0.0", source)
        self.assertIn("0.5 * (positive_term + background_term)", source)

    def test_recipe_uses_deterministic_shuffle_and_train_fit_metrics(self) -> None:
        source = Path(REPO_ROOT / "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py").read_text(encoding="utf-8")
        self.assertIn("stratified_batches(positive_indices, negative_indices", source)
        self.assertIn("train_fit_before = evaluate_train_fit", source)
        self.assertIn("epoch_train_fit.append(evaluate_train_fit", source)
        self.assertIn("train_fit_after = evaluate_train_fit", source)
        self.assertIn('"epoch_train_fit": epoch_train_fit', source)
        self.assertIn('"train_fit_is_held_out_validation": False', source)

    def test_stratified_batches_are_deterministic_mixed_and_cover_each_case_once(self) -> None:
        positive, negative = tuple(range(64)), tuple(range(64, 96))
        first = stratified_batches(positive, negative, 8, 24014202)
        self.assertEqual(first, stratified_batches(positive, negative, 8, 24014202))
        self.assertEqual(12, len(first))
        self.assertTrue(all(len(batch) == 8 and set(batch) & set(positive) and set(batch) & set(negative) for batch in first))
        self.assertCountEqual(range(96), [index for batch in first for index in batch])

    def test_stratified_batches_reject_unmixable_class_counts(self) -> None:
        with self.assertRaises(ValueError):
            stratified_batches(tuple(range(7)), tuple(range(7, 9)), 3, 1)

    def test_context_convolution_expands_receptive_field_and_keeps_spatial_heads(self) -> None:
        import torch
        runtime = require_rx_6600_xt(torch)
        device = torch.device(runtime["device"])
        model = build_spatial_locator(torch).to(device).eval()
        self.assertEqual(31, feature_receptive_field(model.features, torch))
        with torch.inference_mode():
            objectness, boxes = model(torch.zeros((1, 1, 96, 256), device=device))
        self.assertEqual((1, 1, 12, 32), tuple(objectness.shape))
        self.assertEqual((1, 4, 12, 32), tuple(boxes.shape))
        self.assertEqual(device, objectness.device)
        self.assertEqual(device, boxes.device)


if __name__ == "__main__":
    unittest.main()
