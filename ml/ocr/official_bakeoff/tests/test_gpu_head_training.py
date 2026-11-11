# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic device and parity checks. No optimizer steps or weight exports."""
import unittest
from unittest.mock import patch
import numpy as np
import torch
from ml.ocr.official_bakeoff import frozen_head_training as base
from ml.ocr.official_bakeoff import frozen_trunk_head as trunk
from ml.ocr.official_bakeoff import gpu_head_training as gpu


def head():
    rng = np.random.default_rng(393)
    constants = {n: rng.normal(0, 0.02, size=s).astype(np.float32)
                 for n, s in trunk.TRAINABLE_CONSTANT_SHAPES.items()}
    constants.update({n: (np.ones(s, dtype=np.float32) if n.endswith('w_2') else np.zeros(s, dtype=np.float32))
                      for n, s in trunk.FROZEN_BATCH_NORM_SHAPES.items()})
    return trunk.FrozenDbHead(constants)


def panel(name, positive, split='train'):
    def immutable(a):
        return np.frombuffer(a.tobytes(), dtype=np.float32).reshape(a.shape)
    target = np.zeros((1, 4, 4), dtype=np.float32)
    if positive:
        target[0, 1:3, 1:3] = 1
    return base.TrainingPanel(name, split, immutable(np.full((1, 96, 1, 1), 0.25, dtype=np.float32)),
                              immutable(target), immutable(np.ones((1, 4, 4), dtype=np.float32)))


class GpuHeadTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        previous = torch.are_deterministic_algorithms_enabled()
        cls.addClassCleanup(torch.use_deterministic_algorithms, previous)
        torch.use_deterministic_algorithms(True)

    def test_rejects_missing_rocm(self):
        with patch.object(torch.version, 'hip', None), self.assertRaisesRegex(base.FrozenHeadTrainingError, 'CPU fallback is forbidden'):
            gpu.require_device()

    def test_synthetic_forward_backward_and_residency(self):
        gpu.require_device()
        cpu = head().eval()
        model = head().to(gpu.DEVICE).eval()
        panels = [panel('positive', True), panel('empty', False)]
        samples = gpu._samples(panels)
        optimizer = gpu._optimizer(model)
        gpu._assert_residency(model, samples, optimizer)
        with patch.object(torch.optim.AdamW, 'step', side_effect=AssertionError('optimizer step forbidden')):
            for sample, p in zip(samples, sorted(panels, key=lambda p: p.panel_id)):
                x = torch.tensor(p.features)
                target = torch.tensor(p.target).unsqueeze(0)
                mask = torch.tensor(p.mask).unsqueeze(0)
                with torch.backends.mkldnn.flags(enabled=False):
                    expected, category = base.masked_db_dice_loss(cpu.forward_logits(x), target, mask)
                actual, actual_category = base.masked_db_dice_loss(model.forward_logits(sample.features), sample.target, sample.mask)
                self.assertEqual(category, actual_category)
                self.assertTrue(torch.allclose(actual.detach().cpu(), expected.detach(), atol=1e-5, rtol=1e-5))
                actual.backward()
                gpu._assert_residency(model, samples, optimizer, gradients=True)
                base._validate_gradients(model)
                model.zero_grad(set_to_none=True)

    def test_entrypoint_cancellation_before_first_step(self):
        model = head()
        original = {name: value.detach().clone() for name, value in model.state_dict().items()}
        calls = 0
        previous_threads = torch.get_num_threads()
        observed_threads = []
        def cancel():
            nonlocal calls
            calls += 1
            observed_threads.append(torch.get_num_threads())
            if calls == 2:
                raise base.FrozenHeadTrainingCancelledError('synthetic cancellation')
        with patch.object(torch.optim.AdamW, 'step', side_effect=AssertionError('optimizer step forbidden')):
            with self.assertRaisesRegex(base.FrozenHeadTrainingCancelledError, 'synthetic cancellation'):
                gpu.train_extended_head_gpu(model, [panel('synthetic', True)], cancellation_check=cancel)
        self.assertEqual(calls, 2)
        self.assertEqual(observed_threads, [previous_threads, 16])
        self.assertEqual(torch.get_num_threads(), previous_threads)
        self.assertEqual(next(model.parameters()).device, gpu.DEVICE)
        for name, value in model.state_dict().items():
            self.assertTrue(torch.equal(value.cpu(), original[name]))
    def test_rejects_non_train(self):
        gpu.require_device()
        with self.assertRaisesRegex(base.FrozenHeadTrainingError, 'cannot enter optimization'):
            gpu._samples([panel('dev', True, 'dev')])


if __name__ == '__main__':
    unittest.main()
