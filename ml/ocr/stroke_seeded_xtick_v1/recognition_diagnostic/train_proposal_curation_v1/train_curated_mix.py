# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train-only source-balanced mix using frozen V6 and ink-scan curation inputs."""

from __future__ import annotations

from collections import Counter, defaultdict
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import random
import sys
from typing import Any, Mapping, Sequence

from PIL import Image

ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parents[4]
sys.path.insert(0, str(REPO_ROOT))
from ml.ocr.stroke_seeded_xtick_v1 import train_gpu as p2
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers

CONFIG = ROOT / "curated_mix_v1.json"
BASE_CONFIG = REPO_ROOT / "ml/ocr/stroke_seeded_xtick_v1/training/p2.json"
CURATION_RELATIVE = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2/manifest.json"
OUTPUT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-proposal-curation-mix-v1-rx6600xt"
MANIFEST_SHA256 = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
_RUN = {"phase": "starting", "steps": 0}


class CuratedMixError(RuntimeError):
    pass


def digest(value: bytes) -> str:
    return sha256(value).hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _target_signature(kind: str, annotation: Mapping[str, Any]) -> tuple[Any, ...]:
    targets = annotation.get("targets")
    if kind == "background_only":
        if targets != [] or annotation.get("expected_tick_label") is not None:
            raise CuratedMixError("background crop carries text labels")
        return kind, None, None
    if kind != "positive" or not isinstance(targets, list) or len(targets) != 1:
        raise CuratedMixError("trainable crop must be one positive box or an empty background")
    target = targets[0]
    text = target.get("text")
    box = target.get("target_crop_xyxy")
    if not isinstance(text, str) or not isinstance(box, list) or len(box) != 4:
        raise CuratedMixError("positive crop target is malformed")
    coords = tuple(float(x) for x in box)
    if not (0 <= coords[0] < coords[2] <= 256 and 0 <= coords[1] < coords[3] <= 96):
        raise CuratedMixError("positive target box is outside crop canvas")
    if text != annotation.get("expected_tick_label"):
        raise CuratedMixError("positive target text differs from expected label")
    return kind, text, coords


def _reject_semantic_label_conflicts(groups: Mapping[str, set[tuple[Any, ...]]], scope: str) -> None:
    for image_hash, signatures in groups.items():
        if len(signatures) != 1:
            raise CuratedMixError(f"same-image semantic-label conflict quarantined in {scope}: {image_hash}")


def _checked_image(image_bytes: bytes, case_id: str) -> Any:
    try:
        with Image.open(BytesIO(image_bytes)) as source:
            if source.size != (256, 96) or source.mode not in ("L", "P"):
                raise CuratedMixError(f"crop canvas/mode mismatch: {case_id}")
            return __import__("numpy").asarray(source.convert("L"), dtype=__import__("numpy").float32) / 255.0
    except OSError as error:
        raise CuratedMixError(f"crop image decode failed: {case_id}") from error


def _read_json(root: Path, relative: str, label: str, limit: int) -> tuple[dict[str, Any], bytes, str]:
    bound = helpers._bound_read(root, relative, label, limit)
    return helpers._parse_json(bound, label), bound.value, bound.sha256


def load_curated_rows(config: Mapping[str, Any], base: Mapping[str, Any]) -> dict[str, Any]:
    bound = helpers._bound_read(REPO_ROOT, CURATION_RELATIVE, "pinned curation manifest", 12_000_000)
    if bound.sha256 != MANIFEST_SHA256 or bound.sha256 != config["curation_manifest_sha256"]:
        raise CuratedMixError("curation manifest bytes differ from pinned recipe")
    manifest = helpers._parse_json(bound, "curation manifest")
    expected = {
        "schema": "graphreader.stroke-seeded-xtick-v1-inkscan-curated-train.v1",
        "status": "completed-with-source-failures", "split": "train",
        "training_performed": False, "optimizer_steps": 0,
        "input_manifest_path": p2.MANIFEST_RELATIVE,
        "input_manifest_sha256": config["base_manifest_sha256"],
        "curator_sha256": config["curator_sha256"],
        "materializer_sha256": config["materializer_sha256"],
        "scanner_source_sha256": config["scanner_source_sha256"],
        "scanner_project_sha256": config["scanner_project_sha256"],
        "scanner_binary_sha256": config["scanner_binary_sha256"],
        "production_approval": False, "release_eligible": False,
    }
    for key, value in expected.items():
        if manifest.get(key) != value:
            raise CuratedMixError(f"curation manifest binding mismatch: {key}")
    if manifest.get("curated_counts") != {"positive": 105, "background_only": 368, "excluded": 423}:
        raise CuratedMixError("curation row counts differ from the frozen manifest contract")
    records = manifest.get("records")
    sources = manifest.get("source_groups")
    if not isinstance(records, list) or len(records) != 896 or not isinstance(sources, list) or len(sources) != 24:
        raise CuratedMixError("curation inventory is malformed")
    base_sources = {s["source_id"]: s["source_identity_sha256"] for s in base["manifest"]["sources"]}
    curated_sources: dict[str, str] = {}
    failed = manifest.get("scanner_failed_sources")
    if (not isinstance(failed, list) or len(failed) != 1
            or failed[0].get("source_id") != "stroke-seeded-xtick-v1-train-27103111"):
        raise CuratedMixError("curation failed-source receipt changed")
    failed_source_id = failed[0]["source_id"]
    for source in sources:
        sid, identity = source.get("source_id"), source.get("source_identity_sha256")
        if not isinstance(sid, str) or not isinstance(identity, str) or sid in curated_sources:
            raise CuratedMixError("curation source identity is malformed or duplicated")
        if base_sources.get(sid) != identity:
            raise CuratedMixError("curation source group is not the pinned V6 source")
        curated_sources[sid] = identity

    counts = Counter()
    seen_cases: set[str] = set()
    parsed: list[dict[str, Any]] = []
    for row in records:
        if not isinstance(row, dict) or row.get("split") != "train":
            raise CuratedMixError("non-train or malformed curation row")
        kind = row.get("kind")
        if kind not in ("positive", "background_only", "excluded"):
            raise CuratedMixError("unknown curation row kind")
        counts[kind] += 1
        case_id, source_id = row.get("case_id"), row.get("source_id")
        if not isinstance(case_id, str) or case_id in seen_cases:
            raise CuratedMixError("duplicate curation case ID")
        seen_cases.add(case_id)
        if row.get("source_group_id") != source_id or curated_sources.get(source_id) != row.get("source_identity_sha256"):
            raise CuratedMixError("curation row source group identity mismatch")
        if source_id == failed_source_id:
            raise CuratedMixError("failed scanner source has curation rows and is quarantined")
        if kind == "excluded":
            if row.get("trainable_single_box") is not False:
                raise CuratedMixError("excluded curation row is marked trainable")
            continue
        if row.get("trainable_single_box") is not True:
            raise CuratedMixError("curated train row lacks trainable_single_box admission")
        image = helpers._bound_read(bound.path.parent, row["image_path"], f"curated image {case_id}", 1_000_000)
        annotation_bound = helpers._bound_read(bound.path.parent, row["annotation_path"], f"curated annotation {case_id}", 65_536)
        if image.sha256 != row.get("image_sha256") or annotation_bound.sha256 != row.get("annotation_sha256"):
            raise CuratedMixError("curated image or annotation bytes differ from manifest")
        annotation = helpers._parse_json(annotation_bound, f"curated annotation {case_id}")
        for key in ("case_id", "source_id", "source_identity_sha256", "source_group_id",
                    "split", "kind", "trainable_single_box", "image_sha256"):
            if annotation.get(key) != row.get(key):
                raise CuratedMixError(f"curation annotation binding mismatch: {case_id}:{key}")
        if annotation.get("exclusion_reasons"):
            raise CuratedMixError(f"trainable curation row carries exclusion reasons: {case_id}")
        semantic = _target_signature(kind, annotation)
        box = None if kind == "background_only" else tuple(float(v) / s for v, s in zip(semantic[2], (256, 96, 256, 96)))
        parsed.append({"case_id": case_id, "source_id": source_id,
                       "source_identity_sha256": row["source_identity_sha256"],
                       "kind": kind, "image_sha256": image.sha256,
                       "semantic": semantic, "pixels": _checked_image(image.value, case_id),
                       "box": box, "annotation_sha256": annotation_bound.sha256,
                       "image_bytes_sha256": image.sha256})
    if counts != Counter({"positive": 105, "background_only": 368, "excluded": 423}):
        raise CuratedMixError(f"curation kinds differ from pinned counts: {dict(counts)}")
    return {"manifest_sha256": bound.sha256, "manifest": manifest,
            "trainable_rows": parsed, "kind_counts": dict(counts),
            "failed_source_id": failed_source_id}


def combine_deduplicated(base: Mapping[str, Any], curated: Mapping[str, Any]) -> dict[str, Any]:
    """Keep all V6 rows; add one curated row per new semantic image hash."""
    base_manifest_rows = {r["case_id"]: r for r in base["manifest"]["records"]}
    base_semantics: dict[str, set[tuple[Any, ...]]] = defaultdict(set)
    for index, row in enumerate(base["rows"]):
        original = base_manifest_rows[row["case_id"]]
        ann, _, _ = _read_json(REPO_ROOT / "artifacts/stroke-seeded-xtick-v1-train-verified-v6",
                               original["annotation_path"], "base annotation", 65_536)
        sig = _target_signature(row["kind"], ann)
        base_semantics[original["image_sha256"]].add(sig)
    cur_semantics: dict[str, set[tuple[Any, ...]]] = defaultdict(set)
    for row in curated["trainable_rows"]:
        cur_semantics[row["image_sha256"]].add(row["semantic"])
    _reject_semantic_label_conflicts(base_semantics, "V6")
    _reject_semantic_label_conflicts(cur_semantics, "curation")
    for image_hash in set(base_semantics) & set(cur_semantics):
        if base_semantics[image_hash] != cur_semantics[image_hash]:
            raise CuratedMixError(f"cross-manifest image-label conflict quarantined: {image_hash}")

    base_hashes = set(base_semantics)
    seen_curated: set[str] = set()
    additions = []
    duplicate_within = 0
    duplicate_base = 0
    for row in sorted(curated["trainable_rows"], key=lambda x: x["case_id"]):
        image_hash = row["image_sha256"]
        if image_hash in base_hashes:
            duplicate_base += 1
            continue
        if image_hash in seen_curated:
            duplicate_within += 1
            continue
        seen_curated.add(image_hash)
        additions.append(row)
    if Counter(r["kind"] for r in additions) != Counter({"positive": 77, "background_only": 71}):
        raise CuratedMixError("deduplicated curated class counts differ from verified train audit")

    rows = list(base["rows"])
    pixels = list(base["pixels"])
    boxes = list(base["boxes"])
    hashes = [base_manifest_rows[r["case_id"]]["image_sha256"] for r in base["rows"]]
    for row in additions:
        rows.append({k: row[k] for k in ("case_id", "source_id", "source_identity_sha256")})
        pixels.append(row["pixels"])
        boxes.append(row["box"])
        hashes.append(row["image_sha256"])
    return {"rows": rows, "pixels": pixels, "boxes": boxes, "image_hashes": hashes,
            "base_count": len(base["rows"]), "curated_rows_admitted": len(curated["trainable_rows"]),
            "curated_unique_addition_count": len(additions), "curated_additions": additions,
            "curated_duplicates_vs_base": duplicate_base,
            "curated_duplicate_rows_internal": duplicate_within,
            "base_unique_image_hashes": len(base_hashes),
            "combined_unique_image_hashes": len(set(hashes)),
            "base_semantic_image_hash_count": len(base_semantics)}


def _rotated_base_subset(indices: Sequence[int], count: int, epoch: int, seed: int) -> tuple[int, ...]:
    if count > len(indices):
        raise CuratedMixError("base sample quota exceeds available rows")
    order = list(indices)
    random.Random(seed).shuffle(order)
    skipped = len(order) - count
    excluded_start = (epoch * skipped) % len(order)
    excluded = {(excluded_start + i) % len(order) for i in range(skipped)}
    return tuple(index for position, index in enumerate(order) if position not in excluded)


def _source_balanced_curated(rows: Sequence[Mapping[str, Any]], count: int, epoch: int, seed: int) -> tuple[int, ...]:
    if count > len(rows):
        raise CuratedMixError("curated quota exceeds unique admitted rows")
    by_source: dict[str, list[int]] = defaultdict(list)
    for index, row in enumerate(rows):
        by_source[str(row["source_id"])].append(index)
    groups = sorted(by_source)
    if not groups:
        raise CuratedMixError("curated class has no source groups")
    rotation = epoch % len(groups)
    groups = groups[rotation:] + groups[:rotation]
    row_order: dict[str, list[int]] = {}
    for group, group_indices in by_source.items():
        group_indices = sorted(group_indices, key=lambda i: str(rows[i]["case_id"]))
        row_rotation = (epoch + seed) % len(group_indices)
        row_order[group] = group_indices[row_rotation:] + group_indices[:row_rotation]
    cursors = Counter()
    chosen: list[int] = []
    while len(chosen) < count:
        changed = False
        for group in groups:
            cursor = cursors[group]
            if cursor < len(row_order[group]):
                chosen.append(row_order[group][cursor])
                cursors[group] += 1
                changed = True
                if len(chosen) == count:
                    break
        if not changed:
            raise CuratedMixError("source-balanced sampler exhausted curated rows")
    return tuple(chosen)


def sample_epoch(data: Mapping[str, Any], epoch: int, config: Mapping[str, Any]) -> tuple[tuple[int, ...], dict[str, Any]]:
    base_count = int(data["base_count"])
    boxes = data["boxes"]
    curated = data["curated_additions"]
    base_pos = tuple(i for i in range(base_count) if boxes[i] is not None)
    base_neg = tuple(i for i in range(base_count) if boxes[i] is None)
    cur_pos_rows = [r for r in curated if r["kind"] == "positive"]
    cur_neg_rows = [r for r in curated if r["kind"] == "background_only"]
    cur_pos_ids = _source_balanced_curated(cur_pos_rows, 24, epoch, int(config["shuffle_seed"]))
    cur_neg_ids = _source_balanced_curated(cur_neg_rows, 24, epoch, int(config["shuffle_seed"]) + 97)
    cur_pos_lookup = {r["case_id"]: base_count + i for i, r in enumerate(curated)}
    cur_neg_lookup = cur_pos_lookup
    chosen_cur_pos = [cur_pos_lookup[cur_pos_rows[i]["case_id"]] for i in cur_pos_ids]
    chosen_cur_neg = [cur_neg_lookup[cur_neg_rows[i]["case_id"]] for i in cur_neg_ids]
    chosen_base_pos = _rotated_base_subset(base_pos, 211, epoch, int(config["shuffle_seed"]) + 101)
    chosen_base_neg = _rotated_base_subset(base_neg, 385, epoch, int(config["shuffle_seed"]) + 211)
    selected_pos = tuple(chosen_base_pos + tuple(chosen_cur_pos))
    selected_neg = tuple(chosen_base_neg + tuple(chosen_cur_neg))
    selected = selected_pos + selected_neg
    if len(selected) != 644 or len(set(selected)) != 644 or len(selected_pos) != 235 or len(selected_neg) != 409:
        raise CuratedMixError("epoch sampler changed class totals or repeated a row")
    from ml.ocr.fragmented_xtick_detector_v3.gpu_train import stratified_batches
    batches = stratified_batches(selected_pos, selected_neg, 7, int(config["shuffle_seed"]) + epoch)
    if len(batches) != 92 or len({i for batch in batches for i in batch}) != 644:
        raise CuratedMixError("batch helper did not consume each selected row exactly once")
    all_hashes = data["image_hashes"]
    selected_hashes = [all_hashes[i] for i in selected]
    hash_counts = Counter(selected_hashes)
    source_counts = Counter(str(data["rows"][i]["source_id"]) for i in selected)
    cur_pos_sources = Counter(data_source for data_source in
                              (data["rows"][i]["source_id"] for i in chosen_cur_pos))
    cur_neg_sources = Counter(data_source for data_source in
                              (data["rows"][i]["source_id"] for i in chosen_cur_neg))
    stats = {
        "selected_rows": len(selected), "positive_rows": len(selected_pos), "background_rows": len(selected_neg),
        "batch_count": len(batches), "base_positive_rows": len(chosen_base_pos),
        "base_background_rows": len(chosen_base_neg), "curated_positive_rows": len(chosen_cur_pos),
        "curated_background_rows": len(chosen_cur_neg), "unique_image_hashes": len(hash_counts),
        "repeated_image_hash_rows": sum(n - 1 for n in hash_counts.values() if n > 1),
        "max_same_image_multiplicity": max(hash_counts.values()),
        "source_row_counts": dict(sorted(source_counts.items())),
        "curated_positive_by_source": dict(sorted(cur_pos_sources.items())),
        "curated_background_by_source": dict(sorted(cur_neg_sources.items())),
    }
    return batches, stats


def load_config() -> tuple[dict[str, Any], bytes, dict[str, Any]]:
    bound = helpers._bound_read(REPO_ROOT, CONFIG.relative_to(REPO_ROOT).as_posix(), "curated mix config", 65_536)
    config = helpers._parse_json(bound, "curated mix config")
    expected = {
        "schema": "graphreader.stroke-seeded-xtick-v1-curated-mix-config.v1",
        "recipe_id": "stroke-seeded-xtick-v1-proposal-curation-source-balanced-mix-v1",
        "model_identity": "stroke-seeded-xtick-spatial-locator-rf31-logwh-curated-source-balanced-mix-v1",
        "train_manifest_path": p2.MANIFEST_RELATIVE,
        "curation_manifest_path": CURATION_RELATIVE,
        "base_manifest_sha256": "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a",
        "curation_manifest_sha256": MANIFEST_SHA256,
        "base_config_sha256": "1ae28371439c7106631337c12db8f1661f7152778532628db8b3163c6b4cd5e5",
        "base_checkpoint_sha256": "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351",
        "base_trainer_sha256": "dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6",
        "curator_sha256": "7e8a32dda6b5cf74f013db0992bd66ed894301dca21cf94ea32f64f1275c32df",
        "materializer_sha256": "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d",
        "scanner_source_sha256": "40b104c23f0b60ca029dbe596dbd46117211d4f32b13837679380e4da47a8780",
        "scanner_project_sha256": "7a6f7599b67f6e09425cc8168c894e0f2263f66e91bb2c254f593409a5ec3632",
        "scanner_binary_sha256": "7c77eaa5c1949d5f7e22c35b32d4751e24c6a2bed7e8664b77a50b032ebaa831",
        "epochs": 30, "batch_size": 7, "steps_per_epoch": 92, "expected_optimizer_steps": 2760,
        "curated_per_class_per_epoch": 24, "model_seed": 27103151, "shuffle_seed": 27103152,
        "learning_rate": 0.001, "hard_negative_top_k_per_image": 32,
        "optimizer": "AdamW",
        "sampling_policy": "rotate source-balanced unique curation rows plus rotating V6 row subsets; 644 rows per epoch",
        "required_launcher": "tools/gpu-training/Run-GpuPython.ps1",
        "required_device_name_substring": "RX 6600 XT", "cpu_fallback_allowed": False,
        "train_only": True, "probe_input_allowed": False, "dev_input_allowed": False,
        "sealed_input_allowed": False, "private_input_allowed": False,
        "production_approval": False, "release_eligible": False,
        "planned_output_root": OUTPUT_RELATIVE,
        "expected_runner_sha256": digest(Path(__file__).read_bytes()),
    }
    for key, value in expected.items():
        if config.get(key) != value:
            raise CuratedMixError(f"curated mix recipe/config mismatch: {key}")
    if config.get("initialization") != "fresh random initialization using the P2 model seed; P2 checkpoint not loaded":
        raise CuratedMixError("new recipe must use fresh P2-seed initialization")
    p2_config, p2_bytes = p2.load_config(BASE_CONFIG)
    if digest(p2_bytes) != config["base_config_sha256"] or digest(Path(p2.__file__).read_bytes()) != config["base_trainer_sha256"]:
        raise CuratedMixError("committed P2 config or trainer changed")
    if digest((REPO_ROOT / "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/checkpoint.pt").read_bytes()) != config["base_checkpoint_sha256"]:
        raise CuratedMixError("frozen P2 baseline checkpoint hash changed")
    return config, bound.value, p2_config


def preflight() -> dict[str, Any]:
    """Validate pinned inputs, collisions, class counts, and all 30 sampling epochs."""
    config, config_bytes, p2_config = load_config()
    base = p2.load_train_inputs(p2_config)
    curated = load_curated_rows(config, base)
    data = combine_deduplicated(base, curated)
    coverage: set[int] = set()
    epoch_summaries = []
    for epoch in range(int(config["epochs"])):
        batches, stats = sample_epoch(data, epoch, config)
        flat = [i for batch in batches for i in batch]
        if (len(batches) != 92 or len(flat) != 644 or len(set(flat)) != 644
                or sum(data["boxes"][i] is not None for i in flat) != 235):
            raise CuratedMixError(f"epoch {epoch + 1} violates fixed class/step contract")
        coverage.update(flat)
        epoch_summaries.append({"epoch": epoch + 1,
            "unique_image_hashes": stats["unique_image_hashes"],
            "repeated_image_hash_rows": stats["repeated_image_hash_rows"],
            "max_same_image_multiplicity": stats["max_same_image_multiplicity"],
            "curated_positive_by_source": stats["curated_positive_by_source"],
            "curated_background_by_source": stats["curated_background_by_source"]})
    if coverage != set(range(len(data["rows"]))):
        raise CuratedMixError("30-epoch rotation does not cover every admitted base and curated row")
    return {"status": "preflight-passed-no-training", "recipe_id": config["recipe_id"],
            "config_sha256": digest(config_bytes), "trainer_sha256": digest(Path(__file__).read_bytes()),
            "base_manifest_sha256": base["manifest_sha256"],
            "curation_manifest_sha256": curated["manifest_sha256"],
            "v6_rows": base["admitted_count"], "curation_admitted_counts": curated["kind_counts"],
            "curation_unique_additions": data["curated_unique_addition_count"],
            "curation_duplicates_vs_v6": data["curated_duplicates_vs_base"],
            "curation_internal_duplicate_rows": data["curated_duplicate_rows_internal"],
            "same_image_semantic_conflicts": 0,
            "combined_rows": len(data["rows"]),
            "combined_unique_image_hashes": data["combined_unique_image_hashes"],
            "failed_curation_source_id": curated["failed_source_id"],
            "optimizer_steps": 0, "sampled_epochs": epoch_summaries,
            "dev_sealed_private_probe_inputs_read": False}


def _train(config: Mapping[str, Any], config_bytes: bytes, output: Path, p2_config: Mapping[str, Any]) -> dict[str, Any]:
    if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
        raise CuratedMixError("training must run via project RX 6600 XT GPU launcher")
    import numpy as np
    import torch
    from torch import nn

    _RUN["phase"] = "rx6600xt-binding"
    runtime = helpers.require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    torch.manual_seed(int(config["model_seed"]))
    np.random.seed(int(config["model_seed"]) % (2**32))
    _RUN["phase"] = "pinned-train-only-input-admission"
    base = p2.load_train_inputs(p2_config)
    curated = load_curated_rows(config, base)
    data = combine_deduplicated(base, curated)
    features = torch.from_numpy(np.stack(data["pixels"]).astype(np.float32)).unsqueeze(1)
    boxes = tuple(data["boxes"])
    assignments = helpers.target_assignments(boxes)
    count = len(boxes)
    objectness = torch.zeros((count, 1, 12, 32), dtype=torch.float32)
    box_targets = torch.zeros((count, 4, 12, 32), dtype=torch.float32)
    positive_mask = torch.zeros((count, 1, 12, 32), dtype=torch.bool)
    for i, assignment in enumerate(assignments):
        if assignment is not None:
            row, col, target = assignment
            objectness[i, 0, row, col] = 1.0
            box_targets[i, :, row, col] = torch.tensor(target, dtype=torch.float32)
            positive_mask[i, 0, row, col] = True
    _RUN["phase"] = "model-construction"
    model = helpers.build_spatial_locator(torch).to(device)
    rf = helpers.feature_receptive_field(model.features, torch)
    if rf != 31 or any(parameter.device != device for parameter in model.parameters()):
        raise CuratedMixError("P2 architecture or RX 6600 XT model residency differs")
    with torch.no_grad():
        shape = model(features[:1].to(device))[0].shape[-2:]
    if list(shape) != [12, 32]:
        raise CuratedMixError("model output grid differs from P2")
    optimizer = torch.optim.AdamW(model.parameters(), lr=float(config["learning_rate"]))
    before_cells = helpers.evaluate_train_fit(model, features, objectness, box_targets, positive_mask, device, torch)
    before_images = p2._image_metrics(model, features, boxes, device, torch, p2_config)
    epoch_reports: list[dict[str, Any]] = []
    losses: list[float] = []
    pos_losses: list[float] = []
    neg_losses: list[float] = []
    coverage: set[int] = set()
    unique_epoch_hashes: list[int] = []
    repeat_epoch_rows: list[int] = []
    max_mults: list[int] = []
    base_indices = set(range(data["base_count"]))
    curated_indices = set(range(data["base_count"], count))
    for epoch in range(int(config["epochs"])):
        batches, sample_stats = sample_epoch(data, epoch, config)
        flattened = [i for batch in batches for i in batch]
        if len(flattened) != 644 or len(set(flattened)) != 644:
            raise CuratedMixError("epoch batches do not uniquely consume the selected 644 rows")
        coverage.update(flattened)
        epoch_losses: list[float] = []
        for indices in batches:
            x = features[list(indices)].to(device)
            obj, target, mask = objectness[list(indices)].to(device), box_targets[list(indices)].to(device), positive_mask[list(indices)].to(device)
            if any(t.device != device for t in (x, obj, target, mask)):
                raise CuratedMixError("training batch left RX 6600 XT")
            optimizer.zero_grad(set_to_none=True)
            logits, predicted = model(x)
            per_cell = nn.functional.binary_cross_entropy_with_logits(logits, obj, reduction="none")
            obj_loss, pos_loss, neg_loss, _ = p2.balanced_objectness_loss(logits, obj, per_cell, 32)
            box_loss = nn.functional.smooth_l1_loss(predicted[mask.expand(-1, 4, -1, -1)], target[mask.expand(-1, 4, -1, -1)])
            loss = obj_loss + box_loss
            if not torch.isfinite(loss):
                raise CuratedMixError("non-finite training loss")
            loss.backward(); optimizer.step(); _RUN["steps"] += 1
            value = float(loss.detach().cpu()); epoch_losses.append(value); losses.append(value)
            pos_losses.append(float(pos_loss.detach().cpu())); neg_losses.append(float(neg_loss.detach().cpu()))
        unique_epoch_hashes.append(int(sample_stats["unique_image_hashes"]))
        repeat_epoch_rows.append(int(sample_stats["repeated_image_hash_rows"]))
        max_mults.append(int(sample_stats["max_same_image_multiplicity"]))
        cells = helpers.evaluate_train_fit(model, features, objectness, box_targets, positive_mask, device, torch)
        images = p2._image_metrics(model, features, boxes, device, torch, p2_config)
        epoch_reports.append({"epoch": epoch + 1, "loss_mean": sum(epoch_losses) / len(epoch_losses),
                              "sample": sample_stats, "train_pool_cell_metrics": cells,
                              "train_pool_image_metrics": images})
    if _RUN["steps"] != int(config["expected_optimizer_steps"]):
        raise CuratedMixError("optimizer step count differs from new recipe")
    if not base_indices.issubset(coverage) or not curated_indices.issubset(coverage):
        raise CuratedMixError("rotating sampler failed to cover all admitted rows")
    after_cells = helpers.evaluate_train_fit(model, features, objectness, box_targets, positive_mask, device, torch)
    after_images = p2._image_metrics(model, features, boxes, device, torch, p2_config)
    checkpoint = output / "checkpoint.pt"
    torch.save({"state_dict": model.state_dict(), "model_identity": config["model_identity"],
                "architecture": p2_config["architecture"], "box_parameterization": p2_config["box_parameterization"],
                "receptive_field_pixels": rf, "base_manifest_sha256": base["manifest_sha256"],
                "curation_manifest_sha256": curated["manifest_sha256"], "recipe_id": config["recipe_id"],
                "model_seed": config["model_seed"], "initialization": config["initialization"],
                "optimizer_steps": _RUN["steps"]}, checkpoint)
    report = {
        "schema": "graphreader.stroke-seeded-xtick-v1-curated-mix-report.v1",
        "status": "completed-unregistered-exploratory-train-fit", "recipe_id": config["recipe_id"],
        "model_identity": config["model_identity"], "config_sha256": digest(config_bytes),
        "trainer_sha256": digest(Path(__file__).read_bytes()), "base_trainer_sha256": config["base_trainer_sha256"],
        "base_config_sha256": config["base_config_sha256"], "base_manifest_sha256": base["manifest_sha256"],
        "curation_manifest_sha256": curated["manifest_sha256"], "curator_sha256": config["curator_sha256"],
        "input_counts": {"v6_rows_retained": data["base_count"], "v6_positive": 235, "v6_background": 409,
                         "curation_admitted_before_dedup": curated["kind_counts"],
                         "curated_excluded_rows_skipped": 423,
                         "curated_rows_duplicate_of_v6_image": data["curated_duplicates_vs_base"],
                         "curated_internal_duplicate_rows_skipped": data["curated_duplicate_rows_internal"],
                         "curated_unique_added": data["curated_unique_addition_count"],
                         "curated_unique_positive_added": 77, "curated_unique_background_added": 71,
                         "combined_rows": count, "combined_unique_image_hashes": data["combined_unique_image_hashes"],
                         "v6_unique_image_hashes": data["base_unique_image_hashes"]},
        "same_image_semantic_conflicts": 0, "source_group_overlap": "same V6 train groups; not source-disjoint",
        "failed_curation_source_id": curated["failed_source_id"],
        "sampling_contract": {"per_epoch_rows": 644, "positive_rows": 235, "background_rows": 409,
                              "v6_rows_per_epoch": 596, "curated_rows_per_epoch": 48,
                              "curated_per_class": 24, "v6_rows_rotated_to_cover_all_rows": True,
                              "curated_sources_sampled_round_robin": True,
                              "repeats_are_image_hash_duplicates_in_retained_v6_rows": True,
                              "epoch_unique_hashes_min": min(unique_epoch_hashes),
                              "epoch_unique_hashes_max": max(unique_epoch_hashes),
                              "epoch_repeated_hash_rows_min": min(repeat_epoch_rows),
                              "epoch_repeated_hash_rows_max": max(repeat_epoch_rows),
                              "max_same_image_multiplicity_any_epoch": max(max_mults)},
        "epochs": int(config["epochs"]), "batch_size": 7, "steps_per_epoch": 92,
        "optimizer_steps": _RUN["steps"], "model_seed": config["model_seed"],
        "shuffle_seed": config["shuffle_seed"], "initialization": config["initialization"],
        "checkpoint_loaded": False, "learning_rate": config["learning_rate"], "hard_negative_top_k_per_image": 32,
        "runtime": runtime, "receptive_field_pixels": rf,
        "train_fit_before_cell_metrics": before_cells, "train_fit_before_image_metrics": before_images,
        "train_fit_after_cell_metrics": after_cells, "train_fit_after_image_metrics": after_images,
        "epoch_train_fit": epoch_reports, "loss_first": losses[0], "loss_last": losses[-1],
        "positive_cell_bce_first": pos_losses[0], "positive_cell_bce_last": pos_losses[-1],
        "background_cell_bce_first": neg_losses[0], "background_cell_bce_last": neg_losses[-1],
        "train_fit_is_held_out_validation": False,
        "dev_sealed_private_probe_inputs_read": False,
        "production_approval": False, "release_eligible": False,
        "checkpoint_path": checkpoint.relative_to(REPO_ROOT).as_posix(),
        "checkpoint_sha256": digest(checkpoint.read_bytes()), "finished_utc": datetime.now(timezone.utc).isoformat(),
    }
    (output / "report.json").write_bytes(canonical(report))
    return report


def run() -> dict[str, Any]:
    _RUN.update(phase="starting", steps=0)
    config, config_bytes, p2_config = load_config()
    output = REPO_ROOT / OUTPUT_RELATIVE
    if output.parent != REPO_ROOT / "artifacts" or output.exists() or output.is_symlink():
        raise CuratedMixError("planned output exists or is unsafe")
    helpers._checked_chain(REPO_ROOT, output.parent, "curated mix output")
    output.mkdir(parents=False, exist_ok=False)
    (output / "started.json").write_bytes(canonical({"status": "started", "recipe_id": config["recipe_id"],
        "config_sha256": digest(config_bytes), "trainer_sha256": digest(Path(__file__).read_bytes()),
        "started_utc": datetime.now(timezone.utc).isoformat(), "optimizer_steps_completed": 0}))
    try:
        return _train(config, config_bytes, output, p2_config)
    except Exception as error:
        (output / "failure.json").write_bytes(canonical({"status": "failed", "recipe_id": config.get("recipe_id"),
            "config_sha256": digest(config_bytes), "trainer_sha256": digest(Path(__file__).read_bytes()),
            "failure_type": type(error).__name__, "failure_message": str(error), "phase": _RUN["phase"],
            "optimizer_steps_completed": _RUN["steps"], "dev_sealed_private_inputs_read": False,
            "failed_utc": datetime.now(timezone.utc).isoformat()}))
        raise


if __name__ == "__main__":
    if sys.argv[1:] == ["--preflight-only"]:
        print(json.dumps(preflight(), sort_keys=True, indent=2))
    elif sys.argv[1:]:
        raise SystemExit("usage: train_curated_mix.py [--preflight-only]")
    else:
        print(json.dumps(run(), sort_keys=True, indent=2))
