# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed-candidate guard tests; no optimizer or model export."""
from pathlib import Path
from dataclasses import dataclass
from hashlib import sha256
import json
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
from ml.ocr.official_bakeoff import gpu_head_candidate as candidate


class CandidateTests(unittest.TestCase):
    def test_training_result_survives_later_export_failure(self):
        @dataclass(frozen=True)
        class Result:
            selected_epoch: int = 2
            optimizer_steps: int = 10320
            epoch_losses: tuple = (0.75, 0.25)
        with tempfile.TemporaryDirectory(prefix='gpu-result-fixture-', dir=Path.cwd() / 'artifacts') as temp:
            root = Path(temp)
            def write_checkpoint(path, head):
                path.write_bytes(b'synthetic checkpoint')
            with patch.object(candidate.v39, '_write_checkpoint', write_checkpoint):
                checkpoint = candidate._persist_training_result(root, object(), Result(), {'fixture':True}, {'count':43}, 3.5)
            with self.assertRaisesRegex(RuntimeError, 'injected export failure'):
                raise RuntimeError('injected export failure')
            result = json.loads((root/'training-result.json').read_text(encoding='utf-8'))
            self.assertEqual(result['status'], 'optimization_complete_pending_export')
            self.assertEqual(result['training']['selected_epoch'], 2)
            self.assertEqual(result['training']['optimizer_steps'], 10320)
            self.assertEqual(result['training']['epoch_losses'], [0.75, 0.25])
            self.assertEqual(result['checkpoint_sha256'], sha256(checkpoint.read_bytes()).hexdigest())
            self.assertFalse((root/'training-stage.json').exists())
            self.assertFalse((root/'training-result.pending.json').exists())
            self.assertFalse((root/'selected-head.pending.pt').exists())
            with self.assertRaisesRegex(ValueError, 'already exists'):
                candidate._persist_training_result(root, object(), Result(), {}, {}, 0)
    def test_run_retains_result_when_export_fails_and_voids(self):
        @dataclass(frozen=True)
        class Result:
            epochs: int = 240
            optimizer_steps: int = 10320
            selected_epoch: int = 17
            epoch_losses: tuple = (0.75, 0.25)
        root = Path.cwd()
        with tempfile.TemporaryDirectory(prefix='gpu-run-fixture-', dir=root/'artifacts') as temp:
            output = Path(temp)/'candidate'
            auth = SimpleNamespace(binding={'candidate_config_sha256':'a'*64, 'source_snapshot_sha256':'b'*64},
                                   snapshot_path=Path(temp)/'snapshot')
            with patch.object(candidate, '_config', return_value={}):
                with patch.object(candidate.training_budget, 'acquire_training_candidate', return_value=auth):
                    with patch.object(candidate.training_budget, 'void_candidate') as void:
                        with patch.object(candidate, 'prepare', return_value=(object(), (), {'fixture':True}, root/'parent.onnx')):
                            with patch.object(candidate, 'verify_bound_source_snapshot'):
                                with patch.object(candidate.gpu, 'train_extended_head_gpu', return_value=Result()):
                                    with patch.object(candidate.v39, '_write_checkpoint', side_effect=lambda p,h: p.write_bytes(b'fixture')):
                                        with patch.object(candidate.trunk, 'patch_head_constants', side_effect=RuntimeError('fixture export failure')):
                                            with self.assertRaisesRegex(RuntimeError, 'fixture export failure'):
                                                candidate.run(root, 'a'*64, root, root, 'C:\\old\\', root, output)
            record = json.loads((output/'training-result.json').read_text(encoding='utf-8'))
            self.assertEqual(record['training']['selected_epoch'], 17)
            self.assertEqual(record['training']['optimizer_steps'], 10320)
            self.assertEqual(record['checkpoint_sha256'], sha256((output/'selected-head.pt').read_bytes()).hexdigest())
            self.assertFalse((output/'training-stage.json').exists())
            void.assert_called_once()
    def test_unregistered_candidate_stops_before_preparation_or_output(self):
        root = Path.cwd()
        output = root / 'artifacts' / 'candidate-guard-never-created'
        self.assertFalse(output.exists())
        with patch.object(candidate, '_config', return_value={}):
            with patch.object(candidate.training_budget, 'acquire_training_candidate', side_effect=RuntimeError('not authorized')):
                with patch.object(candidate, 'prepare', side_effect=AssertionError('preparation forbidden')):
                    with self.assertRaisesRegex(RuntimeError, 'not authorized'):
                        candidate.run(root, 'a'*64, root, root, 'C:\\old\\', root, output)
        self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
