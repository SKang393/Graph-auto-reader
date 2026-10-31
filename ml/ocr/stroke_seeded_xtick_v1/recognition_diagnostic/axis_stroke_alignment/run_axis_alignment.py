# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Hash-gated in-memory rerender and production OpenCV axis geometry diagnostic."""

from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time
from typing import Any

from PIL import Image

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.stroke_seeded_xtick_v1 import materialize as crop_materializer
from ml.ocr.stroke_seeded_xtick_v1 import probe_materialize

CORPUS = Path("artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3")
MANIFEST_SHA256 = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
MASK_INDEX = Path("tools/gpu-training/.state/stroke-geometry-erase-v1/glyph-mask-reconstruction-final/mask-index.json")
MASK_INDEX_SHA256 = "db101ed7657f5f2d4e2522df46736b94a672e3c4afbbaf33de5871cb23def2d4"
MATCH_RECIPE = "unique nearest-center assignment; per-truth tolerance=max(3 px, half annotated stroke length)"


class DiagnosticError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def read_pinned(path: Path, expected_sha: str, limit: int) -> bytes:
    if path.is_symlink():
        raise DiagnosticError(f"symlink input rejected: {path}")
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(ROOT.resolve()):
        raise DiagnosticError(f"input escapes project: {path}")
    payload = resolved.read_bytes()
    if not 0 < len(payload) <= limit or digest(payload) != expected_sha:
        raise DiagnosticError(f"pinned input hash/size mismatch: {path}")
    return payload


def transformed_panel_bounds(panel: dict[str, Any], image_size: tuple[int, int]) -> list[int]:
    x, y, width, height = (float(value) for value in panel["box"])
    image_width, image_height = image_size
    left, top = max(0, math.floor(x)), max(0, math.floor(y))
    right = min(image_width, math.ceil(x + width))
    bottom = min(image_height, math.ceil(y + height))
    if right <= left or bottom <= top:
        raise DiagnosticError(f"empty transformed panel box: {panel.get('panel_id')}")
    return [left, top, right, bottom]


def endpoint_error(detected: list[float], truth: list[float]) -> float:
    direct = sum((detected[i] - truth[i]) ** 2 for i in range(4))
    reverse = ((detected[0] - truth[2]) ** 2 + (detected[1] - truth[3]) ** 2 +
               (detected[2] - truth[0]) ** 2 + (detected[3] - truth[1]) ** 2)
    return math.sqrt(min(direct, reverse) / 4.0)


def match_panel(truth_rows: list[dict[str, Any]], detections: list[dict[str, Any]]) -> dict[str, Any]:
    candidates = []
    within_by_truth: dict[str, list[tuple[float, int]]] = {row["case_id"]: [] for row in truth_rows}
    for truth in truth_rows:
        cx, cy = truth["stroke_center_original_xy"]
        x0, y0, x1, y1 = truth["stroke_line_original_xyxy"]
        tolerance = max(3.0, 0.5 * math.hypot(x1 - x0, y1 - y0))
        for det_index, det in enumerate(detections):
            dx, dy = det["center"]
            distance = math.hypot(dx - cx, dy - cy)
            if distance <= tolerance:
                candidates.append((distance, truth["case_id"], det_index, tolerance))
                within_by_truth[truth["case_id"]].append((distance, det_index))
    assigned_truth: set[str] = set()
    assigned_detections: set[int] = set()
    pairs = []
    for distance, case_id, det_index, tolerance in sorted(candidates):
        if case_id in assigned_truth or det_index in assigned_detections:
            continue
        truth = next(row for row in truth_rows if row["case_id"] == case_id)
        det = detections[det_index]
        pairs.append({
            "case_id": case_id,
            "detection_index": det_index,
            "center_error_px": distance,
            "line_endpoint_rms_error_px": endpoint_error(det["line"], truth["stroke_line_original_xyxy"]),
            "match_tolerance_px": tolerance,
            "kind": truth["kind"],
        })
        assigned_truth.add(case_id)
        assigned_detections.add(det_index)
    return {
        "truth_count": len(truth_rows),
        "usable_detected_x_stroke_count": len(detections),
        "matched_count": len(pairs),
        "unmatched_truth_count": len(truth_rows) - len(assigned_truth),
        "unmatched_detected_count": len(detections) - len(assigned_detections),
        "truths_with_multiple_detections_within_tolerance": sum(len(items) > 1 for items in within_by_truth.values()),
        "mean_center_error_px": (sum(p["center_error_px"] for p in pairs) / len(pairs) if pairs else None),
        "p50_center_error_px": percentile([p["center_error_px"] for p in pairs], 0.50),
        "p90_center_error_px": percentile([p["center_error_px"] for p in pairs], 0.90),
        "max_center_error_px": max((p["center_error_px"] for p in pairs), default=None),
        "mean_line_endpoint_rms_error_px": (sum(p["line_endpoint_rms_error_px"] for p in pairs) / len(pairs) if pairs else None),
        "p50_line_endpoint_rms_error_px": percentile([p["line_endpoint_rms_error_px"] for p in pairs], 0.50),
        "p90_line_endpoint_rms_error_px": percentile([p["line_endpoint_rms_error_px"] for p in pairs], 0.90),
        "matched_pairs": pairs,
    }


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    index = (len(ordered) - 1) * fraction
    low, high = math.floor(index), math.ceil(index)
    return ordered[low] if low == high else ordered[low] + (ordered[high] - ordered[low]) * (index - low)


def rasterize_segment(line: list[float]) -> list[tuple[int, int]]:
    x0, y0, x1, y1 = (int(round(float(v))) for v in line)
    dx, dy = abs(x1 - x0), abs(y1 - y0)
    sx, sy = (1 if x0 < x1 else -1), (1 if y0 < y1 else -1)
    error = dx - dy
    points = []
    while True:
        points.append((x0, y0))
        if x0 == x1 and y0 == y1:
            return points
        twice = 2 * error
        if twice > -dy:
            error -= dy
            x0 += sx
        if twice < dx:
            error += dx
            y0 += sy


def build_source_input(source: dict[str, Any], recipe_rows: list[dict[str, Any]],
                       panel_records: list[dict[str, Any]], image: Image.Image) -> dict[str, Any]:
    buffer = BytesIO()
    image.save(buffer, format="PNG", compress_level=9)
    png = buffer.getvalue()
    image_sha = digest(png)
    return {
        "source_id": source["source_id"],
        "expected_source_pixel_sha256": source["source_pixel_sha256"],
        "width": image.width,
        "height": image.height,
        "image_png_base64": __import__("base64").b64encode(png).decode("ascii"),
        "image_png_sha256": image_sha,
        "panels": [
            {
                "panel_id": panel["panel_id"],
                "panel_index": index,
                "bounds": transformed_panel_bounds(panel, image.size),
            }
            for index, panel in enumerate(panel_records)
        ],
    }


def run(root: Path, report_path: Path, dotnet_dll: Path) -> dict[str, Any]:
    started = time.perf_counter()
    manifest_bytes = read_pinned(root / CORPUS / "manifest.json", MANIFEST_SHA256, 10_000_000)
    manifest = json.loads(manifest_bytes)
    generator_bytes = Path(probe_materialize.__file__).read_bytes()
    materializer_bytes = Path(crop_materializer.__file__).read_bytes()
    if digest(generator_bytes) != manifest["generator_sha256"]:
        raise DiagnosticError("probe generator source no longer matches pinned manifest")
    if digest(materializer_bytes) != manifest["crop_materializer_sha256"]:
        raise DiagnosticError("crop materializer source no longer matches pinned manifest")

    source_by_id = {source["source_id"]: source for source in manifest["sources"]}
    manifest_rows = {row["case_id"]: row for row in manifest["records"]}
    annotations = {}
    for row in manifest["records"]:
        payload = read_pinned(root / CORPUS / row["annotation_path"], row["annotation_sha256"], 65_536)
        annotations[row["case_id"]] = json.loads(payload)
    all_truth_rows: dict[str, list[dict[str, Any]]] = {}
    source_inputs = []
    source_rerender_counts = {}
    source_hash_receipts = []
    truth_visibility_counts = {"visible": 0, "invisible": 0}
    for source_id, source in source_by_id.items():
        recipe = source["renderer_recipe"]
        scene = crop_materializer.build_train_scene(recipe)
        panel_image, rendered, _ = crop_materializer.render_train_scene(scene)
        pixel_sha = digest(panel_image.tobytes())
        if pixel_sha != source["source_pixel_sha256"]:
            raise DiagnosticError(f"reconstructed source pixel hash mismatch for {source_id}: {pixel_sha}")
        if (panel_image.width, panel_image.height) != tuple(rendered["canvas"][k] for k in ("width", "height")):
            raise DiagnosticError(f"rendered canvas dimensions disagree for {source_id}")
        for manifest_row in manifest["records"]:
            if manifest_row["source_id"] != source_id:
                continue
            if annotations[manifest_row["case_id"]]["crop_transform"]["source_image_size"] != [panel_image.width, panel_image.height]:
                raise DiagnosticError(f"annotation source dimensions mismatch for {manifest_row['case_id']}")
        if len(rendered["panels"]) != int(source["panel_count"]):
            raise DiagnosticError(f"rendered panel count differs from pinned recipe for {source_id}")
        generated_rows = crop_materializer.materialize_recipe(recipe)
        if not generated_rows:
            raise DiagnosticError(f"recipe produced no tick rows: {source_id}")
        visible_tick_by_id = {
            tick["tick_id"]: bool(tick.get("visible", False))
            for panel_record in rendered["panels"]
            for tick in panel_record.get("ticks", [])
            if tick.get("axis") == "x" and tick.get("role") == "x_tick"
        }
        for generated in generated_rows:
            generated_id = generated["case_id"]
            if generated["tick_id"] not in visible_tick_by_id:
                raise DiagnosticError(f"renderer tick record missing for {generated_id}")
            generated["source_stroke_visible"] = visible_tick_by_id[generated["tick_id"]]
            truth_visibility_counts["visible" if generated["source_stroke_visible"] else "invisible"] += 1
            source_rerender_counts[source_id] = source_rerender_counts.get(source_id, 0) + 1
            pinned_row = manifest_rows.get(generated_id)
            if pinned_row is not None:
                crop = Image.frombytes("L", (256, 96), generated["crop_pixels"])
                crop_png = BytesIO()
                crop.save(crop_png, format="PNG", compress_level=9)
                if digest(crop_png.getvalue()) != pinned_row["image_sha256"]:
                    raise DiagnosticError(f"reconstructed crop image hash mismatch: {generated_id}")
                if (generated["stroke_line_original_xyxy"] != annotations[generated_id]["stroke_line_original_xyxy"] or
                        generated["stroke_center_original_xy"] != annotations[generated_id]["stroke_center_original_xy"]):
                    raise DiagnosticError(f"rerendered annotation geometry mismatch: {generated_id}")
            all_truth_rows.setdefault(generated["source_panel_id"], []).append(generated)
        source_inputs.append(build_source_input(source, generated_rows, rendered["panels"], panel_image))
        source_hash_receipts.append({"source_id": source_id, "source_pixel_sha256": pixel_sha,
                                     "width": panel_image.width, "height": panel_image.height,
                                     "panel_count": len(rendered["panels"]),
                                     "regenerated_tick_rows": source_rerender_counts[source_id],
                                     "visible_strokes": sum(row["source_stroke_visible"] for row in generated_rows),
                                     "invisible_strokes": sum(not row["source_stroke_visible"] for row in generated_rows)})
    if sum(source_rerender_counts.values()) != manifest["candidate_crop_count_before_duplicate_exclusion"]:
        raise DiagnosticError("regenerated tick-row count differs from pinned pre-exclusion candidate count")

    process = subprocess.Popen(
        ["dotnet", str(dotnet_dll), "--stdio"],
        cwd=root,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="strict",
        bufsize=1,
    )
    assert process.stdin is not None and process.stdout is not None
    outputs = []
    try:
        for item in source_inputs:
            process.stdin.write(json.dumps(item, separators=(",", ":")) + "\n")
            process.stdin.flush()
            line = process.stdout.readline()
            if not line:
                raise DiagnosticError("axis runner exited without a source result")
            outputs.append(json.loads(line))
    finally:
        process.stdin.close()
    stderr = process.stderr.read() if process.stderr else ""
    exit_code = process.wait(timeout=60)
    if exit_code != 0:
        raise DiagnosticError(f"axis runner exit={exit_code}: {stderr[-4000:]}")
    if len(outputs) != len(source_inputs):
        raise DiagnosticError("axis runner result count differs from source count")

    panels = []
    all_matches: list[dict[str, Any]] = []
    truth_rows_flat = [row for rows in all_truth_rows.values() for row in rows]
    truth_by_id = {row["case_id"]: row for row in truth_rows_flat}
    detection_lines: dict[str, list[dict[str, Any]]] = {}
    for output in outputs:
        if output.get("status") == "source_failed":
            panels.append(output)
            continue
        source = source_by_id[output["source_id"]]
        if output["expected_source_pixel_sha256"] != source["source_pixel_sha256"]:
            raise DiagnosticError(f"runner source identity changed for {output['source_id']}")
        if output["png_sha256"] != next(item["image_png_sha256"] for item in source_inputs
                                        if item["source_id"] == output["source_id"]):
            raise DiagnosticError(f"runner image byte identity changed for {output['source_id']}")
        if [panel["panel_id"] for panel in output["panels"]] != [panel["panel_id"] for panel in
                                                                  next(item for item in source_inputs if item["source_id"] == output["source_id"])["panels"]]:
            raise DiagnosticError(f"runner panel identity differs for {output['source_id']}")
        for panel in output["panels"]:
            panel["source_id"] = output["source_id"]
            source_panel_id = panel["panel_id"]
            truth = [row for row in all_truth_rows.get(source_panel_id, []) if row["source_stroke_visible"]]
            detections = [tick for tick in panel["x_axis_ticks"] if tick["usable_for_ocr_tick_seed"]]
            metrics = match_panel(truth, detections)
            # Retain the line geometry transiently for mask-overlap measurement.
            metrics["_lines"] = [(index, det["line"]) for index, det in enumerate(detections)]
            all_matches.extend(metrics["matched_pairs"])
            detection_lines[source_panel_id] = detections
            panel["truth_count"] = len(truth)
            panel["matching"] = {key: value for key, value in metrics.items() if key != "_lines"}
            panels.append(panel)

    matching_summary = summarize_panels(panels)
    # The mask overlap pass is deliberately after all detector inferences above.
    mask_overlap = glyph_overlap_all(root, manifest, truth_by_id, panels, detection_lines)
    report = {
        "schema": "graphreader.stroke-seeded-xtick-v1-axis-stroke-alignment.v1",
        "recipe_id": "stroke-seeded-xtick-v1-consumed-probe-production-axis-geometry-v1",
        "acceptance_status": "consumed synthetic diagnostic only; not formal acceptance",
        "probe_manifest_sha256": digest(manifest_bytes),
        "probe_generator_sha256": digest(generator_bytes),
        "crop_materializer_sha256": digest(materializer_bytes),
        "axis_runner_source_sha256": digest(Path(__file__).with_name("Program.cs").read_bytes()),
        "axis_runner_project_sha256": digest(Path(__file__).with_name("AxisStrokeAlignment.csproj").read_bytes()),
        "glyph_mask_index_sha256": MASK_INDEX_SHA256,
        "production_components": {
            "raster_decoder": "GraphReader.App.Integration.Workflow.ProductionRasterFrameDecoder",
            "geometry_detector": "GraphReader.Axis.AxisGeometryDetector",
            "line_candidate_provider": "GraphReader.Axis.OpenCvLineCandidateProvider defaults (OpenCV LSD + probabilistic Hough)",
            "model_inference": False,
            "ocr_models_loaded": False,
            "coordinate_mapping": "panel crop coordinates translated by the final-raster panel-box origin into synthetic source-image pixels",
            "ocr_tick_seed_filter": "same finite/id/segment/confidence/supporting-candidate criteria as ProductionAutomaticDetectionAdapter.CreateOcrXAxisTickStrokes",
        },
        "sample_counts": {
            "source_groups": len(source_by_id),
            "source_panels": len(panels),
            "regenerated_full_source_rasters": len(source_inputs),
            "source_tick_rows_regenerated": len(truth_rows_flat),
            "visible_source_strokes": truth_visibility_counts["visible"],
            "invisible_source_strokes": truth_visibility_counts["invisible"],
            "manifest_probe_records": len(manifest["records"]),
            "duplicate_training_crop_exclusions_in_manifest": manifest["duplicate_train_image_exclusion_count"],
            "all_axis_inputs_hash_validated_before_inference": True,
            "panel_raster_files_written": False,
        },
        "verified_source_rasters": source_hash_receipts,
        "matching": {"rule": MATCH_RECIPE, **matching_summary},
        "matched_cases_by_kind": {
            kind: sum(pair["kind"] == kind for panel in panels
                      for pair in panel.get("matching", {}).get("matched_pairs", []))
            for kind in ("positive", "background_only", "excluded")
        },
        "source_group_results": summarize_sources(panels),
        "glyph_overlap": mask_overlap,
        "panel_results": panels,
        "elapsed_rerender_and_inference_seconds": round(time.perf_counter() - started, 6),
        "training_performed": False,
        "dev_sealed_private_inputs_read": False,
        "production_behavior_changed": False,
        "production_approval": False,
        "release_eligible": False,
        "limitations": [
            "The probe is already-consumed synthetic development material, not held-out acceptance.",
            "Panel detection crops use renderer-provided transformed panel boxes; no detector fit is inferred from the crop-only corpus without exact full-raster hash reproduction.",
            "Matching uses the stated diagnostic tolerance and greedy unique nearest-center pairing; it is not an acceptance threshold.",
            "Glyph overlap is available only for positive cases with the separately hash-validated 165-mask sidecar; centerlines are rasterized one pixel wide for intersection counts.",
        ],
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    return report


def glyph_overlap_all(root: Path, manifest: dict[str, Any], truth_by_id: dict[str, Any],
                      panels: list[dict[str, Any]], detections_by_panel: dict[str, list[dict[str, Any]]]) -> dict[str, Any]:
    index_bytes = read_pinned(root / MASK_INDEX, MASK_INDEX_SHA256, 8_000_000)
    index = json.loads(index_bytes)
    if index.get("probe_manifest_sha256") != MANIFEST_SHA256 or index.get("record_count") != 165:
        raise DiagnosticError("verified glyph-mask sidecar is not the expected one")
    mask_by_case = {item["case_id"]: item for item in index["records"]}
    record_by_case = {item["case_id"]: item for item in manifest["records"]}
    matched_for_panel: dict[str, list[dict[str, Any]]] = {}
    for panel in panels:
        if panel.get("status") != "ok":
            continue
        panel_id = panel["panel_id"]
        for pair in panel.get("matching", {}).get("matched_pairs", []):
            matched_for_panel.setdefault(panel_id, []).append(pair)
    total_pairs = intersect_pairs = intersect_pixels = total_pixels = 0
    for panel_id, pairs in matched_for_panel.items():
        detections = detections_by_panel[panel_id]
        for pair in pairs:
            case_id = pair["case_id"]
            truth = truth_by_id[case_id]
            if truth["kind"] != "positive" or case_id not in mask_by_case or case_id not in record_by_case:
                continue
            row = record_by_case[case_id]
            annotation_bytes = read_pinned(root / CORPUS / row["annotation_path"], row["annotation_sha256"], 65_536)
            annotation = json.loads(annotation_bytes)
            mask_row = mask_by_case[case_id]
            target = annotation["targets"]
            if len(target) != 1 or target[0]["glyph_mask_sha256"] != mask_row["full_panel_glyph_mask_sha256"]:
                raise DiagnosticError(f"glyph mask annotation binding mismatch for {case_id}")
            mask_bytes = read_pinned(root / MASK_INDEX.parent / mask_row["path"], mask_row["sha256"], 1_000_000)
            det = detections[pair["detection_index"]]
            sx, _, tx, _, sy, ty = annotation["crop_transform"]["original_to_crop_pixel"]
            mapped = [det["line"][0] * sx + tx, det["line"][1] * sy + ty,
                      det["line"][2] * sx + tx, det["line"][3] * sy + ty]
            with Image.open(BytesIO(mask_bytes)) as im:
                mask = im.convert("L")
                pixels = mask.load()
                points = rasterize_segment(mapped)
                inside = [(x, y) for x, y in points if 0 <= x < mask.width and 0 <= y < mask.height]
                hits = sum(pixels[x, y] > 0 for x, y in inside)
            total_pairs += 1
            total_pixels += len(inside)
            intersect_pixels += hits
            intersect_pairs += hits > 0
    return {
        "matched_positive_pairs_with_verified_crop_masks": total_pairs,
        "detected_lines_intersecting_glyph_mask": intersect_pairs,
        "one_pixel_detected_centerline_mask_intersection_pixels": intersect_pixels,
        "detected_centerline_pixels_inside_crop_canvas": total_pixels,
        "mask_index_sha256": MASK_INDEX_SHA256,
        "interpretation": "actual renderer-derived alpha glyph masks checked after detection; measured line is the detector's emitted segment rasterized one pixel wide",
    }


def summarize_panels(panels: list[dict[str, Any]]) -> dict[str, Any]:
    completed = [p for p in panels if p.get("status") == "ok"]
    failed = [p for p in panels if p.get("status") != "ok"]
    fields = ("truth_count", "usable_detected_x_stroke_count", "matched_count",
              "unmatched_truth_count", "unmatched_detected_count",
              "truths_with_multiple_detections_within_tolerance")
    result = {field: sum(int(p.get("matching", {}).get(field, p.get("truth_count", 0) if field == "truth_count" else 0))
                         for p in completed) for field in fields}
    centers = [pair["center_error_px"] for p in completed for pair in p["matching"]["matched_pairs"]]
    lines = [pair["line_endpoint_rms_error_px"] for p in completed for pair in p["matching"]["matched_pairs"]]
    result.update({
        "panels_succeeded": len(completed),
        "panels_failed": len(failed),
        "center_error_px_distribution": describe(centers),
        "line_endpoint_rms_error_px_distribution": describe(lines),
    })
    return result


def summarize_sources(panels: list[dict[str, Any]]) -> list[dict[str, Any]]:
    groups: dict[str, list[dict[str, Any]]] = {}
    for panel in panels:
        source_id = str(panel.get("source_id", "source_failure"))
        groups.setdefault(source_id, []).append(panel)
    output = []
    for source_id, rows in sorted(groups.items()):
        completed = [row for row in rows if row.get("status") == "ok"]
        output.append({
            "source_id": source_id,
            "panel_count": len(rows),
            "panels_succeeded": len(completed),
            "panels_failed": len(rows) - len(completed),
            "truth_count": sum(row.get("matching", {}).get("truth_count", 0) for row in completed),
            "detected_count": sum(row.get("matching", {}).get("usable_detected_x_stroke_count", 0) for row in completed),
            "matched_count": sum(row.get("matching", {}).get("matched_count", 0) for row in completed),
            "unmatched_truth_count": sum(row.get("matching", {}).get("unmatched_truth_count", 0) for row in completed),
            "unmatched_detected_count": sum(row.get("matching", {}).get("unmatched_detected_count", 0) for row in completed),
        })
    return output


def describe(values: list[float]) -> dict[str, Any]:
    return {"count": len(values), "mean": sum(values) / len(values) if values else None,
            "p50": percentile(values, .50), "p90": percentile(values, .90),
            "max": max(values) if values else None}


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: run_axis_alignment.py <repo-root> <axis-runner.dll> <report.json>", file=sys.stderr)
        return 2
    root = Path(sys.argv[1]).resolve(strict=True)
    report = run(root, Path(sys.argv[3]).resolve(), Path(sys.argv[2]).resolve(strict=True))
    print(json.dumps({"report": str(Path(sys.argv[3]).resolve()),
                      "sources": report["sample_counts"]["source_groups"],
                      "panels": report["sample_counts"]["source_panels"],
                      "matching": report["matching"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(json.dumps({"status": "failed", "error_type": type(exc).__name__, "error": str(exc)}), file=sys.stderr)
        raise
