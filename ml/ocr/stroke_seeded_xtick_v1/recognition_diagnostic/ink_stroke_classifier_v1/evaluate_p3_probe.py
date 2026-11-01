# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Apply frozen P3 to retained truth-blind scanner proposals; consumed probe only."""

from __future__ import annotations

from collections import Counter, defaultdict
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import importlib.util
import json
import math
from pathlib import Path
import stat
import sys
import time
from typing import Any

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
HERE = Path(__file__).resolve().parent
CONFIG_REL = HERE.joinpath("p3_probe_eval.json").relative_to(ROOT).as_posix()
P3_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/train_classifier_p3.py"
P3_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/p3.json"
P3_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-sourceuniform-60ep-rx6600xt/report.json"
P3_CORRECTED_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-sourceuniform-60ep-rx6600xt/report-corrected-v2.json"
P3_CHECKPOINT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-sourceuniform-60ep-rx6600xt/checkpoint.pt"
TRAIN_MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2/manifest.json"
PROBE_MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json"
SCANNER_REL = "artifacts/stroke-seeded-xtick-v1-ink-p2-vs-curated-mix-v2/scanner_proposals.json"
PAIRED_SCORES_REL = "artifacts/stroke-seeded-xtick-v1-ink-p2-vs-curated-mix-v2/paired_proposal_scores.json"
PAIRED_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-p2-vs-curated-mix-v2/report.corrected.json"
PRIOR_FILTER_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-p2-filter-v1/report.json"
MATERIALIZER_REL = "ml/ocr/stroke_seeded_xtick_v1/materialize.py"
AXIS_HELPER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py"
TRUTH_HELPER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/opencv_hough_minline_diagnostic/run_hough_min5.py"
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-probe-filter-v1"
PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-probe-filter-v1-preflight-v2.json"
P3_RUNNER_SHA = "b7c0b381f7d58be23ac8d1b139a18a8467fed9e34c320421765063dbe76c118d"
P3_CONFIG_SHA = "927b93c88d7e61dc505516995b72f2235aa7fc8824e5fc54b45ae6741c3c5717"
P3_REPORT_SHA = "aa57c136d7a6272ca1d0f3590d8ad7404413fdae85be24db39986a3952f854bf"
P3_CORRECTED_REPORT_SHA = "c8af111f18fe6a7d913aeb91f59f9934ffa82d7b057e9e5861617a00213b689f"
P3_CHECKPOINT_SHA = "8b1f8ce7d9897e3db19e71a4f21576b92e51ae56a90aa1fec9fb1df364820c92"
TRAIN_MANIFEST_SHA = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
PROBE_MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
SCANNER_SHA = "0297966c41243bd4a27a0c1fa0aa7e849fc8beb5fa7a5dc177fc7e9029cb6081"
PAIRED_SCORES_SHA = "e58f4a56cfee27fd0b88b9509b87ed8e4f4ebb85769a1d66984a5c01cda172f4"
PAIRED_REPORT_SHA = "51d716225552ec7e3412c373c858117e13a7e79666dbd061a24af0ecbe724b59"
PRIOR_FILTER_SHA = "3a81674fa9655e96c298f4094105b8c6d41d5f0991428f26e6bdd4ded3b7a5ef"
MATERIALIZER_SHA = "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d"
AXIS_HELPER_SHA = "b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de"
TRUTH_HELPER_SHA = "ee3b0c35639f567ec6bdaa705ba0ce1e7b797d95743ad431651338444f1cc979"
MODEL_HELPER_SHA = "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5"
P1_TRAINER_SHA = "3e8617809584dadb9c6b35cb52b95136fe766212a2f0bb65f4863b0d17ebc594"
V2_MATERIALIZER_CURRENT_SHA = "16b48f1a59385181f59b2245fc9ff53816093c55ade4fdc40b2f5bd9eb19a3b7"
THRESHOLD = 0.5
MAX_INPUT_BYTES = 50_000_000
_RUNNER_SHA = ""


class ProbeEvaluationError(RuntimeError):
    pass


def digest(value: bytes) -> str:
    return sha256(value).hexdigest()


def read_project(relative: str, expected_sha: str | None, limit: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or limit <= 0:
        raise ProbeEvaluationError("invalid project-relative bounded read")
    current = ROOT.resolve(strict=True)
    for i, part in enumerate(rel.parts):
        current = current / part
        info = current.lstat()
        attrs = getattr(info, "st_file_attributes", 0)
        final = i == len(rel.parts) - 1
        if stat.S_ISLNK(info.st_mode) or attrs & 0x400:
            raise ProbeEvaluationError(f"reparse component rejected: {relative}")
        if not final and not stat.S_ISDIR(info.st_mode):
            raise ProbeEvaluationError(f"non-directory component: {relative}")
        if final and (not stat.S_ISREG(info.st_mode) or info.st_size > limit):
            raise ProbeEvaluationError(f"input is not a bounded regular file: {relative}")
    with current.open("rb") as stream:
        value = stream.read(limit + 1)
    if len(value) > limit:
        raise ProbeEvaluationError(f"bounded input exceeded: {relative}")
    if expected_sha is not None and digest(value) != expected_sha:
        raise ProbeEvaluationError(f"pinned input hash mismatch: {relative}")
    return value


def read_json(relative: str, expected_sha: str, limit: int) -> tuple[dict[str, Any], bytes]:
    raw = read_project(relative, expected_sha, limit)
    value = json.loads(raw.decode("utf-8"))
    if not isinstance(value, dict):
        raise ProbeEvaluationError(f"pinned JSON root is not an object: {relative}")
    return value, raw


def semantic_train_hashes(records: list[dict[str, Any]], train_groups: set[str],
                          annotation_reader) -> tuple[dict[str, int], dict[str, set[str]], int]:
    labels: dict[str, set[int]] = defaultdict(set)
    groups: dict[str, set[str]] = defaultdict(set)
    admitted_rows = 0
    for row in records:
        if row.get("source_group_id") not in train_groups:
            continue
        if row.get("split") != "train" or row.get("source_id") != row.get("source_group_id"):
            raise ProbeEvaluationError("malformed training manifest row")
        annotation = annotation_reader(row)
        state = annotation.get("matching_state")
        if state not in ("unique_visible_tick", "no_ground_truth_tick_nearby", "ambiguous"):
            raise ProbeEvaluationError("unknown training label state")
        if state == "ambiguous":
            continue
        image_hash = row.get("image_sha256")
        if not isinstance(image_hash, str) or len(image_hash) != 64:
            raise ProbeEvaluationError("malformed training image hash")
        labels[image_hash].add(int(state == "unique_visible_tick"))
        groups[image_hash].add(row["source_group_id"])
        admitted_rows += 1
    conflicts = {h: v for h, v in labels.items() if len(v) != 1}
    if conflicts:
        raise ProbeEvaluationError(f"same training image hash has conflicting semantic labels: {len(conflicts)}")
    return {h: next(iter(v)) for h, v in labels.items()}, groups, admitted_rows


def crop_bytes(image: Image.Image, center: list[float]) -> tuple[bytes, np.ndarray]:
    from ml.ocr.stroke_seeded_xtick_v1 import materialize
    crop, _ = materialize._crop_and_resize(image, float(center[0]), float(center[1]) - 2.0)
    buffer = BytesIO()
    crop.save(buffer, format="PNG", compress_level=9)
    return buffer.getvalue(), np.asarray(crop, dtype=np.float32) / 255.0


def quarantine_rows(rows: list[dict[str, Any]], training_hashes: set[str],
                    holdout_hashes: set[str]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    eligible, quarantine = [], []
    for row in rows:
        reasons = []
        if row["crop_sha256"] in training_hashes:
            reasons.append("exact_P3_training_crop_hash_overlap")
        if row["crop_sha256"] in holdout_hashes:
            reasons.append("exact_P1_internal_holdout_crop_hash_metadata_overlap")
        if reasons:
            quarantine.append({"proposal_id": row["proposal_id"], "crop_sha256": row["crop_sha256"],
                "reasons": reasons})
        else:
            eligible.append(row)
    return eligible, quarantine


def _load_p3_stack():
    raw = read_project(P3_REL, P3_RUNNER_SHA, 4_000_000)
    name = "ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1.p3_probe_parent"
    spec = importlib.util.spec_from_file_location(name, ROOT / P3_REL)
    if spec is None:
        raise ProbeEvaluationError("cannot load P3 source module")
    p3 = importlib.util.module_from_spec(spec)
    sys.modules[name] = p3
    exec(compile(raw, str(ROOT / P3_REL), "exec"), p3.__dict__)
    return p3.load_pinned_stack()


def _load_exact_v2_materializer():
    import ml.ocr.fragmented_xtick_detector_v2 as package
    relative = "ml/ocr/fragmented_xtick_detector_v2/materialize.py"
    raw = read_project(relative, V2_MATERIALIZER_CURRENT_SHA, 2_000_000)
    path = ROOT / relative
    name = "ml.ocr.fragmented_xtick_detector_v2.materialize"
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None:
        raise ProbeEvaluationError("cannot construct exact materializer module")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    package.materialize = module
    return digest(raw)


def _assert_runtime(runtime: dict[str, Any]) -> None:
    if (runtime.get("device") != "cuda:0" or runtime.get("cpu_fallback") is not False
            or "RX 6600 XT" not in str(runtime.get("device_name", ""))):
        raise ProbeEvaluationError("inference requires the project RX 6600 XT runtime without fallback")


def _metrics(panel_rows: list[dict[str, Any]], detector_key: str) -> dict[str, Any]:
    visible = sum(p["visible_truth_count"] for p in panel_rows)
    proposal_count = matched = unmatched = no_visible_fp = no_visible_panels = no_visible_with_fp = 0
    uncertain_proposals = uncertain_matched = 0
    for panel in panel_rows:
        detection = panel[detector_key]
        n = detection["usable_detected_x_stroke_count"]
        proposal_count += n
        matched += detection["matched_count"]
        unmatched += detection["unmatched_detected_count"]
        if panel["visible_truth_count"] == 0:
            no_visible_panels += 1
            no_visible_fp += n
            no_visible_with_fp += int(n > 0)
        if panel["needs_review"]:
            uncertain_proposals += n
            uncertain_matched += detection["matched_count"]
    return {"visible_truth_ticks": visible, "proposals": proposal_count, "matched_ticks": matched,
        "unmatched_proposals": unmatched, "false_proposals": unmatched,
        "precision": matched / proposal_count if proposal_count else 0.0,
        "recall": matched / visible if visible else 0.0,
        "panels_with_no_visible_ticks": no_visible_panels,
        "false_proposals_on_no_visible_tick_panels": no_visible_fp,
        "no_visible_tick_panels_with_false_proposals": no_visible_with_fp,
        "axis_uncertain_panels": sum(bool(p["needs_review"]) for p in panel_rows),
        "proposals_on_axis_uncertain_panels": uncertain_proposals,
        "matches_on_axis_uncertain_panels": uncertain_matched}


def _runtime_metrics(panels: list[dict[str, Any]], rows: list[dict[str, Any]],
                     key: str, score_field: str | None, threshold: float | None) -> dict[str, Any]:
    for panel in panels:
        panel_rows = [r for r in rows if r["panel_id"] == panel["panel_id"]]
        if score_field is None:
            selected = panel_rows
        else:
            selected = [r for r in panel_rows if r.get(score_field) is not None and
                math.isfinite(float(r[score_field])) and float(r[score_field]) >= float(threshold)]
        dets = [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in selected]
        panel[key] = common.match_panel(panel["visible_truth"], dets)
    aggregate = _metrics(panels, key)
    by_source: dict[str, Any] = {}
    for source_id in sorted({p["source_id"] for p in panels}):
        subset = [p for p in panels if p["source_id"] == source_id]
        by_source[source_id] = _metrics(subset, key)
    return {"aggregate": aggregate, "by_source": by_source}


def preflight() -> tuple[dict[str, Any], list[dict[str, Any]], dict[str, Any]]:
    global _RUNNER_SHA
    _RUNNER_SHA = digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), None, 4_000_000))
    config, config_bytes = read_json(CONFIG_REL, None, 65_536)
    if config.get("expected_evaluator_sha256") != _RUNNER_SHA:
        raise ProbeEvaluationError("evaluation source/config identity mismatch")
    expected_config = {"schema": "graphreader.p3-stroke-validity-probe-eval-config.v1",
        "recipe_id": "p3-stroke-validity-filter-on-retained-truth-blind-proposals-v1",
        "p3_training_report_sha256": P3_REPORT_SHA,
        "p3_corrected_report_sha256": P3_CORRECTED_REPORT_SHA,
        "p3_checkpoint_sha256": P3_CHECKPOINT_SHA, "p3_runner_sha256": P3_RUNNER_SHA,
        "p3_config_sha256": P3_CONFIG_SHA, "train_manifest_sha256": TRAIN_MANIFEST_SHA,
        "probe_manifest_sha256": PROBE_MANIFEST_SHA, "scanner_proposals_sha256": SCANNER_SHA,
        "paired_scores_sha256": PAIRED_SCORES_SHA, "paired_report_sha256": PAIRED_REPORT_SHA,
        "prior_p2_filter_report_sha256": PRIOR_FILTER_SHA, "score_threshold": THRESHOLD,
        "crop_geometry": "same pinned P1 crop materializer at proposal center x and center y minus 2 pixels",
        "inference_batch_size": 32, "required_launcher": "tools/gpu-training/Run-GpuPython.ps1",
        "required_device_name_substring": "RX 6600 XT", "cpu_fallback_allowed": False,
        "optimizer_execution_authorized": False, "p1_holdout_file_reads_allowed": False,
        "production_approval": False, "release_eligible": False,
        "output_relative": OUTPUT_REL, "preflight_relative": PREFLIGHT_REL,
        "expected_evaluator_sha256": _RUNNER_SHA}
    if config != expected_config:
        raise ProbeEvaluationError("P3 probe evaluation config differs from predeclared identities/recipe")

    p3_report, _ = read_json(P3_REPORT_REL, P3_REPORT_SHA, 500_000)
    corrected_report, _ = read_json(P3_CORRECTED_REPORT_REL, P3_CORRECTED_REPORT_SHA, 600_000)
    if corrected_report["checkpoint"]["sha256"] != P3_CHECKPOINT_SHA:
        raise ProbeEvaluationError("corrected P3 report is bound to a different checkpoint")
    checkpoint = read_project(P3_CHECKPOINT_REL, P3_CHECKPOINT_SHA, 2_000_000)
    p3_config, _ = read_json(P3_CONFIG_REL, P3_CONFIG_SHA, 65_536)
    if (p3_report["recipe_id"] != "ink-stroke-validity-source-uniform-p3-60ep"
            or p3_report["dependencies"]["p3_runner_sha256"] != P3_RUNNER_SHA
            or p3_report["dependencies"]["p3_config_sha256"] != P3_CONFIG_SHA
            or p3_report["data"]["manifest_sha256"] != TRAIN_MANIFEST_SHA):
        raise ProbeEvaluationError("P3 report source/config/training input identity mismatch")
    if digest(read_project(TRAIN_MANIFEST_REL, TRAIN_MANIFEST_SHA, 10_000_000)) != TRAIN_MANIFEST_SHA:
        raise ProbeEvaluationError("training curation manifest identity mismatch")
    train_manifest, _ = read_json(TRAIN_MANIFEST_REL, TRAIN_MANIFEST_SHA, 10_000_000)
    probe_manifest, _ = read_json(PROBE_MANIFEST_REL, PROBE_MANIFEST_SHA, 10_000_000)
    scanner, scanner_bytes = read_json(SCANNER_REL, SCANNER_SHA, 5_000_000)
    paired, paired_bytes = read_json(PAIRED_SCORES_REL, PAIRED_SCORES_SHA, 5_000_000)
    paired_report, _ = read_json(PAIRED_REPORT_REL, PAIRED_REPORT_SHA, 2_000_000)
    prior_filter, _ = read_json(PRIOR_FILTER_REPORT_REL, PRIOR_FILTER_SHA, 2_000_000)
    materializer_sha = digest(read_project(MATERIALIZER_REL, MATERIALIZER_SHA, 2_000_000))
    axis_helper_sha = digest(read_project(AXIS_HELPER_REL, AXIS_HELPER_SHA, 2_000_000))
    truth_helper_sha = digest(read_project(TRUTH_HELPER_REL, TRUTH_HELPER_SHA, 2_000_000))
    if materializer_sha != MATERIALIZER_SHA or axis_helper_sha != AXIS_HELPER_SHA or truth_helper_sha != TRUTH_HELPER_SHA:
        raise ProbeEvaluationError("geometry helper source identity mismatch")
    if (scanner.get("schema") != "graphreader.truth-blind-ink-proposals.v1"
            or scanner.get("probe_manifest_sha256") != PROBE_MANIFEST_SHA
            or scanner.get("scanner_binary_sha256") != paired_report["scanner_identity"]["binary_sha256"]
            or paired_report["scanner_identity"]["raw_proposal_sha256"] != SCANNER_SHA
            or paired_report["scanner_identity"]["proposal_scores_sha256"] != PAIRED_SCORES_SHA
            or paired_report["probe_manifest_sha256"] != PROBE_MANIFEST_SHA):
        raise ProbeEvaluationError("retained raw scanner proposals are not bound to the frozen paired report")
    if paired.get("scanner_proposals_sha256") != SCANNER_SHA or len(paired.get("rows", [])) != 365:
        raise ProbeEvaluationError("paired baseline score rows do not bind to all 365 scanner proposals")

    train_groups = set(p3_report["data"]["train_source_groups"])
    holdout_groups = set(p3_config["holdout_component_ids"])
    if len(train_groups) != 19 or len(holdout_groups) != 4 or train_groups & holdout_groups:
        raise ProbeEvaluationError("frozen P3 training/P1 holdout group definitions are malformed")

    base = ROOT / "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2"
    def read_train_annotation(row: dict[str, Any]) -> dict[str, Any]:
        rel = f"artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2/{row['annotation_path']}"
        raw = read_project(rel, row["annotation_sha256"], 65_536)
        value = json.loads(raw.decode("utf-8"))
        if any(value.get(k) != row.get(k) for k in ("case_id", "source_id", "source_group_id", "split", "image_sha256")):
            raise ProbeEvaluationError("train annotation does not match its manifest row")
        return value
    train_hash_labels, train_hash_groups, admitted_rows = semantic_train_hashes(
        train_manifest["records"], train_groups, read_train_annotation)
    if (admitted_rows != 796 or len(train_hash_labels) != 446
            or Counter(train_hash_labels.values()) != Counter({1: 81, 0: 365})):
        raise ProbeEvaluationError("P3 train-only manifest label/hash inventory differs from recorded 446 inputs")

    # Holdout comparison uses manifest hash/source metadata only. No holdout files open.
    holdout_records = [r for r in train_manifest["records"] if r.get("source_group_id") in holdout_groups]
    holdout_hashes = {str(r["image_sha256"]) for r in holdout_records if isinstance(r.get("image_sha256"), str)}
    if any(r.get("source_group_id") in train_groups for r in holdout_records):
        raise ProbeEvaluationError("P1 holdout metadata crossed into P3 train group selection")

    train_source_metadata = {r["source_id"]: r for r in train_manifest["source_groups"]
        if r.get("source_id") in train_groups}
    holdout_source_metadata = {r["source_id"]: r for r in train_manifest["source_groups"]
        if r.get("source_id") in holdout_groups}
    probe_sources = {s["source_id"]: s for s in probe_manifest["sources"]}
    train_ids = set(train_source_metadata)
    probe_ids = set(probe_sources)
    train_identities = {r["source_identity_sha256"] for r in train_source_metadata.values()}
    probe_identities = {r["source_identity_sha256"] for r in probe_sources.values()}
    train_panel_hashes = {r["reconstructed_full_panel_pixel_sha256"] for r in train_source_metadata.values()}
    holdout_panel_hashes = {r["reconstructed_full_panel_pixel_sha256"] for r in holdout_source_metadata.values()}
    probe_panel_hashes = {r["source_pixel_sha256"] for r in probe_sources.values()}
    source_overlap = {"train_source_id": sorted(train_ids & probe_ids),
        "train_source_identity_sha256": sorted(train_identities & probe_identities),
        "train_full_panel_pixel_sha256": sorted(train_panel_hashes & probe_panel_hashes),
        "p1_holdout_source_id": sorted(set(holdout_source_metadata) & probe_ids),
        "p1_holdout_source_identity_sha256": sorted(
            {r["source_identity_sha256"] for r in holdout_source_metadata.values()} & probe_identities),
        "p1_holdout_full_panel_pixel_sha256": sorted(holdout_panel_hashes & probe_panel_hashes)}

    scanner_source_hashes = {r["source_id"]: r["source_pixel_sha256"] for r in scanner["source_pixel_hashes"]}
    if set(scanner_source_hashes) != probe_ids:
        raise ProbeEvaluationError("frozen scanner source set differs from the probe manifest")
    from ml.ocr.stroke_seeded_xtick_v1 import materialize
    if digest(read_project(MATERIALIZER_REL, MATERIALIZER_SHA, 2_000_000)) != MATERIALIZER_SHA:
        raise ProbeEvaluationError("probe renderer/crop materializer changed")
    rows: list[dict[str, Any]] = []
    source_images: dict[str, Image.Image] = {}
    rendered_sources: dict[str, Any] = {}
    source_receipts = []
    for source_id, source in sorted(probe_sources.items()):
        scene = materialize.build_train_scene(source["renderer_recipe"])
        image, rendered, _ = materialize.render_train_scene(scene)
        pixel_sha = digest(image.tobytes())
        if pixel_sha != source["source_pixel_sha256"] or pixel_sha != scanner_source_hashes[source_id]:
            raise ProbeEvaluationError(f"probe panel pixels do not match both pinned manifests: {source_id}")
        source_images[source_id] = image
        rendered_sources[source_id] = rendered
        source_receipts.append({"source_id": source_id, "source_pixel_sha256": pixel_sha,
            "width": image.width, "height": image.height, "panel_count": len(rendered["panels"])})

    result_by_source = {r["source_id"]: r for r in scanner["source_results"]}
    if set(result_by_source) != probe_ids:
        raise ProbeEvaluationError("scanner results do not cover exactly the pinned probe source set")
    for source_id, source_result in sorted(result_by_source.items()):
        if source_result.get("expected_source_pixel_sha256") != scanner_source_hashes[source_id]:
            raise ProbeEvaluationError("scanner raw result source pixel identity mismatch")
        raw_panels = {p["panel_id"]: p for p in source_result["panels"]}
        rendered_panels = {p["panel_id"] for p in rendered_sources[source_id]["panels"]}
        if set(raw_panels) != rendered_panels:
            raise ProbeEvaluationError("scanner panel ids differ from rendered full panels")
        for panel in source_result["panels"]:
            for index, proposal in enumerate(panel["ink_proposals"]):
                proposal_id = f"{source_id}:{panel['panel_id']}:{index}"
                png, pixels = crop_bytes(source_images[source_id], proposal["center"])
                rows.append({"proposal_id": proposal_id, "source_id": source_id,
                    "panel_id": panel["panel_id"], "proposal_index": index,
                    "proposal": proposal, "crop_sha256": digest(png), "pixels": pixels})
    if len(rows) != 365:
        raise ProbeEvaluationError(f"frozen raw scanner proposal count changed: {len(rows)}")
    paired_by_id = {r["proposal_id"]: r for r in paired["rows"]}
    if len(paired_by_id) != 365 or len(paired_by_id) != len(paired["rows"]):
        raise ProbeEvaluationError("paired P2 rows have missing or duplicate proposal ids")
    for row in rows:
        paired_row = paired_by_id.get(row["proposal_id"])
        if not paired_row or any(paired_row.get(k) != row[k] for k in ("source_id", "panel_id", "proposal_index")):
            raise ProbeEvaluationError("paired P2 scores do not align to raw scanner proposal identity/order")
        row["p2_score"] = paired_row["p2_score"]
        row["p2_box_original"] = paired_row["p2_box_original"]
    training_hashes = set(train_hash_labels)
    eligible, quarantined = quarantine_rows(rows, training_hashes, holdout_hashes)
    train_overlap_hashes = {row["crop_sha256"] for row in rows} & training_hashes
    holdout_overlap_hashes = {row["crop_sha256"] for row in rows} & holdout_hashes
    overlap_counts = {**{k: len(v) for k, v in source_overlap.items()},
        "train_crop_hashes": len(train_overlap_hashes),
        "p1_holdout_crop_hashes_from_manifest_metadata": len(holdout_overlap_hashes)}
    if any(overlap_counts[k] for k in overlap_counts):
        pass  # exact proposal rows are quarantined below and not scored.
    duplicate_probe_hashes: dict[str, list[str]] = defaultdict(list)
    for row in rows:
        duplicate_probe_hashes[row["crop_sha256"]].append(row["proposal_id"])
    preflight = {"schema": "graphreader.p3-probe-separation-preflight.v1",
        "status": "verified-before-inference", "p3_train_manifest_sha256": TRAIN_MANIFEST_SHA,
        "evaluator_source_sha256": _RUNNER_SHA, "evaluation_config_sha256": digest(config_bytes),
        "probe_manifest_sha256": PROBE_MANIFEST_SHA,
        "scanner_proposals_sha256": digest(scanner_bytes), "paired_scores_sha256": digest(paired_bytes),
        "p3_train_unique_image_hashes": len(train_hash_labels),
        "p3_train_class_counts": {"positive": sum(train_hash_labels.values()),
            "negative": len(train_hash_labels) - sum(train_hash_labels.values())},
        "p3_train_admitted_annotation_occurrences": admitted_rows,
        "training_semantic_label_hash_conflicts": 0,
        "p1_holdout_file_bytes_read": False,
        "p1_holdout_image_or_annotation_files_read": False,
        "p1_holdout_compared_using_only": "source IDs, identity/panel/crop hashes in pinned train manifest metadata; no holdout files opened",
        "source_counts": {"p3_training_source_groups": len(train_ids), "p1_holdout_source_groups": len(holdout_source_metadata),
            "probe_source_groups": len(probe_ids)},
        "source_and_full_panel_overlap_counts": overlap_counts,
        "scan_proposals_total": len(rows), "unique_probe_proposal_crop_hashes": len(duplicate_probe_hashes),
        "exact_duplicate_probe_crop_rows": len(rows) - len(duplicate_probe_hashes),
        "exact_hash_overlap_unique_counts": {"p3_train": len(train_overlap_hashes),
            "p1_holdout_metadata": len(holdout_overlap_hashes)},
        "quarantined_proposal_rows": quarantined, "quarantined_count": len(quarantined),
        "eligible_for_p3_inference": len(eligible),
        "full_panel_pixel_hashes_verified": source_receipts,
        "scanner_binary_sha256": scanner["scanner_binary_sha256"],
        "retained_scanner_report_sha256": paired_report["scanner_identity"]["prior_filter_report_sha256"],
        "p3_training_transitive_materializer_provenance": corrected_report["dependencies"]["unbound_transitive_source_dependency"]}
    return preflight, eligible, {"config": config, "p3_config": p3_config,
        "p3_report": p3_report, "corrected_report": corrected_report,
        "probe_manifest": probe_manifest, "train_manifest": train_manifest,
        "scanner": scanner, "paired": paired, "prior_filter": prior_filter,
        "source_images": source_images, "rendered_sources": rendered_sources,
        "all_rows": rows, "quarantined": quarantined,
        "train_groups": train_groups, "holdout_groups": holdout_groups,
        "source_receipts": source_receipts, "source_overlap": source_overlap,
        "overlap_counts": overlap_counts, "train_hash_labels": train_hash_labels,
        "train_hash_groups": train_hash_groups}


def _infer_p3(rows: list[dict[str, Any]]) -> tuple[list[float], dict[str, Any]]:
    if Path(sys.executable).resolve() != (ROOT / "tools/gpu-training/venv/Scripts/python.exe").resolve():
        raise ProbeEvaluationError("P3 probe inference requires the project GPU Python launcher")
    _load_exact_v2_materializer()
    p2, source_hashes = _load_p3_stack()
    import torch
    runtime = p2.helpers.require_rx_6600_xt(torch)
    _assert_runtime(runtime)
    device = torch.device(str(runtime["device"]))
    checkpoint = read_project(P3_CHECKPOINT_REL, P3_CHECKPOINT_SHA, 2_000_000)
    payload = torch.load(BytesIO(checkpoint), map_location=device, weights_only=True)
    expected = {"model_identity": "stroke-seeded-ink-validity-rf31-p3-sourceuniform-60ep",
        "recipe_id": "ink-stroke-validity-source-uniform-p3-60ep", "epochs": 60,
        "p3_config_sha256": P3_CONFIG_SHA, "p3_runner_sha256": P3_RUNNER_SHA,
        "p2_base_config_sha256": "b230e743cadb3d057aef443b3da1dc5821e2da2ce88ecc20ae3175725069aa42",
        "p2_base_runner_sha256": "dc86c5ee004794519c6c1ac7fa21699b06a31637914de3ab827353c828e3aa5d",
        "manifest_sha256": TRAIN_MANIFEST_SHA, "model_seed": 27104152, "sampler_seed": 27104153}
    if any(payload.get(k) != v for k, v in expected.items()):
        raise ProbeEvaluationError("P3 checkpoint metadata identity mismatch")
    model = p2.p1._build_classifier(torch).to(device)
    model.load_state_dict(payload["state_dict"], strict=True)
    if any(parameter.device != device for parameter in model.parameters()):
        raise ProbeEvaluationError("P3 model parameters did not remain on RX 6600 XT")
    model.eval()
    scores = []
    with torch.inference_mode():
        for start in range(0, len(rows), 32):
            arrays = np.stack([r["pixels"] for r in rows[start:start + 32]]).astype(np.float32, copy=False)
            batch = torch.from_numpy(arrays).unsqueeze(1).to(device)
            if batch.device != device:
                raise ProbeEvaluationError("P3 crop inference tensors left RX 6600 XT")
            logits = model(batch)
            values = torch.sigmoid(logits).reshape(-1).detach().cpu().tolist()
            if len(values) != len(arrays) or not all(math.isfinite(float(v)) for v in values):
                raise ProbeEvaluationError("P3 returned missing or non-finite crop scores")
            scores.extend(float(v) for v in values)
    torch.cuda.synchronize(device)
    return scores, {"runtime": runtime, "device": str(device), "source_hashes": source_hashes,
        "checkpoint_sha256": digest(checkpoint), "checkpoint_metadata": expected,
        "inference_rows": len(scores), "inference_batches": math.ceil(len(scores) / 32)}


def _score_truth(manifest: dict[str, Any], scanner: dict[str, Any],
                 rendered_sources: dict[str, Any], all_rows: list[dict[str, Any]], eligible: list[dict[str, Any]],
                 quarantined: list[dict[str, Any]], prior_filter: dict[str, Any], p2: Any) -> dict[str, Any]:
    from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
    from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.opencv_hough_minline_diagnostic import run_hough_min5 as truth_helper
    truths_by_panel: dict[str, list[dict[str, Any]]] = {}
    for source_id, rendered in rendered_sources.items():
        source_truths = truth_helper.source_truths(source_id, rendered["panels"])
        truths_by_panel.update(source_truths)
    if sum(sum(t["source_stroke_visible"] for t in v) for v in truths_by_panel.values()) != 181:
        raise ProbeEvaluationError("post-inference visible tick count differs from consumed probe")
    scan_panels = {p["panel_id"]: p for source in scanner["source_results"] for p in source["panels"]}
    eligible_ids = {r["proposal_id"] for r in eligible}
    quarantine_ids = {r["proposal_id"] for r in quarantined}
    if eligible_ids & quarantine_ids or len(eligible_ids) + len(quarantine_ids) != 365:
        raise ProbeEvaluationError("eligible/quarantined proposal accounting is inconsistent")
    rows_by_panel: dict[str, list[dict[str, Any]]] = defaultdict(list)
    eligible_by_id = {r["proposal_id"]: r for r in eligible}
    for row in all_rows:
        rows_by_panel[row["panel_id"]].append(row)
    panels = []
    for source in manifest["sources"]:
        source_id = source["source_id"]
        rendered = rendered_sources[source_id]
        for panel in rendered["panels"]:
            panel_id = panel["panel_id"]
            visible = [t for t in truths_by_panel[panel_id] if t["source_stroke_visible"]]
            raw = scan_panels[panel_id]
            panel_rows = rows_by_panel[panel_id]
            eligible_rows = [eligible_by_id[r["proposal_id"]] for r in panel_rows if r["proposal_id"] in eligible_by_id]
            panel_record = {"panel_id": panel_id, "source_id": source_id,
                "visible_truth_count": len(visible), "visible_truth": visible,
                "needs_review": bool(raw["needs_review"]),
                "uncertainty_reasons": list(raw["uncertainty_reasons"]),
                "quarantined_proposals": sum(r["proposal_id"] in quarantine_ids for r in panel_rows)}
            full_raw = [r for r in panel_rows]
            all_p2 = [r for r in panel_rows if r.get("p2_score") is not None and
                r.get("p2_box_original") is not None and float(r["p2_score"]) >= THRESHOLD]
            p2_eligible = [r for r in eligible_rows if r.get("p2_score") is not None and
                r.get("p2_box_original") is not None and float(r["p2_score"]) >= THRESHOLD]
            p3_selected = [r for r in eligible_rows if float(r["p3_score"]) >= THRESHOLD]
            panel_record["raw_scan_all_365"] = common.match_panel(visible,
                [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in full_raw])
            panel_record["raw_scan_eligible"] = common.match_panel(visible,
                [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in eligible_rows])
            panel_record["p2_filter_all_365"] = common.match_panel(visible,
                [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in all_p2])
            panel_record["p2_filter_eligible"] = common.match_panel(visible,
                [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in p2_eligible])
            panel_record["p3_filter_eligible"] = common.match_panel(visible,
                [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in p3_selected])
            panels.append(panel_record)

    prior_metrics = prior_filter["metrics"]
    p2_full = _metrics(panels, "p2_filter_all_365")
    expected = prior_metrics["detector_filtered_scan_05"]
    for k in ("visible_truth_ticks", "proposals", "matched_ticks", "unmatched_proposals", "precision", "recall",
              "panels_with_no_visible_ticks", "false_proposals_on_no_visible_tick_panels",
              "no_visible_tick_panels_with_false_proposals"):
        old_key = {"visible_truth_ticks": "visible_truth_ticks", "proposals": "proposals",
                   "matched_ticks": "matched_ticks", "unmatched_proposals": "unmatched_proposals"}.get(k, k)
        if abs(float(p2_full[k]) - float(expected[old_key])) > 1e-12:
            raise ProbeEvaluationError(f"retained paired P2 scores fail prior full-probe parity for {k}")
    summaries = {}
    for strategy, key in (("raw_scan_all_365", "raw_scan_all_365"),
        ("raw_scan_eligible", "raw_scan_eligible"), ("p2_filter_all_365", "p2_filter_all_365"),
        ("p2_filter_eligible", "p2_filter_eligible"), ("p3_filter_eligible", "p3_filter_eligible")):
        summaries[strategy] = {"aggregate": _metrics(panels, key), "by_source": {}}
        for source_id in sorted({p["source_id"] for p in panels}):
            summaries[strategy]["by_source"][source_id] = _metrics([p for p in panels if p["source_id"] == source_id], key)
    # Duplicate proposal crops are scored row-by-row. Report any geometry-label ambiguity among exact pixel repeats.
    label_by_crop: dict[str, set[bool]] = defaultdict(set)
    for row in all_rows:
        # A proposal is truth-positive only if its detector line was matched to a visible tick after inference.
        panel = next(p for p in panels if p["panel_id"] == row["panel_id"])
        match = panel["raw_scan_all_365"]
        pairs = match["matched_pairs"]
        label_by_crop[row["crop_sha256"]].add(any(pair["detection_index"] == row["proposal_index"] and
            pair["case_id"] and pair["kind"] == "visible_tick" for pair in pairs))
    duplicate_conflicts = sum(len(labels) > 1 for labels in label_by_crop.values())
    return {"panels": panels, "metrics": summaries, "p2_full_parity": {"passed": True,
        "retained_report_metrics": expected, "reproduced_metrics": p2_full},
        "duplicate_crop_truth_label_conflict_hash_count": duplicate_conflicts,
        "exact_duplicate_probe_crop_hash_count": sum(len([r for r in all_rows if r["crop_sha256"] == h]) > 1
            for h in set(r["crop_sha256"] for r in all_rows))}


def run(preflight_path: Path, output: Path) -> dict[str, Any]:
    if Path(sys.executable).resolve() != (ROOT / "tools/gpu-training/venv/Scripts/python.exe").resolve():
        raise ProbeEvaluationError("RX project launcher is required")
    if output.exists() or output.is_symlink() or output.parent != ROOT / "artifacts":
        raise ProbeEvaluationError("output must be a fresh direct artifacts child")
    preflight_result, eligible, context = preflight()
    preflight_bytes = json.dumps(preflight_result, sort_keys=True, indent=2).encode("utf-8") + b"\n"
    if preflight_path.exists():
        actual_preflight = read_project(preflight_path.relative_to(ROOT).as_posix(), None, 500_000)
        try:
            same_preflight = json.loads(actual_preflight.decode("utf-8")) == preflight_result
        except json.JSONDecodeError as error:
            raise ProbeEvaluationError("saved preflight receipt is malformed") from error
        if not same_preflight:
            raise ProbeEvaluationError("existing preflight receipt differs from current bound inputs")
        preflight_bytes = actual_preflight
    else:
        raise ProbeEvaluationError("preflight must be run and reviewed before model inference")
    if not eligible:
        raise ProbeEvaluationError("no eligible probe proposals remain after quarantine")
    output.mkdir(parents=False, exist_ok=False)
    (output / "started.json").write_text(json.dumps({"status": "started", "recipe_id": context["config"]["recipe_id"],
        "preflight_sha256": digest(preflight_bytes), "started_utc": datetime.now(timezone.utc).isoformat(),
        "training_performed": False, "p1_internal_holdout_files_read": False}, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    try:
        p3_scores, inference = _infer_p3(eligible)
        for row, score in zip(eligible, p3_scores):
            row["p3_score"] = score
        for row in context["all_rows"]:
            if row["proposal_id"] not in {r["proposal_id"] for r in eligible}:
                row["p3_score"] = None
        geometry = _score_truth(context["probe_manifest"], context["scanner"], context["rendered_sources"],
            context["all_rows"], eligible, context["quarantined"], context["prior_filter"], None)
        score_rows = [{"proposal_id": r["proposal_id"], "source_id": r["source_id"],
            "panel_id": r["panel_id"], "proposal_index": r["proposal_index"],
            "crop_sha256": r["crop_sha256"], "eligible_for_p3": r["proposal_id"] in {e["proposal_id"] for e in eligible},
            "quarantine_reasons": next((q["reasons"] for q in context["quarantined"] if q["proposal_id"] == r["proposal_id"]), []),
            "p2_score": r.get("p2_score"), "p3_score": r.get("p3_score")} for r in context["all_rows"]]
        scores_raw = json.dumps({"schema": "graphreader.p3-probe-proposal-scores.v1",
            "scanner_proposals_sha256": SCANNER_SHA, "preflight_sha256": digest(preflight_bytes),
            "threshold": THRESHOLD, "rows": score_rows}, sort_keys=True, separators=(",", ":")).encode("utf-8")
        (output / "proposal_scores.json").write_bytes(scores_raw)
        report = {"schema": "graphreader.p3-stroke-validity-probe-filter-report.v1",
            "status": "completed-exploratory-consumed-synthetic-probe-diagnostic",
            "recipe_id": context["config"]["recipe_id"],
            "acceptance_status": "consumed synthetic development probe only; not formal acceptance",
            "production_approval": False, "release_eligible": False, "training_performed": False,
            "optimizer_steps": 0, "p1_internal_holdout_files_read": False,
            "preflight": preflight_result, "preflight_sha256": digest(preflight_bytes),
            "inputs": {"p3_train_manifest_sha256": TRAIN_MANIFEST_SHA,
                "p3_training_report_sha256": P3_REPORT_SHA, "p3_corrected_report_sha256": P3_CORRECTED_REPORT_SHA,
                "p3_checkpoint_sha256": P3_CHECKPOINT_SHA, "p3_runner_sha256": P3_RUNNER_SHA,
                "p3_config_sha256": P3_CONFIG_SHA, "probe_manifest_sha256": PROBE_MANIFEST_SHA,
                "scanner_proposals_sha256": SCANNER_SHA, "paired_p2_scores_sha256": PAIRED_SCORES_SHA,
                "prior_p2_filter_report_sha256": PRIOR_FILTER_SHA,
                "paired_report_sha256": PAIRED_REPORT_SHA},
            "runtime": inference["runtime"], "inference": inference,
            "proposal_count": {"original": 365, "quarantined": len(context["quarantined"]),
                "scored": len(eligible), "threshold": THRESHOLD},
            "raw_scan_p2_and_p3_metrics": geometry["metrics"],
            "p2_full_probe_parity": geometry["p2_full_parity"],
            "duplicate_crop_truth_label_conflict_hash_count": geometry["duplicate_crop_truth_label_conflict_hash_count"],
            "exact_duplicate_probe_crop_hash_count": geometry["exact_duplicate_probe_crop_hash_count"],
            "axis_uncertainty": {"panel_count": 24, "needs_review_count": sum(bool(p["needs_review"]) for p in geometry["panels"]),
                "uncertainty_reasons": dict(sorted(Counter(reason for p in geometry["panels"] for reason in p["uncertainty_reasons"]).items()))},
            "panel_metrics": [{k:v for k,v in p.items() if k != "visible_truth"} for p in geometry["panels"]],
            "proposal_score_artifact_sha256": digest(scores_raw),
            "execution_dependency_hashes": {"model_helper_sha256": MODEL_HELPER_SHA,
                "p1_model_builder_sha256": P1_TRAINER_SHA, "probe_crop_materializer_sha256": MATERIALIZER_SHA,
                "axis_metrics_helper_sha256": AXIS_HELPER_SHA, "probe_truth_geometry_helper_sha256": TRUTH_HELPER_SHA,
                "p3_runner_source_compiled_sha256": P3_RUNNER_SHA,
                "v2_materializer_source_compiled_for_inference_sha256": V2_MATERIALIZER_CURRENT_SHA,
                "p3_training_unbound_transitive_materializer_sha256_current_only": V2_MATERIALIZER_CURRENT_SHA},
            "limitations": ["Probe source is already consumed development evidence, not acceptance or real-chart validation.",
                "Three exact proposal-crop byte hashes overlapped P3 train input hashes and were quarantined before P3 inference; all comparisons use the same eligible subset, with the full 365 P2 baseline separately reproduced.",
                "P1 internal holdout comparisons used manifest hash metadata only; no holdout image or annotation files were opened.",
                "P3 training did not prove execution-time bytes for its transitive V2 materializer; the training run remains reproducibility-limited even though inference dependencies were explicitly source-compiled and hashed.",
                "Synthetic source groups and procedural rendering do not establish generalization to real charts or production value."],
            "finished_utc": datetime.now(timezone.utc).isoformat()}
        (output / "report.json").write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        return report
    except BaseException as error:
        failure = {"status": "failed", "error_type": type(error).__name__, "error": str(error),
            "preflight_sha256": digest(preflight_bytes), "training_performed": False,
            "p1_internal_holdout_files_read": False, "failed_utc": datetime.now(timezone.utc).isoformat()}
        (output / "failure.json").write_text(json.dumps(failure, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        raise


def write_preflight(path: Path) -> dict[str, Any]:
    if path.exists():
        raise ProbeEvaluationError("preflight output already exists; refusing overwrite")
    receipt, eligible, _ = preflight()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(receipt, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"preflight_path": path.relative_to(ROOT).as_posix(),
        "sha256": digest(path.read_bytes()), "eligible": len(eligible),
        "quarantined": receipt["quarantined_count"], "overlap_counts": receipt["exact_hash_overlap_unique_counts"]}, sort_keys=True))
    return receipt


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--preflight", action="store_true")
    parser.add_argument("--preflight-path", default=PREFLIGHT_REL)
    args = parser.parse_args()
    if args.preflight:
        write_preflight(ROOT / args.preflight_path)
    else:
        report = run(ROOT / args.preflight_path, ROOT / OUTPUT_REL)
        print(json.dumps({"metrics": report["raw_scan_p2_and_p3_metrics"],
            "proposal_count": report["proposal_count"], "runtime": report["runtime"]}, indent=2))
