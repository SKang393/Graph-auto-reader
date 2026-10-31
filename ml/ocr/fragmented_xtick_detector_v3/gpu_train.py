# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unregistered RX 6600 XT-only stratified-batch fragmented X-tick experiment.

This checked-in configuration authorizes one unregistered exploratory, train-only
GPU recipe. It is spatial: it predicts objectness per feature-map cell and regresses
an XYXY box only at a positive cell. It never pools the full image before
localization and cannot use dev, sealed, private, or V1 inputs.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
import math
import random
from pathlib import Path
import stat
import sys
from typing import Any, Mapping

from PIL import Image
sys.path.insert(0, str(Path(__file__).resolve().parents[3]))
from ml.ocr.fragmented_xtick_detector_v2.materialize import _png_bytes, cases, render_case, source_bundle_sha256


ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parents[2]
CONFIG_PATH = ROOT / "training" / "p4.json"
GPU_PYTHON = REPO_ROOT / "tools" / "gpu-training" / "venv" / "Scripts" / "python.exe"
MAX_MANIFEST_BYTES = 1_000_000
MAX_ANNOTATION_BYTES = 65_536
MAX_IMAGE_BYTES = 1_000_000


class TrainingAdmissionError(RuntimeError):
    """An unregistered V3 experiment failed before an optimizer can run."""


@dataclass(frozen=True)
class BoundBytes:
    path: Path
    value: bytes
    sha256: str


def _canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _sha256_bytes(value: bytes) -> str:
    return sha256(value).hexdigest()


def runner_sha256() -> str:
    return _sha256_bytes(Path(__file__).read_bytes())


def _is_reparse(path: Path) -> bool:
    try:
        attributes = getattr(path.lstat(), "st_file_attributes", 0)
    except OSError:
        return path.is_symlink()
    return path.is_symlink() or bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def _path_fingerprint(path: Path) -> tuple[int, int, int, int, int]:
    data = path.lstat()
    return (
        int(getattr(data, "st_dev", 0)), int(getattr(data, "st_ino", 0)),
        int(data.st_size), int(data.st_mtime_ns),
        int(getattr(data, "st_file_attributes", 0)),
    )


def _checked_chain(root: Path, path: Path, label: str) -> tuple[tuple[Path, tuple[int, int, int, int, int]], ...]:
    try:
        relative = path.relative_to(root)
    except ValueError as error:
        raise TrainingAdmissionError(f"{label} escapes its bound root") from error
    cursor = root
    chain: list[tuple[Path, tuple[int, int, int, int, int]]] = []
    for part in (".", *relative.parts):
        if part != ".":
            cursor = cursor / part
        if not cursor.exists() or _is_reparse(cursor):
            raise TrainingAdmissionError(f"{label} is missing or traverses a reparse point")
        chain.append((cursor, _path_fingerprint(cursor)))
    return tuple(chain)


def _bound_read(root: Path, relative: str, label: str, maximum_bytes: int = MAX_MANIFEST_BYTES) -> BoundBytes:
    path = Path(relative)
    if path.is_absolute() or ".." in path.parts:
        raise TrainingAdmissionError(f"{label} path is unsafe")
    lexical = root / path
    before = _checked_chain(root, lexical, label)
    if not lexical.is_file() or lexical.stat().st_size > maximum_bytes:
        raise TrainingAdmissionError(f"{label} is not a bounded regular file")
    try:
        with lexical.open("rb") as stream:
            value = stream.read(maximum_bytes + 1)
    except OSError as error:
        raise TrainingAdmissionError(f"{label} cannot be read") from error
    after = _checked_chain(root, lexical, label)
    if before != after or len(value) > maximum_bytes:
        raise TrainingAdmissionError(f"{label} changed, was replaced, or exceeded its bound while being read")
    return BoundBytes(path=lexical, value=value, sha256=_sha256_bytes(value))


def _parse_json(bound: BoundBytes, label: str) -> dict[str, Any]:
    try:
        value = json.loads(bound.value.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise TrainingAdmissionError(f"{label} is not valid UTF-8 JSON") from error
    if not isinstance(value, dict):
        raise TrainingAdmissionError(f"{label} must be an object")
    return value


def _config() -> dict[str, Any]:
    bound = _bound_read(ROOT, "training/p4.json", "V3 log-box regression GPU config")
    config = _parse_json(bound, "V3 log-box regression GPU config")
    expected = {
        "schema": "graphreader.ocr-fragmented-xtick-v3-gpu-exploratory.v1",
        "revision": "graph-text-fragmented-xtick-detector-v3-stratified-30-epochs-rf31-logwh",
        "candidate_id": "P4-stratified-30-epochs-rf31-logwh-unregistered",
        "state": "prepared-gpu-authorized-unregistered",
        "task": "ocr-detection",
        "scope": "train-only synthetic spatial localization, log width-height regression diagnostic",
        "positive_count": 64,
        "background_negative_count": 32,
        "model": "fragmented-xtick-spatial-locator-v3-cell-relative-rf31-logwh",
        "architecture": "three 3x3 stride-2 convs plus one 3x3 stride-1 context conv; 31px receptive field; no global pooling",
        "expected_receptive_field_pixels": 31,
        "box_parameterization": "sigmoid cell-relative center offsets; log normalized width-height regression; exponentiated decode with one-pixel minimum",
        "input": "single-channel 256x96 train-only synthetic raster",
        "target": "12x32 objectness map plus sigmoid cell-relative center offsets and log normalized width-height",
        "loss": "balanced_positive_negative_bce_plus_positive_cell_smooth_l1_center_offset_log_width_height",
        "epochs": 30,
        "batch_size": 8,
        "learning_rate": 0.001,
        "expected_optimizer_steps": 360,
        "seed": 24014201,
        "shuffle_seed": 24014202,
        "required_launcher": "tools/gpu-training/Run-GpuPython.ps1",
        "required_device_name_substring": "RX 6600 XT",
        "required_rocm": True,
        "cpu_fallback_allowed": False,
        "dev_input_allowed": False,
        "sealed_input_allowed": False,
        "private_input_allowed": False,
        "optimizer_execution_authorized": True,
        "batching": "deterministic stratified full-epoch batches, every batch has positives and negatives",
        "training_duration": "30 epochs, 12 optimizer steps per epoch",
        "planned_output_root": "artifacts/fragmented-xtick-detector-v3-p4-rf31-logwh-stratified-30epoch-v1",
        "production_approval": False,
        "release_eligible": False,
    }
    for key, value in expected.items():
        if config.get(key) != value:
            raise TrainingAdmissionError(f"V3 log-box GPU config mismatch: {key}")
    if config.get("expected_runner_sha256") != runner_sha256():
        raise TrainingAdmissionError("V3 P4 runner source hash changed")
    return config


def _annotation_target(annotation: Mapping[str, Any], *, positive: bool) -> tuple[float, float, float, float] | None:
    target = annotation.get("target")
    if not positive:
        if target is not None:
            raise TrainingAdmissionError("Background-negative annotation has a target")
        return None
    if not isinstance(target, dict) or target.get("role") != "XTick" or not isinstance(target.get("text"), str):
        raise TrainingAdmissionError("Positive annotation target is malformed")
    bounds = target.get("bounding_box_xyxy")
    if not isinstance(bounds, list) or len(bounds) != 4 or any(type(value) is not int for value in bounds):
        raise TrainingAdmissionError("Positive target bounds are invalid")
    left, top, right, bottom = bounds
    if not 0 <= left < right <= 256 or not 0 <= top < bottom <= 96:
        raise TrainingAdmissionError("Positive target bounds are out of range")
    return left / 256.0, top / 96.0, right / 256.0, bottom / 96.0


def validate_train_input() -> dict[str, object]:
    """Read each bound V2 byte stream once, with no model/GPU/optimizer work."""

    config = _config()
    manifest_relative = config.get("train_manifest_path")
    if not isinstance(manifest_relative, str):
        raise TrainingAdmissionError("V2 train manifest path is invalid")
    manifest_bound = _bound_read(REPO_ROOT, manifest_relative, "V2 train manifest")
    if manifest_bound.sha256 != config.get("train_manifest_sha256"):
        raise TrainingAdmissionError("V2 train manifest hash changed")
    manifest = _parse_json(manifest_bound, "V2 train manifest")
    required = {
        "schema": "graphreader.ocr-fragmented-xtick-v2-train-materialization.v1",
        "revision": "graph-text-fragmented-xtick-detector-v2", "split": "train", "case_count": 96,
        "positive_count": config["positive_count"], "background_negative_count": config["background_negative_count"],
        "synthetic_only": True, "dev_or_sealed_cases_rendered": False,
        "weights_loaded": False, "optimizer_steps": 0, "inference_executed": False,
        "production_approval": False, "release_eligible": False,
    }
    for key, expected in required.items():
        if manifest.get(key) != expected:
            raise TrainingAdmissionError(f"V2 manifest mismatch: {key}")
    if manifest.get("source_bundle_sha256") != config.get("train_materializer_source_bundle_sha256"):
        raise TrainingAdmissionError("V2 materializer source binding changed")
    if manifest.get("source_bundle_sha256") != source_bundle_sha256():
        raise TrainingAdmissionError("V2 materializer bytes no longer match the captured source bundle")
    records = manifest.get("records")
    if not isinstance(records, list) or len(records) != 96:
        raise TrainingAdmissionError("V2 record count changed")
    root = manifest_bound.path.parent
    identities: set[str] = set()
    x_bins: set[int] = set()
    y_bins: set[int] = set()
    positive = negative = 0
    registered = {str(case["case_id"]): case for case in cases()}
    for record in records:
        if not isinstance(record, dict):
            raise TrainingAdmissionError("V2 record is malformed")
        kind = record.get("kind")
        identity = record.get("case_identity_sha256")
        case_id = record.get("case_id")
        if kind not in {"positive", "background_negative"} or not isinstance(identity, str) or identity in identities or not isinstance(case_id, str) or not case_id.startswith("fresh-fragmented-numeric-xtick-v2-train-"):
            raise TrainingAdmissionError("V2 identity or split contract changed")
        expected_case = registered.get(case_id)
        if expected_case is None or expected_case.get("case_identity_sha256") != identity or expected_case.get("kind") != kind:
            raise TrainingAdmissionError("V2 record is not a registered fresh train case")
        identities.add(identity)
        annotation_path = record.get("annotation_path")
        image_path = record.get("image_path")
        if not isinstance(annotation_path, str) or not isinstance(image_path, str):
            raise TrainingAdmissionError("V2 record paths are invalid")
        annotation_bound = _bound_read(root, annotation_path, "V2 annotation", MAX_ANNOTATION_BYTES)
        image_bound = _bound_read(root, image_path, "V2 image", MAX_IMAGE_BYTES)
        if annotation_bound.sha256 != record.get("annotation_sha256") or image_bound.sha256 != record.get("image_sha256"):
            raise TrainingAdmissionError("V2 bound input hash changed")
        annotation = _parse_json(annotation_bound, "V2 annotation")
        if annotation.get("case_id") != case_id or annotation.get("case_identity_sha256") != identity or annotation.get("split") != "train" or annotation.get("kind") != kind or annotation.get("image_sha256") != image_bound.sha256:
            raise TrainingAdmissionError("V2 annotation identity changed")
        expected_render = render_case(expected_case)
        if _png_bytes(expected_render.image) != image_bound.value or expected_render.annotation.get("target") != annotation.get("target"):
            raise TrainingAdmissionError("V2 complete target box or rendered bytes differ from the captured materializer")
        target = _annotation_target(annotation, positive=kind == "positive")
        try:
            image = Image.open(BytesIO(image_bound.value)).convert("L")
        except OSError as error:
            raise TrainingAdmissionError("V2 image bytes are unreadable") from error
        if image.size != (256, 96):
            raise TrainingAdmissionError("V2 image dimensions changed")
        if target is None:
            negative += 1
        else:
            positive += 1
            left, top, right, bottom = target
            pixel_bounds = (round(left * 256), round(top * 96), round(right * 256), round(bottom * 96))
            if not any(value < 245 for value in image.crop(pixel_bounds).get_flattened_data()):
                raise TrainingAdmissionError("V2 positive box lacks visible glyph pixels")
            x_bins.add(int(((left + right) * 128) // 24))
            y_bins.add(int(((top + bottom) * 48) // 12))
    if positive != 64 or negative != 32 or len(x_bins) < 8 or len(y_bins) < 4:
        raise TrainingAdmissionError("V2 spatial/negative balance contract changed")
    return {
        "manifest_sha256": manifest_bound.sha256, "example_count": 96,
        "positive_count": positive, "background_negative_count": negative,
        "target_center_x_bin_count": len(x_bins), "target_center_y_bin_count": len(y_bins),
        "dev_input_read": False, "sealed_input_read": False, "private_input_read": False,
        "optimizer_steps": 0,
    }


def require_rx_6600_xt(torch: Any) -> dict[str, object]:
    if not getattr(torch.version, "hip", None) or not torch.cuda.is_available():
        raise TrainingAdmissionError("ROCm GPU runtime is unavailable; CPU fallback is forbidden")
    matches = [(index, str(torch.cuda.get_device_properties(index).name)) for index in range(torch.cuda.device_count()) if "RX 6600 XT" in str(torch.cuda.get_device_properties(index).name)]
    if len(matches) != 1:
        raise TrainingAdmissionError("Expected exactly one RX 6600 XT; CPU or wrong-GPU fallback is forbidden")
    index, name = matches[0]
    device = torch.device(f"cuda:{index}")
    torch.cuda.set_device(device)
    probe = torch.zeros((1,), device=device)
    if probe.device.type != "cuda" or probe.device.index != index:
        raise TrainingAdmissionError("RX 6600 XT tensor residency check failed")
    torch.cuda.synchronize(device)
    return {"device": str(device), "device_name": name, "hip": str(torch.version.hip), "cpu_fallback": False}


def _require_authorized_gpu_launcher(config: Mapping[str, Any]) -> None:
    if config.get("optimizer_execution_authorized") is not True:
        raise TrainingAdmissionError("Optimizer execution is not authorized by this checked-in V2 config")
    if not GPU_PYTHON.is_file() or Path(sys.executable).resolve() != GPU_PYTHON.resolve():
        raise TrainingAdmissionError("Optimizer must use tools/gpu-training/Run-GpuPython.ps1; CPU fallback is forbidden")


def build_spatial_locator(torch: Any) -> Any:
    """Build a feature-map detector without global spatial pooling."""

    nn = torch.nn
    class SpatialTickLocator(nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.features = nn.Sequential(
                nn.Conv2d(1, 8, kernel_size=3, stride=2, padding=1), nn.ReLU(),
                nn.Conv2d(8, 16, kernel_size=3, stride=2, padding=1), nn.ReLU(),
                nn.Conv2d(16, 24, kernel_size=3, stride=2, padding=1), nn.ReLU(),
                nn.Conv2d(24, 24, kernel_size=3, stride=1, padding=1), nn.ReLU(),
            )
            self.objectness = nn.Conv2d(24, 1, kernel_size=1)
            self.box = nn.Conv2d(24, 4, kernel_size=1)

        def forward(self, value: Any) -> tuple[Any, Any]:
            features = self.features(value)
            raw_box = self.box(features)
            return self.objectness(features), torch.cat((raw_box[:, :2].sigmoid(), raw_box[:, 2:]), dim=1)
    return SpatialTickLocator()


def feature_receptive_field(features: Any, torch: Any) -> int:
    receptive_field = jump = 1
    for layer in features:
        if isinstance(layer, torch.nn.Conv2d):
            kernel = layer.kernel_size[0]
            dilation = layer.dilation[0]
            receptive_field += (kernel - 1) * dilation * jump
            jump *= layer.stride[0]
    return receptive_field


def encode_cell_relative(box: tuple[float, float, float, float], row: int, column: int) -> tuple[float, float, float, float]:
    left, top, right, bottom = box
    if not 0 <= left < right <= 1 or not 0 <= top < bottom <= 1:
        raise ValueError("box must have positive width and height within the image")
    center_x, center_y = (left + right) / 2.0, (top + bottom) / 2.0
    return center_x * 32.0 - column, center_y * 12.0 - row, math.log(right - left), math.log(bottom - top)


def decode_cell_relative(value: tuple[float, float, float, float], row: int, column: int) -> tuple[float, float, float, float]:
    offset_x, offset_y, log_width, log_height = value
    center_x, center_y = (column + offset_x) / 32.0, (row + offset_y) / 12.0
    log_min_width, log_min_height = math.log(1 / 256), math.log(1 / 96)
    width = math.exp(min(0.0, max(log_min_width, log_width)))
    height = math.exp(min(0.0, max(log_min_height, log_height)))
    return center_x - width / 2.0, center_y - height / 2.0, center_x + width / 2.0, center_y + height / 2.0


def stratified_batches(positive_indices: tuple[int, ...], negative_indices: tuple[int, ...], batch_size: int, seed: int) -> tuple[tuple[int, ...], ...]:
    """Shuffle each class, then evenly distribute all examples across mixed batches."""
    total = len(positive_indices) + len(negative_indices)
    if batch_size < 2 or total == 0 or total % batch_size:
        raise ValueError("stratified batches require mixed classes and a divisible full epoch")
    count = total // batch_size
    if len(positive_indices) < count or len(negative_indices) < count:
        raise ValueError("each stratified batch must contain both classes")
    rng = random.Random(seed)
    positives, negatives = list(positive_indices), list(negative_indices)
    rng.shuffle(positives)
    rng.shuffle(negatives)
    batches: list[list[int]] = [[] for _ in range(count)]
    for offset, index in enumerate(positives):
        batches[offset % count].append(index)
    cursor = 0
    for index in negatives:
        while len(batches[cursor]) == batch_size:
            cursor = (cursor + 1) % count
        batches[cursor].append(index)
        cursor = (cursor + 1) % count
    for batch in batches:
        rng.shuffle(batch)
    if any(len(batch) != batch_size for batch in batches):
        raise ValueError("class counts do not fill equal-sized stratified batches")
    return tuple(tuple(batch) for batch in batches)


def target_assignments(boxes: tuple[tuple[float, float, float, float] | None, ...]) -> tuple[tuple[int, int, tuple[float, float, float, float]] | None, ...]:
    result: list[tuple[int, int, tuple[float, float, float, float]] | None] = []
    for box in boxes:
        if box is None:
            result.append(None)
            continue
        left, top, right, bottom = box
        row, column = min(11, int(((top + bottom) / 2.0) * 12)), min(31, int(((left + right) / 2.0) * 32))
        result.append((row, column, encode_cell_relative(box, row, column)))
    return tuple(result)


def evaluate_train_fit(model: Any, features: Any, objectness: Any, targets: Any, mask: Any, device: Any, torch: Any) -> dict[str, float]:
    """Metrics on the same train-only inputs. This is not held-out validation."""
    was_training = model.training
    model.eval()
    with torch.no_grad():
        logits, predicted = model(features.to(device))
        probabilities = logits.sigmoid()
        positive = mask.to(device).bool()
        negative = ~positive
        background_samples = (~positive.flatten(1).any(dim=1))
        predicted_positive = probabilities >= 0.5
        recall = predicted_positive[positive].float().mean()
        mean_objectness = probabilities[positive].mean()
        false_positive_cells = predicted_positive[background_samples].float().sum() / background_samples.sum()
        expanded = positive.expand(-1, 4, -1, -1)
        box_mae = (predicted[expanded] - targets.to(device)[expanded]).abs().mean()
    if was_training:
        model.train()
    return {
        "positive_cell_recall_at_0_5": float(recall.item()),
        "positive_cell_mean_objectness": float(mean_objectness.item()),
        "background_false_positive_cells_per_image_at_0_5": float(false_positive_cells.item()),
        "positive_cell_box_mae_log_parameter_space": float(box_mae.item()),
    }


def _safe_new_output(value: Path, config: Mapping[str, Any]) -> Path:
    candidate = (value if value.is_absolute() else REPO_ROOT / value).absolute()
    artifacts = (REPO_ROOT / "artifacts").absolute()
    try:
        relative = candidate.relative_to(artifacts)
    except ValueError as error:
        raise TrainingAdmissionError("V2 output must remain under project artifacts") from error
    if candidate != (REPO_ROOT / str(config["planned_output_root"])).absolute():
        raise TrainingAdmissionError("V2 output differs from the frozen planned path")
    cursor = artifacts
    for part in relative.parts:
        cursor = cursor / part
        if cursor.exists() and _is_reparse(cursor):
            raise TrainingAdmissionError("V2 output traverses a reparse point")
    if candidate.exists() or candidate.is_symlink():
        raise TrainingAdmissionError("V2 output must be fresh")
    return candidate


def _write_receipt(path: Path, payload: Mapping[str, object]) -> str:
    value = _canonical_json(payload)
    with path.open("xb") as stream:
        stream.write(value)
        stream.flush()
        __import__("os").fsync(stream.fileno())
    return _sha256_bytes(value)


def run_training(output_root: Path) -> dict[str, object]:
    """Run the authorized train-only spatial localization optimizer on RX 6600 XT."""

    config = _config()
    _require_authorized_gpu_launcher(config)
    input_receipt = validate_train_input()
    output = _safe_new_output(output_root, config)
    import numpy as np
    import torch
    from torch import nn
    from torch.utils.data import DataLoader, TensorDataset

    runtime = require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    torch.manual_seed(int(config["seed"]))
    output.mkdir(parents=True, exist_ok=False)
    _write_receipt(output / "started.json", {
        "schema": "graphreader.ocr-fragmented-xtick-v3-gpu-run.v1", "status": "started-unregistered-exploratory",
        "started_utc": datetime.now(timezone.utc).isoformat(), "config_sha256": _sha256_bytes(CONFIG_PATH.read_bytes()),
        "runner_sha256": runner_sha256(), "input": input_receipt, "runtime": runtime,
        "production_approval": False, "release_eligible": False,
    })
    steps = 0
    try:
        manifest = _parse_json(_bound_read(REPO_ROOT, str(config["train_manifest_path"]), "V2 train manifest", MAX_MANIFEST_BYTES), "V2 train manifest")
        root = REPO_ROOT / Path(str(config["train_manifest_path"])).parent
        values: list[Any] = []
        boxes: list[tuple[float, float, float, float] | None] = []
        for row in manifest["records"]:
            image = _bound_read(root, str(row["image_path"]), "V2 image", MAX_IMAGE_BYTES)
            annotation = _parse_json(_bound_read(root, str(row["annotation_path"]), "V2 annotation", MAX_ANNOTATION_BYTES), "V2 annotation")
            if image.sha256 != row.get("image_sha256") or _sha256_bytes(_canonical_json(annotation)) != row.get("annotation_sha256"):
                raise TrainingAdmissionError("V2 optimizer input differs from the frozen manifest")
            if annotation.get("image_sha256") != image.sha256 or annotation.get("case_id") != row.get("case_id"):
                raise TrainingAdmissionError("V2 optimizer annotation does not bind its consumed image")
            values.append(torch.from_numpy(np.asarray(Image.open(BytesIO(image.value)).convert("L"), dtype=np.float32) / 255.0))
            boxes.append(_annotation_target(annotation, positive=row["kind"] == "positive"))
        assignments = target_assignments(tuple(boxes))
        features = torch.stack(values).unsqueeze(1)
        objectness = torch.zeros((96, 1, 12, 32), dtype=torch.float32)
        targets = torch.zeros((96, 4, 12, 32), dtype=torch.float32)
        mask = torch.zeros((96, 1, 12, 32), dtype=torch.bool)
        for index, item in enumerate(assignments):
            if item is not None:
                row, column, box = item; objectness[index, 0, row, column] = 1.0; targets[index, :, row, column] = torch.tensor(box); mask[index, 0, row, column] = True
        model = build_spatial_locator(torch).to(device)
        if feature_receptive_field(model.features, torch) != int(config["expected_receptive_field_pixels"]):
            raise TrainingAdmissionError("V3 model receptive field differs from the P3 config")
        if any(parameter.device != device for parameter in model.parameters()): raise TrainingAdmissionError("V2 model is not resident on RX 6600 XT")
        optimizer = torch.optim.AdamW(model.parameters(), lr=float(config["learning_rate"]))
        train_fit_before = evaluate_train_fit(model, features, objectness, targets, mask, device, torch)
        dataset = TensorDataset(features, objectness, targets, mask)
        positive_indices = tuple(index for index, box in enumerate(boxes) if box is not None)
        negative_indices = tuple(index for index, box in enumerate(boxes) if box is None)
        losses: list[float] = []; positive_losses: list[float] = []; background_losses: list[float] = []
        step_batch_class_counts: list[tuple[int, int]] = []
        epoch_train_fit: list[dict[str, float]] = []
        for epoch in range(int(config["epochs"])):
            batches = stratified_batches(positive_indices, negative_indices, int(config["batch_size"]), int(config["shuffle_seed"]) + epoch)
            for indices in batches:
                batch = torch.utils.data.default_collate([dataset[index] for index in indices])
                image_batch, objectness_target, box_target, positive_mask = batch
                image_batch, objectness_target, box_target, positive_mask = image_batch.to(device), objectness_target.to(device), box_target.to(device), positive_mask.to(device)
                positive_examples = sum(index in positive_indices for index in indices)
                step_batch_class_counts.append((positive_examples, len(indices) - positive_examples))
                if any(value.device != device for value in (image_batch, objectness_target, box_target, positive_mask)): raise TrainingAdmissionError("V2 tensors are not resident on RX 6600 XT")
                optimizer.zero_grad(set_to_none=True)
                logits, predicted = model(image_batch)
                if logits.shape != objectness_target.shape or predicted.shape != box_target.shape: raise TrainingAdmissionError("V2 spatial output shape changed")
                expanded = positive_mask.expand(-1, 4, -1, -1)
                per_cell = nn.functional.binary_cross_entropy_with_logits(logits, objectness_target, reduction="none")
                positive_cells = objectness_target.bool()
                background_cells = ~positive_cells
                positive_mean = per_cell[positive_cells].mean() if bool(positive_cells.any().item()) else None
                background_mean = per_cell[background_cells].mean() if bool(background_cells.any().item()) else None
                positive_term = positive_mean if positive_mean is not None else logits.sum() * 0.0
                background_term = background_mean if background_mean is not None else logits.sum() * 0.0
                objectness_loss = 0.5 * (positive_term + background_term)
                box_loss = nn.functional.smooth_l1_loss(predicted[expanded], box_target[expanded]) if bool(expanded.any().item()) else predicted.sum() * 0.0
                loss = objectness_loss + box_loss
                if not bool(torch.isfinite(loss).item()): raise TrainingAdmissionError("V2 loss is non-finite")
                loss.backward(); optimizer.step(); steps += 1; losses.append(float(loss.detach().cpu().item()))
                if positive_mean is not None: positive_losses.append(float(positive_mean.detach().cpu().item()))
                if background_mean is not None: background_losses.append(float(background_mean.detach().cpu().item()))
            epoch_train_fit.append(evaluate_train_fit(model, features, objectness, targets, mask, device, torch))
        if steps != int(config["expected_optimizer_steps"]): raise TrainingAdmissionError("V2 optimizer step count differs from config")
        train_fit_after = evaluate_train_fit(model, features, objectness, targets, mask, device, torch)
        checkpoint = output / "checkpoint.pt"; torch.save({"state_dict": model.state_dict(), "model_id": config["model"], "architecture": config["architecture"], "box_parameterization": config["box_parameterization"], "receptive_field_pixels": feature_receptive_field(model.features, torch), "input_manifest_sha256": input_receipt["manifest_sha256"], "runtime": runtime, "optimizer_steps": steps}, checkpoint)
        report = {"schema": "graphreader.ocr-fragmented-xtick-v3-gpu-result.v1", "status": "completed-unregistered-exploratory", "input": input_receipt, "runtime": runtime, "model": config["model"], "architecture": config["architecture"], "box_parameterization": config["box_parameterization"], "feature_map_shape": list(model(features[:1].to(device))[0].shape[-2:]), "spatial_receptive_field_pixels": feature_receptive_field(model.features, torch), "optimizer_steps": steps, "loss_first": losses[0], "loss_last": losses[-1], "positive_cell_bce_first": positive_losses[0], "positive_cell_bce_last": positive_losses[-1], "background_cell_bce_first": background_losses[0], "background_cell_bce_last": background_losses[-1], "objectness_loss": "mean(positive_bce,background_bce), both classes present in each batch", "batch_class_counts_first_epoch": [list(pair) for pair in step_batch_class_counts[:len(batches)]], "box_target": "cell-relative center offsets plus log normalized width-height", "train_fit_before": train_fit_before, "epoch_train_fit": epoch_train_fit, "train_fit_after": train_fit_after, "train_fit_is_held_out_validation": False, "checkpoint_path": checkpoint.relative_to(REPO_ROOT).as_posix(), "checkpoint_sha256": _sha256_bytes(checkpoint.read_bytes()), "dev_input_read": False, "sealed_input_read": False, "private_input_read": False, "production_approval": False, "release_eligible": False}
        report_sha256 = _write_receipt(output / "report.json", report)
        return {"output_root": output.relative_to(REPO_ROOT).as_posix(), "report_sha256": report_sha256, "optimizer_steps": steps, "production_approval": False, "release_eligible": False}
    except BaseException as error:
        _write_receipt(output / "failed.json", {"schema": "graphreader.ocr-fragmented-xtick-v3-gpu-result.v1", "status": "failed-unregistered-exploratory", "exception_type": type(error).__name__, "exception_message_sha256": _sha256_bytes(str(error).encode()), "optimizer_steps": steps, "production_approval": False, "release_eligible": False})
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true")
    parser.add_argument("--run", action="store_true")
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    if arguments.preflight == arguments.run:
        parser.error("select exactly one of --preflight or --run")
    if arguments.preflight:
        print(json.dumps({**validate_train_input(), "gpu_runtime_verified": False, "optimizer_execution_authorized": True}, indent=2, sort_keys=True))
        return 0
    if arguments.output is None:
        parser.error("--run requires --output")
    print(json.dumps(run_training(arguments.output), indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
