# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Curate relaxed-width ink proposals from the frozen P3 train groups only."""

from __future__ import annotations

from collections import Counter, defaultdict
from hashlib import sha256
from io import BytesIO
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import time
from types import ModuleType
from typing import Any

import numpy as np
from PIL import __version__ as PILLOW_VERSION

ROOT = Path(__file__).resolve().parents[6]
HERE = Path(__file__).resolve().parent
CONFIG = HERE / "p3.json"
P1_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/p1.json"
P2_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/p2.json"
P3_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/p3.json"
V6_ROOT_REL = "artifacts/stroke-seeded-xtick-v1-train-verified-v6"
V6_MANIFEST_REL = f"{V6_ROOT_REL}/manifest.json"
CURATION_REL = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2"
CURATION_MANIFEST_REL = f"{CURATION_REL}/manifest.json"
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-wideproposal-train-candidates-p3"
V6_SHA = "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a"
CURATION_SHA = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
P3_CONFIG_SHA = "927b93c88d7e61dc505516995b72f2235aa7fc8824e5fc54b45ae6741c3c5717"
MAX_MANIFEST_BYTES = 5_000_000
MAX_ANNOTATION_BYTES = 65_536
MAX_IMAGE_BYTES = 1_000_000
MAX_RASTER_BYTES = 100_000_000
MAX_PROPOSALS = 20_000
WIDE_CLUSTER_LIMIT = 1_000_000
RUN_STATE = {"phase": "startup"}
EXECUTED_SCRIPT_SHA256 = globals().get("__pinned_source_sha256__")

# The complete local Python execution chain is loaded by compile/exec from these
# verified bytes, avoiding importlib's unchecked stale-bytecode path.
SOURCE_SHA256 = {
    "ml/synthetic/__init__.py": "6875b53c0bbbd101cb06ff7987f06ea66520493f5361456ebc1d724870d13add",
    "ml/synthetic/fonts.py": "c4ec7a2333e0410d4c49cc41c154d3b80a2898c5682a135c25e1162a806d58fc",
    "ml/synthetic/schema.py": "237d9296f2f72a11bb7020c2abac022026f72a56e4e0eac2d322f23a3e6c8acb",
    "ml/synthetic/scene.schema.json": "fc72d6ce4e37dd2893e5fe56d087b0496008a904f11ebc4a5c525a4a27843e0f",
    "ml/synthetic/renderer.py": "c49c070afc505f92bfff7ff55b22e669c3ec2b76b56ff807b72a026ca0535283",
    "ml/synthetic/templates.py": "f59733c5f0589bd1b9143d3c9ae83015b4c53ab593de229d56e46d9f11ec2f1c",
    "ml/ocr/fragmented_xtick_detector_v2/__init__.py": "f7e6cdf4599e357c34dd18515c725b32b405a1fb59be8b720da6c47c0b53bea9",
    "ml/ocr/fragmented_xtick_detector_v2/materialize.py": "16b48f1a59385181f59b2245fc9ff53816093c55ade4fdc40b2f5bd9eb19a3b7",
    "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py": "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5",
    "ml/ocr/stroke_seeded_xtick_v1/__init__.py": "a5f260e011ced1132186a0c77f55addb8991a39f582a67092b88579e8e8e97b0",
    "ml/ocr/stroke_seeded_xtick_v1/materialize.py": "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d",
    "ml/ocr/stroke_seeded_xtick_v1/probe_materialize.py": "c0a6d0559bdd384a84785926fa4dbf0230814f0663b55eff978f5ee32ff6a7a6",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py": "b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/train_proposal_curation_v1/curate_train_proposals.py": "7e8a32dda6b5cf74f013db0992bd66ed894301dca21cf94ea32f64f1275c32df",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_recall_v1/diagnose_misses.py": "32662ad05c220de6a9a5fce451f5101baab32ce4ac6156f1ba4b58d3266b834a",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1/Program.cs": "40b104c23f0b60ca029dbe596dbd46117211d4f32b13837679380e4da47a8780",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1/ShortTickInkPrototype.csproj": "7a6f7599b67f6e09425cc8168c894e0f2263f66e91bb2c254f593409a5ec3632",
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1/bin/Release/net10.0-windows/ShortTickInkPrototype.dll": "7c77eaa5c1949d5f7e22c35b32d4751e24c6a2bed7e8664b77a50b032ebaa831",
}


class CurationError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _safe_path(path: Path, root: Path) -> None:
    root = root.resolve(strict=True)
    try:
        relative = path.relative_to(root)
    except ValueError as error:
        raise CurationError(f"input escapes project root: {path}") from error
    cursor = root
    for part in (".", *relative.parts):
        if part != ".":
            cursor /= part
        try:
            info = cursor.lstat()
        except OSError as error:
            raise CurationError(f"missing input path component: {cursor}") from error
        if cursor.is_symlink() or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            raise CurationError(f"symlink/reparse input rejected: {cursor}")


def read_bounded(path: Path, root: Path, label: str, maximum: int) -> tuple[bytes, str]:
    _safe_path(path, root)
    with path.open("rb") as stream:
        payload = stream.read(maximum + 1)
    if not payload or len(payload) > maximum:
        raise CurationError(f"{label} is empty or exceeds its bound")
    return payload, digest(payload)


def admit_train_sources(manifest: dict[str, Any], p3: dict[str, Any]) -> list[dict[str, Any]]:
    allowed = tuple(p3.get("train_source_groups", ()))
    holdout = set(p3.get("holdout_component_ids", ()))
    if len(allowed) != 19 or len(set(allowed)) != len(allowed) or set(allowed) & holdout:
        raise CurationError("P3 source allowlist is malformed or intersects prior holdout metadata")
    sources = manifest.get("sources")
    if not isinstance(sources, list):
        raise CurationError("V6 source inventory is malformed")
    by_id = {source.get("source_id"): source for source in sources}
    if len(by_id) != len(sources) or not set(allowed) <= set(by_id):
        raise CurationError("V6 source inventory is missing or duplicates an allowed P3 train group")
    selected = [by_id[source_id] for source_id in allowed]
    if any(source.get("split") != "train" or source.get("recipe", {}).get("split_role") != "train"
           for source in selected):
        raise CurationError("allowed P3 group is not explicitly train split")
    # This is the only source list passed to any source-data opener or scanner.
    return selected


def strict_label(kind: str, matching_state: str) -> int | None:
    if kind == "positive" and matching_state == "unique_visible_tick":
        return 1
    if kind == "background_only" and matching_state == "no_ground_truth_tick_nearby":
        return 0
    return None


def reconcile_candidates(base_labels: dict[str, set[int]], candidates: list[dict[str, Any]]) -> dict[str, Any]:
    by_hash: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in candidates:
        by_hash[row["image_sha256"]].append(row)
    kept, duplicate, quarantined = [], [], []
    for image_hash, rows in sorted(by_hash.items()):
        semantic_labels = {int(row["matching_state"] == "unique_visible_tick") for row in rows
                           if row["matching_state"] in {"unique_visible_tick", "no_ground_truth_tick_nearby"}}
        strict_rows = [{**row, "label": strict_label(row["kind"], row["matching_state"])}
                       for row in rows if strict_label(row["kind"], row["matching_state"]) is not None]
        labels = semantic_labels | base_labels.get(image_hash, set())
        if any(row["matching_state"] == "ambiguous" for row in rows):
            quarantined.extend({**row, "quarantine_reason": "same_image_has_ambiguous_truth_occurrence"} for row in strict_rows)
        elif len(labels) != 1:
            quarantined.extend({**row, "quarantine_reason": "same_image_semantic_label_conflict"} for row in strict_rows)
        elif not strict_rows:
            continue
        elif image_hash in base_labels:
            duplicate.extend({**row, "duplicate_of_p3_baseline": True} for row in strict_rows)
        else:
            representative = min(strict_rows, key=lambda row: row["case_id"])
            representative = dict(representative)
            representative["source_group_ids"] = sorted({row["source_group_id"] for row in strict_rows})
            representative["duplicate_candidate_case_ids"] = sorted(row["case_id"] for row in strict_rows)
            kept.append(representative)
            duplicate.extend({**row, "duplicate_candidate_case_ids": sorted(r["case_id"] for r in strict_rows)}
                             for row in strict_rows if row["case_id"] != representative["case_id"])
    return {"kept": kept, "same_label_baseline_duplicates": duplicate,
            "conflicts_quarantined": quarantined}


def assemble_manifest(facts: dict[str, Any]) -> dict[str, Any]:
    reconciled = facts["reconciled"]
    kept = reconciled["kept"]
    duplicates = reconciled["same_label_baseline_duplicates"]
    baseline_duplicates = sum(bool(row.get("duplicate_of_p3_baseline")) for row in duplicates)
    group_additions: dict[str, Counter[int]] = defaultdict(Counter)
    for row in kept:
        for group in row["source_group_ids"]:
            group_additions[group][int(row["label"])] += 1
    panels = [{key: value for key, value in panel.items() if key != "proposals"}
              for _, panel in sorted(facts["panel_records"].items())]
    baseline_class_counts = Counter(next(iter(labels)) for labels in facts["baseline_labels"].values())
    config, scanner = facts["config"], facts["scanner"]
    return {
        "schema": "graphreader.stroke-seeded-wide-proposal-train-candidates.v1",
        "recipe_id": config["recipe_id"], "status": "curated-untrained-exploratory-train-only",
        "config_sha256": facts["config_sha256"], "p3_config_sha256": facts["p3_config_sha256"],
        "input_manifests": {V6_MANIFEST_REL: facts["v6_manifest_sha256"],
                            CURATION_MANIFEST_REL: facts["curation_manifest_sha256"]},
        "executed_local_source_sha256": facts["dependency_hashes"],
        "executed_wrapper_source_sha256": facts["executed_script_sha256"],
        "runtime": facts["runtime"],
        "source_group_allowlist": sorted(facts["allowed_groups"]),
        "source_group_count": len(facts["allowed_groups"]),
        "source_pixel_receipts": facts["source_receipts"],
        "verified_v6_selected_crop_rows": sum(facts["v6_verified_counts"].values()),
        "v6_selected_kind_counts": dict(facts["v6_verified_counts"]),
        "baseline_p3_train": {"unique_images": len(facts["baseline_labels"]),
            "class_counts": {str(key): value for key, value in sorted(baseline_class_counts.items())},
            "row_hash_bindings": len(facts["baseline_groups"])},
        "scan": {"variant": "relaxed maximum cluster width only", "max_cluster_width": WIDE_CLUSTER_LIMIT,
            "radius": scanner.SCAN_RADIUS, "core_exclusion": scanner.AXIS_CORE_EXCLUSION,
            "edge_guard": scanner.EDGE_GUARD, "max_ink_gray": scanner.MAX_INK_GRAY,
            "baseline_parity_proposals": facts["baseline_count"],
            "wide_proposals": facts["wide_proposal_count"], "panel_summaries": panels},
        "curation": {"all_proposal_kind_counts": dict(facts["kind_counts"]),
            "strict_addition_candidate_occurrences": facts["strict_candidate_count"],
            "deduplicated_new_unique_images": len(kept),
            "deduplicated_new_class_counts": {str(key): value for key, value in
                sorted(Counter(row["label"] for row in kept).items())},
            "same_label_duplicates_of_frozen_p3_baseline": baseline_duplicates,
            "same_hash_candidate_duplicate_occurrences": len(duplicates) - baseline_duplicates,
            "semantic_conflict_rows_quarantined": len(reconciled["conflicts_quarantined"]),
            "deduplicated_new_images_by_source_group_and_class": {
                group: {str(label): count for label, count in sorted(values.items())}
                for group, values in sorted(group_additions.items())},
            "source_counts": {group: dict(value) for group, value in sorted(facts["source_counts"].items())},
            "label_policy": config["admission_policy"]},
        "retained_records": kept, "duplicate_records": duplicates,
        "quarantined_records": reconciled["conflicts_quarantined"],
        "v6_nonallowlist_sources_opened": 0, "prior_holdout_payloads_opened": 0,
        "probe_dev_sealed_private_inputs_read": False,
        "training_performed": False, "optimizer_steps": 0,
        "production_behavior_changed": False, "production_approval": False,
        "elapsed_seconds": round(float(facts["elapsed_seconds"]), 3),
    }


def _attach(parent: ModuleType, child_name: str, child: ModuleType) -> None:
    setattr(parent, child_name, child)


def _package(name: str, relative_dir: str, root: Path, init_relative: str | None = None) -> ModuleType:
    existing = sys.modules.get(name)
    if existing is not None:
        return existing
    package = ModuleType(name)
    package.__file__ = str(root / init_relative) if init_relative else str(root / relative_dir)
    package.__package__ = name
    package.__path__ = [str(root / relative_dir)]
    sys.modules[name] = package
    if "." in name:
        parent_name, child_name = name.rsplit(".", 1)
        _attach(sys.modules[parent_name], child_name, package)
    if init_relative:
        raw, actual = read_bounded(root / init_relative, root, f"package source {name}", 1_000_000)
        expected = SOURCE_SHA256.get(init_relative)
        if actual != expected:
            raise CurationError(f"package source hash mismatch: {init_relative}")
        exec(compile(raw, package.__file__, "exec", dont_inherit=True), package.__dict__)
    return package


def _source_module(name: str, relative: str, root: Path) -> ModuleType:
    raw, actual = read_bounded(root / relative, root, f"pinned source {relative}", 5_000_000)
    if actual != SOURCE_SHA256.get(relative):
        raise CurationError(f"pinned source hash mismatch: {relative}")
    module = ModuleType(name)
    module.__file__ = str(root / relative)
    module.__package__ = name.rpartition(".")[0]
    sys.modules[name] = module
    parent_name, child_name = name.rsplit(".", 1)
    _attach(sys.modules[parent_name], child_name, module)
    exec(compile(raw, module.__file__, "exec", dont_inherit=True), module.__dict__)
    return module


def load_pinned_runtime(root: Path) -> dict[str, ModuleType]:
    sys.dont_write_bytecode = True
    schema_bytes, schema_sha = read_bounded(root / "ml/synthetic/scene.schema.json", root,
        "pinned synthetic scene schema", 2_000_000)
    if schema_sha != SOURCE_SHA256["ml/synthetic/scene.schema.json"]:
        raise CurationError("pinned synthetic scene schema hash mismatch")
    del schema_bytes
    for name, rel, init in (
        ("ml", "ml", None), ("ml.synthetic", "ml/synthetic", "ml/synthetic/__init__.py"),
        ("ml.ocr", "ml/ocr", None),
        ("ml.ocr.fragmented_xtick_detector_v2", "ml/ocr/fragmented_xtick_detector_v2", "ml/ocr/fragmented_xtick_detector_v2/__init__.py"),
        ("ml.ocr.fragmented_xtick_detector_v3", "ml/ocr/fragmented_xtick_detector_v3", None),
        ("ml.ocr.stroke_seeded_xtick_v1", "ml/ocr/stroke_seeded_xtick_v1", "ml/ocr/stroke_seeded_xtick_v1/__init__.py"),
        ("ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic", "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic", None),
        ("ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment", "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment", None),
        ("ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1", "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/train_proposal_curation_v1", None),
        ("ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.short_tick_recall_v1", "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_recall_v1", None),
    ):
        _package(name, rel, root, init)
    # Execute every local Python dependency from its checked bytes in dependency order.
    _source_module("ml.synthetic.fonts", "ml/synthetic/fonts.py", root)
    _source_module("ml.synthetic.schema", "ml/synthetic/schema.py", root)
    _source_module("ml.synthetic.renderer", "ml/synthetic/renderer.py", root)
    _source_module("ml.synthetic.templates", "ml/synthetic/templates.py", root)
    _source_module("ml.ocr.fragmented_xtick_detector_v2.materialize", "ml/ocr/fragmented_xtick_detector_v2/materialize.py", root)
    admission = _source_module("ml.ocr.fragmented_xtick_detector_v3.gpu_train", "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py", root)
    materialize = _source_module("ml.ocr.stroke_seeded_xtick_v1.materialize", "ml/ocr/stroke_seeded_xtick_v1/materialize.py", root)
    _source_module("ml.ocr.stroke_seeded_xtick_v1.probe_materialize", "ml/ocr/stroke_seeded_xtick_v1/probe_materialize.py", root)
    common = _source_module("ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment.run_axis_alignment",
                            "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py", root)
    curator = _source_module("ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1.curate_train_proposals",
                             "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/train_proposal_curation_v1/curate_train_proposals.py", root)
    scanner = _source_module("ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.short_tick_recall_v1.diagnose_misses",
                             "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_recall_v1/diagnose_misses.py", root)
    return {"admission": admission, "materialize": materialize, "common": common,
            "curator": curator, "scanner": scanner}


def _p3_baseline(root: Path, curator: ModuleType, p3_ids: set[str]) -> tuple[dict[str, set[int]], dict[str, list[str]]]:
    manifest_bytes, manifest_sha = read_bounded(root / CURATION_MANIFEST_REL, root, "pinned train curation manifest", MAX_MANIFEST_BYTES)
    if manifest_sha != CURATION_SHA:
        raise CurationError("P3 baseline curation manifest hash mismatch")
    manifest = json.loads(manifest_bytes)
    if manifest.get("split") != "train" or manifest.get("training_performed") is not False:
        raise CurationError("source curation manifest is not an untrained train-only corpus")
    selected = [row for row in manifest.get("records", []) if row.get("source_group_id") in p3_ids]
    groups = [row.get("source_id") for row in manifest.get("source_groups", []) if row.get("source_id") in p3_ids]
    if len(groups) != 19 or set(groups) != p3_ids:
        raise CurationError("baseline curation lacks one or more frozen P3 train groups")
    semantic: dict[str, set[int]] = defaultdict(set)
    groups_by_hash: dict[str, set[str]] = defaultdict(set)
    for row in selected:
        ann_bytes, ann_sha = read_bounded(root / CURATION_REL / row["annotation_path"], root,
            f"P3 train annotation {row['case_id']}", MAX_ANNOTATION_BYTES)
        if ann_sha != row.get("annotation_sha256"):
            raise CurationError(f"P3 annotation checksum mismatch: {row['case_id']}")
        ann = json.loads(ann_bytes)
        if ann.get("case_id") != row["case_id"] or ann.get("source_group_id") not in p3_ids:
            raise CurationError(f"P3 annotation identity mismatch: {row['case_id']}")
        image_bytes, image_sha = read_bounded(root / CURATION_REL / row["image_path"], root,
            f"P3 train crop {row['case_id']}", MAX_IMAGE_BYTES)
        if image_sha != row.get("image_sha256") or ann.get("image_sha256") != image_sha:
            raise CurationError(f"P3 crop checksum mismatch: {row['case_id']}")
        del image_bytes
        state = ann.get("matching_state")
        if state != "ambiguous":
            semantic[image_sha].add(int(state == "unique_visible_tick"))
        groups_by_hash[image_sha].add(row["source_group_id"])
    conflicts = {key: sorted(value) for key, value in semantic.items() if len(value) != 1}
    if conflicts:
        raise CurationError(f"P3 baseline has semantic conflicts: {list(conflicts)[:5]}")
    labels = {key: value for key, value in semantic.items() if len(value) == 1}
    if len(labels) != 446 or Counter(next(iter(v)) for v in labels.values()) != Counter({1: 81, 0: 365}):
        raise CurationError("train-only P3 baseline image/class inventory differs from pinned P3 report")
    return labels, {key: sorted(value) for key, value in groups_by_hash.items()}


def run() -> dict[str, Any]:
    started = time.perf_counter()
    root = ROOT.resolve(strict=True)
    if not isinstance(EXECUTED_SCRIPT_SHA256, str) or len(EXECUTED_SCRIPT_SHA256) != 64:
        raise CurationError("run only through the byte-pinned compile/exec wrapper")
    if Path(__file__).resolve().parents[6] != root:
        raise CurationError("script root does not resolve to the project checkout")
    output = root / OUTPUT_REL
    if output.exists() or output.is_symlink() or output.parent != root / "artifacts":
        raise CurationError("curation output must be a fresh direct project artifacts child")
    _safe_path(output.parent, root)
    cfg_bytes, cfg_sha = read_bounded(CONFIG, root, "wide-proposal config", 100_000)
    p1_config_bytes, p1_config_sha = read_bounded(root / P1_CONFIG_REL, root, "parent P1 curation config", 100_000)
    p2_config_bytes, p2_config_sha = read_bounded(root / P2_CONFIG_REL, root, "parent P2 curation config", 100_000)
    p3_bytes, p3_sha = read_bounded(root / P3_CONFIG_REL, root, "frozen P3 config", 100_000)
    if p3_sha != P3_CONFIG_SHA:
        raise CurationError("frozen P3 config bytes differ from the expected source identity")
    config, p2_config, p3 = json.loads(cfg_bytes), json.loads(p2_config_bytes), json.loads(p3_bytes)
    if (config["parent_p2_config_sha256"] != "2d750a9690832ac60537d09fe78e529e87c9151295f044562197c94c9a47253b"
            or config["parent_p2_config_sha256"] != p2_config_sha
            or p2_config.get("parent_p1_config_sha256") != p1_config_sha
            or config["p3_config_sha256"] != p3_sha or config["v6_manifest_sha256"] != V6_SHA
            or config["v2_curation_manifest_sha256"] != CURATION_SHA):
        raise CurationError("new recipe does not bind all expected data/config inputs")
    runtime = load_pinned_runtime(root)
    admission, materialize, common, curator, scanner = (runtime[k] for k in ("admission", "materialize", "common", "curator", "scanner"))

    v6_bytes, v6_sha = read_bounded(root / V6_MANIFEST_REL, root, "pinned V6 train manifest", MAX_MANIFEST_BYTES)
    if v6_sha != V6_SHA:
        raise CurationError("V6 manifest hash mismatch")
    v6 = json.loads(v6_bytes)
    if v6.get("split") != "train" or v6.get("record_count") != 681:
        raise CurationError("V6 manifest is not the fixed train-only corpus")
    selected_sources = admit_train_sources(v6, p3)
    allowed = {source["source_id"] for source in selected_sources}
    if allowed != set(config["train_source_group_ids"]):
        raise CurationError("materializer source allowlist differs from the preregistered P3 groups")
    output.mkdir(parents=False, exist_ok=False)
    v6_records = {row["case_id"]: row for row in v6["records"] if row.get("source_id") in allowed}
    if any(row.get("split") != "train" for row in v6_records.values()):
        raise CurationError("non-train V6 row entered the selected source subset")
    baseline_labels, baseline_groups = _p3_baseline(root, curator, allowed)

    curation_manifest_bytes, curation_manifest_sha = read_bounded(root / CURATION_MANIFEST_REL, root,
        "pinned source-raster curation receipts", MAX_MANIFEST_BYTES)
    if curation_manifest_sha != CURATION_SHA:
        raise CurationError("source-raster curation receipt hash mismatch")
    curation_manifest = json.loads(curation_manifest_bytes)
    panel_sha_by_group = {s["source_id"]: s["reconstructed_full_panel_pixel_sha256"]
                          for s in curation_manifest["source_groups"] if s["source_id"] in allowed}
    if set(panel_sha_by_group) != allowed:
        raise CurationError("pinned source-raster receipts do not cover all P3 groups")

    source_work = []
    v6_verified_counts = Counter()
    source_receipts = []
    RUN_STATE["phase"] = "train-source-render-and-v6-byte-verification"
    for source in selected_sources:
        source_id = source["source_id"]
        recipe = curator._verify_recipe_identity(source)
        rows = materialize.materialize_recipe(recipe)
        subset_refs = {row["case_id"]: v6_records[row["case_id"]] for row in rows if row["case_id"] in v6_records}
        if len(subset_refs) != len(rows):
            raise CurationError(f"selected train materializer row inventory mismatch: {source_id}")
        v6_verified_counts.update(curator._verify_v6_materialization(source, rows, subset_refs,
            root / V6_ROOT_REL))
        scene = materialize.build_train_scene(recipe)
        image, rendered, _ = materialize.render_train_scene(scene)
        if image.width * image.height > MAX_RASTER_BYTES or len(image.tobytes()) > MAX_RASTER_BYTES:
            raise CurationError(f"source raster exceeded the bound: {source_id}")
        if len(rendered["panels"]) != int(source["panel_count"]):
            raise CurationError(f"rendered panel count differs from V6 recipe: {source_id}")
        if digest(image.tobytes()) != panel_sha_by_group[source_id]:
            raise CurationError(f"reconstructed full-panel pixel hash differs from prior curation receipt: {source_id}")
        if (not rows or rows[0]["rendered_font"] != source.get("font_provenance")
                or rows[0]["rendered_font"].get("source") != "project_local"):
            raise CurationError(f"project-local source font receipt mismatch: {source_id}")
        glyphs_by_panel = curator._glyph_geometry(rendered, image)
        rows_by_panel: dict[str, list[dict[str, Any]]] = defaultdict(list)
        for row in rows:
            rows_by_panel[row["source_panel_id"]].append(row)
        panel_by_id = {panel["panel_id"]: panel for panel in rendered["panels"]}
        truths_by_panel = {panel_id: curator._panel_truth(panel_rows, panel_by_id[panel_id], image,
            glyphs_by_panel[panel_id]) for panel_id, panel_rows in rows_by_panel.items()}
        source_work.append({"source": source, "image": image, "rendered": rendered,
            "rows_by_panel": rows_by_panel, "truths_by_panel": truths_by_panel,
            "glyphs_by_panel": glyphs_by_panel})
        source_receipts.append({"source_id": source_id, "source_identity_sha256": source["source_identity_sha256"],
            "panel_pixel_sha256": digest(image.tobytes()), "width": image.width, "height": image.height,
            "panel_count": len(rendered["panels"]), "v6_crop_rows_verified": len(rows)})
    if sum(v6_verified_counts.values()) != len(v6_records):
        raise CurationError("selected P3 source crop/annotation verification did not consume exactly its V6 rows")

    local_state = root / "tools"
    for name, relative in (("DOTNET_CLI_HOME", "dotnet-home"), ("NUGET_PACKAGES", "nuget-packages"),
                           ("TEMP", "tmp"), ("TMP", "tmp")):
        local_path = local_state / relative
        local_path.mkdir(parents=True, exist_ok=True)
        os.environ[name] = str(local_path)
    dotnet_info = subprocess.run(["dotnet", "--version"], cwd=root, capture_output=True,
        text=True, check=False, timeout=30)
    if dotnet_info.returncode != 0:
        raise CurationError(f"project-local .NET scanner runtime version check failed: {dotnet_info.stderr[-1000:]}")
    dll_rel = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1/bin/Release/net10.0-windows/ShortTickInkPrototype.dll"
    dll = root / dll_rel
    dll_bytes, dll_sha = read_bounded(dll, root, "pinned truth-blind scanner binary", 100_000_000)
    del dll_bytes
    if dll_sha != SOURCE_SHA256[dll_rel]:
        raise CurationError("scanner binary identity changed")
    scanner_inputs = []
    by_source = {work["source"]["source_id"]: work for work in source_work}
    for work in source_work:
        source = work["source"]
        panel_sha = digest(work["image"].tobytes())
        scanner_inputs.append(common.build_source_input({"source_id": source["source_id"],
            "source_pixel_sha256": panel_sha}, [], work["rendered"]["panels"], work["image"]))
    scan_outputs = curator._scanner_process(dll, scanner_inputs)
    failures = []
    for result in scan_outputs:
        source_id = result.get("source_id")
        if source_id not in allowed:
            failures.append({"source_id": source_id, "error": "scanner returned an unallowed source ID"})
            continue
        expected_panels = {p["panel_id"] for p in by_source[source_id]["rendered"]["panels"]}
        error = curator.scanner_result_error(result, source_id, expected_panels)
        if error:
            failures.append({"source_id": source_id, "error": error})
    if len(scan_outputs) != len(selected_sources) or failures:
        raise CurationError(f"truth-blind scanner failed admitted train sources: {failures or 'result count mismatch'}")
    if sum(len(panel["ink_proposals"]) for out in scan_outputs for panel in out["panels"]) > MAX_PROPOSALS:
        raise CurationError("baseline scan proposal count exceeds configured limit")

    phase = "truth-blind-wide-scan"
    RUN_STATE["phase"] = phase
    panel_records: dict[str, dict[str, Any]] = {}
    proposals = []
    baseline_count = 0
    for result in scan_outputs:
        work = by_source[result["source_id"]]
        image = work["image"]
        for scanned in result["panels"]:
            panel_id = scanned["panel_id"]
            x0, y0, x1, y1 = map(int, scanned["bounds"])
            frame = np.asarray(image.crop((x0, y0, x1, y1)).convert("L"), dtype=np.uint8)
            axis = scanned["selected_x_axis_line"]
            local_axis = [axis[0] - x0, axis[1] - y0, axis[2] - x0, axis[3] - y0]
            baseline, _ = scanner.scan(frame, local_axis)
            if len(baseline) != len(scanned["ink_proposals"]):
                raise CurationError(f"Python/C# baseline proposal count differs for {panel_id}")
            for actual, frozen in zip(baseline, scanned["ink_proposals"]):
                geometry = [actual["center"][0] + x0, actual["center"][1] + y0,
                    actual["line"][0] + x0, actual["line"][1] + y0,
                    actual["line"][2] + x0, actual["line"][3] + y0]
                frozen_geometry = [*frozen["center"], *frozen["line"]]
                if max(abs(float(a) - float(b)) for a, b in zip(geometry, frozen_geometry)) > 1e-6:
                    raise CurationError(f"Python/C# baseline geometry differs for {panel_id}")
                if any(actual[k] != frozen[k] for k in ("dark_pixel_count", "cluster_width_pixels", "longest_dark_run")):
                    raise CurationError(f"Python/C# baseline trace differs for {panel_id}")
            baseline_count += len(baseline)
            wide, _ = scanner.scan(frame, local_axis, max_cluster_width=WIDE_CLUSTER_LIMIT)
            global_wide = [{**row, "center": [row["center"][0] + x0, row["center"][1] + y0],
                "line": [row["line"][0] + x0, row["line"][1] + y0,
                         row["line"][2] + x0, row["line"][3] + y0]} for row in wide]
            panel_records[panel_id] = {"source_id": result["source_id"], "panel_id": panel_id,
                "panel_index": next(i for i, p in enumerate(work["rendered"]["panels"]) if p["panel_id"] == panel_id),
                "wide_proposal_count": len(global_wide), "baseline_proposal_count": len(baseline),
                "selected_axis": scanned["selected_x_axis_line"],
                "axis_uncertainty": scanned.get("uncertainty_reasons", []),
                "needs_review": bool(scanned.get("needs_review")), "proposals": global_wide}
            proposals.extend((panel_id, row) for row in global_wide)
    if baseline_count == 0 or len(proposals) > MAX_PROPOSALS:
        raise CurationError("baseline or wide proposal inventory violates bounds")

    # Ground truth is consulted only after all wide proposal pixels/geometry are fixed.
    assignments_by_panel = {
        panel_id: curator.assign_proposals(panel["proposals"],
            by_source[panel["source_id"]]["truths_by_panel"].get(panel_id, []))
        for panel_id, panel in panel_records.items()
    }
    (output / "images").mkdir()
    (output / "annotations").mkdir()
    phase = "truth-blind-postscan-curation"
    RUN_STATE["phase"] = phase
    try:
        counts = Counter()
        source_counts: dict[str, Counter[str]] = defaultdict(Counter)
        candidate_rows = []
        candidate_payloads = {}
        strict_candidate_count = 0
        proposal_index_by_panel: dict[str, int] = Counter()
        for panel_id, proposal in proposals:
            panel = panel_records[panel_id]
            work = by_source[panel["source_id"]]
            truths = work["truths_by_panel"].get(panel_id, [])
            # A global per-panel proposal index produces deterministic, noncolliding case IDs.
            index = proposal_index_by_panel[panel_id]
            proposal_index_by_panel[panel_id] += 1
            assignment = assignments_by_panel[panel_id][index]
            curation = curator._curate_one(proposal, assignment, truths,
                work["glyphs_by_panel"][panel_id], work["image"])
            kind, state = curation["kind"], curation["matching_state"]
            counts[kind] += 1
            source_counts[panel["source_id"]][kind] += 1
            case_id = f"{panel['source_id']}-p{panel['panel_index']}-wide{index:04d}"
            crop = curation.pop("crop")
            image_buffer = BytesIO()
            crop.save(image_buffer, format="PNG", compress_level=9)
            image_bytes = image_buffer.getvalue()
            image_sha = digest(image_bytes)
            image_path = f"images/{case_id}.png"
            annotation_path = f"annotations/{case_id}.json"
            source = work["source"]
            annotation = {"schema": "graphreader.stroke-seeded-xtick-v1-wide-inkscan-curated-crop.v1",
                "case_id": case_id, "source_id": source["source_id"], "source_group_id": source["source_id"],
                "source_identity_sha256": source["source_identity_sha256"], "source_panel_id": panel_id,
                "source_panel_index": panel["panel_index"], "split": "train", "proposal_index": index,
                "proposal_variant": "unbounded-cluster-width", "proposal_center_original_xy": proposal["center"],
                "proposal_line_original_xyxy": proposal["line"], "kind": kind,
                "matching_state": state, "matching_reason": curation["matching_reason"],
                "trainable_single_box": curation["trainable_single_box"],
                "label_for_addition": strict_label(kind, state),
                "expected_tick_label": curation["expected_tick_label"],
                "exclusion_reasons": curation["exclusion_reasons"],
                "crop_transform": curation["crop_transform"], "targets": curation["targets"],
                "image_path": image_path, "image_sha256": image_sha,
                "annotation_path": annotation_path}
            ann_bytes = canonical(annotation)
            if len(image_bytes) > MAX_IMAGE_BYTES or len(ann_bytes) > MAX_ANNOTATION_BYTES:
                raise CurationError(f"candidate row exceeds bounded output: {case_id}")
            annotation["annotation_sha256"] = digest(ann_bytes)
            strict_candidate_count += int(strict_label(kind, state) is not None)
            if strict_label(kind, state) is not None:
                candidate_payloads[case_id] = (image_bytes, ann_bytes)
            candidate_rows.append({"case_id": case_id, "source_group_id": source["source_id"],
                "kind": kind, "matching_state": state, "image_sha256": image_sha,
                "annotation_sha256": annotation["annotation_sha256"], "image_path": image_path,
                "annotation_path": annotation_path})

        reconciled = reconcile_candidates(baseline_labels, candidate_rows)
        for row in reconciled["kept"]:
            image_bytes, ann_bytes = candidate_payloads[row["case_id"]]
            (output / row["image_path"]).write_bytes(image_bytes)
            (output / row["annotation_path"]).write_bytes(ann_bytes)
        new_counts = Counter(row["label"] for row in reconciled["kept"])
        dependency_hashes = {rel: sha for rel, sha in SOURCE_SHA256.items()}
        dependency_hashes["ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/curate_wide_train_only.py"] = EXECUTED_SCRIPT_SHA256
        dependency_hashes["ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/p3.json"] = cfg_sha
        dependency_hashes[P1_CONFIG_REL] = p1_config_sha
        dependency_hashes[P2_CONFIG_REL] = p2_config_sha
        dependency_hashes[P3_CONFIG_REL] = p3_sha
        dependency_hashes["artifacts/stroke-seeded-xtick-v1-train-verified-v6/manifest.json"] = v6_sha
        dependency_hashes["artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2/manifest.json"] = curation_manifest_sha
        manifest = assemble_manifest({"config": config, "config_sha256": cfg_sha,
            "p3_config_sha256": p3_sha, "v6_manifest_sha256": v6_sha,
            "curation_manifest_sha256": curation_manifest_sha, "dependency_hashes": dependency_hashes,
            "executed_script_sha256": EXECUTED_SCRIPT_SHA256,
            "runtime": {"python": sys.version, "numpy": np.__version__, "pillow": PILLOW_VERSION,
                "dotnet": dotnet_info.stdout.strip()}, "allowed_groups": allowed,
            "source_receipts": source_receipts, "v6_verified_counts": v6_verified_counts,
            "baseline_labels": baseline_labels, "baseline_groups": baseline_groups,
            "baseline_count": baseline_count, "wide_proposal_count": len(proposals),
            "panel_records": panel_records, "kind_counts": counts,
            "strict_candidate_count": strict_candidate_count, "reconciled": reconciled,
            "source_counts": source_counts, "scanner": scanner,
            "elapsed_seconds": time.perf_counter() - started})
        baseline_duplicate_count = manifest["curation"]["same_label_duplicates_of_frozen_p3_baseline"]
        candidate_duplicate_count = manifest["curation"]["same_hash_candidate_duplicate_occurrences"]
        (output / "manifest.json").write_bytes(canonical(manifest))
        return {"output": OUTPUT_REL, "scan_counts": dict(counts),
            "addition_counts": {str(k): v for k, v in sorted(new_counts.items())},
            "retained_unique": len(reconciled["kept"]),
            "quarantined": len(reconciled["conflicts_quarantined"]),
            "baseline_duplicates": baseline_duplicate_count,
            "candidate_duplicates": candidate_duplicate_count}
    except Exception as error:
        (output / "failure.json").write_bytes(canonical({"status": "failed", "phase": phase,
            "error_type": type(error).__name__, "error": str(error),
            "config_sha256": cfg_sha, "p3_config_sha256": p3_sha,
            "v6_manifest_sha256": v6_sha, "v2_curation_manifest_sha256": curation_manifest_sha,
            "training_performed": False, "optimizer_steps": 0,
            "prior_holdout_payloads_opened": 0, "probe_dev_sealed_private_inputs_read": False}))
        raise


if __name__ == "__main__":
    try:
        print(json.dumps(run(), sort_keys=True, indent=2))
    except Exception as error:
        failure_path = ROOT / OUTPUT_REL / "failure.json"
        if failure_path.parent.is_dir() and not failure_path.exists():
            failure_path.write_bytes(canonical({"status": "failed", "phase": RUN_STATE["phase"],
                "error_type": type(error).__name__, "error": str(error),
                "training_performed": False, "optimizer_steps": 0,
                "prior_holdout_payloads_opened": 0, "probe_dev_sealed_private_inputs_read": False}))
        raise
