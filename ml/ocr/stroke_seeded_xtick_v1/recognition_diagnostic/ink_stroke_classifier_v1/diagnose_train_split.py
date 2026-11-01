# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train-only score and proposal-window perturbation for frozen P1."""

from __future__ import annotations

from collections import defaultdict
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import sys
import time
from typing import Any

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1 import train_classifier as trainer
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers

HERE = Path(__file__).resolve().parent
MANIFEST_RELATIVE = trainer.MANIFEST_RELATIVE
MANIFEST_SHA256 = trainer.MANIFEST_SHA256
CHECKPOINT_RELATIVE = trainer.OUTPUT_RELATIVE + "/checkpoint.pt"
CHECKPOINT_SHA256 = "fc78f81149d5d8a16df862795de87251658ec6e3e6d60d5404e74ea99b5973a9"
P1_CONFIG_SHA256 = "e4059c06a38e934b722c84c5d8e25a16051e2ba915d8c4910e05e029508f4465"
P1_RUNNER_SHA256 = "3e8617809584dadb9c6b35cb52b95136fe766212a2f0bb65f4863b0d17ebc594"
HELPER_SHA256 = "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5"
CURATOR_SHA256 = "7e8a32dda6b5cf74f013db0992bd66ed894301dca21cf94ea32f64f1275c32df"
TRAIN_GROUPS = frozenset({
    "stroke-seeded-xtick-v1-train-27103100", "stroke-seeded-xtick-v1-train-27103101",
    "stroke-seeded-xtick-v1-train-27103102", "stroke-seeded-xtick-v1-train-27103104",
    "stroke-seeded-xtick-v1-train-27103105", "stroke-seeded-xtick-v1-train-27103106",
    "stroke-seeded-xtick-v1-train-27103107", "stroke-seeded-xtick-v1-train-27103108",
    "stroke-seeded-xtick-v1-train-27103109", "stroke-seeded-xtick-v1-train-27103110",
    "stroke-seeded-xtick-v1-train-27103112", "stroke-seeded-xtick-v1-train-27103113",
    "stroke-seeded-xtick-v1-train-27103116", "stroke-seeded-xtick-v1-train-27103117",
    "stroke-seeded-xtick-v1-train-27103118", "stroke-seeded-xtick-v1-train-27103119",
    "stroke-seeded-xtick-v1-train-27103120", "stroke-seeded-xtick-v1-train-27103122",
    "stroke-seeded-xtick-v1-train-27103123",
})
OUTPUT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p1-train-diagnostic-rx6600xt-r4"
WINDOW_HALF_WIDTH = 5
WINDOW_LENGTH_PADDING = 3
THRESHOLD = 0.5


class DiagnosticError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def proposal_window(annotation: dict[str, Any], image_size: tuple[int, int],
                    half_width: int = WINDOW_HALF_WIDTH,
                    length_padding: int = WINDOW_LENGTH_PADDING) -> tuple[int, int, int, int]:
    """Return an inclusive-exclusive crop window using proposal geometry only."""
    if half_width < 1 or length_padding < 0:
        raise ValueError("invalid proposal window dimensions")
    try:
        line = annotation["proposal_line_original_xyxy"]
        transform = annotation["crop_transform"]
        affine = transform["original_to_crop_pixel"]
        content_x, content_y, content_w, content_h = transform["content_xywh"]
        width, height = image_size
        if len(line) != 4 or len(affine) != 6 or width <= 0 or height <= 0:
            raise ValueError
        x0, y0, x1, y1 = (float(v) for v in line)
        a, b, c, d, e, f = (float(v) for v in affine)
    except (KeyError, TypeError, ValueError) as error:
        raise ValueError("malformed proposal geometry") from error

    crop_points = [(a * x + b * y + c, d * x + e * y + f)
                   for x, y in ((x0, y0), (x1, y1))]
    cx = sum(point[0] for point in crop_points) / 2.0
    min_y = min(point[1] for point in crop_points) - length_padding
    max_y = max(point[1] for point in crop_points) + length_padding
    left = max(0, int(math.floor(cx)) - half_width, int(content_x))
    right = min(width, int(math.floor(cx)) + half_width + 1, int(content_x + content_w))
    top = max(0, int(math.floor(min_y)), int(content_y))
    bottom = min(height, int(math.ceil(max_y)) + 1, int(content_y + content_h))
    if right <= left or bottom <= top:
        raise ValueError("proposal window is fully outside crop content")
    return left, top, right, bottom


def mask_to_proposal_window(pixels: np.ndarray, annotation: dict[str, Any],
                            half_width: int = WINDOW_HALF_WIDTH,
                            length_padding: int = WINDOW_LENGTH_PADDING) -> tuple[np.ndarray, tuple[int, int, int, int]]:
    """Whiten all pixels outside a geometry-derived proposal neighborhood."""
    if pixels.ndim != 2 or pixels.shape != (96, 256):
        raise ValueError("expected the pinned 96x256 grayscale crop")
    box = proposal_window(annotation, (pixels.shape[1], pixels.shape[0]), half_width, length_padding)
    masked, _ = mask_to_proposal_windows(pixels, [box])
    return masked, box


def mask_to_proposal_windows(pixels: np.ndarray,
                             boxes: list[tuple[int, int, int, int]]) -> tuple[np.ndarray, int]:
    """Retain the union of all truth-blind proposals attached to identical crop bytes."""
    if pixels.ndim != 2 or pixels.shape != (96, 256):
        raise ValueError("expected the pinned 96x256 grayscale crop")
    if not boxes:
        raise ValueError("at least one proposal window is required")
    masked = np.ones_like(pixels, dtype=np.float32)
    retained = np.zeros(pixels.shape, dtype=bool)
    for left, top, right, bottom in boxes:
        if not (0 <= left < right <= pixels.shape[1] and 0 <= top < bottom <= pixels.shape[0]):
            raise ValueError("proposal window outside crop bounds")
        retained[top:bottom, left:right] = True
    masked[retained] = pixels[retained]
    return masked, int(np.count_nonzero(retained))


def binary_metrics(labels: list[int], scores: list[float], threshold: float = THRESHOLD) -> dict[str, Any]:
    if not labels or len(labels) != len(scores) or any(not math.isfinite(v) for v in scores):
        raise ValueError("nonempty aligned finite scores and labels required")
    tp = sum(y == 1 and s >= threshold for y, s in zip(labels, scores))
    fp = sum(y == 0 and s >= threshold for y, s in zip(labels, scores))
    fn = sum(y == 1 and s < threshold for y, s in zip(labels, scores))
    tn = sum(y == 0 and s < threshold for y, s in zip(labels, scores))
    return {"count": len(labels), "positive_count": sum(labels), "negative_count": len(labels) - sum(labels),
        "threshold": threshold, "true_positive": tp, "false_positive": fp, "false_negative": fn,
        "true_negative": tn, "precision": tp / (tp + fp) if tp + fp else 0.0,
        "recall": tp / (tp + fn) if tp + fn else 0.0,
        "accuracy": (tp + tn) / len(labels)}


def _read_json(root: Path, relative: str, label: str, limit: int) -> tuple[dict[str, Any], str]:
    bound = helpers._bound_read(root, relative, label, limit)
    return helpers._parse_json(bound, label), bound.sha256


def admit_train_images() -> dict[str, Any]:
    """Read metadata for split binding; load annotation/image bytes only for train groups."""
    manifest_bound = helpers._bound_read(ROOT, MANIFEST_RELATIVE, "pinned train curation manifest", 1_000_000)
    if manifest_bound.sha256 != MANIFEST_SHA256:
        raise DiagnosticError("curation manifest SHA changed")
    manifest = helpers._parse_json(manifest_bound, "pinned train curation manifest")
    if manifest.get("split") != "train" or manifest.get("schema") != "graphreader.stroke-seeded-xtick-v1-inkscan-curated-train.v1":
        raise DiagnosticError("expected the pinned train-only manifest")
    records = manifest.get("records")
    if not isinstance(records, list) or len(records) != 896:
        raise DiagnosticError("manifest record inventory changed")

    # Manifest identifiers and hashes are used only to bind the frozen split and
    # prove no exact image hash crosses the split. No holdout annotation/image is read.
    group_ids = {row.get("source_group_id") for row in records}
    if not TRAIN_GROUPS <= group_ids:
        raise DiagnosticError("frozen training source group missing from manifest")
    train_records = [row for row in records if row.get("source_group_id") in TRAIN_GROUPS]
    nontrain_hashes = {row.get("image_sha256") for row in records if row.get("source_group_id") not in TRAIN_GROUPS}
    train_manifest_hashes = {row.get("image_sha256") for row in train_records}
    if train_manifest_hashes & nontrain_hashes:
        raise DiagnosticError("frozen training image hash overlaps non-training source rows")

    metadata: dict[str, dict[str, Any]] = {}
    states_by_hash: dict[str, set[int]] = defaultdict(set)
    groups_by_hash: dict[str, set[str]] = defaultdict(set)
    geometry_by_hash: dict[str, set[tuple[int, int, int, int]]] = defaultdict(set)
    for row in train_records:
        group = row.get("source_group_id")
        if row.get("split") != "train" or row.get("source_id") != group or group not in TRAIN_GROUPS:
            raise DiagnosticError("train row identity mismatch")
        annotation, ann_sha = _read_json(ROOT / trainer.CURATION_RELATIVE,
            row["annotation_path"], f"train annotation {row.get('case_id')}", 65_536)
        if ann_sha != row.get("annotation_sha256"):
            raise DiagnosticError(f"train annotation hash mismatch: {row.get('case_id')}")
        for key in ("case_id", "source_id", "source_group_id", "source_identity_sha256", "split", "image_sha256"):
            if annotation.get(key) != row.get(key):
                raise DiagnosticError(f"train annotation/manifest mismatch: {row.get('case_id')}:{key}")
        state = annotation.get("matching_state")
        if state not in ("unique_visible_tick", "no_ground_truth_tick_nearby", "ambiguous"):
            raise DiagnosticError(f"unknown training label state: {row.get('case_id')}")
        image_hash = row["image_sha256"]
        groups_by_hash[image_hash].add(group)
        if state != "ambiguous":
            states_by_hash[image_hash].add(int(state == "unique_visible_tick"))
            try:
                geo = proposal_window(annotation, (256, 96))
            except (KeyError, TypeError, ValueError) as error:
                raise DiagnosticError(f"proposal geometry absent or invalid: {row.get('case_id')}") from error
            geometry_by_hash[image_hash].add(geo)
            metadata.setdefault(image_hash, {"image_path": row["image_path"], "image_sha256": image_hash})

    labels = trainer._deduplicate_label_map(states_by_hash)
    if len(labels) != 446 or sum(labels.values()) != 81:
        raise DiagnosticError("training image/class counts differ from the frozen P1 split")
    images = []
    loaded_hashes: set[str] = set()
    for image_hash in sorted(labels):
        row = metadata.get(image_hash)
        if row is None:
            raise DiagnosticError("train label has no train annotation binding")
        if image_hash not in loaded_hashes:
            bound = helpers._bound_read(ROOT / trainer.CURATION_RELATIVE, row["image_path"],
                f"train crop {image_hash}", 1_000_000)
            if bound.sha256 != image_hash:
                raise DiagnosticError("train image bytes do not match manifest hash")
            try:
                with Image.open(BytesIO(bound.value)) as image:
                    image.load()
                    if image.mode != "L" or image.size != (256, 96):
                        raise DiagnosticError("train crop dimensions/mode changed")
                    pixels = np.asarray(image, dtype=np.float32) / 255.0
            except OSError as error:
                raise DiagnosticError("train crop decode failed") from error
            loaded_hashes.add(image_hash)
            images.append({"image_sha256": image_hash, "label": labels[image_hash],
                "source_group_ids": sorted(groups_by_hash[image_hash]), "pixels": pixels,
                "proposal_windows": sorted(geometry_by_hash[image_hash])})
    if len(loaded_hashes) != 446:
        raise DiagnosticError("did not load exactly the frozen train split unique images")
    return {"manifest_sha256": manifest_bound.sha256, "images": images,
        "row_count": len(train_records), "unique_count": len(images),
        "class_counts": {"positive": sum(labels.values()), "negative": len(labels) - sum(labels.values())},
        "source_groups": sorted(TRAIN_GROUPS), "nontrain_image_hash_overlap": 0}


def _score_model(torch: Any, model: Any, device: Any, arrays: np.ndarray, batch_size: int = 32) -> list[float]:
    tensor = torch.from_numpy(arrays.astype(np.float32, copy=False)).unsqueeze(1)
    if tensor.device.type != "cpu":
        raise DiagnosticError("unexpected host tensor placement before explicit GPU transfer")
    scores: list[float] = []
    with torch.inference_mode():
        for start in range(0, len(tensor), batch_size):
            batch = tensor[start:start + batch_size].to(device)
            if batch.device != device:
                raise DiagnosticError("inference batch left RX 6600 XT")
            scores.extend(torch.sigmoid(model(batch)).detach().cpu().tolist())
    return [float(score) for score in scores]


def assert_gpu_inference_binding(runtime: dict[str, Any], model: Any, device: Any) -> None:
    if (runtime.get("device") != "cuda:0" or runtime.get("device_name") != "AMD Radeon RX 6600 XT"
            or runtime.get("cpu_fallback") is not False or str(device) != "cuda:0"
            or any(parameter.device != device for parameter in model.parameters())):
        raise DiagnosticError("frozen-model inference is not bound to RX 6600 XT cuda:0")


def _distribution(scores: list[float]) -> dict[str, float]:
    values = np.asarray(scores, dtype=np.float64)
    return {"count": int(values.size), "mean": float(values.mean()), "median": float(np.median(values)),
        "min": float(values.min()), "max": float(values.max()),
        "fraction_at_or_above_0_5": float(np.mean(values >= THRESHOLD))}


def run() -> dict[str, Any]:
    if digest((ROOT / HERE.relative_to(ROOT) / "p1.json").read_bytes()) != P1_CONFIG_SHA256:
        raise DiagnosticError("P1 config SHA changed")
    if digest((ROOT / HERE.relative_to(ROOT) / "train_classifier.py").read_bytes()) != P1_RUNNER_SHA256:
        raise DiagnosticError("P1 trainer SHA changed")
    if digest((ROOT / "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py").read_bytes()) != HELPER_SHA256:
        raise DiagnosticError("pinned feature helper SHA changed")
    if digest((ROOT / trainer.CURATOR_RELATIVE).read_bytes()) != CURATOR_SHA256:
        raise DiagnosticError("pinned curation source SHA changed")
    checkpoint_bound = helpers._bound_read(ROOT, CHECKPOINT_RELATIVE, "frozen P1 checkpoint", 20_000_000)
    if checkpoint_bound.sha256 != CHECKPOINT_SHA256:
        raise DiagnosticError("frozen P1 checkpoint SHA changed")

    dataset = admit_train_images()
    import torch
    runtime = helpers.require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    model = trainer._build_classifier(torch).to(device)
    checkpoint = torch.load(BytesIO(checkpoint_bound.value), map_location=device, weights_only=True)
    if checkpoint.get("model_identity") != "stroke-seeded-ink-validity-rf31-p1":
        raise DiagnosticError("checkpoint model identity mismatch")
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    assert_gpu_inference_binding(runtime, model, device)
    model.eval()

    rows = dataset["images"]
    labels = [row["label"] for row in rows]
    source_scores: dict[str, dict[str, dict[str, list[float]]]] = defaultdict(
        lambda: {"full_crop": {"positive": [], "negative": []},
                 "proposal_window": {"positive": [], "negative": []}})
    arrays = np.stack([row["pixels"] for row in rows])
    masked_arrays = np.empty_like(arrays)
    windows = []
    retained_areas = []
    retained_ink: list[float] = []
    for index, row in enumerate(rows):
        masked, retained_area = mask_to_proposal_windows(row["pixels"], row["proposal_windows"])
        masked_arrays[index] = masked
        windows.extend(row["proposal_windows"])
        retained_areas.append(retained_area)
        retained = masked < 0.98
        retained_ink.append(float(np.count_nonzero(retained)))

    original_scores = _score_model(torch, model, device, arrays)
    masked_scores = _score_model(torch, model, device, masked_arrays)
    torch.cuda.synchronize(device)
    for row, original, masked in zip(rows, original_scores, masked_scores):
        bucket = "positive" if row["label"] else "negative"
        for group in row["source_group_ids"]:
            source_scores[group]["full_crop"][bucket].append(original)
            source_scores[group]["proposal_window"][bucket].append(masked)

    by_group = {}
    for group in sorted(source_scores):
        data = source_scores[group]
        by_group[group] = {view: {label: _distribution(scores) for label, scores in classes.items() if scores}
            for view, classes in data.items()}
    return {
        "schema": "graphreader.stroke-seeded-ink-stroke-classifier-train-diagnostic.v1",
        "status": "completed-train-only-frozen-checkpoint-diagnostic",
        "diagnostic_script_sha256": digest(Path(__file__).read_bytes()),
        "inputs": {"manifest_sha256": dataset["manifest_sha256"], "checkpoint_sha256": checkpoint_bound.sha256,
            "p1_config_sha256": P1_CONFIG_SHA256, "p1_runner_sha256": P1_RUNNER_SHA256,
            "feature_helper_sha256": HELPER_SHA256, "curator_sha256": CURATOR_SHA256,
            "train_source_groups": dataset["source_groups"], "train_row_occurrences": dataset["row_count"],
            "unique_images": dataset["unique_count"], "class_counts": dataset["class_counts"],
            "nontrain_image_hash_overlap": dataset["nontrain_image_hash_overlap"],
            "nontrain_annotation_or_image_bytes_read": False},
        "runtime": {"device": runtime["device"], "device_name": runtime["device_name"],
            "hip": runtime.get("hip"), "cpu_fallback": runtime.get("cpu_fallback"),
            "training_performed": False},
        "full_crop_train_metrics": binary_metrics(labels, original_scores),
        "proposal_window_train_metrics": binary_metrics(labels, masked_scores),
        "score_summary": {
            "full_crop_positive": _distribution([s for y, s in zip(labels, original_scores) if y == 1]),
            "full_crop_negative": _distribution([s for y, s in zip(labels, original_scores) if y == 0]),
            "proposal_window_positive": _distribution([s for y, s in zip(labels, masked_scores) if y == 1]),
            "proposal_window_negative": _distribution([s for y, s in zip(labels, masked_scores) if y == 0]),
            "source_groups": by_group,
        },
        "perturbation": {"rule": "keep only proposal-line crop-x neighborhood and line extent plus fixed padding; whiten all outside pixels; no target or glyph truth used",
            "half_width_pixels": WINDOW_HALF_WIDTH, "line_length_padding_pixels": WINDOW_LENGTH_PADDING,
            "duplicate_hash_policy": "retain the union of all distinct proposal windows associated with the same exact crop bytes",
            "window_box_xyxy_exclusive": {"min": [min(w[0] for w in windows), min(w[1] for w in windows)],
                "max": [max(w[2] for w in windows), max(w[3] for w in windows)]},
            "mean_retained_area_pixels": float(np.mean(retained_areas)),
            "mean_retained_area_fraction": float(np.mean(retained_areas) / (96 * 256)),
            "mean_retained_nonwhite_pixels": float(np.mean(retained_ink)),
            "mean_window_area_pixels": float(np.mean([(w[2] - w[0]) * (w[3] - w[1]) for w in windows])),
            "note": "This is a context-suppression sensitivity test, not a causal proof: the window can also remove target glyph pixels."},
        "limitations": ["Train-fit scores are not generalization evidence.",
            "Exact duplicate images linked across training source groups are counted once for overall metrics and appear in each group membership summary.",
            "The proposal-centered window is derived only from scan proposal geometry, but may remove text or other local evidence; score changes cannot isolate one visual cue.",
            "No internal holdout, probe, dev, sealed, private, or production data was read by this diagnostic."],
        "production_approval": False, "release_eligible": False,
    }


def main() -> None:
    output = ROOT / OUTPUT_RELATIVE
    if output.exists() or output.is_symlink() or output.parent != ROOT / "artifacts":
        raise DiagnosticError("diagnostic output must be a fresh project artifacts child")
    helpers._checked_chain(ROOT, output.parent, "diagnostic output parent")
    output.mkdir(parents=False, exist_ok=False)
    started = {"status": "started", "checkpoint_sha256": CHECKPOINT_SHA256,
        "manifest_sha256": MANIFEST_SHA256, "started_utc": datetime.now(timezone.utc).isoformat()}
    (output / "started.json").write_text(json.dumps(started, sort_keys=True) + "\n", encoding="utf-8")
    try:
        report = run()
        (output / "report.json").write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    except BaseException as error:
        failure = {"status": "failed", "error_type": type(error).__name__, "error": str(error),
            "failed_utc": datetime.now(timezone.utc).isoformat()}
        (output / "failure.json").write_text(json.dumps(failure, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
