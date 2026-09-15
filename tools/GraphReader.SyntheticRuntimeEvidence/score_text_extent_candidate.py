# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Use the frozen V2 OCR metric with saved train text and unchanged dev truth."""
from __future__ import annotations

import argparse
from hashlib import sha256
import json
from pathlib import Path
import time

import score_full_ocr_candidate_v2 as metric
from ml.ocr.official_bakeoff import text_extent_head_inputs as bridge
from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.io import png_bytes

METRIC_SHA = "8b879664c33e83f2aaec0e637f4f0f4df0f9701fe04ac43867672dde2e662656"
ROOT = Path(__file__).resolve().parents[2]


def _read(root, descriptor):
    return bridge._read_json(root/descriptor["path"], descriptor["sha256"], root, "scoring evidence")[1]


def _saved_train_truth(document, authenticated):
    expected = {truth.truth_id: truth for truth in authenticated.source_truths}
    result, seen = [], set()
    for row in document["truths"]:
        truth = expected.get(row["truth_id"])
        if (truth is None or row["truth_id"] in seen or row["source_sha256"] != truth.source_sha256
                or tuple(row["source_box_ltrb"]) != tuple(truth.source_box)
                or row["role"] != truth.role or not isinstance(row["text"], str) or not row["text"].strip()):
            raise metric.EvidenceError("Saved training text differs from authenticated geometry")
        seen.add(row["truth_id"])
        result.append(metric.FullTextTruth(
            truth.truth_id, truth.source_sha256, metric.Box(*truth.source_box),
            row["text"], truth.role, metric._canonical_role(truth.role)))
    if seen != set(expected) or len(result) != 709:
        raise metric.EvidenceError("Saved training text omitted full-source truth")
    return tuple(result)


def _fixed_dev_truth(root, preflight):
    """Reproduce only fixed dev pixels, without loading an obsolete helper binding."""
    manifest = _read(root, preflight["historical_dev"]["manifest"])
    scenes = {scene["seed"]: scene for scene in _build_scenes(
        PRESETS["smoke"], manifest["seed"], require_complete_style_catalog=True)
        if _scene_split(scene) == "validation"}
    if set(scenes) != {row["seed"] for row in manifest["images"]}:
        raise metric.EvidenceError("Fixed dev scene inventory changed")
    result, identities = [], set()
    for row in manifest["images"]:
        scene = scenes[row["seed"]]
        if metric.runtime_domain_binding_v3._family_identity(scene) != row["family"]:
            raise metric.EvidenceError("Fixed dev family changed")
        rgb, annotation = metric.runtime_domain_binding_v3._render_v3_source(scene)
        if (sha256(png_bytes(rgb)).hexdigest() != row["image_sha256"]
                or rgb.size != (row["width"], row["height"])):
            raise metric.EvidenceError("Fixed dev regenerated pixels changed")
        for record in metric.family_scenes._records(annotation, "texts"):
            text = record.get("text")
            if record.get("visible", True) is False or not isinstance(text, str) or not text.strip():
                continue
            if record.get("rendered_pixel_box") is None:
                continue
            text_id = str(record.get("text_id", "")).strip()
            identity = sha256(f'{row["image_sha256"]}\n{text_id}'.encode("utf-8")).hexdigest()
            box = metric.family_scenes._box(record)
            if not text_id or identity in identities or box is None:
                raise metric.EvidenceError("Fixed dev text identity or geometry changed")
            identities.add(identity)
            role = str(record.get("role", ""))
            result.append(metric.FullTextTruth(identity, row["image_sha256"],
                metric.Box(*box), text, role, metric._canonical_role(role)))
    if len(result) != 183 or sum(len(item.text) for item in result) != 1019:
        raise metric.EvidenceError("Fixed dev full text denominator changed")
    return tuple(result)


def score(summary_path, summary_sha, candidate_path, candidate_sha, evaluation_path,
          evaluation_sha, output_path, *, source_sha, repository_root=ROOT):
    root = Path(repository_root).resolve()
    started = time.perf_counter()
    if (sha256(Path(__file__).read_bytes()).hexdigest() != source_sha
            or sha256(Path(metric.__file__).read_bytes()).hexdigest() != METRIC_SHA):
        raise metric.EvidenceError("Scoring adapter or frozen V2 metric changed")
    output = Path(output_path).resolve()
    if not output.is_relative_to(root/"artifacts") or output.exists():
        raise metric.EvidenceError("Use a new scoring artifact")
    source_bindings = metric._validate_sources(root)
    summary = _read(root, {"path": str(summary_path), "sha256": summary_sha})
    request = _read(root, summary["capture_request"])
    binding = request["binding"]
    # All candidate/runtime evidence authenticates before saved or regenerated truth is accessed.
    evidence = metric.geometry._validate_before_truth(
        root, root/binding["path"], binding["sha256"],
        root/summary["capture_report"]["path"], summary["capture_report"]["sha256"],
        root/summary["capture_request"]["path"], summary["capture_request"]["sha256"],
        Path(candidate_path).resolve(), candidate_sha, Path(evaluation_path).resolve(), evaluation_sha)
    reports = [bridge.RuntimeReportEvidence(
        row["split"], root/row["manifest_path"], row["manifest_sha256"],
        root/row["report_path"], row["report_sha256"]) for row in request["reports"]]
    prepared = bridge.prepare_text_extent_capture_request(
        root/binding["path"], binding["sha256"], reports,
        root/request["candidate"]["path"], request["candidate"]["sha256"], repository_root=root)
    if prepared.request != request:
        raise metric.EvidenceError("Capture request changed")
    inputs = bridge.load_text_extent_head_inputs(
        prepared, root/summary["capture_report"]["path"], summary["capture_report"]["sha256"], repository_root=root)
    preflight = _read(root, binding)
    train = _saved_train_truth(_read(root, preflight["train_text_truth"]), inputs.train)
    historical = preflight["historical_dev"]["binding"]
    dev = _fixed_dev_truth(root, preflight)
    expected_dev = {t.truth_id: t for t in inputs.dev.source_truths}
    if len(dev) != 183 or {t.truth_id for t in dev} != set(expected_dev):
        raise metric.EvidenceError("Fixed dev truth inventory changed")
    for truth in dev:
        expected = expected_dev[truth.truth_id]
        box = (truth.box.left, truth.box.top, truth.box.right, truth.box.bottom)
        if truth.source_sha256 != expected.source_sha256 or any(abs(a-b)>1e-6 for a,b in zip(box, expected.source_box)):
            raise metric.EvidenceError("Fixed dev geometry changed")
    truths = {"train": train, "validation": dev}
    recognized = metric._recognized_predictions(evidence)
    metrics = {}
    for split, rows in truths.items():
        sources = {t.source_sha256 for t in rows}
        metrics[split] = metric._score_split(rows, {k:v for k,v in recognized.items() if k in sources})
    raw, recognized_geometry, failures = metric._geometry_and_failure_metrics(evidence, truths)
    bars_sha, _, _ = metric.geometry._load_acceptance_bar(root)
    def descriptor(path, digest):
        return {"path": Path(path).resolve().relative_to(root).as_posix(), "sha256": digest}
    result = {
        "schema": metric.OUTPUT_SCHEMA, "status": "diagnostic_only_unapproved",
        "synthetic_only": True, "private_data": False, "sealed_data": False,
        "optimizer_steps": 0, "production_approval": False, "release_eligible": False,
        "truth_isolation": {"runtime_received_truth": False,
            "all_runtime_and_candidate_evidence_authenticated_before_truth_regeneration": True,
            "train_truth": "saved_preflight_text_and_geometry_no_regeneration",
            "dev_truth": "fixed_dev_renderer_authenticated_against_historical_rasters_and_geometry"},
        "inputs": {"binding": binding, "capture_request": summary["capture_request"],
            "capture_report": summary["capture_report"],
            "candidate": descriptor(candidate_path, candidate_sha),
            "evaluation_report": descriptor(evaluation_path, evaluation_sha),
            "evaluator_sha256": METRIC_SHA, "source_bindings": source_bindings,
            "input_adapter": descriptor(Path(__file__), source_sha),
            "input_summary": descriptor(summary_path, summary_sha),
            "saved_train_truth": preflight["train_text_truth"], "historical_dev_binding": historical},
        "metrics": metrics, "raw_detector_geometry": raw,
        "successfully_recognized_region_geometry": recognized_geometry,
        "recognition_failures": failures,
        "acceptance_bar_reference": {"path": metric.geometry.ACCEPTANCE_BARS_PATH.as_posix(),
            "sha256": bars_sha, "scorer_does_not_select_or_approve_a_candidate": True},
        "integrity": {"source_count": 23, "panel_count": 37, "full_source_truth_count": 892,
            "failed_panels_remain_in_full_source_denominator": True},
        "elapsed_milliseconds": (time.perf_counter()-started)*1000,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, allow_nan=False)
        stream.write("\n")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("summary", "summary-sha", "candidate", "candidate-sha", "evaluation", "evaluation-sha", "output", "source-sha"):
        parser.add_argument("--"+name, required=True)
    args = parser.parse_args()
    result = score(args.summary, args.summary_sha, args.candidate, args.candidate_sha,
                   args.evaluation, args.evaluation_sha, args.output, source_sha=args.source_sha)
    print(json.dumps({"status": result["status"], "dev": result["metrics"]["validation"]}))


if __name__ == "__main__":
    main()
