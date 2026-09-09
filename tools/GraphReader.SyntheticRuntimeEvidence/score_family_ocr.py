# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score saved C# OCR regions against independently regenerated family truth.

This evaluator accepts only the annotation-free project-owned synthetic train
or validation exchange. It verifies every byte identity before renderer truth
is created, then applies the existing fixed IoU 0.5 maximum-cardinality matcher.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import sys
import time
from typing import Any, Mapping, Sequence

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from ml.markers.center.mask_preserving_v24.family_scenes import (
    _box,
    _family_identity,
    _records,
)
from ml.ocr.component_region_detector_v6.dataset import Box, Component
from ml.ocr.real_range_proposal_v34.pipeline import maximum_cardinality_matches
from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.renderer import render_scene


MANIFEST_SCHEMA = "graphreader.synthetic-runtime-raster-inputs.v1"
MANIFEST_SOURCE = "project-owned-synthetic-five-axis-family-v1"
REPORT_SCHEMA = "graphreader.synthetic-runtime-seed-evidence.v1"
REPORT_SCOPE = "local-synthetic-seed-diagnostic"
OUTPUT_SCHEMA = "graphreader.synthetic-runtime-family-ocr-diagnostic.v1"
MATCH_IOU_MINIMUM = 0.5

MANIFEST_KEYS = {
    "schema",
    "source",
    "preset",
    "seed",
    "split",
    "contains_truth",
    "contains_precomputed_masks",
    "images",
}
IMAGE_KEYS = {"image", "image_sha256", "width", "height", "split", "family", "seed"}


class EvidenceError(ValueError):
    """The saved evidence cannot be safely joined to regenerated truth."""


def _sha256_bytes(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _sha256_file(path: Path) -> str:
    return _sha256_bytes(path.read_bytes())


def _load_object(path: Path, label: str) -> tuple[dict[str, Any], bytes]:
    payload = path.read_bytes()
    try:
        value = json.loads(payload)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"{label} is not valid JSON: {exception}") from exception
    if not isinstance(value, dict):
        raise EvidenceError(f"{label} must be a JSON object")
    return value, payload


def _require_exact_keys(record: Mapping[str, Any], expected: set[str], label: str) -> None:
    actual = set(record)
    if actual != expected:
        raise EvidenceError(
            f"{label} keys differ: missing={sorted(expected - actual)}, "
            f"unexpected={sorted(actual - expected)}"
        )


def _require_sha256(value: Any, label: str) -> str:
    normalized = str(value).casefold()
    if len(normalized) != 64 or any(character not in "0123456789abcdef" for character in normalized):
        raise EvidenceError(f"{label} must be a lowercase or uppercase SHA-256 value")
    return normalized


def _require_int(value: Any, label: str, *, minimum: int | None = None) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise EvidenceError(f"{label} must be an integer")
    if minimum is not None and value < minimum:
        raise EvidenceError(f"{label} must be at least {minimum}")
    return value


def _require_artifact_path(path: Path, repository_root: Path, label: str) -> Path:
    resolved = path.resolve()
    artifact_root = (repository_root / "artifacts").resolve()
    if resolved == artifact_root or artifact_root not in resolved.parents:
        raise EvidenceError(f"{label} must stay under the repository artifacts directory")
    return resolved


def _validate_manifest(
    manifest: Mapping[str, Any],
    manifest_path: Path,
) -> tuple[str, int, list[dict[str, Any]]]:
    _require_exact_keys(manifest, MANIFEST_KEYS, "input manifest")
    split = str(manifest["split"])
    if (
        manifest["schema"] != MANIFEST_SCHEMA
        or manifest["source"] != MANIFEST_SOURCE
        or manifest["preset"] != "smoke"
        or split not in {"train", "validation"}
        or manifest["contains_truth"] is not False
        or manifest["contains_precomputed_masks"] is not False
    ):
        raise EvidenceError(
            "only annotation-free project-owned smoke train/validation inputs are accepted"
        )
    dataset_seed = _require_int(manifest["seed"], "input manifest seed")
    raw_images = manifest["images"]
    if not isinstance(raw_images, list) or not raw_images:
        raise EvidenceError("input manifest images must be a nonempty array")

    images: list[dict[str, Any]] = []
    names: set[str] = set()
    hashes: set[str] = set()
    seeds: set[int] = set()
    for index, raw in enumerate(raw_images):
        if not isinstance(raw, dict):
            raise EvidenceError(f"input manifest image {index} must be an object")
        _require_exact_keys(raw, IMAGE_KEYS, f"input manifest image {index}")
        name = str(raw["image"])
        path_name = Path(name)
        image_hash = _require_sha256(raw["image_sha256"], f"image {index} SHA-256")
        image_seed = _require_int(raw["seed"], f"image {index} seed")
        width = _require_int(raw["width"], f"image {index} width", minimum=1)
        height = _require_int(raw["height"], f"image {index} height", minimum=1)
        if path_name.name != name or path_name.suffix.casefold() != ".png":
            raise EvidenceError(f"image {index} must be a local PNG basename")
        if str(raw["split"]) != split:
            raise EvidenceError(f"image {index} split differs from the manifest split")
        if name.casefold() in names or image_hash in hashes or image_seed in seeds:
            raise EvidenceError("duplicate image name, hash, or seed in input manifest")
        names.add(name.casefold())
        hashes.add(image_hash)
        seeds.add(image_seed)
        image_path = manifest_path.parent / name
        if not image_path.is_file() or _sha256_file(image_path) != image_hash:
            raise EvidenceError(f"input image bytes do not match the manifest: {name}")
        images.append(
            {
                "image": name,
                "image_sha256": image_hash,
                "width": width,
                "height": height,
                "split": split,
                "family": str(raw["family"]),
                "seed": image_seed,
            }
        )
    return split, dataset_seed, images


def _validate_report(
    report: Mapping[str, Any],
    manifest_sha256: str,
    images: Sequence[Mapping[str, Any]],
) -> dict[str, Mapping[str, Any]]:
    if report.get("schema") != REPORT_SCHEMA or report.get("scope") != REPORT_SCOPE:
        raise EvidenceError("runtime report has a foreign schema or scope")
    if report.get("production_approved") is not False or report.get("training_input_ready") is not False:
        raise EvidenceError("runtime report must remain an unapproved local diagnostic")
    if _require_sha256(report.get("input_manifest_sha256"), "report input manifest SHA-256") != manifest_sha256:
        raise EvidenceError("runtime report does not bind the supplied input manifest bytes")

    raw_cases = report.get("cases")
    if not isinstance(raw_cases, list):
        raise EvidenceError("runtime report cases must be an array")
    case_map: dict[str, Mapping[str, Any]] = {}
    for index, raw in enumerate(raw_cases):
        if not isinstance(raw, dict):
            raise EvidenceError(f"runtime report case {index} must be an object")
        image_hash = _require_sha256(raw.get("image_sha256"), f"runtime case {index} image SHA-256")
        if image_hash in case_map:
            raise EvidenceError("runtime report contains duplicate case identities")
        if raw.get("status") not in {"seed-completed", "failed"}:
            raise EvidenceError(f"runtime case {index} has an unknown status")
        case_map[image_hash] = raw

    expected_hashes = {str(image["image_sha256"]) for image in images}
    actual_hashes = set(case_map)
    if actual_hashes != expected_hashes:
        raise EvidenceError(
            "runtime report case identities differ from the manifest: "
            f"missing={sorted(expected_hashes - actual_hashes)}, "
            f"foreign={sorted(actual_hashes - expected_hashes)}"
        )
    completed = sum(case.get("status") == "seed-completed" for case in case_map.values())
    failed = len(case_map) - completed
    if (
        _require_int(report.get("count"), "runtime report count", minimum=0) != len(images)
        or _require_int(report.get("completed"), "runtime report completed", minimum=0) != completed
        or _require_int(report.get("failed"), "runtime report failed", minimum=0) != failed
    ):
        raise EvidenceError("runtime report counts do not match its case identities and statuses")
    return case_map


def _regenerate(
    split: str,
    dataset_seed: int,
    images: Sequence[Mapping[str, Any]],
) -> dict[str, tuple[Mapping[str, Any], Mapping[str, Any]]]:
    scenes = _build_scenes(
        PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True
    )
    selected = [scene for scene in scenes if _scene_split(scene) == split]
    scenes_by_seed = {int(scene["seed"]): scene for scene in selected}
    expected_seeds = {int(image["seed"]) for image in images}
    if len(scenes_by_seed) != len(selected) or set(scenes_by_seed) != expected_seeds:
        raise EvidenceError("regenerated scene identities differ from the input manifest")

    regenerated: dict[str, tuple[Mapping[str, Any], Mapping[str, Any]]] = {}
    for image_record in images:
        scene = scenes_by_seed[int(image_record["seed"])]
        if _family_identity(scene) != image_record["family"]:
            raise EvidenceError(f"regenerated family differs for seed {image_record['seed']}")
        image, annotation, _ = render_scene(scene)
        rgb = image.convert("RGB")
        payload = BytesIO()
        rgb.save(payload, format="PNG")
        regenerated_hash = _sha256_bytes(payload.getvalue())
        expected_hash = str(image_record["image_sha256"])
        expected_name = f"{split}-{int(scene['seed'])}-{regenerated_hash[:12]}.png"
        if (
            regenerated_hash != expected_hash
            or expected_name != image_record["image"]
            or rgb.size != (image_record["width"], image_record["height"])
        ):
            raise EvidenceError(f"regenerated PNG identity differs for seed {scene['seed']}")
        regenerated[expected_hash] = (scene, annotation)
    return regenerated


def _truth_regions(annotation: Mapping[str, Any]) -> tuple[tuple[Box, ...], tuple[str, ...]]:
    boxes: list[Box] = []
    roles: list[str] = []
    for record in _records(annotation, "texts"):
        rendered_box = record.get("rendered_pixel_box")
        if (
            record.get("visible", True) is False
            or not str(record.get("text", "")).strip()
            or not isinstance(rendered_box, Sequence)
            or isinstance(rendered_box, (str, bytes))
        ):
            continue
        bounds = _box(record)
        if bounds is None:
            continue
        left, top, right, bottom = bounds
        if not all(math.isfinite(value) for value in bounds) or right <= left or bottom <= top:
            continue
        boxes.append(Box(left, top, right, bottom))
        roles.append(str(record.get("role", "other")))
    return tuple(boxes), tuple(roles)


def _prediction_regions(case: Mapping[str, Any], image: Mapping[str, Any]) -> tuple[Component, ...]:
    if case["status"] == "failed":
        return ()
    if case.get("width") != image["width"] or case.get("height") != image["height"]:
        raise EvidenceError("completed runtime case dimensions differ from the manifest")
    ocr = case.get("ocr")
    if not isinstance(ocr, dict):
        raise EvidenceError("completed runtime case is missing OCR output")
    if (
        ocr.get("contract_version") != 1
        or ocr.get("stage") != "ocr"
        or ocr.get("coordinate_space") != "original_pixels"
        or ocr.get("succeeded") is not True
        or ocr.get("failure") is not None
        or _require_sha256(ocr.get("input_sha256"), "OCR input SHA-256") != image["image_sha256"]
    ):
        raise EvidenceError("completed runtime OCR output has an invalid envelope or image identity")
    raw_regions = ocr.get("regions")
    if not isinstance(raw_regions, list):
        raise EvidenceError("completed runtime OCR regions must be an array")
    identifiers: set[str] = set()
    predictions: list[Component] = []
    for index, region in enumerate(raw_regions):
        if not isinstance(region, dict):
            raise EvidenceError(f"OCR region {index} must be an object")
        identifier = str(region.get("region_id", ""))
        if not identifier or identifier in identifiers:
            raise EvidenceError("OCR region identities must be present and unique within a case")
        identifiers.add(identifier)
        if region.get("coordinate_space") != "original_pixels":
            raise EvidenceError("OCR region is not in original pixel coordinates")
        polygon = region.get("polygon")
        bounds = polygon.get("bounds") if isinstance(polygon, dict) else None
        if not isinstance(bounds, dict):
            raise EvidenceError("OCR region is missing polygon bounds")
        left = float(bounds.get("left"))
        top = float(bounds.get("top"))
        right = float(bounds.get("right"))
        bottom = float(bounds.get("bottom"))
        coordinates = (left, top, right, bottom)
        if not all(math.isfinite(value) for value in coordinates) or right <= left or bottom <= top:
            raise EvidenceError("OCR region has nonpositive or nonfinite bounds")
        # Component stores inclusive right/bottom and exposes the half-open Box
        # consumed by the established matcher. Subtracting one preserves the
        # C# floating-point bounds exactly through Component.box.
        predictions.append(
            Component(
                left,
                top,
                right - 1.0,
                bottom - 1.0,
                (right - left) * (bottom - top),
                1,
            )
        )
    return tuple(predictions)


def _source_binding(repository_root: Path) -> list[dict[str, str]]:
    paths = (
        Path(__file__).resolve(),
        repository_root / "ml/synthetic/dataset.py",
        repository_root / "ml/synthetic/renderer.py",
        repository_root / "ml/markers/center/mask_preserving_v24/family_scenes.py",
        repository_root / "ml/ocr/component_region_detector_v6/dataset.py",
        repository_root / "ml/ocr/real_range_proposal_v34/pipeline.py",
    )
    return [
        {
            "path": path.relative_to(repository_root).as_posix(),
            "sha256": _sha256_file(path),
        }
        for path in paths
    ]


def score(manifest_path: Path, report_path: Path, output_path: Path) -> dict[str, Any]:
    started = time.perf_counter()
    repository_root = REPOSITORY_ROOT
    manifest_path = _require_artifact_path(manifest_path, repository_root, "input manifest")
    report_path = _require_artifact_path(report_path, repository_root, "runtime report")
    output_path = _require_artifact_path(output_path, repository_root, "output")
    if output_path.exists():
        raise EvidenceError("refusing to replace an existing diagnostic output")

    manifest, manifest_bytes = _load_object(manifest_path, "input manifest")
    report, report_bytes = _load_object(report_path, "runtime report")
    split, dataset_seed, images = _validate_manifest(manifest, manifest_path)
    manifest_hash = _sha256_bytes(manifest_bytes)
    cases = _validate_report(report, manifest_hash, images)

    # Truth is regenerated only after the annotation-free runtime exchange and
    # saved report have passed all source, split, case, and byte-identity gates.
    regenerated = _regenerate(split, dataset_seed, images)

    total_truth = total_predictions = total_matches = 0
    failed_cases = 0
    role_counts: defaultdict[str, list[int]] = defaultdict(lambda: [0, 0])
    for image in images:
        image_hash = str(image["image_sha256"])
        case = cases[image_hash]
        if case["status"] == "failed":
            failed_cases += 1
        _, annotation = regenerated[image_hash]
        truths, roles = _truth_regions(annotation)
        predictions = _prediction_regions(case, image)
        matched = maximum_cardinality_matches(predictions, truths)
        total_truth += len(truths)
        total_predictions += len(predictions)
        total_matches += matched
        for role in sorted(set(roles)):
            role_truths = tuple(box for box, current in zip(truths, roles) if current == role)
            role_matched = maximum_cardinality_matches(predictions, role_truths)
            role_counts[role][0] += len(role_truths)
            role_counts[role][1] += role_matched

    false_positives = total_predictions - total_matches
    false_negatives = total_truth - total_matches
    precision = total_matches / max(1, total_predictions)
    recall = total_matches / max(1, total_truth)

    bars_path = repository_root / "ml/policy/acceptance-bars.json"
    bars = json.loads(bars_path.read_text(encoding="utf-8"))["tier1_reviewable_error"]
    precision_bar = float(bars["text_region_detection_precision_minimum"])
    recall_bar = float(bars["text_region_detection_recall_minimum"])
    clears_reference_bar = precision >= precision_bar and recall >= recall_bar
    status = (
        "dev_diagnostic_clears_reference_bar_not_approval"
        if clears_reference_bar
        else "failed_dev_diagnostic"
    )
    failures: list[str] = []
    if precision < precision_bar:
        failures.append(
            f"detection precision {precision:.6f} is below the shared {precision_bar:.2f} reference bar"
        )
    if recall < recall_bar:
        failures.append(
            f"detection recall {recall:.6f} is below the shared {recall_bar:.2f} reference bar"
        )
    if failed_cases:
        failures.append(f"{failed_cases} runtime case(s) failed and were scored as zero predictions")

    case_identity_material = "\n".join(sorted(cases)).encode("ascii")
    result: dict[str, Any] = {
        "schema": OUTPUT_SCHEMA,
        "status": status,
        "scope": "local-synthetic-train-dev-diagnostic",
        "split": split,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "sealed_runs": 0,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "truth_isolation": {
            "runtime_input_contains_truth": False,
            "runtime_input_contains_precomputed_masks": False,
            "truth_created_only_in_python_evaluator_after_provenance_validation": True,
        },
        "input_manifest": {
            "path": manifest_path.relative_to(repository_root).as_posix(),
            "sha256": manifest_hash,
            "source": MANIFEST_SOURCE,
            "preset": "smoke",
            "dataset_seed": dataset_seed,
            "case_count": len(images),
            "case_identity_set_sha256": _sha256_bytes(case_identity_material),
            "all_disk_and_regenerated_png_hashes_verified": True,
        },
        "runtime_report": {
            "path": report_path.relative_to(repository_root).as_posix(),
            "sha256": _sha256_bytes(report_bytes),
            "input_manifest_sha256_verified": True,
            "case_image_sha256_verified": True,
            "reported_completed": report["completed"],
            "reported_failed": report["failed"],
            "failed_cases_scored_as_zero_predictions": failed_cases,
        },
        "evaluator": {
            "matching": "existing maximum-cardinality one-to-one matching",
            "intersection_over_union_minimum": MATCH_IOU_MINIMUM,
            "box_convention": "half-open Box via inclusive Component right/bottom plus one",
            "sources": _source_binding(repository_root),
        },
        "reference_acceptance_bar": {
            "path": bars_path.relative_to(repository_root).as_posix(),
            "sha256": _sha256_file(bars_path),
            "precision_minimum": precision_bar,
            "recall_minimum": recall_bar,
            "clears_reference_bar": clears_reference_bar,
            "use": "descriptive comparison only; this diagnostic cannot approve a model",
        },
        "detection_metrics": {
            "case_count": len(images),
            "truth_region_count": total_truth,
            "predicted_region_count": total_predictions,
            "true_positives": total_matches,
            "false_positives": false_positives,
            "false_negatives": false_negatives,
            "precision": precision,
            "recall": recall,
            "by_truth_role": {
                role: {
                    "truth_region_count": counts[0],
                    "matched_truth_count": counts[1],
                    "false_negative_count": counts[0] - counts[1],
                    "recall": counts[1] / max(1, counts[0]),
                }
                for role, counts in sorted(role_counts.items())
            },
        },
        "recognition_metrics": {
            "scored": False,
            "reason": "recognition is not claimed without persisted one-to-one matched pairs",
        },
        "failure_mode": failures or ["none observed against the descriptive detection reference bar"],
        "limitations": [
            "Synthetic runtime images are currently wrapped as one panel by the current axis path even when an image contains multiple rendered panels.",
            "This scores OCR region detection only and is not an end-to-end workflow, recognition, role, calibration, export, private-data, or production-approval result.",
            "The reference bar was read without selecting or changing a model, threshold, architecture, or weight.",
        ],
        "elapsed_milliseconds": round((time.perf_counter() - started) * 1000.0, 3),
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-manifest", type=Path, required=True)
    parser.add_argument("--runtime-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        result = score(arguments.input_manifest, arguments.runtime_report, arguments.output)
    except (EvidenceError, OSError, KeyError, TypeError, json.JSONDecodeError) as exception:
        print(json.dumps({"status": "rejected", "error": str(exception)}, sort_keys=True), file=sys.stderr)
        return 1
    print(
        json.dumps(
            {
                "status": result["status"],
                "precision": result["detection_metrics"]["precision"],
                "recall": result["detection_metrics"]["recall"],
                "production_approval": False,
                "output": str(arguments.output),
            },
            sort_keys=True,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
