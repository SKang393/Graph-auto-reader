# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Prepare a train-only seed-relative crop rule for recognizer diagnostics."""
from __future__ import annotations

from collections import defaultdict
from hashlib import sha256
import json
import os
from pathlib import Path
import stat
from typing import Any

ROOT = Path(__file__).resolve().parents[5]
MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2/manifest.json"
CURATION_ROOT_REL = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2"
MANIFEST_SHA256 = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
BASE_MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-train-verified-v6/manifest.json"
BASE_MANIFEST_SHA256 = "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a"
FIT_HOLDOUT_GROUPS = frozenset({
    "stroke-seeded-xtick-v1-train-27103105",
    "stroke-seeded-xtick-v1-train-27103106",
    "stroke-seeded-xtick-v1-train-27103110",
})
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-seed-crop-localization-train-v1"
MAX_BYTES = 2_000_000
PAD_PX = 2.0


class AdmissionError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def read_project(relative: str, expected_sha: str | None = None, max_bytes: int = MAX_BYTES) -> bytes:
    """Bounded project-root read; reject links/reparse points along the path."""
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts:
        raise AdmissionError("input path is not a project-relative path")
    path = ROOT
    for part in rel.parts:
        path = path / part
        try:
            reparse = bool(os.lstat(path).st_file_attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))
        except OSError as exc:
            raise AdmissionError("input path component is missing or unreadable") from exc
        if path.is_symlink() or getattr(path, "is_junction", lambda: False)() or reparse:
            raise AdmissionError("symlink/reparse component rejected")
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(ROOT.resolve()):
        raise AdmissionError("resolved input escaped project root")
    with resolved.open("rb") as stream:
        data = stream.read(max_bytes + 1)
    if len(data) > max_bytes:
        raise AdmissionError("input exceeded byte limit")
    if expected_sha is not None and digest(data) != expected_sha:
        raise AdmissionError("input SHA-256 differs from pinned manifest")
    return data


def _apply(matrix: list[float], x: float, y: float) -> tuple[float, float]:
    if len(matrix) != 6:
        raise AdmissionError("expected affine 2x3 mapping")
    return matrix[0] * x + matrix[1] * y + matrix[2], matrix[3] * x + matrix[4] * y + matrix[5]


def seed_relative_target(annotation: dict[str, Any]) -> tuple[float, float, float, float]:
    box = annotation["targets"][0]["target_crop_xyxy"]
    sx, sy = _apply(annotation["crop_transform"]["original_to_crop_pixel"], *annotation["proposal_center_original_xy"])
    return float(box[0]) - sx, float(box[1]) - sy, float(box[2]) - sx, float(box[3]) - sy


def derive_roi(fit_positive_boxes: list[tuple[float, float, float, float]], padding: float = PAD_PX) -> tuple[float, float, float, float]:
    if not fit_positive_boxes or padding < 0:
        raise AdmissionError("ROI requires positive fit boxes and nonnegative padding")
    return (min(box[0] for box in fit_positive_boxes) - padding,
            min(box[1] for box in fit_positive_boxes) - padding,
            max(box[2] for box in fit_positive_boxes) + padding,
            max(box[3] for box in fit_positive_boxes) + padding)


def _records_and_annotations(manifest: dict[str, Any]) -> list[tuple[dict[str, Any], dict[str, Any]]]:
    rows = []
    for record in manifest["records"]:
        if record.get("split") != "train":
            raise AdmissionError("non-train row found")
        if record.get("kind") not in {"positive", "background_only"}:
            continue  # Never open excluded annotation or image payloads.
        if record.get("trainable_single_box") is not True:
            raise AdmissionError("admitted row is not marked trainable_single_box")
        raw = read_project(f"{CURATION_ROOT_REL}/{record['annotation_path']}", record["annotation_sha256"])
        annotation = json.loads(raw)
        if annotation.get("split") != "train" or annotation.get("source_group_id") != record["source_group_id"]:
            raise AdmissionError("annotation split or group mismatch")
        if annotation.get("image_sha256") != record["image_sha256"] or annotation.get("kind") != record["kind"]:
            raise AdmissionError("annotation row identity mismatch")
        rows.append((record, annotation))
    return rows


def validate_hash_labels(rows: list[tuple[dict[str, Any], dict[str, Any]]]) -> dict[str, list[tuple[dict[str, Any], dict[str, Any]]]]:
    by_hash: dict[str, list[tuple[dict[str, Any], dict[str, Any]]]] = defaultdict(list)
    for row in rows:
        by_hash[row[0]["image_sha256"]].append(row)
    for image_hash, copies in by_hash.items():
        if len({record["kind"] for record, _ in copies}) != 1:
            raise AdmissionError(f"semantic label conflict for {image_hash}")
    return by_hash


def partition_hash_linked_groups(rows: list[tuple[dict[str, Any], dict[str, Any]]],
                                 holdout_groups: frozenset[str]) -> tuple[set[str], set[str], set[str], set[str]]:
    """Split whole source components linked by any admitted identical image hash."""
    by_hash = validate_hash_labels(rows)
    groups = {record["source_group_id"] for record, _ in rows}
    if not holdout_groups <= groups:
        raise AdmissionError("predeclared holdout group missing from trainable rows")
    parent = {group: group for group in groups}
    def find(x: str) -> str:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    def union(a: str, b: str) -> None:
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[rb] = ra
    for copies in by_hash.values():
        members = [record["source_group_id"] for record, _ in copies]
        for group in members[1:]:
            union(members[0], group)
    holdout_roots = {find(group) for group in holdout_groups}
    if len(holdout_roots) != len(holdout_groups):
        raise AdmissionError("predeclared holdout source groups are hash-linked")
    holdout = {group for group in groups if find(group) in holdout_roots}
    fit = groups - holdout
    fit_hashes = {record["image_sha256"] for record, _ in rows if record["source_group_id"] in fit}
    holdout_hashes = {record["image_sha256"] for record, _ in rows if record["source_group_id"] in holdout}
    if fit_hashes & holdout_hashes:
        raise AdmissionError("image hash leakage between fit and holdout")
    return fit, holdout, fit_hashes, holdout_hashes


def build_inputs() -> tuple[dict[str, Any], dict[str, Any]]:
    manifest_bytes = read_project(MANIFEST_REL, MANIFEST_SHA256, 20_000_000)
    base_bytes = read_project(BASE_MANIFEST_REL, BASE_MANIFEST_SHA256, 20_000_000)
    manifest = json.loads(manifest_bytes)
    base = json.loads(base_bytes)
    if manifest.get("split") != "train" or manifest.get("input_manifest_sha256") != BASE_MANIFEST_SHA256:
        raise AdmissionError("curation manifest does not bind the expected train-only source")
    if base.get("split") != "train" or len(manifest["records"]) != 896:
        raise AdmissionError("base/curated manifest split or row count changed")
    rows = _records_and_annotations(manifest)
    if len(rows) != 473:
        raise AdmissionError("trainable row count changed")

    validate_hash_labels(rows)
    fit_groups, holdout_groups, fit_hashes, holdout_hashes = partition_hash_linked_groups(rows, FIT_HOLDOUT_GROUPS)
    if fit_groups & FIT_HOLDOUT_GROUPS or len(fit_groups) != 18:
        raise AdmissionError("unexpected fit/holdout grouping")
    if holdout_groups != FIT_HOLDOUT_GROUPS:
        raise AdmissionError("holdout component includes an undeclared source group")

    fit_positive: dict[str, tuple[dict[str, Any], dict[str, Any]]] = {}
    for record, annotation in rows:
        if record["source_group_id"] not in fit_groups or record["kind"] != "positive":
            continue
        fit_positive.setdefault(record["image_sha256"], (record, annotation))
    offsets = [seed_relative_target(annotation) for _, annotation in fit_positive.values()]
    roi = derive_roi(offsets)
    holdout_rows = []
    holdout_truth = []
    for record, annotation in rows:
        if record["source_group_id"] not in holdout_groups:
            continue
        image_rel = f"{CURATION_ROOT_REL}/{record['image_path']}"
        image_raw = read_project(image_rel, record["image_sha256"], 1_000_000)
        target = annotation["targets"][0]["target_crop_xyxy"] if record["kind"] == "positive" else None
        seed_x, seed_y = _apply(annotation["crop_transform"]["original_to_crop_pixel"], *annotation["proposal_center_original_xy"])
        box = [seed_x + roi[0], seed_y + roi[1], seed_x + roi[2], seed_y + roi[3]]
        # Restrict only by the declared canvas. A boundary clip makes the row
        # non-comparable and is recorded rather than silently padded as ROI.
        clipped = box[0] < 0 or box[1] < 0 or box[2] > 256 or box[3] > 96
        holdout_rows.append({"case_id": record["case_id"], "source_group_id": record["source_group_id"],
                             "image_path": image_rel, "image_sha256": digest(image_raw),
                             "localized_roi_xyxy": box, "roi_clipped": clipped})
        holdout_truth.append({"case_id": record["case_id"], "kind": record["kind"],
                              "text": annotation.get("expected_tick_label") if target else None,
                              "target_crop_xyxy": target})
    input_doc = {"schema": "graphreader.seed-crop-localization-input.v1", "truth_read": False,
                 "train_only": True, "rows": holdout_rows}
    truth_doc = {"schema": "graphreader.seed-crop-localization-truth.v1", "rows": holdout_truth}
    meta = {"schema": "graphreader.seed-crop-localization-protocol.v1", "train_manifest_sha256": MANIFEST_SHA256,
            "base_manifest_sha256": BASE_MANIFEST_SHA256, "padding_px": PAD_PX,
            "rule": "axis-aligned union of unique fit-positive glyph boxes relative to affine-mapped truth-blind proposal seed, expanded 2 px",
            "fit_source_group_count": len(fit_groups), "holdout_source_groups": sorted(FIT_HOLDOUT_GROUPS),
            "fit_unique_positive_images": len(fit_positive), "roi_seed_relative_xyxy": list(roi),
            "input_rows": len(holdout_rows), "truth_rows": len(holdout_truth),
            "fit_unique_trainable_hashes": len(fit_hashes), "holdout_unique_trainable_hashes": len(holdout_hashes),
            "fit_holdout_hash_overlap": len(fit_hashes & holdout_hashes),
            "recognizer_rule": {"v5_confidence": 0.65, "v5_grammar": "^-?\\d+(?:\\.\\d+)?%?$",
                                "official_confidence": 0.0, "official_graph_number_grammar": "^-?\\d+(?:\\.\\d+)?%?$"}}
    return {"input": input_doc, "truth": truth_doc}, meta


def main() -> int:
    output = ROOT / OUTPUT_REL
    output.mkdir(parents=True, exist_ok=True)
    docs, meta = build_inputs()
    for name, doc in docs.items():
        path = output / f"{name}.json"
        if path.exists():
            raise AdmissionError(f"refusing to overwrite {path}")
        path.write_text(json.dumps(doc, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    meta["input_sha256"] = digest((output / "input.json").read_bytes())
    meta["truth_sha256"] = digest((output / "truth.json").read_bytes())
    meta_path = output / "protocol.json"
    if meta_path.exists():
        raise AdmissionError(f"refusing to overwrite {meta_path}")
    meta_path.write_text(json.dumps(meta, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: meta[k] for k in ("fit_source_group_count", "holdout_source_groups", "fit_unique_positive_images", "roi_seed_relative_xyxy", "input_rows", "fit_holdout_hash_overlap")}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
