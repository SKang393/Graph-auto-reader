# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train a binary visible-X-tick validity classifier from the pinned train curation."""

from __future__ import annotations

from collections import Counter, defaultdict
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
from itertools import combinations
import json
import math
from pathlib import Path
import random
import sys
import time
from typing import Any, Mapping

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers

HERE = Path(__file__).resolve().parent
CONFIG_PATH = HERE / "p1.json"
CURATION_RELATIVE = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2"
MANIFEST_RELATIVE = CURATION_RELATIVE + "/manifest.json"
MANIFEST_SHA256 = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
CURATOR_RELATIVE = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/train_proposal_curation_v1/curate_train_proposals.py"
OUTPUT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-v1-rx6600xt"
_RUN = {"phase": "starting", "steps": 0}


class StrokeClassifierError(RuntimeError):
    pass


def digest(value: bytes) -> str:
    return sha256(value).hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _bound_json(root: Path, relative: str, label: str, limit: int) -> tuple[dict[str, Any], bytes, str]:
    bound = helpers._bound_read(root, relative, label, limit)
    return helpers._parse_json(bound, label), bound.value, bound.sha256


def _union_components(records: list[dict[str, Any]]) -> tuple[dict[str, str], dict[str, list[str]]]:
    parents: dict[str, str] = {}

    def find(item: str) -> str:
        parents.setdefault(item, item)
        while parents[item] != item:
            parents[item] = parents[parents[item]]
            item = parents[item]
        return item

    def union(left: str, right: str) -> None:
        a, b = find(left), find(right)
        if a != b:
            low, high = sorted((a, b))
            parents[high] = low

    groups_by_hash: dict[str, set[str]] = defaultdict(set)
    for row in records:
        group = row["source_group_id"]
        find(group)
        groups_by_hash[row["image_sha256"]].add(group)
    for groups in groups_by_hash.values():
        ordered = sorted(groups)
        for other in ordered[1:]:
            union(ordered[0], other)
    components: dict[str, list[str]] = defaultdict(list)
    for group in sorted(parents):
        components[find(group)].append(group)
    return {group: find(group) for group in parents}, {key: value for key, value in sorted(components.items())}


def _deduplicate_label_map(labels_by_hash: Mapping[str, set[int]]) -> dict[str, int]:
    conflicts = {image_hash: sorted(labels) for image_hash, labels in labels_by_hash.items()
                  if len(labels) != 1}
    if conflicts:
        raise StrokeClassifierError(f"same-image binary label conflict; refusing admission: {list(conflicts)[:5]}")
    return {image_hash: next(iter(labels)) for image_hash, labels in labels_by_hash.items()}


def _choose_holdout(dataset: Mapping[str, Any], config: Mapping[str, Any]) -> tuple[str, ...]:
    images = dataset["unique_images"]
    component_by_group = dataset["component_by_group"]
    positive_by_component: Counter[str] = Counter()
    negative_by_component: Counter[str] = Counter()
    for row in images:
        components = {component_by_group[group] for group in row["source_group_ids"]}
        if len(components) != 1:
            raise StrokeClassifierError("same image hash spans disconnected components")
        component = next(iter(components))
        (positive_by_component if row["label"] else negative_by_component)[component] += 1
    component_ids = tuple(sorted(dataset["components"]))
    count = int(config["holdout_component_count"])
    target_positive = int(config["holdout_target_positive_unique_images"])
    tolerance = int(config["holdout_positive_tolerance"])
    min_negative = int(config["holdout_min_negative_unique_images"])
    candidates = []
    for selected in combinations(component_ids, count):
        positive = sum(positive_by_component[c] for c in selected)
        negative = sum(negative_by_component[c] for c in selected)
        train_positive = sum(positive_by_component.values()) - positive
        train_negative = sum(negative_by_component.values()) - negative
        if (abs(positive - target_positive) <= tolerance and negative >= min_negative
                and train_positive > 0 and train_negative > 0):
            tie = digest((str(config["split_seed"]) + "|" + "|".join(selected)).encode("utf-8"))
            candidates.append(((abs(positive - target_positive), tie), selected))
    if not candidates:
        raise StrokeClassifierError("no four-component internal holdout satisfies the frozen class-coverage rule")
    selected = min(candidates, key=lambda item: item[0])[1]
    frozen = config.get("holdout_component_ids")
    if frozen is not None and tuple(frozen) != selected:
        raise StrokeClassifierError("deterministically selected components differ from the frozen config")
    return selected


def load_dataset(config: Mapping[str, Any]) -> dict[str, Any]:
    manifest, manifest_bytes, manifest_sha = _bound_json(ROOT, MANIFEST_RELATIVE,
        "pinned ink-scan curation manifest", 1_000_000)
    if manifest_sha != MANIFEST_SHA256 or manifest_sha != config["curation_manifest_sha256"]:
        raise StrokeClassifierError("curation manifest identity mismatch")
    if (manifest.get("schema") != "graphreader.stroke-seeded-xtick-v1-inkscan-curated-train.v1"
            or manifest.get("split") != "train" or manifest.get("training_performed") is not False
            or manifest.get("optimizer_steps") != 0 or manifest.get("production_approval") is not False):
        raise StrokeClassifierError("curation manifest is not the pinned untrained train-only corpus")
    if manifest.get("input_admission", {}).get("probe_input_read") is not False or any(
            manifest.get("input_admission", {}).get(key) is not False
            for key in ("dev_input_read", "sealed_input_read", "private_input_read")):
        raise StrokeClassifierError("curation manifest reports a prohibited input read")
    if digest((ROOT / CURATOR_RELATIVE).read_bytes()) != config["curator_sha256"]:
        raise StrokeClassifierError("pinned curation source changed")
    sources = manifest.get("source_groups")
    records = manifest.get("records")
    if not isinstance(sources, list) or not isinstance(records, list) or len(records) != 896:
        raise StrokeClassifierError("curation source or record inventory changed")
    identities = {s["source_id"]: s["source_identity_sha256"] for s in sources}
    failed_sources = {s["source_id"] for s in manifest.get("scanner_failed_sources", [])}
    if len(identities) != len(sources) or not failed_sources <= set(identities):
        raise StrokeClassifierError("failed or duplicate source group identity is malformed")

    state_occurrences: Counter[str] = Counter()
    kind_by_state: dict[str, Counter[str]] = defaultdict(Counter)
    semantic_by_hash: dict[str, set[int]] = defaultdict(set)
    all_groups_by_hash: dict[str, set[str]] = defaultdict(set)
    row_bindings: list[dict[str, Any]] = []
    seen_cases: set[str] = set()
    image_bytes_by_hash: dict[str, bytes] = {}
    image_paths_verified: set[str] = set()
    for row in records:
        if row.get("split") != "train" or row.get("source_group_id") != row.get("source_id"):
            raise StrokeClassifierError("non-train or malformed source-group record")
        case_id = row.get("case_id")
        source_id = row.get("source_id")
        if not isinstance(case_id, str) or case_id in seen_cases or identities.get(source_id) != row.get("source_identity_sha256"):
            raise StrokeClassifierError("duplicate case or unknown source identity")
        if source_id in failed_sources:
            raise StrokeClassifierError("failed scanner source has admitted curation rows")
        seen_cases.add(case_id)
        annotation, annotation_bytes, annotation_sha = _bound_json(
            ROOT / CURATION_RELATIVE, row["annotation_path"], f"curation annotation {case_id}", 65_536)
        if annotation_sha != row.get("annotation_sha256"):
            raise StrokeClassifierError(f"annotation hash differs from manifest: {case_id}")
        for key in ("case_id", "source_id", "source_group_id", "source_identity_sha256", "split", "kind", "image_sha256"):
            if annotation.get(key) != row.get(key):
                raise StrokeClassifierError(f"annotation/manifest binding mismatch: {case_id}:{key}")
        state = annotation.get("matching_state")
        if state not in ("unique_visible_tick", "no_ground_truth_tick_nearby", "ambiguous"):
            raise StrokeClassifierError(f"unknown matching_state: {case_id}")
        state_occurrences[state] += 1
        kind_by_state[state][str(row.get("kind"))] += 1
        if state == "ambiguous" and row.get("kind") != "excluded":
            raise StrokeClassifierError("ambiguous matching state is not excluded by the curated manifest")
        if row.get("kind") == "positive" and state != "unique_visible_tick":
            raise StrokeClassifierError("curation positive conflicts with unique-visible matching state")
        if row.get("kind") == "background_only" and state != "no_ground_truth_tick_nearby":
            raise StrokeClassifierError("curation background conflicts with no-nearby matching state")

        image_hash = row.get("image_sha256")
        if not isinstance(image_hash, str) or len(image_hash) != 64:
            raise StrokeClassifierError("invalid image hash")
        all_groups_by_hash[image_hash].add(source_id)
        if state != "ambiguous":
            label = int(state == "unique_visible_tick")
            semantic_by_hash[image_hash].add(label)
        image_relative = row["image_path"]
        if image_relative not in image_paths_verified:
            image = helpers._bound_read(ROOT / CURATION_RELATIVE, image_relative,
                f"curation image {case_id}", 1_000_000)
            if image.sha256 != image_hash:
                raise StrokeClassifierError(f"image hash differs from manifest: {case_id}")
            image_paths_verified.add(image_relative)
            image_bytes_by_hash.setdefault(image_hash, image.value)
        row_bindings.append({"case_id": case_id, "source_id": source_id,
            "source_identity_sha256": identities[source_id], "image_sha256": image_hash,
            "annotation_sha256": annotation_sha, "matching_state": state,
            "kind": row["kind"]})

    if state_occurrences != Counter({"unique_visible_tick": 114, "no_ground_truth_tick_nearby": 769, "ambiguous": 13}):
        raise StrokeClassifierError(f"matching_state occurrence counts changed: {dict(state_occurrences)}")
    if failed_sources & {r["source_id"] for r in row_bindings}:
        raise StrokeClassifierError("scanner-failed source has curation records")
    state_by_hash = _deduplicate_label_map(semantic_by_hash)
    component_by_group, components = _union_components(records)
    groups_per_hash = {image_hash: sorted(groups) for image_hash, groups in all_groups_by_hash.items()}
    if len(state_by_hash) != 533 or Counter(state_by_hash.values()) != Counter({1: 101, 0: 432}):
        raise StrokeClassifierError("unique image or stroke-validity class counts changed")
    group_class_sets: dict[int, set[str]] = defaultdict(set)
    for image_hash, label in state_by_hash.items():
        for group in groups_per_hash[image_hash]:
            group_class_sets[label].add(group)
    if (len(group_class_sets[1]), len(group_class_sets[0]), len(components)) != (13, 23, 18):
        raise StrokeClassifierError("source-group/component counts changed")

    unique_images = []
    for image_hash in sorted(state_by_hash):
        try:
            with Image.open(BytesIO(image_bytes_by_hash[image_hash])) as source:
                source.load()
                if source.size != (256, 96) or source.mode != "L":
                    raise StrokeClassifierError(f"classifier input must be exact 256x96 L crop: {image_hash}")
                pixels = np.asarray(source, dtype=np.float32) / 255.0
        except OSError as error:
            raise StrokeClassifierError(f"curated image decode failed: {image_hash}") from error
        unique_images.append({"image_sha256": image_hash, "label": state_by_hash[image_hash],
            "source_group_ids": groups_per_hash[image_hash], "pixels": pixels})
    linked_hashes = [groups for groups in groups_per_hash.values() if len(groups) > 1]
    row_bindings.sort(key=lambda row: row["case_id"])
    return {"manifest_sha256": manifest_sha, "manifest_bytes_sha256": digest(manifest_bytes),
        "row_count": len(records), "occurrence_counts": dict(state_occurrences),
        "kind_by_matching_state": {key: dict(value) for key, value in kind_by_state.items()},
        "unique_images": unique_images, "component_by_group": component_by_group,
        "components": components, "groups_per_hash": groups_per_hash,
        "source_group_count": len(identities), "failed_source_ids": sorted(failed_sources),
        "annotation_row_bindings_sha256": digest(canonical(row_bindings)),
        "source_group_ids": sorted({row["source_id"] for row in row_bindings}),
        "source_identity_by_group": identities,
        "component_link_counts": {"active_source_groups": len({row["source_id"] for row in row_bindings}),
            "components": len(components), "hashes_shared_by_multiple_groups": len(linked_hashes),
            "groups_in_multi_group_components": sum(len(v) for v in components.values() if len(v) > 1)}}


def validate_split(dataset: Mapping[str, Any], holdout_components: tuple[str, ...]) -> dict[str, Any]:
    component_by_group = dataset["component_by_group"]
    holdout = set(holdout_components)
    if len(holdout) != len(holdout_components) or not holdout <= set(dataset["components"]):
        raise StrokeClassifierError("holdout components are duplicated or unknown")
    train_groups = {g for g, c in component_by_group.items() if c not in holdout}
    holdout_groups = {g for g, c in component_by_group.items() if c in holdout}
    if train_groups & holdout_groups:
        raise StrokeClassifierError("source-group leakage across split")
    train_hashes = {row["image_sha256"] for row in dataset["unique_images"]
                    if set(row["source_group_ids"]) <= train_groups}
    holdout_hashes = {row["image_sha256"] for row in dataset["unique_images"]
                      if set(row["source_group_ids"]) <= holdout_groups}
    if train_hashes & holdout_hashes or len(train_hashes) + len(holdout_hashes) != len(dataset["unique_images"]):
        raise StrokeClassifierError("unique image leakage or omission across component split")
    train = [r for r in dataset["unique_images"] if r["image_sha256"] in train_hashes]
    held = [r for r in dataset["unique_images"] if r["image_sha256"] in holdout_hashes]
    train_counts = Counter(r["label"] for r in train)
    hold_counts = Counter(r["label"] for r in held)
    if min(train_counts[0], train_counts[1], hold_counts[0], hold_counts[1]) == 0:
        raise StrokeClassifierError("both classes must occur in train and internal holdout")
    return {"train_groups": sorted(train_groups), "holdout_groups": sorted(holdout_groups),
        "train_components": sorted(set(component_by_group[g] for g in train_groups)),
        "holdout_components": sorted(holdout), "train_image_hashes": train_hashes,
        "holdout_image_hashes": holdout_hashes, "train_counts": {"positive": train_counts[1], "negative": train_counts[0]},
        "holdout_counts": {"positive": hold_counts[1], "negative": hold_counts[0]}}


def _source_balanced_order(grouped_indices: Mapping[str, list[int]], draw_count: int, seed: int) -> list[int]:
    rng = random.Random(seed)
    groups = list(sorted(grouped_indices))
    rng.shuffle(groups)
    queues = {}
    for group, indices in grouped_indices.items():
        shuffled = list(indices)
        rng.shuffle(shuffled)
        queues[group] = shuffled
    base = []
    while any(queues.values()):
        for group in groups:
            if queues[group]:
                base.append(queues[group].pop())
    if not base:
        raise ValueError("cannot sample an empty class")
    result = list(base)
    while len(result) < draw_count:
        replay = list(base)
        rng.shuffle(replay)
        result.extend(replay[:draw_count - len(result)])
    return result[:draw_count]


def _classification_metrics(labels: list[int], scores: list[float], threshold: float = 0.5) -> dict[str, Any]:
    if len(labels) != len(scores) or not labels:
        raise ValueError("classification metrics require aligned, non-empty arrays")
    tp = sum(y == 1 and score >= threshold for y, score in zip(labels, scores))
    fp = sum(y == 0 and score >= threshold for y, score in zip(labels, scores))
    fn = sum(y == 1 and score < threshold for y, score in zip(labels, scores))
    tn = sum(y == 0 and score < threshold for y, score in zip(labels, scores))
    return {"count": len(labels), "positive_count": sum(labels), "negative_count": len(labels) - sum(labels),
        "threshold": threshold, "true_positive": tp, "false_positive": fp, "false_negative": fn,
        "true_negative": tn, "precision": tp / (tp + fp) if tp + fp else 0.0,
        "recall": tp / (tp + fn) if tp + fn else 0.0,
        "accuracy": (tp + tn) / len(labels)}


def _assert_gpu_binding(runtime: Mapping[str, Any], model: Any, tensors: list[Any], device: Any) -> None:
    if (runtime.get("device_name") != "AMD Radeon RX 6600 XT" or runtime.get("cpu_fallback") is not False
            or getattr(device, "type", None) != "cuda"):
        raise StrokeClassifierError("optimizer must bind exclusively to the RX 6600 XT")
    if any(parameter.device != device for parameter in model.parameters()):
        raise StrokeClassifierError("classifier parameters left the RX 6600 XT")
    if any(tensor.device != device for tensor in tensors):
        raise StrokeClassifierError("training tensor left the RX 6600 XT")


def load_config() -> tuple[dict[str, Any], bytes]:
    config, raw, _ = _bound_json(ROOT, CONFIG_PATH.relative_to(ROOT).as_posix(), "stroke classifier config", 65_536)
    expected = {
        "schema": "graphreader.stroke-seeded-xtick-v1-ink-stroke-classifier-config.v1",
        "recipe_id": "ink-stroke-validity-source-component-holdout-p1",
        "curation_manifest_sha256": MANIFEST_SHA256,
        "curator_sha256": digest((ROOT / CURATOR_RELATIVE).read_bytes()),
        "model_helper_sha256": "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5",
        "required_input_root": CURATION_RELATIVE, "allowed_split": "train",
        "label_positive": "matching_state=unique_visible_tick",
        "label_negative": "matching_state=no_ground_truth_tick_nearby",
        "excluded_matching_state": "ambiguous",
        "expected_unique_images": {"positive": 101, "negative": 432},
        "expected_source_groups_with_class": {"positive": 13, "negative": 23},
        "expected_connected_components": 18,
        "holdout_component_count": 4, "holdout_target_positive_unique_images": 20,
        "holdout_component_ids": [
            "stroke-seeded-xtick-v1-train-27103103",
            "stroke-seeded-xtick-v1-train-27103114",
            "stroke-seeded-xtick-v1-train-27103115",
            "stroke-seeded-xtick-v1-train-27103121",
        ],
        "holdout_positive_tolerance": 2, "holdout_min_negative_unique_images": 50,
        "split_seed": 27104151, "model_seed": 27104152, "sampler_seed": 27104153,
        "epochs": 20, "batch_size": 32, "learning_rate": 0.001,
        "optimizer_execution_authorized": True, "cpu_training_allowed": False,
        "dev_input_allowed": False, "sealed_input_allowed": False, "private_input_allowed": False,
        "probe_input_allowed": False, "production_approval": False, "release_eligible": False,
        "planned_output_root": OUTPUT_RELATIVE,
        "expected_runner_sha256": digest(Path(__file__).read_bytes()),
    }
    for key, value in expected.items():
        if config.get(key) != value:
            raise StrokeClassifierError(f"classifier config mismatch: {key}")
    return config, raw


def _build_classifier(torch: Any) -> Any:
    nn = torch.nn
    backbone = helpers.build_spatial_locator(torch).features

    class StrokeValidityClassifier(nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.features = backbone
            self.pool = nn.AdaptiveAvgPool2d(1)
            self.classifier = nn.Linear(24, 1)

        def forward(self, value: Any) -> Any:
            return self.classifier(self.pool(self.features(value)).flatten(1)).squeeze(1)

    return StrokeValidityClassifier()


def run() -> dict[str, Any]:
    config, config_bytes = load_config()
    output = ROOT / OUTPUT_RELATIVE
    if output.exists() or output.is_symlink() or output.parent != ROOT / "artifacts":
        raise StrokeClassifierError("planned run output must be a fresh project artifacts child")
    helpers._checked_chain(ROOT, output.parent, "stroke classifier output parent")
    output.mkdir(parents=False, exist_ok=False)
    (output / "started.json").write_bytes(canonical({"status": "started", "recipe_id": config["recipe_id"],
        "config_sha256": digest(config_bytes), "runner_sha256": digest(Path(__file__).read_bytes()),
        "started_utc": datetime.now(timezone.utc).isoformat(), "optimizer_steps": 0}))
    started = time.perf_counter()
    try:
        _RUN["phase"] = "train-only-data-admission"
        dataset = load_dataset(config)
        holdout_components = _choose_holdout(dataset, config)
        split = validate_split(dataset, holdout_components)
        if digest((ROOT / CURATOR_RELATIVE).read_bytes()) != config["curator_sha256"]:
            raise StrokeClassifierError("curation source changed after admission")
        _RUN["phase"] = "rx6600xt-binding"
        if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
            raise StrokeClassifierError("optimizer must use project GPU launcher; CPU fallback forbidden")
        import torch
        runtime = helpers.require_rx_6600_xt(torch)
        device = torch.device(str(runtime["device"]))
        torch.manual_seed(int(config["model_seed"]))
        train_rows = [r for r in dataset["unique_images"] if r["image_sha256"] in split["train_image_hashes"]]
        held_rows = [r for r in dataset["unique_images"] if r["image_sha256"] in split["holdout_image_hashes"]]
        features = torch.from_numpy(np.stack([r["pixels"] for r in train_rows]).astype(np.float32)).unsqueeze(1)
        labels = [int(r["label"]) for r in train_rows]
        positive_indices = {g: [] for g in split["train_groups"]}
        negative_indices = {g: [] for g in split["train_groups"]}
        owner_by_index = {}
        for index, row in enumerate(train_rows):
            groups = sorted(set(row["source_group_ids"]) & set(split["train_groups"]))
            if not groups:
                raise StrokeClassifierError("training image has no training source group")
            owner = groups[0]
            owner_by_index[index] = owner
            (positive_indices if labels[index] else negative_indices)[owner].append(index)
        positive_groups = {k: v for k, v in positive_indices.items() if v}
        negative_groups = {k: v for k, v in negative_indices.items() if v}
        if not positive_groups or not negative_groups:
            raise StrokeClassifierError("source-balanced sampling requires both classes")
        train_positive_count = sum(r["label"] == 1 for r in train_rows)
        train_negative_count = len(train_rows) - train_positive_count
        steps_per_epoch = math.ceil(max(train_positive_count, train_negative_count) / (int(config["batch_size"]) // 2))
        per_class_draws = steps_per_epoch * (int(config["batch_size"]) // 2)
        model = _build_classifier(torch).to(device)
        output_shape = tuple(model.features(torch.zeros((1, 1, 96, 256), device=device)).shape[-2:])
        if output_shape != (12, 32) or any(p.device != device for p in model.parameters()):
            raise StrokeClassifierError("classifier output shape or GPU residency mismatch")
        binding_probe = torch.zeros((1, 1, 96, 256), device=device)
        _assert_gpu_binding(runtime, model, [binding_probe], device)
        if model(binding_probe).shape != (1,):
            raise StrokeClassifierError("binary classifier output shape mismatch")
        optimizer = torch.optim.AdamW(model.parameters(), lr=float(config["learning_rate"]))
        losses, sampling_counts = [], []
        _RUN["phase"] = "optimizer-training"
        for epoch in range(int(config["epochs"])):
            seed = int(config["sampler_seed"]) + epoch
            pos_order = _source_balanced_order(positive_groups, per_class_draws, seed)
            neg_order = _source_balanced_order(negative_groups, per_class_draws, seed + 1)
            epoch_draws = Counter(pos_order + neg_order)
            sampling_counts.append({"epoch": epoch + 1, "positive_draws": len(pos_order),
                "negative_draws": len(neg_order), "positive_unique_covered": len({i for i in pos_order}),
                "negative_unique_covered": len({i for i in neg_order}),
                "repeated_draws": sum(n - 1 for n in epoch_draws.values() if n > 1),
                "positive_draws_by_group": dict(Counter(owner_by_index[i] for i in pos_order)),
                "negative_draws_by_group": dict(Counter(owner_by_index[i] for i in neg_order))})
            for batch_index in range(steps_per_epoch):
                start = batch_index * (int(config["batch_size"]) // 2)
                selected = pos_order[start:start + int(config["batch_size"]) // 2] + neg_order[start:start + int(config["batch_size"]) // 2]
                random.Random(seed + 2 + batch_index).shuffle(selected)
                x = features[selected].to(device)
                y = torch.tensor([labels[i] for i in selected], dtype=torch.float32, device=device)
                if x.device != device or y.device != device or int(y.sum()) != int(config["batch_size"]) // 2:
                    raise StrokeClassifierError("balanced batch or RX residency check failed")
                optimizer.zero_grad(set_to_none=True)
                logits = model(x)
                loss = torch.nn.functional.binary_cross_entropy_with_logits(logits, y)
                if not torch.isfinite(loss):
                    raise StrokeClassifierError("non-finite training loss")
                loss.backward()
                optimizer.step()
                _RUN["steps"] += 1
                losses.append(float(loss.detach().cpu()))
        torch.cuda.synchronize(device)
        _RUN["phase"] = "internal-train-source-holdout-evaluation"
        model.eval()
        held_features = torch.from_numpy(np.stack([r["pixels"] for r in held_rows]).astype(np.float32)).unsqueeze(1)
        held_labels = [int(r["label"]) for r in held_rows]
        with torch.inference_mode():
            scores = []
            for start in range(0, len(held_rows), int(config["batch_size"])):
                batch = held_features[start:start + int(config["batch_size"])].to(device)
                if batch.device != device:
                    raise StrokeClassifierError("holdout tensor left RX 6600 XT")
                scores.extend(torch.sigmoid(model(batch)).detach().cpu().tolist())
        metrics = _classification_metrics(held_labels, [float(s) for s in scores])
        checkpoint = output / "checkpoint.pt"
        torch.save({"state_dict": model.state_dict(), "model_identity": "stroke-seeded-ink-validity-rf31-p1",
            "architecture": "V3 31px feature stack + global average pooling + 24-to-1 binary head",
            "label_positive": config["label_positive"], "label_negative": config["label_negative"],
            "curation_manifest_sha256": dataset["manifest_sha256"], "config_sha256": digest(config_bytes),
            "heldout_components": split["holdout_components"], "model_seed": config["model_seed"],
            "runtime": runtime}, checkpoint)
        report = {"schema": "graphreader.stroke-seeded-ink-stroke-classifier-report.v1",
            "status": "completed-unregistered-internal-train-source-holdout-diagnostic",
            "recipe_id": config["recipe_id"], "acceptance_status": "internal train-source holdout only; not acceptance",
            "config_sha256": digest(config_bytes), "runner_sha256": digest(Path(__file__).read_bytes()),
            "curation_manifest_sha256": dataset["manifest_sha256"],
            "curator_sha256": config["curator_sha256"], "model_helper_sha256": config["model_helper_sha256"],
            "input_label_contract": {"positive": config["label_positive"], "negative": config["label_negative"],
                "excluded": config["excluded_matching_state"], "curation_kind_is_not_classifier_target": True,
                "ambiguous_occurrences_excluded": dataset["occurrence_counts"]["ambiguous"]},
            "dataset": {"row_occurrences": dataset["occurrence_counts"],
                "unique_images": {"positive": 101, "negative": 432},
                "source_groups": {"with_positive": 13, "with_negative": 23},
                "source_components": len(dataset["components"]), "component_link_counts": dataset["component_link_counts"],
                "kind_by_matching_state": dataset["kind_by_matching_state"],
                "failed_scanner_source_ids_excluded": dataset["failed_source_ids"],
                "source_group_ids": dataset["source_group_ids"],
                "source_identity_by_group": dataset["source_identity_by_group"],
                "manifest_row_bindings_sha256": dataset["annotation_row_bindings_sha256"]},
            "split": {"policy": "deterministic four-component holdout; among candidates with 18-22 positive and at least 50 negative hashes, minimize distance to 20 positives; SHA256 tie-break keyed by split seed",
                "seed": config["split_seed"], "train_component_ids": split["train_components"],
                "holdout_component_ids": split["holdout_components"],
                "train_source_groups": split["train_groups"], "holdout_source_groups": split["holdout_groups"],
                "train_counts": split["train_counts"], "holdout_counts": split["holdout_counts"],
                "source_overlap": sorted(set(split["train_groups"]) & set(split["holdout_groups"])),
                "image_hash_overlap_count": len(split["train_image_hashes"] & split["holdout_image_hashes"])},
            "model": {"identity": "stroke-seeded-ink-validity-rf31-p1",
                "input_shape": [1, 96, 256], "feature_map_shape": list(output_shape),
                "feature_receptive_field_pixels": 31, "view": "unaltered hash-verified curation crop; no truth-driven crop or line transform",
                "context_limit": "whole crop is visible to global average classifier; it may learn adjacent text or chart context as well as ink geometry"},
            "sampling": {"policy": "equal positive/negative draws per batch; exact duplicate hashes are assigned to the lexicographically first training source group in their connected component; within each class, deterministic round-robin owner-group order; every unique training image appears at least once per epoch, additional draws repeat a source-balanced epoch order",
                "batch_size": config["batch_size"], "steps_per_epoch": steps_per_epoch,
                "epochs": config["epochs"], "per_class_draws_per_epoch": per_class_draws,
                "positive_owner_group_count": len(positive_groups), "negative_owner_group_count": len(negative_groups),
                "epoch_draws": sampling_counts},
            "training": {"runtime": runtime, "optimizer": "AdamW", "learning_rate": config["learning_rate"],
                "model_seed": config["model_seed"], "loss_first": losses[0], "loss_last": losses[-1],
                "optimizer_steps": _RUN["steps"], "training_performed": True,
                "cpu_training_allowed": False, "probe_dev_sealed_private_inputs_read": False},
            "internal_train_source_holdout_metrics_once": metrics,
            "checkpoint_path": checkpoint.relative_to(ROOT).as_posix(), "checkpoint_sha256": digest(checkpoint.read_bytes()),
            "elapsed_seconds": round(time.perf_counter() - started, 6),
            "production_approval": False, "release_eligible": False,
            "limitations": ["Holdout is only a few source components and about 20 positive images; metrics are highly uncertain and not acceptance.",
                "The binary label is proposal-to-visible-tick matching state, not text correctness or OCR glyph presence.",
                "The global-average head sees the entire crop and could exploit text or local context; no causal cue isolation was performed.",
                "The curation source reports one scanner-failed train source; that source has no admitted rows and is recorded as excluded."]}
        (output / "report.json").write_bytes(canonical(report))
        return report
    except Exception as error:
        (output / "failure.json").write_bytes(canonical({"status": "failed", "phase": _RUN["phase"],
            "error_type": type(error).__name__, "error": str(error),
            "config_sha256": digest(config_bytes), "runner_sha256": digest(Path(__file__).read_bytes()),
            "optimizer_steps": _RUN["steps"], "dev_sealed_private_probe_inputs_read": False,
            "failed_utc": datetime.now(timezone.utc).isoformat()}))
        raise


if __name__ == "__main__":
    print(json.dumps(run(), sort_keys=True, indent=2))
