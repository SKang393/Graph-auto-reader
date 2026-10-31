# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""GPU-environment reader for pinned synthetic caches, with no CPU transforms.

Four-scene development data, historical independence, target ambiguities and
the localized mask policy remain unapproved. This reader opens no heldout,
private or sealed pixels or labels.
"""
from dataclasses import dataclass
from hashlib import sha256
import json
from pathlib import Path

import numpy as np

from ml.markers.center.real_positive_context_v1.positive_archive import _open_read_only_no_writers

ROOT = Path(__file__).resolve().parents[3]
REPORT = ROOT / "artifacts/synthetic-tick-feature-preflight-960-v1-20261030/report.json"
REPORT_SHA = "7409758605004e060d0f6b27072c47e1d2a2eda99f26e523ebb2254b2334ba96"
MASK_DIR = ROOT / "artifacts/ocr-db-head-localized-mask-cache-v1-20261030"
MASK_MANIFEST_SHA = "a9c30e80c270384ebc2ed8252013dc2ac76fdfcd0aed8b2dda69635c5fbedc86"
MASK_SHA = (
    "dc395d51123c93eee1bfa9e8ec428d1cab03bd1312baf85a5c4b18a91e201171",
    "8f4553faa22fa8f67666d55d3bd5ba5f16b844190b2fe551a4b560f47b6e384e",
    "1a4f5c22696e4d10f9980da4997e12595641623b7dacd99b7452469de76b2b91",
    "7e55a4234f8074fa4e98e59ae04d605f0a2d4a195b8451478418f50710d7a382",
)
FEATURE_SHA = (
    "35ceff40cd4606b6b574b19e9222f586ba42b6d140b0407f52ed64a6c856a8b0",
    "3b18b9e96e654caa942651f39b00f30086cbe20f0073ef12669fc51edb6001d1",
    "f70cc41438c70a3d5913fb2699361cd8fdb8522d1b2571a5a082f02606e7005c",
    "6effc16156261fab1dea89d171ce489d9e43e358834a130aa7f644fa854a9d15",
)
TARGET_SHA = (
    "81e6d20e15f808f57064da8f54f4a5bf4d9b5d463314f6f633c2e83151a0f088",
    "2d3c0b429812f8d8ad69130ef53ad32fa915ddd06e02f94ee0750428f1f456eb",
    "5cacbe621434d37f28b57d339b4e84bee02a62d97f63ab414499aa6b68e815ab",
    "d44ef683e8b2f7cec99c2e0c8b425e1fb755089a3bb83a6e9d06eb87a7e13ef2",
)
MODEL = ROOT / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/ocr-scale-coverage-v46/P1/detector-scale-coverage-v46-p1.onnx"
MODEL_SHA = "497a3527820ce0359496084e5afb1e0d3241aca79e192187f7ab737b27818b7f"
TRUNK_SHA = "6115e9e02bb20ea495b04e3f5329721115ba72b5e79cc7246773919c9699b081"
PARITY_SHA = "97d5712832b6ca3e07c9d4ad086dc4525cec37873e6daec61ff59eb4ab59e982"
PARITY = ROOT / "artifacts/synthetic-tensor-parity-960-comparison-v3-20261030.json"
IDENTITY_SHA = "81c3ea76acec8ce59f749387e603e98de99bbaad8c0326b1f105ef42af0c6bd0"
IDENTITY = ROOT / "artifacts/synthetic-tensor-parity-identity-v3-20261030.json"


@dataclass(frozen=True)
class Sample:
    feature: np.ndarray
    target: np.ndarray
    mask: np.ndarray
    feature_sha256: str
    target_sha256: str
    mask_sha256: str


@dataclass(frozen=True)
class BoundSource:
    samples: tuple[Sample, ...]
    preflight_sha256: str
    model_sha256: str
    mask_policy_sha256: str


def _read(path: Path, expected: str) -> bytes:
    if not isinstance(expected, str) or len(expected) != 64 or any(c not in "0123456789abcdef" for c in expected):
        raise ValueError("Bound SHA-256 is invalid")
    path = path.resolve(strict=True)
    if not path.is_relative_to(ROOT.resolve(strict=True)):
        raise ValueError("Bound file escapes project")
    with _open_read_only_no_writers(path) as stream:
        raw = stream.read()
    if sha256(raw).hexdigest() != expected:
        raise ValueError("Bound file hash differs")
    return raw


def _json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON key")
            result[key] = value
        return result
    result = json.loads(raw, object_pairs_hook=pairs)
    if not isinstance(result, dict):
        raise ValueError("Expected JSON object")
    return result


def _array(path: Path, descriptor: dict, shape: tuple[int, ...], *, binary: bool) -> np.ndarray:
    if descriptor.get("dtype") != "float32-le" or descriptor.get("shape") != list(shape):
        raise ValueError("Bound cache descriptor differs")
    raw = _read(path, descriptor.get("sha256"))
    if len(raw) != int(np.prod(shape)) * 4:
        raise ValueError("Bound cache byte count differs")
    array = np.frombuffer(raw, dtype="<f4").reshape(shape)
    if not np.isfinite(array).all() or (binary and not np.isin(array, [0, 1]).all()):
        raise ValueError("Bound cache contains invalid values")
    return array


def load_bound_source() -> BoundSource:
    report = _json(_read(REPORT, REPORT_SHA))
    required = {"schema": "graphreader.synthetic-tick-production-size-feature-preflight.v1", "status": "completed_diagnostic",
                "train_scene_count": 4, "training_input_approved": False, "production_approved": False,
                "full_production_workflow_replayed": False, "private_reads": 0, "sealed_reads": 0,
                "holdout_raster_or_label_reads": 0, "optimizer_steps": 0, "model_sha256": MODEL_SHA,
                "trunk_sha256": TRUNK_SHA, "tensor_parity_report_sha256": PARITY_SHA, "tensor_parity_identity_sha256": IDENTITY_SHA}
    if any(type(report.get(key)) is not type(value) or report.get(key) != value for key, value in required.items()):
        raise ValueError("Bound preflight scope differs")
    modules = {"script": "ml/ocr/gpu_db_head_probe_v1/feature_preflight_960.py",
               "target_builder": "ml/ocr/official_bakeoff/db_inverse_targets.py",
               "geometry_audit": "ml/ocr/gpu_db_head_probe_v1/target_geometry_audit.py",
               "conservative_mask": "ml/ocr/gpu_db_head_probe_v1/loss_mask_counterfactual.py",
               "conflict_mask": "ml/ocr/gpu_db_head_probe_v1/conflict_pixel_counterfactual.py"}
    for key, relative in modules.items():
        _read(ROOT / relative, report["source_sha256"][key])
    compared = _json(_read(PARITY, PARITY_SHA))
    identity = _json(_read(IDENTITY, IDENTITY_SHA))
    if (compared.get("run_identity_sha256") != IDENTITY_SHA or compared.get("run_identity_files") != identity.get("files") or
            compared.get("all_tensors_byte_identical") is not True or compared.get("training_input_approved") is not False):
        raise ValueError("Parity identity or scope differs")
    files = identity.get("files")
    if not isinstance(files, dict) or len(files) != 24:
        raise ValueError("Parity source inventory differs")
    for descriptor in files.values():
        relative = Path(descriptor["path"])
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError("Parity file path is unsafe")
        _read(ROOT / relative, descriptor["sha256"])
    manifest = _json(_read(MASK_DIR / "manifest.json", MASK_MANIFEST_SHA))
    if (manifest.get("schema") != "graphreader.synthetic-tick-localized-mask-cache.v1" or
            manifest.get("preflight_sha256") != REPORT_SHA or manifest.get("model_sha256") != MODEL_SHA or
            manifest.get("mask_policy_sha256") != report["source_sha256"]["conflict_mask"] or
            manifest.get("training_approved") is not False or manifest.get("optimizer_steps") != 0 or
            any(manifest.get(key) != 0 for key in ("private_reads", "sealed_reads", "holdout_raster_or_label_reads"))):
        raise ValueError("Localized-mask cache scope differs")
    _read(Path(__file__).with_name("source_derivation.py"), manifest.get("derivation_source_sha256"))
    _read(Path(__file__).with_name("prepare_masks.py"), manifest.get("builder_source_sha256"))
    _read(MODEL, MODEL_SHA)
    rows, parity_rows, masks = report.get("cases"), compared.get("cases"), manifest.get("masks")
    if not all(isinstance(value, list) and len(value) == 4 for value in (rows, parity_rows, masks)):
        raise ValueError("Bound input population differs")
    samples = []
    for index, (row, proof, mask_desc) in enumerate(zip(rows, parity_rows, masks, strict=True)):
        shape = row.get("shape")
        if (not isinstance(shape, list) or len(shape) != 4 or shape[:2] != [1, 3] or
                any(type(v) is not int or v <= 0 or v > 1024 or v % 128 for v in shape[2:]) or
                row.get("source_image_sha256") != proof.get("source_image_sha256") or
                row.get("tensor_sha256") != proof.get("candidate_tensor_sha256") or proof.get("byte_identical") is not True):
            raise ValueError("Train image or parity binding differs")
        h, w = shape[2:]
        descriptors = row.get("arrays", {})
        expected = {"features": f"train-{index}.features.f32", "target": f"train-{index}.target.f32", "mask": f"train-{index}.mask.f32"}
        if set(descriptors) != set(expected) or any(descriptors[k].get("file") != v for k, v in expected.items()):
            raise ValueError("Feature cache inventory differs")
        if descriptors["features"].get("sha256") != FEATURE_SHA[index] or descriptors["target"].get("sha256") != TARGET_SHA[index]:
            raise ValueError("Feature or target identity differs")
        if mask_desc.get("file") != f"train-{index}.localized-mask.f32" or mask_desc.get("sha256") != MASK_SHA[index]:
            raise ValueError("Localized mask identity differs")
        feature = _array(REPORT.parent / expected["features"], descriptors["features"], (1, 96, h // 4, w // 4), binary=False)
        target = _array(REPORT.parent / expected["target"], descriptors["target"], (1, h, w), binary=True)
        baseline = _array(REPORT.parent / expected["mask"], descriptors["mask"], (1, h, w), binary=True)
        localized = _array(MASK_DIR / mask_desc["file"], mask_desc, (1, h, w), binary=True)
        if (np.any(localized > baseline) or not np.any((localized == 1) & (target == 1)) or
                not np.any((localized == 1) & (target == 0))):
            raise ValueError("Localized mask changes artifact exclusions or lacks supervised classes")
        samples.append(Sample(feature, target, localized, descriptors["features"]["sha256"], descriptors["target"]["sha256"], MASK_SHA[index]))
    return BoundSource(tuple(samples), REPORT_SHA, MODEL_SHA, report["source_sha256"]["conflict_mask"])
