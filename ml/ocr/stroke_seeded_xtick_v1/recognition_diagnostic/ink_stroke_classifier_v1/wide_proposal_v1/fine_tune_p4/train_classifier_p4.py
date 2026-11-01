# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded P4 fine-tune of frozen P3 on the fixed P3 train split plus 19 crops."""

from __future__ import annotations

from collections import Counter, defaultdict
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import importlib.util
import inspect
import json
from pathlib import Path
import random
import stat
import sys
from typing import Any

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[7]
HERE = Path(__file__).resolve().parent
CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/p4.json"
P3_RUNNER_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/train_classifier_p3.py"
P3_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/p3.json"
P3_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-sourceuniform-60ep-rx6600xt/report.json"
P3_MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2/manifest.json"
P3_CURATED_ROOT = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2"
CANDIDATE_ROOT = "artifacts/stroke-seeded-xtick-v1-wideproposal-train-candidates-p3"
P3_CHECKPOINT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-sourceuniform-60ep-rx6600xt/checkpoint.pt"
P3_CHECKPOINT_SHA = "8b1f8ce7d9897e3db19e71a4f21576b92e51ae56a90aa1fec9fb1df364820c92"
P3_CONFIG_SHA = "927b93c88d7e61dc505516995b72f2235aa7fc8824e5fc54b45ae6741c3c5717"
P3_REPORT_SHA = "aa57c136d7a6272ca1d0f3590d8ad7404413fdae85be24db39986a3952f854bf"
P3_MANIFEST_SHA = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
CANDIDATE_MANIFEST_SHA = "d02190e6260a18769e2fba06f286670d69bd78a9640a58078bee1fc83567cce5"
OVERLAP_AUDIT_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p4/p4_overlap_audit.json"
OVERLAP_AUDIT_SHA = "a42c60882517bcf2a3e45043f83eda029374f6070dea0a45477b550218b40196"
BASE_OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-rx6600xt"
PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p4-wideproposal-finetune-preflight-v4.json"
TRAIN_GROUPS = frozenset({
    "stroke-seeded-xtick-v1-train-27103100", "stroke-seeded-xtick-v1-train-27103101",
    "stroke-seeded-xtick-v1-train-27103102", "stroke-seeded-xtick-v1-train-27103104",
    "stroke-seeded-xtick-v1-train-27103105", "stroke-seeded-xtick-v1-train-27103106",
    "stroke-seeded-xtick-v1-train-27103107", "stroke-seeded-xtick-v1-train-27103108",
    "stroke-seeded-xtick-v1-train-27103109", "stroke-seeded-xtick-v1-train-27103110",
    "stroke-seeded-xtick-v1-train-27103112", "stroke-seeded-xtick-v1-train-27103113",
    "stroke-seeded-xtick-v1-train-27103116", "stroke-seeded-xtick-v1-train-27103117",
    "stroke-seeded-xtick-v1-train-27103118", "stroke-seeded-xtick-v1-train-27103119",
    "stroke-seeded-xtick-v1-train-27103120", "stroke-seeded-xtick-v1-train-27103122",
    "stroke-seeded-xtick-v1-train-27103123",
})
HOLDOUT_GROUPS = frozenset({
    "stroke-seeded-xtick-v1-train-27103103", "stroke-seeded-xtick-v1-train-27103114",
    "stroke-seeded-xtick-v1-train-27103115", "stroke-seeded-xtick-v1-train-27103121",
})
MAX_MANIFEST = 4_000_000
MAX_ANNOTATION = 65_536
MAX_IMAGE = 1_000_000


class P4Error(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def read_project(relative: str, limit: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or limit <= 0:
        raise P4Error("invalid bounded project-relative path")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        try:
            info = current.lstat()
        except OSError as error:
            raise P4Error(f"missing bounded input: {relative}") from error
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise P4Error(f"reparse component rejected: {relative}")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise P4Error(f"non-directory path component: {relative}")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > limit):
            raise P4Error(f"input is not a bounded regular file: {relative}")
    with current.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise P4Error(f"input grew beyond bound: {relative}")
    return data


def load_exact(relative: str, expected: str, name: str):
    raw = read_project(relative, 4_000_000)
    if digest(raw) != expected:
        raise P4Error(f"pinned local source changed: {relative}")
    path = ROOT / relative
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None:
        raise P4Error(f"cannot load pinned source: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    if digest(read_project(relative, 4_000_000)) != expected:
        raise P4Error(f"pinned source changed during execution: {relative}")
    return module


def strict_label(state: str, kind: str | None = None) -> int | None:
    if state == "unique_visible_tick" and kind in (None, "positive"):
        return 1
    if state == "no_ground_truth_tick_nearby" and kind in (None, "background_only"):
        return 0
    return None


def merge_hash_labels(rows: list[dict[str, Any]]) -> dict[str, dict[str, Any]]:
    """Reject semantic collisions, then deduplicate labels while merging source IDs."""
    labels: dict[str, set[int]] = defaultdict(set)
    groups: dict[str, set[str]] = defaultdict(set)
    for row in rows:
        image_hash, label = row.get("image_sha256"), row.get("label")
        if not isinstance(image_hash, str) or len(image_hash) != 64 or label not in (0, 1):
            raise P4Error("malformed row image hash or binary label")
        labels[image_hash].add(int(label))
        groups[image_hash].update(row.get("source_group_ids", []))
    conflicts = {image_hash: sorted(value) for image_hash, value in labels.items() if len(value) != 1}
    if conflicts:
        raise P4Error(f"semantic label conflict for image hashes: {sorted(conflicts)[:5]}")
    return {image_hash: {"label": next(iter(label)), "source_group_ids": sorted(groups[image_hash])}
            for image_hash, label in labels.items()}


def _decode_l(image_bytes: bytes, image_hash: str) -> np.ndarray:
    try:
        with Image.open(BytesIO(image_bytes)) as image:
            image.load()
            if image.mode != "L" or image.size != (256, 96):
                raise P4Error(f"unexpected classifier crop geometry: {image_hash}")
            return np.asarray(image, dtype=np.float32) / 255.0
    except OSError as error:
        raise P4Error(f"cannot decode training image {image_hash}") from error


def load_dataset(config: dict[str, Any]) -> dict[str, Any]:
    """Read only train-group annotation/image payloads and the pinned candidate rows."""
    raw = read_project(P3_MANIFEST_REL, MAX_MANIFEST)
    if digest(raw) != P3_MANIFEST_SHA:
        raise P4Error("P3 train curation manifest hash changed")
    manifest = json.loads(raw)
    if manifest.get("split") != "train" or len(manifest.get("records", [])) != 896:
        raise P4Error("P3 manifest identity or row count changed")
    groups_seen = {row.get("source_group_id") for row in manifest["records"]}
    if not TRAIN_GROUPS <= groups_seen or TRAIN_GROUPS & HOLDOUT_GROUPS:
        raise P4Error("P3 train/holdout group boundary changed")
    selected = [row for row in manifest["records"] if row.get("source_group_id") in TRAIN_GROUPS]
    other_hashes = {row.get("image_sha256") for row in manifest["records"]
                    if row.get("source_group_id") not in TRAIN_GROUPS}
    if {row.get("image_sha256") for row in selected} & other_hashes:
        raise P4Error("train image hash overlaps a non-train group in metadata")

    base_rows: list[dict[str, Any]] = []
    payload_by_hash: dict[str, bytes] = {}
    semantic: dict[str, set[int]] = defaultdict(set)
    groups_by_hash: dict[str, set[str]] = defaultdict(set)
    row_bindings = []
    for row in selected:
        group = row.get("source_group_id")
        if row.get("split") != "train" or row.get("source_id") != group or group not in TRAIN_GROUPS:
            raise P4Error("selected baseline row is not admitted by train group allowlist")
        ann_rel = f"{P3_CURATED_ROOT}/{row['annotation_path']}"
        ann_raw = read_project(ann_rel, MAX_ANNOTATION)
        if digest(ann_raw) != row.get("annotation_sha256"):
            raise P4Error(f"baseline annotation hash mismatch: {row.get('case_id')}")
        annotation = json.loads(ann_raw)
        for key in ("case_id", "source_id", "source_group_id", "split", "image_sha256"):
            if annotation.get(key) != row.get(key):
                raise P4Error(f"baseline annotation identity mismatch: {row.get('case_id')}:{key}")
        state = annotation.get("matching_state")
        if state not in ("unique_visible_tick", "no_ground_truth_tick_nearby", "ambiguous"):
            raise P4Error("baseline annotation has unknown matching state")
        image_hash = row.get("image_sha256")
        if not isinstance(image_hash, str) or len(image_hash) != 64:
            raise P4Error("baseline row has invalid image hash")
        groups_by_hash[image_hash].add(group)
        if state != "ambiguous":
            semantic[image_hash].add(int(state == "unique_visible_tick"))
        row_bindings.append({"case_id": row["case_id"], "image_sha256": image_hash,
                             "annotation_sha256": row["annotation_sha256"], "group": group,
                             "matching_state": state})
    conflict = {key: values for key, values in semantic.items() if len(values) > 1}
    if conflict:
        raise P4Error(f"P3 baseline has semantic hash collisions: {sorted(conflict)[:5]}")
    baseline_labels = {key: next(iter(values)) for key, values in semantic.items() if values}
    if len(baseline_labels) != 446 or Counter(baseline_labels.values()) != Counter({1: 81, 0: 365}):
        raise P4Error("P3 baseline unique image/class counts changed")
    selected_by_hash: dict[str, dict[str, Any]] = {}
    for row in selected:
        h = row["image_sha256"]
        if h in baseline_labels:
            selected_by_hash.setdefault(h, row)
    for h, label in baseline_labels.items():
        row = selected_by_hash.get(h)
        if row is None:
            raise P4Error("P3 train image hash has no image payload binding")
        image_rel = f"{P3_CURATED_ROOT}/{row['image_path']}"
        image_bytes = read_project(image_rel, MAX_IMAGE)
        if digest(image_bytes) != h:
            raise P4Error(f"baseline image hash mismatch: {h}")
        payload_by_hash[h] = image_bytes
        base_rows.append({"image_sha256": h, "label": label,
                          "source_group_ids": sorted(groups_by_hash[h])})

    cand_raw = read_project(f"{CANDIDATE_ROOT}/manifest.json", MAX_MANIFEST)
    if digest(cand_raw) != CANDIDATE_MANIFEST_SHA:
        raise P4Error("wide-candidate manifest hash changed")
    candidate_manifest = json.loads(cand_raw)
    if candidate_manifest.get("status") != "curated-untrained-exploratory-train-only":
        raise P4Error("wide-candidate manifest is not the completed train-only output")
    if (candidate_manifest.get("prior_holdout_payloads_opened") not in (False, 0)
            or candidate_manifest.get("probe_dev_sealed_private_inputs_read") is not False):
        raise P4Error("candidate source report has an input-boundary violation")
    if (candidate_manifest.get("input_manifests", {}).get(P3_MANIFEST_REL) != P3_MANIFEST_SHA
            or candidate_manifest.get("p3_config_sha256") != P3_CONFIG_SHA
            or candidate_manifest.get("executed_wrapper_source_sha256") !=
               "e5f10b513a88b72970bcdf4ddbb93e4cddadc0cfe36524ef05085e80aa533c8b"):
        raise P4Error("wide candidate provenance does not bind the reviewed P3 train inputs")
    curation_dependencies = candidate_manifest.get("executed_local_source_sha256")
    if not isinstance(curation_dependencies, dict) or not curation_dependencies:
        raise P4Error("candidate curation omitted executed dependency provenance")
    for relative, expected_sha in sorted(curation_dependencies.items()):
        limit = 100_000_000 if relative.endswith(".dll") else 4_000_000
        if digest(read_project(relative, limit)) != expected_sha:
            raise P4Error(f"wide curation executed dependency changed: {relative}")
    quarantined = candidate_manifest.get("quarantined_records", [])
    if (len(quarantined) != 268 or any(r.get("quarantine_reason") != "same_image_has_ambiguous_truth_occurrence"
                                       for r in quarantined)):
        raise P4Error("ambiguous candidate quarantine inventory changed")
    candidate_records = candidate_manifest.get("retained_records", [])
    if len(candidate_records) != 19:
        raise P4Error("expected exactly 19 retained wide proposals")
    candidate_rows = []
    added_counts: Counter[int] = Counter()
    for row in candidate_records:
        source_group = row.get("source_group_id")
        if source_group not in TRAIN_GROUPS:
            raise P4Error("wide candidate escaped the P3 train group allowlist")
        h, label = row.get("image_sha256"), row.get("label")
        if label not in (0, 1) or h in payload_by_hash:
            raise P4Error("candidate label invalid or image overlaps P3 baseline")
        ann_rel = f"{CANDIDATE_ROOT}/{row['annotation_path']}"
        ann_raw = read_project(ann_rel, MAX_ANNOTATION)
        if digest(ann_raw) != row.get("annotation_sha256"):
            raise P4Error("wide candidate annotation hash mismatch")
        annotation = json.loads(ann_raw)
        expected_state = "unique_visible_tick" if label == 1 else "no_ground_truth_tick_nearby"
        expected_kind = "positive" if label == 1 else "background_only"
        if (annotation.get("case_id") != row.get("case_id")
                or annotation.get("source_group_id") != source_group
                or annotation.get("split") != "train"
                or annotation.get("image_sha256") != h
                or annotation.get("label_for_addition") != label
                or annotation.get("matching_state") != expected_state
                or annotation.get("kind") != expected_kind
                or annotation.get("trainable_single_box") is not True):
            raise P4Error("wide candidate annotation is not a strict trainable single-label row")
        image_bytes = read_project(f"{CANDIDATE_ROOT}/{row['image_path']}", MAX_IMAGE)
        if digest(image_bytes) != h:
            raise P4Error("wide candidate image hash mismatch")
        payload_by_hash[h] = image_bytes
        candidate_rows.append({"image_sha256": h, "label": label,
                               "source_group_ids": [source_group]})
        added_counts[label] += 1
        row_bindings.append({"case_id": row["case_id"], "image_sha256": h,
                             "annotation_sha256": row["annotation_sha256"], "group": source_group,
                             "matching_state": expected_state, "addition": True})
    if added_counts != Counter({1: 8, 0: 11}):
        raise P4Error(f"wide candidate class counts changed: {dict(added_counts)}")
    merged = merge_hash_labels(base_rows + candidate_rows)
    if len(merged) != 465 or Counter(row["label"] for row in merged.values()) != Counter({1: 89, 0: 376}):
        raise P4Error("combined train image/class inventory changed")

    p3_report_raw = read_project(P3_REPORT_REL, 300_000)
    if digest(p3_report_raw) != P3_REPORT_SHA:
        raise P4Error("frozen P3 report changed")
    p3_report = json.loads(p3_report_raw)
    if (p3_report.get("data", {}).get("holdout_used_for_training_or_metrics") is not False
            or p3_report.get("data", {}).get("manifest_sha256") != P3_MANIFEST_SHA):
        raise P4Error("P3 baseline report identity/boundary mismatch")
    rows = []
    for h in sorted(merged):
        info = merged[h]
        rows.append({"image_sha256": h, "label": info["label"],
                     "source_group_ids": info["source_group_ids"],
                     "pixels": _decode_l(payload_by_hash[h], h)})
    bindings_raw = json.dumps(sorted(row_bindings, key=lambda r: (r["case_id"], r["image_sha256"])),
                              sort_keys=True, separators=(",", ":")).encode()
    class_counts = Counter(row["label"] for row in rows)
    return {"rows": rows, "row_binding_count": len(row_bindings),
            "row_bindings_sha256": digest(bindings_raw), "class_counts": dict(class_counts),
            "baseline_unique_images": len(baseline_labels), "candidate_unique_images": len(candidate_rows),
            "candidate_quarantined_rows": len(quarantined),
            "candidate_dependency_map_sha256": digest(canonical_json(curation_dependencies)),
            "candidate_dependency_count": len(curation_dependencies),
            "candidate_dependency_sha256": dict(sorted(curation_dependencies.items())),
            "groups": sorted(TRAIN_GROUPS),
            "p3_report": p3_report, "input_manifest_hashes": {
                P3_MANIFEST_REL: P3_MANIFEST_SHA,
                f"{CANDIDATE_ROOT}/manifest.json": CANDIDATE_MANIFEST_SHA}}


def prepare_stack():
    p3_raw = read_project(P3_RUNNER_REL, 4_000_000)
    p3_sha = digest(p3_raw)
    p3 = load_exact(P3_RUNNER_REL, p3_sha, "_pinned_p3_runner_for_p4")
    if p3_sha != "b7c0b381f7d58be23ac8d1b139a18a8467fed9e34c320421765063dbe76c118d":
        raise P4Error("P3 runner source bytes differ from the reviewed source")
    p3._RUNNER_SHA = p3_sha
    p2, stack_hashes = p3.load_pinned_stack()
    p2_config, _ = p2.validate_config()
    p3_config, p3_config_sha = p3.load_and_validate_config(p2_config, stack_hashes)
    if p3_config_sha != P3_CONFIG_SHA:
        raise P4Error("P3 config changed")
    return p3, p2, p3_config, stack_hashes


def validate_config(p2, stack_hashes: dict[str, str]) -> tuple[dict[str, Any], str, bytes, dict[str, Any]]:
    raw = read_project(CONFIG_REL, 65_536)
    cfg = json.loads(raw)
    actual_runner = digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000))
    deps = cfg["dependencies"]
    expected_sources = {
        "p4_runner_sha256": actual_runner,
        "p3_runner_sha256": "b7c0b381f7d58be23ac8d1b139a18a8467fed9e34c320421765063dbe76c118d",
        "p2_runner_sha256": "dc86c5ee004794519c6c1ac7fa21699b06a31637914de3ab827353c828e3aa5d",
        "p2_config_sha256": "b230e743cadb3d057aef443b3da1dc5821e2da2ce88ecc20ae3175725069aa42",
        "p1_trainer_sha256": "3e8617809584dadb9c6b35cb52b95136fe766212a2f0bb65f4863b0d17ebc594",
        "train_admission_sha256": "72947560a68324ca2a6b470898755334e2f6ef98dd7491552e3618a615b30ede",
        "model_helper_sha256": "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5",
        "p3_config_sha256": P3_CONFIG_SHA, "p3_checkpoint_sha256": P3_CHECKPOINT_SHA,
        "p3_report_sha256": P3_REPORT_SHA, "train_curation_manifest_sha256": P3_MANIFEST_SHA,
        "wide_candidate_manifest_sha256": CANDIDATE_MANIFEST_SHA,
        "wide_curation_runner_sha256": "e5f10b513a88b72970bcdf4ddbb93e4cddadc0cfe36524ef05085e80aa533c8b",
        "wide_curation_config_sha256": "919915504214530c378b2eb682f6d0fce113087aaf18e422f2246e596719661a",
    }
    for key, value in expected_sources.items():
        if deps.get(key) != value:
            raise P4Error(f"P4 source/config provenance mismatch: {key}")
    inputs = cfg.get("training_inputs", {})
    exact_recipe = {
        "schema": "graphreader.stroke-seeded-ink-stroke-classifier-config.p4-finetune.v1",
        "recipe_id": "ink-stroke-validity-p4-sourceuniform-wideproposal-finetune",
        "epochs": 20, "batch_size": 32, "steps_per_epoch": 23, "optimizer_steps": 460,
        "learning_rate": 0.001, "weight_decay": 0.01, "optimizer": "AdamW",
        "loss": "binary_cross_entropy_with_logits", "class_draws_per_batch": 16,
        "per_class_draws_per_epoch": 368, "model_seed": 27104152, "sampler_seed": 27104153,
        "threshold_for_train_only_metrics": 0.5, "capturable": True,
        "torch_rng_seed": 27104152,
    }
    for key, value in exact_recipe.items():
        if cfg.get(key) != value:
            raise P4Error(f"P4 fixed recipe field changed: {key}")
    if (cfg.get("output_relative") != BASE_OUTPUT_REL
            or cfg.get("preflight_relative") != PREFLIGHT_REL):
        raise P4Error("P4 output identity changed")
    if (cfg.get("threshold_tuning") is not False
            or cfg.get("cpu_training_allowed") is not False
            or cfg.get("cpu_fallback_allowed") is not False
            or cfg.get("production_approval") is not False
            or cfg.get("probe_dev_sealed_private_inputs_allowed") is not False
            or cfg.get("training_requires_explicit_flag") != "--train"
            or cfg.get("torch_rng_seed") != cfg.get("model_seed")
            or cfg.get("torch_rng_seed_policy") !=
               "torch.manual_seed and torch.cuda.manual_seed_all before model construction; Python sampler uses the separate configured seeds"):
        raise P4Error("P4 safety boundary or explicit training gate changed")
    if (inputs.get("expected_base_unique_images") != 446
            or inputs.get("expected_base_class_counts") != {"positive": 81, "negative": 365}
            or inputs.get("expected_added_unique_images") != 19
            or inputs.get("expected_added_class_counts") != {"positive": 8, "negative": 11}
            or inputs.get("expected_combined_unique_images") != 465
            or inputs.get("expected_combined_class_counts") != {"positive": 89, "negative": 376}
            or inputs.get("expected_owner_group_counts") != {"positive": 12, "negative": 19}
            or set(inputs.get("train_source_groups", [])) != TRAIN_GROUPS
            or set(inputs.get("excluded_prior_holdout_groups", [])) != HOLDOUT_GROUPS):
        raise P4Error("P4 data/split inventory differs from the frozen recipe")
    if (stack_hashes.get("p2_runner_sha256") != expected_sources["p2_runner_sha256"]
            or stack_hashes.get("p1_trainer_source_sha256") != expected_sources["p1_trainer_sha256"]
            or stack_hashes.get("train_admission_source_sha256") != expected_sources["train_admission_sha256"]
            or stack_hashes.get("helper_source_sha256") != expected_sources["model_helper_sha256"]):
        raise P4Error("executed pinned local stack differs from P4 config")
    audit = read_project(OVERLAP_AUDIT_REL, 65_536)
    if digest(audit) != OVERLAP_AUDIT_SHA:
        raise P4Error("metadata overlap audit changed")
    audit_obj = json.loads(audit)
    if audit_obj.get("candidate_metadata", {}).get("images_and_annotations_rehashed") is not True:
        raise P4Error("overlap audit does not confirm recomputed train payload hashes")
    if cfg.get("training_inputs", {}).get("metadata_overlap_audit_sha256") != OVERLAP_AUDIT_SHA:
        raise P4Error("P4 config does not bind the candidate overlap audit")
    holdout_audit = audit_obj.get("holdout_metadata", {})
    probe_audit = audit_obj.get("consumed_probe_metadata", {})
    if (holdout_audit.get("source_group_count") != 4 or holdout_audit.get("crop_hash_count") != 89
            or holdout_audit.get("retained_candidate_hash_overlap") != 0
            or probe_audit.get("manifest_sha256") != "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
            or probe_audit.get("record_count") != 359 or probe_audit.get("duplicate_train_image_exclusions") != 63
            or probe_audit.get("retained_candidate_hash_overlap") != 0
            or probe_audit.get("train_source_id_overlap") != 0
            or probe_audit.get("train_source_pixel_hash_overlap") != 0
            or audit_obj.get("holdout_or_probe_payload_bytes_opened") is not False
            or audit_obj.get("private_or_sealed_inputs_read") is not False):
        raise P4Error("pinned holdout/probe metadata overlap audit is not clean")
    overlap_summary = {"audit_sha256": OVERLAP_AUDIT_SHA,
        "holdout_source_groups": holdout_audit["source_group_count"],
        "holdout_crop_hashes": holdout_audit["crop_hash_count"],
        "candidate_holdout_hash_overlap": holdout_audit["retained_candidate_hash_overlap"],
        "probe_manifest_sha256": probe_audit["manifest_sha256"],
        "probe_manifest_records": probe_audit["record_count"],
        "probe_duplicate_train_exclusions": probe_audit["duplicate_train_image_exclusions"],
        "candidate_probe_hash_overlap": probe_audit["retained_candidate_hash_overlap"],
        "train_probe_source_id_overlap": probe_audit["train_source_id_overlap"],
        "train_probe_panel_pixel_hash_overlap": probe_audit["train_source_pixel_hash_overlap"],
        "holdout_or_probe_payload_bytes_opened": False, "private_or_sealed_inputs_read": False}
    if cfg.get("optimizer_steps") != cfg.get("epochs") * cfg.get("steps_per_epoch"):
        raise P4Error("P4 fixed optimizer schedule is inconsistent")
    if cfg.get("threshold_tuning") is not False or cfg.get("cpu_fallback_allowed") is not False:
        raise P4Error("P4 diagnostic policy changed")
    return cfg, digest(raw), raw, overlap_summary


def sampler_audit(p2, rows: list[dict[str, Any]], cfg: dict[str, Any]) -> dict[str, Any]:
    train_groups = set(cfg["training_inputs"]["train_source_groups"])
    per_class = {}
    for label, name in ((1, "positive"), (0, "negative")):
        local_rows = [{"source_group_ids": row["source_group_ids"]} for row in rows if row["label"] == label]
        grouped, owners = p2._owner_group_indices(local_rows, train_groups)
        if len(grouped) != cfg["training_inputs"]["expected_owner_group_counts"][name]:
            raise P4Error(f"{name} owner-group count changed")
        state = p2.make_group_state(grouped, int(cfg["sampler_seed"]) + (0 if label else 1))
        seen: set[int] = set()
        draws_per_group: Counter[str] = Counter()
        for epoch in range(int(cfg["epochs"])):
            order, group_draws = p2.source_uniform_order(grouped, int(cfg["per_class_draws_per_epoch"]), epoch, state)
            if max(group_draws.values()) - min(group_draws.values()) > 1:
                raise P4Error("source-uniform per-epoch draw balance violated")
            seen.update(order)
            draws_per_group.update(group_draws)
        if seen != set(range(len(local_rows))):
            raise P4Error(f"{name} sampler did not cover every training row")
        per_class[name] = {"unique_rows": len(local_rows), "groups": len(grouped),
                           "draws": sum(draws_per_group.values()), "unique_covered": len(seen),
                           "draws_by_owner_group": dict(sorted(draws_per_group.items())),
                           "unique_rows_by_owner_group": {g: len(v) for g, v in sorted(grouped.items())},
                           "owner_assignment_sha256": digest(json.dumps(owners, sort_keys=True).encode())}
    return per_class


def require_capturable_adamw(optimizer_class: Any) -> None:
    try:
        parameters = inspect.signature(optimizer_class).parameters
    except (TypeError, ValueError) as error:
        raise P4Error("cannot verify AdamW capturable support") from error
    if "capturable" not in parameters:
        raise P4Error("installed AdamW does not expose capturable=True")


def audit_capturable_adamw_runtime(torch: Any) -> dict[str, Any]:
    """Statically confirm this installed PyTorch AdamW routes capturable to its kernels."""
    require_capturable_adamw(torch.optim.AdamW)
    try:
        step_source = inspect.getsource(torch.optim.AdamW.step)
        import torch.optim.adam as adam_impl
        single_source = inspect.getsource(adam_impl._single_tensor_adam)
        multi_source = inspect.getsource(adam_impl._multi_tensor_adam)
        step_path = Path(inspect.getsourcefile(torch.optim.AdamW.step)).resolve(strict=True)
        adam_path = Path(inspect.getsourcefile(adam_impl._single_tensor_adam)).resolve(strict=True)
    except (AttributeError, OSError, TypeError) as error:
        raise P4Error("cannot audit installed AdamW capturable implementation") from error
    try:
        step_rel = step_path.relative_to(ROOT).as_posix()
        adam_rel = adam_path.relative_to(ROOT).as_posix()
    except ValueError as error:
        raise P4Error("AdamW implementation source is outside the project runtime") from error
    step_bytes, adam_bytes = read_project(step_rel, 2_000_000), read_project(adam_rel, 2_000_000)
    routed = 'capturable=group["capturable"]' in step_source
    single_support = "capturable" in single_source and "if capturable" in single_source
    multi_support = "capturable" in multi_source and "if capturable" in multi_source
    if not (routed and single_support and multi_support):
        raise P4Error("installed AdamW implementation does not statically route capturable through both paths")
    return {"signature_support": True, "step_routes_group_capturable": routed,
            "single_tensor_path_has_capturable_branch": single_support,
            "foreach_path_has_capturable_branch": multi_support,
            "step_source_path": step_rel, "step_source_sha256": digest(step_bytes),
            "adam_kernel_source_path": adam_rel, "adam_kernel_source_sha256": digest(adam_bytes),
            "optimizer_step_executed": False}


def optimizer_state_tensors(value: Any, tensor_type: type) -> list[Any]:
    if isinstance(value, tensor_type):
        return [value]
    if isinstance(value, dict):
        return [tensor for nested in value.values() for tensor in optimizer_state_tensors(nested, tensor_type)]
    if isinstance(value, (list, tuple)):
        return [tensor for nested in value for tensor in optimizer_state_tensors(nested, tensor_type)]
    return []


def assert_optimizer_gpu_residency(torch: Any, model: Any, optimizer: Any, device: Any) -> dict[str, int]:
    """Reject host-side parameters, gradients, or optimizer state at every training step."""
    parameters = list(model.parameters())
    if any(parameter.device != device for parameter in parameters):
        raise P4Error("model parameter left the required GPU")
    gradients = [parameter.grad for parameter in parameters if parameter.grad is not None]
    if any(gradient.device != device for gradient in gradients):
        raise P4Error("model gradient left the required GPU")
    groups = optimizer.param_groups
    if not groups or any(group.get("capturable") is not True for group in groups):
        raise P4Error("AdamW parameter group is not capturable")
    optimizer_parameters = [parameter for group in groups for parameter in group["params"]]
    if any(parameter.device != device for parameter in optimizer_parameters):
        raise P4Error("optimizer parameter left the required GPU")
    state_tensors = [tensor for state in optimizer.state.values()
                     for tensor in optimizer_state_tensors(state, torch.Tensor)]
    if any(tensor.device != device for tensor in state_tensors):
        raise P4Error("AdamW state tensor left the required GPU")
    return {"model_parameters": len(parameters), "gradients": len(gradients),
            "optimizer_parameters": len(optimizer_parameters), "optimizer_state_tensors": len(state_tensors)}


def seed_torch_rng(torch: Any, seed: int) -> None:
    torch.manual_seed(seed)
    torch.cuda.manual_seed_all(seed)


def gpu_preflight(p2, rows: list[dict[str, Any]], cfg: dict[str, Any]) -> dict[str, Any]:
    import torch
    runtime = p2.helpers.require_rx_6600_xt(torch)
    if Path(sys.executable).resolve() != p2.helpers.GPU_PYTHON.resolve():
        raise P4Error("must be invoked via the project GPU launcher interpreter")
    capturable_audit = audit_capturable_adamw_runtime(torch)
    seed_torch_rng(torch, int(cfg["torch_rng_seed"]))
    device = torch.device(str(runtime["device"]))
    model = p2.p1._build_classifier(torch).to(device)
    checkpoint_raw = read_project(P3_CHECKPOINT_REL, 100_000_000)
    if digest(checkpoint_raw) != P3_CHECKPOINT_SHA:
        raise P4Error("frozen P3 checkpoint hash changed")
    checkpoint = torch.load(BytesIO(checkpoint_raw), map_location=device, weights_only=False)
    if (checkpoint.get("model_identity") != cfg["initialization"]["model_identity"]
            or checkpoint.get("recipe_id") != "ink-stroke-validity-source-uniform-p3-60ep"
            or checkpoint.get("p3_config_sha256") != P3_CONFIG_SHA
            or checkpoint.get("p3_runner_sha256") != "b7c0b381f7d58be23ac8d1b139a18a8467fed9e34c320421765063dbe76c118d"):
        raise P4Error("P3 checkpoint metadata identity mismatch")
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    x = torch.from_numpy(np.stack([row["pixels"] for row in rows[:2]]).astype(np.float32)).unsqueeze(1).to(device)
    p2.p1._assert_gpu_binding(runtime, model, [x], device)
    with torch.inference_mode():
        fmap = tuple(model.features(x).shape[-2:])
        logits = model(x)
    if fmap != (12, 32) or tuple(logits.shape) != (2,) or not torch.isfinite(logits).all():
        raise P4Error("P3 architecture forward check failed")
    torch.cuda.synchronize(device)
    return {"runtime": p2._versions(runtime), "device": str(device),
            "checkpoint_sha256": digest(checkpoint_raw), "checkpoint_model_identity": checkpoint["model_identity"],
            "feature_map_shape": list(fmap), "logits_shape": list(logits.shape),
            "forward_finite": True, "model_parameter_device": str(next(model.parameters()).device),
            "input_tensor_device": str(x.device), "optimizer_created": False,
            "adamw_capturable_supported": True, "adamw_capturable_requested": cfg["capturable"],
            "adamw_capturable_runtime_audit": capturable_audit,
            "torch_cpu_rng_seed": cfg["torch_rng_seed"], "torch_cuda_rng_seed": cfg["torch_rng_seed"],
            "training_or_backward_performed": False}


def canonical_json(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def build_preflight_report(cfg: dict[str, Any], config_sha: str, data: dict[str, Any],
                           sampler: dict[str, Any], gpu: dict[str, Any], runner_sha: str,
                           stack_hashes: dict[str, str], overlap_summary: dict[str, Any]) -> dict[str, Any]:
    return {"schema": "graphreader.stroke-seeded-ink-stroke-classifier-p4-preflight.v1",
            "status": "preflight-passed-no-optimizer", "recipe_id": cfg["recipe_id"],
            "acceptance_status": "train-only exploratory; not acceptance", "production_approval": False,
            "release_eligible": False, "p4_config_sha256": config_sha, "p4_runner_sha256": runner_sha,
            "inputs": {**data["input_manifest_hashes"], "p3_checkpoint_sha256": P3_CHECKPOINT_SHA,
                       "p3_report_sha256": P3_REPORT_SHA, "row_bindings_sha256": data["row_bindings_sha256"]},
            "overlap_and_exclusion_audit": overlap_summary,
            "executed_dependency_sha256": {"p4_runner": runner_sha,
                "p4_config": config_sha,
                "p3_runner": "b7c0b381f7d58be23ac8d1b139a18a8467fed9e34c320421765063dbe76c118d",
                **stack_hashes,
                "wide_candidate_curation_dependencies": data["candidate_dependency_sha256"]},
            "data": {"baseline_unique_images": data["baseline_unique_images"],
                     "added_unique_images": data["candidate_unique_images"], "unique_total": len(data["rows"]),
                     "class_counts": {"positive": data["class_counts"].get(1, 0),
                                      "negative": data["class_counts"].get(0, 0)},
                     "candidate_quarantined_rows": data["candidate_quarantined_rows"],
                     "train_source_groups": data["groups"], "holdout_source_groups_opened": [],
                     "probe_dev_sealed_private_payloads_read": False},
            "recipe": ({key: cfg[key] for key in ("architecture", "model_seed", "sampler_seed", "epochs",
                       "batch_size", "steps_per_epoch", "optimizer_steps", "learning_rate", "weight_decay",
                       "optimizer", "loss", "class_draws_per_batch", "per_class_draws_per_epoch", "sampler",
                       "threshold_for_train_only_metrics", "threshold_tuning", "capturable", "torch_rng_seed")}
                    | {"torch_rng_seed_policy": cfg["torch_rng_seed_policy"]}),
            "sampler_audit": sampler, "gpu_preflight": gpu,
            "provenance_limitation": "P2 used exact helper source bytes but normally imported P1 trainer/admission after hash checks; P3's pinned source loader bounds the dependency bytes used for this P4 preflight. This does not retroactively strengthen earlier P2 provenance.",
            "training_performed": False}


def train(p2, cfg: dict[str, Any], config_sha: str, data: dict[str, Any], output: Path) -> dict[str, Any]:
    """The optimizer path is opt-in and cannot be reached by the default preflight command."""
    import torch
    runtime = p2.helpers.require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    audit_capturable_adamw_runtime(torch)
    seed_torch_rng(torch, int(cfg["torch_rng_seed"]))
    model = p2.p1._build_classifier(torch).to(device)
    checkpoint_raw = read_project(P3_CHECKPOINT_REL, 100_000_000)
    checkpoint = torch.load(BytesIO(checkpoint_raw), map_location=device, weights_only=False)
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    p2.p1._assert_gpu_binding(runtime, model, [], device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=float(cfg["learning_rate"]),
                                  weight_decay=float(cfg["weight_decay"]), capturable=bool(cfg["capturable"]))
    residency_after_init = assert_optimizer_gpu_residency(torch, model, optimizer, device)
    rows = data["rows"]
    pixels = torch.from_numpy(np.stack([row["pixels"] for row in rows]).astype(np.float32)).unsqueeze(1)
    labels = [int(row["label"]) for row in rows]
    train_groups = set(cfg["training_inputs"]["train_source_groups"])
    pos_rows = [row for row in rows if row["label"] == 1]
    neg_rows = [row for row in rows if row["label"] == 0]
    pos_groups, pos_owner = p2._owner_group_indices([{"source_group_ids": r["source_group_ids"]} for r in pos_rows], train_groups)
    neg_groups, neg_owner = p2._owner_group_indices([{"source_group_ids": r["source_group_ids"]} for r in neg_rows], train_groups)
    pos_state = p2.make_group_state(pos_groups, int(cfg["sampler_seed"]))
    neg_state = p2.make_group_state(neg_groups, int(cfg["sampler_seed"]) + 1)
    pos_global = {i: next(j for j, row in enumerate(rows) if row["label"] == 1 and row is pos_rows[i]) for i in range(len(pos_rows))}
    neg_global = {i: next(j for j, row in enumerate(rows) if row["label"] == 0 and row is neg_rows[i]) for i in range(len(neg_rows))}
    optimizer_steps = 0
    losses = []
    draw_summary = {"positive": Counter(), "negative": Counter()}
    for epoch in range(int(cfg["epochs"])):
        po, pd = p2.source_uniform_order(pos_groups, int(cfg["per_class_draws_per_epoch"]), epoch, pos_state)
        no, nd = p2.source_uniform_order(neg_groups, int(cfg["per_class_draws_per_epoch"]), epoch, neg_state)
        if max(pd.values()) - min(pd.values()) > 1 or max(nd.values()) - min(nd.values()) > 1:
            raise P4Error("source-uniform draws are not balanced within epoch")
        draw_summary["positive"].update(pd)
        draw_summary["negative"].update(nd)
        for batch in range(int(cfg["steps_per_epoch"])):
            half = int(cfg["class_draws_per_batch"])
            start = batch * half
            chosen = [pos_global[i] for i in po[start:start + half]] + [neg_global[i] for i in no[start:start + half]]
            random.Random(int(cfg["sampler_seed"]) + epoch * 10007 + batch).shuffle(chosen)
            x = pixels[chosen].to(device)
            y = torch.tensor([labels[i] for i in chosen], dtype=torch.float32, device=device)
            p2.p1._assert_gpu_binding(runtime, model, [x, y], device)
            optimizer.zero_grad(set_to_none=True)
            loss = torch.nn.functional.binary_cross_entropy_with_logits(model(x), y)
            if not torch.isfinite(loss):
                raise P4Error("non-finite P4 loss")
            loss.backward()
            optimizer.step()
            residency_after_step = assert_optimizer_gpu_residency(torch, model, optimizer, device)
            optimizer_steps += 1
            losses.append(float(loss.detach().cpu()))
    if optimizer_steps != int(cfg["optimizer_steps"]):
        raise P4Error("executed optimizer step count differs from fixed P4 schedule")
    metrics, groups, scores = p2._evaluate_train(model, rows, device, int(cfg["batch_size"]))
    checkpoint_path = output / "checkpoint.pt"
    torch.save({"state_dict": model.state_dict(), "model_identity": "stroke-seeded-ink-validity-rf31-p4-wideproposal-finetune",
                "recipe_id": cfg["recipe_id"], "architecture": cfg["architecture"], "initialization_model_identity": checkpoint["model_identity"],
                "p3_checkpoint_sha256": P3_CHECKPOINT_SHA, "p4_config_sha256": config_sha,
                "train_manifest_sha256s": data["input_manifest_hashes"], "train_row_bindings_sha256": data["row_bindings_sha256"],
                "source_groups": sorted(train_groups), "runtime": runtime}, checkpoint_path)
    return {"schema": "graphreader.stroke-seeded-ink-stroke-classifier-report.v4",
            "status": "completed-train-only-p4-finetune", "recipe_id": cfg["recipe_id"],
            "acceptance_status": "train-only exploratory; not acceptance", "production_approval": False,
            "inputs": {**data["input_manifest_hashes"], "p3_checkpoint_sha256": P3_CHECKPOINT_SHA,
                       "row_bindings_sha256": data["row_bindings_sha256"]},
            "data": {"unique_images": len(rows), "class_counts": {"positive": sum(labels), "negative": len(labels)-sum(labels)},
                     "baseline_unique_images": data["baseline_unique_images"], "added_unique_images": data["candidate_unique_images"],
                     "train_source_groups": sorted(train_groups), "holdout_used_for_training_or_metrics": False,
                     "probe_dev_sealed_private_payloads_read": False},
            "recipe": {"epochs": cfg["epochs"], "steps_per_epoch": cfg["steps_per_epoch"],
                       "optimizer_steps": optimizer_steps, "batch_size": cfg["batch_size"],
                       "class_draws_per_batch": cfg["class_draws_per_batch"],
                       "per_class_draws_per_epoch": cfg["per_class_draws_per_epoch"],
                       "optimizer": cfg["optimizer"], "learning_rate": cfg["learning_rate"],
                       "weight_decay": cfg["weight_decay"], "loss": cfg["loss"],
                       "capturable": cfg["capturable"], "torch_rng_seed": cfg["torch_rng_seed"],
                       "optimizer_gpu_residency_after_init": residency_after_init,
                       "optimizer_gpu_residency_after_last_step": residency_after_step,
                       "sampler": cfg["sampler"], "threshold_for_train_only_metrics": 0.5},
            "training": {"loss_first": losses[0], "loss_last": losses[-1], "cpu_training_allowed": False,
                         "source_draws": {k: dict(sorted(v.items())) for k, v in draw_summary.items()}},
            "train_only_metrics": {"threshold": 0.5, "overall": metrics, "by_source_group": groups,
                                   "score_count": len(scores)},
            "checkpoint": {"path": f"{BASE_OUTPUT_REL}/checkpoint.pt", "sha256": digest(checkpoint_path.read_bytes()),
                           "model_identity": "stroke-seeded-ink-validity-rf31-p4-wideproposal-finetune",
                           "initialized_from_p3_sha256": P3_CHECKPOINT_SHA},
            "limitations": ["Only source-group train data was scored; no generalization or acceptance claim.",
                            "Added crops are 19 unique examples from 19 existing procedural train source groups.",
                            "Fresh AdamW state is used because P3 checkpoint has model weights only."]}


def main() -> None:
    runner_sha = digest(read_project(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000))
    p3, p2, _p3_config, source_hashes = prepare_stack()
    cfg, config_sha, _, overlap_summary = validate_config(p2, source_hashes)
    if cfg["dependencies"]["p4_runner_sha256"] != runner_sha:
        raise P4Error("P4 runner SHA differs from config binding")
    data = load_dataset(cfg)
    if len(data["rows"]) != 465 or data["class_counts"] != {0: 376, 1: 89}:
        raise P4Error("P4 admitted image set differs from predeclared inventory")
    sampler = sampler_audit(p2, data["rows"], cfg)
    gpu = gpu_preflight(p2, data["rows"], cfg)
    report = build_preflight_report(cfg, config_sha, data, sampler, gpu, runner_sha, source_hashes, overlap_summary)
    if "--train" not in sys.argv:
        out = ROOT / PREFLIGHT_REL
        if out.exists() or out.is_symlink():
            raise P4Error("P4 preflight receipt already exists; refusing overwrite")
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(json.dumps(report, sort_keys=True, indent=2).encode() + b"\n")
        print(json.dumps({"status": report["status"], "path": PREFLIGHT_REL,
                          "p4_config_sha256": config_sha, "p4_runner_sha256": runner_sha,
                          "gpu": gpu, "data": report["data"]}, sort_keys=True))
        return
    outdir = ROOT / BASE_OUTPUT_REL
    if outdir.exists() or outdir.is_symlink():
        raise P4Error("P4 train output already exists; refusing overwrite")
    p2.helpers._checked_chain(ROOT, outdir.parent, "P4 output parent")
    outdir.mkdir(parents=False, exist_ok=False)
    started = {"status": "started", "recipe_id": cfg["recipe_id"], "p4_config_sha256": config_sha,
               "p4_runner_sha256": runner_sha, "pinned_dependencies": source_hashes,
               "started_utc": datetime.now(timezone.utc).isoformat()}
    (outdir / "started.json").write_text(json.dumps(started, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    try:
        result = train(p2, cfg, config_sha, data, outdir)
        result["p4_runner_sha256"] = runner_sha
        result["p4_config_sha256"] = config_sha
        result["executed_local_dependencies"] = source_hashes
        result["training"]["versions"] = p2._versions(gpu["runtime"])
        (outdir / "report.json").write_text(json.dumps(result, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    except BaseException as error:
        failure = {"status": "failed", "error_type": type(error).__name__, "error": str(error),
                   "p4_runner_sha256": runner_sha, "p4_config_sha256": config_sha,
                   "failed_utc": datetime.now(timezone.utc).isoformat()}
        (outdir / "failure.json").write_text(json.dumps(failure, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
