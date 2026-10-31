# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic-only validation; never constructs or steps an optimizer."""
from hashlib import sha256
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
import unittest
from unittest import mock

import numpy as np
import torch

from ml.ocr.gpu_db_head_probe_v1 import conflict_pixel_counterfactual as conflict, probe
from ml.ocr.gpu_db_head_training_v1 import execution_bundle, journal, runner, source, source_derivation
from ml.ocr.official_bakeoff.db_inverse_targets import build_inverse_targets


class FoundationTests(unittest.TestCase):
    def test_localized_mask_preserves_all_intended_positive_support(self):
        built = build_inverse_targets(40, 40, [(3, 3, 9, 9), (6, 3, 12, 9), (27, 27, 35, 35)])
        baseline = np.ones_like(built.target)
        mask, _ = conflict.evaluate(built, baseline, [("a", "x_tick"), ("b", "y_tick"), ("c", "x_tick")])
        source_derivation._verify_localized_mask(built, baseline, mask)
        self.assertTrue(((built.target == 1) & (mask == 1)).any())
        self.assertTrue(((built.target == 0) & (mask == 1)).any())
        self.assertEqual(mask[0, 31, 31], 1)
        changed = mask.copy()
        changed[0, 31, 31] = 0
        with self.assertRaisesRegex(ValueError, "intended supervision"):
            source_derivation._verify_localized_mask(built, baseline, changed)

    def test_cache_hash_and_binary_values_checked(self):
        temp_root = journal.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as tmp:
            path = Path(tmp) / "synthetic.f32"
            good = np.array([[[0., 1.]]], dtype="<f4").tobytes()
            path.write_bytes(good)
            desc = {"dtype": "float32-le", "shape": [1, 1, 2], "sha256": sha256(good).hexdigest()}
            self.assertEqual(source._array(path, desc, (1, 1, 2), binary=True).tolist(), [[[0., 1.]]])
            path.write_bytes(np.array([[[0., 0.]]], dtype="<f4").tobytes())
            with self.assertRaisesRegex(ValueError, "hash differs"):
                source._array(path, desc, (1, 1, 2), binary=True)
            bad = np.array([[[0., 0.5]]], dtype="<f4").tobytes()
            path.write_bytes(bad)
            desc["sha256"] = sha256(bad).hexdigest()
            with self.assertRaisesRegex(ValueError, "invalid values"):
                source._array(path, desc, (1, 1, 2), binary=True)

    def test_recipe_schedule_and_single_use_crash_safety(self):
        recipe = journal.Recipe(seed=42, epochs=1, learning_rate=0.00001, weight_decay=0.0001, bundle_sha256="a" * 64)
        self.assertEqual(sorted(runner._schedule(recipe)), [0, 1, 2, 3])
        self.assertEqual(runner._schedule(recipe), runner._schedule(recipe))
        temp_root = journal.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as tmp, mock.patch.object(journal, "ATTEMPTS", Path(tmp) / "attempts"), \
             mock.patch.object(execution_bundle, "require_runtime"), mock.patch.object(execution_bundle, "verify_full"), \
             mock.patch.object(execution_bundle, "verify_critical"):
            receipt = execution_bundle.verify_startup(recipe.bundle_sha256)
            attempt = journal.SingleUseJournal(recipe, receipt)
            order = runner._schedule(recipe)
            with self.assertRaisesRegex(ValueError, "invalid optimizer step"):
                attempt.reserve(0, (order[0] + 1) % 4)
            attempt.reserve(0, order[0])
            with self.assertRaises(FileExistsError):
                journal.SingleUseJournal(recipe, execution_bundle.verify_startup(recipe.bundle_sha256))
            with self.assertRaisesRegex(ValueError, "Duplicate"):
                attempt.reserve(0, order[0])
            with self.assertRaisesRegex(ValueError, "Incomplete"):
                attempt.finish()
            attempt.complete_step(0)
            for step in range(1, 4):
                attempt.reserve(step, order[step])
                attempt.complete_step(step)
            attempt.finish()
            with self.assertRaisesRegex(ValueError, "Duplicate"):
                attempt.reserve(4, 0)
            with self.assertRaises(FileExistsError):
                journal.SingleUseJournal(recipe, execution_bundle.verify_startup(recipe.bundle_sha256))

    def test_step_input_cannot_self_assert_changed_target(self):
        features = np.zeros((1, 96, 8, 8), dtype="<f4")
        target = np.zeros((1, 32, 32), dtype="<f4")
        target[0, 0, 0] = 1
        mask = np.ones_like(target)
        h = lambda x: sha256(x.tobytes()).hexdigest()
        sample = source.Sample(features, target, mask, h(features), h(target), h(mask))
        with mock.patch.object(runner, "FEATURE_SHA", (h(features),)), \
             mock.patch.object(runner, "TARGET_SHA", (h(target),)), \
             mock.patch.object(runner, "MASK_SHA", (h(mask),)):
            runner._verify_sample(sample, 0)
            changed = target.copy()
            changed[0, 0, 1] = 1
            forged = source.Sample(features, changed, mask, h(features), h(changed), h(mask))
            with self.assertRaisesRegex(ValueError, "identity differs"):
                runner._verify_sample(forged, 0)

    def test_wrong_gpu_or_cpu_tensor_are_rejected(self):
        with mock.patch.object(torch.cuda, "is_available", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "CPU fallback"):
                probe.require_gpu()
        with mock.patch.object(torch.cuda, "is_available", return_value=True), mock.patch.object(torch.version, "hip", None):
            with self.assertRaisesRegex(RuntimeError, "CPU fallback"):
                probe.require_gpu()
        props = SimpleNamespace(name="AMD Radeon RX 6600 XT", gcnArchName="gfx1032")
        with mock.patch.object(torch.cuda, "is_available", return_value=True), \
             mock.patch.object(torch.version, "hip", "synthetic"), \
             mock.patch.object(torch.cuda, "get_device_properties", return_value=props):
            with self.assertRaisesRegex(RuntimeError, "reside"):
                probe.require_gpu(torch.zeros(1))
            props.gcnArchName = "gfx9999"
            with self.assertRaisesRegex(RuntimeError, "reside"):
                probe.require_gpu()
            props.gcnArchName = "gfx1032"
            props.name = "Other device"
            with self.assertRaisesRegex(RuntimeError, "reside"):
                probe.require_gpu()

    def test_all_bn_affine_and_running_values_are_frozen(self):
        head = probe.synthetic_head().eval()
        for name, parameter in head.named_parameters():
            parameter.requires_grad_(name in runner.TRAINABLE)
        saved = {name: value.detach().clone() for name, value in head.named_parameters() if name in runner.FROZEN_BN}
        saved.update({"buffer:" + name: value.detach().clone() for name, value in head.named_buffers()})
        with mock.patch.object(runner, "require_gpu"):
            runner._check_head(head, saved)
            with torch.no_grad():
                head.bn0_weight[0] += 1
            with self.assertRaisesRegex(ValueError, "BN affine"):
                runner._check_head(head, saved)
            with torch.no_grad():
                head.bn0_weight.copy_(saved["bn0_weight"])
                head.bn0_bias[0] = 0.0
                saved["bn0_bias"][0] = 0.0
                head.bn0_bias[0] = -0.0
            with self.assertRaisesRegex(ValueError, "BN affine"):
                runner._check_head(head, saved)
            with torch.no_grad():
                head.bn0_bias.copy_(saved["bn0_bias"])
                head.bn1_bias[0] += 1
            with self.assertRaisesRegex(ValueError, "BN affine"):
                runner._check_head(head, saved)
            with torch.no_grad():
                head.bn1_bias.copy_(saved["bn1_bias"])
                head.bn1_running_variance[0] += 1
            with self.assertRaisesRegex(ValueError, "BN running"):
                runner._check_head(head, saved)

    def test_bundle_tamper_rejected_before_step_reserve_and_completion(self):
        temp_root = journal.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as tmp:
            root = Path(tmp)
            (root / "venv").mkdir()
            (root / "venv" / "runtime.bin").write_bytes(b"runtime")
            (root / "probe.py").write_bytes(b"probe")
            (root / "frozen_trunk_head.py").write_bytes(b"head")
            (root / "model.py").write_bytes(b"model")
            (root / "postprocess.py").write_bytes(b"postprocess")
            manifest = root / "execution-bundle.json"
            with mock.patch.object(execution_bundle, "ROOT", root), \
                 mock.patch.object(execution_bundle, "VENV", Path("venv")), \
                 mock.patch.object(execution_bundle, "SOURCE_PATHS", ("probe.py", "frozen_trunk_head.py", "model.py", "postprocess.py")), \
                 mock.patch.object(execution_bundle, "CRITICAL_RUNTIME", ("venv/runtime.bin",)), \
                 mock.patch.object(execution_bundle, "MANIFEST", manifest), \
                 mock.patch.object(execution_bundle, "require_runtime"), \
                 mock.patch.object(journal, "ATTEMPTS", root / "attempts"):
                digest = execution_bundle.generate()
                recipe = journal.Recipe(seed=42, epochs=1, learning_rate=0.00001,
                                        weight_decay=0.0001, bundle_sha256=digest)
                receipt = execution_bundle.verify_startup(digest)
                attempt = journal.SingleUseJournal(recipe, receipt)
                order = journal.schedule(recipe)
                (root / "probe.py").write_bytes(b"tampered")
                with self.assertRaisesRegex(ValueError, "bundle file changed"):
                    attempt.reserve(0, order[0])
                (root / "probe.py").write_bytes(b"probe")
                for name in ("model.py", "postprocess.py"):
                    original = (root / name).read_bytes()
                    (root / name).write_bytes(b"tampered")
                    with self.assertRaisesRegex(ValueError, "bundle file changed"):
                        attempt.reserve(0, order[0])
                    (root / name).write_bytes(original)
                attempt.reserve(0, order[0])
                (root / "frozen_trunk_head.py").write_bytes(b"tampered")
                with self.assertRaisesRegex(ValueError, "bundle file changed"):
                    attempt.complete_step(0)
                (root / "frozen_trunk_head.py").write_bytes(b"head")
                attempt.complete_step(0)
                (root / "venv" / "runtime.bin").write_bytes(b"tampered")
                with self.assertRaisesRegex(ValueError, "bundle file changed"):
                    attempt.reserve(1, order[1])
                (root / "venv" / "runtime.bin").write_bytes(b"runtime")
                (root / "venv" / "new.bin").write_bytes(b"injected")
                with self.assertRaisesRegex(ValueError, "inventory changed"):
                    execution_bundle.verify_full(digest)

    def test_full_bundle_failure_precedes_source_model_and_optimizer(self):
        recipe = journal.Recipe(seed=42, epochs=1, learning_rate=0.00001,
                                weight_decay=0.0001, bundle_sha256="a" * 64)
        with mock.patch.object(execution_bundle, "require_runtime"), \
             mock.patch.object(execution_bundle, "verify_full", side_effect=ValueError("full verification failed")), \
             mock.patch.object(runner, "load_bound_source") as load, \
             mock.patch.object(runner, "_new_head") as head, \
             mock.patch.object(torch.optim, "AdamW") as optimizer:
            with self.assertRaisesRegex(ValueError, "full verification failed"):
                runner.run_unregistered_experiment(recipe)
            load.assert_not_called()
            head.assert_not_called()
            optimizer.assert_not_called()

    def test_startup_receipt_is_single_use_and_bundle_bound(self):
        with mock.patch.object(execution_bundle, "verify_full"):
            receipt = execution_bundle.verify_startup("a" * 64)
        with self.assertRaisesRegex(ValueError, "different bundle"):
            execution_bundle.consume_startup(receipt, "b" * 64)
        execution_bundle.consume_startup(receipt, "a" * 64)
        with self.assertRaisesRegex(ValueError, "already used"):
            execution_bundle.consume_startup(receipt, "a" * 64)

    def test_rebase_reuses_venv_hashes_but_startup_detects_changes(self):
        temp_root = journal.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as tmp:
            root = Path(tmp)
            (root / "venv").mkdir()
            (root / "venv" / "runtime.bin").write_bytes(b"runtime")
            (root / "probe.py").write_bytes(b"probe")
            (root / "model.py").write_bytes(b"model")
            old, new = root / "old.json", root / "new.json"
            with mock.patch.object(execution_bundle, "ROOT", root), \
                 mock.patch.object(execution_bundle, "VENV", Path("venv")), \
                 mock.patch.object(execution_bundle, "CRITICAL_RUNTIME", ("venv/runtime.bin",)), \
                 mock.patch.object(execution_bundle, "SOURCE_PATHS", ("probe.py",)), \
                 mock.patch.object(execution_bundle, "MANIFEST", old):
                old_sha = execution_bundle.generate()
            (root / "venv" / "runtime.bin").write_bytes(b"tampered")
            with mock.patch.object(execution_bundle, "ROOT", root), \
                 mock.patch.object(execution_bundle, "VENV", Path("venv")), \
                 mock.patch.object(execution_bundle, "CRITICAL_RUNTIME", ("venv/runtime.bin",)), \
                 mock.patch.object(execution_bundle, "SOURCE_PATHS", ("probe.py", "model.py")), \
                 mock.patch.object(execution_bundle, "MANIFEST", new), \
                 mock.patch.object(execution_bundle, "PREVIOUS_MANIFEST", old), \
                 mock.patch.object(execution_bundle, "PREVIOUS_SHA", old_sha):
                new_sha = execution_bundle.generate_rebased()
                with self.assertRaisesRegex(ValueError, "bundle file changed"):
                    execution_bundle.verify_startup(new_sha)


if __name__ == "__main__":
    unittest.main()
