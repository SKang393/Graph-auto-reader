# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Pair a frozen graph-text detector with source-grouped tick-seeded crops.

This is an exploratory crop-level confirmation diagnostic. It performs no
optimizer work and does not modify either production detector.
"""

from __future__ import annotations

from collections import Counter, defaultdict
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import sys
import uuid
from typing import Any


ROOT = Path(__file__).resolve().parents[5]
CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/p3_text_gate_v1/recipe.json"
CONFIG_SHA256 = "02590c9a615ff331d3b233f7f33f65e3f938293e217ffdd10ceea85a65a3ec79"
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-p3-text-gate-v1"
P3_ONNX_REL = "ml/ocr/graph_text_detector_v1/artifacts/P3-run/graph-text-region-detector-v1-p3.onnx"
P3_ONNX_SHA256 = "7c9450a01b9ec3f37d609bf1894f5763ef9a6ebccbe2916ea0450b96cc81fb1f"
P3_RESULT_REL = "ml/ocr/graph_text_detector_v1/P3_RESULT.json"
P3_RESULT_SHA256 = "b9628f2fea238977f982e43d30210c434b0628bce1f52b555f7744746a905f7b"
P3_CONFIG_REL = "ml/ocr/graph_text_detector_v1/training/p3.json"
P3_CONFIG_SHA256 = "750f305ee22c80574a4e1462acbcd9eade5375f2f072a494a6d08b297ec78e02"
P3_RUNNER_REL = "ml/ocr/graph_text_detector_v1/train_p3.py"
P3_RUNNER_SHA256 = "fa210b1458b6093e641962038a883a7a6b1884784fd4e0071812d8bb41bda7a2"
P3_DATASET_REL = "ml/ocr/graph_text_detector_v1/dataset_p3.py"
P3_DATASET_SHA256 = "ce4f912e983d5de65bbdc4339f6125875b597841c26ccec83035d31ee3d12603"
P3_MODEL_REL = "ml/ocr/graph_text_detector_v1/model_p2.py"
P3_MODEL_SHA256 = "79cee0ef1048995d3105d8214af7795b00e06a27cff8bf794569861111bfbd27"
V6_MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-train-verified-v6/manifest.json"
V6_MANIFEST_SHA256 = "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a"
MATERIALIZER_REL = "ml/ocr/stroke_seeded_xtick_v1/materialize.py"
MATERIALIZER_SHA256 = "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d"
SOURCE_HASHES = {
    "ml/synthetic/renderer.py": "c49c070afc505f92bfff7ff55b22e669c3ec2b76b56ff807b72a026ca0535283",
    "ml/synthetic/templates.py": "f59733c5f0589bd1b9143d3c9ae83015b4c53ab593de229d56e46d9f11ec2f1c",
    "ml/synthetic/fonts.py": "c4ec7a2333e0410d4c49cc41c154d3b80a2898c5682a135c25e1162a806d58fc",
    "ml/synthetic/schema.py": "237d9296f2f72a11bb7020c2abac022026f72a56e4e0eac2d322f23a3e6c8acb",
    "src/GraphReader.App/Assets/Fonts/NotoSans-Regular.ttf": "478c558ea716033cd60c03438f628dfa75694dcf6b5f6d505a2f05fd2b4f3823",
    "src/GraphReader.App/Assets/Fonts/NotoSans-Medium.ttf": "635d93d1131d791f2576de90b3bb0f7cdf61929906e8420a61b5f7f8e76420bb",
    "src/GraphReader.App/Assets/Fonts/NotoSans-SemiBold.ttf": "a4e91fd530ac2b4ef5367240144ff37d7d65d66cf76f2e9a2187b93c676f92d0",
    "ml/ocr/official_bakeoff/structure_consensus_evaluate.py": "65de1c76288c2cd9646386afb941bf641a1a87c5abfec5d76b6cb0f7a818c992",
    "ml/ocr/official_bakeoff/production_evaluate.py": "626fe9742fe83219a525e3f6b6c933b4f7c6ffcdda28c83da2bf9938380b4b3e",
    "ml/ocr/production_gate.py": "cc354ec53e4d0ecc5eab7dcf6243e5538e39f043057a949fd3d6ce84a83d50ee",
}
SEED_START = 27105100
SOURCE_COUNT = 24
SOURCE_PREFIX = "stroke-seeded-xtick-v1-p3-gate-train-"
SPLIT_DOMAIN = "stroke-seeded-p3-text-gate-source-split-v1"
THRESHOLDS = (0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90)
REGION_MATCH_IOU = 0.50


class GateError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def checked_bytes(relative: str, expected: str, limit: int) -> bytes:
    path = ROOT / relative
    if path.is_symlink() or not path.resolve(strict=True).is_relative_to(ROOT.resolve()):
        raise GateError(f"unsafe or missing project input: {relative}")
    raw = path.read_bytes()
    if not 0 < len(raw) <= limit or digest(raw) != expected:
        raise GateError(f"pinned input bytes differ: {relative}")
    return raw


def bootstrap_sources() -> dict[str, str]:
    """Pin helper bytes before importing them and compile with an empty cache."""
    actual = {MATERIALIZER_REL: digest(checked_bytes(MATERIALIZER_REL, MATERIALIZER_SHA256, 2_000_000))}
    for relative, expected in SOURCE_HASHES.items():
        actual[relative] = digest(checked_bytes(relative, expected, 20_000_000))
    actual.update({
        P3_ONNX_REL: digest(checked_bytes(P3_ONNX_REL, P3_ONNX_SHA256, 100_000_000)),
        P3_RESULT_REL: digest(checked_bytes(P3_RESULT_REL, P3_RESULT_SHA256, 100_000)),
        P3_CONFIG_REL: digest(checked_bytes(P3_CONFIG_REL, P3_CONFIG_SHA256, 100_000)),
        P3_RUNNER_REL: digest(checked_bytes(P3_RUNNER_REL, P3_RUNNER_SHA256, 2_000_000)),
        P3_DATASET_REL: digest(checked_bytes(P3_DATASET_REL, P3_DATASET_SHA256, 2_000_000)),
        P3_MODEL_REL: digest(checked_bytes(P3_MODEL_REL, P3_MODEL_SHA256, 2_000_000)),
        V6_MANIFEST_REL: digest(checked_bytes(V6_MANIFEST_REL, V6_MANIFEST_SHA256, 5_000_000)),
    })
    sys.dont_write_bytecode = True
    sys.pycache_prefix = str(ROOT / "tools" / "tmp" / f"p3-text-gate-pycache-{uuid.uuid4().hex}")
    sys.path.insert(0, str(ROOT))
    return actual


def recipes() -> list[dict[str, Any]]:
    fonts = ("vector_clean", "print_monochrome", "scan_rough", "hand_drawn")
    designs = ("ab", "aba", "abab", "multiple_baseline", "multiple_probe")
    result = []
    for index in range(SOURCE_COUNT):
        seed = SEED_START + index
        row: dict[str, Any] = {
            "source_id": f"{SOURCE_PREFIX}{seed}", "seed": seed, "split": "train", "split_role": "train",
            "design": designs[(index * 3) % len(designs)], "renderer_family": fonts[index % len(fonts)],
            "panel_count": 1 + (index % 2), "session_count": 7 + ((index * 7) % 20),
            "canvas_width": 800 + 50 * (index % 12), "panel_height": 190 + 20 * (index % 8),
            "font_size_px": 8 + 2 * (index % 11),
            "x_label_visibility": ("visible", "visible", "partial", "hidden")[index % 4],
        }
        row["source_identity_sha256"] = digest(canonical_json(row))
        result.append(row)
    return result


def _components(rows: list[dict[str, Any]], source_ids: set[str]) -> list[list[str]]:
    parent = {source: source for source in source_ids}

    def find(source: str) -> str:
        while parent[source] != source:
            parent[source] = parent[parent[source]]
            source = parent[source]
        return source

    def union(a: str, b: str) -> None:
        a, b = find(a), find(b)
        if a != b:
            parent[b] = a

    by_identity: dict[str, list[str]] = defaultdict(list)
    by_crop: dict[str, list[str]] = defaultdict(list)
    for row in rows:
        by_identity[row["source_pixel_sha256"]].append(row["source_id"])
        by_crop[row["image_sha256"]].append(row["source_id"])
    for linked in (*by_identity.values(), *by_crop.values()):
        ids = sorted(set(linked))
        for source in ids[1:]:
            union(ids[0], source)
    groups: dict[str, list[str]] = defaultdict(list)
    for source in sorted(source_ids):
        groups[find(source)].append(source)
    return [sorted(group) for group in groups.values()]


def freeze_split(rows: list[dict[str, Any]], source_ids: set[str]) -> dict[str, Any]:
    """Split whole source/hash components using identities only, before truth scoring."""
    components = _components(rows, source_ids)
    # `rows` is prefiltered to trainable single-box positives/backgrounds.
    positive_total = sum(row["kind"] == "positive" for row in rows)
    target = max(1, math.ceil(positive_total * 0.20))
    ordered = sorted((digest((SPLIT_DOMAIN + "|" + "|".join(group)).encode()), group)
                     for group in components)
    validation: list[str] = []
    positive_rows = 0
    for _, group in ordered:
        if positive_rows >= target and len(validation) >= 2:
            break
        validation.extend(group)
        positive_rows += sum(row["source_id"] in group and row["kind"] == "positive" for row in rows)
    fit = sorted(source_ids - set(validation))
    if not fit or not validation or set(fit) & set(validation):
        raise GateError("deterministic source split is empty or overlapping")
    if any(any(source in group for source in fit) and any(source in group for source in validation)
           for group in components):
        raise GateError("hash-linked source component crosses the split")
    return {"fit_source_ids": fit, "validation_source_ids": sorted(validation),
            "fit_components": [g for g in components if set(g) <= set(fit)],
            "validation_components": [g for g in components if set(g) <= set(validation)],
            "positive_target_rows_for_split": target, "positive_rows_selected_validation": positive_rows,
            "component_count": len(components), "algorithm": SPLIT_DOMAIN}


def semantic_conflict_hashes(rows: list[dict[str, Any]]) -> set[str]:
    meanings: dict[str, set[tuple[Any, ...]]] = defaultdict(set)
    for row in rows:
        target = row.get("target_crop_xyxy")
        signature = tuple(round(float(v), 4) for v in target) if target else None
        meanings[row["image_sha256"]].add((row["kind"], signature))
    return {image_hash for image_hash, signatures in meanings.items() if len(signatures) > 1}


def xyxy_iou(a: list[float], b: list[float]) -> float:
    left, top = max(a[0], b[0]), max(a[1], b[1])
    right, bottom = min(a[2], b[2]), min(a[3], b[3])
    intersection = max(0.0, right - left) * max(0.0, bottom - top)
    area_a = max(0.0, a[2] - a[0]) * max(0.0, a[3] - a[1])
    area_b = max(0.0, b[2] - b[0]) * max(0.0, b[3] - b[1])
    union = area_a + area_b - intersection
    return intersection / union if union > 0 else 0.0


def _score(rows: list[dict[str, Any]], indices: list[int], threshold: float) -> dict[str, Any]:
    positives = [i for i in indices if rows[i]["kind"] == "positive"]
    backgrounds = [i for i in indices if rows[i]["kind"] == "background_only"]
    tp = fp = fn = localized = 0
    for index in positives:
        row = rows[index]
        regions = row["regions"]
        selected = [r for r in regions if r["confidence"] >= threshold]
        if selected:
            top = max(selected, key=lambda r: (r["confidence"], -r["index"]))
            if xyxy_iou(top["box"], row["target_crop_xyxy"]) >= REGION_MATCH_IOU:
                tp += 1
                localized += 1
            else:
                fp += 1
                fn += 1
        else:
            fn += 1
    for index in backgrounds:
        if any(r["confidence"] >= threshold for r in rows[index]["regions"]):
            fp += 1
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {"positive_crops": len(positives), "background_crops": len(backgrounds),
            "true_positive_crops": tp, "false_positive_crops": fp, "false_negative_crops": fn,
            "localized_top_region_iou_ge_0_5": localized,
            "precision": precision, "recall": recall, "f1": f1}


def choose_threshold(rows: list[dict[str, Any]], fit_indices: list[int]) -> tuple[float, dict[str, Any]]:
    candidates = [(threshold, _score(rows, fit_indices, threshold)) for threshold in THRESHOLDS]
    threshold, score = max(candidates, key=lambda pair: (pair[1]["f1"], pair[1]["precision"], pair[0]))
    return threshold, {"threshold_candidates": list(THRESHOLDS), "selection_rule": "max F1; tie by precision then higher threshold",
                       "selected_threshold": threshold, "fit_scores_by_threshold": [
                           {"threshold": t, **metrics} for t, metrics in candidates], "selected_fit_metrics": score}


def _runtime_versions() -> dict[str, str]:
    import cv2
    import numpy
    import onnxruntime
    import PIL
    return {"python": sys.version.split()[0], "onnxruntime": onnxruntime.__version__,
            "opencv": cv2.__version__, "numpy": numpy.__version__, "pillow": PIL.__version__}


def run() -> dict[str, Any]:
    bound_sources = bootstrap_sources()
    # Import only after the exact source files are hashed and the bytecode cache is isolated.
    from PIL import Image
    import numpy as np
    import onnxruntime as ort
    from ml.ocr.stroke_seeded_xtick_v1 import materialize as materializer
    from ml.ocr.official_bakeoff import structure_consensus_evaluate as contract

    config_raw = checked_bytes(CONFIG_REL, CONFIG_SHA256, 65_536)
    config = json.loads(config_raw)
    if config.get("schema") != "graphreader.stroke-seeded-p3-text-gate-recipe.v1":
        raise GateError("recipe schema mismatch")
    v6 = json.loads(checked_bytes(V6_MANIFEST_REL, V6_MANIFEST_SHA256, 5_000_000))
    train_sources = {r["source_id"] for r in v6["sources"]}
    train_seeds = {int(r["recipe"]["seed"]) for r in v6["sources"]}
    v6_crop_hashes = {r["image_sha256"] for r in v6["records"]}
    recipes_rows: list[tuple[dict[str, Any], list[dict[str, Any]]]] = []
    rejected_sources = []
    for recipe in recipes():
        if recipe["source_id"] in train_sources or int(recipe["seed"]) in train_seeds:
            raise GateError("fresh source identity or seed overlaps V6 train")
        try:
            rows = materializer.materialize_recipe(recipe)
        except (materializer.MaterializationError, ValueError) as error:
            rejected_sources.append({"source_id": recipe["source_id"], "seed": recipe["seed"],
                                     "reason": str(error)})
            continue
        recipes_rows.append((recipe, rows))
    if len(recipes_rows) < 16:
        raise GateError("fewer than 16 fresh source groups materialized")

    records: list[dict[str, Any]] = []
    source_manifest = []
    for recipe, crop_rows in recipes_rows:
        source_hashes = {r["source_sha256"] for r in crop_rows}
        if len(source_hashes) != 1:
            raise GateError("full-panel pixel identity changed within one source group")
        source_manifest.append({"source_id": recipe["source_id"], "seed": recipe["seed"],
                                "source_identity_sha256": recipe["source_identity_sha256"],
                                "source_pixel_sha256": next(iter(source_hashes)), "recipe": recipe})
        for crop_index, row in enumerate(crop_rows):
            if not row["trainable_single_box"] or row["kind"] not in ("positive", "background_only"):
                continue
            image = Image.frombytes("L", (materializer.INPUT_WIDTH, materializer.INPUT_HEIGHT), row["crop_pixels"])
            encoded = BytesIO()
            image.save(encoded, format="PNG", compress_level=9)
            target = row["targets"][0]["target_crop_xyxy"] if row["kind"] == "positive" else None
            records.append({"record_id": row["case_id"], "source_id": recipe["source_id"],
                "source_pixel_sha256": row["source_sha256"], "kind": row["kind"],
                "image_sha256": digest(encoded.getvalue()), "image_png": encoded.getvalue(),
                "pixels": row["crop_pixels"], "target_crop_xyxy": target,
                "v6_image_hash_overlap": digest(encoded.getvalue()) in v6_crop_hashes,
                "exclusion": False, "regions": []})

    # Exact-byte duplicates are grouped before splitting; contradictory class/target labels are quarantined.
    conflict_hashes = semantic_conflict_hashes(records)
    for row in records:
        row["semantic_conflict"] = row["image_sha256"] in conflict_hashes
    source_ids = {source["source_id"] for source in source_manifest}
    split = freeze_split(records, source_ids)
    fit_ids, validation_ids = set(split["fit_source_ids"]), set(split["validation_source_ids"])
    if fit_ids & validation_ids:
        raise GateError("source ID leakage")
    split_by_source = {source: "fit" if source in fit_ids else "validation" for source in source_ids}
    for row in records:
        row["split"] = split_by_source[row["source_id"]]

    # The model sees pixels only. Annotation targets are not consulted until all rows are scored.
    onnx_bytes = checked_bytes(P3_ONNX_REL, P3_ONNX_SHA256, 100_000_000)
    onnx_path = ROOT / P3_ONNX_REL
    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    provider_names = session.get_providers()
    if provider_names != ["CPUExecutionProvider"]:
        raise GateError(f"unexpected ONNX providers: {provider_names}")
    tensors = []
    for row in records:
        gray = np.frombuffer(row["pixels"], dtype=np.uint8).reshape(
            (materializer.INPUT_HEIGHT, materializer.INPUT_WIDTH))
        bgr = np.repeat(gray[:, :, None], 3, axis=2)
        tensors.append(contract.detector_tensor(
            bgr.tobytes(order="C"), materializer.INPUT_WIDTH, materializer.INPUT_HEIGHT))
    input_name = session.get_inputs()[0].name
    for start in range(0, len(records), 16):
        batch = np.concatenate(tensors[start:start + 16], axis=0)
        output_batch = session.run(None, {input_name: batch})[0]
        for offset, output in enumerate(output_batch):
            row = records[start + offset]
            regions = contract.db_model_regions(output[None, ...], materializer.INPUT_WIDTH,
                                                materializer.INPUT_HEIGHT)
            row["regions"] = [{"index": i, "confidence": float(region.confidence),
                               "box": [region.bounds.left, region.bounds.top,
                                       region.bounds.right, region.bounds.bottom]}
                              for i, region in enumerate(regions)]

    usable = [i for i, row in enumerate(records)
              if not row["semantic_conflict"] and not row["v6_image_hash_overlap"]]
    fit_indices = [i for i in usable if records[i]["split"] == "fit"]
    validation_indices = [i for i in usable if records[i]["split"] == "validation"]
    if (sum(records[i]["kind"] == "positive" for i in fit_indices) < 20
            or sum(records[i]["kind"] == "positive" for i in validation_indices) < 10
            or sum(records[i]["kind"] == "background_only" for i in fit_indices) < 20
            or sum(records[i]["kind"] == "background_only" for i in validation_indices) < 10):
        raise GateError("fresh source-group split lacks a minimum class denominator")
    threshold, frozen = choose_threshold(records, fit_indices)
    baseline = {"positive_crops": sum(records[i]["kind"] == "positive" for i in validation_indices),
                "background_crops": sum(records[i]["kind"] == "background_only" for i in validation_indices)}
    baseline["true_positive_crops"] = baseline["positive_crops"]
    baseline["false_positive_crops"] = baseline["background_crops"]
    baseline["false_negative_crops"] = 0
    baseline["precision"] = baseline["true_positive_crops"] / max(1, baseline["true_positive_crops"] + baseline["false_positive_crops"])
    baseline["recall"] = 1.0 if baseline["positive_crops"] else 0.0
    baseline["f1"] = 2 * baseline["precision"] * baseline["recall"] / max(1e-12, baseline["precision"] + baseline["recall"])
    paired = _score(records, validation_indices, threshold)
    fp_reduction = 1.0 - paired["false_positive_crops"] / max(1, baseline["false_positive_crops"])
    recall_delta = baseline["recall"] - paired["recall"]
    gain = fp_reduction >= 0.20 and recall_delta <= 0.05

    source_classes = {}
    for source in source_manifest:
        source_classes[source["source_id"]] = Counter(r["kind"] for r in records
            if r["source_id"] == source["source_id"] and not r["semantic_conflict"])
    report = {
        "schema": "graphreader.stroke-seeded-p3-text-gate-report.v1", "status": "exploratory",
        "recipe_sha256": digest(config_raw), "source_hashes": bound_sources,
        "onnx_sha256": digest(onnx_bytes), "runtime_versions": _runtime_versions(),
        "onnx_provider": provider_names, "training_performed": False, "production_activation": False,
        "fresh_sources": source_manifest, "rejected_sources": rejected_sources,
        "group_split": split, "records": len(records), "unique_crop_hashes": len({r["image_sha256"] for r in records}),
        "semantic_conflict_hashes_quarantined": len(conflict_hashes),
        "semantic_conflict_rows_quarantined": sum(r["semantic_conflict"] for r in records),
        "v6_training_crop_hash_collisions_quarantined": sum(r["v6_image_hash_overlap"] for r in records),
        "source_class_counts": {key: dict(value) for key, value in source_classes.items()},
        "fit_rows": {"total": len(fit_indices), "positive": sum(records[i]["kind"] == "positive" for i in fit_indices),
                     "background": sum(records[i]["kind"] == "background_only" for i in fit_indices)},
        "validation_rows": {"total": len(validation_indices), "positive": baseline["positive_crops"],
                            "background": baseline["background_crops"]},
        "model_input_domain_limitation": "P3 was trained on 512x192 full-panel context patches; this exploratory gate scores the materializer's 256x96 stroke-seeded padded crops.",
        "p3_result_status": json.loads(checked_bytes(P3_RESULT_REL, P3_RESULT_SHA256, 100_000))["status"],
        "threshold_selection": frozen, "validation": {"scanner_only": baseline, "scanner_plus_p3_top_region_gate": paired,
            "false_positive_reduction_fraction": fp_reduction, "positive_recall_loss": recall_delta,
            "predeclared_material_gain_pass": gain},
        "metrics_are_crop_weighted": True,
        "limitations": ["The 24 synthetic groups are newly generated from the project renderer and are not real-chart validation.",
            "The scanner candidate is represented by its deterministic seed-centered crop; this does not run the scanner on full panels.",
            "P3 failed its original selection gate and is used only as a frozen region-score feature.",
            "The model's training patch scale/context differs from this crop-level input."]}
    output = ROOT / OUTPUT_REL
    output.mkdir(parents=True, exist_ok=False)
    (output / "frozen_rule.json").write_bytes(canonical_json({"schema": "graphreader.p3-text-gate-frozen-rule.v1",
        "fit_source_ids": split["fit_source_ids"], "validation_source_ids": split["validation_source_ids"],
        "threshold": threshold, "region_match_iou": REGION_MATCH_IOU,
        "fit_selection": frozen, "truth_not_used_for_runtime": True}) + b"\n")
    (output / "report.json").write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    return report


if __name__ == "__main__":
    result = run()
    print(json.dumps({"output": OUTPUT_REL, "validation": result["validation"],
                      "fit_rows": result["fit_rows"], "validation_rows": result["validation_rows"]}, indent=2))
