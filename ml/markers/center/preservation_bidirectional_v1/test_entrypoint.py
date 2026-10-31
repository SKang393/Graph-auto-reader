# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic admission and train-only preflight tests; no optimizer steps."""
from contextlib import ExitStack
from dataclasses import asdict
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import torch

from . import entrypoint, runner
from .source import _load_bytes, V37_CHECKPOINT_SHA256
from ..real_positive_context_v1 import test_registered_source as synthetic_registered_fixture
from ..real_positive_context_v1.test_positive_archive import _KEY
from ..real_positive_context_v1.mixed_source import FamilyRows
from ..real_positive_context_v1.schedule import population_sha256
from ..real_positive_context_v1 import registered_source as registered


class RegistrationTests(unittest.TestCase):
    def setUp(self):
        recipe = {"component_retention": runner.preservation.expected_component_config(),
                  "directional_preservation": runner.expected_directional_config()}
        self.protocol = json.dumps({"schema": "graphreader.marker-bidirectional-preservation-protocol.v1",
            "task": "marker-center", "revision": runner.REVISION, "candidate_id": "P1", **recipe,
            "production_approval": False, "sealed_runs_authorized": 0}).encode()
        self.bundle = "d" * 64
        self.config = {"task": "marker-center", "revision": runner.REVISION, "candidate_id": "P1",
            "recipe": asdict(entrypoint.prior.DRAFT_RECIPE), **recipe,
            "expected_runner_source_bundle_sha256": self.bundle, "source_checkpoint_sha256": entrypoint.prior.V37_CHECKPOINT_SHA256,
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
            "authorized_candidate_id": "P1", "status": "candidate_1_preregistered", "preregistered_candidate_ids": ["P1"],
            "consumed_candidate_ids": [], "sealed_runs_authorized": 0, "optimizer_steps_expected": runner.TOTAL_STEPS,
            "candidate_config_paths": {"P1": entrypoint.CONFIG.as_posix()},
            "candidate_config_sha256": {"P1": sha256(self.raw).hexdigest()},
            "protocol_path": entrypoint.PROTOCOL.as_posix(), "protocol_sha256": sha256(self.protocol).hexdigest()}

    def test_synthetic_registration_and_entrypoint_source_binding(self):
        entrypoint._check_registration({"revisions": [self.entry]}, self.config, self.raw, self.protocol, self.bundle)
        paths = {path.as_posix() for path in entrypoint.REQUIRED_SOURCES}
        self.assertTrue({path.as_posix() for path in runner.REQUIRED_SOURCES}.issubset(paths))
        self.assertIn((runner.LOCAL / "entrypoint.py").as_posix(), paths)
        self.assertIn(entrypoint.PROTOCOL.as_posix(), paths)

    def test_bad_protocol_config_or_bundle_fails_before_intent(self):
        for mode in ("protocol", "bundle", "directional", "consumed"):
            with self.subTest(mode=mode):
                config = json.loads(self.raw)
                ledger = dict(self.entry)
                if mode == "directional":
                    config["directional_preservation"]["component_weight"] = 2.0
                if mode == "consumed":
                    ledger["consumed_candidate_ids"] = ["P1"]
                raw = json.dumps(config).encode()
                ledger["candidate_config_sha256"] = {"P1": sha256(raw).hexdigest()}
                def bad_preflight():
                    entrypoint._check_registration({"revisions": [ledger]}, config, raw,
                        b"wrong" if mode == "protocol" else self.protocol, "e" * 64 if mode == "bundle" else self.bundle)
                    raise AssertionError("invalid registration was accepted")
                with patch.object(entrypoint, "_preflight", side_effect=bad_preflight), \
                     patch.object(entrypoint.prior, "_write_new_durable") as intent:
                    with self.assertRaises(ValueError):
                        entrypoint.run_candidate()
                    intent.assert_not_called()

    def test_mocked_handoff_keeps_locks_through_admission_and_runner(self):
        with tempfile.TemporaryDirectory(dir=r"E:\Graph-auto-reader\artifacts\test-temp") as tmp:
            root = Path(tmp)
            (root / "candidate.json").write_bytes(b"synthetic config")
            events = []
            auth, source, model, optimizer = MagicMock(), MagicMock(), MagicMock(), MagicMock()
            source_context = MagicMock()
            source_context.__enter__.return_value = source
            expected = {"status": "training_complete_unapproved", "checkpoint_sha256": "b" * 64}
            def prepare(_config, _key, stack):
                events.append("inputs")
                stack.callback(lambda: events.append("unlocked"))
                return model, optimizer
            with patch.object(entrypoint, "ROOT", root), patch.object(entrypoint, "CONFIG", Path("candidate.json")), \
                 patch.object(entrypoint, "_preflight", side_effect=lambda: (events.append("preflight") or ({}, b"key"))), \
                 patch.object(entrypoint, "_require_training_gpu", side_effect=lambda: events.append("gpu")), \
                 patch.object(entrypoint, "configure_project_git", side_effect=lambda: events.append("git")), \
                 patch.object(entrypoint, "_prepare_inputs", side_effect=prepare), \
                 patch.object(entrypoint.prior, "_write_new_durable", side_effect=lambda *_: events.append("intent")), \
                 patch.object(entrypoint, "acquire_training_candidate", side_effect=lambda *_, **__: (events.append("acquire") or auth)), \
                 patch.object(entrypoint.RegisteredMixedSource, "open_admitted", return_value=source_context), \
                 patch.object(entrypoint.runner, "run_training", side_effect=lambda *_: (events.append("runner_mock") or expected)):
                self.assertEqual(entrypoint.run_candidate(), expected)
            self.assertEqual(events, ["preflight", "gpu", "git", "inputs", "intent", "acquire", "runner_mock", "unlocked"])

    def test_first_read_intent_blocks_before_new_intent_at_both_checks(self):
        for existing_first in (True, False):
            with self.subTest(existing_first=existing_first), tempfile.TemporaryDirectory(
                    dir=r"E:\Graph-auto-reader\artifacts\test-temp") as tmp:
                root = Path(tmp)
                directory = root / "ml/markers/training-seals/marker-center" / runner.REVISION / "P1"
                directory.mkdir(parents=True)
                first_read = directory / entrypoint.FIRST_READ_INTENT_NAME
                if existing_first:
                    first_read.write_text("synthetic marker", encoding="utf-8")
                def prepare(*_):
                    first_read.write_text("synthetic marker", encoding="utf-8")
                    return MagicMock(), MagicMock()
                with patch.object(entrypoint, "ROOT", root), \
                     patch.object(entrypoint, "_preflight", return_value=({}, b"key")), \
                     patch.object(entrypoint, "_require_training_gpu"), patch.object(entrypoint, "configure_project_git"), \
                     patch.object(entrypoint, "_prepare_inputs", side_effect=prepare) as prepared, \
                     patch.object(entrypoint.prior, "_write_new_durable") as intent, \
                     patch.object(entrypoint, "acquire_training_candidate") as acquire:
                    with self.assertRaisesRegex(RuntimeError, "existing single-use attempt"):
                        entrypoint.run_candidate()
                    self.assertEqual(prepared.call_count, 0 if existing_first else 1)
                    intent.assert_not_called()
                    acquire.assert_not_called()
                self.assertFalse((directory / "entrypoint-intent.json").exists())

    def test_real_synthetic_adapter_handoff_rejects_unregistered_runner_before_journal(self):
        """Admission and preflight are mocked; adapter and runner checks are real."""
        fixture = synthetic_registered_fixture.RegisteredSourceTests("test_bound_archive_model_and_single_use_begin")
        fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        observed = []
        open_admitted = entrypoint.RegisteredMixedSource.open_admitted
        def open_source(*args, **kwargs):
            actual = open_admitted(*args, **kwargs)
            self.assertIs(type(actual), entrypoint.RegisteredMixedSource)
            self.assertTrue(actual.verify_input_binding(fixture.config["real_positive_binding_sha256"], "a" * 64))
            observed.append(actual)
            return actual
        with patch.object(entrypoint, "ROOT", fixture.path), patch.object(entrypoint, "CONFIG", Path("config.json")), \
             patch.object(entrypoint, "_preflight", return_value=(fixture.config, _KEY)), \
             patch.object(entrypoint, "_require_training_gpu"), patch.object(entrypoint, "configure_project_git"), \
             patch.object(entrypoint, "_prepare_inputs", return_value=(fixture.model, MagicMock())), \
             patch.object(entrypoint.prior, "_write_new_durable"), \
             patch.object(entrypoint, "acquire_training_candidate", return_value=fixture.authorization), \
             patch.object(entrypoint, "void_candidate"), \
             patch.object(entrypoint.RegisteredMixedSource, "open_admitted", side_effect=open_source), \
             patch.object(entrypoint.runner.prior._RunJournal, "claim") as claim:
            with self.assertRaisesRegex(RuntimeError, "not exactly preregistered"):
                entrypoint.run_candidate()
            claim.assert_not_called()
        self.assertEqual(len(observed), 1)
        self.assertTrue(observed[0]._closed)

    def test_synthetic_registered_adapter_reaches_journal_boundary_without_optimizer(self):
        """Synthetic ledger/admission and mixed rows; real adapter, runner setup and stores."""
        fixture = synthetic_registered_fixture.RegisteredSourceTests("test_bound_archive_model_and_single_use_begin")
        fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        class SyntheticFamily(FamilyRows):
            def __init__(self):
                self._count = 9053
                self._identities = b"".join(sha256(f"synthetic-row-{i}".encode()).digest() for i in range(36212))
        family = SyntheticFamily()
        real_v37 = (entrypoint.DATA / entrypoint.V37).resolve()
        real_base = torch.load(BytesIO(_load_bytes(real_v37, V37_CHECKPOINT_SHA256)), map_location="cpu", weights_only=True)
        fixture.model.local.load_state_dict(real_base["state_dict"], strict=True)
        fixture.config.update(revision=runner.REVISION, candidate_id="P1", optimizer_steps_expected=runner.TOTAL_STEPS,
            source_checkpoint_path=real_v37.relative_to(entrypoint.prior.PROJECT_ROOT).as_posix(),
            source_checkpoint_sha256=V37_CHECKPOINT_SHA256,
            component_retention=runner.preservation.expected_component_config(),
            directional_preservation=runner.expected_directional_config())
        fixture.config["schedule"].update(synthetic_population_count=36212,
            synthetic_population_sha256=population_sha256("synthetic", family.row_identities))
        fixture._write()
        fixture.authorization.binding["runner_source_paths"] = [path.as_posix() for path in entrypoint.REQUIRED_SOURCES]
        protocol_raw = b"synthetic protocol fixture"
        protocol = fixture.path / "synthetic-protocol.json"
        protocol.write_bytes(protocol_raw)
        ledger = fixture.path / "synthetic-ledger.json"
        ledger.write_text(json.dumps({"revisions": [{"task": "marker-center", "revision": runner.REVISION,
            "protocol_path": runner.PROTOCOL.as_posix(), "protocol_sha256": sha256(protocol_raw).hexdigest()}]}))
        original_inside = entrypoint.prior._inside_project
        def inside(root, path):
            resolved = Path(path).resolve()
            if resolved == (root / entrypoint.CANONICAL_LEDGER_PATH).resolve(): return ledger
            if resolved == (root / runner.PROTOCOL).resolve(): return protocol
            return original_inside(root, path)
        fixture.model.to("cuda:0")
        optimizer = torch.optim.AdamW(fixture.model.residual.parameters(), lr=entrypoint.prior.DRAFT_RECIPE.learning_rate,
            weight_decay=entrypoint.prior.DRAFT_RECIPE.weight_decay, capturable=True)
        opened = []
        original_open = entrypoint.RegisteredMixedSource.open_admitted
        def open_source(*args, **kwargs):
            actual = original_open(*args, **kwargs)
            self.assertIs(type(actual), entrypoint.RegisteredMixedSource)
            self.assertEqual(actual.schedule.total_steps, runner.TOTAL_STEPS)
            opened.append(actual)
            return actual
        with patch.object(entrypoint, "ROOT", fixture.path), patch.object(entrypoint, "CONFIG", Path("config.json")), \
             patch.object(entrypoint, "_preflight", return_value=(fixture.config, _KEY)), \
             patch.object(entrypoint, "configure_project_git"), \
             patch.object(entrypoint, "_prepare_inputs", return_value=(fixture.model, optimizer)), \
             patch.object(entrypoint.prior, "_write_new_durable"), \
             patch.object(entrypoint, "acquire_training_candidate", return_value=fixture.authorization), \
             patch.object(entrypoint, "void_candidate"), \
             patch.object(entrypoint.prior, "_inside_project", side_effect=inside), \
             patch.object(entrypoint.prior, "V37_CHECKPOINT_SHA256", V37_CHECKPOINT_SHA256), \
             patch.object(registered, "_ALLOWED_BASES", (real_v37,)), \
             patch.object(registered, "load_bound_family", return_value=(family, fixture.provenance)), \
             patch.object(entrypoint.RegisteredMixedSource, "open_admitted", side_effect=open_source), \
             patch.object(entrypoint.runner.prior._RunJournal, "claim", side_effect=RuntimeError("synthetic journal boundary")) as claim, \
             patch.object(optimizer, "step", side_effect=AssertionError("optimizer step forbidden")) as step:
            with self.assertRaisesRegex(RuntimeError, "synthetic journal boundary"):
                entrypoint.run_candidate()
            claim.assert_called_once()
            step.assert_not_called()
        self.assertEqual(len(opened), 1)
        self.assertTrue(opened[0]._closed)
        self.assertEqual(len(optimizer.state), 0)


class SyntheticPreflightTests(unittest.TestCase):
    def test_all_train_views_are_locked_until_handoff_ends(self):
        opened = []
        original = entrypoint._open_read_only_no_writers
        def lock(path):
            stream = original(path)
            opened.append((Path(path).resolve(), stream))
            return stream
        with patch.object(entrypoint, "_open_read_only_no_writers", side_effect=lock):
            with ExitStack() as stack:
                entrypoint._locked_synthetic_stores(stack)
                self.assertTrue(all(not stream.closed for _, stream in opened))
                names = {path.name for path, _ in opened}
                for view in ("horizontal", "vertical", "both"):
                    self.assertIn(f"component-{view}-features.npy", names)
                    self.assertIn(f"family-{view}-features.npy", names)
                self.assertIn("component-spatial-features.npy", names)
                self.assertIn("family-spatial-features.npy", names)
                with self.assertRaises(OSError):
                    next(path for path, _ in opened if path.name == "family-labels.npy").open("r+b")
            self.assertTrue(all(stream.closed for _, stream in opened))

    def test_gpu_forward_uses_bound_fresh_v37_without_optimizer_step(self):
        raw = _load_bytes(entrypoint.DATA / entrypoint.V37, entrypoint.prior.V37_CHECKPOINT_SHA256)
        model, optimizer = entrypoint._gpu_model(raw)
        self.assertEqual(next(model.parameters()).device.type, "cuda")
        self.assertFalse(model.local.training)
        self.assertEqual(len(optimizer.state), 0)


if __name__ == "__main__":
    unittest.main()
