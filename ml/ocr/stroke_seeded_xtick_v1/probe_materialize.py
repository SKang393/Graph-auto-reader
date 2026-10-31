# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Materialize a source-disjoint, generated-only diagnostic from stroke crops."""

from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys
from typing import Any

from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as admission
from ml.ocr.stroke_seeded_xtick_v1 import materialize as crop_materializer


TRAIN_MANIFEST = "artifacts/stroke-seeded-xtick-v1-train-verified-v6/manifest.json"
TRAIN_MANIFEST_SHA256 = "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a"
OUTPUT = ROOT / "artifacts" / "stroke-seeded-xtick-v1-p2-probe-corpus-v3"
PROBE_PREFIX = "stroke-seeded-xtick-v1-probe-p2-"
PROBE_SEED_BASE = 27104100
MAX_PROBE_SOURCES = 20
MAX_PROBE_PANELS = 40
MAX_PROBE_CROPS = 700


class ProbeMaterializationError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def probe_recipes() -> tuple[dict[str, Any], ...]:
    """Deterministic recipes with fresh IDs/seeds and a different style mixture."""
    styles = (
        "hand_drawn", "scan_rough", "hand_drawn", "print_monochrome",
        "vector_clean", "hand_drawn", "scan_rough", "print_monochrome",
        "hand_drawn", "vector_clean", "hand_drawn", "scan_rough",
        "print_monochrome", "hand_drawn", "vector_clean", "hand_drawn",
        "scan_rough", "print_monochrome", "hand_drawn", "vector_clean",
        "hand_drawn", "scan_rough", "print_monochrome", "vector_clean",
    )
    designs = ("multiple_probe", "ab", "multiple_baseline", "abab", "aba")
    sessions = (6, 19, 8, 23, 12, 21, 7, 24, 10, 18, 15, 22, 9, 25, 11, 26, 14, 20, 16, 13)
    recipes = []
    for index in range(MAX_PROBE_SOURCES):
        seed = PROBE_SEED_BASE + index
        recipe = {
            "source_id": f"{PROBE_PREFIX}{seed}",
            "seed": seed,
            # The existing renderer's scene builder requires these internal values.
            # Stored/evaluated manifests mark this corpus diagnostic-only.
            "split": "train",
            "split_role": "train",
            "diagnostic_role": "fresh-source-disjoint-synthetic-probe",
            "design": designs[(index * 3) % len(designs)],
            "renderer_family": styles[index],
            "panel_count": 1 + (index % 2),
            "session_count": sessions[index],
            "canvas_width": 800 + 50 * (index % 12),
            "panel_height": 190 + 20 * (index % 8),
            "font_size_px": 8 + 2 * (index % 11),
            "x_label_visibility": ("visible", "visible", "partial", "hidden")[index % 4],
        }
        recipe["source_identity_sha256"] = digest(canonical_json(recipe))
        recipes.append(recipe)
    return tuple(recipes)


def _train_fingerprints() -> dict[str, set[str]]:
    bound = admission._bound_read(ROOT, TRAIN_MANIFEST, "pinned train-only V6 manifest", 5_000_000)
    if bound.sha256 != TRAIN_MANIFEST_SHA256:
        raise ProbeMaterializationError("V6 train manifest hash changed")
    manifest = admission._parse_json(bound, "pinned train-only V6 manifest")
    if (manifest.get("split") != "train" or len(manifest.get("sources", [])) != 24
            or len(manifest.get("records", [])) != 681):
        raise ProbeMaterializationError("V6 train manifest scope/counts changed")
    source_ids, source_identity_hashes, source_pixel_hashes = set(), set(), set()
    train_seeds = set()
    source_map = {}
    for source in manifest["sources"]:
        if source.get("split") != "train":
            raise ProbeMaterializationError("non-train V6 source in pinned manifest")
        recipe = source.get("recipe", {})
        source_id = source.get("source_id")
        source_ids.add(source_id)
        source_identity_hashes.add(source.get("source_identity_sha256"))
        train_seeds.add(recipe.get("seed"))
        source_map[source_id] = source.get("source_identity_sha256")
    image_hashes = set()
    train_rows_seen = set()
    corpus_root = (ROOT / Path(TRAIN_MANIFEST).parent).resolve(strict=True)
    for row in manifest["records"]:
        if row.get("split") != "train" or row.get("source_id") not in source_map:
            raise ProbeMaterializationError("non-train V6 row in pinned manifest")
        image_hashes.add(row.get("image_sha256"))
        train_rows_seen.add(row.get("case_id"))
        annotation_bound = admission._bound_read(
            corpus_root, row["annotation_path"], f"V6 train annotation {row['case_id']}", 65_536)
        if annotation_bound.sha256 != row.get("annotation_sha256"):
            raise ProbeMaterializationError("V6 annotation differs from the pinned train manifest")
        annotation = admission._parse_json(annotation_bound, "V6 train annotation")
        if (annotation.get("source_id") != row["source_id"]
                or annotation.get("source_identity_sha256") != source_map[row["source_id"]]):
            raise ProbeMaterializationError("V6 annotation source identity differs")
        source_pixel_hash = annotation.get("source_sha256")
        if not isinstance(source_pixel_hash, str) or len(source_pixel_hash) != 64:
            raise ProbeMaterializationError("V6 source pixel hash is malformed")
        source_pixel_hashes.add(source_pixel_hash)
    if len(train_rows_seen) != 681 or len(source_ids) != 24:
        raise ProbeMaterializationError("V6 train fingerprint inventory is incomplete")
    return {"source_ids": source_ids, "source_identity_hashes": source_identity_hashes,
            "source_pixel_hashes": source_pixel_hashes, "image_hashes": image_hashes,
            "seeds": train_seeds}


def verify_disjoint(train: dict[str, set[str]], probe_sources: list[dict[str, Any]],
                    probe_records: list[dict[str, Any]]) -> dict[str, Any]:
    source_ids = {str(item["source_id"]) for item in probe_sources}
    identity_hashes = {str(item["source_identity_sha256"]) for item in probe_sources}
    source_pixel_hashes = {str(item["source_pixel_sha256"]) for item in probe_sources}
    seeds = {str(item["seed"]) for item in probe_sources}
    image_hashes = {str(item["image_sha256"]) for item in probe_records}
    if len(source_ids) != len(probe_sources) or len(seeds) != len(probe_sources):
        raise ProbeMaterializationError("probe source IDs or seeds are not unique")
    overlaps = {
        "source_ids": source_ids & train["source_ids"],
        "source_identity_hashes": identity_hashes & train["source_identity_hashes"],
        "source_pixel_hashes": source_pixel_hashes & train["source_pixel_hashes"],
        "crop_image_hashes": image_hashes & train["image_hashes"],
    }
    if any(overlaps.values()) or any(not value.startswith(PROBE_PREFIX) for value in source_ids):
        raise ProbeMaterializationError("probe overlaps or reuses a V6 train source/image identity")
    try:
        numeric_train_seeds = {int(value) for value in train["seeds"]}
    except (TypeError, ValueError) as error:
        raise ProbeMaterializationError("V6 train source seed inventory is invalid") from error
    if seeds & {str(value) for value in numeric_train_seeds}:
        raise ProbeMaterializationError("probe seeds overlap the V6 train seed inventory")
    return {
        "compared_to_manifest_sha256": TRAIN_MANIFEST_SHA256,
        "training_source_count": len(train["source_ids"]),
        "probe_source_count": len(source_ids),
        "probe_seed_min": min(int(seed) for seed in seeds),
        "probe_seed_max": max(int(seed) for seed in seeds),
        "training_crop_image_hash_count": len(train["image_hashes"]),
        "probe_crop_image_hash_count": len(image_hashes),
        "overlap_counts": {key: len(value) for key, value in overlaps.items()},
    }


def materialize(output: Path = OUTPUT) -> dict[str, Any]:
    if output.exists() or output.is_symlink() or output.parent.resolve() != (ROOT / "artifacts").resolve():
        raise ProbeMaterializationError("probe corpus output must be a fresh direct child of project artifacts")
    admission._checked_chain(ROOT, output.parent, "probe corpus output parent")
    train = _train_fingerprints()
    recipes = probe_recipes()
    if len(recipes) != MAX_PROBE_SOURCES or any(r["seed"] in train["seeds"] for r in recipes):
        raise ProbeMaterializationError("probe recipe inventory overlaps train seeds")
    prepared: list[tuple[dict[str, Any], list[dict[str, Any]]]] = []
    rejected_recipes: list[dict[str, Any]] = []
    for recipe in recipes:
        try:
            source_rows = crop_materializer.materialize_recipe(recipe)
        except (crop_materializer.MaterializationError, ValueError) as error:
            rejected_recipes.append({"source_id": recipe["source_id"], "seed": recipe["seed"],
                                     "renderer_family": recipe["renderer_family"],
                                     "reason": str(error), "status": "excluded-before-materialization"})
            continue
        prepared.append((recipe, source_rows))
    if (len(prepared) < 12 or len({recipe["renderer_family"] for recipe, _ in prepared}) < 3
            or sum(int(recipe["panel_count"]) for recipe, _ in prepared) > MAX_PROBE_PANELS
            or sum(len(source_rows) for _, source_rows in prepared) > MAX_PROBE_CROPS):
        raise ProbeMaterializationError("admissible recipe diversity or corpus bounds are insufficient")
    output.mkdir(parents=True, exist_ok=False)
    (output / "images").mkdir()
    (output / "annotations").mkdir()
    sources: list[dict[str, Any]] = []
    rows: list[dict[str, Any]] = []
    duplicate_train_image_exclusions: list[dict[str, Any]] = []
    panel_total = 0
    for recipe, source_rows in prepared:
        panel_total += int(recipe["panel_count"])
        source_pixel_hashes = {row["source_sha256"] for row in source_rows}
        if len(source_pixel_hashes) != 1:
            raise ProbeMaterializationError("single source recipe produced inconsistent panel source hashes")
        sources.append({
            "source_id": recipe["source_id"], "source_identity_sha256": recipe["source_identity_sha256"],
            "source_pixel_sha256": next(iter(source_pixel_hashes)), "seed": recipe["seed"],
            "design": recipe["design"], "renderer_family": recipe["renderer_family"],
            "session_count": recipe["session_count"], "font_size_px": recipe["font_size_px"],
            "x_label_visibility": recipe["x_label_visibility"], "panel_count": recipe["panel_count"],
            "diagnostic_split": "fresh-source-disjoint-exploratory-probe",
            "renderer_recipe": recipe,
        })
        for row in source_rows:
            pixels = row.pop("crop_pixels")
            image = Image.frombytes("L", (crop_materializer.INPUT_WIDTH, crop_materializer.INPUT_HEIGHT), pixels)
            buffer = BytesIO()
            image.save(buffer, format="PNG", compress_level=9)
            image_bytes = buffer.getvalue()
            image_sha256 = digest(image_bytes)
            if image_sha256 in train["image_hashes"]:
                duplicate_train_image_exclusions.append({
                    "case_id": row["case_id"], "source_id": row["source_id"], "kind": row["kind"],
                    "reason": "rendered crop image bytes equal a pinned V6 training image; omitted from probe records",
                })
                continue
            row["split"] = "diagnostic"
            row["image_path"] = f"images/{row['case_id']}.png"
            row["image_sha256"] = image_sha256
            row["annotation_path"] = f"annotations/{row['case_id']}.json"
            annotation_bytes = canonical_json(row)
            row["annotation_sha256"] = digest(annotation_bytes)
            (output / row["image_path"]).write_bytes(image_bytes)
            (output / row["annotation_path"]).write_bytes(annotation_bytes)
            rows.append({key: row[key] for key in (
                "case_id", "source_id", "source_identity_sha256", "source_sha256", "split", "kind",
                "trainable_single_box", "image_path", "image_sha256", "annotation_path", "annotation_sha256")})
            if len(rows) > MAX_PROBE_CROPS:
                raise ProbeMaterializationError("diagnostic crop bound exceeded")
    if panel_total > MAX_PROBE_PANELS:
        raise ProbeMaterializationError("diagnostic panel bound exceeded")
    separation = verify_disjoint(train, sources, rows)
    kinds = {kind: sum(row["kind"] == kind for row in rows)
             for kind in ("positive", "background_only", "excluded")}
    manifest = {
        "schema": "graphreader.stroke-seeded-xtick-v1-source-disjoint-diagnostic.v1",
        "family": "stroke-seeded-xtick-v1-p2-source-disjoint-synthetic-diagnostic",
        "split": "exploratory-synthetic-diagnostic", "generator_sha256": digest(Path(__file__).read_bytes()),
        "crop_materializer_sha256": digest(Path(crop_materializer.__file__).read_bytes()),
        "record_count": len(rows), "source_count": len(sources), "panel_count": sum(s["panel_count"] for s in sources),
        "candidate_source_count": len(recipes), "excluded_source_recipes": rejected_recipes,
        "candidate_crop_count_before_duplicate_exclusion": len(rows) + len(duplicate_train_image_exclusions),
        "duplicate_train_image_exclusion_count": len(duplicate_train_image_exclusions),
        "duplicate_train_image_exclusions": duplicate_train_image_exclusions,
        "positive_count": kinds["positive"], "background_only_count": kinds["background_only"],
        "excluded_count": kinds["excluded"],
        "trainable_single_box_positive_count": sum(r["kind"] == "positive" and r["trainable_single_box"] for r in rows),
        "trainable_single_box_background_count": sum(r["kind"] == "background_only" and r["trainable_single_box"] for r in rows),
        "probe_dev_sealed_private_inputs_read": False,
        "source_distribution": {key: {value: sum(s[key] == value for s in sources)
                                       for value in sorted({s[key] for s in sources})}
                                 for key in ("renderer_family", "design", "session_count", "font_size_px", "x_label_visibility")},
        "train_separation": separation,
        "sources": sources, "records": rows,
        "crop_contract": "V1 stroke-seeded fixed window with alpha-mask glyph bounds and reversible affine mapping",
        "acceptance_status": "exploratory diagnostic only; not formal acceptance",
    }
    manifest_bytes = canonical_json(manifest)
    (output / "manifest.json").write_bytes(manifest_bytes)
    verify_probe(output, digest(manifest_bytes))
    return {"output": output.relative_to(ROOT).as_posix(), "manifest_sha256": digest(manifest_bytes),
            "sources": len(sources), "panels": sum(s["panel_count"] for s in sources), "crops": len(rows),
            "counts": kinds, "trainable_single_box_positive": manifest["trainable_single_box_positive_count"],
            "trainable_single_box_background": manifest["trainable_single_box_background_count"],
            "separation": separation}


def verify_probe(output: Path = OUTPUT, expected_manifest_sha256: str | None = None) -> dict[str, Any]:
    resolved = output.resolve(strict=True)
    if not resolved.is_relative_to((ROOT / "artifacts").resolve()) or output.is_symlink():
        raise ProbeMaterializationError("diagnostic corpus escapes artifacts or is a symlink")
    manifest_bound = admission._bound_read(ROOT, (resolved / "manifest.json").relative_to(ROOT).as_posix(),
                                           "source-disjoint diagnostic manifest", 5_000_000)
    if expected_manifest_sha256 and manifest_bound.sha256 != expected_manifest_sha256:
        raise ProbeMaterializationError("diagnostic manifest hash differs from expected bytes")
    manifest = admission._parse_json(manifest_bound, "source-disjoint diagnostic manifest")
    if (manifest.get("schema") != "graphreader.stroke-seeded-xtick-v1-source-disjoint-diagnostic.v1"
            or manifest.get("split") != "exploratory-synthetic-diagnostic"
            or manifest.get("probe_dev_sealed_private_inputs_read") is not False
            or not 12 <= len(manifest.get("sources", [])) <= MAX_PROBE_SOURCES
            or manifest.get("candidate_source_count") != MAX_PROBE_SOURCES
            or len(manifest.get("excluded_source_recipes", [])) + len(manifest.get("sources", [])) != MAX_PROBE_SOURCES
            or manifest.get("duplicate_train_image_exclusion_count") != len(manifest.get("duplicate_train_image_exclusions", []))
            or manifest.get("record_count") != len(manifest.get("records", []))):
        raise ProbeMaterializationError("diagnostic manifest scope/inventory is malformed")
    source_by_id: dict[str, dict[str, Any]] = {}
    for source in manifest["sources"]:
        recipe = source.get("renderer_recipe")
        if not isinstance(recipe, dict):
            raise ProbeMaterializationError("diagnostic source recipe is malformed")
        identity_payload = dict(recipe)
        identity_payload.pop("source_identity_sha256", None)
        if (digest(canonical_json(identity_payload)) != source.get("source_identity_sha256")
                or recipe.get("source_id") != source.get("source_id")
                or recipe.get("seed") != source.get("seed")
                or source.get("diagnostic_split") != "fresh-source-disjoint-exploratory-probe"
                or source.get("source_id") in source_by_id):
            raise ProbeMaterializationError("diagnostic source ID/seed/recipe identity differs")
        source_by_id[source["source_id"]] = source
    counts: dict[str, int] = {}
    for row in manifest["records"]:
        if (row.get("split") != "diagnostic" or row.get("source_id") not in source_by_id
                or row.get("source_identity_sha256") != source_by_id[row["source_id"]]["source_identity_sha256"]
                or row.get("source_sha256") != source_by_id[row["source_id"]]["source_pixel_sha256"]):
            raise ProbeMaterializationError("probe row is not marked diagnostic")
        counts[row["kind"]] = counts.get(row["kind"], 0) + 1
        for key, size_limit in (("image_path", 1_000_000), ("annotation_path", 65_536)):
            bound = admission._bound_read(resolved, row[key], f"probe {key} {row['case_id']}", size_limit)
            if bound.sha256 != row["image_sha256" if key == "image_path" else "annotation_sha256"]:
                raise ProbeMaterializationError("probe artifact hash differs from manifest")
            if key == "image_path":
                with Image.open(BytesIO(bound.value)) as image:
                    if image.mode != "L" or image.size != (256, 96):
                        raise ProbeMaterializationError("probe crop shape/mode differs")
            else:
                annotation = admission._parse_json(bound, "probe crop annotation")
                if (annotation.get("case_id") != row["case_id"] or annotation.get("source_id") != row["source_id"]
                        or annotation.get("source_identity_sha256") != row["source_identity_sha256"]
                        or annotation.get("source_sha256") != row["source_sha256"]
                        or annotation.get("split") != "diagnostic" or annotation.get("kind") != row["kind"]):
                    raise ProbeMaterializationError("probe annotation identity/split differs")
                targets = annotation.get("targets")
                if not isinstance(targets, list):
                    raise ProbeMaterializationError("probe target list is malformed")
                if row["kind"] == "excluded":
                    if row.get("trainable_single_box") or not annotation.get("exclusion_reasons"):
                        raise ProbeMaterializationError("excluded probe crop lacks explicit exclusion")
                elif row["kind"] == "positive":
                    if not row.get("trainable_single_box") or len(targets) != 1 or targets[0].get("text") != annotation.get("expected_tick_label"):
                        raise ProbeMaterializationError("positive probe crop is not one complete seeded target")
                    glyph = targets[0]["glyph_mask_xyxy"]
                    target = targets[0]["target_crop_xyxy"]
                    transform = annotation["crop_transform"]
                    forward, inverse = transform["original_to_crop_pixel"], transform["crop_pixel_to_original"]
                    mapped = (glyph[0] * forward[0] + forward[2], glyph[1] * forward[4] + forward[5],
                              glyph[2] * forward[0] + forward[2], glyph[3] * forward[4] + forward[5])
                    restored = (target[0] * inverse[0] + inverse[2], target[1] * inverse[4] + inverse[5],
                                target[2] * inverse[0] + inverse[2], target[3] * inverse[4] + inverse[5])
                    if any(abs(x - y) > 1e-6 for x, y in zip(mapped, target)) or any(abs(x - y) > 1e-6 for x, y in zip(restored, glyph)):
                        raise ProbeMaterializationError("probe target box does not follow reversible glyph-mask mapping")
                elif row["kind"] == "background_only":
                    if not row.get("trainable_single_box") or targets or annotation.get("expected_tick_label") is not None:
                        raise ProbeMaterializationError("background probe crop contains/claims glyph targets")
                else:
                    raise ProbeMaterializationError("unknown probe crop class")
    train = _train_fingerprints()
    separation = verify_disjoint(train, manifest["sources"], manifest["records"])
    return {"manifest_sha256": manifest_bound.sha256, "sources": len(manifest["sources"]),
            "crops": len(manifest["records"]), "counts": counts, "separation": separation}


if __name__ == "__main__":
    result = materialize()
    print(json.dumps(result, sort_keys=True, indent=2))
