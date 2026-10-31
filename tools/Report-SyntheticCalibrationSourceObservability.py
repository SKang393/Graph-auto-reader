#!/usr/bin/env python3
"""Create aggregate-only source-observability evidence for a fixed synthetic run.

It retains all source and panel denominators. Generator anchors are checked only
to label them as truth-only metadata, never as runtime calibration evidence.
"""

from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
import re
from pathlib import Path
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
NUMERIC = re.compile(r"^\s*[+-]?(?:\d+|\d+\.\d+)\s*$")
SESSION_ONE_WORD = re.compile(r"\bsession\s*[-:]?\s*1\b", re.IGNORECASE)


class EvidenceError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise EvidenceError(message)


def digest(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def bound_path(root: Path, ref: dict) -> Path:
    path = (root / ref["path"]).resolve()
    require(path.is_relative_to(root) and path.is_file(), "bound evidence missing or outside repository")
    require(digest(path) == ref["sha256"], f"bound evidence checksum mismatch: {path.name}")
    return path


def read_bound(root: Path, ref: dict) -> tuple[Path, dict]:
    path = bound_path(root, ref)
    return path, json.loads(path.read_text(encoding="utf-8-sig"))


def aggregate_binding(items: list[tuple[str, str, str]]) -> str:
    material = "\n".join("\t".join(row) for row in sorted(items))
    return sha256(material.encode("utf-8")).hexdigest()


def require_stage_join(entry, stage, known_source_hashes, case_image_hash):
    observation = stage.get("observation", {})
    source = stage.get("panel_source", {}).get("source", {}).get("image", {})
    require(observation.get("source_id") == entry.get("source_id"), "stage source ID does not join report")
    require(observation.get("panel_id") == entry.get("panel_id"), "stage panel ID does not join report")
    require(observation.get("ocr", {}).get("panel_id") == entry.get("panel_id"), "stage OCR panel does not join report")
    require(source.get("sha256") in known_source_hashes, "stage source hash does not join input manifest")
    require(source.get("sha256") == case_image_hash, "stage source hash does not join report case image")
    require(observation.get("ocr", {}).get("input_sha256") == stage.get("panel_source", {}).get("panel_image_sha256"), "stage OCR input does not join panel source")
    require(stage.get("synthetic_only") is True and stage.get("private_corpus_access") is False and stage.get("sealed_corpus_access") is False, "stage scope is outside public synthetic evidence")


def require_stage_binding(stage, candidate_hash, input_hash):
    require(stage.get("candidate_binding_sha256") == candidate_hash and stage.get("input_manifest_sha256") == input_hash, "stage candidate/input binding mismatch")


def decoded_size(path):
    with Image.open(path) as image:
        image.load()
        return image.size


def valid_rendered_box(box, width, height):
    return isinstance(box, list) and len(box) == 4 and all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in box) and box[2] > 0 and box[3] > 0 and box[0] >= 0 and box[1] >= 0 and box[0] + box[2] <= width and box[1] + box[3] <= height


def has_printed_session_one(x_tick_texts):
    return any(NUMERIC.match(text["text"]) and float(text["text"]) == 1.0 for text in x_tick_texts) or any(SESSION_ONE_WORD.search(text["text"]) for text in x_tick_texts)


def build(root: Path, report_path: Path, generator_path: Path) -> dict:
    report_path, generator_path = report_path.resolve(), generator_path.resolve()
    require(report_path.is_relative_to(root) and generator_path.is_relative_to(root), "inputs must remain in repository")
    report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    generator = json.loads(generator_path.read_text(encoding="utf-8-sig"))
    require(report.get("schema") == "graphreader.real-acceptance-frozen-synthetic-report.v1", "unexpected run report")
    require(generator.get("private_reads") == generator.get("sealed_reads") == 0, "generator is not public synthetic evidence")
    require(report.get("private_corpus_access") is False and report.get("sealed_corpus_access") is False, "run is outside public synthetic scope")

    run_dir = report_path.parent
    frozen = run_dir / "frozen-inputs"
    input_manifest_path = frozen / "input-manifest.json"
    candidate_binding_path = frozen / "candidate-binding.json"
    require(input_manifest_path.is_file() and candidate_binding_path.is_file(), "missing frozen binding inputs")
    require(digest(input_manifest_path) == report["input_manifest_sha256"], "input manifest hash mismatch")
    require(digest(candidate_binding_path) == report["candidate_binding_sha256"], "candidate binding hash mismatch")
    input_manifest = json.loads(input_manifest_path.read_text(encoding="utf-8-sig"))
    manifest_sources = {source["sha256"]: source for source in input_manifest["sources"]}
    require(len(manifest_sources) == report["source_count"], "input source denominator mismatch")
    case_by_id = {case["source_id"]: case for case in report["cases"]}
    case_status = {source_id: case["status"] for source_id, case in case_by_id.items()}
    require(len(case_status) == report["source_count"] == 21, "run source denominator is incomplete")
    source_status = {}
    runtime_xlabels = Counter()
    for entry in report["calibration_diagnostic_files"]:
        stage_path = run_dir / entry["path"]
        require(stage_path.is_file() and digest(stage_path) == entry["sha256"], "calibration stage checksum mismatch")
        stage = json.loads(stage_path.read_text(encoding="utf-8-sig"))
        require_stage_binding(stage, report["candidate_binding_sha256"], report["input_manifest_sha256"])
        require(entry["source_id"] in case_by_id, "stage source ID does not join report case")
        require_stage_join(entry, stage, set(manifest_sources), case_by_id[entry["source_id"]]["image_sha256"])
        source_hash = stage["panel_source"]["source"]["image"]["sha256"]
        status = case_status[entry["source_id"]]
        require(source_hash not in source_status or source_status[source_hash] == status, "source status conflict")
        source_status[source_hash] = status
        runtime_xlabels[source_hash] += sum(region.get("role", "").lower() == "xtick"
                                            for region in stage["observation"]["ocr"]["regions"])

    source_bindings = []
    counts = Counter()
    for source in generator["sources"]:
        image_path = bound_path(root, source["image"])
        _, scene = read_bound(root, source["scene"])
        _, annotation = read_bound(root, source["annotation"])
        source_hash = source["image"]["sha256"]
        manifest = manifest_sources.get(source_hash)
        require(manifest is not None, "generator source does not join input manifest")
        require(decoded_size(image_path) == (manifest["width"], manifest["height"]), "decoded image dimensions do not join manifest")
        require(scene.get("scene_id") == annotation.get("scene_id"), "scene does not join annotation")
        status = source_status.get(source_hash)
        require(status in {"completed", "failed"}, "source lacks bound runtime status")
        panels = annotation.get("panels")
        require(isinstance(panels, list) and panels, "annotation lacks complete panel list")
        texts = [text for panel in panels for text in panel.get("texts", [])
                 if text.get("visible", True) and str(text.get("text", "")).strip()]
        x_tick_texts = [text for text in texts if text.get("role") == "x_tick"]
        visible_x_strokes = [tick for panel in panels for tick in panel.get("ticks", [])
                             if tick.get("axis") == "x" and tick.get("visible", True)]
        numeric_x = [text for text in x_tick_texts if NUMERIC.match(text["text"])]
        require(all(valid_rendered_box(text.get("rendered_pixel_box"), manifest["width"], manifest["height"]) for text in x_tick_texts), "declared X label rendered box is invalid")
        x_values = {float(text["text"]) for text in numeric_x}
        printed_session_one = has_printed_session_one(x_tick_texts)
        later_printed_session = any(value > 1.0 for value in x_values)
        anchor_kinds = [anchor.get("kind") for panel in panels for anchor in panel.get("anchors", [])]
        require({"session1_y0", "sessionmax_y0"}.issubset(anchor_kinds), "generator anchor metadata incomplete")
        observability = "session1_and_later_x_label" if printed_session_one and later_printed_session else "missing_session1_or_later_x_label"
        category = f"workflow_{status}__annotation_{observability}"
        require(runtime_xlabels[source_hash] == len(x_tick_texts), "runtime XTick count differs from rendered XTick count")
        counts[("sources", category)] += 1
        counts[("panels", category)] += len(panels)
        counts[("visible_x_tick_strokes", category)] += len(visible_x_strokes)
        counts[("visible_numeric_x_labels", category)] += len(numeric_x)
        counts[("runtime_final_xtick_labels", category)] += runtime_xlabels[source_hash]
        source_bindings.append((source_hash, source["scene"]["sha256"], source["annotation"]["sha256"]))

    require(sum(value for (kind, _), value in counts.items() if kind == "sources") == 21, "source denominator changed")
    require(sum(value for (kind, _), value in counts.items() if kind == "panels") == 31, "panel denominator changed")
    return {
        "schema": "graphreader.synthetic-calibration-source-observability.v1",
        "status": "diagnostic_only_all_sources_retained",
        "run_report": {"path": str(report_path), "sha256": digest(report_path)},
        "generator_report": {"path": str(generator_path), "sha256": digest(generator_path)},
        "binding": {
            "candidate_binding_sha256": report["candidate_binding_sha256"],
            "input_manifest_sha256": report["input_manifest_sha256"],
            "candidate_binding_sha256": report["candidate_binding_sha256"],
            "public_source_scene_annotation_binding_sha256": aggregate_binding(source_bindings),
            "source_binding_count": len(source_bindings),
        },
        "denominator": {"sources": 21, "panels": 31, "excluded_sources": 0, "excluded_panels": 0},
        "observability": {category: {kind: value for (kind, current), value in counts.items() if current == category}
                           for category in sorted({category for _, category in counts})},
        "count_semantics": "SOURCE-level annotation aggregates. Runtime panel IDs are bound to report stages, but no source-annotation panel ID to runtime panel ID correspondence is asserted.",
        "diagnostic_script": {"path": "tools/Report-SyntheticCalibrationSourceObservability.py", "sha256": digest(Path(__file__).resolve())},
        "generator_anchors": {
            "classification": "truth_only_metadata",
            "statement": "session1_y0, session1_ymax, and sessionmax_y0 anchors are verified generator metadata and are never runtime OCR or automatic-calibration evidence.",
        },
        "limitations": [
            "The report retains all 21 sources and 31 panels; it does not alter acceptance gates or exclude ambiguous cases.",
            "Completed means only that the saved workflow report marked the source completed, not product or release acceptance.",
            "Missing numeric X evidence is annotation-reported visibility, not a pixel-level proof of absent rendered glyphs; hidden-anchor inference is prohibited.",
            "Workflow completion and annotation observability are reported as a cross-tab, not asserted equivalent; future detector or calibration failures remain visible.",
            "Decoded source dimensions and declared X-label rendered boxes are bound, but this report does not perform exhaustive raster text discovery.",
            "Scene and annotation files are source-bound by scene ID, but runtime panel correspondence to source annotation panel IDs is not established by this report.",
            "This is public synthetic fixed-output analysis only; no inference, training, private reads, or sealed reads occurred.",
        ],
        "private_reads": 0, "sealed_reads": 0, "inference_rerun": False, "optimizer_steps": 0,
        "production_approved": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-report", type=Path)
    parser.add_argument("--generator-report", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        require(not any((args.run_report, args.generator_report, args.output)), "self-test accepts no paths")
        stage = {"synthetic_only": True, "private_corpus_access": False, "sealed_corpus_access": False, "candidate_binding_sha256": "c", "input_manifest_sha256": "i", "observation": {"source_id": "s", "panel_id": "p", "ocr": {"panel_id": "p", "input_sha256": "i"}}, "panel_source": {"source": {"image": {"sha256": "h"}}, "panel_image_sha256": "i"}}
        require_stage_join({"source_id": "s", "panel_id": "p"}, stage, {"h"}, "h")
        require_stage_binding(stage, "c", "i")
        for bad in ({"source_id": "wrong", "panel_id": "p"}, {"source_id": "s", "panel_id": "wrong"}):
            try:
                require_stage_join(bad, stage, {"h"}, "h")
            except EvidenceError:
                pass
            else:
                raise AssertionError("mismatched source/panel join must fail")
        try:
            require_stage_join({"source_id": "s", "panel_id": "p"}, {**stage, "private_corpus_access": True}, {"h"}, "h")
        except EvidenceError:
            pass
        else:
            raise AssertionError("private stage scope must fail")
        try:
            require_stage_binding(stage, "wrong", "i")
        except EvidenceError:
            pass
        else:
            raise AssertionError("candidate mismatch must fail")
        non_axis = [{"role": "annotation", "text": "Session 1"}]
        require(not has_printed_session_one([text for text in non_axis if text["role"] == "x_tick"]), "non-axis Session 1 cannot be X evidence")
        require(has_printed_session_one([{"role": "x_tick", "text": "Session 1"}]), "XTick Session 1 must remain evidence")
        require(valid_rendered_box([0, 0, 1, 1], 1, 1) and not valid_rendered_box([0, 0, 2, 1], 1, 1), "rendered box validation")
        print(json.dumps({"status": "pass", "checks": 8}))
        return
    require(all((args.run_report, args.generator_report, args.output)), "run report, generator report, and output are required")
    output = args.output.resolve()
    require(output.is_relative_to(ROOT) and not output.exists(), "output must be a new repository-local file")
    result = build(ROOT, args.run_report, args.generator_report)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "denominator": result["denominator"]}))


if __name__ == "__main__":
    main()
