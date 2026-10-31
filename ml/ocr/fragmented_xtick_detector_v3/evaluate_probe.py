# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Run image-level metrics on a fresh procedural probe with the frozen P4 checkpoint."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from statistics import median
import sys
from typing import Any

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))
from ml.ocr.fragmented_xtick_detector_v3.evaluate_p2 import decode_prediction_box, summarize_image_metrics
from ml.ocr.fragmented_xtick_detector_v3.gpu_train import (
    GPU_PYTHON,
    REPO_ROOT,
    _annotation_target,
    build_spatial_locator,
    require_rx_6600_xt,
    target_assignments,
)
from ml.ocr.fragmented_xtick_detector_v3.probe_materialize import (
    OUTPUT_RELATIVE,
    _png_bytes,
    _sha256_bytes,
    cases,
    generator_sha256,
    LABELS,
    TRAIN_LABELS,
    render_case,
)


P4_ROOT = REPO_ROOT / "artifacts" / "fragmented-xtick-detector-v3-p4-rf31-logwh-stratified-30epoch-v1"
P4_MODEL_ID = "fragmented-xtick-spatial-locator-v3-cell-relative-rf31-logwh"
P4_ARCHITECTURE = "three 3x3 stride-2 convs plus one 3x3 stride-1 context conv; 31px receptive field; no global pooling"
P4_BOX_PARAMETERIZATION = "sigmoid cell-relative center offsets; log normalized width-height regression; exponentiated decode with one-pixel minimum"
PROBE_ROOT = REPO_ROOT / OUTPUT_RELATIVE


def _read_probe(torch: Any) -> tuple[dict[str, Any], list[Any], list[tuple[float, float, float, float] | None]]:
    manifest_path = PROBE_ROOT / "manifest.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes.decode("utf-8"))
    if (manifest.get("schema") != "graphreader.ocr-fragmented-xtick-v3-fresh-probe.v1"
            or manifest.get("generator_sha256") != generator_sha256()
            or manifest.get("labels_disjoint_from_train") is not True
            or not set(LABELS).isdisjoint(TRAIN_LABELS)):
        raise RuntimeError("fresh probe manifest is not bound to this generator")
    if (manifest.get("case_count"), manifest.get("positive_count"), manifest.get("background_only_count")) != (96, 64, 32):
        raise RuntimeError("fresh probe counts differ from the checked-in generation recipe")
    expected_cases = {case["case_id"]: case for case in cases()}
    values: list[Any] = []
    targets: list[tuple[float, float, float, float] | None] = []
    for record in manifest["records"]:
        spec = expected_cases.get(record.get("case_id"))
        if spec is None or spec["kind"] != record.get("kind"):
            raise RuntimeError("probe case identity differs from the generated case catalog")
        image_relative, annotation_relative = Path(record["image_path"]), Path(record["annotation_path"])
        if image_relative.is_absolute() or annotation_relative.is_absolute() or ".." in image_relative.parts or ".." in annotation_relative.parts:
            raise RuntimeError("probe manifest contains an unsafe relative path")
        image_path = PROBE_ROOT / image_relative
        annotation_path = PROBE_ROOT / annotation_relative
        image_bytes = image_path.read_bytes()
        annotation_bytes = annotation_path.read_bytes()
        image_sha = _sha256_bytes(image_bytes)
        if image_sha != record["image_sha256"] or _sha256_bytes(annotation_bytes) != record["annotation_sha256"]:
            raise RuntimeError("probe image or annotation hash differs from its manifest")
        annotation = json.loads(annotation_bytes.decode("utf-8"))
        if annotation.get("image_sha256") != image_sha or annotation.get("case_id") != record["case_id"]:
            raise RuntimeError("probe annotation does not bind to its image and case")
        rendered = render_case(spec)
        if _png_bytes(rendered.image) != image_bytes:
            raise RuntimeError("probe image does not reproduce from its procedural case")
        for key, expected in rendered.annotation.items():
            if annotation.get(key) != expected:
                raise RuntimeError("probe target annotation differs from rendered glyph bounds")
        target = _annotation_target(annotation, positive=record["kind"] == "positive")
        pixels = np.asarray(Image.open(BytesIO(image_bytes)).convert("L"), dtype=np.float32) / 255.0
        if pixels.shape != (96, 256):
            raise RuntimeError("probe image has unexpected dimensions")
        values.append(torch.from_numpy(pixels))
        targets.append(target)
    return manifest, values, targets


def evaluate() -> dict[str, Any]:
    if Path(sys.executable).resolve() != GPU_PYTHON.resolve():
        raise RuntimeError("P4 probe inference must use the project RX 6600 XT launcher")
    import torch

    p4_report = json.loads((P4_ROOT / "report.json").read_text(encoding="utf-8"))
    checkpoint_bytes = (P4_ROOT / "checkpoint.pt").read_bytes()
    checkpoint_sha = sha256(checkpoint_bytes).hexdigest()
    if (checkpoint_sha != p4_report.get("checkpoint_sha256") or p4_report.get("model") != P4_MODEL_ID
            or p4_report.get("box_parameterization") != P4_BOX_PARAMETERIZATION):
        raise RuntimeError("frozen P4 checkpoint hash differs from its training report")
    runtime = require_rx_6600_xt(torch)
    device = torch.device(runtime["device"])
    model = build_spatial_locator(torch).to(device)
    payload = torch.load(BytesIO(checkpoint_bytes), map_location=device, weights_only=True)
    if (payload.get("model_id"), payload.get("architecture"), payload.get("box_parameterization")) != (
        P4_MODEL_ID,
        P4_ARCHITECTURE,
        P4_BOX_PARAMETERIZATION,
    ):
        raise RuntimeError("checkpoint metadata does not identify the frozen P4 model")
    if payload.get("input_manifest_sha256") != p4_report["input"]["manifest_sha256"]:
        raise RuntimeError("frozen P4 checkpoint does not bind to its training report")
    model.load_state_dict(payload["state_dict"])
    model.eval()
    if any(parameter.device != device for parameter in model.parameters()):
        raise RuntimeError("P4 model is not resident on the RX 6600 XT")
    manifest, images, targets = _read_probe(torch)
    features = torch.stack(images).unsqueeze(1).to(device)
    if features.device != device:
        raise RuntimeError("procedural probe tensors are not resident on the RX 6600 XT")
    with torch.inference_mode():
        logits, box_values = model(features)
        scores = logits.sigmoid().squeeze(1).reshape(len(images), -1).detach().cpu().tolist()
        boxes = box_values.detach().cpu().permute(0, 2, 3, 1).tolist()
    assignments = target_assignments(tuple(targets))
    rows: list[dict[str, Any]] = []
    widths = []
    heights = []
    for index, target in enumerate(targets):
        candidates = []
        for cell, score in enumerate(scores[index]):
            row, column = divmod(cell, 32)
            candidates.append((float(score), decode_prediction_box(boxes[index][row][column], row, column)))
        target_cell = None
        if assignments[index] is not None:
            row, column, _ = assignments[index]
            values = tuple(float(value) for value in boxes[index][row][column])
            target_cell = {"score": float(scores[index][row * 32 + column]), "box": decode_prediction_box(values, row, column), "values": values}
            widths.append((target[2] - target[0]) * 256.0)
            heights.append((target[3] - target[1]) * 96.0)
        rows.append({"target": target, "target_cell": target_cell, "candidates": candidates})
    metrics = summarize_image_metrics(rows)
    return {
        "schema": "graphreader.ocr-fragmented-xtick-v3-frozen-p4-probe-evaluation.v1",
        "status": "fresh-procedural-probe-not-formal-acceptance",
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "checkpoint_sha256": checkpoint_sha,
        "checkpoint_model_id": P4_MODEL_ID,
        "probe_id": manifest["probe_id"],
        "probe_manifest_sha256": _sha256_bytes((PROBE_ROOT / "manifest.json").read_bytes()),
        "probe_generator_sha256": manifest["generator_sha256"],
        "runtime": runtime,
        "cpu_fallback": False,
        "input_scope": {"fresh_procedural_probe": True, "train": False, "dev": False, "sealed": False, "private": False},
        "probe_target_box_pixels": {
            "width_min": min(widths),
            "width_median": median(widths),
            "width_max": max(widths),
            "height_min": min(heights),
            "height_median": median(heights),
            "height_max": max(heights),
        },
        "distribution_overlap_and_limits": manifest["distribution_overlap_and_limits"],
        "formal_acceptance_set": False,
        "metrics": metrics,
        "geometry_convention": "one-pixel minimum decode clamp prevents zero-area boxes; clamp fractions are reported and are not localization credit",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=PROBE_ROOT / "p4-evaluation-v2.json")
    args = parser.parse_args()
    result = evaluate()
    output = args.output if args.output.is_absolute() else REPO_ROOT / args.output
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=True, sort_keys=True, separators=(",", ":"))
    print(json.dumps({"output": output.relative_to(REPO_ROOT).as_posix(), "result_sha256": sha256(output.read_bytes()).hexdigest(), "metrics": result["metrics"], "runtime": result["runtime"]}, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
