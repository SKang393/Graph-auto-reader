# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""No-step tests for admission, device pinning, labels, and durable budget slots."""
from __future__ import annotations

import hashlib
import builtins
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock
from types import SimpleNamespace

import numpy as np
from PIL import Image

from . import corpus, engine
from .corpus import (
    CorpusError, _check_runtime_eligible, _read_json, encode_positive_components,
    encode_reject_components, is_nonnumeric_reject_text, numeric_class_labels, _load_gray_v5_raster,
    project_file, sha256_bytes,
)
from .journal import JournalError, StepBudgetJournal
from .protocol import (
    EXPECTED_GPU_ARCH, EXPECTED_GPU_NAME, PACKAGE, PUBLIC_REJECT_CONTRACT_MANIFEST,
    PUBLIC_REJECT_CONTRACT_SHA256, PUBLIC_V3_CONTRACT_MANIFEST,
    PUBLIC_V3_CONTRACT_SHA256, RECIPE_SHA256, ROOT,
)
from ml.synthetic.io import canonical_json_bytes


class FakeCuda:
    def __init__(self, *, available=True, name=EXPECTED_GPU_NAME, arch=EXPECTED_GPU_ARCH):
        self.available = available
        self.properties = SimpleNamespace(name=name, gcnArchName=arch)

    def is_available(self):
        return self.available

    def get_device_properties(self, index):
        if index != 0:
            raise AssertionError("guard must inspect only cuda:0")
        return self.properties


class FakeTorch:
    def __init__(self, *, hip="10.1.0", available=True, name=EXPECTED_GPU_NAME, arch=EXPECTED_GPU_ARCH):
        self.cuda = FakeCuda(available=available, name=name, arch=arch)
        self.version = SimpleNamespace(hip=hip)
        self.optim = SimpleNamespace(AdamW=lambda *args, **kwargs: (_ for _ in ()).throw(AssertionError("optimizer constructed")))

    @staticmethod
    def device(name):
        return name


class FakeTensor:
    def __init__(self, device):
        self.device = device

    @staticmethod
    def numel():
        return 1


class AdmissionTests(unittest.TestCase):
    def test_recipe_pin_matches_bytes_and_checked_in_registration_stays_blocked(self):
        self.assertEqual(hashlib.sha256((PACKAGE / "recipe.json").read_bytes()).hexdigest(), RECIPE_SHA256)
        registration = json.loads((PACKAGE / "registration.json").read_bytes())
        self.assertEqual(registration["state"], "blocked_pending_independent_corpus_and_recipe_review")
        self.assertIs(registration["training_authorized"], False)

    def test_review_receipt_uses_acyclic_canonical_registration_core(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            fixture_root = Path(temporary)
            receipt_path = fixture_root / "artifacts" / "review.json"
            receipt_path.parent.mkdir()
            registration = {
                "schema": "graphreader.gpu-numeric-v5-train.registration.v1",
                "state": "registered_after_independent_review",
                "training_authorized": True,
                "positive_corpus": {"artifact_root": "artifacts/positive", "manifest_path": "artifacts/positive/manifest.json", "manifest_sha256": "1" * 64},
                "positive_source_bundle": {"source.py": "8" * 64},
                "positive_v5_component_corpus": {"artifact_root": "artifacts/components", "manifest_path": "artifacts/components/manifest.json", "manifest_sha256": "2" * 64},
                "reject_corpus": {"artifact_root": "artifacts/reject", "manifest_path": "artifacts/reject/manifest.json", "manifest_sha256": "5" * 64},
                "review_receipt_path": "artifacts/review.json",
                "review_receipt_sha256": "0" * 64,
                "positive_v5_adapter_path": "artifacts/adapter.json",
                "positive_v5_adapter_sha256": "3" * 64,
                "positive_v5_production_source_bindings": {"source.py": "4" * 64},
                "positive_v5_crop_transform": {"schema": "crop-v1", "padding_rule": "bound"},
                "v5_component_source_sha256": "9" * 64,
                "reject_source_bindings": {"reject.py": "a" * 64},
                "run_output_path": "artifacts/run-once",
                "engine_source_bundle_sha256": "b" * 64,
            }
            recipe_sha = "6" * 64
            source_bindings = {"source.py": "4" * 64}
            source_bundle_sha = engine._source_bundle_digest(source_bindings)
            registration["engine_source_bundle_sha256"] = source_bundle_sha
            core_sha = engine.registration_core_sha256(registration)
            receipt = {
                "training_authorized": True,
                "production_approved": False,
                "recipe_sha256": recipe_sha,
                "engine_source_bundle_sha256": source_bundle_sha,
                "registration_core_sha256": core_sha,
                "positive_manifest_sha256": registration["positive_corpus"]["manifest_sha256"],
                "positive_v5_component_manifest_sha256": registration["positive_v5_component_corpus"]["manifest_sha256"],
                "positive_v5_adapter_sha256": registration["positive_v5_adapter_sha256"],
                "positive_v5_production_source_bindings": registration["positive_v5_production_source_bindings"],
                "positive_v5_crop_transform": registration["positive_v5_crop_transform"],
                "reject_manifest_sha256": registration["reject_corpus"]["manifest_sha256"],
            }
            payload = json.dumps(receipt, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
            receipt_path.write_bytes(payload)
            registration["review_receipt_sha256"] = hashlib.sha256(payload).hexdigest()
            self.assertEqual(engine.registration_core_sha256(registration), core_sha)
            registration_bytes = json.dumps(registration, sort_keys=True, indent=2).encode("utf-8") + b"\n"
            (fixture_root / "ml" / "ocr" / "gpu_numeric_v5_train_v1").mkdir(parents=True)
            (fixture_root / "ml" / "ocr" / "gpu_numeric_v5_train_v1" / "registration.json").write_bytes(registration_bytes)

            old_project_file = engine.project_file
            engine.project_file = lambda relative: fixture_root / relative
            try:
                loaded_registration = engine._load_registration()
                self.assertEqual(loaded_registration, registration)
                self.assertEqual(engine._verify_engine_source_bundle(loaded_registration, source_bindings), source_bundle_sha)
                engine._verify_review_receipt(loaded_registration, recipe_sha, source_bundle_sha)

                tampered_registration = json.loads(json.dumps(loaded_registration))
                tampered_registration["positive_v5_crop_transform"]["padding_rule"] = "different"
                with self.assertRaisesRegex(engine.TrainingAdmissionError, "does not bind this exact training run"):
                    engine._verify_review_receipt(tampered_registration, recipe_sha, source_bundle_sha)

                receipt_path.write_bytes(payload + b" ")
                with self.assertRaisesRegex(engine.TrainingAdmissionError, "receipt hash differs"):
                    engine._verify_review_receipt(loaded_registration, recipe_sha, source_bundle_sha)
            finally:
                engine.project_file = old_project_file

    def test_protocol_source_tampering_invalidates_registration_source_identity(self):
        source_bindings = engine._source_bundle()
        self.assertIn("ml/ocr/gpu_numeric_v5_train_v1/protocol.py", source_bindings)
        original_digest = engine._source_bundle_digest(source_bindings)
        registration = {"engine_source_bundle_sha256": original_digest}

        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            fixture_root = Path(temporary)
            protocol_path = fixture_root / "protocol.py"
            protocol_path.write_bytes((PACKAGE / "protocol.py").read_bytes() + b"\n# tampered")
            old_project_file = engine.project_file

            def project_file_with_tampered_protocol(relative):
                if relative == "ml/ocr/gpu_numeric_v5_train_v1/protocol.py":
                    return protocol_path
                return old_project_file(relative)

            engine.project_file = project_file_with_tampered_protocol
            try:
                tampered_digest = engine._source_bundle_digest(engine._source_bundle())
                self.assertNotEqual(tampered_digest, original_digest)
                with self.assertRaisesRegex(engine.TrainingAdmissionError, "engine source bundle differs"):
                    engine._verify_engine_source_bundle(registration, engine._source_bundle())
            finally:
                engine.project_file = old_project_file

    def test_fixed_registration_blocks_before_model_or_optimizer(self):
        fake_torch = FakeTorch()
        old_gpu_guard = engine.require_rx6600xt_gpu
        engine.require_rx6600xt_gpu = lambda _torch: "cuda:0"
        original_import = builtins.__import__

        def reject_model_import(name, *args, **kwargs):
            if name == "ml.ocr.component_ensemble_v5.model":
                raise AssertionError("model must not be constructed before corpus registration")
            return original_import(name, *args, **kwargs)

        try:
            with mock.patch("builtins.__import__", side_effect=reject_model_import):
                with self.assertRaisesRegex(engine.TrainingAdmissionError, "no independently registered"):
                    engine.train_registered("artifacts/goal22-runs/gpu-numeric-v5-training", torch_module=fake_torch)
        finally:
            engine.require_rx6600xt_gpu = old_gpu_guard

    def test_gpu_guard_rejects_unavailable_or_wrong_runtime(self):
        for fake in (FakeTorch(available=False), FakeTorch(hip=None),
                     FakeTorch(name="different GPU"), FakeTorch(arch="gfx90a")):
            with self.subTest(fake=fake):
                with self.assertRaises(engine.TrainingAdmissionError):
                    engine.require_rx6600xt_gpu(fake)

    def test_gpu_guard_accepts_only_pinned_hip_device(self):
        self.assertEqual(engine.require_rx6600xt_gpu(FakeTorch()), "cuda:0")

    def test_every_tensor_guard_rejects_cpu_state(self):
        with self.assertRaisesRegex(engine.TrainingAdmissionError, "escaped fixed HIP device"):
            engine.assert_all_tensors_on_gpu(FakeTorch(), {"weight": FakeTensor("cpu")})
        engine._assert_training_outputs_gpu(FakeTorch(), FakeTensor("cuda:0"), FakeTensor("cuda:0"))
        with self.assertRaisesRegex(engine.TrainingAdmissionError, "escaped fixed HIP device"):
            engine._assert_training_outputs_gpu(FakeTorch(), FakeTensor("cuda:0"), FakeTensor("cpu"))

    def test_output_path_rejects_restricted_scope_and_corpus_overlap(self):
        for relative in ("artifacts/private/run", "artifacts/sealed/run", "artifacts/test/run", "artifacts/tests/run"):
            with self.subTest(relative=relative), self.assertRaisesRegex(engine.TrainingAdmissionError, "prohibited data scope"):
                engine._fixed_output_path(relative)
        with self.assertRaisesRegex(engine.TrainingAdmissionError, "overlaps a corpus artifact root"):
            engine._fixed_output_path("artifacts/corpus/run", protected_artifact_roots=("artifacts/corpus",))
        with self.assertRaisesRegex(engine.TrainingAdmissionError, "overlaps a corpus artifact root"):
            engine._fixed_output_path("artifacts", protected_artifact_roots=("artifacts/corpus",))
        with self.assertRaisesRegex(engine.TrainingAdmissionError, "overlaps a corpus artifact root"):
            engine._fixed_output_path("ARTIFACTS/positivecorpus/run",
                                      protected_artifact_roots=("artifacts/PositiveCorpus",))

    def test_numeric_labels_exclude_percent_and_unknown_characters(self):
        self.assertEqual(numeric_class_labels("-12.4"), (11, 1, 2, 10, 4))
        for invalid in ("", "12%", "abc", "123456789"):
            with self.subTest(invalid=invalid):
                with self.assertRaises(CorpusError):
                    numeric_class_labels(invalid)

    def test_reject_text_must_be_wholly_outside_numeric_alphabet(self):
        self.assertTrue(is_nonnumeric_reject_text("Primary outcome"))
        for invalid in ("", "", "12 sessions", "-", "50%", "Baseline 2"):
            with self.subTest(invalid=invalid):
                self.assertFalse(is_nonnumeric_reject_text(invalid))

    def test_v5_preclassifier_short_circuits_are_not_labeled(self):
        eligible = np.zeros((1, 24, 26), dtype=np.float32)
        eligible[0, 0, 20] = 0.5
        _check_runtime_eligible((eligible,), "eligible")
        too_tall = eligible.copy()
        too_tall[0, 0, 20] = 0.75
        with self.assertRaisesRegex(CorpusError, "structural height guard"):
            _check_runtime_eligible((too_tall,), "too-tall")
        with self.assertRaisesRegex(CorpusError, "component limit"):
            _check_runtime_eligible(tuple(eligible.copy() for _ in range(9)), "too-many")

    def test_positive_count_mismatch_fails_and_reject_components_get_class_13(self):
        raster = np.full((32, 128), 255, dtype=np.uint8)
        raster[10:20, 10:13] = 0
        raster[10:20, 21:24] = 0
        positive = encode_positive_components(raster, "12", "two-numeric-components")
        self.assertEqual(tuple(label for _, label in positive), (1, 2))
        with self.assertRaisesRegex(CorpusError, "do not align one-to-one"):
            encode_positive_components(raster, "1", "mismatched-numeric-components")
        reject = encode_reject_components(raster, "Primary outcome", "nonnumeric-components")
        self.assertEqual(len(reject), 2)
        self.assertEqual(tuple(label for _, label in reject), (13, 13))
        with self.assertRaisesRegex(CorpusError, "V5 numeric alphabet"):
            encode_reject_components(raster, "Baseline 2", "mixed-class")

    def test_real_public_v3_manifest_contract_and_missing_component_adapter(self):
        manifest = _read_json(PUBLIC_V3_CONTRACT_MANIFEST, PUBLIC_V3_CONTRACT_SHA256)
        self.assertEqual(manifest["schema"], "graphreader.synthetic-tick-corpus.v3")
        self.assertEqual(manifest["preset"], "real_range")
        self.assertEqual((len(manifest["source_cases"]), len(manifest["samples"])), (240, 1528))
        self.assertTrue(manifest["synthetic_only"])
        self.assertEqual((manifest["private_reads"], manifest["sealed_reads"], manifest["test_scene_count"]), (0, 0, 0))
        self.assertFalse(manifest["training_approved"])
        self.assertFalse(manifest["production_approved"])
        self.assertEqual(manifest["historical_benchmark_independence"], "unproven")
        self.assertEqual(sha256_bytes(canonical_json_bytes(manifest["source_bundle"])), manifest["source_sha256"])
        self.assertTrue(all(sample["split"] in {"train", "validation"} for sample in manifest["samples"]))
        self.assertTrue(all("v5_component_input" not in sample for sample in manifest["samples"]))
        self.assertTrue(all(numeric_class_labels(sample["text"]) for sample in manifest["samples"]))
        positive_source = project_file(PUBLIC_V3_CONTRACT_MANIFEST)
        self.assertEqual(hashlib.sha256(positive_source.read_bytes()).hexdigest(), PUBLIC_V3_CONTRACT_SHA256)

    def test_real_public_reject_manifest_is_separate_and_unapproved(self):
        manifest = _read_json(PUBLIC_REJECT_CONTRACT_MANIFEST, PUBLIC_REJECT_CONTRACT_SHA256)
        self.assertEqual(manifest["schema"], "graphreader.synthetic-reject-glyph.v1")
        self.assertEqual(manifest["sample_kind"], "non_numeric_text_reject")
        self.assertFalse(manifest["training_approved"])
        self.assertFalse(manifest["production_approved"])
        self.assertEqual((manifest["private_reads"], manifest["sealed_reads"], manifest["test_reads"]), (0, 0, 0))
        self.assertEqual(manifest["counts"]["eligible_samples"], len(manifest["samples"]))
        self.assertEqual(manifest["counts"]["components"], sum(len(row["components"]) for row in manifest["samples"]))
        self.assertTrue(all(row["class_index"] == 13 and is_nonnumeric_reject_text(row["text"])
                            for row in manifest["samples"]))

    def test_raster_hash_and_decode_use_the_same_bytes(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            base = Path(temporary)
            raster_path = base / "crop.png"
            expected = np.full((32, 128), 255, dtype=np.uint8)
            replacement = np.zeros((32, 128), dtype=np.uint8)
            Image.fromarray(expected).save(raster_path, format="PNG")
            expected_payload = raster_path.read_bytes()
            expected_sha = hashlib.sha256(expected_payload).hexdigest()
            pixel_sha = hashlib.sha256(expected.tobytes(order="C")).hexdigest()
            replacement_path = base / "replacement.png"
            Image.fromarray(replacement).save(replacement_path, format="PNG")
            replacement_payload = replacement_path.read_bytes()
            real_open = Image.open
            swapped = False

            def replace_path_before_decode(source, *args, **kwargs):
                nonlocal swapped
                if not swapped:
                    raster_path.write_bytes(replacement_payload)
                    swapped = True
                return real_open(source, *args, **kwargs)

            with mock.patch.object(corpus.Image, "open", side_effect=replace_path_before_decode):
                actual = _load_gray_v5_raster(base, "crop.png", expected_sha, pixel_sha)
            self.assertTrue(swapped)
            np.testing.assert_array_equal(actual, expected)


class JournalTests(unittest.TestCase):
    def setUp(self):
        self.test_temp = ROOT / "artifacts" / "test-temp"
        self.test_temp.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="gpu-v5-journal-", dir=self.test_temp)
        self.root = Path(self.temp.name) / "run"

    def tearDown(self):
        self.temp.cleanup()

    def journal(self, *, create, budget=2):
        return StepBudgetJournal(self.root, run_id="fixed-test-run", maximum_steps=budget,
                                 recipe_sha256="a" * 64, input_bundle_sha256="b" * 64, create=create)

    def test_completed_step_is_durable_and_budget_is_bounded(self):
        journal = self.journal(create=True, budget=1)
        step = journal.begin_step()
        self.assertEqual(step, 1)
        (self.root / "checkpoint.pt").write_bytes(b"dummy-checkpoint-for-journal-only")
        digest = journal.complete_step(step)
        self.assertEqual(journal.completed_steps, 1)
        self.assertEqual(digest, hashlib.sha256((self.root / "checkpoint.pt").read_bytes()).hexdigest())
        journal.verify_latest_checkpoint()
        with self.assertRaisesRegex(JournalError, "budget is exhausted"):
            journal.begin_step()

    def test_unfinished_step_consumes_slot_and_blocks_replay(self):
        journal = self.journal(create=True)
        self.assertEqual(journal.begin_step(), 1)
        with self.assertRaisesRegex(JournalError, "consumed its slot"):
            self.journal(create=False)

    def test_checkpoint_tampering_is_detected(self):
        journal = self.journal(create=True, budget=1)
        step = journal.begin_step()
        (self.root / "checkpoint.pt").write_bytes(b"checkpoint")
        journal.complete_step(step)
        (self.root / "checkpoint.pt").write_bytes(b"changed")
        with self.assertRaisesRegex(JournalError, "checkpoint hash differs"):
            journal.verify_latest_checkpoint()

    def test_step_identity_tampering_is_detected(self):
        journal = self.journal(create=True, budget=1)
        step = journal.begin_step()
        (self.root / "checkpoint.pt").write_bytes(b"checkpoint")
        journal.complete_step(step)
        begin_path = self.root / "journal" / "step-000001.complete" / "begin.json"
        begin = json.loads(begin_path.read_text(encoding="utf-8"))
        begin["input_bundle_sha256"] = "c" * 64
        begin_path.write_text(json.dumps(begin), encoding="utf-8")
        with self.assertRaisesRegex(JournalError, "step receipt identity differs"):
            _ = journal.completed_steps

    def test_budget_file_mismatch_fails_closed(self):
        journal = self.journal(create=True, budget=1)
        budget_path = self.root / "budget.json"
        value = json.loads(budget_path.read_text(encoding="utf-8"))
        value["maximum_steps"] = 99
        budget_path.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaisesRegex(JournalError, "identity differs"):
            self.journal(create=False, budget=1)


if __name__ == "__main__":
    unittest.main()
