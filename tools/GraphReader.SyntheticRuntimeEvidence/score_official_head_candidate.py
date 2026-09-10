# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score an authenticated frozen official-head OCR candidate on V3 synthetic inputs.

All annotation-free runtime evidence is authenticated before the production-head
loader is allowed to regenerate project-owned truth. Detection is graded at the
fixed IoU 0.5 operating point. Recognition text and role accuracy are not scored.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from hashlib import sha256
import json
import math
from pathlib import Path
import sys
import time
from typing import Any, Mapping, Sequence


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from ml.ocr.component_region_detector_v6.dataset import Box  # noqa: E402
from ml.ocr.official_bakeoff import production_head_inputs  # noqa: E402
from ml.ocr.real_range_proposal_v34.pipeline import maximum_cardinality_matches  # noqa: E402


class EvidenceError(ValueError):
    """Authenticated evidence is incomplete, inconsistent, or out of scope."""


OUTPUT_SCHEMA = "graphreader.official-head-candidate-score.v1"
EVALUATION_SCHEMA = "graphreader.official-head-candidate-evaluation.v1"
CANDIDATE_SCHEMA = "graphreader.frozen-db-head-ocr-candidate.v1"
CANDIDATE_SCOPE = "project-owned-synthetic-train-dev-unapproved-frozen-candidate"
CAPTURE_REQUEST_SCHEMA = "graphreader.official-head-tensor-capture-request.v1"
CAPTURE_REPORT_SCHEMA = "graphreader.official-head-tensor-capture-report.v1"
RUNTIME_REPORT_SCHEMA = "graphreader.synthetic-runtime-seed-evidence.v2"
RUNTIME_SCOPE = "local-synthetic-seed-diagnostic"
COMPOSITION_VERSION = "original-db-head-candidate-v1"
EXPECTED_ASSEMBLIES = {
    "GraphReader.SyntheticRuntimeEvidence", "GraphReader.App",
    "GraphReader.Ocr", "GraphReader.Inference",
}
EXPECTED_COUNTS = {
    "train": {"sources": 20, "panels": 28, "truths": 709},
    "validation": {"sources": 3, "panels": 9, "truths": 183},
}
PINNED_CAPTURE_MODEL_SHA256 = (
    "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb"
)
HISTORICAL_CAPTURE_MODEL_PATH = Path(
    "ml/ocr/official_bakeoff/runs/conversion/PP-OCRv5_mobile_det.onnx"
)
REVIEWED_CAPTURE_MODEL_PATH = Path(
    "artifacts/goal22-runs/pretrained-detector-head-feasibility/reviewed-parent.onnx"
)
HISTORICAL_CAPTURE_ARTIFACT_MIRRORS = {
    HISTORICAL_CAPTURE_MODEL_PATH: REVIEWED_CAPTURE_MODEL_PATH,
    Path("artifacts/goal19-opencv-source/evidence-repro-pass2-final-a/bin/OpenCvSharpExtern.dll"):
        Path("artifacts/synthetic-runtime-evidence/"
             "advisory-structure-source-c96-executable-v1/OpenCvSharpExtern.dll"),
    Path("LICENSES/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt"):
        Path("LICENSES/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt"),
    Path("LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt"):
        Path("LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt"),
}
MATCH_IOU_MINIMUM = 0.5
ACCEPTANCE_BARS_PATH = Path("ml/policy/acceptance-bars.json")
EXPECTED_ACCEPTANCE_BARS_SHA256 = (
    "aab9f2ab60cf166828f0928b8496f537341870fd457d0408952e22549fc53a56"
)
SOURCE_BINDINGS = {
    "ml/ocr/official_bakeoff/production_head_inputs.py":
        "51800bcddfd9fc02c538bba04015312f94fb88113ea14c563616db58a61cff90",
    "ml/ocr/production_tiled_inputs.py":
        "b2ee5dba050790b504f7d35b129d882d4d5bcf011d87a3b21a4431202d3fdc35",
    "ml/ocr/real_range_proposal_v34/pipeline.py":
        "7cfc997de0ceb39edfbc4f50c0ea5f07fb058f8eb65ceb21745ef099e291afa5",
    "ml/ocr/real_range_proposal_v34/dataset.py":
        "1dea389b6941dd68a3f900618c843587ac8deddcffd5743b2d4dbfff135fc3b4",
    "ml/ocr/component_context_detector_v7/dataset.py":
        "96bebccedc58404e369a1ac3ef6fe3d4d8baa657872543f8958ed73e902d595f",
    "ml/ocr/component_region_detector_v6/dataset.py":
        "cfc1760cfe0f5231d34960100bed3b24bbd54bab6ab69919c3eae43fddc22f59",
}


@dataclass(frozen=True)
class _Prediction:
    box: Box


@dataclass(frozen=True)
class _PanelIdentity:
    split: str
    source_sha256: str
    source_width: int
    source_height: int
    panel_id: str
    panel_sha256: str
    width: int
    height: int
    crop: tuple[int, int, int, int]
    requested_crop: tuple[int, int, int, int]
    source_to_panel_matrix: tuple[float, ...]
    panel_to_source_matrix: tuple[float, ...]
    gray_sha256: str
    bgr_sha256: str


@dataclass(frozen=True)
class _ValidatedEvidence:
    report: Mapping[str, Any]
    candidate: Mapping[str, Any]
    panels: Mapping[str, _PanelIdentity]
    raw_by_source: Mapping[str, tuple[_Prediction, ...]]
    recognized_by_source: Mapping[str, tuple[_Prediction, ...]]
    explicit_region_failures: Mapping[str, int]
    failed_panel_raw_regions: Mapping[str, int]


def _sha(value: Any, label: str) -> str:
    if (not isinstance(value, str) or len(value) != 64 or value != value.lower()
            or any(character not in "0123456789abcdef" for character in value)):
        raise EvidenceError(f"{label} must be a lowercase SHA-256")
    return value


def _inside(root: Path, value: Any, label: str) -> Path:
    if not isinstance(value, (str, Path)) or not str(value).strip():
        raise EvidenceError(f"{label} path is missing")
    candidate = Path(value)
    resolved = (candidate if candidate.is_absolute() else root / candidate).resolve()
    if resolved == root or root not in resolved.parents:
        raise EvidenceError(f"{label} escaped the repository")
    return resolved


def _read_exact(root: Path, path_value: Any, digest_value: Any, label: str,
                byte_count: Any | None = None) -> tuple[Path, bytes]:
    path = _inside(root, path_value, label)
    digest = _sha(digest_value, f"{label} SHA-256")
    if not path.is_file():
        raise EvidenceError(f"{label} is missing")
    payload = path.read_bytes()
    if byte_count is not None and (type(byte_count) is not int or byte_count != len(payload)):
        raise EvidenceError(f"{label} byte count changed")
    if sha256(payload).hexdigest() != digest:
        raise EvidenceError(f"{label} bytes differ from the authenticated identity")
    return path, payload


def _object(value: Any, label: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise EvidenceError(f"{label} must be an object")
    return value


def _array(value: Any, label: str) -> Sequence[Any]:
    if not isinstance(value, list):
        raise EvidenceError(f"{label} must be an array")
    return value


def _json(payload: bytes, label: str) -> Mapping[str, Any]:
    try:
        return _object(json.loads(payload), label)
    except (json.JSONDecodeError, UnicodeDecodeError) as exception:
        raise EvidenceError(f"{label} is invalid JSON") from exception


def _integer(value: Any, label: str, *, minimum: int = 0) -> int:
    if type(value) is not int or value < minimum:
        raise EvidenceError(f"{label} must be an integer >= {minimum}")
    return value


def _finite(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise EvidenceError(f"{label} must be finite")
    return float(value)


def _box(value: Any, label: str) -> tuple[int, int, int, int]:
    record = _object(value, label)
    if set(record) != {"x", "y", "width", "height"}:
        raise EvidenceError(f"{label} has unknown or missing fields")
    result = tuple(_integer(record[key], f"{label} {key}") for key in ("x", "y", "width", "height"))
    if result[2] <= 0 or result[3] <= 0:
        raise EvidenceError(f"{label} is empty")
    return result


def _matrix(value: Any, label: str) -> tuple[float, ...]:
    values = _array(value, label)
    if len(values) != 9:
        raise EvidenceError(f"{label} must have nine values")
    return tuple(_finite(item, label) for item in values)


def _validate_inverse(forward: tuple[float, ...], inverse: tuple[float, ...]) -> None:
    for row in range(3):
        for column in range(3):
            value = sum(forward[row * 3 + index] * inverse[index * 3 + column]
                        for index in range(3))
            if abs(value - (1.0 if row == column else 0.0)) > 1e-9:
                raise EvidenceError("panel transforms are not mutual inverses")


def _descriptor(root: Path, value: Any, label: str) -> tuple[Path, bytes]:
    record = _object(value, label)
    if set(record) != {"path", "sha256"}:
        raise EvidenceError(f"{label} has unknown or missing fields")
    return _read_exact(root, record["path"], record["sha256"], label)


def _read_historical_capture_artifact(
    root: Path, path_value: Any, digest_value: Any, label: str
) -> tuple[Path, bytes]:
    """Authenticate a known historical path through its retained local mirror.

    The immutable tensor-capture request predates the isolated Goal 22 worktree
    and names a few inputs under the containing main checkout.  Only explicitly
    mapped historical locations are accepted.  Retained byte-identical mirrors
    under the scoring root supply the bytes used for authentication.
    """

    digest = _sha(digest_value, f"{label} SHA-256")
    if not isinstance(path_value, (str, Path)) or not str(path_value).strip():
        raise EvidenceError(f"{label} path is missing")
    candidate = Path(path_value)
    resolved = (candidate if candidate.is_absolute() else root / candidate).resolve()
    if resolved != root and root in resolved.parents:
        return _read_exact(root, resolved, digest, label)

    if (root.parent.name != "goal22-worktrees" or root.parent.parent.name != "artifacts"):
        raise EvidenceError(f"{label} escaped the repository")
    historical_root = root.parent.parent.parent
    try:
        historical_relative = resolved.relative_to(historical_root)
    except ValueError as exception:
        raise EvidenceError(f"{label} escaped the repository") from exception
    mirror = HISTORICAL_CAPTURE_ARTIFACT_MIRRORS.get(historical_relative)
    if mirror is None:
        raise EvidenceError(f"{label} is not an authenticated historical capture artifact")
    return _read_exact(root, mirror, digest, f"reviewed {label}")


def _validate_capture_detector_model(root: Path, path_value: Any, digest_value: Any) -> Path:
    digest = _sha(digest_value, "capture detector model SHA-256")
    if digest != PINNED_CAPTURE_MODEL_SHA256:
        raise EvidenceError("capture detector model is not the pinned official model")
    return _read_historical_capture_artifact(
        root, path_value, digest, "capture detector model")[0]


def _validate_assemblies(root: Path, value: Any, label: str) -> tuple[dict[str, str], ...]:
    records = _array(value, label)
    if len(records) != 4:
        raise EvidenceError(f"{label} must contain four assemblies")
    result: list[dict[str, str]] = []
    seen: set[str] = set()
    roots: set[Path] = set()
    for raw in records:
        record = _object(raw, f"{label} item")
        if set(record) != {"name", "path", "sha256"}:
            raise EvidenceError(f"{label} item has unknown or missing fields")
        name = record.get("name")
        if not isinstance(name, str) or name not in EXPECTED_ASSEMBLIES or name in seen:
            raise EvidenceError(f"{label} names are incomplete or duplicated")
        path, _ = _read_exact(root, record["path"], record["sha256"], f"{label} {name}")
        roots.add(path.parent)
        seen.add(name)
        result.append({"name": name, "path": path.relative_to(root).as_posix(),
                       "sha256": _sha(record["sha256"], f"{label} {name}")})
    if seen != EXPECTED_ASSEMBLIES or len(roots) != 1:
        raise EvidenceError(f"{label} does not identify one complete immutable executable snapshot")
    return tuple(sorted(result, key=lambda item: item["name"]))


def _validate_candidate(root: Path, path: Path, expected_sha256: str) -> tuple[Mapping[str, Any], bytes]:
    candidate_path, payload = _read_exact(root, str(path), expected_sha256, "candidate")
    candidate = _json(payload, "candidate")
    required = {"schema", "scope", "production_approved", "training_input_ready",
                "composition_version", "native_path", "native_sha256", "native_scope",
                "license_inputs", "detector", "recognizer", "execution_assemblies"}
    if set(candidate) != required:
        raise EvidenceError("candidate has unknown or missing fields")
    if (candidate.get("schema") != CANDIDATE_SCHEMA or candidate.get("scope") != CANDIDATE_SCOPE
            or candidate.get("production_approved") is not False
            or candidate.get("training_input_ready") is not False
            or candidate.get("composition_version") != COMPOSITION_VERSION
            or candidate.get("native_scope") != "reviewed-source-runtime-local-diagnostic"):
        raise EvidenceError("candidate scope or composition is invalid")
    _read_exact(root, candidate["native_path"], candidate["native_sha256"], "candidate native")
    _validate_assemblies(root, candidate["execution_assemblies"], "candidate execution assemblies")
    licenses = _array(candidate["license_inputs"], "candidate license inputs")
    if not licenses:
        raise EvidenceError("candidate has no license inputs")
    license_keys: set[tuple[str, str]] = set()
    for raw in licenses:
        record = _object(raw, "candidate license input")
        if set(record) != {"path", "sha256"}:
            raise EvidenceError("candidate license input has unknown or missing fields")
        bound_path, _ = _read_exact(root, record["path"], record["sha256"], "candidate license input")
        key = (str(bound_path).lower(), _sha(record["sha256"], "candidate license input"))
        if key in license_keys:
            raise EvidenceError("candidate repeats a license input")
        license_keys.add(key)
    model_hashes: set[str] = set()
    for role, task in (("detector", "ocr_detection"), ("recognizer", "ocr_recognition")):
        model = _object(candidate[role], f"candidate {role}")
        required_model = {"model_path", "model_id", "model_version", "model_sha256",
                          "manifest_path", "manifest_sha256"}
        if set(model) != required_model:
            raise EvidenceError(f"candidate {role} has unknown or missing fields")
        _read_exact(root, model["model_path"], model["model_sha256"], f"candidate {role} model")
        _, manifest_payload = _read_exact(root, model["manifest_path"], model["manifest_sha256"],
                                          f"candidate {role} manifest")
        manifest = _json(manifest_payload, f"candidate {role} manifest")
        if (manifest.get("model_id") != model.get("model_id")
                or manifest.get("model_version") != model.get("model_version")
                or manifest.get("task") != task
                or manifest.get("sha256") != model.get("model_sha256")
                or "cpu" not in [str(item).lower() for item in _array(manifest.get("providers"),
                                                                       f"{role} providers")]):
            raise EvidenceError(f"candidate {role} manifest identity changed")
        model_hashes.add(_sha(model["model_sha256"], f"candidate {role} model"))
    if len(model_hashes) != 2:
        raise EvidenceError("candidate detector and recognizer payloads are not distinct")
    return candidate, payload


def _runtime_panels(root: Path, request: Mapping[str, Any]) -> dict[str, _PanelIdentity]:
    report_records = _array(request.get("reports"), "capture request reports")
    if (len(report_records) != 6
            or sum(_object(item, "report").get("split") == "train" for item in report_records) != 5
            or sum(_object(item, "report").get("split") == "validation" for item in report_records) != 1):
        raise EvidenceError("capture request does not bind five train and one validation reports")
    report_by_path: dict[Path, tuple[str, str, Mapping[str, Any]]] = {}
    source_counts = {"train": 0, "validation": 0}
    all_source_ids: set[str] = set()
    report_panel_ids: set[str] = set()
    for raw in report_records:
        descriptor = _object(raw, "capture request report")
        if set(descriptor) != {"split", "manifest_path", "manifest_sha256", "report_path", "report_sha256"}:
            raise EvidenceError("capture request report has unknown or missing fields")
        split = descriptor.get("split")
        if split not in EXPECTED_COUNTS:
            raise EvidenceError("capture request report has an invalid split")
        _, manifest_payload = _read_exact(root, descriptor["manifest_path"], descriptor["manifest_sha256"],
                                          "runtime input manifest")
        manifest = _json(manifest_payload, "runtime input manifest")
        images = _array(manifest.get("images"), "runtime input manifest images")
        if any(_object(image, "manifest image").get("split") != split for image in images):
            raise EvidenceError("runtime input manifest contains a foreign split")
        manifest_sources = {
            _sha(_object(image, "manifest image").get("image_sha256"), "manifest image"):
            (_integer(_object(image, "manifest image").get("width"), "manifest width", minimum=1),
             _integer(_object(image, "manifest image").get("height"), "manifest height", minimum=1))
            for image in images
        }
        if len(manifest_sources) != len(images):
            raise EvidenceError("runtime input manifest repeats a source")
        for raw_image in images:
            image = _object(raw_image, "manifest image")
            image_path = _inside(root, _inside(root, descriptor["manifest_path"],
                                               "runtime input manifest").parent / str(image.get("image")),
                                 "manifest source image")
            source_payload = image_path.read_bytes() if image_path.is_file() else b""
            if sha256(source_payload).hexdigest() != image.get("image_sha256"):
                raise EvidenceError("runtime source image bytes differ from its manifest")
        report_path, report_payload = _read_exact(root, descriptor["report_path"], descriptor["report_sha256"],
                                                  "runtime report")
        report = _json(report_payload, "runtime report")
        if (report.get("schema") != RUNTIME_REPORT_SCHEMA or report.get("scope") != RUNTIME_SCOPE
                or report.get("production_approved") is not False
                or report.get("input_manifest_sha256") != descriptor["manifest_sha256"]
                or report.get("failed") != 0 or report.get("failed_panels") != 0
                or report.get("completed") != report.get("count")
                or report.get("completed_panels") != report.get("panel_count")):
            raise EvidenceError("runtime report is incomplete or out of scope")
        cases = _array(report.get("cases"), "runtime cases")
        if len(cases) != len(manifest_sources):
            raise EvidenceError("runtime report source count differs from its manifest")
        case_sources: set[str] = set()
        for raw_case in cases:
            case = _object(raw_case, "runtime case")
            source_sha = _sha(case.get("image_sha256"), "runtime source")
            dims = manifest_sources.get(source_sha)
            if (dims is None or dims != (case.get("width"), case.get("height"))
                    or case.get("status") != "panels-completed"):
                raise EvidenceError("runtime source identity differs from its manifest")
            case_sources.add(source_sha)
            for raw_panel in _array(case.get("panels"), "runtime panels"):
                panel = _object(raw_panel, "runtime panel")
                panel_id = panel.get("panel_id")
                if not isinstance(panel_id, str) or not panel_id or panel_id in report_panel_ids:
                    raise EvidenceError("runtime panel identity is missing or duplicated")
                report_panel_ids.add(panel_id)
        if case_sources != set(manifest_sources):
            raise EvidenceError("runtime report source inventory differs from its manifest")
        if all_source_ids & case_sources:
            raise EvidenceError("runtime reports repeat a source identity")
        all_source_ids.update(case_sources)
        if report_path in report_by_path:
            raise EvidenceError("capture request repeats a runtime report")
        report_by_path[report_path] = (split, descriptor["report_sha256"], report)
        source_counts[split] += len(cases)
    if any(source_counts[split] != expected["sources"] for split, expected in EXPECTED_COUNTS.items()):
        raise EvidenceError("runtime source denominator changed")

    result: dict[str, _PanelIdentity] = {}
    request_panels = _array(request.get("panels"), "capture request panels")
    for raw in request_panels:
        panel = _object(raw, "capture request panel")
        panel_id = panel.get("panel_id")
        if not isinstance(panel_id, str) or not panel_id or panel_id in result:
            raise EvidenceError("capture request panel identity is missing or duplicated")
        split = panel.get("split")
        report_path = _inside(root, panel.get("report_path"), "panel runtime report")
        report_binding = report_by_path.get(report_path)
        if (split not in EXPECTED_COUNTS or report_binding is None or report_binding[0] != split
                or panel.get("report_sha256") != report_binding[1]):
            raise EvidenceError("capture panel report binding changed")
        source_sha = _sha(panel.get("source_sha256"), "capture source")
        report_case = next((_object(case, "runtime case") for case in report_binding[2]["cases"]
                            if _object(case, "runtime case").get("image_sha256") == source_sha), None)
        if report_case is None:
            raise EvidenceError("capture source is absent from its runtime report")
        report_panel = next((_object(item, "runtime panel") for item in report_case["panels"]
                             if _object(item, "runtime panel").get("panel_id") == panel_id), None)
        if report_panel is None:
            raise EvidenceError("capture panel is absent from its runtime report")
        width = _integer(panel.get("width"), "panel width", minimum=1)
        height = _integer(panel.get("height"), "panel height", minimum=1)
        crop_list = _array(panel.get("crop"), "capture crop")
        if len(crop_list) != 4 or any(type(item) is not int for item in crop_list):
            raise EvidenceError("capture crop must contain four integers")
        crop = tuple(crop_list)
        report_crop = _box(report_panel.get("crop"), "runtime crop")
        requested_crop = _box(report_panel.get("requested_crop"), "runtime requested crop")
        panel_sha = _sha(panel.get("panel_sha256"), "capture panel")
        source_width = _integer(report_case.get("width"), "source width", minimum=1)
        source_height = _integer(report_case.get("height"), "source height", minimum=1)
        source_to_panel = _matrix(report_panel.get("source_to_panel_matrix"), "source-to-panel matrix")
        panel_to_source = _matrix(report_panel.get("panel_to_source_matrix"), "panel-to-source matrix")
        _validate_inverse(source_to_panel, panel_to_source)
        gray = _sha(panel.get("recorded_unmasked_gray_sha256"), "request Gray8")
        bgr = _sha(panel.get("reconstructed_bgr_sha256"), "request BGR24")
        diagnostic = _object(report_panel.get("ocr_proposal_diagnostic"), "runtime OCR diagnostic")
        if (crop[0] < 0 or crop[1] < 0 or crop[0] + crop[2] > source_width
                or crop[1] + crop[3] > source_height
                or requested_crop[0] < crop[0] or requested_crop[1] < crop[1]
                or requested_crop[0] + requested_crop[2] > crop[0] + crop[2]
                or requested_crop[1] + requested_crop[3] > crop[1] + crop[3]):
            raise EvidenceError("capture crop geometry is outside its source or requested extent")
        expected_forward = (1.0, 0.0, -float(crop[0]), 0.0, 1.0, -float(crop[1]), 0.0, 0.0, 1.0)
        expected_inverse = (1.0, 0.0, float(crop[0]), 0.0, 1.0, float(crop[1]), 0.0, 0.0, 1.0)
        if source_to_panel != expected_forward or panel_to_source != expected_inverse:
            raise EvidenceError("capture matrices do not encode the exact integer crop offsets")
        if (report_panel.get("status") != "seed-completed" or report_panel.get("image_sha256") != panel_sha
                or report_panel.get("width") != width or report_panel.get("height") != height
                or report_panel.get("source_image_sha256") != source_sha
                or report_panel.get("source_width") != source_width
                or report_panel.get("source_height") != source_height
                or tuple(crop) != report_crop or crop[2:] != (width, height)
                or diagnostic.get("unmasked_input_sha256") != gray
                or panel.get("bgr_identity_kind") != "reconstructed_from_authenticated_panel_png"):
            raise EvidenceError("capture panel differs from its runtime report")
        png = _object(panel.get("panel_png"), "capture panel PNG")
        report_png = _object(report_panel.get("panel_png"), "runtime panel PNG")
        _, encoded = _read_exact(root, png.get("path"), png.get("sha256"), "capture panel PNG",
                                 png.get("byte_count"))
        if (png.get("sha256") != report_png.get("sha256")
                or png.get("byte_count") != report_png.get("byte_count")
                or png.get("sha256") != panel_sha):
            raise EvidenceError("capture panel PNG differs from runtime evidence")
        try:
            decoded_gray, decoded_bgr = production_head_inputs._decode_production_pixels(
                encoded, width, height)
        except Exception as exception:
            raise EvidenceError("capture panel PNG cannot be decoded by the production pixel contract") from exception
        if (sha256(decoded_gray.tobytes(order="C")).hexdigest() != gray
                or sha256(decoded_bgr.tobytes(order="C")).hexdigest() != bgr):
            raise EvidenceError("capture Gray8 or reconstructed BGR24 bytes changed")
        result[panel_id] = _PanelIdentity(
            split, source_sha, source_width, source_height, panel_id, panel_sha, width, height,
            crop, requested_crop, source_to_panel, panel_to_source, gray, bgr)
    if set(result) != report_panel_ids:
        raise EvidenceError("capture request does not bind the exact runtime panel inventory")
    for split, expected in EXPECTED_COUNTS.items():
        if sum(item.split == split for item in result.values()) != expected["panels"]:
            raise EvidenceError(f"{split} panel denominator changed")
    return result


def _prevalidate_capture_report(root: Path, report_path: Path, expected_sha256: str,
                                request_path: Path, request_sha256: str,
                                request: Mapping[str, Any], panels: Mapping[str, _PanelIdentity]) -> None:
    _, payload = _read_exact(root, str(report_path), expected_sha256, "tensor capture report")
    report = _json(payload, "tensor capture report")
    expected_header = {
        "binding_sha256": _object(request.get("binding"), "request binding").get("sha256"),
        "candidate_sha256": _object(request.get("candidate"), "request candidate").get("sha256"),
        "detector_model_sha256": _object(request.get("detector"), "request detector").get("model_sha256"),
        "detector_manifest_sha256": _object(request.get("detector"), "request detector").get("manifest_sha256"),
        "native_sha256": _object(request.get("native"), "request native").get("sha256"),
        "capture_source_sha256": _object(request.get("capture_source"), "request capture source").get("sha256"),
        "maximum_side_length": production_head_inputs.MAXIMUM_SIDE_LENGTH,
        "dimension_multiple": production_head_inputs.DIMENSION_MULTIPLE,
        "detector_configuration_fingerprint": production_head_inputs.DETECTOR_CONFIGURATION_FINGERPRINT,
    }
    if (report.get("schema") != CAPTURE_REPORT_SCHEMA
            or report.get("scope") != production_head_inputs.CAPTURE_SCOPE
            or report.get("synthetic_only") is not True or report.get("private_data") is not False
            or report.get("sealed_data") is not False or report.get("truth_used_by_capture") is not False
            or report.get("model_inference") is not False or report.get("production_approved") is not False
            or report.get("training_input_ready") is not False
            or report.get("panel_count") != len(panels) or report.get("failed_panel_count") != 0
            or any(report.get(key) != value for key, value in expected_header.items())):
        raise EvidenceError("tensor capture report scope or completeness changed")
    descriptor = _object(report.get("request"), "tensor capture request descriptor")
    if (_inside(root, descriptor.get("path"), "tensor capture request") != request_path
            or descriptor.get("sha256") != request_sha256):
        raise EvidenceError("tensor capture report request binding changed")
    records = _array(report.get("panels"), "tensor capture panels")
    by_id: dict[str, Mapping[str, Any]] = {}
    for raw in records:
        record = _object(raw, "tensor capture panel")
        panel_id = record.get("panel_id")
        if not isinstance(panel_id, str) or panel_id in by_id:
            raise EvidenceError("tensor capture panel identity is missing or duplicated")
        by_id[panel_id] = record
    if set(by_id) != set(panels):
        raise EvidenceError("tensor capture panel inventory changed")
    for panel_id, expected in panels.items():
        record = by_id[panel_id]
        if (record.get("split") != expected.split or record.get("source_sha256") != expected.source_sha256
                or record.get("panel_sha256") != expected.panel_sha256
                or record.get("width") != expected.width or record.get("height") != expected.height
                or tuple(_array(record.get("crop"), "capture report crop")) != expected.crop
                or record.get("recorded_unmasked_gray_sha256") != expected.gray_sha256
                or record.get("reconstructed_bgr_sha256") != expected.bgr_sha256
                or record.get("bgr_identity_kind") != "reconstructed_from_authenticated_panel_png"
                or record.get("detector_configuration_fingerprint")
                != production_head_inputs.DETECTOR_CONFIGURATION_FINGERPRINT):
            raise EvidenceError("tensor capture panel differs from its authenticated request")
        tensor = _object(record.get("tensor"), "captured tensor")
        shape = _array(tensor.get("shape"), "captured tensor shape")
        if (len(shape) != 4 or any(type(item) is not int or item <= 0 for item in shape)
                or tensor.get("dtype") != "float32-le"):
            raise EvidenceError("captured tensor metadata is invalid")
        tensor_path = _inside(root, report_path.parent / str(tensor.get("file")), "captured tensor")
        tensor_payload = tensor_path.read_bytes() if tensor_path.is_file() else b""
        if (sha256(tensor_payload).hexdigest() != _sha(tensor.get("sha256"), "captured tensor")
                or tensor.get("byte_count") != len(tensor_payload)
                or len(tensor_payload) != math.prod(shape) * 4):
            raise EvidenceError("captured tensor bytes changed")
    if report.get("assemblies") != request.get("assemblies"):
        raise EvidenceError("tensor capture execution assemblies changed")


def _polygon(value: Any, label: str, width: int, height: int) -> tuple[tuple[float, float], ...]:
    polygon = _object(value, label)
    points = _array(polygon.get("points"), f"{label} points")
    if len(points) < 3:
        raise EvidenceError(f"{label} has fewer than three points")
    parsed = tuple(_object(point, f"{label} point") for point in points)
    if any(set(point) != {"x", "y", "is_finite"} or point.get("is_finite") is not True
           for point in parsed):
        raise EvidenceError(f"{label} point metadata is invalid")
    result = tuple((_finite(point.get("x"), f"{label} x"),
                    _finite(point.get("y"), f"{label} y")) for point in parsed)
    if any(x < -1e-6 or y < -1e-6 or x > width + 1e-6 or y > height + 1e-6
           for x, y in result):
        raise EvidenceError(f"{label} lies outside its coordinate frame")
    if max(x for x, _ in result) <= min(x for x, _ in result) or max(y for _, y in result) <= min(y for _, y in result):
        raise EvidenceError(f"{label} is empty")
    return result


def _mapped(points: tuple[tuple[float, float], ...], matrix: tuple[float, ...]) -> tuple[tuple[float, float], ...]:
    output = []
    for x, y in points:
        denominator = matrix[6] * x + matrix[7] * y + matrix[8]
        if not math.isfinite(denominator) or abs(denominator) < 1e-12:
            raise EvidenceError("panel-to-source transform is singular")
        output.append(((matrix[0] * x + matrix[1] * y + matrix[2]) / denominator,
                       (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator))
    return tuple(output)


def _same_points(left: tuple[tuple[float, float], ...], right: tuple[tuple[float, float], ...]) -> bool:
    return len(left) == len(right) and all(abs(a - c) <= 1e-9 and abs(b - d) <= 1e-9
                                           for (a, b), (c, d) in zip(left, right))


def _prediction(points: tuple[tuple[float, float], ...]) -> _Prediction:
    return _Prediction(Box(min(x for x, _ in points), min(y for _, y in points),
                           max(x for x, _ in points), max(y for _, y in points)))


def _validate_evaluation(root: Path, report_path: Path, expected_sha256: str,
                         request_path: Path, request_sha256: str,
                         candidate_path: Path, candidate_sha256: str,
                         panels: Mapping[str, _PanelIdentity],
                         candidate: Mapping[str, Any]) -> _ValidatedEvidence:
    _, payload = _read_exact(root, str(report_path), expected_sha256, "candidate evaluation report")
    report = _json(payload, "candidate evaluation report")
    if (report.get("schema") != EVALUATION_SCHEMA or report.get("scope") != CANDIDATE_SCOPE
            or report.get("synthetic_only") is not True or report.get("private_data") is not False
            or report.get("sealed_data") is not False or report.get("truth_used_by_runtime") is not False
            or report.get("optimizer_steps") != 0 or report.get("production_approved") is not False
            or report.get("training_input_ready") is not False or report.get("model_inference") is not True
            or report.get("input_mode") != "production_decoded_original_bgr_db"
            or report.get("detector_postprocess") != "manifest_bound_unchanged_db_postprocess"
            or report.get("maximum_logical_detector_requests_per_panel") != 2
            or report.get("detector_requests_share_exact_runtime_input_and_stage_cache_key") is not True
            or report.get("graph_structure_consensus_applied") is not False
            or report.get("axis_mask_applied_to_detector") is not False
            or report.get("axis_bounds_used_for_role_classification") is not True):
        raise EvidenceError("candidate evaluation scope or isolated behavior is invalid")
    request_descriptor = _object(report.get("request"), "evaluation request")
    if (_inside(root, request_descriptor.get("path"), "evaluation request") != request_path
            or request_descriptor.get("sha256") != request_sha256):
        raise EvidenceError("candidate evaluation request binding changed")
    candidate_descriptor = _object(report.get("candidate"), "evaluation candidate")
    if set(candidate_descriptor) != {
        "path", "sha256", "composition_version", "adapter_id", "configuration_scope",
        "detector", "recognizer", "native_sha256",
    }:
        raise EvidenceError("candidate evaluation descriptor has unknown or missing fields")
    detector = _object(candidate.get("detector"), "candidate detector")
    recognizer = _object(candidate.get("recognizer"), "candidate recognizer")
    expected_detector = {
        "task": "ocr_detection", "model_id": detector.get("model_id"),
        "version": detector.get("model_version"), "sha256": detector.get("model_sha256"),
        "manifest_path": _inside(root, detector.get("manifest_path"), "detector manifest")
            .relative_to(root).as_posix(),
        "manifest_sha256": detector.get("manifest_sha256"),
    }
    expected_recognizer = {
        "task": "ocr_recognition", "model_id": recognizer.get("model_id"),
        "version": recognizer.get("model_version"), "sha256": recognizer.get("model_sha256"),
        "manifest_path": _inside(root, recognizer.get("manifest_path"), "recognizer manifest")
            .relative_to(root).as_posix(),
        "manifest_sha256": recognizer.get("manifest_sha256"),
    }
    expected_adapter = (
        f"graphreader-ocr:{COMPOSITION_VERSION}:"
        f"{str(detector.get('model_sha256'))[:12]}:{str(recognizer.get('model_sha256'))[:12]}:"
        f"{str(candidate.get('native_sha256'))[:12]}"
    )
    if (_inside(root, candidate_descriptor.get("path"), "evaluation candidate") != candidate_path
            or candidate_descriptor.get("sha256") != candidate_sha256
            or candidate_descriptor.get("composition_version") != COMPOSITION_VERSION
            or candidate_descriptor.get("adapter_id") != expected_adapter
            or candidate_descriptor.get("configuration_scope") != "unapproved_frozen_candidate"
            or candidate_descriptor.get("native_sha256") != candidate.get("native_sha256")
            or candidate_descriptor.get("detector") != expected_detector
            or candidate_descriptor.get("recognizer") != expected_recognizer):
        raise EvidenceError("candidate evaluation identity changed")
    evaluation_assemblies = _validate_assemblies(root, report.get("execution_assemblies"),
                                                  "evaluation execution assemblies")
    candidate_assemblies = _validate_assemblies(root, candidate.get("execution_assemblies"),
                                                 "candidate execution assemblies")
    if evaluation_assemblies != candidate_assemblies:
        raise EvidenceError("evaluation report execution assemblies differ from candidate")
    records = _array(report.get("panels"), "evaluation panels")
    panel_count = _integer(report.get("panel_count"), "evaluation panel count")
    completed_count = _integer(report.get("completed_panel_count"), "completed panel count")
    failed_count = _integer(report.get("failed_panel_count"), "failed panel count")
    if (panel_count != len(panels) or len(records) != len(panels)
            or completed_count + failed_count != len(panels)
            or report.get("status") != ("panels_completed" if failed_count == 0 else "failed")):
        raise EvidenceError("candidate evaluation panel counts are inconsistent")
    raw_by_source: dict[str, list[_Prediction]] = {}
    recognized_by_source: dict[str, list[_Prediction]] = {}
    explicit = {"train": 0, "validation": 0}
    failed_raw = {"train": 0, "validation": 0}
    seen: set[str] = set()
    completed = failed = 0
    for raw_panel in records:
        record = _object(raw_panel, "evaluation panel")
        panel_id = record.get("panel_id")
        expected = panels.get(panel_id) if isinstance(panel_id, str) else None
        if expected is None or panel_id in seen:
            raise EvidenceError("evaluation panel identity is foreign or duplicated")
        seen.add(panel_id)
        if (record.get("split") != expected.split or record.get("source_sha256") != expected.source_sha256
                or record.get("source_width") != expected.source_width
                or record.get("source_height") != expected.source_height
                or record.get("panel_sha256") != expected.panel_sha256
                or record.get("width") != expected.width or record.get("height") != expected.height
                or _box(record.get("crop"), "evaluation crop") != expected.crop
                or _box(record.get("requested_crop"), "evaluation requested crop") != expected.requested_crop
                or _matrix(record.get("source_to_panel_matrix"), "evaluation source-to-panel") != expected.source_to_panel_matrix
                or _matrix(record.get("panel_to_source_matrix"), "evaluation panel-to-source") != expected.panel_to_source_matrix):
            raise EvidenceError("evaluation panel provenance differs from the capture request")
        status = record.get("status")
        if status not in {"completed", "failed"}:
            raise EvidenceError("evaluation panel status is invalid")
        if status == "completed":
            completed += 1
            if (record.get("original_gray_sha256") != expected.gray_sha256
                    or record.get("original_bgr_sha256") != expected.bgr_sha256):
                raise EvidenceError("evaluation decoded pixel identity changed")
        else:
            failed += 1
        raw_regions = _array(record.get("raw_detector_regions"), "raw detector regions")
        recognized_regions = _array(record.get("recognized_regions"), "recognized regions")
        raw_geometry: dict[str, tuple[tuple[float, float], ...]] = {}
        for raw_region in raw_regions:
            region = _object(raw_region, "raw detector region")
            region_id = region.get("region_id")
            if not isinstance(region_id, str) or not region_id or region_id in raw_geometry:
                raise EvidenceError("raw detector region identity is missing or duplicated")
            panel_points = _polygon(region.get("panel_polygon"), "raw panel polygon",
                                    expected.width, expected.height)
            source_points = _polygon(region.get("source_polygon"), "raw source polygon",
                                     expected.source_width, expected.source_height)
            if region.get("coordinate_space") != "source_original_pixels" or not _same_points(
                    _mapped(panel_points, expected.panel_to_source_matrix), source_points):
                raise EvidenceError("raw detector source geometry differs from the authenticated transform")
            raw_geometry[region_id] = source_points
            raw_by_source.setdefault(expected.source_sha256, []).append(_prediction(source_points))
        recognized_ids: set[str] = set()
        for raw_region in recognized_regions:
            region = _object(raw_region, "recognized region")
            region_id = region.get("region_id")
            if not isinstance(region_id, str) or region_id in recognized_ids or region_id not in raw_geometry:
                raise EvidenceError("recognized region identity is missing, duplicated, or absent from raw output")
            recognized_ids.add(region_id)
            panel_points = _polygon(region.get("panel_polygon"), "recognized panel polygon",
                                    expected.width, expected.height)
            source_points = _polygon(region.get("source_polygon"), "recognized source polygon",
                                     expected.source_width, expected.source_height)
            if (not _same_points(panel_points, _polygon(
                    next(item for item in raw_regions if _object(item, "raw region").get("region_id") == region_id)
                    .get("panel_polygon"), "raw comparison polygon", expected.width, expected.height))
                    or not _same_points(source_points, raw_geometry[region_id])
                    or region.get("coordinate_space") != "source_original_pixels"):
                raise EvidenceError("recognized geometry differs from its raw detector region")
            recognized_by_source.setdefault(expected.source_sha256, []).append(_prediction(source_points))
        if status == "completed":
            failure_records = _array(record.get("region_failures"), "recognition failures")
            failure_ids = []
            for item in failure_records:
                failure = _object(item, "recognition failure")
                failure_id = failure.get("region_id")
                if not isinstance(failure_id, str) or not failure_id:
                    raise EvidenceError("recognition failure lacks a region identity")
                failure_ids.append(failure_id)
            if len(failure_ids) != len(set(failure_ids)) or set(raw_geometry) != recognized_ids | set(failure_ids):
                raise EvidenceError("recognized regions and explicit failures do not preserve raw inventory")
            if recognized_ids & set(failure_ids):
                raise EvidenceError("a raw region is both recognized and failed")
            explicit[expected.split] += len(failure_ids)
        else:
            if recognized_regions:
                raise EvidenceError("failed panel unexpectedly retains recognized regions")
            failed_raw[expected.split] += len(raw_regions)
    if seen != set(panels) or completed != completed_count or failed != failed_count:
        raise EvidenceError("candidate evaluation did not retain every panel exactly once")
    return _ValidatedEvidence(
        report, candidate, panels,
        {key: tuple(value) for key, value in raw_by_source.items()},
        {key: tuple(value) for key, value in recognized_by_source.items()},
        explicit, failed_raw)


def _score_predictions(truths: Sequence[Any], predictions: Mapping[str, tuple[_Prediction, ...]]) -> dict[str, Any]:
    truths_by_source: dict[str, list[Box]] = {}
    for truth in truths:
        source_box = truth.source_box
        if isinstance(source_box, Box):
            box = source_box
        elif (isinstance(source_box, (tuple, list)) and len(source_box) == 4
              and all(not isinstance(value, bool) and isinstance(value, (int, float))
                      and math.isfinite(value) for value in source_box)):
            box = Box(*(float(value) for value in source_box))
        else:
            raise EvidenceError("source truth box has an unsupported representation")
        if box.right <= box.left or box.bottom <= box.top:
            raise EvidenceError("source truth box is empty")
        truths_by_source.setdefault(truth.source_sha256, []).append(box)
    source_ids = set(truths_by_source) | set(predictions)
    true_positives = false_positives = false_negatives = 0
    for source_sha in source_ids:
        source_truths = tuple(truths_by_source.get(source_sha, ()))
        source_predictions = tuple(predictions.get(source_sha, ()))
        matched = maximum_cardinality_matches(source_predictions, source_truths)
        true_positives += matched
        false_positives += len(source_predictions) - matched
        false_negatives += len(source_truths) - matched
    return {
        "truth_region_count": sum(len(value) for value in truths_by_source.values()),
        "predicted_region_count": sum(len(value) for value in predictions.values()),
        "true_positives": true_positives,
        "false_positives": false_positives,
        "false_negatives": false_negatives,
        "precision": true_positives / max(1, true_positives + false_positives),
        "recall": true_positives / max(1, true_positives + false_negatives),
        "intersection_over_union_minimum": MATCH_IOU_MINIMUM,
    }


def _load_acceptance_bar(root: Path) -> tuple[str, float, float]:
    _, payload = _read_exact(root, ACCEPTANCE_BARS_PATH.as_posix(),
                             EXPECTED_ACCEPTANCE_BARS_SHA256, "acceptance bars")
    bars = _json(payload, "acceptance bars")
    tier = _object(bars.get("tier1_reviewable_error"), "tier 1 acceptance bars")
    precision = _finite(tier.get("text_region_detection_precision_minimum"), "detection precision bar")
    recall = _finite(tier.get("text_region_detection_recall_minimum"), "detection recall bar")
    if precision != 0.95 or recall != 0.95:
        raise EvidenceError("canonical text-region detection bars changed")
    return EXPECTED_ACCEPTANCE_BARS_SHA256, precision, recall


def _validate_source_bindings(root: Path) -> tuple[dict[str, str], ...]:
    result = []
    for path, digest in SOURCE_BINDINGS.items():
        _read_exact(root, path, digest, f"scoring dependency {path}")
        result.append({"path": path, "sha256": digest})
    return tuple(result)


def _validate_before_truth(
    root: Path,
    binding_path: Path,
    binding_sha256: str,
    capture_report_path: Path,
    capture_report_sha256: str,
    request_path: Path,
    request_sha256: str,
    candidate_path: Path,
    candidate_sha256: str,
    evaluation_report_path: Path,
    evaluation_report_sha256: str,
) -> _ValidatedEvidence:
    _read_exact(root, str(binding_path), binding_sha256, "V3 binding")
    resolved_request, request_payload = _read_exact(root, str(request_path), request_sha256,
                                                    "capture request")
    request = _json(request_payload, "capture request")
    if (request.get("schema") != CAPTURE_REQUEST_SCHEMA
            or request.get("scope") != production_head_inputs.CAPTURE_SCOPE
            or request.get("synthetic_only") is not True or request.get("private_data") is not False
            or request.get("sealed_data") is not False or request.get("truth_included") is not False
            or request.get("model_inference") is not False or request.get("production_approved") is not False
            or request.get("binding") != {"path": binding_path.resolve().relative_to(root).as_posix(),
                                          "sha256": binding_sha256}):
        raise EvidenceError("capture request scope or binding changed")
    _descriptor(root, request.get("capture_source"), "capture source")
    _validate_assemblies(root, request.get("assemblies"), "capture assemblies")
    _descriptor(root, request.get("candidate"), "capture baseline candidate")
    detector = _object(request.get("detector"), "capture detector")
    if (detector.get("model_id") != "PP-OCRv5_mobile_det"
            or detector.get("model_version") != "5.0.0"
            or detector.get("model_sha256") != PINNED_CAPTURE_MODEL_SHA256):
        raise EvidenceError("capture request detector is not the pinned official model")
    _validate_capture_detector_model(root, detector.get("model_path"),
                                     detector.get("model_sha256"))
    _read_exact(root, detector.get("manifest_path"), detector.get("manifest_sha256"),
                "capture detector manifest")
    native = _object(request.get("native"), "capture native")
    _read_historical_capture_artifact(
        root, native.get("path"), native.get("sha256"), "capture native")
    if (request.get("maximum_side_length") != production_head_inputs.MAXIMUM_SIDE_LENGTH
            or request.get("dimension_multiple") != production_head_inputs.DIMENSION_MULTIPLE
            or request.get("detector_configuration_fingerprint")
            != production_head_inputs.DETECTOR_CONFIGURATION_FINGERPRINT):
        raise EvidenceError("capture request detector preprocessing identity changed")
    for license_record in _array(request.get("license_inputs"), "capture license inputs"):
        record = _object(license_record, "capture license input")
        _read_historical_capture_artifact(
            root, record.get("path"), record.get("sha256"), "capture license input")
    panels = _runtime_panels(root, request)
    candidate, _ = _validate_candidate(root, candidate_path, candidate_sha256)
    _prevalidate_capture_report(root, capture_report_path, capture_report_sha256,
                                resolved_request, request_sha256, request, panels)
    return _validate_evaluation(root, evaluation_report_path, evaluation_report_sha256,
                                resolved_request, request_sha256, candidate_path.resolve(),
                                candidate_sha256, panels, candidate)


def score(
    binding_path: Path,
    binding_sha256: str,
    capture_report_path: Path,
    capture_report_sha256: str,
    request_path: Path,
    request_sha256: str,
    candidate_path: Path,
    candidate_sha256: str,
    evaluation_report_path: Path,
    evaluation_report_sha256: str,
    output_path: Path,
    *,
    evaluator_sha256: str,
    repository_root: Path = REPOSITORY_ROOT,
) -> Mapping[str, Any]:
    started = time.perf_counter()
    root = repository_root.resolve()
    evaluator = _sha(evaluator_sha256, "evaluator SHA-256")
    if sha256(Path(__file__).read_bytes()).hexdigest() != evaluator:
        raise EvidenceError("current evaluator bytes differ from the reviewed identity")
    output = output_path.resolve()
    artifacts = (root / "artifacts").resolve()
    if output == artifacts or artifacts not in output.parents or output.exists():
        raise EvidenceError("score output must be a new file under repository artifacts")
    sources = _validate_source_bindings(root)
    bar_sha, precision_bar, recall_bar = _load_acceptance_bar(root)
    evidence = _validate_before_truth(
        root, binding_path.resolve(), _sha(binding_sha256, "V3 binding"),
        capture_report_path.resolve(), _sha(capture_report_sha256, "capture report"),
        request_path.resolve(), _sha(request_sha256, "capture request"),
        candidate_path.resolve(), _sha(candidate_sha256, "candidate"),
        evaluation_report_path.resolve(), _sha(evaluation_report_sha256, "evaluation report"))

    # This is the first operation allowed to regenerate project-owned truth.
    inputs = production_head_inputs.load_production_head_inputs(
        binding_path, binding_sha256, capture_report_path, capture_report_sha256,
        repository_root=root)
    if inputs.binding_sha256 != binding_sha256 or inputs.capture_report_sha256 != capture_report_sha256:
        raise EvidenceError("production-head loader returned a different evidence identity")
    splits = {"train": inputs.train, "validation": inputs.dev}
    raw_metrics: dict[str, Any] = {}
    recognized_metrics: dict[str, Any] = {}
    failure_counts: dict[str, Any] = {}
    for split, split_inputs in splits.items():
        expected = EXPECTED_COUNTS[split]
        if (split_inputs.source_count != expected["sources"]
                or split_inputs.panel_count != expected["panels"]
                or split_inputs.full_source_truth_count != expected["truths"]
                or len(split_inputs.source_truths) != expected["truths"]):
            raise EvidenceError(f"{split} production-head denominator changed")
        allowed_sources = {truth.source_sha256 for truth in split_inputs.source_truths}
        panel_sources = {panel.source_sha256 for panel in evidence.panels.values() if panel.split == split}
        if allowed_sources != panel_sources:
            raise EvidenceError(f"{split} truth and runtime source inventories differ")
        raw = {key: value for key, value in evidence.raw_by_source.items() if key in allowed_sources}
        recognized = {key: value for key, value in evidence.recognized_by_source.items()
                      if key in allowed_sources}
        raw_metrics[split] = _score_predictions(split_inputs.source_truths, raw)
        recognized_metrics[split] = _score_predictions(split_inputs.source_truths, recognized)
        failure_counts[split] = {
            "failed_panel_count": sum(panel.split == split for panel_id, panel in evidence.panels.items()
                                      if next(item for item in evidence.report["panels"]
                                              if item["panel_id"] == panel_id)["status"] == "failed"),
            "explicit_region_failure_count": evidence.explicit_region_failures[split],
            "raw_regions_on_failed_panels": evidence.failed_panel_raw_regions[split],
            "raw_regions_without_successful_recognition": (
                raw_metrics[split]["predicted_region_count"]
                - recognized_metrics[split]["predicted_region_count"]),
        }
    elapsed = (time.perf_counter() - started) * 1000.0
    result: dict[str, Any] = {
        "schema": OUTPUT_SCHEMA,
        "status": "diagnostic_only_unapproved",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "truth_isolation": {
            "runtime_received_truth": False,
            "all_runtime_and_candidate_evidence_authenticated_before_truth_regeneration": True,
            "truth_regenerated_only_in_python_evaluator": True,
        },
        "inputs": {
            "binding": {"path": binding_path.resolve().relative_to(root).as_posix(),
                        "sha256": binding_sha256},
            "capture_request": {"path": request_path.resolve().relative_to(root).as_posix(),
                                "sha256": request_sha256},
            "capture_report": {"path": capture_report_path.resolve().relative_to(root).as_posix(),
                               "sha256": capture_report_sha256},
            "candidate": {"path": candidate_path.resolve().relative_to(root).as_posix(),
                          "sha256": candidate_sha256},
            "evaluation_report": {"path": evaluation_report_path.resolve().relative_to(root).as_posix(),
                                  "sha256": evaluation_report_sha256},
            "evaluator_sha256": evaluator,
            "scoring_dependencies": sources,
            "execution_assemblies": evidence.report["execution_assemblies"],
        },
        "denominators": {
            split: {"source_count": EXPECTED_COUNTS[split]["sources"],
                    "panel_count": EXPECTED_COUNTS[split]["panels"],
                    "full_source_truth_count": EXPECTED_COUNTS[split]["truths"]}
            for split in ("train", "validation")
        },
        "raw_detector_geometry": raw_metrics,
        "successfully_recognized_region_geometry": recognized_metrics,
        "recognition_failures": failure_counts,
        "acceptance_bar_reference": {
            "path": ACCEPTANCE_BARS_PATH.as_posix(), "sha256": bar_sha,
            "text_region_detection_precision_minimum": precision_bar,
            "text_region_detection_recall_minimum": recall_bar,
            "scorer_does_not_decide_candidate_or_production_approval": True,
        },
        "text_and_role_metrics": {
            "scored": False,
            "reason": "This evaluator grades detector and recognized-region geometry only; it makes no recognition exact-match, character-error, or role-accuracy claim.",
        },
        "integrity": {
            "source_count": 23, "panel_count": 37, "full_source_truth_count": 892,
            "failed_panels_remain_in_full_source_denominator": True,
            "recognition_failures_preserve_raw_detector_geometry": True,
            "panel_source_crop_gray_bgr_and_matrix_identities_verified": True,
            "evaluation_execution_assemblies_verified": True,
        },
        "elapsed_milliseconds": elapsed,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as stream:
        stream.write(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=True).encode("utf-8") + b"\n")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binding", type=Path, required=True)
    parser.add_argument("--binding-sha256", required=True)
    parser.add_argument("--capture-report", type=Path, required=True)
    parser.add_argument("--capture-report-sha256", required=True)
    parser.add_argument("--capture-request", type=Path, required=True)
    parser.add_argument("--capture-request-sha256", required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--candidate-sha256", required=True)
    parser.add_argument("--evaluation-report", type=Path, required=True)
    parser.add_argument("--evaluation-report-sha256", required=True)
    parser.add_argument("--evaluator-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        result = score(
            arguments.binding, arguments.binding_sha256,
            arguments.capture_report, arguments.capture_report_sha256,
            arguments.capture_request, arguments.capture_request_sha256,
            arguments.candidate, arguments.candidate_sha256,
            arguments.evaluation_report, arguments.evaluation_report_sha256,
            arguments.output, evaluator_sha256=arguments.evaluator_sha256)
    except (EvidenceError, OSError, ValueError) as exception:
        print(json.dumps({"status": "failed_precondition", "error": str(exception),
                          "production_approval": False}), file=sys.stderr)
        return 1
    print(json.dumps({"status": result["status"], "output": str(arguments.output),
                      "production_approval": False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
