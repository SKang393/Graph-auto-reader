# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Aggregate-only V24 diagnosis on five-axis family-disjoint synthetic dev."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time

import numpy as np
import onnxruntime as ort
import torch

from ml.markers.center.mask_preserving_v24.family_scenes import (
    FamilyScene,
    build_family_split,
)
from ml.markers.center.mask_preserving_v24.train_p1 import (
    _evaluate,
    _shared_marker_acceptance_bar,
)
from ml.markers.gate_seal import canonical_json_bytes, sha256_file
from ml.synthetic.dataset import family_holdout_audit


REPO_ROOT = Path(__file__).resolve().parents[4]
RETRY9_RESULT_PATH = Path(
    "ml/markers/center/mask_preserving_v24/P1_RETRY9_RESULT.json"
)
SOURCE_PATHS = (
    Path("ml/markers/center/mask_preserving_v24/diagnose_family_dev.py"),
    Path("ml/markers/center/mask_preserving_v24/family_scenes.py"),
    Path("ml/markers/center/mask_preserving_v24/mask_preserving.py"),
    RETRY9_RESULT_PATH,
    Path("ml/markers/center/mask_preserving_v24/train_p1.py"),
    Path("ml/synthetic/dataset.py"),
    Path("ml/synthetic/templates.py"),
    Path("ml/synthetic/renderer.py"),
    Path("ml/policy/acceptance-bars.json"),
)


class _OnnxModel:
    def __init__(self, model_path: Path) -> None:
        self._session = ort.InferenceSession(
            str(model_path), providers=["CPUExecutionProvider"]
        )

    def __call__(self, patches: torch.Tensor) -> torch.Tensor:
        outputs: list[np.ndarray] = []
        for start in range(0, len(patches), 2048):
            values = patches[start : start + 2048].numpy()
            outputs.append(
                self._session.run(
                    ["candidate_predictions"], {"candidate_patches": values}
                )[0]
            )
        return torch.from_numpy(np.concatenate(outputs, axis=0))


def _tensor_set_sha256(scenes: tuple[FamilyScene, ...]) -> str:
    hashes = [
        hashlib.sha256(scene.tensor.numpy().tobytes(order="C")).hexdigest()
        for scene in scenes
    ]
    return hashlib.sha256(canonical_json_bytes(sorted(hashes))).hexdigest()


def run(model_path: Path) -> dict[str, object]:
    started = time.perf_counter()
    retry9 = json.loads((REPO_ROOT / RETRY9_RESULT_PATH).read_text(encoding="utf-8"))
    expected_model_sha256 = str(retry9["onnx_sha256"])
    actual_model_sha256 = sha256_file(model_path)
    if actual_model_sha256 != expected_model_sha256:
        raise ValueError("model is not the frozen V24 retry9 ONNX payload")
    family_audit = family_holdout_audit()
    if not family_audit["train_dev_family_disjoint"]:
        raise RuntimeError("synthetic family holdout is not disjoint")
    dev = build_family_split("dev")
    metrics = _evaluate(dev, _OnnxModel(model_path), 0.25)
    bar = _shared_marker_acceptance_bar()
    gates = {
        "precision": metrics["precision"] >= bar["precision_minimum"],
        "recall": metrics["recall"] >= bar["recall_minimum"],
        "prohibited_structure_hit_rate": metrics["prohibited_structure_hit_rate"]
        <= bar["prohibited_structure_hit_rate_maximum"],
    }
    source_hashes = {
        path.as_posix(): sha256_file(REPO_ROOT / path) for path in SOURCE_PATHS
    }
    return {
        "schema": "graphreader.marker-v24-family-dev-diagnostic.v1",
        "status": "pass" if all(gates.values()) else "failed_dev",
        "revision": "marker-center-mask-preserving-v24",
        "candidate_id": "P1-retry9-frozen",
        "failure_mode": (
            "held-out synthetic families produce reviewable false positives, misses, "
            "and prohibited-structure hits at the shipped threshold"
        ),
        "responsible_subsystem": "marker-center family coverage and training distribution",
        "model_sha256": actual_model_sha256,
        "model_source_result": RETRY9_RESULT_PATH.as_posix(),
        "acceptance_bar": bar,
        "gates": gates,
        "selected": metrics,
        "family_holdout": family_audit,
        "dev_tensor_set_sha256": _tensor_set_sha256(dev),
        "source_sha256": source_hashes,
        "source_bundle_sha256": hashlib.sha256(
            canonical_json_bytes(source_hashes)
        ).hexdigest(),
        "scope": {
            "synthetic_only": True,
            "candidate_selection": False,
            "optimizer_steps": 0,
            "private_reads": 0,
            "sealed_reads": 0,
            "case_ids_or_pixels_emitted": False,
        },
        "inference": {
            "provider": "CPUExecutionProvider",
            "scene_count": len(dev),
            "threshold": 0.25,
        },
        "elapsed_ms": round((time.perf_counter() - started) * 1000, 3),
        "production_approval": False,
        "release_eligible": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=Path, required=True)
    arguments = parser.parse_args()
    report = run(arguments.model.resolve())
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if report["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
