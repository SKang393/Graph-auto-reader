# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Truth-blind ink proposals with conservative train-only curation labels."""

from __future__ import annotations

from collections import Counter
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path, PurePosixPath
import subprocess
import sys
import time
from typing import Any

from PIL import Image, ImageFont

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as admission
from ml.ocr.stroke_seeded_xtick_v1 import materialize
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common

TRAIN_ROOT_REL = "artifacts/stroke-seeded-xtick-v1-train-verified-v6"
TRAIN_MANIFEST_REL = f"{TRAIN_ROOT_REL}/manifest.json"
TRAIN_MANIFEST_SHA256 = "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a"
SCANNER_PROJECT = ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1/ShortTickInkPrototype.csproj"
SCANNER_DLL = SCANNER_PROJECT.parent / "bin/Release/net10.0-windows/ShortTickInkPrototype.dll"
OUTPUT = ROOT / "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2"
MAX_SOURCES, MAX_PANELS, MAX_PROPOSALS = 24, 72, 10_000
MAX_SOURCE_BYTES = 100_000_000


class CurationError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _box_intersects(a: tuple[float, float, float, float], b: tuple[float, float, float, float]) -> bool:
    return a[0] < b[2] and a[2] > b[0] and a[1] < b[3] and a[3] > b[1]


def _fully_inside(box: tuple[float, float, float, float], window: tuple[float, float, float, float]) -> bool:
    return window[0] <= box[0] and box[2] <= window[2] and window[1] <= box[1] and box[3] <= window[3]


def _window(transform: dict[str, Any]) -> tuple[float, float, float, float]:
    x, y, width, height = transform["source_window_xywh"]
    return float(x), float(y), float(x + width), float(y + height)


def _center_tolerance(truth: dict[str, Any]) -> float:
    x0, y0, x1, y1 = truth["stroke_line_original_xyxy"]
    return max(3.0, 0.5 * math.hypot(x1 - x0, y1 - y0))


def assign_proposals(proposals: list[dict[str, Any]], truths: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Assign only one-to-one geometric matches; any overlap with hidden or duplicate truth is review-only."""
    edges: list[list[int]] = []
    reverse: dict[int, list[int]] = {index: [] for index in range(len(truths))}
    for proposal_index, proposal in enumerate(proposals):
        px, py = proposal["center"]
        nearby = []
        for truth_index, truth in enumerate(truths):
            tx, ty = truth["stroke_center_original_xy"]
            if math.hypot(px - tx, py - ty) <= _center_tolerance(truth):
                nearby.append(truth_index)
                reverse[truth_index].append(proposal_index)
        edges.append(nearby)
    result = []
    for proposal_index, nearby in enumerate(edges):
        if not nearby:
            result.append({"state": "no_ground_truth_tick_nearby", "truth_index": None, "reason": None})
        elif len(nearby) != 1:
            result.append({"state": "ambiguous", "truth_index": None, "reason": "proposal_near_multiple_ticks"})
        elif len(reverse[nearby[0]]) != 1:
            result.append({"state": "ambiguous", "truth_index": None, "reason": "multiple_proposals_near_one_tick"})
        elif not truths[nearby[0]]["visible"]:
            result.append({"state": "ambiguous", "truth_index": None, "reason": "near_nonvisible_tick_geometry"})
        else:
            result.append({"state": "unique_visible_tick", "truth_index": nearby[0], "reason": None})
    return result


def classify_candidate(match: dict[str, Any], *, expected_label: str | None,
                       intended_region_id: str | None,
                       xtick_glyphs_intersecting: list[dict[str, Any]],
                       non_xtick_text_intersects: bool,
                       intended_glyph_complete: bool) -> tuple[str, list[str]]:
    """Positive requires a unique tick and its sole complete glyph; negative requires proven text-free geometry."""
    reasons: list[str] = []
    if match["state"] == "unique_visible_tick":
        if expected_label is None or intended_region_id is None:
            reasons.append("intended_xtick_glyph_unavailable")
        if len(xtick_glyphs_intersecting) != 1:
            reasons.append("neighboring_or_multiple_xtick_glyphs_intersect_crop")
        elif intended_region_id is not None and xtick_glyphs_intersecting[0]["region_id"] != intended_region_id:
            reasons.append("intersecting_xtick_glyph_is_not_intended_label")
        if not intended_glyph_complete:
            reasons.append("intended_xtick_glyph_missing_or_clipped")
        if non_xtick_text_intersects:
            reasons.append("non_xtick_text_intersects_crop")
        return ("positive", []) if not reasons else ("excluded", reasons)
    if match["state"] != "no_ground_truth_tick_nearby":
        return "excluded", [match["reason"] or "ambiguous_tick_assignment"]
    if xtick_glyphs_intersecting:
        reasons.append("xtick_text_intersects_unmatched_crop")
    if non_xtick_text_intersects:
        reasons.append("non_xtick_text_intersects_crop")
    return ("background_only", []) if not reasons else ("excluded", reasons)


def _sample_tick_ink(gray: Image.Image, line: list[float]) -> bool:
    x0, y0, x1, y1 = (float(v) for v in line)
    length = math.hypot(x1 - x0, y1 - y0)
    if not math.isfinite(length) or length < 2:
        return False
    hits = 0
    for fraction in (0.30, 0.45, 0.60, 0.75):
        x = int(round(x0 + (x1 - x0) * fraction))
        y = int(round(y0 + (y1 - y0) * fraction))
        xlo, xhi = max(0, x - 1), min(gray.width, x + 2)
        ylo, yhi = max(0, y - 1), min(gray.height, y + 2)
        if xlo < xhi and ylo < yhi and gray.crop((xlo, ylo, xhi, yhi)).getextrema()[0] < 245:
            hits += 1
    return hits >= 2


def _mapped_geometry(row: dict[str, Any]) -> tuple[list[float], list[float]]:
    # materialize_recipe has already mapped nested tick geometry to final raster pixels.
    center = [float(v) for v in row["stroke_center_original_xy"]]
    line = [float(v) for v in row["stroke_line_original_xyxy"]]
    return center, line


def _glyph_geometry(rendered: dict[str, Any], image: Image.Image) -> dict[str, list[dict[str, Any]]]:
    final_forward = rendered["transforms"][-1]["forward_matrix_3x3"] if rendered.get("transforms") else None
    if final_forward is not None and (len(final_forward) != 9 or not all(math.isfinite(float(n)) for n in final_forward)
            or abs(final_forward[6]) > 1e-12 or abs(final_forward[7]) > 1e-12
            or abs(final_forward[8]) < 1e-12
            or abs(final_forward[0] * final_forward[4] - final_forward[1] * final_forward[3]) < 1e-12):
        raise CurationError("non-affine/ambiguous final renderer transform is not admitted")
    font_info = rendered["font"]
    font_path = Path(font_info["resolved_path"])
    if font_path.is_symlink() or not font_path.resolve().is_relative_to(ROOT.resolve()):
        raise CurationError("font resolver produced an unsafe or external project font")
    font_bytes = font_path.read_bytes()
    if digest(font_bytes) != font_info["sha256"]:
        raise CurationError("rendered font bytes changed")
    font = ImageFont.truetype(str(font_path), int(font_info["size_px"]))
    output: dict[str, list[dict[str, Any]]] = {}
    for panel in rendered["panels"]:
        glyphs = []
        for record in panel["texts"]:
            if not record.get("visible") or not record.get("rendered_pixel_box"):
                continue
            mask = materialize._glyph_alpha_mask(record, font, image.size)
            if final_forward is not None:
                mask = materialize._warp_mask(mask, final_forward)
            bounds = mask.getbbox()
            if bounds is None:
                continue
            box = tuple(float(v) for v in bounds)
            x, y, width, height = (float(v) for v in record["rendered_pixel_box"])
            expected_box = (x, y, x + width, y + height)
            if record.get("role") == "x_tick":
                if final_forward is None:
                    if any(abs(a - b) > 1e-6 for a, b in zip(box, expected_box)):
                        raise CurationError("XTick actual glyph mask differs from renderer bounds")
                else:
                    expected_box = materialize._apply_box(final_forward, expected_box)
                    if any(abs(a - b) > 5.0 for a, b in zip(box, expected_box)):
                        raise CurationError("warped XTick glyph mask differs from transformed renderer bounds")
            glyphs.append({"region_id": str(record["region_id"]), "text": str(record["text"]),
                           "role": str(record["role"]), "box": [float(v) for v in box],
                           "glyph_pixel_count": sum(value > 0 for value in mask.tobytes()),
                           "glyph_mask_sha256": digest(mask.tobytes())})
        output[panel["panel_id"]] = glyphs
    return output


def _verify_recipe_identity(source: dict[str, Any]) -> dict[str, Any]:
    recipe = source.get("recipe")
    if not isinstance(recipe, dict) or recipe.get("split") != "train" or recipe.get("split_role") != "train":
        raise CurationError("V6 source recipe is not explicitly train-only")
    if source.get("split") != "train" or recipe.get("source_id") != source.get("source_id"):
        raise CurationError("V6 source ID or split identity mismatch")
    identity = recipe.get("source_identity_sha256")
    identity_payload = dict(recipe)
    identity_payload.pop("source_identity_sha256", None)
    if identity != source.get("source_identity_sha256") or digest(canonical_json(identity_payload)) != identity:
        raise CurationError("V6 deterministic source recipe identity mismatch")
    return recipe


def _verify_v6_materialization(source: dict[str, Any], rows: list[dict[str, Any]],
                               manifest_records: dict[str, dict[str, Any]], corpus_root: Path) -> dict[str, int]:
    counts = Counter()
    if len(rows) != int(source["crop_count"]):
        raise CurationError(f"renderer crop count mismatch for {source['source_id']}")
    for materialized in rows:
        case_id = materialized["case_id"]
        ref = manifest_records.get(case_id)
        if ref is None or ref["source_id"] != source["source_id"]:
            raise CurationError(f"re-rendered row is absent from pinned V6 index: {case_id}")
        row = dict(materialized)
        pixel_bytes = row.pop("crop_pixels")
        image = Image.frombytes("L", (materialize.INPUT_WIDTH, materialize.INPUT_HEIGHT), pixel_bytes)
        buffer = BytesIO()
        image.save(buffer, format="PNG", compress_level=9)
        image_bytes = buffer.getvalue()
        row["image_path"] = f"images/{case_id}.png"
        row["image_sha256"] = digest(image_bytes)
        row["annotation_path"] = f"annotations/{case_id}.json"
        annotation_bytes = canonical_json(row)
        annotation_sha = digest(annotation_bytes)
        if row["image_sha256"] != ref["image_sha256"] or annotation_sha != ref["annotation_sha256"]:
            raise CurationError(f"V6 train crop/annotation cannot be reproduced: {case_id}")
        if (ref["kind"] != row["kind"] or ref["trainable_single_box"] != row["trainable_single_box"]
                or ref["source_identity_sha256"] != row["source_identity_sha256"] or ref["split"] != "train"):
            raise CurationError(f"V6 train label identity mismatch: {case_id}")
        annotation_bound = admission._bound_read(corpus_root, row["annotation_path"],
                                                 f"V6 train annotation {case_id}", 65_536)
        if annotation_bound.sha256 != annotation_sha or annotation_bound.value != annotation_bytes:
            raise CurationError(f"V6 train annotation bytes do not match rerendered truth: {case_id}")
        counts[row["kind"]] += 1
    return dict(counts)


def _panel_truth(rows: list[dict[str, Any]], rendered_panel: dict[str, Any], image: Image.Image,
                 glyphs: list[dict[str, Any]]) -> list[dict[str, Any]]:
    tick_records = {tick["tick_id"]: tick for tick in rendered_panel["ticks"]
                    if tick.get("axis") == "x" and tick.get("role") == "x_tick"}
    result = []
    gray = image.convert("L")
    for row in rows:
        tick = tick_records.get(row["tick_id"])
        if tick is None:
            raise CurationError("materializer XTick identity is missing from the rendered panel")
        center, line = _mapped_geometry(row)
        target_ids = [target["text_region_id"] for target in row["targets"]
                      if target["text"] == row["expected_tick_label"]]
        result.append({"tick_id": row["tick_id"], "stroke_center_original_xy": center,
                       "stroke_line_original_xyxy": line,
                       "expected_tick_label": row["expected_tick_label"],
                       "intended_region_id": target_ids[0] if len(target_ids) == 1 else None,
                       "ground_truth_target_unambiguous": row["kind"] == "positive" and len(target_ids) == 1,
                       "source_visible_flag": bool(tick.get("visible")),
                       "raster_stroke_ink_verified": _sample_tick_ink(gray, line),
                       "visible": bool(tick.get("visible")) and _sample_tick_ink(gray, line),
                       "glyphs": glyphs})
    return result


def _curate_one(proposal: dict[str, Any], match: dict[str, Any], truths: list[dict[str, Any]],
                all_glyphs: list[dict[str, Any]], image: Image.Image) -> dict[str, Any]:
    center_x, center_y = (float(v) for v in proposal["center"])
    crop, transform = materialize._crop_and_resize(image, center_x, center_y - 2.0)
    window = _window(transform)
    touching_xticks = [glyph for glyph in all_glyphs if glyph["role"] == "x_tick"
                       and _box_intersects(tuple(glyph["box"]), window)]
    touching_other_text = any(glyph["role"] != "x_tick" and _box_intersects(tuple(glyph["box"]), window)
                              for glyph in all_glyphs)
    target = truths[match["truth_index"]] if match["state"] == "unique_visible_tick" else None
    label = target["expected_tick_label"] if target else None
    region_id = target["intended_region_id"] if target else None
    glyph = next((item for item in all_glyphs if region_id is not None and item["region_id"] == region_id), None)
    glyph_complete = bool(glyph is not None and _fully_inside(tuple(glyph["box"]), window))
    kind, reasons = classify_candidate(match, expected_label=label, intended_region_id=region_id,
        xtick_glyphs_intersecting=touching_xticks, non_xtick_text_intersects=touching_other_text,
        intended_glyph_complete=glyph_complete)
    targets = []
    if kind == "positive" and glyph is not None:
        mapped = materialize._map_box(tuple(glyph["box"]), transform)
        if mapped is None:
            raise CurationError("complete source glyph could not map into its admitted crop")
        targets = [{"text": label, "text_region_id": region_id,
                    "glyph_mask_xyxy": list(glyph["box"]),
                    "target_crop_xyxy": mapped,
                    "glyph_pixel_count": glyph["glyph_pixel_count"],
                    "glyph_mask_sha256": glyph["glyph_mask_sha256"]}]
    return {"kind": kind, "trainable_single_box": kind in {"positive", "background_only"},
            "exclusion_reasons": reasons, "matched_tick_id": target["tick_id"] if target and kind == "positive" else None,
            "expected_tick_label": label if kind == "positive" else None,
            "crop": crop, "crop_transform": transform, "targets": targets,
            "matching_state": match["state"], "matching_reason": match["reason"]}


def _scanner_process(dll: Path, inputs: list[dict[str, Any]]) -> list[dict[str, Any]]:
    source_ids = [str(item["source_id"]) for item in inputs]
    def failures(reason: str) -> list[dict[str, Any]]:
        return [{"source_id": source_id, "error": reason} for source_id in source_ids]
    try:
        self_test = subprocess.run(["dotnet", str(dll), "--self-test"], cwd=ROOT,
            capture_output=True, text=True, encoding="utf-8", errors="strict", check=False, timeout=30)
    except Exception as error:
        return failures(f"scanner self-test could not execute: {type(error).__name__}: {error}")
    expected = {"blank_axis": 0, "horizontal_marker": 0, "thin_horizontal_grid": 0,
                "thick_axis_border_confuser": 1, "crossing_gridline_confuser": 1,
                "nearby_text_stroke_confuser": 1, "short_tick": 1}
    try:
        actual = json.loads(self_test.stdout)
    except json.JSONDecodeError as error:
        return failures(f"scanner self-test returned malformed JSON: {error}")
    if self_test.returncode != 0 or actual != expected:
        return failures(f"scanner self-test failed: exit={self_test.returncode}, result={actual}, stderr={self_test.stderr[-1000:]}")
    try:
        proc = subprocess.Popen(["dotnet", str(dll), "--stdio"], cwd=ROOT,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="strict", bufsize=1)
    except Exception as error:
        return failures(f"scanner process could not start: {type(error).__name__}: {error}")
    assert proc.stdin and proc.stdout
    outputs_by_id: dict[str, dict[str, Any]] = {}
    try:
        for source_index, item in enumerate(inputs):
            source_id = source_ids[source_index]
            try:
                proc.stdin.write(json.dumps(item, separators=(",", ":")) + "\n")
                proc.stdin.flush()
                line = proc.stdout.readline()
                if not line:
                    reason = "scanner process exited before returning a source result"
                    outputs_by_id[source_id] = {"source_id": source_id, "error": reason}
                    for remaining_id in source_ids[source_index + 1:]:
                        outputs_by_id[remaining_id] = {"source_id": remaining_id, "error": reason}
                    break
                parsed = json.loads(line)
                if not isinstance(parsed, dict):
                    raise ValueError("source result is not a JSON object")
                if parsed.get("source_id") != source_id:
                    outputs_by_id[source_id] = {"source_id": source_id,
                        "error": f"scanner returned unexpected source ID {parsed.get('source_id')!r}"}
                else:
                    outputs_by_id[source_id] = parsed
            except Exception as error:
                outputs_by_id[source_id] = {"source_id": source_id,
                    "error": f"scanner source protocol failure: {type(error).__name__}: {error}"}
                for remaining_id in source_ids[source_index + 1:]:
                    outputs_by_id[remaining_id] = {"source_id": remaining_id,
                        "error": "scanner source protocol failed before this source was processed"}
                break
    finally:
        try:
            proc.stdin.close()
        except OSError:
            pass
    stderr = proc.stderr.read() if proc.stderr else ""
    try:
        exit_code = proc.wait(timeout=120)
    except Exception as error:
        exit_code = None
        process_failure = f"scanner process did not terminate: {type(error).__name__}: {error}"
    else:
        process_failure = f"scanner process exited with {exit_code}: {stderr[-2000:]}" if exit_code != 0 else None
    if process_failure:
        return failures(process_failure)
    for source_id in source_ids:
        outputs_by_id.setdefault(source_id, {"source_id": source_id, "error": "scanner returned no source result"})
    return [outputs_by_id[source_id] for source_id in source_ids]


def scanner_result_error(result: dict[str, Any], source_id: str,
                         expected_panel_ids: set[str]) -> str | None:
    if result.get("source_id") != source_id:
        return "scanner returned an unexpected source ID"
    if "error" in result:
        return str(result["error"])
    panels = result.get("panels")
    if not isinstance(panels, list) or {panel.get("panel_id") for panel in panels if isinstance(panel, dict)} != expected_panel_ids:
        return "scanner result has a missing/malformed panel inventory"
    if any(not isinstance(panel.get("ink_proposals"), list) for panel in panels):
        return "scanner result has a malformed panel proposal payload"
    return None


def run(output: Path = OUTPUT, dll: Path = SCANNER_DLL) -> dict[str, Any]:
    started = time.perf_counter()
    phase = "manifest-admission"
    if output.exists() or output.is_symlink() or output.parent != ROOT / "artifacts":
        raise CurationError("output must be a fresh direct child of the project artifacts directory")
    admission._checked_chain(ROOT, ROOT / "artifacts", "curation output parent")
    if output.resolve(strict=False).is_relative_to((ROOT / TRAIN_ROOT_REL).resolve()):
        raise CurationError("curation output cannot overlap its V6 input corpus")
    if not dll.is_file() or dll.is_symlink() or not dll.resolve().is_relative_to(SCANNER_PROJECT.parent.resolve()):
        raise CurationError("truth-blind scanner DLL is missing or outside its project directory")
    manifest_bound = admission._bound_read(ROOT, TRAIN_MANIFEST_REL, "pinned V6 train-only manifest", 5_000_000)
    if manifest_bound.sha256 != TRAIN_MANIFEST_SHA256:
        raise CurationError("pinned V6 train manifest hash changed")
    manifest = admission._parse_json(manifest_bound, "V6 train-only manifest")
    if (manifest.get("schema") != "graphreader.stroke-seeded-xtick-v1-train.v1" or manifest.get("split") != "train"
            or manifest.get("source_count") != MAX_SOURCES or manifest.get("record_count") != 681
            or manifest.get("generator_sha256") != digest(Path(materialize.__file__).read_bytes())
            or manifest.get("probe_dev_sealed_private_inputs_read") is not False):
        raise CurationError("V6 train-only manifest contract or materializer source changed")
    sources = manifest["sources"]
    records = manifest["records"]
    if len(sources) != MAX_SOURCES or len(records) != 681:
        raise CurationError("V6 manifest source or row inventory changed")
    manifest_records = {row["case_id"]: row for row in records}
    if len(manifest_records) != len(records):
        raise CurationError("V6 manifest case IDs are not unique")
    source_ids = {source["source_id"] for source in sources}
    if len(source_ids) != MAX_SOURCES or any(row.get("split") != "train" or row.get("source_id") not in source_ids for row in records):
        raise CurationError("V6 manifest contains a non-train or unknown source row")
    if {row["kind"] for row in records} != {"positive", "background_only", "excluded"}:
        raise CurationError("V6 manifest class inventory changed")
    expected_counts = {"positive": 235, "background_only": 409, "excluded": 37}
    actual_counts = dict(Counter(row["kind"] for row in records))
    if actual_counts != expected_counts:
        raise CurationError("V6 manifest class counts changed")
    corpus_root = ROOT / TRAIN_ROOT_REL
    output.mkdir(parents=False, exist_ok=False)
    (output / "images").mkdir()
    (output / "annotations").mkdir()
    (output / "started.json").write_bytes(canonical_json({"status": "started", "recipe_id": "truth-blind-inkscan-v6-train-curation-v1",
        "manifest_sha256": manifest_bound.sha256, "started_utc_epoch_seconds": time.time(),
        "training_performed": False, "probe_input_read": False, "dev_sealed_private_input_read": False}))
    try:
        phase = "v6-label-and-raster-reconstruction"
        source_work = []
        source_pixel_receipts = []
        reconstructed_v6_counts = Counter()
        for source in sources:
            recipe = _verify_recipe_identity(source)
            rows = materialize.materialize_recipe(recipe)
            reconstructed_v6_counts.update(_verify_v6_materialization(source, rows, manifest_records, corpus_root))
            scene = materialize.build_train_scene(recipe)
            image, rendered, _ = materialize.render_train_scene(scene)
            if image.width * image.height > MAX_SOURCE_BYTES or len(image.tobytes()) > MAX_SOURCE_BYTES:
                raise CurationError("rendered train source exceeds the configured raster bound")
            if len(rendered["panels"]) != int(source["panel_count"]):
                raise CurationError("renderer panel count disagrees with pinned V6 recipe")
            first_font = rows[0]["rendered_font"] if rows else None
            if first_font != source.get("font_provenance") or not first_font or first_font["source"] != "project_local":
                raise CurationError("rendered source font does not match V6 project-local font receipt")
            glyph_by_panel = _glyph_geometry(rendered, image)
            rows_by_panel: dict[str, list[dict[str, Any]]] = {}
            for row in rows:
                rows_by_panel.setdefault(row["source_panel_id"], []).append(row)
            truths_by_panel = {}
            panels_by_id = {panel["panel_id"]: panel for panel in rendered["panels"]}
            for panel_id, panel_rows in rows_by_panel.items():
                truths_by_panel[panel_id] = _panel_truth(panel_rows, panels_by_id[panel_id], image,
                                                         glyph_by_panel[panel_id])
            source_hash = digest(image.tobytes())
            source_pixel_receipts.append({"source_id": source["source_id"],
                "source_identity_sha256": source["source_identity_sha256"],
                "reconstructed_full_panel_pixel_sha256": source_hash,
                "full_panel_pixel_hash_was_pinned_by_v6_manifest": False,
                "width": image.width, "height": image.height,
                "panel_count": len(rendered["panels"])})
            source_work.append({"source": source, "image": image, "rendered": rendered,
                                "rows_by_panel": rows_by_panel, "truths_by_panel": truths_by_panel,
                                "glyph_by_panel": glyph_by_panel})

        phase = "truth-blind-scan-input"
        scanner_inputs = []
        by_source_id = {}
        for work in source_work:
            source, image, rendered = work["source"], work["image"], work["rendered"]
            source_sha = next(row["reconstructed_full_panel_pixel_sha256"] for row in source_pixel_receipts
                              if row["source_id"] == source["source_id"])
            scan_source = {"source_id": source["source_id"], "source_pixel_sha256": source_sha}
            scanner_inputs.append(common.build_source_input(scan_source, [], rendered["panels"], image))
            by_source_id[source["source_id"]] = work
        phase = "truth-blind-scan"
        scan_outputs = _scanner_process(dll.resolve(), scanner_inputs)
        if len(scan_outputs) != len(scanner_inputs):
            raise CurationError("scanner result count does not cover all admitted source groups")
        for result in scan_outputs:
            if result.get("source_id") not in by_source_id:
                raise CurationError("scanner result returned an unknown source ID")
            expected_panel_ids = {panel["panel_id"] for panel in by_source_id[result["source_id"]]["rendered"]["panels"]}
            payload_error = scanner_result_error(result, result["source_id"], expected_panel_ids)
            if payload_error:
                result["error"] = payload_error
                continue
            expected_png = next(item["image_png_sha256"] for item in scanner_inputs if item["source_id"] == result["source_id"])
            if result["png_sha256"] != expected_png:
                result["error"] = "scanner image transport hash mismatch"
                continue
        successful_scan_outputs = [result for result in scan_outputs if "error" not in result]
        failed_scan_outputs = [result for result in scan_outputs if "error" in result]
        proposal_count = sum(len(panel["ink_proposals"]) for result in successful_scan_outputs for panel in result["panels"])
        if proposal_count > MAX_PROPOSALS:
            raise CurationError("scan proposal count exceeds the configured bound")

        phase = "post-scan-ground-truth-curation"
        output_records = []
        source_summaries = []
        exclusion_counts = Counter()
        global_counts = Counter()
        for source_result in scan_outputs:
            if "error" in source_result:
                source = by_source_id[source_result["source_id"]]["source"]
                source_summaries.append({"source_id": source["source_id"],
                    "source_identity_sha256": source["source_identity_sha256"],
                    "source_group_id": source["source_id"], "status": "scanner-failed",
                    "proposal_count": None, "positive_count": None,
                    "background_only_count": None, "excluded_count": None,
                    "scanner_failure": source_result["error"]})
                continue
            work = by_source_id[source_result["source_id"]]
            source = work["source"]
            source_id = source["source_id"]
            image = work["image"]
            panel_rows_for_source = []
            per_source_counts = Counter()
            panel_index_by_id = {panel["panel_id"]: index for index, panel in enumerate(work["rendered"]["panels"])}
            for scanned_panel in source_result["panels"]:
                panel_id = scanned_panel["panel_id"]
                truths = work["truths_by_panel"].get(panel_id, [])
                proposals = scanned_panel["ink_proposals"]
                assignments = assign_proposals(proposals, truths)
                glyphs = work["glyph_by_panel"][panel_id]
                for proposal_index, (proposal, assignment) in enumerate(zip(proposals, assignments)):
                    curation = _curate_one(proposal, assignment, truths, glyphs, image)
                    kind = curation["kind"]
                    global_counts[kind] += 1
                    per_source_counts[kind] += 1
                    for reason in curation["exclusion_reasons"]:
                        exclusion_counts[reason] += 1
                    case_id = f"{source_id}-p{panel_index_by_id[panel_id]}-scan{proposal_index:04d}"
                    crop = curation.pop("crop")
                    crop_pixels = crop.tobytes()
                    buffer = BytesIO()
                    crop.save(buffer, format="PNG", compress_level=9)
                    image_bytes = buffer.getvalue()
                    image_path = f"images/{case_id}.png"
                    annotation_path = f"annotations/{case_id}.json"
                    annotation = {
                        "schema": "graphreader.stroke-seeded-xtick-v1-inkscan-curated-crop.v1",
                        "case_id": case_id, "source_id": source_id,
                        "source_group_id": source_id,
                        "source_identity_sha256": source["source_identity_sha256"],
                        "source_panel_id": panel_id, "source_panel_index": panel_index_by_id[panel_id],
                        "split": "train", "proposal_index": proposal_index,
                        "proposal_center_original_xy": [float(v) for v in proposal["center"]],
                        "proposal_line_original_xyxy": [float(v) for v in proposal["line"]],
                        "kind": kind, "trainable_single_box": curation["trainable_single_box"],
                        "matching_state": curation["matching_state"], "matching_reason": curation["matching_reason"],
                        "matched_tick_id": curation["matched_tick_id"],
                        "expected_tick_label": curation["expected_tick_label"],
                        "exclusion_reasons": curation["exclusion_reasons"],
                        "crop_transform": curation["crop_transform"], "targets": curation["targets"],
                        "image_path": image_path, "image_sha256": digest(image_bytes),
                        "annotation_path": annotation_path,
                    }
                    annotation_bytes = canonical_json(annotation)
                    annotation["annotation_sha256"] = digest(annotation_bytes)
                    if len(image_bytes) > 1_000_000 or len(annotation_bytes) > 65_536:
                        raise CurationError("one curated crop exceeded its bounded output size")
                    (output / image_path).write_bytes(image_bytes)
                    (output / annotation_path).write_bytes(annotation_bytes)
                    output_records.append({key: annotation[key] for key in (
                        "case_id", "source_id", "source_group_id", "source_identity_sha256", "source_panel_id",
                        "split", "kind", "trainable_single_box", "image_path", "image_sha256",
                        "annotation_path", "annotation_sha256")})
                    panel_rows_for_source.append({"case_id": case_id, "kind": kind,
                        "matching_state": curation["matching_state"],
                        "exclusion_reasons": curation["exclusion_reasons"]})
            source_summaries.append({"source_id": source_id,
                "source_identity_sha256": source["source_identity_sha256"],
                "source_group_id": source_id,
                "status": "scanned",
                "proposal_count": sum(len(p["ink_proposals"]) for p in source_result["panels"]),
                "positive_count": per_source_counts["positive"],
                "background_only_count": per_source_counts["background_only"],
                "excluded_count": per_source_counts["excluded"]})
        if len(output_records) != proposal_count:
            raise CurationError("curated output rows do not account for every scanner proposal")
        if sum(reconstructed_v6_counts.values()) != 681:
            raise CurationError("reconstructed V6 source rows do not account for the train manifest")
        (output / "manifest.json").write_bytes(canonical_json({
            "schema": "graphreader.stroke-seeded-xtick-v1-inkscan-curated-train.v1",
            "recipe_id": "truth-blind-short-ink-scan-v6-train-curation-v1",
            "split": "train", "status": "curated-untrained-exploratory-corpus",
            "input_manifest_path": TRAIN_MANIFEST_REL, "input_manifest_sha256": manifest_bound.sha256,
            "materializer_sha256": digest(Path(materialize.__file__).read_bytes()),
            "curator_sha256": digest(Path(__file__).read_bytes()),
            "scanner_project_sha256": digest(SCANNER_PROJECT.read_bytes()),
            "scanner_source_sha256": digest((SCANNER_PROJECT.parent / "Program.cs").read_bytes()),
            "scanner_binary_sha256": digest(dll.read_bytes()),
            "status": "completed-with-source-failures" if failed_scan_outputs else "curated-untrained-exploratory-corpus",
            "source_group_count": len(sources), "source_groups": source_pixel_receipts,
            "source_pixel_hash_limitation": "V6 manifest pins recipes and crop bytes but not full-panel raster bytes; the rerendered full-panel SHA is recorded per group after verifying every original V6 crop and annotation byte-for-byte.",
            "renderer_recipe_count": len(sources), "reconstructed_v6_class_counts": dict(reconstructed_v6_counts),
            "scan_proposal_count": proposal_count, "curated_counts": dict(global_counts),
            "scanner_failed_source_count": len(failed_scan_outputs),
            "scanner_failed_sources": [{"source_id": item["source_id"],
                "source_identity_sha256": by_source_id[item["source_id"]]["source"]["source_identity_sha256"],
                "error": item["error"]} for item in failed_scan_outputs],
            "exclusion_reason_counts": dict(exclusion_counts), "source_group_counts": source_summaries,
            "records": output_records,
            "label_policy": {
                "positive": "Only when one ink proposal has a unique one-to-one distance match to a rendered tick marked visible and ink-verified, V6 ground truth identifies exactly one intended XTick glyph, that glyph is fully inside the proposed crop, no other XTick glyph intersects, and no non-XTick text intersects.",
                "background_only": "Only when the proposal is outside every visible or hidden tick matching tolerance and the source crop intersects no XTick glyph or non-XTick text.",
                "excluded": "Any tick ambiguity, hidden geometry, duplicate proposal assignment, absent/clipped/neighbor glyph, intersecting adjacent text, or renderer/raster disagreement.",
                "tick_match_tolerance": "max(3 px, half the rendered tick segment length)",
                "proposal_crop": "Existing V6 materializer crop: 48x48 source pixels, center x at proposal center and top y at proposal center y minus 2 px, aspect-preserving pad to 256x96.",
            },
            "input_admission": {"only_v6_train_manifest_read": True, "probe_input_read": False,
                "dev_input_read": False, "sealed_input_read": False, "private_input_read": False,
                "source_pixel_hashes_reconstructed": len(source_pixel_receipts),
                "all_v6_crop_image_and_annotation_hashes_reproduced": True},
            "training_performed": False, "optimizer_steps": 0,
            "production_behavior_changed": False, "production_approval": False, "release_eligible": False,
            "elapsed_seconds": round(time.perf_counter() - started, 6),
        }))
        return {"output": str(output.relative_to(ROOT)), "records": len(output_records),
                "counts": dict(global_counts), "elapsed_seconds": round(time.perf_counter() - started, 6)}
    except Exception as error:
        (output / "failure.json").write_bytes(canonical_json({"status": "failed", "phase": phase,
            "error_type": type(error).__name__, "error": str(error),
            "input_manifest_sha256": manifest_bound.sha256,
            "curator_sha256": digest(Path(__file__).read_bytes()), "training_performed": False,
            "probe_input_read": False, "dev_sealed_private_input_read": False}))
        raise


if __name__ == "__main__":
    result = run()
    print(json.dumps(result, sort_keys=True, indent=2))
