# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic ONNX fixture only; no optimizer step or candidate export."""
from hashlib import sha256
from pathlib import Path
import sys
import tempfile
from types import ModuleType, SimpleNamespace
import unittest
from unittest.mock import patch
import numpy as np
import torch
from ml.ocr.official_bakeoff import gpu_head_bridge as bridge
from ml.ocr.official_bakeoff import gpu_head_training as gpu


class BridgeTests(unittest.TestCase):
    def test_public_nine_source_dispatch_binds_reviewed_detector(self):
        split = SimpleNamespace(name='train', source_count=9, panel_count=9, panels=('authenticated',))
        with patch.object(bridge.marker, 'load_model_grid_inputs', return_value=split) as load:
            with patch.object(bridge, '_assemble', return_value=('head', (), {})) as assemble:
                output = bridge.assemble_nine_profile(repository_root=Path.cwd(),
                    model_path=Path.cwd().parent / 'retained-reviewed.onnx',
                    request_path=Path('request.json'), request_sha256='a' * 64,
                    report_path=Path('report.json'), report_sha256='b' * 64)
        self.assertEqual(output, ('head', (), {}))
        self.assertEqual(load.call_args.args[1], 'a' * 64)
        self.assertEqual(load.call_args.args[3], 'b' * 64)
        self.assertEqual(assemble.call_args.args[2], bridge.trunk.REVIEWED_DETECTOR_SHA256)
        self.assertEqual(assemble.call_args.args[0], ('authenticated',))
    def test_relocation_is_explicit_and_restores_authenticator(self):
        from ml.ocr.official_bakeoff import text_extent_head_inputs as inputs
        from ml.ocr.legend_coverage_db_head_v45 import train_p1 as v45
        prefix = 'C:\\old\\'
        candidate = {'detector': {'model_path': prefix+'model', 'manifest_path': prefix+'manifest'},
                     'native_path': prefix+'native',
                     'license_inputs': [{'path': prefix+'license1'}, {'path': prefix+'license2'}]}
        saved = inputs._authenticate_candidate
        mapped = []
        def authenticated(value, root):
            mapped.append(value)
            return value['detector'], Path.cwd() / 'manifest', 'a'*64
        def prepare(root):
            detector, _, _ = inputs._authenticate_candidate(candidate, root)
            self.assertEqual(detector, candidate['detector'])
            return SimpleNamespace(panels=tuple(SimpleNamespace(panel_id=str(i), split='train') for i in range(34)))
        with patch.object(inputs, '_authenticate_candidate', authenticated), patch.object(v45, 'prepare_training', prepare):
            bridge.prepare_historical_train(Path.cwd(), old_prefix=prefix, relocated_root=Path.cwd())
            self.assertIs(inputs._authenticate_candidate, authenticated)
            self.assertEqual(mapped[0]['detector']['model_path'], str(Path.cwd() / 'model'))
            bad = dict(candidate, native_path='D:\\elsewhere\\native')
            def invalid(root):
                inputs._authenticate_candidate(bad, root)
            with patch.object(v45, 'prepare_training', invalid):
                with self.assertRaisesRegex(bridge.BridgeError, 'exact relocation prefix'):
                    bridge.prepare_historical_train(Path.cwd(), old_prefix=prefix, relocated_root=Path.cwd())
            self.assertIs(inputs._authenticate_candidate, authenticated)
        self.assertIs(inputs._authenticate_candidate, saved)
    def test_combined_rejects_cross_profile_collision(self):
        old_panels = tuple(SimpleNamespace(panel_id=f'old-{i}', split='train') for i in range(34))
        new_panels = tuple(SimpleNamespace(panel_id=f'new-{i}', split='train') for i in range(9))
        old = SimpleNamespace(panels=old_panels, config_sha256='a'*64,
            feature_inventory_sha256='b'*64, target_inventory_sha256='c'*64,
            parity_maximum_absolute_error=0.0)
        args = dict(historical_root=Path.cwd(), old_prefix='C:\\old\\', relocated_root=Path.cwd(),
            repository_root=Path.cwd(), model_path=Path('model'), request_path=Path('request'),
            request_sha256='d'*64, report_path=Path('report'), report_sha256='e'*64)
        with patch.object(bridge, 'prepare_historical_train', return_value=old):
            with patch.object(bridge, 'assemble_nine_profile', return_value=('head', new_panels, {})):
                _, panels, evidence = bridge.prepare_combined(**args)
                self.assertEqual(len(panels), 43)
                self.assertTrue(evidence['historical_open_dev_authenticated'])
                self.assertEqual(evidence['optimizer_steps'], 0)
            duplicate = (old_panels[0],) + new_panels[1:]
            with patch.object(bridge, 'assemble_nine_profile', return_value=('head', duplicate, {})):
                with self.assertRaisesRegex(bridge.BridgeError, 'disjoint'):
                    bridge.prepare_combined(**args)
    def test_synthetic_onnx_trunk_and_gpu_head(self):
        gpu.require_device()
        # Reuse the existing ONNX fixture builder; its test module imports pytest,
        # which is not installed in the ROCm environment.
        with patch.dict(sys.modules, {'pytest': ModuleType('pytest')}):
            from ml.ocr.official_bakeoff.tests.test_frozen_trunk_head import _fixture_model
        artifacts = Path.cwd() / 'artifacts'
        artifacts.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='gpu-bridge-fixture-', dir=artifacts) as directory:
            root = Path(directory)
            model = root / 'synthetic-fixture.onnx'
            digest = _fixture_model(model)
            values = np.full((1, 3, 8, 8), 0.25, dtype=np.float32)
            target = np.ones((1, 8, 8), dtype=np.float32)
            item = SimpleNamespace(panel_id='synthetic-fixture', split='train', input_values=values,
                tensor_sha256=sha256(values.tobytes()).hexdigest(), shrink_target=target,
                supervision_mask=target)
            previous = torch.are_deterministic_algorithms_enabled()
            try:
                torch.use_deterministic_algorithms(True)
                with patch.object(torch.optim.AdamW, 'step', side_effect=AssertionError('step forbidden')):
                    model_head, panels, evidence = bridge._assemble([item], model, digest, root)
                    self.assertEqual(tuple(panels[0].features.shape), (1, 96, 2, 2))
                    self.assertFalse(panels[0].features.flags.writeable)
                    self.assertEqual(panels[0].split, 'train')
                    self.assertLessEqual(evidence['maximum_absolute_error'], 1e-5)
                    samples = gpu._samples(panels)
                    model_head.to(gpu.DEVICE)
                    loss, _ = bridge.training.masked_db_dice_loss(model_head.forward_logits(samples[0].features),
                                                                  samples[0].target, samples[0].mask)
                    loss.backward()
                    gpu._assert_residency(model_head, samples, gradients=True)
                    item.tensor_sha256 = '0' * 64
                    with self.assertRaisesRegex(bridge.BridgeError, 'tensor identity changed'):
                        bridge._assemble([item], model, digest, root)
            finally:
                torch.use_deterministic_algorithms(previous)


if __name__ == '__main__':
    unittest.main()
