"""Receipt-bound overlay materializer for the private two-source train pilot."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from pathlib import Path
from typing import Any

from .render import PIL_VERSION, render_one, sha256_file

ROOT = Path(r"E:\Graph-auto-reader")
PLAN_SCHEMA = "graphreader.manual-train-marker-overlay-pilot-plan.v1"
EXTRACTION_SCHEMA = "graphreader.manual-train-marker-overlay-pilot-source-extraction.v1"
PROTOCOL_PATH = ROOT / "ml/markers/center/manual_train_overlay_pilot_v1/protocol.json"
MANIFEST_SHA256 = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57"
RENDER_RELATIVE_PATH = "ml/markers/center/manual_train_overlay_pilot_v1/render.py"


class CaptureRenderError(ValueError):
    pass


def _inside_project(path: Path, *, directory: bool = False, exists: bool = True) -> Path:
    root = Path(os.path.abspath(ROOT))
    absolute = Path(os.path.abspath(path))
    try:
        relative = absolute.relative_to(root)
    except ValueError as exc:
        raise CaptureRenderError("path is outside the E project") from exc
    current = Path(root.anchor)
    for part in root.parts[1:] + relative.parts:
        current = current / part
        if current.exists() or current.is_symlink():
            attributes = getattr(os.lstat(current), "st_file_attributes", 0)
            if current.is_symlink() or attributes & 0x400:
                raise CaptureRenderError("path contains a reparse-point ancestor")
    if exists:
        valid = absolute.is_dir() if directory else absolute.is_file()
        if not valid:
            raise CaptureRenderError("path has the wrong file kind or does not exist")
    return absolute


def _locked_bytes(path: Path) -> bytes:
    path = _inside_project(path)
    with path.open("rb") as stream:
        return stream.read()


def _parse_expected_json(path: Path, expected_sha256: str) -> tuple[dict[str, Any], bytes]:
    raw = _locked_bytes(path)
    if hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise CaptureRenderError("input JSON bytes differ from the externally supplied hash")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise CaptureRenderError("input JSON root must be an object")
    return value, raw


def _validate_protocol(plan: dict[str, Any]) -> tuple[str, str]:
    protocol_bytes = _locked_bytes(PROTOCOL_PATH)
    protocol_sha = hashlib.sha256(protocol_bytes).hexdigest()
    if plan.get("protocol_sha256") != protocol_sha:
        raise CaptureRenderError("plan does not bind the current frozen protocol")
    protocol = json.loads(protocol_bytes)
    renderer_hashes = protocol.get("source_files", {})
    if not isinstance(renderer_hashes, dict) or len(renderer_hashes) != 14:
        raise CaptureRenderError("protocol source closure is malformed")
    for relative, expected in renderer_hashes.items():
        path = _inside_project(ROOT / relative)
        if hashlib.sha256(_locked_bytes(path)).hexdigest() != expected:
            raise CaptureRenderError("a frozen extraction source no longer matches its protocol hash")
    expected_base = renderer_hashes.get(RENDER_RELATIVE_PATH)
    actual_base = hashlib.sha256(_locked_bytes(ROOT / RENDER_RELATIVE_PATH)).hexdigest()
    if expected_base != actual_base:
        raise CaptureRenderError("base renderer source no longer matches the frozen protocol")
    return protocol_sha, actual_base


def _validate_attempt(path: Path, expected_sha256: str, expected_events: int) -> None:
    raw = _locked_bytes(path)
    if hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise CaptureRenderError("source attempt journal hash differs")
    try:
        events = [json.loads(line) for line in raw.decode("utf-8").splitlines()]
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise CaptureRenderError("source attempt journal is malformed") from exc
    if len(events) != expected_events or not events or events[-1] != {"event_name": "sources_complete", "source_count": 2}:
        raise CaptureRenderError("source attempt journal is incomplete")
    expected_names = ["source_started", "source_completed", "source_started", "source_completed", "sources_complete"]
    if [event.get("event_name") for event in events] != expected_names:
        raise CaptureRenderError("source attempt event order differs")
    if [event.get("source_index") for event in events[:4]] != [0, 0, 1, 1]:
        raise CaptureRenderError("source attempt index order differs")


def render_captured(
    plan_path: Path,
    expected_plan_sha256: str,
    extraction_receipt_path: Path,
    expected_extraction_receipt_sha256: str,
    expected_renderer_sha256: str,
    output_dir: Path,
) -> dict[str, Any]:
    plan, plan_bytes = _parse_expected_json(plan_path, expected_plan_sha256)
    if hashlib.sha256(_locked_bytes(Path(__file__))).hexdigest() != expected_renderer_sha256:
        raise CaptureRenderError("capture renderer source differs from external pin")
    protocol_sha, base_renderer_sha = _validate_protocol(plan)
    receipt, receipt_bytes = _parse_expected_json(extraction_receipt_path, expected_extraction_receipt_sha256)
    receipt_sha = hashlib.sha256(receipt_bytes).hexdigest()

    if plan.get("schema") != PLAN_SCHEMA or plan.get("status") != "frozen_plan_not_yet_materialized" or plan.get("source_project_count") != 104:
        raise CaptureRenderError("plan is not the frozen 104-source feasibility plan")
    if plan.get("source_capture_label_scope") != "curve-point-centers-only; marker-glyph-correspondence-unverified; no-negative-labels" or \
        "not official dev" not in plan.get("split_claim", ""):
        raise CaptureRenderError("plan overstates weak-label or split scope")
    if plan.get("source_capture_manifest_sha256") != MANIFEST_SHA256 or plan.get("protocol_sha256") != protocol_sha:
        raise CaptureRenderError("plan source or protocol identity differs")
    if set(receipt) != {"schema", "status", "plan_sha256", "protocol_sha256", "source_manifest_sha256", "capture_attempt_sha256", "assignment_sha256", "selected_inventory_sha256", "extracted_sources", "capture_attempt_event_count", "development_payload_reads", "sealed_payload_reads", "optimizer_steps", "training_admitted", "runtime", "sources"}:
        raise CaptureRenderError("source extraction receipt fields differ")
    if receipt.get("schema") != EXTRACTION_SCHEMA or receipt.get("status") != "train_only_source_pngs_extracted_overlay_not_yet_rendered":
        raise CaptureRenderError("source extraction receipt schema/status differs")
    if receipt.get("plan_sha256") != expected_plan_sha256 or receipt.get("protocol_sha256") != protocol_sha or receipt.get("source_manifest_sha256") != MANIFEST_SHA256:
        raise CaptureRenderError("source extraction receipt is not bound to the frozen plan")
    if receipt.get("development_payload_reads") != 0 or receipt.get("sealed_payload_reads") != 0 or receipt.get("optimizer_steps") != 0 or receipt.get("training_admitted") is not False:
        raise CaptureRenderError("source extraction receipt exceeds train-only, no-training scope")
    sources = plan.get("sources")
    captured = receipt.get("sources")
    if not isinstance(sources, list) or len(sources) != 2 or not isinstance(captured, list) or len(captured) != 2 or receipt.get("extracted_sources") != 2:
        raise CaptureRenderError("pilot source count differs")
    expected_runtime = json.loads(_locked_bytes(PROTOCOL_PATH)).get("runtime_assemblies", {})
    runtime = receipt.get("runtime")
    if not isinstance(runtime, dict) or set(runtime) != {"ExportAssemblySha256", "ExtractorAssemblySha256"} or \
        runtime["ExportAssemblySha256"] != expected_runtime.get("GraphReader.Export") or \
        runtime["ExtractorAssemblySha256"] != expected_runtime.get("GraphReader.MarkerTrainOverlayPilot"):
        raise CaptureRenderError("source runtime binding is malformed")
    _validate_attempt(extraction_receipt_path.parent / "source-attempt.jsonl", receipt.get("capture_attempt_sha256", ""), receipt.get("capture_attempt_event_count", -1))

    output_dir = _inside_project(output_dir, exists=False)
    if output_dir.exists() or output_dir.is_symlink() or not output_dir.parent.is_dir():
        raise CaptureRenderError("render output must be fresh with an existing project-local parent")
    validated_sources: list[tuple[dict[str, Any], Path]] = []
    for index, (source, captured_row) in enumerate(zip(sources, captured, strict=True)):
        if set(captured_row) != {"index", "assigned_partition", "case_id", "study_id", "project_sha256", "image_sha256", "extracted_png_sha256", "width", "height", "byte_length", "path", "curve_point_count"}:
            raise CaptureRenderError("captured source row schema differs")
        if captured_row["index"] != index or captured_row["assigned_partition"] != source["assigned_partition"] or \
            captured_row["case_id"] != source["case_id"] or captured_row["study_id"] != source["study_id"] or \
            captured_row["project_sha256"] != source["project_sha256"] or captured_row["image_sha256"] != source["image_sha256"] or \
            captured_row["width"] != source["image_width"] or captured_row["height"] != source["image_height"] or \
            captured_row["curve_point_count"] != source["curve_point_count"]:
            raise CaptureRenderError("plan and source extraction row identities differ")
        filename = captured_row["path"]
        if not isinstance(filename, str) or Path(filename).name != filename or filename in ("", ".", ".."):
            raise CaptureRenderError("captured image path is not a basename")
        source_path = _inside_project(extraction_receipt_path.parent / filename)
        if source_path.stat().st_size != captured_row["byte_length"] or sha256_file(source_path) != captured_row["extracted_png_sha256"] or captured_row["extracted_png_sha256"] != source["image_sha256"]:
            raise CaptureRenderError("captured image bytes do not match both extraction and source manifest hashes")
        validated_sources.append((source, source_path))

    staging_dir = output_dir.with_name(output_dir.name + ".staging")
    if staging_dir.exists() or staging_dir.is_symlink():
        raise CaptureRenderError("render staging directory must be fresh")
    staging_dir = _inside_project(staging_dir, exists=False)
    staging_dir.mkdir()
    rows: list[dict[str, Any]] = []
    for index, (source, source_path) in enumerate(validated_sources):
        output_path = staging_dir / f"overlay-{index:02d}.png"
        row = render_one(source_path, source["image_sha256"], source, output_path)
        rows.append({"assigned_partition": source["assigned_partition"], **row})

    result = {
        "schema": "graphreader.manual-train-marker-overlay-pilot-result.v1",
        "status": "materialized_unapproved_feasibility_pilot",
        "plan_sha256": hashlib.sha256(plan_bytes).hexdigest(),
        "source_extraction_receipt_sha256": receipt_sha,
        "source_attempt_sha256": receipt["capture_attempt_sha256"],
        "protocol_sha256": protocol_sha,
        "renderer_source_sha256": expected_renderer_sha256,
        "base_renderer_source_sha256": base_renderer_sha,
        "source_count": len(rows),
        "optimizer_steps": 0,
        "development_payload_reads": 0,
        "sealed_payload_reads": 0,
        "training_admitted": False,
        "renderer": {"python": sys.version.split()[0], "pillow": PIL_VERSION},
        "rows": rows,
        "limitation": "Only injected patch glyphs are labeled. Original structures remain unknown. This does not measure full-image false-positive precision or transfer to naturally occurring markers/artifacts.",
    }
    receipt_path = staging_dir / "receipt.json"
    receipt_path.write_bytes((json.dumps(result, sort_keys=True, indent=2) + "\n").encode("utf-8"))
    result_sha = sha256_file(receipt_path)
    (staging_dir / "receipt.sha256").write_text(result_sha + "  receipt.json\n", encoding="ascii")
    staging_dir.rename(output_dir)
    return {"status": result["status"], "source_count": len(rows), "receipt_sha256": result_sha,
            "optimizer_steps": 0, "development_payload_reads": 0, "sealed_payload_reads": 0, "training_admitted": False}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--expected-plan-sha256", required=True)
    parser.add_argument("--expected-extraction-receipt-sha256", required=True)
    parser.add_argument("--expected-renderer-sha256", required=True)
    parser.add_argument("--output-dir", required=True)
    args = parser.parse_args()
    result = render_captured(
        ROOT / "artifacts/goal22-runs/marker-train-overlay-pilot-plan-v2/plan.json",
        args.expected_plan_sha256,
        ROOT / "artifacts/goal22-runs/marker-train-overlay-pilot-extract-v1/source-receipt.json",
        args.expected_extraction_receipt_sha256,
        args.expected_renderer_sha256,
        ROOT / args.output_dir,
    )
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
