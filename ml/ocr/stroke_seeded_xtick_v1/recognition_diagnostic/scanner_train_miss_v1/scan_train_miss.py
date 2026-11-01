# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train-only, source-disjoint diagnostic for missed ink-scan tick proposals.

Proposal generation sees rendered panel pixels, panel bounds and source IDs only.
Truth annotations are loaded after the fit proposals have been scored and the
pixel-only cluster-width rule is frozen; validation labels are loaded last.
"""

from __future__ import annotations

from collections import Counter, defaultdict
from hashlib import sha256
from pathlib import Path
import json
import math
import sys
import time
from typing import Any
import uuid

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))

# Imports use a unique project-local bytecode prefix. This prevents an existing
# user cache from substituting old code while preserving normal module imports.
PY_CACHE = ROOT / f"tools/gpu-training/.state/scan-train-miss-pycache-{uuid.uuid4().hex}"
sys.pycache_prefix = str(PY_CACHE)

from PIL import Image

from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as admission
from ml.ocr.stroke_seeded_xtick_v1 import materialize
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1 import curate_train_proposals as curation

V6_REL = Path("artifacts/stroke-seeded-xtick-v1-train-verified-v6")
V6_MANIFEST_REL = V6_REL / "manifest.json"
V6_MANIFEST_SHA = "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a"
CURATION_REL = Path("artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2")
CURATION_MANIFEST_SHA = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
SCANNER_DLL_REL = Path("ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1/bin/Release/net10.0-windows/ShortTickInkPrototype.dll")
OUTPUT_REL = Path("artifacts/stroke-seeded-xtick-v1-scan-train-miss-v2")
MAX_WIDTH_CANDIDATES = (8, 12, 16)
SPLIT_DOMAIN = "stroke-seeded-xtick-v1-scan-train-miss-split-v1"
FIT_SELECTION_RULE = "maximize fit F1 among widths whose fit precision is >= baseline precision - 0.10; tie by higher precision, then smaller width"

PINNED_SOURCES = {
    "ml/ocr/stroke_seeded_xtick_v1/materialize.py": "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/train_proposal_curation_v1/curate_train_proposals.py": "7e8a32dda6b5cf74f013db0992bd66ed894301dca21cf94ea32f64f1275c32df",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py": "b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de",
    "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py": "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5",
    "ml/ocr/fragmented_xtick_detector_v2/materialize.py": "16b48f1a59385181f59b2245fc9ff53816093c55ade4fdc40b2f5bd9eb19a3b7",
    "ml/synthetic/renderer.py": "c49c070afc505f92bfff7ff55b22e669c3ec2b76b56ff807b72a026ca0535283",
    "ml/synthetic/templates.py": "f59733c5f0589bd1b9143d3c9ae83015b4c53ab593de229d56e46d9f11ec2f1c",
    "ml/synthetic/fonts.py": "c4ec7a2333e0410d4c49cc41c154d3b80a2898c5682a135c25e1162a806d58fc",
    "ml/synthetic/schema.py": "237d9296f2f72a11bb7020c2abac022026f72a56e4e0eac2d322f23a3e6c8acb",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1/Program.cs": "40b104c23f0b60ca029dbe596dbd46117211d4f32b13837679380e4da47a8780",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1/ShortTickInkPrototype.csproj": "7a6f7599b67f6e09425cc8168c894e0f2263f66e91bb2c254f593409a5ec3632",
    str(SCANNER_DLL_REL): "7c77eaa5c1949d5f7e22c35b32d4751e24c6a2bed7e8664b77a50b032ebaa831",
}


class DiagnosticError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def read_pinned(path: Path, expected_sha: str, limit: int) -> bytes:
    try:
        relative = path.relative_to(ROOT)
    except ValueError:
        raise DiagnosticError(f"input escapes project: {path}")
    bound = admission._bound_read(ROOT, relative.as_posix(), f"pinned diagnostic input {relative.as_posix()}", limit)
    if not 0 < len(bound.value) <= limit or bound.sha256 != expected_sha:
        raise DiagnosticError(f"pinned input hash/size mismatch: {path}")
    return bound.value


def group_components(records: list[dict[str, Any]], source_ids: list[str],
                     full_panel_hashes: dict[str, str] | None = None) -> tuple[list[list[str]], list[dict[str, Any]]]:
    """Join groups connected by exact crop hashes and report semantic conflicts."""
    parent = {source_id: source_id for source_id in source_ids}

    def find(x: str) -> str:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a: str, b: str) -> None:
        a, b = find(a), find(b)
        if a != b:
            parent[b] = a

    groups_by_hash: dict[str, set[str]] = defaultdict(set)
    kinds_by_hash: dict[str, set[str]] = defaultdict(set)
    for row in records:
        groups_by_hash[row["image_sha256"]].add(row["source_id"])
        kinds_by_hash[row["image_sha256"]].add(row["kind"])
    for groups in groups_by_hash.values():
        ordered = sorted(groups)
        for other in ordered[1:]:
            union(ordered[0], other)
    if full_panel_hashes is not None:
        if set(full_panel_hashes) != set(source_ids):
            raise DiagnosticError("full-panel hash inventory differs from admitted source IDs")
        groups_by_panel_hash: dict[str, list[str]] = defaultdict(list)
        for source_id, panel_hash in full_panel_hashes.items():
            groups_by_panel_hash[panel_hash].append(source_id)
        for groups in groups_by_panel_hash.values():
            ordered = sorted(groups)
            for other in ordered[1:]:
                union(ordered[0], other)
    components: dict[str, list[str]] = defaultdict(list)
    for source_id in source_ids:
        components[find(source_id)].append(source_id)
    conflicts = [{"image_sha256": image_sha, "kinds": sorted(kinds),
                  "source_ids": sorted(groups_by_hash[image_sha]),
                  "occurrences": sum(r["image_sha256"] == image_sha for r in records)}
                 for image_sha, kinds in sorted(kinds_by_hash.items()) if len(kinds) > 1]
    return [sorted(v) for v in components.values()], conflicts


def freeze_split(records: list[dict[str, Any]], source_ids: list[str], holdout_fraction: float = 0.20,
                 full_panel_hashes: dict[str, str] | None = None) -> dict[str, Any]:
    components, conflicts = group_components(records, source_ids, full_panel_hashes)
    conflict_hashes = {row["image_sha256"] for row in conflicts}
    positive_rows = Counter(r["source_id"] for r in records
                            if r["kind"] == "positive" and r["image_sha256"] not in conflict_hashes)
    ordered = sorted((digest((SPLIT_DOMAIN + "|" + "|".join(groups)).encode("utf-8")), groups)
                     for groups in components)
    target = math.ceil(sum(positive_rows.values()) * holdout_fraction)
    holdout: list[list[str]] = []
    held_positive = 0
    for component_hash, groups in ordered:
        if held_positive >= target:
            break
        holdout.append(groups)
        held_positive += sum(positive_rows[g] for g in groups)
    validation = sorted(g for component in holdout for g in component)
    fit = sorted(set(source_ids) - set(validation))
    if not fit or not validation or set(fit) & set(validation):
        raise DiagnosticError("deterministic source-group split is empty or overlapping")
    val_components = [c for c in components if set(c).issubset(validation)]
    fit_components = [c for c in components if set(c).issubset(fit)]
    if len(val_components) != len(holdout) or len(fit_components) + len(val_components) != len(components):
        raise DiagnosticError("source hash component crosses the frozen split")
    return {
            "algorithm": SPLIT_DOMAIN + ": union crop hashes and full-panel hashes; sort components by SHA256(domain|sorted-source-ids), then add whole components until ceil(20% of nonconflicted positive rows) is reached",
        "fit_source_ids": fit, "validation_source_ids": validation,
        "fit_components": fit_components, "validation_components": val_components,
        "fit_positive_rows": sum(positive_rows[g] for g in fit),
        "validation_positive_rows": sum(positive_rows[g] for g in validation),
        "total_positive_rows": sum(positive_rows.values()),
        "semantic_image_hash_conflicts": conflicts,
        "semantic_conflict_rows_quarantined_from_split_class_counts": sum(x["occurrences"] for x in conflicts),
        "positive_rows_quarantined_by_semantic_conflict": sum(r["kind"] == "positive" and r["image_sha256"] in conflict_hashes for r in records),
        "component_count": len(components),
    }


def _round_pixel(value: float) -> int:
    # Python and C# both use IEEE ties-to-even for Math.Round/round.
    return int(round(value))


def scan_panel(gray: Image.Image, axis_full: list[float], panel_bounds: list[int], max_cluster_width: int) -> list[dict[str, Any]]:
    """Pixel-only reference implementation of the frozen C# scan with width override."""
    left, top, right, bottom = panel_bounds
    axis = [axis_full[0] - left, axis_full[1] - top, axis_full[2] - left, axis_full[3] - top]
    dx, dy = axis[2] - axis[0], axis[3] - axis[1]
    length = math.hypot(dx, dy)
    if not math.isfinite(length) or length <= 8:
        return []
    ux, uy = dx / length, dy / length
    if ux < 0:
        ux, uy = -ux, -uy
    nx, ny = -uy, ux
    if ny < 0:
        nx, ny = -nx, -ny
    panel = gray.crop((left, top, right, bottom)).convert("L")
    px = panel.load()
    hits: list[tuple[int, int, int]] = []
    along = 4
    while along <= length - 4:
        dark_count = longest_run = run = 0
        previous_dark = False
        for offset in range(-12, 13):
            if abs(offset) <= 2:
                run = 0
                previous_dark = False
                continue
            x = _round_pixel(axis[0] + ux * along + nx * offset)
            y = _round_pixel(axis[1] + uy * along + ny * offset)
            dark = 0 <= x < panel.width and 0 <= y < panel.height and px[x, y] < 200
            if dark:
                dark_count += 1
                run = run + 1 if previous_dark else 1
                longest_run = max(longest_run, run)
            else:
                run = 0
            previous_dark = dark
        if dark_count >= 3 and longest_run >= 2:
            hits.append((along, dark_count, longest_run))
        along += 1
    clusters: list[list[tuple[int, int, int]]] = []
    for hit in hits:
        if not clusters or hit[0] - clusters[-1][-1][0] > 1:
            clusters.append([])
        clusters[-1].append(hit)
    proposals = []
    for cluster in clusters:
        width = cluster[-1][0] - cluster[0][0] + 1
        if width > max_cluster_width:
            continue
        along = cluster[len(cluster) // 2][0]
        dark_offsets = []
        for offset in range(-12, 13):
            if abs(offset) <= 2:
                continue
            x = _round_pixel(axis[0] + ux * along + nx * offset)
            y = _round_pixel(axis[1] + uy * along + ny * offset)
            if 0 <= x < panel.width and 0 <= y < panel.height and px[x, y] < 200:
                dark_offsets.append(offset)
        if len(dark_offsets) < 3:
            continue
        start_offset, end_offset = min(0, min(dark_offsets)), max(0, max(dark_offsets))
        if end_offset - start_offset < 4:
            continue
        start = [axis[0] + ux * along + nx * start_offset + left,
                 axis[1] + uy * along + ny * start_offset + top]
        end = [axis[0] + ux * along + nx * end_offset + left,
               axis[1] + uy * along + ny * end_offset + top]
        proposals.append({"center": [(start[0] + end[0]) / 2, (start[1] + end[1]) / 2],
                          "line": [start[0], start[1], end[0], end[1]],
                          "cluster_width_pixels": width,
                          "dark_pixel_count": sum(h[1] for h in cluster),
                          "longest_dark_run": max(h[2] for h in cluster)})
    return proposals


def _panel_metrics(truths: list[dict[str, Any]], proposals: list[dict[str, Any]]) -> dict[str, Any]:
    assignments = curation.assign_proposals(proposals, truths)
    tp = sum(row["state"] == "unique_visible_tick" for row in assignments)
    fp = sum(row["state"] == "no_ground_truth_tick_nearby" for row in assignments)
    ambiguous = sum(row["state"] == "ambiguous" for row in assignments)
    visible_labels = sum(bool(row["ground_truth_target_unambiguous"]) for row in truths)
    matched_visible_labels = sum(row["state"] == "unique_visible_tick" and row["truth_index"] is not None
        and bool(truths[row["truth_index"]]["ground_truth_target_unambiguous"]) for row in assignments)
    return {"truths": len(truths), "visible_text_target_truths": visible_labels,
            "matched_visible_text_targets": matched_visible_labels,
            "missed_visible_text_targets": visible_labels - matched_visible_labels,
            "proposals": len(proposals), "true_matches": tp,
            "unmatched_proposals": fp, "ambiguous_proposals": ambiguous,
            "precision": tp / len(proposals) if proposals else 0.0,
            "recall": tp / len(truths) if truths else 0.0}


def _aggregate(rows: list[dict[str, Any]]) -> dict[str, Any]:
    t = visible_text = matched_text = p = tp = fp = ambiguous = 0
    no_truth_panel_props = 0
    uncertain_panels = 0
    per_source: dict[str, Counter[str]] = defaultdict(Counter)
    for row in rows:
        m = row["metrics"]
        t += m["truths"]; visible_text += m["visible_text_target_truths"]
        matched_text += m["matched_visible_text_targets"]
        p += m["proposals"]; tp += m["true_matches"]
        fp += m["unmatched_proposals"]; ambiguous += m["ambiguous_proposals"]
        if m["truths"] == 0:
            no_truth_panel_props += m["proposals"]
        if row["needs_review"]:
            uncertain_panels += 1
        c = per_source[row["source_id"]]
        for key in ("truths", "visible_text_target_truths", "matched_visible_text_targets",
                    "missed_visible_text_targets", "proposals", "true_matches", "unmatched_proposals", "ambiguous_proposals"):
            c[key] += m[key]
    return {"visible_tick_truths": t, "visible_text_target_truths": visible_text,
            "matched_visible_text_targets": matched_text,
            "missed_visible_text_targets": visible_text - matched_text,
            "visible_text_target_recall": matched_text / visible_text if visible_text else 0.0,
            "proposals": p, "true_matches": tp,
            "unmatched_proposals": fp, "ambiguous_proposals": ambiguous,
            "precision": tp / p if p else 0.0, "recall": tp / t if t else 0.0,
            "proposals_on_panels_without_visible_ticks": no_truth_panel_props,
            "panels_without_visible_ticks_with_proposals": sum(1 for r in rows if r["metrics"]["truths"] == 0 and r["metrics"]["proposals"]),
            "uncertain_panels": uncertain_panels,
            "per_source": {source: dict(values) for source, values in sorted(per_source.items())}}


def _load_annotations(source_id: str, manifest_records: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rows = []
    for ref in manifest_records:
        if ref["source_id"] != source_id:
            continue
        payload = read_pinned(ROOT / V6_REL / ref["annotation_path"], ref["annotation_sha256"], 65_536)
        annotation = json.loads(payload)
        if (annotation.get("split") != "train" or annotation.get("source_id") != source_id
                or annotation.get("source_identity_sha256") != ref["source_identity_sha256"]
                or annotation.get("case_id") != ref["case_id"]):
            raise DiagnosticError("train annotation identity/split mismatch")
        rows.append(annotation)
    return rows


def _truths_for_panel(annotations: list[dict[str, Any]], rendered_panel: dict[str, Any], image: Image.Image,
                      panel_index: int) -> tuple[list[dict[str, Any]], int]:
    ticks = {tick["tick_id"]: tick for tick in rendered_panel["ticks"]
             if tick.get("axis", "x") == "x" and tick.get("role") == "x_tick"}
    result = []
    positive_text_visible = 0
    gray = image.convert("L")
    for annotation in annotations:
        if annotation["source_panel_index"] != panel_index:
            continue
        tick = ticks.get(annotation["tick_id"])
        if tick is None:
            raise DiagnosticError("pinned train annotation tick is absent from exact rendered panel")
        if annotation["source_sha256"] != digest(image.tobytes()):
            raise DiagnosticError("annotation source raster hash differs from reconstructed panel")
        line = annotation["stroke_line_original_xyxy"]
        center = annotation["stroke_center_original_xy"]
        visible = bool(tick.get("visible")) and curation._sample_tick_ink(gray, line)
        if visible and annotation["kind"] == "positive":
            positive_text_visible += 1
        if visible:
            result.append({"tick_id": annotation["tick_id"], "stroke_center_original_xy": center,
                           "stroke_line_original_xyxy": line,
                           "expected_tick_label": annotation.get("expected_tick_label"),
                           "ground_truth_target_unambiguous": annotation["kind"] == "positive",
                           "visible": True})
    return result, positive_text_visible


def _source_bindings() -> dict[str, str]:
    result = {}
    for rel, expected in PINNED_SOURCES.items():
        payload = read_pinned(ROOT / rel, expected, 100_000_000)
        result[rel] = digest(payload)
    return result


def _truth_blind_inputs(source_ids: list[str], source_map: dict[str, dict[str, Any]],
                        raster_hashes: dict[str, dict[str, Any]]) -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]]]:
    inputs, data = [], {}
    for source_id in source_ids:
        source = source_map[source_id]
        recipe = curation._verify_recipe_identity(source)
        scene = materialize.build_train_scene(recipe)
        image, rendered, _ = materialize.render_train_scene(scene)
        full_pixel_sha = digest(image.tobytes())
        receipt = raster_hashes.get(source_id)
        if (receipt is None or full_pixel_sha != receipt["reconstructed_full_panel_pixel_sha256"]
                or source["source_identity_sha256"] != receipt["source_identity_sha256"]
                or (image.width, image.height) != (receipt["width"], receipt["height"])):
            raise DiagnosticError(f"source raster failed its prior train-only pixel receipt: {source_id}")
        source_bound = {"source_id": source_id, "source_pixel_sha256": full_pixel_sha}
        # Only IDs, bounds and pixels enter the scanner request. No annotations, text labels or truth geometry.
        scan_input = common.build_source_input(source_bound, [], rendered["panels"], image)
        inputs.append(scan_input)
        data[source_id] = {"image": image, "rendered": rendered, "input": scan_input,
                           "source_pixel_sha256": full_pixel_sha}
    return inputs, data


def _run_scanner(inputs: list[dict[str, Any]], dll: Path) -> list[dict[str, Any]]:
    return curation._scanner_process(dll, inputs)


def proposal_counts(records: list[dict[str, Any]]) -> Counter[str]:
    return Counter(str(row["source_id"]) for row in records)


def _assert_curation_parity(source_ids: list[str], outputs: list[dict[str, Any]],
                            data: dict[str, dict[str, Any]], curation_records: list[dict[str, Any]]) -> None:
    """Compare baseline proposals to the earlier frozen train-only scanner output."""
    by_source = {row["source_id"]: row for row in outputs}
    refs_by_source: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for ref in curation_records:
        if ref["source_id"] in source_ids:
            refs_by_source[ref["source_id"]].append(ref)
    for source_id in source_ids:
        output = by_source.get(source_id)
        if output is None or "error" in output:
            raise DiagnosticError(f"missing/failed baseline scanner source {source_id}")
        expected_panel_ids = {p["panel_id"] for p in data[source_id]["rendered"]["panels"]}
        error = curation.scanner_result_error(output, source_id, expected_panel_ids)
        if error:
            raise DiagnosticError(f"baseline scanner response failed contract for {source_id}: {error}")
        expected_png_sha = data[source_id]["input"]["image_png_sha256"]
        if output.get("png_sha256") != expected_png_sha:
            raise DiagnosticError(f"baseline scanner raster response hash mismatch for {source_id}")
        panels = {p["panel_id"]: p for p in output["panels"]}
        expected: dict[str, dict[int, dict[str, Any]]] = {panel_id: {} for panel_id in panels}
        for ref in refs_by_source[source_id]:
            raw = read_pinned(ROOT / CURATION_REL / ref["annotation_path"], ref["annotation_sha256"], 65_536)
            annotation = json.loads(raw)
            if annotation["source_id"] != source_id or annotation["split"] != "train":
                raise DiagnosticError("curation annotation escaped the pinned train source")
            expected.setdefault(annotation["source_panel_id"], {})[int(annotation["proposal_index"])] = annotation
        if set(expected) != set(panels):
            raise DiagnosticError(f"curation/baseline panel inventory mismatch for {source_id}")
        for panel_id, panel in panels.items():
            refs = expected[panel_id]
            actual = panel["ink_proposals"]
            if set(refs) != set(range(len(actual))):
                raise DiagnosticError(f"curation/baseline proposal index mismatch for {source_id}/{panel_id}")
            for index, proposal in enumerate(actual):
                annotation = refs[index]
                for actual_key, annotation_key in (("center", "proposal_center_original_xy"),
                                                   ("line", "proposal_line_original_xyxy")):
                    a, b = proposal[actual_key], annotation[annotation_key]
                    if len(a) != len(b) or max(abs(float(x) - float(y)) for x, y in zip(a, b)) > 1e-6:
                        raise DiagnosticError(f"baseline coordinate parity mismatch {source_id}/{panel_id}/{index}")


def _assert_python_baseline_parity(source_ids: list[str], data: dict[str, dict[str, Any]],
                                   outputs: list[dict[str, Any]]) -> None:
    by_source = {row["source_id"]: row for row in outputs}
    for source_id in source_ids:
        image = data[source_id]["image"]
        rendered = data[source_id]["rendered"]
        for panel_output in by_source[source_id]["panels"]:
            index = int(panel_output["panel_index"])
            panel = rendered["panels"][index]
            bounds = common.transformed_panel_bounds(panel, image.size)
            actual = scan_panel(image, panel_output["selected_x_axis_line"], bounds, 8)
            expected = panel_output["ink_proposals"]
            if len(actual) != len(expected):
                raise DiagnosticError(f"Python/C# baseline proposal count mismatch {source_id}/{panel_output['panel_id']}")
            keys = (("center", "center"), ("line", "line"), ("cluster_width_pixels", "cluster_width_pixels"),
                    ("dark_pixel_count", "dark_pixel_count"), ("longest_dark_run", "longest_dark_run"))
            for position, (left, right) in enumerate(zip(actual, expected)):
                for actual_key, expected_key in keys:
                    av, ev = left[actual_key], right[expected_key]
                    if isinstance(av, list):
                        if len(av) != len(ev) or max(abs(float(a) - float(b)) for a, b in zip(av, ev)) > 1e-6:
                            raise DiagnosticError(f"Python/C# baseline geometry mismatch {source_id}/{panel_output['panel_id']}/{position}")
                    elif av != ev:
                        raise DiagnosticError(f"Python/C# baseline field mismatch {source_id}/{panel_output['panel_id']}/{position}/{actual_key}")


def _score_group(source_ids: list[str], data: dict[str, dict[str, Any]], outputs: list[dict[str, Any]],
                 manifest_records: list[dict[str, Any]], width: int) -> tuple[dict[str, Any], dict[str, Any]]:
    by_output = {row["source_id"]: row for row in outputs}
    report_rows = []
    for source_id in source_ids:
        source_output = by_output.get(source_id)
        if source_output is None or "error" in source_output:
            raise DiagnosticError(f"cannot score failed/missing scanner source: {source_id}")
        source_data = data[source_id]
        image, rendered = source_data["image"], source_data["rendered"]
        annotations = _load_annotations(source_id, manifest_records)
        for panel_output in source_output["panels"]:
            panel_index = int(panel_output["panel_index"])
            panel_id = panel_output["panel_id"]
            panel = rendered["panels"][panel_index]
            if panel["panel_id"] != panel_id:
                raise DiagnosticError("scanner panel order/identity mismatch")
            bounds = common.transformed_panel_bounds(panel, image.size)
            truths, _ = _truths_for_panel(annotations, panel, image, panel_index)
            axis = panel_output["selected_x_axis_line"]
            proposals = scan_panel(image, axis, bounds, width)
            metrics = _panel_metrics(truths, proposals)
            report_rows.append({"source_id": source_id, "panel_id": panel_id,
                "panel_index": panel_index, "needs_review": bool(panel_output["needs_review"]),
                "uncertainty_reasons": panel_output["uncertainty_reasons"], "metrics": metrics})
    return _aggregate(report_rows), {"panel_count": len(report_rows), "rows": report_rows}


def preflight() -> dict[str, Any]:
    bindings = _source_bindings()
    v6_bytes = read_pinned(ROOT / V6_MANIFEST_REL, V6_MANIFEST_SHA, 5_000_000)
    v6 = json.loads(v6_bytes)
    curation_bytes = read_pinned(ROOT / CURATION_REL / "manifest.json", CURATION_MANIFEST_SHA, 2_000_000)
    curation_manifest = json.loads(curation_bytes)
    if (v6.get("schema") != "graphreader.stroke-seeded-xtick-v1-train.v1" or v6.get("split") != "train"
            or v6.get("probe_dev_sealed_private_inputs_read") is not False
            or v6.get("generator_sha256") != bindings["ml/ocr/stroke_seeded_xtick_v1/materialize.py"]):
        raise DiagnosticError("V6 input is not the pinned train-only corpus")
    if curation_manifest.get("input_manifest_sha256") != V6_MANIFEST_SHA or curation_manifest.get("split") != "train":
        raise DiagnosticError("curation pixel-hash receipt is not bound to pinned V6 train manifest")
    admitted = curation_manifest.get("input_admission", {})
    if any(admitted.get(name) is not expected for name, expected in (
            ("only_v6_train_manifest_read", True), ("dev_input_read", False),
            ("sealed_input_read", False), ("private_input_read", False), ("probe_input_read", False))):
        raise DiagnosticError("curation receipt reports an out-of-scope input")
    records = v6["records"]
    if any(r.get("split") != "train" for r in records):
        raise DiagnosticError("non-train source row in V6 manifest")
    sources = v6["sources"]
    source_map = {r["source_id"]: r for r in sources}
    if len(source_map) != len(sources) or len(sources) != 24:
        raise DiagnosticError("V6 train source inventory mismatch")
    raster_hashes = {r["source_id"]: r for r in curation_manifest["source_groups"]}
    if set(raster_hashes) != set(source_map):
        raise DiagnosticError("prior train-only full-raster receipt inventory mismatch")
    split = freeze_split(records, sorted(source_map),
        full_panel_hashes={source_id: row["reconstructed_full_panel_pixel_sha256"]
                           for source_id, row in raster_hashes.items()})
    failed_receipts = {row["source_id"]: row["error"] for row in curation_manifest["scanner_failed_sources"]}
    # Preserve the previously recorded scanner failure; do not replay that source.
    failed_ids = sorted(failed_receipts)
    if failed_ids != ["stroke-seeded-xtick-v1-train-27103111"]:
        raise DiagnosticError("known scanner failure inventory changed")
    positives = Counter(r["source_id"] for r in records if r["kind"] == "positive")
    if split["component_count"] != 18 or split["total_positive_rows"] != 235 or split["validation_positive_rows"] != 56:
        raise DiagnosticError("frozen source split disagrees with audited train-only counts")
    admission._checked_chain(ROOT, ROOT / "artifacts", "scan diagnostic output parent")
    return {"source_code_sha256": bindings, "v6_manifest_bytes": v6_bytes,
        "curation_manifest_bytes": curation_bytes,
        "curation_manifest": curation_manifest, "records": records,
        "source_map": source_map, "raster_hashes": raster_hashes,
        "split": split, "failed_receipts": failed_receipts,
        "train_positive_rows_by_source": dict(positives)}


def run() -> dict[str, Any]:
    started = time.perf_counter()
    checked = preflight()
    bindings = checked["source_code_sha256"]
    v6_bytes, curation_bytes = checked["v6_manifest_bytes"], checked["curation_manifest_bytes"]
    curation_manifest = checked["curation_manifest"]
    records, source_map = checked["records"], checked["source_map"]
    raster_hashes, split = checked["raster_hashes"], checked["split"]
    failed_receipts = checked["failed_receipts"]
    failed_ids = sorted(failed_receipts)
    output_root = ROOT / OUTPUT_REL
    if output_root.exists() or output_root.is_symlink() or output_root.parent != ROOT / "artifacts":
        raise DiagnosticError("output must be a fresh direct child of the project artifacts directory")
    admission._checked_chain(ROOT, output_root.parent, "scan diagnostic output parent")
    output_root.mkdir(parents=False)
    (output_root / "started.json").write_text(json.dumps({"schema": "graphreader.scan-train-miss-started.v1",
        "v6_manifest_sha256": digest(v6_bytes), "curation_manifest_sha256": digest(curation_bytes),
        "source_code_sha256": bindings, "split_sha256": digest(json.dumps(split, sort_keys=True, separators=(",", ":")).encode()),
        "source_ids": split["fit_source_ids"] + split["validation_source_ids"],
        "training_performed": False, "dev_sealed_private_inputs_read": False},
        sort_keys=True, separators=(",", ":")), encoding="utf-8")
    fit_ids = [sid for sid in split["fit_source_ids"] if sid not in failed_ids]
    val_ids = [sid for sid in split["validation_source_ids"] if sid not in failed_ids]

    # Proposal generation is truth-blind, and source partitions stay isolated.
    fit_inputs, fit_data = _truth_blind_inputs(fit_ids, source_map, raster_hashes)
    fit_outputs = _run_scanner(fit_inputs, ROOT / SCANNER_DLL_REL)
    if len(fit_outputs) != len(fit_inputs) or any("error" in r for r in fit_outputs):
        raise DiagnosticError("fit baseline scanner pass failed; source errors are not negative rows")
    _assert_curation_parity(fit_ids, fit_outputs, fit_data, curation_manifest["records"])
    _assert_python_baseline_parity(fit_ids, fit_data, fit_outputs)
    # Compare every accepted default proposal to the already pinned curation
    # output count before enabling the Python width variants.
    curation_counts = proposal_counts(curation_manifest["records"])
    for output in fit_outputs:
        proposal_count = sum(len(panel["ink_proposals"]) for panel in output["panels"])
        if proposal_count != curation_counts[output["source_id"]]:
            raise DiagnosticError(f"frozen C# baseline parity count mismatch: {output['source_id']}")
    fit_scored = {}
    for width in MAX_WIDTH_CANDIDATES:
        metrics, details = _score_group(fit_ids, fit_data, fit_outputs, records, width)
        fit_scored[width] = {"metrics": metrics, "details": details}
    base_precision = fit_scored[8]["metrics"]["precision"]
    eligible = [w for w in MAX_WIDTH_CANDIDATES if fit_scored[w]["metrics"]["precision"] >= base_precision - 0.10]
    frozen_width = max(eligible, key=lambda w: (
        _f1(fit_scored[w]["metrics"]["precision"], fit_scored[w]["metrics"]["recall"]),
        fit_scored[w]["metrics"]["precision"], -w)) if eligible else 8
    frozen_rule = {"schema": "graphreader.scan-train-miss-frozen-rule.v1",
        "cluster_width_pixels": frozen_width,
        "fit_source_component_split": split,
        "selection_rule": FIT_SELECTION_RULE,
        "candidate_widths": list(MAX_WIDTH_CANDIDATES),
        "fit_scores_sha256": digest(json.dumps({w:fit_scored[w]["metrics"] for w in MAX_WIDTH_CANDIDATES},
                                                 sort_keys=True,separators=(",",":")).encode()),
        "validation_truth_opened": False,
        "threshold_tuning_on_dev": False}

    # Freeze the selected rule before any validation annotation is opened.
    (output_root / "frozen_rule.json").write_text(json.dumps(frozen_rule, sort_keys=True, separators=(",", ":")), encoding="utf-8")

    # Validate once on whole held-out hash components. The failed source remains
    # recorded as a failure and is not converted to zero detections.
    val_inputs, val_data = _truth_blind_inputs(val_ids, source_map, raster_hashes)
    val_outputs = _run_scanner(val_inputs, ROOT / SCANNER_DLL_REL)
    if len(val_outputs) != len(val_inputs) or any("error" in r for r in val_outputs):
        raise DiagnosticError("validation scanner pass failed; source errors are not negative rows")
    _assert_curation_parity(val_ids, val_outputs, val_data, curation_manifest["records"])
    _assert_python_baseline_parity(val_ids, val_data, val_outputs)
    for output in val_outputs:
        src = output["source_id"]
        if sum(len(p["ink_proposals"]) for p in output["panels"]) != curation_counts.get(src, 0):
            raise DiagnosticError(f"held-out frozen C# baseline count mismatch: {src}")
    validation_baseline, validation_baseline_details = _score_group(val_ids, val_data, val_outputs, records, 8)
    validation_selected, validation_selected_details = _score_group(val_ids, val_data, val_outputs, records, frozen_width)
    for payload, name in ((fit_outputs, "fit_truth_blind_proposals.json"), (val_outputs, "validation_truth_blind_proposals.json")):
        (output_root / name).write_text(json.dumps(payload, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    report = {"schema": "graphreader.scan-train-miss-diagnostic.v1",
        "recipe_id": "train-only-source-component-fit-then-single-source-disjoint-validation-v2",
        "status": "exploratory-train-only-source-disjoint-validation",
        "input_bindings": {"v6_manifest_sha256": digest(v6_bytes),
            "curation_manifest_sha256": digest(curation_bytes),
            "source_code_sha256": bindings,
            "scanner_binary_sha256": PINNED_SOURCES[str(SCANNER_DLL_REL)],
            "v6_curation_pixel_hash_source_groups": len(raster_hashes)},
        "split": split,
        "fit_sources_scored": fit_ids,
        "validation_sources_scored": val_ids,
        "known_scanner_failures_not_replayed_or_scored": failed_receipts,
        "scanner_interface": {"truth_sent_to_detector": False,
            "fields_sent": ["full-panel raster pixels", "source ID", "panel IDs and bounds"],
            "validation_rule_frozen_before_validation_annotation_read": True},
        "fit_candidate_results": {str(w): fit_scored[w]["metrics"] for w in MAX_WIDTH_CANDIDATES},
        "frozen_rule": frozen_rule,
        "validation_baseline": validation_baseline,
        "validation_selected": validation_selected,
        "validation_per_panel_baseline": validation_baseline_details,
        "validation_per_panel_selected": validation_selected_details,
        "elapsed_seconds": round(time.perf_counter() - started, 6),
        "runtime": {"python": sys.version, "pillow": Image.__version__},
        "training_performed": False, "gpu_used": False,
        "dev_sealed_private_inputs_read": False, "production_behavior_changed": False,
        "limitations": ["Internal source-disjoint holdout is small and synthetic; it is not real-world acceptance.",
            "The pixel scan evaluates stroke proposals only, not crop-classifier or OCR recognition.",
            "A semantic crop-image hash conflict with background_only versus excluded labels is reported, joined by source component, and omitted from split-positive class counts."]}
    (output_root / "report.json").write_text(json.dumps(report, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    return report


def _f1(precision: float, recall: float) -> float:
    return 2 * precision * recall / (precision + recall) if precision + recall else 0.0


if __name__ == "__main__":
    if sys.argv[1:] == ["--preflight"]:
        checked = preflight()
        print(json.dumps({"source_code_sha256": checked["source_code_sha256"],
            "split": checked["split"], "failed_sources": checked["failed_receipts"],
            "output_path": str(ROOT / OUTPUT_REL), "training_performed": False}, indent=2))
    else:
        try:
            print(json.dumps(run(), indent=2))
        except Exception as error:
            output_root = ROOT / OUTPUT_REL
            if output_root.is_dir() and not (output_root / "report.json").exists():
                receipt = {"schema": "graphreader.scan-train-miss-failure.v1",
                    "failure_type": type(error).__name__, "failure": str(error),
                    "training_performed": False, "dev_sealed_private_inputs_read": False}
                (output_root / "failure.json").write_text(json.dumps(receipt, sort_keys=True, separators=(",", ":")), encoding="utf-8")
            raise
