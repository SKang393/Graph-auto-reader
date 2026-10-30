# SPDX-License-Identifier: Apache-2.0
"""Synthetic preflight and orchestration tests. No optimizer or training."""
from contextlib import ExitStack
from hashlib import sha256
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
from unittest.mock import MagicMock, patch

from . import entrypoint
from . import registered_source as registered
from .test_positive_archive import _build, _KEY, _RUN, _PROVENANCE
from .test_mixed_source import _inputs
from .mixed_source import FamilyRows
from .positive_archive import PositiveArchive
from .schedule import population_sha256


class EntrypointTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=r"E:\Graph-auto-reader\artifacts\test-temp")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.config_path, self.ledger_path = self.directory / "candidate.json", self.directory / "ledger.json"
        self.archive_path, self.receipt_path = self.directory / "archive.bin", self.directory / "receipt.json"
        self.base_path, self.output_path = self.directory / "base.pt", self.directory / "output.pt"
        self.base_path.write_bytes(b"synthetic base")
        self.archive = _build()
        self.archive_path.write_bytes(self.archive)
        provenance = [x.hex() for x in _PROVENANCE]
        prep = dict(zip(registered._PROVENANCE_KEYS, provenance))
        prep.update(selected_source_count=2, train_group_count=2, development_group_count=1,
                    processed_train_group_count=2, positive_proposal_count=3, aggregate_only=True,
                    source_images_output=False, per_case_output=False, inference_cache_output=False,
                    production_approved=False, optimizer_steps=0, sealed_reads=0)
        self.receipt = {"status": "pass", "receipt": {"sha256": sha256(self.archive).hexdigest(),
                        "run_id": _RUN.hex(), "batch_count": 2, "positive_count": 3}, "preparation": prep}
        self.config = {"task": "marker-center", "revision": "fixture-v1", "candidate_id": "P1",
            "recipe": entrypoint.asdict(entrypoint.runner.DRAFT_RECIPE), "production_approval": False,
            "sealed_runs_authorized": 0, "source_checkpoint_sha256": entrypoint.runner.V37_CHECKPOINT_SHA256,
            "synthetic_feature_report_sha256": entrypoint.runner.V37_FEATURE_REPORT_SHA256,
            "optimizer_steps_expected": 8, "expected_runner_source_bundle_sha256": "d" * 64,
            "source_checkpoint_path": self.base_path.relative_to(entrypoint._ROOT).as_posix(),
            "candidate_checkpoint_path": self.output_path.relative_to(entrypoint._ROOT).as_posix(),
            "real_positive_binding_sha256": provenance[0], "archive_key_path": "artifacts/example-key.bin",
            "schedule": {"algorithm": entrypoint.ALGORITHM, "torch_version": str(entrypoint.torch.__version__),
                "synthetic_population_sha256": "b" * 64, "real_positive_population_sha256": "c" * 64,
                "synthetic_population_count": 8, "real_positive_population_count": 3,
                "synthetic_source_binding_sha256": entrypoint.runner.V37_FEATURE_REPORT_SHA256,
                "real_source_binding_sha256": provenance[0]},
            "real_positive_archive": {"receipt_path": self.receipt_path.relative_to(entrypoint._ROOT).as_posix(),
                "receipt_sha256": "", "archive_path": self.archive_path.relative_to(entrypoint._ROOT).as_posix(),
                "file_sha256": sha256(self.archive).hexdigest(), "run_id": _RUN.hex(),
                "key_sha256": sha256(_KEY).hexdigest(), "provenance_sha256": provenance}}
        self.entry = {"task": "marker-center", "revision": "fixture-v1", "candidate_id": "P1",
            "execution_authorized": True, "authorized_candidate_id": "P1", "status": "candidate_1_preregistered",
            "preregistered_candidate_ids": ["P1"], "consumed_candidate_ids": [], "sealed_runs_authorized": 0,
            "optimizer_steps_expected": 8, "candidate_config_paths": {"P1": self.config_path.relative_to(entrypoint._ROOT).as_posix()},
            "candidate_config_sha256": {"P1": ""}}
        self._write()
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        self.stack.enter_context(patch.object(entrypoint, "CANONICAL_LEDGER_PATH", self.ledger_path.relative_to(entrypoint._ROOT)))
        self.stack.enter_context(patch.object(entrypoint, "source_bundle_sha256", return_value="d" * 64))
        self.stack.enter_context(patch.object(entrypoint, "_load_key", return_value=_KEY))
        self.stack.enter_context(patch.object(entrypoint, "_ALLOWED_BASES", (self.base_path,)))
        self.stack.enter_context(patch.object(entrypoint, "configure_project_git"))

    def _write(self):
        receipt = json.dumps(self.receipt, sort_keys=True).encode()
        self.receipt_path.write_bytes(receipt)
        self.config["real_positive_archive"]["receipt_sha256"] = sha256(receipt).hexdigest()
        raw = json.dumps(self.config, sort_keys=True).encode()
        self.config_path.write_bytes(raw)
        self.entry["candidate_config_sha256"]["P1"] = sha256(raw).hexdigest()
        self.ledger_path.write_text(json.dumps({"revisions": [self.entry]}))

    def test_exact_ledger_config_and_receipt_preflight(self):
        config, path, key = entrypoint._preflight("fixture-v1", "P1")
        self.assertEqual(config, self.config)
        self.assertEqual(path, self.config_path.relative_to(entrypoint._ROOT))
        self.assertEqual(key, _KEY)
        self.config_path.write_bytes(b"{}")
        with self.assertRaisesRegex(ValueError, "configuration differs"):
            entrypoint._preflight("fixture-v1", "P1")
        self._write()
        self.receipt_path.write_bytes(b"{}")
        with self.assertRaisesRegex(ValueError, "receipt or key"):
            entrypoint._preflight("fixture-v1", "P1")

    def test_unregistered_or_duplicate_ledger_entries_fail_before_key_read(self):
        self.entry["execution_authorized"] = False
        self._write()
        with patch.object(entrypoint, "_load_key") as key:
            with self.assertRaisesRegex(RuntimeError, "does not authorize"):
                entrypoint._preflight("fixture-v1", "P1")
            key.assert_not_called()
        self.entry["execution_authorized"] = True
        self._write()
        self.ledger_path.write_text(json.dumps({"revisions": [self.entry, self.entry]}))
        with self.assertRaisesRegex(RuntimeError, "uniquely register"):
            entrypoint._preflight("fixture-v1", "P1")

    def test_bad_schedule_or_existing_output_rejected_before_acquisition(self):
        self.config["schedule"]["real_positive_population_count"] = 17
        self._write()
        with self.assertRaisesRegex(ValueError, "cannot cover"):
            entrypoint._preflight("fixture-v1", "P1")
        self.config["schedule"]["real_positive_population_count"] = 3
        self._write()
        self.output_path.write_bytes(b"do not overwrite")
        with self.assertRaisesRegex(ValueError, "checkpoint paths"):
            entrypoint._preflight("fixture-v1", "P1")
        self.assertEqual(self.output_path.read_bytes(), b"do not overwrite")

    def test_gpu_and_gate_precede_exclusive_acquisition_and_failed_attempt_cannot_retry(self):
        events = []
        test_root = self.directory
        with patch.object(entrypoint, "_ROOT", test_root), \
             patch.object(entrypoint, "_preflight", return_value=({}, Path("candidate.json"), _KEY)), \
             patch.object(entrypoint, "_prepare_inputs", return_value=(MagicMock(), MagicMock())), \
             patch.object(entrypoint.runner, "REGISTERED_SOURCE_SCHEDULE_GATE_READY", True, create=True), \
             patch.object(entrypoint, "_require_training_gpu", side_effect=lambda: events.append("gpu")), \
             patch.object(entrypoint, "acquire_training_candidate", side_effect=RuntimeError("synthetic failure")) as acquire:
            (test_root / "candidate.json").write_bytes(b"fixture")
            with self.assertRaisesRegex(RuntimeError, "synthetic failure"):
                entrypoint.run_candidate("fixture-v1", "P1")
            self.assertEqual(events, ["gpu"])
            acquire.assert_called_once()
            intent = test_root / "ml/markers/training-seals/marker-center/fixture-v1/P1/entrypoint-intent.json"
            self.assertTrue(intent.is_file())
            with self.assertRaisesRegex(RuntimeError, "existing single-use"):
                entrypoint.run_candidate("fixture-v1", "P1")
            acquire.assert_called_once()

    def test_unreviewed_gate_or_gpu_failure_never_acquires(self):
        with patch.object(entrypoint, "_preflight", return_value=({}, Path("candidate.json"), _KEY)), \
             patch.object(entrypoint.runner, "REGISTERED_SOURCE_SCHEDULE_GATE_READY", False, create=True), \
             patch.object(entrypoint, "acquire_training_candidate") as acquire:
            with self.assertRaisesRegex(RuntimeError, "not reviewed"):
                entrypoint.run_candidate("fixture-v1", "P1")
            acquire.assert_not_called()
        with patch.object(entrypoint, "_preflight", return_value=({}, Path("candidate.json"), _KEY)), \
             patch.object(entrypoint.runner, "REGISTERED_SOURCE_SCHEDULE_GATE_READY", True, create=True), \
             patch.object(entrypoint, "_require_training_gpu", side_effect=RuntimeError("gpu unavailable")), \
             patch.object(entrypoint, "acquire_training_candidate") as acquire:
            with self.assertRaisesRegex(RuntimeError, "gpu unavailable"):
                entrypoint.run_candidate("fixture-v1", "P1")
            acquire.assert_not_called()

    def test_failed_input_validation_creates_no_candidate_attempt(self):
        test_root = self.directory
        with patch.object(entrypoint, "_ROOT", test_root), \
             patch.object(entrypoint, "_preflight", return_value=({}, Path("candidate.json"), _KEY)), \
             patch.object(entrypoint.runner, "REGISTERED_SOURCE_SCHEDULE_GATE_READY", True, create=True), \
             patch.object(entrypoint, "_require_training_gpu"), \
             patch.object(entrypoint, "_prepare_inputs", side_effect=ValueError("synthetic bad input")), \
             patch.object(entrypoint, "acquire_training_candidate") as acquire:
            with self.assertRaisesRegex(ValueError, "synthetic bad input"):
                entrypoint.run_candidate("fixture-v1", "P1")
            acquire.assert_not_called()
            self.assertFalse((test_root / "ml/markers/training-seals/marker-center/fixture-v1/P1").exists())

    def test_actual_synthetic_archive_and_schedule_are_preflighted_under_locks(self):
        contexts, labels, bound = _inputs()
        family = FamilyRows(contexts, labels, bound)
        with PositiveArchive(self.archive_path, key=_KEY, file_sha256=sha256(self.archive).digest(),
                             run_id=_RUN, provenance=_PROVENANCE) as archive:
            self.config["schedule"]["real_positive_population_sha256"] = population_sha256("real-positive", archive.row_identities)
        self.config["schedule"]["synthetic_population_sha256"] = population_sha256("synthetic", family.row_identities)
        base_hash = sha256(self.base_path.read_bytes()).hexdigest()
        self.config["source_checkpoint_sha256"] = base_hash
        self._write()
        model = MagicMock()
        model.to.return_value = model
        model.parameters.return_value = []
        model.residual.parameters.return_value = []
        with patch.object(entrypoint.runner, "V37_CHECKPOINT_SHA256", base_hash), \
             patch.object(entrypoint, "_require_training_gpu"), \
             patch.object(entrypoint, "_cpu_state"), \
             patch.object(entrypoint.runner, "_require_fresh_state"), \
             patch.object(entrypoint, "load_bound_family", return_value=(family, {})), \
             patch.object(entrypoint.torch, "load", return_value={"state_dict": {}}), \
             patch.object(entrypoint, "SpatialCenterNet"), \
             patch.object(entrypoint, "ScaleClassifierNet"), \
             patch.object(entrypoint, "RealPositiveCenterNet", return_value=model), \
             patch.object(entrypoint.torch.optim, "AdamW") as optimizer:
            config, path, key = entrypoint._preflight("fixture-v1", "P1")
            with ExitStack() as locks:
                actual_model, _ = entrypoint._prepare_inputs(config, path, key, locks)
                self.assertIs(actual_model, model)
                self.assertTrue(optimizer.call_args.kwargs["capturable"])
                for value in (self.archive_path, self.base_path):
                    with self.assertRaises((PermissionError, OSError)):
                        with open(value, "r+b"):
                            pass
            for value in (self.archive_path, self.base_path):
                with open(value, "r+b"):
                    pass
            self.config["schedule"]["synthetic_population_sha256"] = "0" * 64
            self._write()
            config, path, key = entrypoint._preflight("fixture-v1", "P1")
            with ExitStack() as locks, self.assertRaisesRegex(ValueError, "digest"):
                entrypoint._prepare_inputs(config, path, key, locks)
            self.config["schedule"]["synthetic_population_sha256"] = population_sha256("synthetic", family.row_identities)
            self._write()
            config, path, key = entrypoint._preflight("fixture-v1", "P1")
            self.base_path.write_bytes(b"changed base")
            with ExitStack() as locks, self.assertRaisesRegex(ValueError, "V37 checkpoint"):
                entrypoint._prepare_inputs(config, path, key, locks)
            self.base_path.write_bytes(b"synthetic base")
            damaged = bytearray(self.archive)
            damaged[100] ^= 1
            self.archive_path.write_bytes(damaged)
            with ExitStack() as locks, self.assertRaises(ValueError):
                entrypoint._prepare_inputs(config, path, key, locks)
    def test_pre_optimizer_failure_voids_with_sanitized_error(self):
        test_root = self.directory
        (test_root / "candidate.json").write_bytes(b"fixture")
        authorization = MagicMock()
        with patch.object(entrypoint, "_ROOT", test_root), \
             patch.object(entrypoint, "_preflight", return_value=({}, Path("candidate.json"), _KEY)), \
             patch.object(entrypoint, "_prepare_inputs", return_value=(MagicMock(), MagicMock())), \
             patch.object(entrypoint.runner, "REGISTERED_SOURCE_SCHEDULE_GATE_READY", True, create=True), \
             patch.object(entrypoint, "_require_training_gpu"), \
             patch.object(entrypoint, "acquire_training_candidate", return_value=authorization), \
             patch.object(entrypoint.RegisteredMixedSource, "open_admitted", side_effect=ValueError("secret path")), \
             patch.object(entrypoint, "void_candidate") as void:
            with self.assertRaisesRegex(ValueError, "secret path"):
                entrypoint.run_candidate("fixture-v1", "P1")
            self.assertEqual(str(void.call_args.args[1]), "entrypoint_pre_optimizer_failure")

    def test_synthetic_success_orchestrates_snapshot_source_gpu_and_aggregate_output(self):
        test_root = self.directory
        (test_root / "candidate.json").write_bytes(b"fixture")
        source = MagicMock()
        source.verify_model.return_value = True
        context = MagicMock()
        context.__enter__.return_value = source
        model = MagicMock()
        model.to.return_value = model
        model.parameters.return_value = []
        model.residual.parameters.return_value = []
        authorization = MagicMock()
        order = []
        def assert_locked():
            for path in (self.archive_path, self.base_path):
                with self.assertRaises((OSError, PermissionError)):
                    with open(path, "r+b"):
                        pass
        def acquire(*_args, **_kwargs):
            self.assertTrue((test_root / "ml/markers/training-seals/marker-center/fixture-v1/P1/entrypoint-intent.json").is_file())
            assert_locked()
            order.append("acquire")
            return authorization
        def open_source(*_args, **_kwargs):
            assert_locked()
            order.append("registered_source")
            return context
        def run(*_args):
            order.append("runner")
            return {"status": "training_complete_unapproved", "checkpoint_sha256": "b" * 64,
                    "ignored_private_metadata": "never output"}
        raw = b"synthetic checkpoint"
        def prepare(_config, _path, _key, stack):
            stack.enter_context(entrypoint._open_read_only_no_writers(self.archive_path))
            stack.enter_context(entrypoint._open_read_only_no_writers(self.base_path))
            order.append("inputs_ready")
            return model, MagicMock()
        with patch.object(entrypoint, "_ROOT", test_root), \
             patch.object(entrypoint, "_preflight", return_value=({"source_checkpoint_path": "synthetic.pt"}, Path("candidate.json"), _KEY)), \
             patch.object(entrypoint, "_prepare_inputs", side_effect=prepare), \
             patch.object(entrypoint.runner, "REGISTERED_SOURCE_SCHEDULE_GATE_READY", True, create=True), \
             patch.object(entrypoint.runner, "V37_CHECKPOINT_SHA256", sha256(raw).hexdigest()), \
             patch.object(entrypoint, "_require_training_gpu", side_effect=lambda *_: order.append("gpu_check")), \
             patch.object(entrypoint, "acquire_training_candidate", side_effect=acquire), \
             patch.object(entrypoint.RegisteredMixedSource, "open_admitted", side_effect=open_source), \
             patch.object(entrypoint, "_artifact_path", return_value=test_root / "synthetic.pt"), \
             patch.object(entrypoint, "_locked_bytes", return_value=raw), \
             patch.object(entrypoint.torch, "load", return_value={"state_dict": {}}), \
             patch.object(entrypoint, "SpatialCenterNet"), \
             patch.object(entrypoint, "ScaleClassifierNet"), \
             patch.object(entrypoint, "RealPositiveCenterNet", return_value=model), \
             patch.object(entrypoint.runner, "run_training", side_effect=run):
            result = entrypoint.run_candidate("fixture-v1", "P1")
        self.assertEqual(order, ["gpu_check", "inputs_ready", "acquire", "registered_source", "runner"])
        self.assertEqual(result, {"status": "training_complete_unapproved", "checkpoint_sha256": "b" * 64})

    def test_windows_key_acl_accepts_only_protected_authorized_principals(self):
        key_path = self.directory / "synthetic-key.bin"
        key_path.write_bytes(_KEY)
        identity = subprocess.run(["whoami", "/user", "/fo", "csv", "/nh"], capture_output=True,
                                  text=True, check=True).stdout
        match = re.search(r"S-1-5-21-(?:[0-9]+-){3}[0-9]+", identity)
        self.assertIsNotNone(match)
        sid = match.group(0)
        def icacls(*args):
            subprocess.run(["icacls", str(key_path), *args], capture_output=True, text=True, check=True)
        # The test-created file initially inherits its parent ACL.
        with entrypoint._open_read_only_no_writers(key_path) as stream:
            with self.assertRaises(ValueError):
                entrypoint._assert_key_acl(stream)
        icacls("/setowner", "*" + sid)
        icacls("/inheritance:r", "/grant:r", "*" + sid + ":(F)")
        with entrypoint._open_read_only_no_writers(key_path) as stream:
            entrypoint._assert_key_acl(stream)
        icacls("/grant", "*S-1-1-0:(R)")
        with entrypoint._open_read_only_no_writers(key_path) as stream:
            with self.assertRaisesRegex(ValueError, "unauthorized principal"):
                entrypoint._assert_key_acl(stream)


if __name__ == "__main__":
    unittest.main()
