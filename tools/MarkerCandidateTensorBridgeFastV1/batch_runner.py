# SPDX-License-Identifier: Apache-2.0
"""Fast train-only wrapper around the pinned V1 batch accounting runner.

This command is intentionally not launched by the package tests. It requires a
caller-pinned protocol and the V1 runner's fixed 64-source train descriptor.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import uuid
from pathlib import Path
from types import ModuleType
from typing import Any

from .fast_verifier import FastVerifier, PROTOCOL_PATH as FAST_PROTOCOL_PATH, _strict_object


ROOT = Path(r"E:\Graph-auto-reader")
PACKAGE_ROOT = Path(__file__).resolve().parent
PROTOCOL_PATH = PACKAGE_ROOT / "batch_protocol.json"
V1_ROOT = ROOT / "tools" / "MarkerCandidateTensorBridgeV1"
V1_RUNNER_PATH = V1_ROOT / "runner.py"
V1_RUNNER_PROTOCOL_PATH = V1_ROOT / "runner_protocol.json"
V1_BRIDGE_PATH = V1_ROOT / "bridge.py"
V1_PACKAGE_INIT = V1_ROOT / "__init__.py"
FAST_VERIFIER_PATH = PACKAGE_ROOT / "fast_verifier.py"
PACKAGE_INIT = PACKAGE_ROOT / "__init__.py"


def _hash(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _canonical(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=False, allow_nan=False).encode("utf-8")


def _write_exclusive(path: Path, payload: bytes) -> None:
    with path.open("xb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())


class FastBatchRunner:
    """Run the unchanged V1 batch path with only its sampler evidence adapter replaced."""

    def __init__(self, *, project_root: Path, expected_batch_protocol_sha256: str) -> None:
        self.project_root = Path(project_root).resolve(strict=True)
        FastVerifier._reject_reparse(self.project_root)
        if self.project_root != ROOT.resolve(strict=True):
            raise ValueError("fast batch runner requires the canonical E project root")
        FastVerifier._reject_reparse(PROTOCOL_PATH)
        self.batch_protocol_bytes = PROTOCOL_PATH.read_bytes()
        if _hash(self.batch_protocol_bytes) != expected_batch_protocol_sha256:
            raise ValueError("fast batch protocol differs from caller pin")
        self.protocol = json.loads(self.batch_protocol_bytes, object_pairs_hook=_strict_object)
        self._validate_protocol()
        self.fast_protocol_bytes = FAST_PROTOCOL_PATH.read_bytes()
        self.fast_protocol_sha256 = _hash(self.fast_protocol_bytes)
        if self.fast_protocol_sha256 != self.protocol["fast_verifier_protocol_sha256"]:
            raise ValueError("fast verifier protocol differs from batch protocol")
        self.fast = FastVerifier(project_root=self.project_root,
            expected_protocol_sha256=self.fast_protocol_sha256)
        self.runner_protocol_bytes = V1_RUNNER_PROTOCOL_PATH.read_bytes()
        self.runner_protocol_sha256 = _hash(self.runner_protocol_bytes)
        if self.runner_protocol_sha256 != self.protocol["v1_runner_protocol_sha256"]:
            raise ValueError("V1 runner protocol differs from fast batch protocol")
        self.runner_module, self.adapted_runner_sha256 = self._load_isolated_v1_runner()
        if self.adapted_runner_sha256 != self.protocol["adapted_v1_runner_sha256"]:
            raise ValueError("adapted V1 runner source differs from batch protocol")

    @staticmethod
    def _checked_source(path: Path, root: Path) -> bytes:
        FastVerifier._reject_reparse(path)
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(root) or not resolved.is_file():
            raise ValueError("batch runner source escapes project root or is not a file")
        return resolved.read_bytes()

    def _validate_protocol(self) -> None:
        expected_fields = {
            "schema", "scope", "fast_verifier_protocol_sha256",
            "v1_runner_protocol_sha256", "adapted_v1_runner_sha256",
            "batch_execution", "runner_transformations", "source_bindings",
            "guarantees", "limitations",
        }
        protocol = self.protocol
        if not isinstance(protocol, dict) or set(protocol) != expected_fields:
            raise ValueError("fast batch protocol fields differ")
        if (protocol.get("schema") != "graphreader.marker-candidate-tensor-fast-batch-runner.v1" or
                protocol.get("scope") != "synthetic_train_split_review_only_no_training" or
                protocol.get("batch_execution") != {
                    "source_count": 64, "validation_passes": 1,
                    "per_source_tensor_retention": "one_source_at_a_time"} or
                protocol.get("runner_transformations") != {
                    "relative_import": "inject_the_hash_pinned_fast_bridge_clone",
                    "sampler_input_format": "full_capture_panel_shared_plane_fast_v1"} or
                protocol.get("guarantees") != {
                    "candidate_order_and_v1_label_semantics": "delegated_to_hash_pinned_v1_batch_core",
                    "source_failure_accounting": "preserve_v1_per_source_statuses",
                    "atomic_publication": "publish_final_directory_only_after_fast_receipt_and_ready_marker",
                    "training_approved": False, "optimizer_steps": 0,
                    "dev_input_supplied": False, "private_input_supplied": False,
                    "sealed_input_supplied": False} or
                protocol.get("limitations") != [
                    "Evidence-envelope hashes differ from V1; tensor payload and review-row semantics are preserved.",
                    "Classifier normalization and production model-store factory/runtime parity are not verified.",
                    "Failed runs may leave an explicitly named .fast-batch-stage-* directory as evidence; no final output is published.",
                    "No actual full batch is launched by tests or this module during import."]):
            raise ValueError("fast batch protocol scope or policy differs")
        bindings = protocol.get("source_bindings")
        expected_names = {"batch_runner.py", "fast_verifier.py", "__init__.py",
                          "v1_runner.py", "v1_runner_protocol.json", "v1_bridge.py", "v1_package_init.py"}
        if not isinstance(bindings, dict) or set(bindings) != expected_names:
            raise ValueError("fast batch source binding set differs")
        source_paths = {
            "batch_runner.py": Path(__file__),
            "fast_verifier.py": FAST_VERIFIER_PATH,
            "__init__.py": PACKAGE_INIT,
            "v1_runner.py": V1_RUNNER_PATH,
            "v1_runner_protocol.json": V1_RUNNER_PROTOCOL_PATH,
            "v1_bridge.py": V1_BRIDGE_PATH,
            "v1_package_init.py": V1_PACKAGE_INIT,
        }
        for name, path in source_paths.items():
            data = self._checked_source(path, self.project_root)
            if _hash(data) != bindings.get(name):
                raise ValueError(f"fast batch source binding differs: {name}")

    def _load_isolated_v1_runner(self) -> tuple[ModuleType, str]:
        runner_bytes = self._checked_source(V1_RUNNER_PATH, self.project_root)
        source = runner_bytes.decode("utf-8")
        relative_import = "from . import bridge"
        inline_sampler = 'result["sampler_input_format"] = "full_capture_panel_inline_v1"'
        if source.count(relative_import) != 1 or source.count(inline_sampler) != 1:
            raise ValueError("V1 runner adaptation anchors are no longer unique")
        adapted = source.replace(relative_import, "bridge = _fast_bridge", 1)
        adapted = adapted.replace(inline_sampler,
            'result["sampler_input_format"] = "full_capture_panel_shared_plane_fast_v1"', 1)
        adapted_bytes = adapted.encode("utf-8")
        module = ModuleType("_marker_candidate_fast_v1_batch_runner")
        module.__file__ = str(V1_RUNNER_PATH)
        namespace = module.__dict__
        namespace["_fast_bridge"] = self.fast.bridge
        exec(compile(adapted_bytes, str(V1_RUNNER_PATH), "exec"), namespace)

        plane_snapshots: dict[str, dict[str, str]] = {}
        bridge = self.fast.bridge
        original_evidence = bridge._full_capture_candidate_evidence
        original_core = bridge._materialize_source_core

        def shared_plane_evidence(panel: dict[str, Any], panel_bytes: bytes,
                                  source_binding: dict[str, Any]):
            if original_evidence is None:  # fixed anchor sanity, never call the old serializer
                raise ValueError("pinned V1 evidence adapter is unavailable")
            evidence, hashes = self.fast_module_fast_evidence(panel_bytes, source_binding)
            source_id = source_binding["source_id"]
            if source_id in plane_snapshots:
                raise ValueError("duplicate source materialization within V1 serial batch")
            plane_snapshots[source_id] = hashes
            return evidence

        def shared_plane_core(**kwargs):
            result = original_core(**kwargs)
            source_id = kwargs["source"]["source_id"]
            hashes = plane_snapshots.get(source_id)
            if hashes is None:
                raise ValueError("shared-plane verification did not precede candidate core")
            result["shared_plane_hashes"] = hashes
            result["source_closure"] = self.fast.source_closure
            result["limitations"].append(
                "Per-view evidence uses a compact in-memory token bound to one source-shared frame/mask snapshot; evidence-envelope hashes differ from V1.")
            return result

        bridge._full_capture_candidate_evidence = shared_plane_evidence
        bridge._materialize_source_core = shared_plane_core
        return module, _hash(adapted_bytes)

    def fast_module_fast_evidence(self, panel_bytes: bytes, source: dict[str, Any]):
        from .fast_verifier import _fast_evidence_map
        return _fast_evidence_map(self.fast.bridge, panel_bytes=panel_bytes, source=source)

    @staticmethod
    def _validate_batch_accounting(report: dict[str, Any], base_receipt: dict[str, Any]) -> None:
        sources = report.get("sources")
        if report.get("source_count") != 64 or not isinstance(sources, list) or len(sources) != 64:
            raise ValueError("V1 batch report does not account for all 64 sources")
        ids = [row.get("source_id") for row in sources if isinstance(row, dict)]
        if len(ids) != 64 or len(set(ids)) != 64 or any(not isinstance(value, str) or not value for value in ids):
            raise ValueError("V1 batch source IDs are missing or duplicated")
        counts = {"completed": 0, "failed": 0}
        totals = {key: 0 for key in ("candidate_count", "captured_candidate_count",
                                    "positive_review_only_rows", "legend_review_only_rows",
                                    "unknown_or_excluded_rows", "tensor_file_count")}
        for row in sources:
            status = row.get("status")
            if status == "completed_review_only":
                counts["completed"] += 1
                for key in totals:
                    value = row.get(key)
                    if type(value) is not int or value < 0:
                        raise ValueError(f"completed source has invalid {key}")
                    totals[key] += value
                if (row["candidate_count"] != row["captured_candidate_count"] or
                        row["tensor_file_count"] != 2 * row["candidate_count"] or
                        row["positive_review_only_rows"] + row["legend_review_only_rows"] +
                        row["unknown_or_excluded_rows"] != row["candidate_count"]):
                    raise ValueError("completed source candidate/tensor/label accounting differs")
                if (row.get("trainable_rows") != 0 or row.get("negative_training_rows") != 0 or
                        row.get("training_approved") is not False or row.get("optimizer_steps") != 0 or
                        row.get("failure_code") is not None):
                    raise ValueError("completed source violates review-only accounting")
            elif status == "failed":
                counts["failed"] += 1
                failure_code = row.get("failure_code")
                if (not isinstance(failure_code, str) or not failure_code.isidentifier() or
                        row.get("trainable_rows") != 0 or row.get("negative_training_rows") != 0 or
                        row.get("training_approved") is not False or row.get("optimizer_steps") != 0):
                    raise ValueError("failed source has invalid failure/review-only fields")
                captured = row.get("captured_candidate_count")
                if type(captured) is not int or captured < 0:
                    raise ValueError("failed source lacks captured candidate accounting")
                totals["captured_candidate_count"] += captured
            else:
                raise ValueError("V1 batch source has an unknown status")
        expected_status = "completed_review_only" if counts["failed"] == 0 else "completed_with_source_failures_review_only"
        if (report.get("source_status_counts") != counts or report.get("status") != expected_status or
                base_receipt.get("source_status_counts") != counts or base_receipt.get("status") != expected_status):
            raise ValueError("V1 batch completion/failure accounting differs")
        for key, value in totals.items():
            if report.get(key) != value or base_receipt.get(key, value) != value:
                raise ValueError(f"V1 batch aggregate differs from per-source {key}")
        for obj in (report, base_receipt):
            if (obj.get("trainable_rows") != 0 or obj.get("negative_training_rows") != 0 or
                    obj.get("training_approved") is not False or obj.get("optimizer_steps") != 0 or
                    obj.get("private_input_supplied") is not False or
                    obj.get("sealed_input_supplied") is not False or
                    obj.get("dev_input_supplied") is not False):
                raise ValueError("V1 batch output violates the review-only, train-split boundary")

    def run_train_batch(self, *, descriptor_bytes: bytes, output_relative: str) -> dict[str, Any]:
        FastVerifier._reject_reparse(PROTOCOL_PATH)
        if _hash(PROTOCOL_PATH.read_bytes()) != _hash(self.batch_protocol_bytes):
            raise ValueError("fast batch protocol changed after initialization")
        parts = output_relative.split("/") if isinstance(output_relative, str) else []
        if (not isinstance(output_relative, str) or "\\" in output_relative or
                len(parts) != 3 or parts[:2] != ["artifacts", "goal22-runs"] or
                any(part in ("", ".", "..") for part in parts)):
            raise ValueError("fast batch output must be one fresh goal22-runs child")
        run_root = self.project_root / "artifacts" / "goal22-runs"
        FastVerifier._reject_reparse(run_root)
        output = self.project_root.joinpath(*parts)
        if output.exists():
            raise FileExistsError("fast batch final output already exists")
        stage_name = f".fast-batch-stage-{uuid.uuid4().hex}"
        stage_relative = f"artifacts/goal22-runs/{stage_name}"
        stage = run_root / stage_name
        FastVerifier._reject_reparse(run_root)
        if stage.exists():
            raise FileExistsError("fast batch staging path collision")
        self.last_staging_relative = stage_relative
        result = self.runner_module.run_train_batch(
            descriptor_bytes=descriptor_bytes, project_root=self.project_root,
            output_relative=stage_relative,
            expected_runner_protocol_sha256=self.runner_protocol_sha256)
        FastVerifier._reject_reparse(stage)
        if not stage.is_dir():
            raise ValueError("V1 batch runner did not create the requested staging directory")
        report_path = stage / "batch-report.json"
        receipt_path = stage / "receipt.json"
        for path in (report_path, receipt_path):
            FastVerifier._reject_reparse(path)
        report_bytes, receipt_bytes = report_path.read_bytes(), receipt_path.read_bytes()
        if _hash(report_bytes) != result.get("batch_report_sha256") or _canonical(result) != receipt_bytes:
            raise ValueError("V1 batch report/receipt bytes differ from returned receipt")
        report = json.loads(report_bytes, object_pairs_hook=_strict_object)
        base_receipt = json.loads(receipt_bytes, object_pairs_hook=_strict_object)
        self._validate_batch_accounting(report, base_receipt)
        if (report.get("runner_protocol_sha256") != self.runner_protocol_sha256 or
                base_receipt.get("runner_protocol_sha256") != self.runner_protocol_sha256):
            raise ValueError("V1 batch output runner protocol binding differs")
        self._validate_protocol()
        if (_hash(FAST_PROTOCOL_PATH.read_bytes()) != self.fast_protocol_sha256 or
                _hash(V1_RUNNER_PROTOCOL_PATH.read_bytes()) != self.runner_protocol_sha256):
            raise ValueError("pinned runner protocol changed during batch execution")
        batch_receipt = {
            "schema": "graphreader.marker-candidate-tensor-fast-batch-receipt.v1",
            "status": base_receipt["status"], "source_count": 64,
            "source_status_counts": base_receipt["source_status_counts"],
            "fast_batch_protocol_sha256": _hash(self.batch_protocol_bytes),
            "fast_verifier_protocol_sha256": self.fast.protocol_sha256,
            "adapted_v1_runner_sha256": self.adapted_runner_sha256,
            "v1_runner_protocol_sha256": self.runner_protocol_sha256,
            "v1_runner_receipt_sha256": _hash(receipt_bytes),
            "batch_report_sha256": _hash(report_bytes),
            "descriptor_sha256": base_receipt["descriptor_sha256"],
            "published_output": output_relative,
            "staging_directory_name": stage_name,
            "input_sha256": base_receipt["input_sha256"],
            "source_closure_sha256": base_receipt["source_closure_sha256"],
            "candidate_count": base_receipt["candidate_count"],
            "tensor_file_count": base_receipt["tensor_file_count"],
            "trainable_rows": 0, "negative_training_rows": 0,
            "training_approved": False, "optimizer_steps": 0,
            "private_input_supplied": False, "sealed_input_supplied": False,
            "dev_input_supplied": False,
            "limitations": self.protocol["limitations"],
        }
        receipt_path_fast = stage / "fast-batch-receipt.json"
        FastVerifier._reject_reparse(stage)
        _write_exclusive(receipt_path_fast, _canonical(batch_receipt))
        ready = {"schema": "graphreader.marker-candidate-tensor-fast-batch-ready.v1",
            "status": batch_receipt["status"], "fast_batch_receipt_sha256": _hash(_canonical(batch_receipt)),
            "batch_report_sha256": _hash(report_bytes), "published_output": output_relative}
        _write_exclusive(stage / "FAST_BATCH_READY.json", _canonical(ready))
        if output.exists():
            raise FileExistsError("fast batch final output appeared before atomic publication")
        os.rename(stage, output)
        return batch_receipt


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--descriptor", required=True, help="project-relative V1 64-source train descriptor")
    parser.add_argument("--output", required=True, help="fresh artifacts/goal22-runs child")
    parser.add_argument("--expected-batch-protocol-sha256", required=True)
    args = parser.parse_args()
    runner = FastBatchRunner(project_root=ROOT,
        expected_batch_protocol_sha256=args.expected_batch_protocol_sha256)
    descriptor_parts = args.descriptor.split("/")
    if ("\\" in args.descriptor or args.descriptor.startswith("/") or
            any(part in ("", ".", "..") for part in descriptor_parts)):
        raise ValueError("batch descriptor must be a normalized project-relative path")
    descriptor_path = ROOT.joinpath(*descriptor_parts)
    FastVerifier._reject_reparse(descriptor_path)
    if "\\" in args.descriptor or not descriptor_path.is_file() or not descriptor_path.resolve(strict=True).is_relative_to(ROOT):
        raise ValueError("batch descriptor must be a project-relative regular file")
    receipt = runner.run_train_batch(descriptor_bytes=descriptor_path.read_bytes(),
                                    output_relative=args.output)
    print(json.dumps(receipt, sort_keys=True))
    return 0 if receipt["status"] == "completed_review_only" else 2


if __name__ == "__main__":
    raise SystemExit(main())
