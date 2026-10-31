# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded, project-local train input admission for V4 recipes."""

from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import stat
from typing import Any

import numpy as np
from PIL import Image

from ml.ocr.fragmented_xtick_detector_v3.gpu_train import _annotation_target
from ml.ocr.fragmented_xtick_detector_v4.materialize import REPO_ROOT


class V4InputAdmissionError(RuntimeError):
    pass


def sha256_bytes(data: bytes) -> str:
    return sha256(data).hexdigest()


def _is_reparse(path: Path) -> bool:
    try:
        info = path.lstat()
    except OSError as error:
        raise V4InputAdmissionError("V4 train input is missing or inaccessible") from error
    flags = getattr(info, "st_file_attributes", 0)
    return path.is_symlink() or bool(flags & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def _reject_reparse_chain(root: Path, relative: Path) -> Path:
    if relative.is_absolute() or ".." in relative.parts:
        raise V4InputAdmissionError("V4 train input path is not a safe relative path")
    root_abs = root.absolute()
    try:
        parts = root_abs.relative_to(Path(root_abs.anchor)).parts + relative.parts
    except ValueError as error:
        raise V4InputAdmissionError("V4 train root is not on its filesystem anchor") from error
    cursor = Path(root_abs.anchor)
    for part in parts:
        cursor = cursor / part
        if _is_reparse(cursor):
            raise V4InputAdmissionError("V4 train input traverses a symlink or reparse point")
    try:
        cursor.resolve(strict=True).relative_to(root_abs.resolve(strict=True))
    except (ValueError, OSError) as error:
        raise V4InputAdmissionError("V4 train input escapes its bound root") from error
    return cursor


def read_bounded(root: Path, relative: str, limit: int) -> bytes:
    """Read at most limit+1 bytes, rejecting oversize/reparse/escaping paths."""
    if type(limit) is not int or limit < 0:
        raise ValueError("input byte limit must be a nonnegative integer")
    path = _reject_reparse_chain(root, Path(relative))
    if not path.is_file():
        raise V4InputAdmissionError("V4 train input is not a regular file")
    try:
        with path.open("rb") as stream:
            data = stream.read(limit + 1)
    except OSError as error:
        raise V4InputAdmissionError("V4 train input could not be read") from error
    if len(data) > limit:
        raise V4InputAdmissionError("V4 train input exceeds its byte limit")
    return data


def load_train_inputs(torch: Any, manifest_relative: str, expected_manifest_sha256: str | None = None) -> tuple[dict[str, Any], list[Any], list[tuple[float, float, float, float] | None], str]:
    allowed = "artifacts/fragmented-xtick-v4-diverse-train-v1/manifest.json"
    if manifest_relative != allowed:
        raise V4InputAdmissionError("V4 trainer only accepts its generated train manifest")
    manifest_bytes = read_bounded(REPO_ROOT, manifest_relative, 1_000_000)
    manifest_sha = sha256_bytes(manifest_bytes)
    if expected_manifest_sha256 is not None and manifest_sha != expected_manifest_sha256:
        raise V4InputAdmissionError("V4 train manifest differs from the config-pinned SHA-256")
    try:
        manifest = json.loads(manifest_bytes)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise V4InputAdmissionError("V4 train manifest is not valid JSON") from error
    if (manifest.get("schema") != "graphreader.ocr-fragmented-xtick-v4-train.v1" or manifest.get("split") != "train"
            or manifest.get("family") != "fragmented-xtick-v4-diverse-train"
            or manifest.get("labels") != ["6", "9", "13", "18", "45", "90", "250", "2.75", "-8", "0.125", "35.5", "750"]
            or manifest.get("canvas") != [256, 96] or manifest.get("probe_inputs_read") is not False
            or manifest.get("dev_sealed_private_inputs_read") is not False):
        raise V4InputAdmissionError("V4 manifest is outside its train-only data contract")
    root = REPO_ROOT / Path(manifest_relative).parent
    records = manifest.get("records")
    if not isinstance(records, list) or len(records) != manifest.get("record_count") or len(records) != 288:
        raise V4InputAdmissionError("V4 train manifest record count is invalid")
    values, boxes, case_ids = [], [], set()
    for row in records:
        case_id = row.get("case_id")
        if (row.get("split") != "train" or case_id in case_ids
                or not isinstance(case_id, str) or not case_id.startswith("fragmented-xtick-v4-diverse-train-train-")):
            raise V4InputAdmissionError("V4 train records contain a duplicate or non-train identity")
        case_ids.add(case_id)
        image_bytes = read_bounded(root, row["image_path"], 1_000_000)
        annotation_bytes = read_bounded(root, row["annotation_path"], 1_000_000)
        image_sha, annotation_sha = sha256_bytes(image_bytes), sha256_bytes(annotation_bytes)
        try:
            annotation = json.loads(annotation_bytes)
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise V4InputAdmissionError("V4 train annotation is not valid JSON") from error
        if (image_sha != row.get("image_sha256") or annotation_sha != row.get("annotation_sha256")
                or annotation.get("image_sha256") != image_sha or annotation.get("case_id") != case_id
                or annotation.get("case_identity_sha256") != row.get("case_identity_sha256")
                or annotation.get("split") != "train" or annotation.get("materialized") is not True):
            raise V4InputAdmissionError("V4 image/annotation bytes are not bound to manifest identity")
        try:
            pixels = np.asarray(Image.open(BytesIO(image_bytes)).convert("L"), dtype=np.float32) / 255.0
        except Exception as error:
            raise V4InputAdmissionError("V4 train image could not be decoded") from error
        if pixels.shape != (96, 256):
            raise V4InputAdmissionError("V4 train image dimensions differ from canvas contract")
        values.append(torch.from_numpy(pixels))
        boxes.append(_annotation_target(annotation, positive=row["kind"] == "positive"))
    if (sum(box is not None for box in boxes), sum(box is None for box in boxes)) != (192, 96):
        raise V4InputAdmissionError("V4 positive/background counts differ from protocol")
    return manifest, values, boxes, manifest_sha
