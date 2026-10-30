# SPDX-License-Identifier: Apache-2.0
"""In-memory synthetic inputs and mocked admission, without data or training."""
from dataclasses import asdict
from hashlib import sha256
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import torch

from ml.markers.training_budget import TrainingAuthorization
from . import registered_source as registered
from .mixed_source import FamilyRows
from .runner_foundation import DRAFT_RECIPE
from .schedule import ALGORITHM, population_sha256
from .test_mixed_source import _inputs
from .test_positive_archive import _build, _KEY, _RUN, _PROVENANCE
from .model import RealPositiveCenterNet
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet


class RegisteredSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=r"E:\Graph-auto-reader\artifacts\test-temp")
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        self.archive_path, self.receipt_path = self.path / "archive.bin", self.path / "receipt.json"
        self.config_path, self.base_path = self.path / "config.json", self.path / "base.pt"
        self.archive_bytes = _build()
        self.archive_path.write_bytes(self.archive_bytes)
        self.model = RealPositiveCenterNet(SpatialCenterNet(ScaleClassifierNet(ModelConfig())))
        torch.save({"state_dict": self.model.local.state_dict()}, self.base_path)
        self.base_sha = sha256(self.base_path.read_bytes()).hexdigest()
        contexts, labels, bound = _inputs()
        self.family = FamilyRows(contexts, labels, bound)
        # Only synthetic fixture pixels are opened by this local test helper.
        from .positive_archive import PositiveArchive
        with PositiveArchive(self.archive_path, key=_KEY, file_sha256=sha256(self.archive_bytes).digest(),
                             run_id=_RUN, provenance=_PROVENANCE) as archive:
            self.real_ids = archive.row_identities
        provenance = [entry.hex() for entry in _PROVENANCE]
        prep = dict(zip(registered._PROVENANCE_KEYS, provenance))
        prep.update(selected_source_count=2, train_group_count=2, development_group_count=1,
                    processed_train_group_count=2, positive_proposal_count=3, aggregate_only=True,
                    source_images_output=False, per_case_output=False, inference_cache_output=False,
                    production_approved=False, optimizer_steps=0, sealed_reads=0)
        self.receipt = {"status": "pass", "receipt": {"sha256": sha256(self.archive_bytes).hexdigest(),
            "run_id": _RUN.hex(), "batch_count": 2, "positive_count": 3}, "preparation": prep}
        self.config = {"recipe": asdict(DRAFT_RECIPE), "optimizer_steps_expected": 8,
            "source_checkpoint_path": self.base_path.relative_to(registered.runner.PROJECT_ROOT).as_posix(),
            "source_checkpoint_sha256": self.base_sha, "synthetic_feature_report_sha256": "a" * 64,
            "real_positive_binding_sha256": provenance[0],
            "real_positive_archive": {"receipt_path": self.receipt_path.relative_to(registered.runner.PROJECT_ROOT).as_posix(),
                "receipt_sha256": "", "archive_path": self.archive_path.relative_to(registered.runner.PROJECT_ROOT).as_posix(),
                "file_sha256": sha256(self.archive_bytes).hexdigest(), "run_id": _RUN.hex(),
                "key_sha256": sha256(_KEY).hexdigest(), "provenance_sha256": provenance},
            "schedule": {"algorithm": ALGORITHM, "torch_version": str(torch.__version__),
                "synthetic_population_sha256": population_sha256("synthetic", self.family.row_identities),
                "real_positive_population_sha256": population_sha256("real-positive", self.real_ids),
                "synthetic_population_count": self.family.count, "real_positive_population_count": 3,
                "synthetic_source_binding_sha256": "a" * 64, "real_source_binding_sha256": provenance[0]}}
        self.provenance = {"runtime_mask_readiness": {"training_input_ready": False, "complete_artifact_mask": False},
                           "component_development_source_disjointness_verified": False}
        self._write()
        self._patches = [patch.object(registered.runner, "_require_bound_source", return_value=self.config),
                         patch.object(registered.runner, "V37_CHECKPOINT_SHA256", self.base_sha),
                         patch.object(registered, "_ALLOWED_BASES", (self.base_path,)),
                         patch.object(registered, "load_bound_family", return_value=(self.family, self.provenance))]
        for item in self._patches:
            item.start()
        self.addCleanup(self._stop_patches)

    def _stop_patches(self):
        for item in reversed(self._patches):
            item.stop()

    def _write(self):
        receipt = json.dumps(self.receipt, sort_keys=True).encode()
        self.receipt_path.write_bytes(receipt)
        self.config["real_positive_archive"]["receipt_sha256"] = sha256(receipt).hexdigest()
        config = json.dumps(self.config, sort_keys=True).encode()
        self.config_path.write_bytes(config)
        self.authorization = TrainingAuthorization(directory=self.path, opened_path=self.path / "opened.json",
            binding={"candidate_config_path": self.config_path.relative_to(registered.runner.PROJECT_ROOT).as_posix(),
                     "candidate_config_sha256": sha256(config).hexdigest()})

    def test_bound_archive_model_and_single_use_begin(self):
        with registered.RegisteredMixedSource.open_admitted(self.authorization, archive_key=_KEY) as source:
            self.assertEqual(source.schedule.total_steps, 8)
            self.assertEqual(source.family_provenance, self.provenance)
            self.assertTrue(source.verify_input_binding(self.config["real_positive_binding_sha256"], "a" * 64))
            self.assertTrue(source.verify_model(self.model, self.base_sha))
            self.assertFalse(source.verify_model(self.model, "0" * 64))
            with torch.no_grad():
                self.model.local.confidence.output.bias.add_(1)
            self.assertFalse(source.verify_model(self.model, self.base_sha))
            self.assertTrue(source.begin_fixed_run(DRAFT_RECIPE, self.authorization))
            self.assertFalse(source.begin_fixed_run(DRAFT_RECIPE, self.authorization))
            with self.assertRaises((OSError, PermissionError)):
                with open(self.archive_path, "r+b"):
                    pass
            self.receipt_path.write_text("altered")
            self.assertFalse(source.verify_input_binding(self.config["real_positive_binding_sha256"], "a" * 64))

    def test_admission_failure_occurs_before_archive_open(self):
        with patch.object(registered.runner, "_require_bound_source", side_effect=RuntimeError("forged")), \
             patch.object(registered, "PositiveArchive") as archive:
            with self.assertRaisesRegex(RuntimeError, "forged"):
                registered.RegisteredMixedSource.open_admitted(self.authorization, archive_key=_KEY)
            archive.assert_not_called()

    def test_wrong_config_hash_and_key_are_rejected(self):
        self.authorization.binding["candidate_config_sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "configuration bytes"):
            registered.RegisteredMixedSource.open_admitted(self.authorization, archive_key=_KEY)
        self._write()
        with self.assertRaisesRegex(ValueError, "receipt or key"):
            registered.RegisteredMixedSource.open_admitted(self.authorization, archive_key=b"x" * 32)

    def test_receipt_flags_provenance_and_actual_counts_are_enforced(self):
        modifications = (lambda: self.receipt["preparation"].update(sealed_reads=1),
                         lambda: self.receipt["preparation"].update(binding_sha256="0" * 64),
                         lambda: (self.receipt["receipt"].update(batch_count=3),
                                  self.receipt["preparation"].update(train_group_count=3, processed_train_group_count=3,
                                                                     selected_source_count=3)))
        for modify in modifications:
            with self.subTest(change=modify):
                original = json.loads(json.dumps(self.receipt))
                modify()
                self._write()
                with self.assertRaises(ValueError):
                    registered.RegisteredMixedSource.open_admitted(self.authorization, archive_key=_KEY)
                self.receipt = original


if __name__ == "__main__":
    unittest.main()
