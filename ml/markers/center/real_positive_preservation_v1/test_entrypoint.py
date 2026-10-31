# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""In-memory preregistration and mocked orchestration checks; no training."""
from dataclasses import asdict
from hashlib import sha256
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch

import torch

from . import entrypoint, runner


class EntrypointTests(unittest.TestCase):
    def setUp(self):
        self.protocol = json.dumps({"schema": "graphreader.marker-real-positive-preservation-protocol.v1",
            "task": "marker-center", "revision": runner.REVISION, "candidate_id": "P1",
            "component_retention": runner.expected_component_config(), "production_approval": False,
            "sealed_runs_authorized": 0}).encode()
        self.bundle = "d" * 64
        self.config = {"task": "marker-center", "revision": runner.REVISION, "candidate_id": "P1",
            "recipe": asdict(entrypoint.prior.DRAFT_RECIPE), "component_retention": runner.expected_component_config(),
            "expected_runner_source_bundle_sha256": self.bundle,
            "source_checkpoint_sha256": entrypoint.prior.V37_CHECKPOINT_SHA256,
            "synthetic_feature_report_sha256": entrypoint.prior.V37_FEATURE_REPORT_SHA256,
            "optimizer_steps_expected": runner.TOTAL_STEPS, "production_approval": False,
            "sealed_runs_authorized": 0, "real_positive_binding_sha256": "a" * 64,
            "schedule": {"algorithm": entrypoint.MIXED_ALGORITHM, "torch_version": str(torch.__version__),
                "synthetic_population_count": 36212, "real_positive_population_count": 12,
                "synthetic_population_sha256": "b" * 64, "real_positive_population_sha256": "c" * 64,
                "synthetic_source_binding_sha256": entrypoint.prior.V37_FEATURE_REPORT_SHA256,
                "real_source_binding_sha256": "a" * 64}}
        self.raw = json.dumps(self.config).encode()
        self.entry = {"task": "marker-center", "revision": runner.REVISION, "execution_authorized": True,
            "authorized_candidate_id": "P1", "status": "candidate_1_preregistered",
            "preregistered_candidate_ids": ["P1"], "consumed_candidate_ids": [], "sealed_runs_authorized": 0,
            "optimizer_steps_expected": runner.TOTAL_STEPS,
            "candidate_config_paths": {"P1": entrypoint.CONFIG.as_posix()},
            "candidate_config_sha256": {"P1": sha256(self.raw).hexdigest()},
            "protocol_path": entrypoint.PROTOCOL.as_posix(), "protocol_sha256": sha256(self.protocol).hexdigest()}

    def _check(self, config=None, raw=None, protocol=None, bundle=None):
        entrypoint._check_registration({"revisions": [self.entry]}, config or self.config, raw or self.raw,
                                       protocol or self.protocol, bundle or self.bundle)

    def test_valid_registration_and_source_membership(self):
        self._check()
        paths = {item.as_posix() for item in runner.REQUIRED_SOURCES}
        self.assertIn((runner.LOCAL / "entrypoint.py").as_posix(), paths)
        self.assertIn(entrypoint.PROTOCOL.as_posix(), paths)

    def test_bad_protocol_bundle_and_component_fail_before_intent(self):
        damaged = json.loads(self.raw)
        damaged["component_retention"]["feature_sha256_by_view"]["identity"] = "0" * 64
        damaged_raw = json.dumps(damaged).encode()
        for mode in ("protocol", "bundle", "component"):
            with self.subTest(mode=mode):
                entry = dict(self.entry)
                if mode == "component":
                    entry["candidate_config_sha256"] = {"P1": sha256(damaged_raw).hexdigest()}
                def bad_preflight():
                    entrypoint._check_registration({"revisions": [entry]},
                        damaged if mode == "component" else self.config,
                        damaged_raw if mode == "component" else self.raw,
                        b"wrong" if mode == "protocol" else self.protocol,
                        "e" * 64 if mode == "bundle" else self.bundle)
                    raise AssertionError("invalid preregistration was accepted")
                with patch.object(entrypoint, "_preflight", side_effect=bad_preflight), \
                     patch.object(entrypoint.prior, "_write_new_durable") as intent:
                    with self.assertRaises(ValueError):
                        entrypoint.run_candidate()
                    intent.assert_not_called()

    def test_valid_flow_calls_runner_after_preflight_and_acquisition(self):
        with tempfile.TemporaryDirectory(dir=r"E:\Graph-auto-reader\artifacts\test-temp") as tmp:
            root = Path(tmp)
            (root / "candidate.json").write_bytes(b"synthetic config")
            events = []
            auth, source, model, optimizer = MagicMock(), MagicMock(), MagicMock(), MagicMock()
            context = MagicMock()
            context.__enter__.return_value = source
            result = {"status": "training_complete_unapproved", "checkpoint_sha256": "b" * 64}
            with patch.object(entrypoint, "ROOT", root), patch.object(entrypoint, "CONFIG", Path("candidate.json")), \
                 patch.object(entrypoint, "_preflight", side_effect=lambda: (events.append("preflight") or ({}, b"key"))), \
                 patch.object(entrypoint, "_require_training_gpu", side_effect=lambda: events.append("gpu")), \
                 patch.object(entrypoint, "configure_project_git", side_effect=lambda: events.append("git")), \
                 patch.object(entrypoint, "_prepare_inputs", side_effect=lambda *_: (events.append("inputs") or (model, optimizer))), \
                 patch.object(entrypoint.prior, "_write_new_durable", side_effect=lambda *_: events.append("intent")), \
                 patch.object(entrypoint, "acquire_training_candidate", side_effect=lambda *_, **__: (events.append("acquire") or auth)), \
                 patch.object(entrypoint.RegisteredMixedSource, "open_admitted", side_effect=lambda *_, **__: (events.append("source") or context)), \
                 patch.object(entrypoint.runner, "run_training", side_effect=lambda *_: (events.append("runner_mock") or result)):
                self.assertEqual(entrypoint.run_candidate(), result)
            self.assertEqual(events, ["preflight", "gpu", "git", "inputs", "intent", "acquire", "source", "runner_mock"])


if __name__ == "__main__":
    unittest.main()
