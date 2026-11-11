# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic metadata only; no optimizer, model session or dev truth read."""
from copy import deepcopy
from contextlib import ExitStack
from hashlib import sha256
import json
from pathlib import Path
import tempfile
import sys
from types import ModuleType, SimpleNamespace
import unittest
from unittest.mock import patch

from ml.markers.gate_seal import canonical_json_bytes, source_bundle_sha256
from ml.ocr.official_bakeoff import gpu_head_evaluation as subject
from ml.ocr.official_bakeoff.frozen_head_training import TRAINABLE_PARAMETER_NAMES
from ml.ocr.official_bakeoff.frozen_trunk_head import TRAINABLE_CONSTANT_SHAPES, REVIEWED_DETECTOR_SHA256


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = value if isinstance(value, bytes) else canonical_json_bytes(value)
    path.write_bytes(payload)
    return sha256(payload).hexdigest()


class EvaluationTests(unittest.TestCase):
    def setUp(self):
        artifacts = Path.cwd() / "artifacts"
        artifacts.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="gpu-evaluation-metadata-test-", dir=artifacts)
        self.addCleanup(self.temporary.cleanup)
        base = Path(self.temporary.name)
        self.stage_root, self.historical, self.runtime_root = base / "stage", base / "historical", base / "runtime-source"
        for root in (self.stage_root, self.historical, self.runtime_root):
            root.mkdir()
        for name, value in (("SOURCE_N_APP_SOURCE_SHA256", write(self.stage_root / subject.APP_SOURCE, b"// synthetic reviewed App source\n")),
                            ("SOURCE_N_EVALUATOR_SOURCE_SHA256", write(self.stage_root / subject.EVALUATOR_SOURCE, b"// synthetic reviewed evaluator source\n"))):
            patcher = patch.object(subject, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        driver = Path("runner.py")
        driver_sha = write(self.stage_root / driver, b"# synthetic authorized source\n")
        bundle = source_bundle_sha256(self.stage_root, [driver])
        snapshot = {"sources": [{"path": str(driver), "sha256": driver_sha}]}
        snapshot_sha = write(self.stage_root / "artifacts/snapshot.json", snapshot)
        self.framework = subject._framework()
        self.capture_source = Path("artifacts/historical-source.cs")
        source_sha = write(self.historical / self.capture_source, b"// synthetic historical capture source\n")
        binding_path = Path("artifacts/historical-source-binding.json")
        binding = {"original": {"path": "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadTensorCapture.cs", "sha256": source_sha},
                   "snapshot": self.capture_source.as_posix()}
        binding_sha = write(self.historical / binding_path, binding)
        self.original = {"capture_source": binding["original"], "panels": [{"split": "validation"}]}
        request_path = Path("artifacts/original-request.json")
        request_sha = write(self.historical / request_path, self.original)
        license_path = Path("LICENSES/license.txt")
        license_sha = write(self.historical / license_path, b"Apache-2.0 synthetic notice\n")
        native_path = Path("artifacts/native.dll")
        native_sha = write(self.historical / native_path, b"synthetic native metadata fixture, not loaded")
        detector = self.model("parent", "ocr_detection")
        recognizer = self.model("recognizer", "ocr_recognition")
        self.template = {"schema": "graphreader.frozen-db-head-ocr-candidate.v1",
            "scope": "project-owned-synthetic-train-dev-unapproved-frozen-candidate", "production_approved": False,
            "training_input_ready": False, "composition_version": "original-db-head-candidate-v1",
            "native_path": native_path.as_posix(), "native_sha256": native_sha,
            "native_scope": "reviewed-source-runtime-local-diagnostic",
            "license_inputs": [{"path": license_path.as_posix(), "sha256": license_sha}],
            "execution_assemblies": [], "detector": detector, "recognizer": recognizer}
        template_path = Path("artifacts/template.json")
        template_sha = write(self.historical / template_path, self.template)
        config = {"task": subject.TASK, "revision": subject.REVISION, "candidate_id": "P1", "candidate_count": 1,
            "sealed_budget": 0, "synthetic_only": True, "private_data": False, "sealed_data": False,
            "expected_runner_source_bundle_sha256": bundle,
            "historical_bound_files": {"capture_request": {"path": request_path.as_posix(), "sha256": request_sha},
                "capture_report": {"path": "artifacts/report.json", "sha256": self.framework.ORIGINAL_CAPTURE_SHA256},
                "candidate_template": {"path": template_path.as_posix(), "sha256": template_sha}}}
        self.config_sha = write(self.stage_root / subject.CONFIG, config)
        training = {"epochs": 240, "optimizer_steps": 10320, "panel_count": 43, "positive_panel_count": 43,
            "empty_positive_panels": 0, "seed": 20260939, "batch_size": 1, "learning_rate": .0001,
            "weight_decay": .0001, "dice_epsilon": 1e-6, "torch_backend": "rocm-rx6600xt-cuda0-float64-conv0",
            "selected_epoch": 240, "trainable_parameter_names": list(TRAINABLE_PARAMETER_NAMES),
            "epoch_losses": [{"epoch": i, "optimizer_steps": 43*i, "full_train_loss": 1/i} for i in range(1,241)],
            "frozen_batch_norm_sha256_before": "a"*64, "frozen_batch_norm_sha256_after": "a"*64}
        self.stage_path = Path("artifacts/trained/training-stage.json")
        model_sha = write(self.stage_root / self.stage_path.parent / "candidate.onnx", b"synthetic exported model, never loaded")
        checkpoint_sha = write(self.stage_root / self.stage_path.parent / "selected-head.pt", b"synthetic checkpoint, never loaded")
        self.stage = {"schema": "graphreader.marker-style-gpu-training-stage.v1", "status": "trained_pending_open_dev",
            "task": subject.TASK, "revision": subject.REVISION, "candidate_id": "P1", "private_reads": 0,
            "sealed_reads": 0, "production_approved": False, "training": training,
            "authorization": {"task": subject.TASK, "revision": subject.REVISION, "candidate_id": "P1",
                "candidate_config_path": subject.CONFIG.as_posix(), "candidate_config_sha256": self.config_sha,
                "runner_source_paths": [str(driver)], "runner_source_bundle_sha256": bundle,
                "source_snapshot_path": "artifacts/snapshot.json", "source_snapshot_sha256": snapshot_sha},
            "model_sha256": model_sha, "checkpoint_sha256": checkpoint_sha,
            "trained_onnx_parity_maximum_absolute_error": 1e-6,
            "patch": {"source_sha256": REVIEWED_DETECTOR_SHA256, "output_sha256": model_sha,
                      "changed_constants": list(TRAINABLE_CONSTANT_SHAPES), "unchanged_trainable_constants": []}}
        self.stage_sha = write(self.stage_root / self.stage_path, self.stage)
        runtime_files = []
        for name in (*[n + ".dll" for n in subject.ASSEMBLIES], "GraphReader.SyntheticRuntimeEvidence.deps.json",
                     "GraphReader.SyntheticRuntimeEvidence.runtimeconfig.json"):
            path = self.runtime_root / "bin" / name
            payload = ("synthetic runtime " + name).encode()
            digest = write(path, payload)
            runtime_files.append({"path": path.relative_to(self.runtime_root).as_posix(), "sha256": digest, "byte_count": len(payload)})
        self.runtime_snapshot = Path("snapshot.json")
        self.runtime_sha = write(self.runtime_root / self.runtime_snapshot, {"source_commit": subject.SOURCE_N_COMMIT, "files": runtime_files})
        for name, value in (("SOURCE_N_RUNTIME_SHA256", self.runtime_sha),
                            ("SOURCE_N_APP_DLL_SHA256", next(row["sha256"] for row in runtime_files if row["path"].endswith("GraphReader.App.dll")))):
            patcher = patch.object(subject, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        constants = {"ORIGINAL_REQUEST_SHA256": request_sha, "HISTORICAL_SOURCE": self.capture_source,
            "HISTORICAL_SOURCE_SHA256": source_sha, "HISTORICAL_SOURCE_BINDING": binding_path,
            "HISTORICAL_SOURCE_BINDING_SHA256": binding_sha, "CANDIDATE_TEMPLATE_PATH": template_path,
            "CANDIDATE_TEMPLATE_SHA256": template_sha}
        for key, value in constants.items():
            patcher = patch.object(self.framework, key, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def model(self, name, task):
        path = Path("artifacts") / (name + ".onnx")
        digest = write(self.historical / path, ("synthetic " + name).encode())
        manifest_path = Path("artifacts") / (name + ".json")
        manifest_sha = write(self.historical / manifest_path, {"model_id": name, "model_version": "5.0.0",
            "task": task, "sha256": digest, "providers": ["cpu"],
            "license": {"spdx": "Apache-2.0", "reviewed": True}, "commercial_use": True,
            "redistribution": True, "preprocessing": {"maximum_side_length": 960}, "postprocessing": {"threshold": .3}})
        return {"model_path": path.as_posix(), "model_sha256": digest, "model_id": name, "model_version": "5.0.0",
                "manifest_path": manifest_path.as_posix(), "manifest_sha256": manifest_sha}

    def prepare(self):
        return subject.prepare(stage_root=self.stage_root, stage_path=self.stage_path, stage_sha256=self.stage_sha,
            config_sha256=self.config_sha, historical_root=self.historical, runtime_root=self.runtime_root,
            runtime_snapshot=self.runtime_snapshot, runtime_snapshot_sha256=self.runtime_sha,
            output=self.historical / "artifacts/evaluation")

    def test_new_stage_produces_real_reader_compatible_candidate_and_only_source_path_delta(self):
        before = (self.historical / "artifacts/original-request.json").read_bytes()
        result = self.prepare()
        self.assertEqual(result["primary_open_dev"], {"source_count": 3, "panel_count": 9, "truth_count": 183})
        path = self.historical / result["candidate"]["path"]
        candidate, _ = self.framework.metric.geometry._validate_candidate(self.historical, path, result["candidate"]["sha256"])
        self.assertEqual(candidate["recognizer"], self.template["recognizer"])
        self.assertEqual(candidate["native_sha256"], self.template["native_sha256"])
        self.assertEqual(candidate["license_inputs"][:-1], self.template["license_inputs"])
        self.assertEqual(len(candidate["execution_assemblies"]), 4)
        self.assertEqual(candidate["detector"]["model_id"], subject.MODEL_ID)
        derived = json.loads((self.historical / result["evaluation_request"]["path"]).read_bytes())
        self.framework._validate_historical_request_delta(self.original, derived)
        self.assertEqual((self.historical / "artifacts/original-request.json").read_bytes(), before)
        manifest = json.loads((self.historical / candidate["detector"]["manifest_path"]).read_bytes())
        self.assertEqual(manifest["postprocessing"], {"threshold": .3})
        self.assertEqual(manifest["preprocessing"], {"maximum_side_length": 960})
        with self.assertRaisesRegex(ValueError, "new evaluation"):
            self.prepare()

    def test_rejects_bad_stage_schedule_selection_export_and_scope_before_output(self):
        for mutation in ("dev_scope", "panels", "epochs", "selection", "parity", "patch", "authorization"):
            with self.subTest(mutation=mutation):
                stage = deepcopy(self.stage)
                if mutation == "dev_scope": stage["sealed_reads"] = 1
                elif mutation == "panels": stage["training"]["panel_count"] = 42
                elif mutation == "epochs": stage["training"]["epochs"] = 60
                elif mutation == "selection": stage["training"]["selected_epoch"] = 1
                elif mutation == "parity": stage["trained_onnx_parity_maximum_absolute_error"] = 1e-3
                elif mutation == "patch": stage["patch"]["changed_constants"].pop()
                else: stage["authorization"]["candidate_id"] = "P2"
                self.stage_sha = write(self.stage_root / self.stage_path, stage)
                with self.assertRaises(ValueError): self.prepare()
                self.assertFalse((self.historical / "artifacts/evaluation").exists())

    def test_rejects_changed_runtime_or_checkpoint_bytes_before_output(self):
        path = self.runtime_root / "bin/GraphReader.App.dll"
        payload = path.read_bytes()
        path.write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "runtime bytes"):
            self.prepare()
        path.write_bytes(payload)
        (self.stage_root / self.stage_path.parent / "selected-head.pt").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "checkpoint checksum"):
            self.prepare()
        self.assertFalse((self.historical / "artifacts/evaluation").exists())

    def test_score_authenticates_before_truth_and_reuses_existing_dev_matching(self):
        result = self.prepare()
        preparation_path = self.historical / "artifacts/evaluation/preparation.json"
        preparation_sha = sha256(preparation_path.read_bytes()).hexdigest()
        evaluation_path = self.historical / "artifacts/runtime-result.json"
        evaluation_sha = write(evaluation_path, {"synthetic_unit_metadata": True})
        framework = self.framework
        geometry = framework.metric.geometry
        original = {**self.original, "binding": {"path": "artifacts/preflight.json", "sha256": "a"*64}}
        dev_panels = {str(i): SimpleNamespace(split="validation", source_sha256=f"source-{i%3}") for i in range(9)}
        truths, predictions = [], {f"source-{i}": [] for i in range(3)}
        for i in range(183):
            source = f"source-{i%3}"
            box = framework.metric.Box((i//3)*20, 10, (i//3)*20+10, 20)
            truths.append(framework.metric.FullTextTruth(str(i), source, box, "text", "annotation", "annotation"))
            predictions[source].append(geometry._Prediction(box))
        predictions["source-0"].append(geometry._Prediction(framework.metric.Box(2000, 10, 2010, 20)))
        # This train prediction must not contaminate the fixed dev denominator.
        predictions["train-source"] = [geometry._Prediction(framework.metric.Box(0,0,10,10))]
        evidence = SimpleNamespace(panels=dev_panels, report={"panels": [{"panel_id": str(i), "status": "completed"} for i in range(9)]},
            raw_by_source=predictions, recognized_by_source=predictions,
            explicit_region_failures={"validation": 0}, failed_panel_raw_regions={"validation": 0})
        args = dict(historical_root=self.historical, preparation_path=preparation_path,
            preparation_sha256=preparation_sha, evaluation_path=evaluation_path,
            evaluation_sha256=evaluation_sha, output=self.historical / "artifacts/score.json")
        with ExitStack() as patches:
            patches.enter_context(patch.object(subject, "_scoring_sources", return_value={}))
            patches.enter_context(patch.object(framework, "_validate_historical_requests", return_value=(original, {}, dev_panels)))
            patches.enter_context(patch.object(geometry, "_prevalidate_capture_report"))
            validate = patches.enter_context(patch.object(geometry, "_validate_evaluation", side_effect=ValueError("bad evaluation")))
            truth_reader = patches.enter_context(patch.object(framework.historical_truth, "_fixed_dev_truth", return_value=tuple(truths)))
            patches.enter_context(patch.object(framework.historical_truth, "_read", return_value={}))
            patches.enter_context(patch.object(geometry, "_load_acceptance_bar", return_value=("b"*64, .95, .95)))
            with self.assertRaisesRegex(ValueError, "bad evaluation"):
                subject.score(**args)
            truth_reader.assert_not_called()
            validate.side_effect = None
            validate.return_value = evidence
            scored = subject.score(**args)
            diagnostic = json.loads(preparation_path.read_bytes())
            diagnostic["schema"] = "graphreader.gpu-head-open-dev-diagnostic-preparation.v1"
            diagnostic["diagnostic_receipt"] = diagnostic.pop("training_stage")
            diagnostic.update(export_parity_failed=True, acceptance_blocked=True, candidate_acceptance=False,
                              parity={"absolute_only_passed": False, "mixed_tolerance_passed": False})
            diagnostic_path = preparation_path.parent / "diagnostic-preparation.json"
            diagnostic_sha = write(diagnostic_path, diagnostic)
            with patch.object(subject, "_diagnostic", return_value=({}, self.stage, self.stage_root / self.stage_path.parent / "candidate.onnx")):
                diagnosed = subject.score(**{**args, "preparation_path": diagnostic_path, "preparation_sha256": diagnostic_sha,
                    "output": self.historical / "artifacts/diagnostic-score.json"})
        metric = scored["raw_detector_geometry"]["validation"]
        self.assertEqual(metric["truth_region_count"], 183)
        self.assertEqual(metric["predicted_region_count"], 184)
        self.assertEqual(metric["true_positives"], 183)
        self.assertEqual(metric["false_positives"], 1)
        self.assertEqual(metric["recall"], 1)
        self.assertAlmostEqual(metric["precision"], 183/184)
        self.assertFalse(scored["production_approved"])
        self.assertTrue(diagnosed["export_parity_failed"])
        self.assertTrue(diagnosed["acceptance_blocked"])
        self.assertFalse(diagnosed["candidate_acceptance"])
        self.assertEqual(diagnosed["selected_epoch"], "not recorded")

    def test_historical_path_mapping_uses_only_existing_allowlist_and_restores_reader(self):
        geometry = self.framework.metric.geometry
        original = geometry._read_historical_capture_artifact
        allowed = next(iter(geometry.HISTORICAL_CAPTURE_ARTIFACT_MIRRORS))
        value = subject.OLD_PREFIX + str(allowed)
        with patch.object(geometry, "_read_historical_capture_artifact", return_value=(Path("verified"), b"bytes")) as reader:
            with subject._historical_capture_paths(self.framework, self.historical):
                result = geometry._read_historical_capture_artifact(self.historical, value, "a"*64, "fixture")
                self.assertEqual(result, (Path("verified"), b"bytes"))
                self.assertEqual(reader.call_args.args[1], self.historical.parent.parent.parent / allowed)
                with self.assertRaisesRegex(ValueError, "existing mirror allowlist"):
                    geometry._read_historical_capture_artifact(self.historical, subject.OLD_PREFIX + "unknown.png", "a"*64, "fixture")
            self.assertIs(geometry._read_historical_capture_artifact, reader)
        self.assertIs(geometry._read_historical_capture_artifact, original)

    def test_reviewed_source_receipts_do_not_allow_historical_app_or_matcher_changes(self):
        preparation = self.prepare()
        framework = self.framework
        helper = "ml/frozen-matcher.py"
        helper_path = self.historical / helper
        helper_sha = write(helper_path, b"# fixed matching helper\n")
        with patch.object(framework.metric.geometry, "SOURCE_BINDINGS", {subject.APP_SOURCE: subject.FROZEN_APP_SOURCE_SHA256}), \
             patch.object(framework.metric, "SOURCE_BINDINGS", {subject.EVALUATOR_SOURCE: subject.LEGACY_EVALUATOR_SOURCE_SHA256, helper: helper_sha}):
            observed = subject._scoring_sources(framework, self.historical, preparation)
            self.assertEqual(observed[subject.APP_SOURCE], subject.SOURCE_N_APP_SOURCE_SHA256)
            self.assertEqual(observed[subject.EVALUATOR_SOURCE], subject.SOURCE_N_EVALUATOR_SOURCE_SHA256)
            self.assertEqual(observed[helper], helper_sha)
            bad = deepcopy(preparation)
            bad["reviewed_source_substitutions"][0]["actual_sha256"] = "4b9a4e6a654b89308b972a9b7c1b227f8cdc570d01c5c39f31b5519da5c80e29"
            with self.assertRaisesRegex(ValueError, "substitution changed"):
                subject._scoring_sources(framework, self.historical, bad)
            helper_path.write_bytes(b"# changed matching helper\n")
            with self.assertRaises(ValueError):
                subject._scoring_sources(framework, self.historical, preparation)

    def test_diagnostic_void_is_separate_and_uses_archived_sources_not_current_runner(self):
        from ml.ocr.official_bakeoff import frozen_trunk_head as trunk
        config = json.loads((self.stage_root / subject.CONFIG).read_bytes())
        parent = self.template["detector"]
        config["historical_bound_files"]["parent_model"] = {"path": parent["model_path"], "sha256": parent["model_sha256"]}
        config_sha = write(self.stage_root / subject.CONFIG, config)
        snapshot = json.loads((self.stage_root / "artifacts/snapshot.json").read_bytes())
        snapshot.update(base_commit=subject.SOURCE_N_COMMIT, identity={"task": subject.TASK, "revision": subject.REVISION, "candidate_id": "P1"})
        snapshot_sha = write(self.stage_root / "artifacts/void/source-snapshot.json", snapshot)
        auth = {**self.stage["authorization"], "candidate_config_sha256": config_sha, "source_snapshot_sha256": snapshot_sha}
        opened_sha = write(self.stage_root / "artifacts/void/opened.json", {"status": "opened", "binding": auth})
        void_sha = write(self.stage_root / "artifacts/void/void.json", {"status": "void", "binding": auth,
            "sealed_split_read": False, "budget_consumed": False})
        log_sha = write(self.stage_root / "artifacts/launch.log", b"synthetic failed parity trace")
        receipt = {"schema": "graphreader.marker-style-gpu-void-diagnostic.v1", "scope": "diagnostic-only",
            "candidate_acceptance": False, "private_reads": 0, "sealed_reads": 0, "production_approved": False,
            "selected_epoch": "not recorded", "loss_history": "not recorded", "source_commit": subject.SOURCE_N_COMMIT,
            "source_bundle_sha256": config["expected_runner_source_bundle_sha256"],
            "config": {"path": subject.CONFIG.as_posix(), "sha256": config_sha},
            "source_snapshot": {"path": "artifacts/void/source-snapshot.json", "sha256": snapshot_sha},
            "opened_record": {"path": "artifacts/void/opened.json", "sha256": opened_sha},
            "void_record": {"path": "artifacts/void/void.json", "sha256": void_sha},
            "launch_log": {"path": "artifacts/launch.log", "sha256": log_sha},
            "model": {"path": (self.stage_path.parent / "candidate.onnx").as_posix(), "sha256": self.stage["model_sha256"]},
            "checkpoint": {"path": (self.stage_path.parent / "selected-head.pt").as_posix(), "sha256": self.stage["checkpoint_sha256"]},
            "parity": {"absolute_only_passed": False, "mixed_tolerance_passed": False,
                "absolute_tolerance": 1e-5, "mixed_tolerance_atol": 1e-5, "mixed_tolerance_rtol": 1.3e-6},
            "export_identity": {"trainable_constants_match": 9, "frozen_bn_constants_match": 4}}
        receipt_path = Path("artifacts/diagnostic-receipt.json")
        receipt_sha = write(self.stage_root / receipt_path, receipt)
        # The repaired live runner intentionally no longer matches its archived snapshot.
        (self.stage_root / "runner.py").write_bytes(b"# post-run repair\n")
        with ExitStack() as patches:
            for name, value in (("DIAGNOSTIC_CONFIG_SHA256", config_sha),
                ("DIAGNOSTIC_MODEL_SHA256", self.stage["model_sha256"]),
                ("DIAGNOSTIC_CHECKPOINT_SHA256", self.stage["checkpoint_sha256"]),
                ("DIAGNOSTIC_SOURCE_BUNDLE_SHA256", config["expected_runner_source_bundle_sha256"])):
                patches.enter_context(patch.object(subject, name, value))
            patches.enter_context(patch.object(trunk, "REVIEWED_DETECTOR_SHA256", parent["model_sha256"]))
            patches.enter_context(patch.object(subject, "_verify_export_identity", return_value=receipt["export_identity"]))
            result = subject.prepare_diagnostic(stage_root=self.stage_root, receipt_path=receipt_path, receipt_sha256=receipt_sha,
                historical_root=self.historical, runtime_root=self.runtime_root, runtime_snapshot=self.runtime_snapshot,
                runtime_snapshot_sha256=self.runtime_sha, output=self.historical / "artifacts/diagnostic")
            self.assertTrue(result["acceptance_blocked"])
            self.assertTrue(result["export_parity_failed"])
            self.assertNotIn("training_stage", result)
            self.assertEqual(result["selected_epoch"], "not recorded")
            self.assertFalse((self.historical / "artifacts/diagnostic/training-stage.json").exists())
            with self.assertRaisesRegex(ValueError, "stage identity or scope"):
                subject._stage(self.stage_root, receipt_path, receipt_sha, config_sha)

    def test_diagnostic_export_verifies_actual_nine_head_and_four_bn_values_without_inference(self):
        import torch
        from ml.ocr.official_bakeoff import frozen_trunk_head as trunk
        with patch.dict(sys.modules, {"pytest": ModuleType("pytest")}):
            from ml.ocr.official_bakeoff.tests.test_frozen_trunk_head import _fixture_model
        parent = self.stage_root / "fixture-parent.onnx"
        parent_sha = _fixture_model(parent)
        head = trunk.extract_frozen_trunk_head(parent, parent_sha).head
        with torch.no_grad():
            for parameter in head.parameters():
                parameter.add_(.001)
        candidate = self.stage_root / "fixture-diagnostic.onnx"
        trunk.patch_head_constants(parent, candidate, head, parent_sha)
        checkpoint = self.stage_root / "fixture-checkpoint.pt"
        torch.save(head.state_dict(), checkpoint)
        self.assertEqual(subject._verify_export_identity(parent, candidate, checkpoint, parent_sha),
                         {"trainable_constants_match": 9, "frozen_bn_constants_match": 4})
        with torch.no_grad():
            head.bn0_running_mean.add_(1)
        torch.save(head.state_dict(), checkpoint)
        with self.assertRaisesRegex(ValueError, "batch-normalization"):
            subject._verify_export_identity(parent, candidate, checkpoint, parent_sha)


if __name__ == "__main__":
    unittest.main()
