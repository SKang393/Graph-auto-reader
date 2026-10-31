# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Offline scorer for the fixed public synthetic development set.

This module ranks already-exported production candidate features. It never
constructs a model, invokes a detector, or runs an optimizer. Ground truth is
opened only after the source-only exporter report has been created.
"""
from __future__ import annotations

from contextlib import ExitStack
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import re
import sys
from typing import Any

from ml.axis.candidate_ranker_foundation_v1 import features, labels

SCHEMA = "graphreader.axis-ranker-dev-evaluation.v1"
BINDING_SCHEMA = "graphreader.axis-ranker-dev-evaluation-binding.v1"
PROTOCOL_PATH = "ml/axis/candidate_ranker_dev_eval_v1/protocol.json"
DEV_MANIFEST_PATH = "artifacts/axis-roles-dev-corpus-v3-20261031/manifest.json"
DEV_MANIFEST_SHA256 = "c3c735a38dac5aa893331958ba09319e7fcfac534f8122738d8d07db37f507b1"
DEV_BINDING_PATH = "artifacts/goal22-runs/axis-roles-dev-measure-admission-v2/binding.json"
DEV_BINDING_SHA256 = "a32b43b05c74ea2279f2a7cd90e53024c5981f356dbf8735adbc8d1653168203"
DEV_PANELIZER_RUNTIME_PREFIX = "artifacts/goal22-runs/axis-roles-panelization-build-v2/"
DEV_PANELIZER_RUNTIME_FILE_COUNT = 73
DEV_REQUIRED_PANELIZER_RUNTIME_PATHS = frozenset({
    DEV_PANELIZER_RUNTIME_PREFIX + "Panelization.deps.json",
    DEV_PANELIZER_RUNTIME_PREFIX + "Panelization.dll",
    DEV_PANELIZER_RUNTIME_PREFIX + "Panelization.runtimeconfig.json",
    DEV_PANELIZER_RUNTIME_PREFIX + "GraphReader.App.dll",
    DEV_PANELIZER_RUNTIME_PREFIX + "GraphReader.Axis.dll",
    DEV_PANELIZER_RUNTIME_PREFIX + "GraphReader.Domain.dll",
    DEV_PANELIZER_RUNTIME_PREFIX + "GraphReader.Imaging.dll",
    DEV_PANELIZER_RUNTIME_PREFIX + "GraphReader.Pdf.dll",
    DEV_PANELIZER_RUNTIME_PREFIX + "OpenCvSharp.dll",
    DEV_PANELIZER_RUNTIME_PREFIX + "runtimes/win-x64/native/OpenCvSharpExtern.dll",
})
DEV_INPUTS_PATH = "artifacts/goal22-runs/axis-roles-dev-measure-admission-v2/inputs.json"
DEV_INPUTS_SHA256 = "c0a4b9f292f0ea71e61365323909bfbbad46e84ad67baa27d93ec2ba5916c6cd"
DEV_AXIS_ADAPTER_SOURCE_PATH = "src/GraphReader.App/Integration/Workflow/ProductionAxisGeometryAdapter.cs"
DEV_ADMISSION_SOURCE_PATHS = frozenset({
    "artifacts/goal22-tools/axis-roles-dev-measure-v2/bind.py",
    "artifacts/goal22-tools/axis-roles-dev-measure-v2/job.py",
    "artifacts/goal22-tools/axis-roles-dev-measure-v2/rank.py",
    "artifacts/goal22-tools/axis-roles-dev-measure-v2/run.py",
    "artifacts/goal22-tools/axis-roles-dev-measure-v2/test_rank.py",
    "artifacts/goal22-tools/axis-roles-production-panelization-v2/Panelization.csproj",
    "artifacts/goal22-tools/axis-roles-production-panelization-v2/Program.cs",
    "artifacts/goal22-tools/axis-unmasked-ocr-v1/rank_numeric_regions.py",
    "src/GraphReader.App/Integration/Workflow/ProductionRasterPanelizer.cs",
    "src/GraphReader.App/Integration/Workflow/ProductionWorkflowImportStage.cs",
    "src/GraphReader.App/Integration/Workflow/RasterPanelSourceProvenance.cs",
    "src/GraphReader.Imaging/ImageImportService.cs",
})
DEV_REQUIRED_PRODUCTION_SOURCE_PATHS = frozenset({
    "artifacts/goal22-tools/axis-roles-production-panelization-v2/Panelization.csproj",
    "artifacts/goal22-tools/axis-roles-production-panelization-v2/Program.cs",
    "src/GraphReader.App/GraphReader.App.csproj",
    "src/GraphReader.App/Integration/Workflow/ProductionRasterFrameDecoder.cs",
    "src/GraphReader.App/Integration/Workflow/ProductionWorkflowDetectionStage.cs",
    "src/GraphReader.App/Integration/Workflow/ProductionWorkflowImportStage.cs",
    "src/GraphReader.App/Integration/Workflow/ProductionWorkflowPanelStore.cs",
    "src/GraphReader.App/Integration/Workflow/ProductionRasterPanelizer.cs",
    "src/GraphReader.App/Integration/Workflow/ProductionAxisGeometryAdapter.cs",
    "src/GraphReader.App/Integration/Workflow/RasterPanelSourceProvenance.cs",
    "src/GraphReader.App/Integration/Workflow/WorkflowContracts.cs",
    "src/GraphReader.Axis/AxisGeometryContracts.cs",
    "src/GraphReader.Axis/AxisGeometryDetector.cs",
    "src/GraphReader.Axis/GraphReader.Axis.csproj",
    "src/GraphReader.Axis/OpenCvLineCandidateProvider.cs",
    "src/GraphReader.Imaging/GraphReader.Imaging.csproj",
    "src/GraphReader.Imaging/ImageImportService.cs",
    "Directory.Packages.props",
})
DEV_REQUIRED_TOOL_SOURCE_PATHS = frozenset({
    "tools/GraphReader.AxisCandidateEvidence/GraphReader.AxisCandidateEvidence.csproj",
    "tools/GraphReader.AxisCandidateEvidence/EvidenceContracts.cs",
    "tools/GraphReader.AxisCandidateEvidence/EvidenceRunner.cs",
    "tools/GraphReader.AxisCandidateEvidence/FeatureNormalization.cs",
    "tools/GraphReader.AxisCandidateEvidence/GlobalUsings.cs",
    "tools/GraphReader.AxisCandidateEvidence/PanelBatchEvidenceContracts.cs",
    "tools/GraphReader.AxisCandidateEvidence/PanelBatchEvidenceRunner.cs",
    "tools/GraphReader.AxisCandidateEvidence/Program.cs",
    "tools/GraphReader.AxisCandidateEvidence/input-binding.json",
})
SCORER_SOURCE_PATHS = (
    "ml/axis/__init__.py",
    "ml/axis/candidate_ranker_dev_eval_v1/__init__.py",
    "ml/axis/candidate_ranker_dev_eval_v1/evaluator.py",
    PROTOCOL_PATH,
    "ml/axis/candidate_ranker_foundation_v1/__init__.py",
    "ml/axis/candidate_ranker_foundation_v1/features.py",
    "ml/axis/candidate_ranker_foundation_v1/labels.py",
)
PANELIZATION_RECEIPT_PATH = "artifacts/goal22-runs/axis-roles-dev-measure-run-v2/truth-free-receipt.json"
PANELIZATION_RECEIPT_SHA256 = "4bd1189d391b6a2e0cd9dfd4c73c1c3206f774327ebf7fa5f36fe6a6dfc8678a"
TRAINING_CONFIG_PATH = "ml/axis/gpu_candidate_ranker_v1/p1-v4.json"
TRAINING_CONFIG_SHA256 = "4acacf53a23a7bc948a482c2fde7daddb839e682082891964805126eb0c745cd"
TRAINING_REPORT_PATH = "artifacts/goal22-runs/axis-ranker-gpu-training-attempt-v1/report.json"
TRAINING_REPORT_SHA256 = "3c196ec8f935d797f709efd48a415051b4d68e2bf22e085d32064d50aaee57a1"
CHECKPOINT_PATH = "artifacts/goal22-runs/axis-ranker-gpu-training-attempt-v1/checkpoint.json"
CHECKPOINT_SHA256 = "42a57afd0428bf028dbbf24279a0547ab02769dec1db4d49c5313d5735483adf"
FINALIZATION_PATH = "artifacts/goal22-runs/axis-ranker-gpu-training-attempt-v1/finalization.json"
FINALIZATION_SHA256 = "4e113ba64158c60ae285f58bb8e1231f40c55970177e81673548ea81b0d9140c"
_SHA = re.compile(r"^[0-9a-f]{64}$")
_MAX_JSON_BYTES = 128 * 1024 * 1024
_EXPECTED_SOURCE_COUNT = 32
_EXPECTED_PANEL_COUNT = 48
_EXPECTED_ANNOTATION_PREFLIGHT = {
    "sources": 32,
    "annotation_plots": 48,
    "runtime_panels": 48,
    "detected_unique_matches": 46,
    "fallback_unscored_sources": 2,
}


class EvaluationError(ValueError):
    """The supplied development evidence does not satisfy the pinned contract."""


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _unique_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise EvaluationError("duplicate JSON key")
        result[key] = value
    return result


def _reject_constant(_: str) -> None:
    raise EvaluationError("non-finite JSON constant")


def _parse(raw: bytes, description: str) -> Any:
    if len(raw) > _MAX_JSON_BYTES:
        raise EvaluationError(f"{description} exceeds the byte limit")
    try:
        return json.loads(raw, parse_constant=_reject_constant, object_pairs_hook=_unique_pairs)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise EvaluationError(f"{description} is not valid UTF-8 JSON") from error


def _safe_project_path(root: Path, relative: str, *, allow_missing: bool = False) -> Path:
    if not isinstance(relative, str) or not relative or "\\" in relative:
        raise EvaluationError("project path must be a nonempty POSIX-relative path")
    pure = PurePosixPath(relative)
    if pure.is_absolute() or any(
        part in ("", ".", "..", "test", "tests") or
        "private" in part.casefold() or "sealed" in part.casefold()
        for part in pure.parts
    ):
        raise EvaluationError("project path traversal rejected")
    path = root.joinpath(*pure.parts)
    current = root
    if _has_reparse(current):
        raise EvaluationError("project root is a reparse point")
    for part in pure.parts:
        current = current / part
        try:
            _ = current.lstat()
        except FileNotFoundError:
            if allow_missing:
                continue
            raise EvaluationError("bound input does not exist")
        if _has_reparse(current):
            raise EvaluationError("reparse-point path rejected")
    try:
        path.resolve(strict=not allow_missing).relative_to(root.resolve(strict=True))
    except (FileNotFoundError, ValueError) as error:
        raise EvaluationError("resolved path escapes project root") from error
    return path


def _has_reparse(path: Path) -> bool:
    try:
        attributes = path.lstat().st_file_attributes
    except AttributeError:
        return path.is_symlink()
    return bool(attributes & 0x400)


def _lock_read(stack: ExitStack, path: Path) -> None:
    if os.name != "nt":
        raise EvaluationError("Windows no-writer input locks are required")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                                   ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32,
                                   ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.CreateFileW(str(path), 0x80000000, 1, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    stack.callback(kernel.CloseHandle, handle)


def _read_bound(root: Path, relative: str, expected_sha256: str, stack: ExitStack) -> bytes:
    if not isinstance(expected_sha256, str) or not _SHA.fullmatch(expected_sha256):
        raise EvaluationError("expected SHA-256 is malformed")
    path = _safe_project_path(root, relative)
    _lock_read(stack, path)
    raw = path.read_bytes()
    if _sha(raw) != expected_sha256:
        raise EvaluationError(f"SHA-256 mismatch: {relative}")
    return raw


def _number(value: Any, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise EvaluationError(f"{name} must be a finite number")
    return float(value)


def _canonical_digest(value: dict[str, Any]) -> str:
    return _sha(json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8"))


def _validate_hash_map(root: Path, expected: Any, observed: Any, name: str,
                       stack: ExitStack, *, required: set[str] | None = None) -> dict[str, str]:
    if not isinstance(expected, dict) or not expected or not isinstance(observed, dict) or expected != observed:
        raise EvaluationError(f"{name} differs between input binding and runtime report")
    if len(expected) > 256 or any(
        not isinstance(path, str) or not isinstance(digest, str) or not _SHA.fullmatch(digest)
        for path, digest in expected.items()
    ):
        raise EvaluationError(f"{name} is malformed")
    if required is not None and not required.issubset(expected):
        raise EvaluationError(f"{name} omits required source paths")
    for path, digest in expected.items():
        _read_bound(root, path, digest, stack)
    return dict(expected)


def _verify_export_runtime(root: Path, report: dict[str, Any], export_binding: dict[str, Any],
                           admission: dict[str, Any], stack: ExitStack) -> dict[str, Any]:
    runtime = report.get("runtime")
    expected_runtime_keys = {
        "tool_assembly_path", "tool_assembly_sha256", "app_assembly_path", "app_assembly_sha256",
        "axis_assembly_path", "axis_assembly_sha256", "open_cv_managed_assembly_path",
        "open_cv_managed_assembly_sha256", "open_cv_native_module_path", "open_cv_native_module_sha256",
        "production_source_bindings", "panelizer_runtime_bindings", "tool_source_bindings",
        "tool_runtime_bindings", "open_cv_options", "geometry_options",
    }
    if not isinstance(runtime, dict) or set(runtime) != expected_runtime_keys:
        raise EvaluationError("candidate exporter runtime receipt is missing or has an unknown shape")

    input_keys = {
        "schema", "scope", "split", "panelizationAdmissionBindingPath", "panelizationAdmissionBindingSha256",
        "panelizationReceiptPath", "panelizationReceiptSha256", "panelizationRunRoot", "expectedSourceCount",
        "expectedDetectedSourceCount", "expectedFallbackSourceCount", "expectedFailedSourceCount",
        "expectedPanelCount", "selectedPanels", "productionSourceBindings", "panelizerRuntimeBindings",
        "toolSourceBindings", "toolRuntimeBindings", "openCvOptions", "geometryOptions",
        "devCorpusManifestPath", "devCorpusManifestSha256",
    }
    if set(export_binding) != input_keys or export_binding.get("schema") != "graphreader.axis-candidate-panel-batch-input.v1":
        raise EvaluationError("candidate exporter input manifest shape differs from the reviewed contract")

    production_sources = _validate_hash_map(
        root, export_binding.get("productionSourceBindings"), runtime.get("production_source_bindings"),
        "production source bindings", stack, required=DEV_REQUIRED_PRODUCTION_SOURCE_PATHS)
    tool_sources = _validate_hash_map(
        root, export_binding.get("toolSourceBindings"), runtime.get("tool_source_bindings"),
        "exporter source bindings", stack, required=DEV_REQUIRED_TOOL_SOURCE_PATHS)
    if set(production_sources) != DEV_REQUIRED_PRODUCTION_SOURCE_PATHS or \
       set(tool_sources) != DEV_REQUIRED_TOOL_SOURCE_PATHS:
        raise EvaluationError("source binding closure has unexpected paths")
    tool_runtime = _validate_hash_map(
        root, export_binding.get("toolRuntimeBindings"), runtime.get("tool_runtime_bindings"),
        "exporter runtime bindings", stack)
    admission_sources = admission.get("files")
    if not isinstance(admission_sources, dict) or set(admission_sources) != DEV_ADMISSION_SOURCE_PATHS:
        raise EvaluationError("fixed dev admission source bundle differs")
    _validate_hash_map(root, admission_sources, admission_sources, "fixed dev admission source bundle", stack,
                       required=DEV_ADMISSION_SOURCE_PATHS)
    admission_runtime = admission.get("runtime_files")
    if not isinstance(admission_runtime, dict):
        raise EvaluationError("fixed dev admission runtime map is missing")
    panelizer_sources = {
        path: digest for path, digest in admission_runtime.items()
        if isinstance(path, str) and path.startswith(DEV_PANELIZER_RUNTIME_PREFIX)
    }
    if len(panelizer_sources) != DEV_PANELIZER_RUNTIME_FILE_COUNT:
        raise EvaluationError("fixed dev panelizer runtime subset has an unexpected file count")
    if not DEV_REQUIRED_PANELIZER_RUNTIME_PATHS.issubset(panelizer_sources):
        raise EvaluationError("fixed dev panelizer runtime subset is missing required runtime files")
    panelizer_runtime = _validate_hash_map(
        root, export_binding.get("panelizerRuntimeBindings"), runtime.get("panelizer_runtime_bindings"),
        "panelizer runtime bindings", stack)
    if set(panelizer_runtime) != set(panelizer_sources) or panelizer_runtime != panelizer_sources:
        raise EvaluationError("panelizer runtime bindings differ from the fixed dev admission map")

    assembly_fields = (
        ("tool_assembly_path", "tool_assembly_sha256"),
        ("app_assembly_path", "app_assembly_sha256"),
        ("axis_assembly_path", "axis_assembly_sha256"),
        ("open_cv_managed_assembly_path", "open_cv_managed_assembly_sha256"),
        ("open_cv_native_module_path", "open_cv_native_module_sha256"),
    )
    for path_key, hash_key in assembly_fields:
        path, digest = runtime.get(path_key), runtime.get(hash_key)
        if not isinstance(path, str) or not isinstance(digest, str) or tool_runtime.get(path) != digest:
            raise EvaluationError(f"runtime assembly identity is not bound: {path_key}")
    if set(tool_runtime) != {runtime[path_key] for path_key, _ in assembly_fields}:
        raise EvaluationError("tool runtime map has missing or unexpected assemblies")

    expected_input_opencv = {
        "useLineSegmentDetector": True, "useProbabilisticHough": True,
        "cannyLowThreshold": 40.0, "cannyHighThreshold": 120.0, "houghThreshold": 24,
        "houghMinimumLineLengthPixels": 12.0, "houghMaximumLineGapPixels": 4.0,
        "lsdRefinement": "RefineStd",
    }
    expected_input_geometry = {
        "maximumAxisDeviationDegrees": 15.0, "mergeAngleToleranceDegrees": 4.0,
        "mergeDistancePixels": 3.0, "minimumCandidateLengthPixels": 1.0,
        "minimumAxisSpanFraction": 0.25, "tickMaximumLengthFraction": 0.08,
        "tickAxisDistancePixels": 4.0, "dividerMinimumSpanFraction": 0.45,
        "dividerMinimumCoverageFraction": 0.06, "dottedDividerMinimumSegments": 3,
        "dottedDividerMaximumSegmentFraction": 0.18, "plotEdgeExclusionFraction": 0.025,
        "gridSpacingCoefficientOfVariation": 0.12, "gridMinimumAlignedLines": 3,
        "needsReviewConfidenceThreshold": 0.55,
    }
    expected_report_opencv = {
        "UseLineSegmentDetector": True, "UseProbabilisticHough": True,
        "CannyLowThreshold": 40.0, "CannyHighThreshold": 120.0, "HoughThreshold": 24,
        "HoughMinimumLineLengthPixels": 12.0, "HoughMaximumLineGapPixels": 4.0,
        "LsdRefinement": 1,
    }
    expected_report_geometry = {
        "MaximumAxisDeviationDegrees": 15.0, "MergeAngleToleranceDegrees": 4.0,
        "MergeDistancePixels": 3.0, "MinimumCandidateLengthPixels": 1.0,
        "MinimumAxisSpanFraction": 0.25, "TickMaximumLengthFraction": 0.08,
        "TickAxisDistancePixels": 4.0, "DividerMinimumSpanFraction": 0.45,
        "DividerMinimumCoverageFraction": 0.06, "DottedDividerMinimumSegments": 3,
        "DottedDividerMaximumSegmentFraction": 0.18, "PlotEdgeExclusionFraction": 0.025,
        "GridSpacingCoefficientOfVariation": 0.12, "GridMinimumAlignedLines": 3,
        "NeedsReviewConfidenceThreshold": 0.55,
    }
    if export_binding.get("openCvOptions") != expected_input_opencv or \
       export_binding.get("geometryOptions") != expected_input_geometry or \
       runtime.get("open_cv_options") != expected_report_opencv or \
       runtime.get("geometry_options") != expected_report_geometry:
        raise EvaluationError("candidate exporter options differ from production adapter defaults")

    return {
        "tool_assembly_path": runtime["tool_assembly_path"],
        "tool_assembly_sha256": runtime["tool_assembly_sha256"],
        "app_assembly_path": runtime["app_assembly_path"],
        "app_assembly_sha256": runtime["app_assembly_sha256"],
        "axis_assembly_path": runtime["axis_assembly_path"],
        "axis_assembly_sha256": runtime["axis_assembly_sha256"],
        "open_cv_managed_assembly_path": runtime["open_cv_managed_assembly_path"],
        "open_cv_managed_assembly_sha256": runtime["open_cv_managed_assembly_sha256"],
        "open_cv_native_module_path": runtime["open_cv_native_module_path"],
        "open_cv_native_module_sha256": runtime["open_cv_native_module_sha256"],
        "production_source_bindings": production_sources,
        "panelizer_runtime_bindings_sha256": _canonical_digest(panelizer_runtime),
        "tool_source_bindings": tool_sources,
        "tool_runtime_bindings_sha256": _canonical_digest(tool_runtime),
        "open_cv_options": runtime["open_cv_options"],
        "geometry_options": runtime["geometry_options"],
    }


def _require_dev_export(report: dict[str, Any], export_binding: dict[str, Any], manifest: dict[str, Any],
                        panelization_receipt: dict[str, Any]) -> None:
    if report.get("schema") != "graphreader.axis-candidate-panel-batch-evidence.v1" or \
       report.get("status") not in ("success", "completed_with_failures") or report.get("scope") != "public_synthetic_development" or \
       report.get("split") != "dev":
        raise EvaluationError("exporter report is not a successful pinned public dev export")
    if report.get("source_count") != _EXPECTED_SOURCE_COUNT or \
       report.get("panel_count") != _EXPECTED_PANEL_COUNT or \
       report.get("selected_panel_count") != _EXPECTED_PANEL_COUNT or \
       len(report.get("source_summaries", [])) != _EXPECTED_SOURCE_COUNT or \
       len(report.get("panel_results", [])) != _EXPECTED_PANEL_COUNT:
        raise EvaluationError("export report does not retain the full 32-source / 48-panel denominator")
    if report.get("panelization_admission_binding_sha256") != DEV_BINDING_SHA256 or \
       report.get("panelization_receipt_sha256") != PANELIZATION_RECEIPT_SHA256:
        raise EvaluationError("export report does not bind the fixed dev admission and panelization receipts")
    if export_binding.get("scope") != "public_synthetic_development" or export_binding.get("split") != "dev" or \
       export_binding.get("devCorpusManifestPath") != DEV_MANIFEST_PATH or \
       export_binding.get("devCorpusManifestSha256") != DEV_MANIFEST_SHA256 or \
       export_binding.get("selectedPanels") != []:
        raise EvaluationError("export input manifest is not the fixed all-panel dev binding")
    if export_binding.get("panelizationAdmissionBindingPath") != DEV_BINDING_PATH or \
       export_binding.get("panelizationAdmissionBindingSha256") != DEV_BINDING_SHA256 or \
       export_binding.get("panelizationReceiptPath") != PANELIZATION_RECEIPT_PATH or \
       export_binding.get("panelizationReceiptSha256") != PANELIZATION_RECEIPT_SHA256 or \
       export_binding.get("panelizationRunRoot") != "artifacts/goal22-runs/axis-roles-dev-measure-run-v2" or \
       export_binding.get("expectedSourceCount") != 32 or export_binding.get("expectedDetectedSourceCount") != 30 or \
       export_binding.get("expectedFallbackSourceCount") != 2 or export_binding.get("expectedFailedSourceCount") != 0 or \
       export_binding.get("expectedPanelCount") != 48:
        raise EvaluationError("export input does not bind the fixed full dev panelization denominator")
    cases = manifest.get("dev_cases")
    if not isinstance(cases, list) or len(cases) != _EXPECTED_SOURCE_COUNT:
        raise EvaluationError("pinned dev manifest does not contain exactly 32 cases")
    ids = [case.get("scene_id") for case in cases if isinstance(case, dict)]
    if len(ids) != _EXPECTED_SOURCE_COUNT or len(set(ids)) != _EXPECTED_SOURCE_COUNT:
        raise EvaluationError("dev manifest scene IDs are invalid or duplicated")
    summaries = report["source_summaries"]
    if {row.get("source_id") for row in summaries} != set(ids):
        raise EvaluationError("source summaries differ from the fixed development manifest")
    summary_by_id = {row["source_id"]: row for row in summaries}
    source_status_by_id = {source_id: row.get("source_status") for source_id, row in summary_by_id.items()}
    case_by_id = {case["scene_id"]: case for case in cases}
    status_counts = {"detected": 0, "no_panel_fallback": 0, "failed": 0}
    for row in summaries:
        case = case_by_id[row.get("source_id")]
        if row.get("source_sha256") != case.get("image_sha256"):
            raise EvaluationError("source summary digest differs from the fixed dev manifest")
        status = row.get("source_status")
        if status not in status_counts:
            raise EvaluationError("source summary status is unknown")
        status_counts[status] += 1
    if status_counts != {"detected": 30, "no_panel_fallback": 2, "failed": 0}:
        raise EvaluationError("source status denominator differs from the fixed development export")
    panels = report["panel_results"]
    if any(row.get("source_id") not in set(ids) for row in panels):
        raise EvaluationError("panel result references a non-dev source")
    unique_panels = {(row.get("source_id"), row.get("panel_id")) for row in panels}
    if len(unique_panels) != _EXPECTED_PANEL_COUNT:
        raise EvaluationError("panel result identities are missing or duplicated")
    receipt_panels: dict[tuple[str, str], str] = {}
    for source in panelization_receipt.get("sources", []):
        source_id = source.get("id")
        for panel in source.get("panels", []):
            key = (source_id, panel.get("panel_id"))
            if key in receipt_panels:
                raise EvaluationError("truth-free panelization receipt contains duplicate panel identity")
            receipt_panels[key] = panel.get("panel_sha256")
    if len(receipt_panels) != _EXPECTED_PANEL_COUNT:
        raise EvaluationError("truth-free receipt panel denominator differs")
    panel_status_counts: dict[str, int] = {"success": 0, "failed": 0}
    for row in panels:
        case = case_by_id[row["source_id"]]
        expected_origin = "full_image_review_fallback" if source_status_by_id[row["source_id"]] == "no_panel_fallback" else "detected_panel"
        expected_source_path = f"artifacts/axis-roles-dev-corpus-v3-20261031/images/{case['index']}.png"
        if row.get("source_sha256") != case.get("image_sha256") or \
           row.get("source_image_path") != expected_source_path or \
           row.get("panel_origin_kind") != expected_origin or \
           row.get("coordinate_space") != "panel_original_pixels" or \
           not isinstance(row.get("panel_image_path"), str) or \
           not row["panel_image_path"].startswith("artifacts/goal22-runs/axis-roles-dev-measure-run-v2/") or \
           receipt_panels.get((row["source_id"], row["panel_id"])) != row.get("panel_sha256"):
            raise EvaluationError("panel result path or source digest differs from the fixed dev export")
        summary = summary_by_id[row["source_id"]]
        if row.get("source_width") != summary.get("source_width") or row.get("source_height") != summary.get("source_height"):
            raise EvaluationError("panel source dimensions differ from source summary")
        _validate_panel_transform(row)
        status = row.get("status")
        if status not in ("success", "failed"):
            raise EvaluationError("candidate export has an incomplete or cancelled panel")
        panel_status_counts[status] = panel_status_counts.get(status, 0) + 1
        candidates = row.get("candidate_snapshots")
        if status == "success":
            if not isinstance(candidates, list) or not candidates or not isinstance(row.get("normalized_features"), dict) or row.get("failure") is not None:
                raise EvaluationError("successful exporter panel has empty or inconsistent candidate evidence")
        elif candidates != [] or row.get("normalized_features") is not None or not isinstance(row.get("failure"), dict):
            raise EvaluationError("failed exporter panel has candidate evidence or no failure record")
    if sum(panel_status_counts.values()) != _EXPECTED_PANEL_COUNT:
        raise EvaluationError("panel status counts do not sum to the full panel denominator")
    if report.get("status") == "success" and panel_status_counts.get("failed", 0) != 0:
        raise EvaluationError("successful exporter report contains failed panels")
    if report.get("status") == "completed_with_failures" and panel_status_counts.get("failed", 0) == 0:
        raise EvaluationError("failed exporter report has no failed panels")


def _validate_panel_transform(row: dict[str, Any]) -> None:
    encoded = row.get("encoded_crop_in_source_pixels")
    requested = row.get("requested_crop_in_source_pixels")
    if not isinstance(encoded, dict) or not isinstance(requested, dict):
        raise EvaluationError("panel crop metadata is missing")
    required_rect_keys = {"X", "Y", "Width", "Height", "Right", "Bottom", "Area", "IsValid"}
    if set(encoded) != required_rect_keys or set(requested) != required_rect_keys:
        raise EvaluationError("panel crop shape differs")
    rectangles: list[tuple[float, float, float, float]] = []
    for rect in (requested, encoded):
        if rect.get("IsValid") is not True:
            raise EvaluationError("panel crop is marked invalid")
        x, y, width, height = (_number(rect.get(key), "panel crop") for key in ("X", "Y", "Width", "Height"))
        if min(x, y) < 0 or min(width, height) <= 0 or any(value != math.floor(value) for value in (x, y, width, height)):
            raise EvaluationError("panel crop coordinates are invalid")
        if rect.get("Right") != x + width or rect.get("Bottom") != y + height or rect.get("Area") != width * height:
            raise EvaluationError("panel crop derived bounds differ")
        rectangles.append((x, y, width, height))
    req_x, req_y, req_w, req_h = rectangles[0]
    enc_x, enc_y, enc_w, enc_h = rectangles[1]
    if req_x < enc_x or req_y < enc_y or req_x + req_w > enc_x + enc_w or req_y + req_h > enc_y + enc_h:
        raise EvaluationError("requested crop escapes encoded crop")
    if row.get("panel_width") != enc_w or row.get("panel_height") != enc_h:
        raise EvaluationError("panel dimensions differ from encoded crop")
    forward = row.get("source_to_panel_matrix_row_major")
    inverse = row.get("panel_to_source_matrix_row_major")
    expected_forward = [1, 0, -enc_x, 0, 1, -enc_y, 0, 0, 1]
    expected_inverse = [1, 0, enc_x, 0, 1, enc_y, 0, 0, 1]
    if forward != expected_forward or inverse != expected_inverse:
        raise EvaluationError("panel source coordinate transforms differ from production crop translation")


def _verify_checkpoint(checkpoint: dict[str, Any], config: dict[str, Any], training: dict[str, Any],
                       finalization: dict[str, Any]) -> tuple[list[float], list[float], float]:
    if checkpoint.get("schema") != "graphreader.axis-linear-ranker-checkpoint.v1" or \
       checkpoint.get("production_approved") is not False or \
       checkpoint.get("config_sha256") != TRAINING_CONFIG_SHA256:
        raise EvaluationError("checkpoint identity or approval state differs")
    if set(checkpoint) != {"schema", "config_sha256", "feature_scale", "weight", "bias", "production_approved"}:
        raise EvaluationError("checkpoint contains unknown fields")
    if config.get("architecture") != "linear-17-to-1" or config.get("feature_scale") != checkpoint.get("feature_scale") or \
       config.get("task") != "axis-candidate-ranker" or config.get("candidate_id") != "P1" or \
       config.get("group_count") != 93 or config.get("candidate_count") != 27776 or \
       config.get("optimizer_steps") != 372 or config.get("objective") != "listwise-soft-target-cross-entropy" or \
       config.get("dev_read") is not False or config.get("reserved_read") is not False or \
       config.get("production_approved") is not False:
        raise EvaluationError("trainer configuration differs from the pinned train-only recipe")
    if training.get("schema") != "graphreader.axis-gpu-ranker-training-report.v1" or \
       training.get("status") != "training_complete_pending_finalization" or \
       training.get("steps") != 372 or training.get("checkpoint_sha256") != CHECKPOINT_SHA256 or \
       training.get("config_sha256") != TRAINING_CONFIG_SHA256 or \
       training.get("dev_read") is not False or training.get("reserved_read") is not False or \
       training.get("production_approved") is not False:
        raise EvaluationError("training report is not the pinned unapproved train-only report")
    if finalization.get("schema") != "graphreader.axis-gpu-ranker-finalization.v1" or \
       finalization.get("status") != "finalized" or finalization.get("report_sha256") != TRAINING_REPORT_SHA256 or \
       finalization.get("checkpoint_sha256") != CHECKPOINT_SHA256 or \
       finalization.get("production_approved") is not False:
        raise EvaluationError("training finalization receipt differs")
    weights_raw, scale_raw = checkpoint.get("weight"), checkpoint.get("feature_scale")
    if not isinstance(weights_raw, list) or not isinstance(scale_raw, list) or \
       len(weights_raw) != len(features.FEATURE_NAMES) or len(scale_raw) != len(features.FEATURE_NAMES):
        raise EvaluationError("checkpoint feature dimensions differ")
    weights = [_number(value, "checkpoint weight") for value in weights_raw]
    scale = [_number(value, "checkpoint scale") for value in scale_raw]
    bias = _number(checkpoint.get("bias"), "checkpoint bias")
    if any(value <= 0 for value in scale):
        raise EvaluationError("checkpoint scales must be positive")
    return weights, scale, bias


def _validate_feature_rows(panel: dict[str, Any]) -> list[list[float]]:
    snapshots = panel.get("candidate_snapshots")
    matrix = panel.get("normalized_features")
    if not isinstance(snapshots, list) or not isinstance(matrix, dict) or \
       matrix.get("schema") != features.SCHEMA or matrix.get("feature_names") != list(features.FEATURE_NAMES):
        raise EvaluationError("panel candidate or normalized-feature rows are missing")
    rows = matrix.get("rows")
    if not isinstance(rows, list) or len(rows) != len(snapshots):
        raise EvaluationError("candidate and feature row counts differ")
    computed = features.snapshot_group(panel["source_sha256"], panel["panel_sha256"],
                                       panel["panel_width"], panel["panel_height"], snapshots)["rows"]
    values: list[list[float]] = []
    for index, (snapshot, actual, expected) in enumerate(zip(snapshots, rows, computed, strict=True)):
        if not isinstance(actual, dict) or set(actual) != {"rank", "features"}:
            raise EvaluationError("normalized feature row shape differs from the production exporter contract")
        if type(snapshot.get("Rank")) is not int or snapshot.get("Rank") != index or \
           type(actual.get("rank")) is not int or actual.get("rank") != index + 1 or \
           actual.get("rank") != expected["rank"]:
            raise EvaluationError("candidate rank/order identity differs")
        actual_features = actual.get("features")
        if not isinstance(actual_features, list) or len(actual_features) != len(features.FEATURE_NAMES):
            raise EvaluationError("feature vector width differs")
        row = [_number(value, "exported feature") for value in actual_features]
        for actual_value, expected_value in zip(row, expected["features"], strict=True):
            if abs(actual_value - expected_value) > 1e-10:
                raise EvaluationError("exported C#/Python feature values differ")
        values.append(row)
    return values


def _candidate_corners(snapshot: dict[str, Any], crop: list[float]) -> list[list[float]]:
    ox, oy = crop[0], crop[1]
    corners = []
    for key in ("TopLeft", "TopRight", "BottomRight", "Intersection"):
        point = snapshot.get(key)
        if not isinstance(point, dict):
            raise EvaluationError("candidate corner is missing")
        corners.append([_number(point.get("X"), key + ".X") + ox,
                        _number(point.get("Y"), key + ".Y") + oy])
    return corners


def _normalize_dev_annotation_plots(annotation: dict[str, Any], source_id: str, seed: int) -> list[dict[str, Any]]:
    """Adapt v3 annotation plot rows to the foundation label helper contract.

    The annotation's panel_id is retained as its local plot identity. It is not
    a runtime panel ID; association with exported panels remains geometric.
    """
    if annotation.get("schema_version") != 1 or annotation.get("scene_id") != source_id or \
       annotation.get("coordinate_space") != "original_pixels" or annotation.get("seed") != seed:
        raise EvaluationError("annotation schema, scene, seed, or coordinate space differs")
    rows = annotation.get("plots")
    if not isinstance(rows, list):
        raise EvaluationError("annotation plots must be an array")

    normalized: list[dict[str, Any]] = []
    expected_plot_keys = {"box", "coordinate_space", "panel_id", "polygon"}
    for row in rows:
        if not isinstance(row, dict) or set(row) != expected_plot_keys:
            raise EvaluationError("annotation plot row shape differs from the pinned v3 schema")
        panel_id = row.get("panel_id")
        if not isinstance(panel_id, str) or not panel_id or row.get("coordinate_space") != "original_pixels":
            raise EvaluationError("annotation plot identity or coordinate space differs")
        box = row.get("box")
        if not isinstance(box, list) or len(box) != 4:
            raise EvaluationError("annotation plot box must contain four values")
        box_values = [_number(value, "annotation plot box") for value in box]
        if min(box_values[2:]) <= 0:
            raise EvaluationError("annotation plot box has nonpositive dimensions")
        normalized.append({**row, "id": panel_id})
    return normalized


def _preflight_annotation_panel_identities(
    manifest: dict[str, Any],
    export_report: dict[str, Any],
    annotations: dict[str, dict[str, Any]],
) -> dict[str, dict[str, Any]]:
    """Validate fixed annotation schema and geometric panel association only.

    This preflight does not inspect candidate features or compute model scores.
    """
    source_rows = export_report.get("source_summaries")
    panel_rows = export_report.get("panel_results")
    cases = manifest.get("dev_cases")
    if not isinstance(source_rows, list) or len(source_rows) != _EXPECTED_SOURCE_COUNT or \
       not isinstance(panel_rows, list) or len(panel_rows) != _EXPECTED_PANEL_COUNT or \
       not isinstance(cases, list) or len(cases) != _EXPECTED_SOURCE_COUNT:
        raise EvaluationError("annotation preflight source or panel denominator differs")

    sources = {row.get("source_id"): row for row in source_rows if isinstance(row, dict)}
    if len(sources) != _EXPECTED_SOURCE_COUNT or set(annotations) != set(sources):
        raise EvaluationError("annotation and exported source identities differ")
    case_by_id = {case.get("scene_id"): case for case in cases if isinstance(case, dict)}
    if len(case_by_id) != _EXPECTED_SOURCE_COUNT or set(case_by_id) != set(sources):
        raise EvaluationError("annotation manifest source identities differ")
    panels_by_source: dict[str, list[dict[str, Any]]] = {source_id: [] for source_id in sources}
    for panel in panel_rows:
        if not isinstance(panel, dict) or panel.get("source_id") not in panels_by_source:
            raise EvaluationError("exported panel source identity is invalid")
        panels_by_source[panel["source_id"]].append(panel)

    associations: dict[str, dict[str, Any]] = {}
    totals: dict[str, int] = {
        "sources": len(sources),
        "annotation_plots": 0,
        "runtime_panels": len(panel_rows),
        "detected_unique_matches": 0,
        "fallback_unscored_sources": 0,
    }
    for source_id in sorted(sources):
        summary = sources[source_id]
        case = case_by_id[source_id]
        plot_rows = _normalize_dev_annotation_plots(annotations[source_id], source_id, case.get("seed"))
        totals["annotation_plots"] += len(plot_rows)
        source_panels = panels_by_source[source_id]
        if len({row.get("panel_id") for row in source_panels}) != len(source_panels):
            raise EvaluationError("duplicate runtime panel identity within source")
        association_panels = []
        for row in source_panels:
            crop = row.get("encoded_crop_in_source_pixels")
            if not isinstance(crop, dict):
                raise EvaluationError("panel crop is missing during annotation preflight")
            candidates = row.get("candidate_snapshots")
            successful = row.get("status") == "success" and isinstance(candidates, list) and len(candidates) > 0
            association_panels.append({
                "id": row["panel_id"],
                "crop": [_number(crop[key], "crop coordinate") for key in ("X", "Y", "Width", "Height")],
                "exporter_success": successful,
                "candidate_count": len(candidates) if successful else None,
            })
        source_status = summary.get("source_status")
        association = labels.associate(source_status, plot_rows, association_panels)
        plot_ids = {plot["id"] for plot in plot_rows}
        panel_ids = {panel["id"] for panel in association_panels}
        for match in association["matches"]:
            if match["plot_id"] not in plot_ids or match["panel_id"] not in panel_ids:
                raise EvaluationError("geometric association escaped its source identities")
        totals["detected_unique_matches"] += association["summary"]["labelable_groups"]
        if source_status == "no_panel_fallback":
            totals["fallback_unscored_sources"] += 1
        associations[source_id] = association

    if totals != _EXPECTED_ANNOTATION_PREFLIGHT:
        raise EvaluationError(f"fixed annotation/panel preflight counts differ: {totals}")
    return associations


def _corner_metrics(plot: dict[str, Any], crop: list[float], snapshot: dict[str, Any]) -> tuple[float, float, list[list[float]]]:
    corners = _candidate_corners(snapshot, crop)
    points = plot.get("polygon")
    if not isinstance(points, list) or len(points) != 4:
        raise EvaluationError("plot truth must have four corners")
    truth = [[_number(point[0], "plot x"), _number(point[1], "plot y")] for point in points]
    width = max(point[0] for point in truth) - min(point[0] for point in truth)
    height = max(point[1] for point in truth) - min(point[1] for point in truth)
    if width <= 0 or height <= 0:
        raise EvaluationError("plot truth has degenerate bounds")
    pixel_mean = sum(math.hypot(actual[0] - expected[0], actual[1] - expected[1])
                     for actual, expected in zip(corners, truth, strict=True)) / 4
    normalized_mean = labels.continuous_targets(plot, crop, [snapshot])[0]
    return pixel_mean, normalized_mean, corners


def _score_payload(manifest: dict[str, Any], export_report: dict[str, Any],
                   annotations: dict[str, dict[str, Any]], checkpoint: dict[str, Any],
                   config: dict[str, Any], training: dict[str, Any], finalization: dict[str, Any],
                   provenance: dict[str, Any], *, expected_source_count: int = _EXPECTED_SOURCE_COUNT,
                   expected_panel_count: int = _EXPECTED_PANEL_COUNT) -> dict[str, Any]:
    weights, scale, bias = _verify_checkpoint(checkpoint, config, training, finalization)
    source_summaries = {row["source_id"]: row for row in export_report["source_summaries"]}
    panels_by_source: dict[str, list[dict[str, Any]]] = {source_id: [] for source_id in source_summaries}
    panel_status_counts: dict[str, int] = {"success": 0, "failed": 0}
    for panel in export_report["panel_results"]:
        status = panel.get("status")
        if status not in ("success", "failed"):
            raise EvaluationError("panel exporter status is not complete")
        panel_status_counts[status] = panel_status_counts.get(status, 0) + 1
        panels_by_source[panel["source_id"]].append(panel)
    dev_cases = manifest["dev_cases"]
    case_by_id = {case["scene_id"]: case for case in dev_cases}
    per_panel: list[dict[str, Any]] = []
    association_totals: dict[str, int] = {}
    scored_errors: list[tuple[float, float, float, float]] = []

    for source_id in sorted(source_summaries):
        summary = source_summaries[source_id]
        annotation = annotations[source_id]
        case = case_by_id[source_id]
        plot_rows = _normalize_dev_annotation_plots(annotation, source_id, case.get("seed"))
        panel_rows = panels_by_source[source_id]
        source_status = summary.get("source_status")
        for row in panel_rows:
            crop = row.get("encoded_crop_in_source_pixels")
            if not isinstance(crop, dict):
                raise EvaluationError("panel crop is missing")
            row["_crop"] = [_number(crop[key], "crop coordinate") for key in ("X", "Y", "Width", "Height")]
        association_panels = []
        for row in panel_rows:
            candidates = row.get("candidate_snapshots")
            successful = row.get("status") == "success" and isinstance(candidates, list) and len(candidates) > 0
            association_panels.append({
                "id": row["panel_id"],
                "crop": row["_crop"],
                "exporter_success": successful,
                "candidate_count": len(candidates) if successful else None,
            })
        association = labels.associate(source_status, plot_rows, association_panels)
        for key, count in association["summary"].items():
            association_totals[key] = association_totals.get(key, 0) + count
        match_by_panel = {match["panel_id"]: match["plot_id"] for match in association["matches"]}
        plots_by_id = {plot["id"]: plot for plot in plot_rows}
        for row in panel_rows:
            output: dict[str, Any] = {
                "source_id": source_id,
                "panel_id": row["panel_id"],
                "source_sha256": row["source_sha256"],
                "panel_sha256": row["panel_sha256"],
                "panel_origin_kind": row["panel_origin_kind"],
                "exporter_status": row["status"],
                "candidate_count": len(row.get("candidate_snapshots", [])),
                "scoring_status": "not_matched",
                "baseline_rank": None,
                "candidate_rank": None,
                "baseline_linear_score": None,
                "candidate_linear_score": None,
                "baseline_mean_corner_error_pixels": None,
                "candidate_mean_corner_error_pixels": None,
                "baseline_mean_normalized_corner_error": None,
                "candidate_mean_normalized_corner_error": None,
                "plot_id": None,
            }
            if row["panel_id"] not in match_by_panel:
                if row["status"] != "success":
                    output["scoring_status"] = "export_failed_or_empty"
                elif source_status == "no_panel_fallback":
                    output["scoring_status"] = "fallback_not_scored"
                elif not plot_rows:
                    output["scoring_status"] = "no_plot_truth"
                else:
                    output["scoring_status"] = "unmatched_or_ambiguous"
                per_panel.append(output)
                continue

            if source_status == "no_panel_fallback":
                output["scoring_status"] = "fallback_not_scored"
                per_panel.append(output)
                continue

            plot_id = match_by_panel[row["panel_id"]]
            plot = plots_by_id[plot_id]
            vectors = _validate_feature_rows(row)
            model_scores = [sum(weight * value * factor for weight, value, factor in zip(weights, vector, scale, strict=True)) + bias
                            for vector in vectors]
            model_scores = [_number(value, "candidate rank score") for value in model_scores]
            candidate_index = max(range(len(model_scores)), key=lambda index: (model_scores[index], -index))
            baseline_index = 0
            snapshots = row["candidate_snapshots"]
            baseline_px, baseline_norm, baseline_corners = _corner_metrics(plot, row["_crop"], snapshots[baseline_index])
            candidate_px, candidate_norm, candidate_corners = _corner_metrics(plot, row["_crop"], snapshots[candidate_index])
            output.update({
                "scoring_status": "scored",
                "plot_id": plot_id,
                "baseline_rank": baseline_index + 1,
                "candidate_rank": candidate_index + 1,
                "baseline_linear_score": model_scores[baseline_index],
                "candidate_linear_score": model_scores[candidate_index],
                "baseline_mean_corner_error_pixels": baseline_px,
                "candidate_mean_corner_error_pixels": candidate_px,
                "baseline_mean_normalized_corner_error": baseline_norm,
                "candidate_mean_normalized_corner_error": candidate_norm,
                "baseline_corners_source_pixels": baseline_corners,
                "candidate_corners_source_pixels": candidate_corners,
            })
            per_panel.append(output)
            scored_errors.append((baseline_px, candidate_px, baseline_norm, candidate_norm))

    if len(per_panel) != expected_panel_count:
        raise EvaluationError("panel output denominator changed during scoring")
    if sum(panel_status_counts.values()) != expected_panel_count:
        raise EvaluationError("panel status counts do not sum to the full panel denominator")
    if not scored_errors:
        raise EvaluationError("no uniquely matched, successful panel can be scored")
    baseline_pixel_mean = sum(value[0] for value in scored_errors) / len(scored_errors)
    candidate_pixel_mean = sum(value[1] for value in scored_errors) / len(scored_errors)
    baseline_mean = sum(value[2] for value in scored_errors) / len(scored_errors)
    candidate_mean = sum(value[3] for value in scored_errors) / len(scored_errors)
    return {
        "schema": SCHEMA,
        "status": "exploratory_dev_scored_no_acceptance_claim",
        "scope": "public_synthetic_development",
        "split": "dev",
        "production_approved": False,
        "sealed_read": False,
        "private_read": False,
        "training_performed": False,
        "optimizer_steps": 0,
        "truth_read_by_offline_scorer": True,
        "truth_used_by_exporter_or_model_ranking": False,
        "candidate_checkpoint_sha256": provenance["checkpoint_sha256"],
        "training_report_sha256": provenance["training_report_sha256"],
        "training_config_sha256": provenance["training_config_sha256"],
        "finalization_sha256": provenance["finalization_sha256"],
        "dev_manifest_sha256": provenance["dev_manifest_sha256"],
        "panel_export_report_sha256": provenance["panel_export_report_sha256"],
        "panel_export_input_manifest_sha256": provenance["panel_export_input_manifest_sha256"],
        "panel_export_runtime": provenance["panel_export_runtime"],
        "evaluation_binding_sha256": provenance["evaluation_binding_sha256"],
        "scorer_protocol_sha256": provenance["scorer_protocol_sha256"],
        "scorer_source_bindings": provenance["scorer_source_bindings"],
        "scorer_source_bundle_sha256": provenance["scorer_source_bundle_sha256"],
        "source_count": expected_source_count,
        "panel_count": expected_panel_count,
        "panel_export_status_counts": panel_status_counts,
        "scored_panel_count": len(scored_errors),
        "association_counters": association_totals,
        "baseline_policy": "first candidate in production observer rank order",
        "candidate_policy": "highest checkpoint linear score; ties keep earlier production rank",
        "metric_policy": "mean of four corner Euclidean errors; normalized by plot width and height independently, matching train_labels_v2.continuous_targets",
        "baseline_mean_normalized_corner_error": baseline_mean,
        "candidate_mean_normalized_corner_error": candidate_mean,
        "baseline_mean_corner_error_pixels": baseline_pixel_mean,
        "candidate_mean_corner_error_pixels": candidate_pixel_mean,
        "mean_normalized_error_delta_candidate_minus_baseline": candidate_mean - baseline_mean,
        "candidate_improved_panel_count": sum(candidate < baseline for _, _, baseline, candidate in scored_errors),
        "candidate_equal_panel_count": sum(candidate == baseline for _, _, baseline, candidate in scored_errors),
        "candidate_worsened_panel_count": sum(candidate > baseline for _, _, baseline, candidate in scored_errors),
        "panels": per_panel,
    }


def _load_binding_inputs(root: Path, binding_path: str, expected_binding_sha256: str,
                         output_path: str) -> tuple[dict[str, Any], dict[str, Any], str]:
    with ExitStack() as stack:
        binding_raw = _read_bound(root, binding_path, expected_binding_sha256, stack)
        binding = _parse(binding_raw, "evaluation binding")
        required = {
            "schema", "scope", "split", "dev_manifest_path", "dev_manifest_sha256",
            "panel_export_report_path", "panel_export_report_sha256", "panelization_receipt_path",
            "panelization_receipt_sha256", "training_config_path", "training_config_sha256",
            "training_report_path", "training_report_sha256", "checkpoint_path", "checkpoint_sha256",
            "finalization_path", "finalization_sha256", "protocol_path", "protocol_sha256",
            "scorer_source_bindings",
        }
        if not isinstance(binding, dict) or set(binding) != required or binding.get("schema") != BINDING_SCHEMA or \
           binding.get("scope") != "public_synthetic_development" or binding.get("split") != "dev":
            raise EvaluationError("evaluation binding schema or scope differs")
        fixed = {
            "dev_manifest_path": DEV_MANIFEST_PATH,
            "dev_manifest_sha256": DEV_MANIFEST_SHA256,
            "panelization_receipt_path": PANELIZATION_RECEIPT_PATH,
            "panelization_receipt_sha256": PANELIZATION_RECEIPT_SHA256,
            "training_config_path": TRAINING_CONFIG_PATH,
            "training_config_sha256": TRAINING_CONFIG_SHA256,
            "training_report_path": TRAINING_REPORT_PATH,
            "training_report_sha256": TRAINING_REPORT_SHA256,
            "checkpoint_path": CHECKPOINT_PATH,
            "checkpoint_sha256": CHECKPOINT_SHA256,
            "finalization_path": FINALIZATION_PATH,
        }
        for key, value in fixed.items():
            if binding.get(key) != value:
                raise EvaluationError(f"fixed binding identity differs: {key}")
        if binding.get("finalization_sha256") != FINALIZATION_SHA256:
            raise EvaluationError("finalization SHA-256 differs from the fixed training receipt")
        if binding.get("protocol_path") != PROTOCOL_PATH:
            raise EvaluationError("scorer protocol path differs")
        source_bundle = binding.get("scorer_source_bindings")
        if not isinstance(source_bundle, dict) or set(source_bundle) != set(SCORER_SOURCE_PATHS):
            raise EvaluationError("scorer source bundle inventory differs")
        _validate_hash_map(root, source_bundle, source_bundle, "scorer source bundle", stack,
                           required=set(SCORER_SOURCE_PATHS))
        if binding.get("protocol_sha256") != source_bundle.get(PROTOCOL_PATH):
            raise EvaluationError("scorer protocol SHA differs from the source bundle")
        protocol = _parse(_read_bound(root, PROTOCOL_PATH, binding["protocol_sha256"], stack), "scorer protocol")
        if protocol.get("schema") != "graphreader.axis-ranker-dev-evaluation-protocol.v1" or \
           protocol.get("dev_manifest_sha256") != DEV_MANIFEST_SHA256 or \
           protocol.get("panelization_receipt_sha256") != PANELIZATION_RECEIPT_SHA256 or \
           protocol.get("checkpoint_sha256") != CHECKPOINT_SHA256 or \
           protocol.get("scorer_source_bundle") != list(SCORER_SOURCE_PATHS) or \
           protocol.get("exporter_production_source_bundle") != sorted(DEV_REQUIRED_PRODUCTION_SOURCE_PATHS) or \
           protocol.get("exporter_tool_source_bundle") != sorted(DEV_REQUIRED_TOOL_SOURCE_PATHS) or \
           protocol.get("required_export_runtime") != {
               "production_axis_adapter_source": DEV_AXIS_ADAPTER_SOURCE_PATH,
               "open_cv_options": "production OpenCvLineCandidateProvider defaults",
               "geometry_options": "production AxisGeometryRequest defaults",
               "panelizer_runtime_map": "the 73-file axis-roles-panelization-build-v2 subset of fixed dev admission runtime_files; dev admission files source map is separately hash-verified",
               "panel_statuses": ["success", "failed"],
               "required_coordinate_space": "panel_original_pixels",
               "crop_transform": "exact integer crop translation in both directions",
           } or \
           protocol.get("annotation_contract") != {
               "schema_version": 1,
               "plot_fields": ["box", "coordinate_space", "panel_id", "polygon"],
               "plot_identity_adapter": "preserve annotation panel_id as the labels helper plot id",
               "runtime_panel_association": "geometric plot-center-in-crop association; annotation panel_id is not equated to runtime panel_id",
               "preflight_counts": {
                   "sources": 32,
                   "annotation_plots": 48,
                   "runtime_panels": 48,
                   "detected_unique_matches": 46,
                   "fallback_unscored_sources": 2,
               },
           } or \
           protocol.get("candidate_row_contract") != {
               "keys": ["rank", "features"],
               "identity_binding": "zero-based snapshot Rank and one-based normalized row rank pair rows by exact order; each 17-value vector is recomputed from the corresponding ordered snapshot",
               "stored_candidate_identity_sha256": False,
           } or \
           protocol.get("acceptance") != "exploratory only; no production or release acceptance claim":
            raise EvaluationError("scorer protocol content differs from the fixed dev evaluation contract")
        for key in ("panel_export_report_path",):
            value = binding.get(key)
            if not isinstance(value, str) or not value.startswith("artifacts/goal22-runs/axis-candidate-evidence-public-v3-dev-") or not value.endswith("/report.json"):
                raise EvaluationError("candidate export report path is outside the fixed dev output family")
        manifest = _parse(_read_bound(root, DEV_MANIFEST_PATH, DEV_MANIFEST_SHA256, stack), "dev manifest")
        export_raw = _read_bound(root, binding["panel_export_report_path"], binding["panel_export_report_sha256"], stack)
        report = _parse(export_raw, "candidate export report")
        export_binding_path = report.get("input_manifest_path") if isinstance(report, dict) else None
        export_binding_sha = report.get("input_manifest_sha256") if isinstance(report, dict) else None
        if not isinstance(export_binding_path, str) or \
           not export_binding_path.startswith("artifacts/goal22-runs/axis-candidate-evidence-public-v3-dev-") or \
           not export_binding_path.endswith("/input-manifest.json"):
            raise EvaluationError("candidate exporter input manifest path is outside the fixed dev output family")
        export_input = _parse(_read_bound(root, export_binding_path, export_binding_sha, stack), "candidate export input manifest")
        receipt = _parse(_read_bound(root, PANELIZATION_RECEIPT_PATH, PANELIZATION_RECEIPT_SHA256, stack), "truth-free panelization receipt")
        if receipt.get("schema") != "graphreader.axis-roles-dev-truth-free-receipt.v2" or \
           receipt.get("truth_read") is not False or receipt.get("reserved_read") is not False or \
           receipt.get("binding_sha256") != DEV_BINDING_SHA256 or \
           len(receipt.get("sources", [])) != _EXPECTED_SOURCE_COUNT:
            raise EvaluationError("public dev panelization receipt differs")
        admission = _parse(_read_bound(root, DEV_BINDING_PATH, DEV_BINDING_SHA256, stack), "public dev panelization admission")
        if admission.get("schema") != "graphreader.axis-roles-dev-measure-binding.v2" or \
           admission.get("corpus_manifest_sha256") != DEV_MANIFEST_SHA256 or \
           admission.get("source_count") != _EXPECTED_SOURCE_COUNT or \
           admission.get("reserved_read") is not False or admission.get("truth_in_prediction") is not False or \
           admission.get("production_approved") is not False or admission.get("optimizer_steps") != 0:
            raise EvaluationError("public dev admission binding differs")
        config = _parse(_read_bound(root, TRAINING_CONFIG_PATH, TRAINING_CONFIG_SHA256, stack), "training config")
        training = _parse(_read_bound(root, TRAINING_REPORT_PATH, TRAINING_REPORT_SHA256, stack), "training report")
        checkpoint = _parse(_read_bound(root, CHECKPOINT_PATH, CHECKPOINT_SHA256, stack), "ranker checkpoint")
        finalization_sha = binding["finalization_sha256"]
        finalization = _parse(_read_bound(root, FINALIZATION_PATH, finalization_sha, stack), "training finalization")
        if binding["finalization_sha256"] != _sha(_safe_project_path(root, FINALIZATION_PATH).read_bytes()):
            raise EvaluationError("finalization receipt digest changed")
        runtime_provenance = _verify_export_runtime(root, report, export_input, admission, stack)
        _require_dev_export(report, export_input, manifest, receipt)
        if export_input.get("panelizationAdmissionBindingPath") != DEV_BINDING_PATH or \
           export_input.get("panelizationAdmissionBindingSha256") != DEV_BINDING_SHA256 or \
           export_input.get("panelizationReceiptPath") != PANELIZATION_RECEIPT_PATH or \
           export_input.get("panelizationReceiptSha256") != PANELIZATION_RECEIPT_SHA256 or \
           export_input.get("devCorpusManifestPath") != DEV_MANIFEST_PATH or \
           export_input.get("devCorpusManifestSha256") != DEV_MANIFEST_SHA256:
            raise EvaluationError("candidate export does not bind the fixed truth-free panelization receipt")
        provenance = {
            "checkpoint_sha256": CHECKPOINT_SHA256,
            "training_report_sha256": TRAINING_REPORT_SHA256,
            "training_config_sha256": TRAINING_CONFIG_SHA256,
            "finalization_sha256": binding["finalization_sha256"],
            "dev_manifest_sha256": DEV_MANIFEST_SHA256,
            "panel_export_report_sha256": binding["panel_export_report_sha256"],
            "panel_export_input_manifest_sha256": export_binding_sha,
            "panel_export_runtime": runtime_provenance,
            "evaluation_binding_sha256": expected_binding_sha256,
            "scorer_protocol_sha256": binding["protocol_sha256"],
            "scorer_source_bindings": dict(source_bundle),
            "scorer_source_bundle_sha256": _canonical_digest(source_bundle),
        }
        annotations: dict[str, dict[str, Any]] = {}
        for case in manifest["dev_cases"]:
            index = case.get("index")
            if type(index) is not int or not 0 <= index < _EXPECTED_SOURCE_COUNT:
                raise EvaluationError("dev case index is invalid")
            image_path = f"artifacts/axis-roles-dev-corpus-v3-20261031/images/{index}.png"
            annotation_path = f"artifacts/axis-roles-dev-corpus-v3-20261031/annotations/{index}.json"
            image_raw = _read_bound(root, image_path, case["image_sha256"], stack)
            _ = image_raw
            annotation_raw = _read_bound(root, annotation_path, case["annotation_sha256"], stack)
            annotation = _parse(annotation_raw, "dev annotation")
            if annotation.get("scene_id") != case.get("scene_id"):
                raise EvaluationError("annotation scene identity differs")
            annotations[case["scene_id"]] = annotation
        _preflight_annotation_panel_identities(manifest, report, annotations)
        result = _score_payload(manifest, report, annotations, checkpoint, config, training, finalization, provenance)
        output = _safe_project_path(root, output_path, allow_missing=True)
        if not output_path.startswith("artifacts/goal22-runs/axis-ranker-dev-eval-") or not output_path.endswith("/report.json") or output.exists():
            raise EvaluationError("evaluation output must be fresh and inside its scoped run directory")
        output.parent.mkdir(parents=True, exist_ok=True)
        _safe_project_path(root, output_path, allow_missing=True)
        with output.open("xb") as stream:
            stream.write((json.dumps(result, sort_keys=True, indent=2, allow_nan=False) + "\n").encode("utf-8"))
        return result, binding, _sha(export_raw)


def main(argv: list[str] | None = None) -> int:
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binding", required=True, help="project-relative pinned evaluation binding JSON")
    parser.add_argument("--binding-sha256", required=True, help="reviewed SHA-256 of binding bytes")
    parser.add_argument("--output", required=True, help="fresh project-relative report path")
    args = parser.parse_args(argv)
    root = Path(__file__).resolve().parents[3]
    result, _, _ = _load_binding_inputs(root, args.binding, args.binding_sha256, args.output)
    print(f"status={result['status']}")
    print(f"report={args.output}")
    print(f"scored_panel_count={result['scored_panel_count']}")
    print(f"baseline_mean_normalized_corner_error={result['baseline_mean_normalized_corner_error']:.12g}")
    print(f"candidate_mean_normalized_corner_error={result['candidate_mean_normalized_corner_error']:.12g}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
