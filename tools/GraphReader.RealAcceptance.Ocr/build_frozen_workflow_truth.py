# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Build offline CSV-evaluator truth for the fixed seven-source workflow run.

This tool authenticates the already-completed synthetic workflow evidence before
regenerating project-owned truth. It never initializes a model, reruns a
workflow, reads private or sealed data, or assigns production approval.
"""

from __future__ import annotations

import argparse
from hashlib import sha256
import json
import math
from pathlib import Path
import sys
from typing import Any, Iterable, Mapping, Sequence
from uuid import UUID


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
RUNTIME_TOOL_ROOT = REPOSITORY_ROOT / "tools/GraphReader.SyntheticRuntimeEvidence"
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))
if str(RUNTIME_TOOL_ROOT) not in sys.path:
    sys.path.insert(0, str(RUNTIME_TOOL_ROOT))

import score_family_ocr as family_ocr  # noqa: E402
import score_initial_contour_output as initial_scorer  # noqa: E402
from ml.synthetic.dataset import _csv_rows  # noqa: E402


EvidenceError = family_ocr.EvidenceError

REPORT_PATH = Path("artifacts/frozen-workflow-candidate-inputs/workflow-run-v2/report.json")
REPORT_SHA256 = "3f5e2a840252d98c3f1af6bd8d87ca9e79b64f57829f3b9b7b9dd7abf45c83ef"
CANDIDATE_PATH = Path(
    "artifacts/frozen-workflow-candidate-inputs/workflow-candidate-v2/candidate-binding.json"
)
CANDIDATE_SHA256 = "f6d87c77f7f436726f43906160488babca1457e0f769d564a0696da36738bf4e"
INPUT_PATH = Path(
    "artifacts/frozen-workflow-candidate-inputs/workflow-run-v2/frozen-inputs/input-manifest.json"
)
INPUT_SHA256 = "e06973c1ae2def6b774111c5d2673c2349e187c965a1ef5429ed3076498d29b5"
INPUT_CANDIDATE_COPY_PATH = Path(
    "artifacts/frozen-workflow-candidate-inputs/workflow-run-v2/frozen-inputs/candidate-binding.json"
)
PROTOCOL_PATH = Path("tools/GraphReader.RealAcceptance.Ocr/frozen_candidate_synthetic_protocol.json")
PROTOCOL_SHA256 = "56f75cdbba1a634d6bf5f0012ec2c48cec58f0e8fc47d5a8f886e6ef72ccd9af"
INITIAL_SCORER_PATH = Path(
    "tools/GraphReader.SyntheticRuntimeEvidence/score_initial_contour_output.py"
)
INITIAL_SCORER_SHA256 = "b8c705659c7816d333ca03618461bba82f2c0fb45098e682d86360aabff48502"
TRAIN_MANIFEST_PATH = Path(
    "artifacts/synthetic-runtime-evidence/inputs-train393-layout2/input-manifest.json"
)
TRAIN_MANIFEST_SHA256 = "3a76a405d8e8eb2512988aba877a6d3cecb9975494c36e403344a197d84d0d37"
DEV_MANIFEST_PATH = Path(
    "artifacts/synthetic-runtime-evidence/inputs-dev393-layout2/input-manifest.json"
)
DEV_MANIFEST_SHA256 = "197ac4eda3157f0883b80bb135e13ed41165eac23f0d758309a4823125b865b7"
OUTPUT_ROOT = Path("artifacts/frozen-workflow-candidate-inputs/csv-evaluation")
OUTPUT_SCHEMA = "graphreader.frozen-workflow-csv-evaluation-input.v1"
EXPECTED_SOURCES = 7
EXPECTED_PANELS = 13
EXPECTED_TRUTH_POINTS = 306

CLASSIFIER_FILES = {
    "model_sha256": Path(
        "artifacts/frozen-workflow-candidate-inputs/static-v1/classifier-store/runtime/"
        "graph-marker-classifier/0.1.0/marker-classifier-probability-packed.onnx"
    ),
    "manifest_sha256": Path(
        "artifacts/frozen-workflow-candidate-inputs/static-v1/classifier-store/manifest/"
        "graph-marker-classifier/0.1.0/manifest.json"
    ),
    "notice_sha256": Path(
        "artifacts/frozen-workflow-candidate-inputs/static-v1/classifier-store/notices/"
        "graph-marker-classifier/0.1.0/MARKER_CLASSIFIER_MODEL_NOTICE.md"
    ),
    "benchmark_sha256": Path(
        "artifacts/frozen-workflow-candidate-inputs/static-v1/classifier-store/evidence/"
        "graph-marker-classifier/0.1.0/marker-classifier-production-approval.json"
    ),
    "package_index_sha256": Path(
        "artifacts/frozen-workflow-candidate-inputs/static-v1/classifier-store/production-model-index.json"
    ),
}


def _repository_path(relative: Path, label: str) -> Path:
    if relative.is_absolute():
        raise EvidenceError(f"{label} must be repository-relative")
    root = REPOSITORY_ROOT.resolve()
    path = (root / relative).resolve()
    if path == root or root not in path.parents:
        raise EvidenceError(f"{label} escaped the repository")
    return path


def _sha256_bytes(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _exact_bytes(relative: Path, expected_sha256: str, label: str) -> bytes:
    path = _repository_path(relative, label)
    if not path.is_file():
        raise EvidenceError(f"{label} is missing")
    payload = path.read_bytes()
    if _sha256_bytes(payload) != family_ocr._require_sha256(expected_sha256, f"{label} SHA-256"):
        raise EvidenceError(f"{label} bytes differ from the fixed identity")
    return payload


def _load_exact_object(relative: Path, expected_sha256: str, label: str) -> dict[str, Any]:
    payload = _exact_bytes(relative, expected_sha256, label)
    try:
        value = json.loads(payload)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"{label} is invalid JSON: {exception}") from exception
    if not isinstance(value, dict):
        raise EvidenceError(f"{label} must be an object")
    return value


def _validate_program_identity(expected_builder_sha256: str) -> str:
    expected = family_ocr._require_sha256(
        expected_builder_sha256, "reviewed truth builder SHA-256")
    actual = _sha256_bytes(Path(__file__).resolve().read_bytes())
    if actual != expected:
        raise EvidenceError("truth builder bytes differ from the reviewed identity")
    _exact_bytes(INITIAL_SCORER_PATH, INITIAL_SCORER_SHA256, "truth-environment guard source")
    return actual


def _require_mapping(value: Any, label: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise EvidenceError(f"{label} must be an object")
    return value


def _require_list(value: Any, label: str) -> list[Any]:
    if not isinstance(value, list):
        raise EvidenceError(f"{label} must be an array")
    return value


def _require_int(value: Any, label: str, *, minimum: int = 0) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise EvidenceError(f"{label} must be an integer of at least {minimum}")
    return value


def _finite(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        raise EvidenceError(f"{label} must be finite")
    return float(value)


def _canonical_uuid(value: Any, label: str) -> str:
    if not isinstance(value, str):
        raise EvidenceError(f"{label} must be a canonical UUID")
    try:
        canonical = str(UUID(value))
    except ValueError as exception:
        raise EvidenceError(f"{label} must be a canonical UUID") from exception
    if value != canonical:
        raise EvidenceError(f"{label} must be a canonical UUID")
    return value


def _validate_file_reference(record: Mapping[str, Any], label: str) -> None:
    relative = record.get("file")
    expected = record.get("sha256")
    if not isinstance(relative, str) or Path(relative).as_posix() != relative:
        raise EvidenceError(f"{label} file must be a canonical repository-relative path")
    _exact_bytes(Path(relative), str(expected), label)


def _walk_file_references(value: Any, label: str = "candidate") -> Iterable[tuple[Mapping[str, Any], str]]:
    if isinstance(value, dict):
        if "file" in value or "sha256" in value:
            if "file" in value and "sha256" in value:
                yield value, label
        for key, child in value.items():
            yield from _walk_file_references(child, f"{label}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from _walk_file_references(child, f"{label}[{index}]")


def _validate_candidate(candidate: Mapping[str, Any], report: Mapping[str, Any], protocol: Mapping[str, Any]) -> None:
    if (
        candidate.get("schema") != "graphreader.real-acceptance-frozen-candidate.v1"
        or candidate.get("candidate_id") != report.get("candidate_id")
        or candidate.get("revision") != report.get("revision")
        or candidate.get("protocol") != {
            "file": PROTOCOL_PATH.as_posix(), "sha256": PROTOCOL_SHA256,
        }
    ):
        raise EvidenceError("candidate identity differs from the workflow report or protocol")
    runtime = _require_mapping(candidate.get("runtime"), "candidate runtime")
    protocol_runtime = _require_mapping(protocol.get("runtime"), "protocol runtime")
    expected_runtime = {
        "execution_provider": "cpu", "graph_optimization": "disabled",
        "intra_operation_threads": 1, "inter_operation_threads": 1,
        "queue_capacity": 1, "worker_count": 1,
    }
    protocol_runtime_values = {
        "execution_provider": protocol_runtime.get("provider"),
        "graph_optimization": protocol_runtime.get("graph_optimization"),
        "intra_operation_threads": protocol_runtime.get("intra_operation_threads"),
        "inter_operation_threads": protocol_runtime.get("inter_operation_threads"),
        "queue_capacity": protocol_runtime.get("queue_capacity"),
        "worker_count": protocol_runtime.get("worker_count"),
    }
    if runtime != expected_runtime or protocol_runtime_values != expected_runtime:
        raise EvidenceError("candidate runtime differs from the frozen protocol")
    fixed_models = _require_mapping(protocol.get("fixed_models"), "protocol fixed models")
    if (
        _require_mapping(candidate.get("ocr_detection"), "candidate OCR detector").get("payload", {}).get("sha256")
        != fixed_models.get("ocr_detection_sha256")
        or _require_mapping(candidate.get("ocr_recognition"), "candidate OCR recognizer").get("payload", {}).get("sha256")
        != fixed_models.get("ocr_recognition_sha256")
        or _require_mapping(candidate.get("marker_center"), "candidate marker center").get("payload", {}).get("sha256")
        != fixed_models.get("marker_center_sha256")
        or _require_mapping(candidate.get("marker_classifier"), "candidate classifier").get("model_sha256")
        != fixed_models.get("marker_classifier_sha256")
    ):
        raise EvidenceError("candidate model payload identities differ from the protocol")
    references = list(_walk_file_references(candidate))
    if not references:
        raise EvidenceError("candidate contains no bound file references")
    for record, label in references:
        _validate_file_reference(record, label)
    classifier = _require_mapping(candidate.get("marker_classifier"), "candidate classifier")
    for field, relative in CLASSIFIER_FILES.items():
        _exact_bytes(relative, str(classifier.get(field)), f"candidate classifier {field}")


def _load_source_manifest(relative: Path, expected_sha256: str) -> tuple[str, int, list[dict[str, Any]]]:
    document = _load_exact_object(relative, expected_sha256, f"source manifest {relative.name}")
    return family_ocr._validate_manifest(document, _repository_path(relative, "source manifest"))


def _validate_artifact(report_root: Path, raw: Mapping[str, Any], label: str) -> dict[str, Any]:
    family_ocr._require_exact_keys(raw, {"file", "sha256", "row_count", "path"}, label)
    file_name = raw.get("file")
    relative = raw.get("path")
    if (
        not isinstance(file_name, str) or Path(file_name).name != file_name
        or not isinstance(relative, str) or Path(relative).is_absolute()
        or Path(relative).as_posix() != relative
        or any(part in {"", ".", ".."} for part in Path(relative).parts)
    ):
        raise EvidenceError(f"{label} path is invalid")
    path = (report_root / relative).resolve()
    if report_root.resolve() not in path.parents or path.name != file_name or not path.is_file():
        raise EvidenceError(f"{label} is missing or outside its report")
    payload = path.read_bytes()
    expected = family_ocr._require_sha256(raw.get("sha256"), f"{label} SHA-256")
    if _sha256_bytes(payload) != expected:
        raise EvidenceError(f"{label} bytes differ from the report")
    row_count = _require_int(raw.get("row_count"), f"{label} row count")
    if path.suffix.casefold() == ".csv":
        try:
            line_count = len(payload.decode("utf-8-sig").splitlines())
        except UnicodeDecodeError as exception:
            raise EvidenceError(f"{label} is not UTF-8 CSV") from exception
        if max(0, line_count - 1) != row_count:
            raise EvidenceError(f"{label} row count differs from its CSV bytes")
    return {
        "file_name": file_name,
        "sha256": expected,
        "row_count": row_count,
        "written_path": path.relative_to(REPOSITORY_ROOT).as_posix(),
    }


def _validate_evidence() -> dict[str, Any]:
    report = _load_exact_object(REPORT_PATH, REPORT_SHA256, "workflow report")
    protocol = _load_exact_object(PROTOCOL_PATH, PROTOCOL_SHA256, "workflow protocol")
    candidate = _load_exact_object(CANDIDATE_PATH, CANDIDATE_SHA256, "candidate binding")
    input_manifest = _load_exact_object(INPUT_PATH, INPUT_SHA256, "workflow input manifest")
    if _exact_bytes(INPUT_CANDIDATE_COPY_PATH, CANDIDATE_SHA256, "frozen candidate copy") != \
            _exact_bytes(CANDIDATE_PATH, CANDIDATE_SHA256, "candidate binding"):
        raise EvidenceError("workflow candidate snapshot differs from the bound candidate")
    _validate_candidate(candidate, report, protocol)

    train_split, train_seed, train_images = _load_source_manifest(
        TRAIN_MANIFEST_PATH, TRAIN_MANIFEST_SHA256)
    dev_split, dev_seed, dev_images = _load_source_manifest(DEV_MANIFEST_PATH, DEV_MANIFEST_SHA256)
    if train_split != "train" or dev_split != "validation" or train_seed != dev_seed:
        raise EvidenceError("source manifests do not bind the same train/validation smoke dataset")
    images = train_images + dev_images
    if len(images) != EXPECTED_SOURCES or len({item["image_sha256"] for item in images}) != len(images):
        raise EvidenceError("source manifests do not define seven distinct sources")

    family_ocr._require_exact_keys(
        input_manifest, {"schema", "protocol_sha256", "split", "sources"},
        "workflow input manifest")
    if (
        input_manifest.get("schema") != "graphreader.real-acceptance-frozen-synthetic-input.v1"
        or input_manifest.get("protocol_sha256") != PROTOCOL_SHA256
        or input_manifest.get("split") != "synthetic"
    ):
        raise EvidenceError("workflow input manifest has a foreign identity")
    sources = _require_list(input_manifest.get("sources"), "workflow input sources")
    expected_sources = [
        {
            "relative_path": (manifest_path.parent / str(image["image"]))
                .relative_to(REPOSITORY_ROOT).as_posix(),
            "sha256": image["image_sha256"], "width": image["width"], "height": image["height"],
        }
        for manifest_path, records in (
            (_repository_path(TRAIN_MANIFEST_PATH, "train manifest"), train_images),
            (_repository_path(DEV_MANIFEST_PATH, "dev manifest"), dev_images),
        )
        for image in records
    ]
    if sources != expected_sources:
        raise EvidenceError("workflow input sources differ from the complete source manifests")

    family_ocr._require_exact_keys(
        report,
        {"schema", "scope", "candidate_id", "revision", "candidate_binding_sha256",
         "input_manifest_sha256", "protocol_sha256", "execution_provider", "graph_optimization",
         "ocr_configuration_scope", "source_count", "completed_count", "failed_count",
         "elapsed_milliseconds", "production_approved", "private_corpus_access",
         "sealed_corpus_access", "model_selection_performed", "truth_consumed_by_inference", "cases"},
        "workflow report")
    if (
        report.get("schema") != "graphreader.real-acceptance-frozen-synthetic-report.v1"
        or report.get("scope") != "local-synthetic-frozen-candidate-diagnostic"
        or report.get("candidate_binding_sha256") != CANDIDATE_SHA256
        or report.get("input_manifest_sha256") != INPUT_SHA256
        or report.get("protocol_sha256") != PROTOCOL_SHA256
        or report.get("execution_provider") != "cpu"
        or report.get("graph_optimization") != "disabled"
        or report.get("ocr_configuration_scope") != "unapproved_frozen_candidate"
        or any(report.get(field) is not False for field in (
            "production_approved", "private_corpus_access", "sealed_corpus_access",
            "model_selection_performed", "truth_consumed_by_inference"))
    ):
        raise EvidenceError("workflow report identity or safety scope is invalid")
    cases = _require_list(report.get("cases"), "workflow report cases")
    if _require_int(report.get("source_count"), "workflow source count") != EXPECTED_SOURCES or len(cases) != EXPECTED_SOURCES:
        raise EvidenceError("workflow report does not retain all seven sources")
    report_root = _repository_path(REPORT_PATH, "workflow report").parent
    outputs: list[dict[str, Any]] = []
    completed = failed = panel_count = 0
    case_ids: set[str] = set()
    for index, (raw, source) in enumerate(zip(cases, expected_sources, strict=True)):
        case = _require_mapping(raw, f"workflow case {index}")
        source_id = _canonical_uuid(case.get("source_id"), f"workflow case {index} source ID")
        if source_id in case_ids:
            raise EvidenceError("workflow case source IDs are not unique")
        case_ids.add(source_id)
        if (
            case.get("image_sha256") != source["sha256"]
            or case.get("width") != source["width"] or case.get("height") != source["height"]
            or _require_int(case.get("panel_count"), "workflow panel count") < 0
            or _require_int(case.get("correction_count"), "workflow correction count") != 0
        ):
            raise EvidenceError("workflow case differs from its ordered source identity")
        status = case.get("status")
        if status not in {"completed", "failed"}:
            raise EvidenceError("workflow case status is invalid")
        raw_artifacts = _require_list(case.get("artifacts"), "workflow case artifacts")
        artifacts = [
            _validate_artifact(report_root, _require_mapping(item, "workflow artifact"),
                               f"workflow case {index} artifact {artifact_index}")
            for artifact_index, item in enumerate(raw_artifacts)
        ]
        if status == "completed":
            completed += 1
            if case.get("failure_type") is not None or len(artifacts) == 0:
                raise EvidenceError("completed workflow case has invalid failure or artifact evidence")
            failure_code = None
        else:
            failed += 1
            if artifacts or not isinstance(case.get("failure_type"), str) or not case["failure_type"]:
                raise EvidenceError("failed workflow case must retain failure type and zero artifacts")
            failure_code = case["failure_type"]
        panel_count += int(case["panel_count"])
        outputs.append({
            "case_key": source_id, "source_sha256": source["sha256"],
            "workflow_succeeded": status == "completed", "failure_code": failure_code,
            "artifacts": artifacts,
        })
    if (
        report.get("completed_count") != completed or report.get("failed_count") != failed
        or completed + failed != EXPECTED_SOURCES or panel_count != EXPECTED_PANELS
    ):
        raise EvidenceError("workflow report totals are inconsistent")
    return {
        "report": report, "protocol": protocol, "candidate": candidate,
        "dataset_seed": train_seed, "train_images": train_images, "dev_images": dev_images,
        "sources": expected_sources, "outputs": outputs,
    }


def _expected_printed_mode(panel: Mapping[str, Any]) -> str:
    session_count = _require_int(
        _require_mapping(panel.get("axes"), "panel axes").get("x", {}).get("session_count"),
        "panel session count", minimum=2)
    visible_x = {
        int(_finite(tick.get("value"), "x tick value"))
        for tick in _require_list(panel.get("ticks"), "panel ticks")
        if isinstance(tick, dict) and tick.get("axis") == "x" and tick.get("label") is not None
    }
    anchors = {
        str(anchor.get("kind")): tuple(_finite(value, "calibration graph value") for value in anchor.get("graph", []))
        for anchor in _require_list(panel.get("calibration_anchors"), "panel calibration anchors")
        if isinstance(anchor, dict)
    }
    if not {1, session_count}.issubset(visible_x):
        raise EvidenceError("fixed workflow panel lacks printed first/final session evidence")
    if (
        anchors.get("session1_y0", (None,))[0] != 1.0
        or anchors.get("session1_ymax", (None,))[0] != 1.0
        or anchors.get("sessionmax_y0", (None,))[0] != float(session_count)
    ):
        raise EvidenceError("fixed workflow panel lacks exact printed-session calibration anchors")
    return "printed_session"


def _truth_case(
    source: Mapping[str, Any], source_id: str, scene: Mapping[str, Any], annotation: Mapping[str, Any],
) -> dict[str, Any]:
    canvas = _require_mapping(scene.get("canvas"), "scene canvas")
    if canvas.get("width") != source["width"] or canvas.get("height") != source["height"]:
        raise EvidenceError("truth scene dimensions differ from the authenticated source")
    rows = list(_csv_rows(scene, annotation))
    rows_by_point = {str(row["point_id"]): row for row in rows}
    if len(rows_by_point) != len(rows):
        raise EvidenceError("truth table contains duplicate point identities")
    truth_series: list[dict[str, str]] = []
    truth_points: list[dict[str, Any]] = []
    relations: list[dict[str, Any]] = []
    series_ids: set[str] = set()
    point_ids: set[str] = set()
    for panel_index, raw_panel in enumerate(_require_list(scene.get("panels"), "scene panels")):
        panel = _require_mapping(raw_panel, f"scene panel {panel_index}")
        mode = _expected_printed_mode(panel)
        panel_series = _require_list(panel.get("series"), "panel series")
        for raw_series in panel_series:
            series = _require_mapping(raw_series, "truth series")
            series_id = _canonical_uuid(series.get("series_id"), "truth series ID")
            if series_id in series_ids:
                raise EvidenceError("truth series identities are not unique within a source")
            series_ids.add(series_id)
            truth_series.append({"series_key": series_id})
            if series.get("semantic_role") == "intervention":
                shared = series.get("shared_baseline_series_id")
                if shared is not None:
                    shared = _canonical_uuid(shared, "shared baseline series ID")
                probes = [
                    _canonical_uuid(value, "applicable probe series ID")
                    for value in _require_list(
                        series.get("applicable_probe_series_ids"), "applicable probe series IDs")
                ]
                relations.append({
                    "target_intervention_series_key": series_id,
                    "shared_baseline_series_key": shared,
                    "applicable_probe_series_keys": probes,
                })
        phase_codes = {
            _canonical_uuid(phase.get("phase_id"), "phase ID"): str(phase.get("code"))
            for phase in (
                _require_mapping(item, "truth phase")
                for item in _require_list(panel.get("phases"), "panel phases")
            )
        }
        if any(not value.strip() for value in phase_codes.values()):
            raise EvidenceError("truth phase codes must be nonempty")
        for raw_point in _require_list(panel.get("points"), "panel points"):
            point = _require_mapping(raw_point, "truth point")
            point_id = _canonical_uuid(point.get("point_id"), "truth point ID")
            if point_id in point_ids:
                raise EvidenceError("truth point identities are not unique within a source")
            point_ids.add(point_id)
            row = rows_by_point.get(point_id)
            if row is None:
                raise EvidenceError("truth table omits a scene point")
            series_id = _canonical_uuid(point.get("series_id"), "truth point series ID")
            phase_id = _canonical_uuid(point.get("phase_id"), "truth point phase ID")
            graph = point.get("graph")
            if not isinstance(graph, list) or len(graph) != 2:
                raise EvidenceError("truth point graph coordinate must contain two values")
            graph_x, graph_y = (_finite(value, "truth graph coordinate") for value in graph)
            source_x = _finite(row.get("original_pixel_x"), "truth source pixel x")
            source_y = _finite(row.get("original_pixel_y"), "truth source pixel y")
            if (
                series_id not in series_ids or phase_id not in phase_codes
                or row.get("series_id") != series_id or row.get("panel_id") != panel.get("panel_id")
                or _finite(row.get("x_value"), "truth table graph x") != graph_x
                or _finite(row.get("y_value"), "truth table graph y") != graph_y
                or row.get("phase") != phase_codes[phase_id]
                or not (0 <= source_x <= source["width"] and 0 <= source_y <= source["height"])
            ):
                raise EvidenceError("truth scene, annotation, and CSV projection disagree")
            truth_points.append({
                "point_key": point_id, "series_key": series_id,
                "source_pixel_x": source_x, "source_pixel_y": source_y,
                "graph_x": graph_x, "graph_y": graph_y,
                "expected_export_x": graph_x, "expected_export_mode": mode,
                "authoritative_phase_code": phase_codes[phase_id],
            })
    if set(rows_by_point) != point_ids:
        raise EvidenceError("truth table contains foreign points")
    if not truth_series or not truth_points or not relations:
        raise EvidenceError("truth case requires series, points, and explicit relations")
    referenced = {
        relation["target_intervention_series_key"] for relation in relations
    } | {
        relation["shared_baseline_series_key"] for relation in relations
        if relation["shared_baseline_series_key"] is not None
    } | {
        probe for relation in relations for probe in relation["applicable_probe_series_keys"]
    }
    if not referenced.issubset(series_ids):
        raise EvidenceError("truth relations reference a foreign series")
    return {
        "case_key": source_id, "source_sha256": source["sha256"],
        "source_width": source["width"], "source_height": source["height"],
        "series": truth_series, "points": truth_points, "relations": relations,
    }


def _build_document(evidence: Mapping[str, Any], builder_sha256: str) -> dict[str, Any]:
    # All runtime, report, artifact, and truth-code identities are checked before
    # the first call that creates synthetic truth.
    truth_environment = initial_scorer._validate_truth_environment()
    train_truth = family_ocr._regenerate(
        "train", int(evidence["dataset_seed"]), evidence["train_images"])
    dev_truth = family_ocr._regenerate(
        "validation", int(evidence["dataset_seed"]), evidence["dev_images"])
    regenerated = {**train_truth, **dev_truth}
    cases = _require_list(evidence["report"].get("cases"), "workflow report cases")
    truth_cases = [
        _truth_case(
            source, str(case["source_id"]),
            regenerated[str(source["sha256"])][0], regenerated[str(source["sha256"])][1])
        for source, case in zip(evidence["sources"], cases, strict=True)
    ]
    point_count = sum(len(case["points"]) for case in truth_cases)
    series_count = sum(len(case["series"]) for case in truth_cases)
    relation_count = sum(len(case["relations"]) for case in truth_cases)
    if point_count != EXPECTED_TRUTH_POINTS:
        raise EvidenceError(
            f"fixed truth denominator changed: expected {EXPECTED_TRUTH_POINTS}, found {point_count}")
    builder_path = Path(__file__).resolve()
    return {
        "schema": OUTPUT_SCHEMA,
        "scope": "project-owned-synthetic-only",
        "private_corpus_access": False,
        "sealed_corpus_access": False,
        "truth_consumed_by_inference": False,
        "workflow_report": {"path": REPORT_PATH.as_posix(), "sha256": REPORT_SHA256},
        "truth_cases": truth_cases,
        "outputs": evidence["outputs"],
        "options": {
            "source_pixel_match_tolerance": 5.0,
            "graph_x_absolute_tolerance": 0.5,
            "graph_y_absolute_tolerance": 5.0,
        },
        "provenance": {
            "purpose": "offline synthetic workflow CSV diagnosis only",
            "acceptance_evaluated": False,
            "production_approved": False,
            "model_inference_runs": 0,
            "builder": {
                "path": builder_path.relative_to(REPOSITORY_ROOT).as_posix(),
                "sha256": builder_sha256,
            },
            "truth_environment_guard": {
                "path": INITIAL_SCORER_PATH.as_posix(), "sha256": INITIAL_SCORER_SHA256,
            },
            "candidate_binding": {"path": CANDIDATE_PATH.as_posix(), "sha256": CANDIDATE_SHA256},
            "workflow_input_manifest": {"path": INPUT_PATH.as_posix(), "sha256": INPUT_SHA256},
            "protocol": {"path": PROTOCOL_PATH.as_posix(), "sha256": PROTOCOL_SHA256},
            "source_manifests": [
                {"path": TRAIN_MANIFEST_PATH.as_posix(), "sha256": TRAIN_MANIFEST_SHA256},
                {"path": DEV_MANIFEST_PATH.as_posix(), "sha256": DEV_MANIFEST_SHA256},
            ],
            "truth_environment": truth_environment,
            "counts": {
                "sources": len(truth_cases), "panels": EXPECTED_PANELS,
                "series": series_count, "relations": relation_count, "points": point_count,
                "failed_workflow_cases": sum(not output["workflow_succeeded"] for output in evidence["outputs"]),
            },
            "expected_export_mode": "printed_session proved independently for every fixed panel",
        },
    }


def preflight(builder_sha256: str) -> dict[str, Any]:
    validated_builder_sha256 = _validate_program_identity(builder_sha256)
    evidence = _validate_evidence()
    truth_environment = initial_scorer._validate_truth_environment()
    return {
        "status": "preflight-passed", "truth_regenerated": False,
        "builder_sha256": validated_builder_sha256,
        "truth_environment_guard_sha256": INITIAL_SCORER_SHA256,
        "workflow_report_sha256": REPORT_SHA256,
        "candidate_binding_sha256": CANDIDATE_SHA256,
        "workflow_input_manifest_sha256": INPUT_SHA256,
        "protocol_sha256": PROTOCOL_SHA256,
        "source_count": len(evidence["sources"]),
        "panel_count": sum(int(case["panel_count"]) for case in evidence["report"]["cases"]),
        "completed_count": int(evidence["report"]["completed_count"]),
        "failed_count": int(evidence["report"]["failed_count"]),
        "truth_environment": truth_environment,
    }


def build(output: Path, builder_sha256: str) -> dict[str, Any]:
    validated_builder_sha256 = _validate_program_identity(builder_sha256)
    evidence = _validate_evidence()
    document = _build_document(evidence, validated_builder_sha256)
    output_path = _repository_path(output, "CSV evaluation input")
    allowed_root = _repository_path(OUTPUT_ROOT, "CSV evaluation output root")
    if allowed_root not in output_path.parents:
        raise EvidenceError("CSV evaluation input must remain under its ignored artifact directory")
    if output_path.exists():
        raise EvidenceError("refusing to replace an existing CSV evaluation input")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    payload = (json.dumps(document, indent=2, sort_keys=True) + "\n").encode("utf-8")
    with output_path.open("xb") as stream:
        stream.write(payload)
    return {
        "status": "built", "path": output.relative_to(REPOSITORY_ROOT).as_posix()
        if output.is_absolute() else output.as_posix(),
        "sha256": _sha256_bytes(payload),
        "source_count": len(document["truth_cases"]),
        "truth_point_count": sum(len(case["points"]) for case in document["truth_cases"]),
        "failed_workflow_cases": sum(not item["workflow_succeeded"] for item in document["outputs"]),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true", help="authenticate inputs without regenerating truth")
    parser.add_argument(
        "--builder-sha256", required=True,
        help="reviewed SHA-256 of this exact frozen builder source")
    parser.add_argument(
        "--output", type=Path,
        default=OUTPUT_ROOT / "workflow-v2-csv-evaluation-input.json")
    arguments = parser.parse_args()
    result = preflight(arguments.builder_sha256) if arguments.preflight else build(
        arguments.output, arguments.builder_sha256)
    print(json.dumps(result, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
