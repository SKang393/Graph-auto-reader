# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Write a provenance-linked correction for the paired inference batch counter."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
import json
from pathlib import Path
from typing import Any

from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers

ROOT = Path(__file__).resolve().parents[6]
REPORT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-ink-p2-vs-curated-mix-v2/report.json"
CONFIG_RELATIVE = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/train_proposal_curation_v1/paired_ink_scan_eval_v1/paired_ink_eval_v2.json"
REPORT_SHA256 = "6a300ac53cd2a3e8f9045084deb2b513a85afca62ef67cabac083847decfe173"
CONFIG_SHA256 = "6a29013a19d800d6b69d2298e5007ddc1a1ceab449c95b2725d3526b14dde75a"


class CorrectionError(RuntimeError):
    pass


def digest(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def batch_accounting(total_model_batch_calls: int, row_count: int, batch_size: int,
                     model_count: int) -> dict[str, int]:
    if min(total_model_batch_calls, row_count, batch_size, model_count) <= 0:
        raise ValueError("batch accounting inputs must be positive")
    per_model = (row_count + batch_size - 1) // batch_size
    expected_total = per_model * model_count
    if total_model_batch_calls != expected_total:
        raise ValueError("recorded total model-batch calls do not match row/batch/model counts")
    return {"batches_per_model": per_model, "model_count": model_count,
            "total_model_batch_calls": total_model_batch_calls}


def correct() -> tuple[dict[str, Any], dict[str, Any]]:
    report_bound = helpers._bound_read(ROOT, REPORT_RELATIVE, "immutable original paired report", 10_000_000)
    config_bound = helpers._bound_read(ROOT, CONFIG_RELATIVE, "paired evaluation config", 65_536)
    if report_bound.sha256 != REPORT_SHA256 or config_bound.sha256 != CONFIG_SHA256:
        raise CorrectionError("pinned original report or config changed")
    report = helpers._parse_json(report_bound, "original paired report")
    config = helpers._parse_json(config_bound, "paired evaluation config")
    output_dir = report_bound.path.parent
    corrected_path = output_dir / "report.corrected.json"
    receipt_path = output_dir / "batch-counter-correction.json"
    if corrected_path.exists() or corrected_path.is_symlink() or receipt_path.exists() or receipt_path.is_symlink():
        raise CorrectionError("correction output already exists")
    models = [key for key in ("p2_checkpoint_sha256", "mixed_checkpoint_sha256")
              if key in report.get("frozen_model_references", {})]
    accounting = batch_accounting(int(report["inference_batches_per_model"]),
        int(report["counts"]["scan_proposals"]), int(config["crop_batch_size"]), len(models))
    original_counter = int(report["inference_batches_per_model"])
    corrected = dict(report)
    corrected["inference_batches_per_model"] = accounting["batches_per_model"]
    corrected["inference_model_batch_calls_total"] = accounting["total_model_batch_calls"]
    corrected["batch_counter_correction"] = {
        "source_report_sha256": report_bound.sha256,
        "source_config_sha256": config_bound.sha256,
        "correction_source_sha256": digest(Path(__file__).read_bytes()),
        "observed_counter_semantics": "the original counter incremented once for each model in each input batch",
        "derivation": "ceil(scored_rows / crop_batch_size) per model; verified total equals batches_per_model times model_count",
        **accounting,
    }
    corrected_bytes = json.dumps(corrected, ensure_ascii=True, sort_keys=True,
        separators=(",", ":")).encode("utf-8")
    corrected_sha = digest(corrected_bytes)
    receipt = {
        "schema": "graphreader.paired-ink-batch-counter-correction.v1",
        "status": "completed-report-only-correction",
        "original_report_path": REPORT_RELATIVE,
        "original_report_sha256": report_bound.sha256,
        "original_report_unchanged": True,
        "corrected_report_path": "report.corrected.json",
        "corrected_report_sha256": corrected_sha,
        "config_sha256": config_bound.sha256,
        "correction_source_sha256": digest(Path(__file__).read_bytes()),
        "original_counter_value": original_counter,
        "counter_interpretation": "24 total model-batch calls, which is 12 batches for each of two models",
        "accounting": accounting,
        "inference_rerun": False,
        "training_performed": False,
        "dev_sealed_private_inputs_read": False,
        "finished_utc": datetime.now(timezone.utc).isoformat(),
    }
    receipt_bytes = json.dumps(receipt, ensure_ascii=True, sort_keys=True,
        separators=(",", ":")).encode("utf-8")
    helpers._checked_chain(ROOT, output_dir, "paired report correction output")
    corrected_path.write_bytes(corrected_bytes)
    receipt_path.write_bytes(receipt_bytes)
    if digest(report_bound.path.read_bytes()) != REPORT_SHA256:
        raise CorrectionError("original report changed during correction")
    return corrected, receipt


if __name__ == "__main__":
    _, result = correct()
    print(json.dumps(result, sort_keys=True, indent=2))
