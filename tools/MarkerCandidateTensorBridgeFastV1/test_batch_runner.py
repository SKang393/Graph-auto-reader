from __future__ import annotations

import json
import shutil
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

import tools.MarkerCandidateTensorBridgeFastV1.batch_runner as batch_module
from tools.MarkerCandidateTensorBridgeFastV1.batch_runner import (
    FastBatchRunner, _canonical, _hash,
)


ROOT = Path(r"E:\Graph-auto-reader")
PROTOCOL = Path(__file__).with_name("batch_protocol.json")


def synthetic_batch(*, failed_index: int | None = None):
    sources = []
    completed = 0
    failed = 0
    candidate_total = captured_total = positive_total = legend_total = unknown_total = tensor_total = 0
    for index in range(64):
        source_id = f"synthetic-source-{index:03d}"
        if index == failed_index:
            failed += 1
            sources.append({"source_index": index, "source_id": source_id,
                "status": "failed", "failure_code": "ValueError",
                "captured_candidate_count": 3, "candidate_count": 0,
                "trainable_rows": 0, "negative_training_rows": 0,
                "training_approved": False, "optimizer_steps": 0})
            captured_total += 3
            continue
        completed += 1
        sources.append({"source_index": index, "source_id": source_id,
            "status": "completed_review_only", "failure_code": None,
            "candidate_count": 2, "captured_candidate_count": 2,
            "positive_review_only_rows": 1, "legend_review_only_rows": 0,
            "unknown_or_excluded_rows": 1, "tensor_file_count": 4,
            "trainable_rows": 0, "negative_training_rows": 0,
            "training_approved": False, "optimizer_steps": 0})
        candidate_total += 2
        captured_total += 2
        positive_total += 1
        unknown_total += 1
        tensor_total += 4
    status_counts = {"completed": completed, "failed": failed}
    status = "completed_review_only" if failed == 0 else "completed_with_source_failures_review_only"
    safety = {"trainable_rows": 0, "negative_training_rows": 0,
              "training_approved": False, "optimizer_steps": 0,
              "private_input_supplied": False, "sealed_input_supplied": False,
              "dev_input_supplied": False}
    report = {"schema": "graphreader.marker-candidate-tensor-bridge-batch-report.v1",
        "status": status, "source_count": 64, "source_status_counts": status_counts,
        "sources": sources, "candidate_count": candidate_total,
        "captured_candidate_count": captured_total,
        "positive_review_only_rows": positive_total,
        "legend_review_only_rows": legend_total,
        "unknown_or_excluded_rows": unknown_total, "tensor_file_count": tensor_total,
        **safety}
    receipt = {"schema": "graphreader.marker-candidate-tensor-bridge-batch-receipt.v1",
        "status": status, "source_count": 64, "source_status_counts": status_counts,
        "batch_report_sha256": _hash(_canonical(report)),
        "descriptor_sha256": "a" * 64, "input_sha256": {"fixture": "b" * 64},
        "source_closure_sha256": "c" * 64,
        "candidate_count": candidate_total, "tensor_file_count": tensor_total,
        **safety}
    return report, receipt


class FastBatchRunnerTests(unittest.TestCase):
    def test_pinned_wrapper_loads_isolated_v1_runner_without_executing_batch(self):
        protocol_bytes = PROTOCOL.read_bytes()
        runner = FastBatchRunner(project_root=ROOT,
            expected_batch_protocol_sha256=_hash(protocol_bytes))
        self.assertIs(runner.runner_module.bridge, runner.fast.bridge)
        self.assertIn("full_capture_panel_shared_plane_fast_v1",
                      runner.runner_module.run_train_batch.__code__.co_consts)
        self.assertEqual(64, runner.protocol["batch_execution"]["source_count"])
        self.assertEqual("one_source_at_a_time",
            runner.protocol["batch_execution"]["per_source_tensor_retention"])

    def test_external_protocol_pin_fails_closed(self):
        with self.assertRaisesRegex(ValueError, "caller pin"):
            FastBatchRunner(project_root=ROOT, expected_batch_protocol_sha256="0" * 64)

    def test_review_only_accounting_accepts_completed_and_failed_source_totals(self):
        report, receipt = synthetic_batch(failed_index=7)
        FastBatchRunner._validate_batch_accounting(report, receipt)

    def test_review_only_accounting_rejects_source_or_policy_tampering(self):
        report, receipt = synthetic_batch(failed_index=7)
        broken = json.loads(json.dumps(report))
        broken["source_status_counts"]["failed"] = 0
        with self.assertRaisesRegex(ValueError, "accounting differs"):
            FastBatchRunner._validate_batch_accounting(broken, receipt)
        broken, receipt = synthetic_batch()
        broken["sources"][0]["training_approved"] = True
        with self.assertRaisesRegex(ValueError, "review-only accounting"):
            FastBatchRunner._validate_batch_accounting(broken, receipt)

    def _fake_v1_batch(self, runner, report, receipt, **kwargs):
        self.assertEqual(b"synthetic descriptor", kwargs["descriptor_bytes"])
        self.assertEqual(runner.runner_protocol_sha256,
                         kwargs["expected_runner_protocol_sha256"])
        stage = ROOT.joinpath(*kwargs["output_relative"].split("/"))
        stage.mkdir(exist_ok=False)
        (stage / "batch-report.json").write_bytes(_canonical(report))
        (stage / "receipt.json").write_bytes(_canonical(receipt))
        return receipt

    def test_fast_receipt_is_written_before_atomic_final_publication(self):
        protocol_bytes = PROTOCOL.read_bytes()
        runner = FastBatchRunner(project_root=ROOT,
            expected_batch_protocol_sha256=_hash(protocol_bytes))
        report, receipt = synthetic_batch(failed_index=7)
        report["runner_protocol_sha256"] = runner.runner_protocol_sha256
        receipt["runner_protocol_sha256"] = runner.runner_protocol_sha256
        receipt["batch_report_sha256"] = _hash(_canonical(report))
        final_name = f"fast-batch-contract-test-{uuid.uuid4().hex}"
        relative = f"artifacts/goal22-runs/{final_name}"
        final = ROOT / relative
        stage = None
        try:
            with patch.object(runner.runner_module, "run_train_batch",
                    side_effect=lambda **kw: self._fake_v1_batch(runner, report, receipt, **kw)):
                fast_receipt = runner.run_train_batch(
                    descriptor_bytes=b"synthetic descriptor", output_relative=relative)
            stage = ROOT.joinpath(*runner.last_staging_relative.split("/"))
            self.assertFalse(stage.exists())
            self.assertTrue(final.is_dir())
            saved = json.loads((final / "fast-batch-receipt.json").read_bytes())
            ready = json.loads((final / "FAST_BATCH_READY.json").read_bytes())
            self.assertEqual(fast_receipt, saved)
            self.assertEqual(_hash((final / "fast-batch-receipt.json").read_bytes()),
                             ready["fast_batch_receipt_sha256"])
            self.assertEqual(relative, ready["published_output"])
            self.assertEqual("completed_with_source_failures_review_only", saved["status"])
            self.assertEqual({"completed": 63, "failed": 1}, saved["source_status_counts"])
            self.assertEqual(0, saved["trainable_rows"])
            self.assertFalse(saved["training_approved"])
            with patch.object(runner.runner_module, "run_train_batch") as never_called:
                with self.assertRaises(FileExistsError):
                    runner.run_train_batch(descriptor_bytes=b"synthetic descriptor",
                                           output_relative=relative)
                never_called.assert_not_called()
        finally:
            if final.exists():
                shutil.rmtree(final)
            if stage is not None and stage.exists():
                shutil.rmtree(stage)

    def test_validation_failure_leaves_only_named_stage_not_final_output(self):
        runner = FastBatchRunner(project_root=ROOT,
            expected_batch_protocol_sha256=_hash(PROTOCOL.read_bytes()))
        report, receipt = synthetic_batch(failed_index=7)
        report["runner_protocol_sha256"] = runner.runner_protocol_sha256
        report["candidate_count"] += 1
        receipt["runner_protocol_sha256"] = runner.runner_protocol_sha256
        receipt["batch_report_sha256"] = _hash(_canonical(report))
        relative = f"artifacts/goal22-runs/fast-batch-validation-fail-{uuid.uuid4().hex}"
        final = ROOT / relative
        try:
            with patch.object(runner.runner_module, "run_train_batch",
                    side_effect=lambda **kw: self._fake_v1_batch(runner, report, receipt, **kw)):
                with self.assertRaisesRegex(ValueError, "aggregate differs"):
                    runner.run_train_batch(descriptor_bytes=b"synthetic descriptor", output_relative=relative)
            stage = ROOT.joinpath(*runner.last_staging_relative.split("/"))
            self.assertFalse(final.exists())
            self.assertTrue(stage.name.startswith(".fast-batch-stage-"))
            self.assertTrue((stage / "receipt.json").is_file())
            self.assertFalse((stage / "fast-batch-receipt.json").exists())
            self.assertFalse((stage / "FAST_BATCH_READY.json").exists())
        finally:
            stage = ROOT.joinpath(*runner.last_staging_relative.split("/"))
            if stage.exists():
                shutil.rmtree(stage)

    def test_fast_receipt_write_failure_cannot_publish_v1_looking_final(self):
        runner = FastBatchRunner(project_root=ROOT,
            expected_batch_protocol_sha256=_hash(PROTOCOL.read_bytes()))
        report, receipt = synthetic_batch()
        report["runner_protocol_sha256"] = runner.runner_protocol_sha256
        receipt["runner_protocol_sha256"] = runner.runner_protocol_sha256
        receipt["batch_report_sha256"] = _hash(_canonical(report))
        relative = f"artifacts/goal22-runs/fast-batch-write-fail-{uuid.uuid4().hex}"
        final = ROOT / relative
        original_write = batch_module._write_exclusive

        def fail_fast_receipt(path, payload):
            if path.name == "fast-batch-receipt.json":
                raise OSError("synthetic receipt write failure")
            return original_write(path, payload)

        try:
            with patch.object(runner.runner_module, "run_train_batch",
                    side_effect=lambda **kw: self._fake_v1_batch(runner, report, receipt, **kw)):
                with patch.object(batch_module, "_write_exclusive", side_effect=fail_fast_receipt):
                    with self.assertRaisesRegex(OSError, "synthetic receipt write failure"):
                        runner.run_train_batch(descriptor_bytes=b"synthetic descriptor", output_relative=relative)
            stage = ROOT.joinpath(*runner.last_staging_relative.split("/"))
            self.assertFalse(final.exists())
            self.assertTrue(stage.is_dir())
            self.assertTrue((stage / "batch-report.json").is_file())
            self.assertTrue((stage / "receipt.json").is_file())
            self.assertFalse((stage / "FAST_BATCH_READY.json").exists())
        finally:
            stage = ROOT.joinpath(*runner.last_staging_relative.split("/"))
            if stage.exists():
                shutil.rmtree(stage)


if __name__ == "__main__":
    unittest.main()
