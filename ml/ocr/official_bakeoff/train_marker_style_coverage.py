# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Prepare train-only nontext styles; no models, capture, or optimizer is run.

python -m ml.ocr.official_bakeoff.train_marker_style_coverage --output artifacts/ocr-marker-style-train-v1
The image manifest uses the existing synthetic runtime capture input format.
Separate source-space OCR truth passes the shared row reader. Model-grid targets
and trainer input still require actual application crop/tensor capture; the
historical 20-source/709-truth V44/V46 population contract is unchanged.
Historical fixtures and development inputs are neither read nor replaced.
"""
from __future__ import annotations

import argparse
from hashlib import sha256
from pathlib import Path

import numpy as np

from ml.synthetic.contextual_negatives import render_contextual_scene
from ml.synthetic.dataset import CaseSpec, _build_scenes, _scene_split
from ml.synthetic.io import canonical_json_bytes, png_bytes
from ml.synthetic.templates import FILL_STATES, MARKER_SHAPES
from .db_inverse_targets import build_inverse_targets
from .text_extent_head_inputs import _read_train_truth

STYLE_SCHEDULE = tuple((shape, fill) for shape in MARKER_SHAPES for fill in FILL_STATES)
SERIES_PER_SOURCE = 3
DEFAULT_SEED = 20261112
SPEC = CaseSpec("alternating_treatments", "vector_clean", 1, 12)


def build_train_scenes(seed: int = DEFAULT_SEED) -> list[dict]:
    if isinstance(seed, bool) or not isinstance(seed, int) or seed < 0:
        raise ValueError("seed must be a non-negative integer")
    scenes = []
    observed = []
    for ordinal, offset in enumerate(range(0, len(STYLE_SCHEDULE), SERIES_PER_SOURCE)):
        scene = _build_scenes([SPEC], seed + ordinal, require_complete_style_catalog=False,
                              marker_style_offset=offset)[0]
        if _scene_split(scene) != "train":
            raise ValueError("Supplemental scene escaped the train families")
        styles = [(s["shape"], s["fill"]) for p in scene["panels"] for s in p["series"]]
        if styles != list(STYLE_SCHEDULE[offset:offset + SERIES_PER_SOURCE]):
            raise ValueError("Supplemental marker-style schedule changed")
        observed.extend(styles)
        scenes.append(scene)
    if observed != list(STYLE_SCHEDULE):
        raise ValueError("Supplemental styles must cover the complete intended catalog once")
    return scenes


def source_truths(annotation: dict) -> list[dict]:
    return [t for p in annotation["panels"] for t in p["texts"]
            if t.get("visible", True) and t.get("text", "").strip() and t.get("rendered_pixel_box")]


def source_targets(annotation: dict, width: int, height: int) -> tuple[np.ndarray, np.ndarray]:
    boxes = []
    for text in source_truths(annotation):
        x, y, w, h = text["rendered_pixel_box"]
        boxes.append((x, y, x + w, y + h))
    target = build_inverse_targets(width, height, boxes).target
    # Complete source images have no clipped-word ignore regions.
    mask = np.ones_like(target)
    mask.setflags(write=False)
    return target, mask


def verify_background(annotation: dict, width: int, height: int) -> dict[str, int]:
    target, mask = source_targets(annotation, width, height)
    texts = [t["rendered_pixel_box"] for t in source_truths(annotation)]
    counts: dict[str, int] = {}
    for panel in annotation["panels"]:
        symbols = [(m["shape"], m["fill"], m["center"]) for m in panel["markers"]]
        symbols += [(e["shape"], e["fill"], e["glyph_center"])
                    for legend in panel["legends"] for e in legend["entries"]]
        for shape, fill, (x, y) in symbols:
            if any(left <= x <= left + w and top <= y <= top + h for left, top, w, h in texts):
                continue
            if not (0 <= x < width and 0 <= y < height) or target[0, int(y), int(x)] != 0 or mask[0, int(y), int(x)] != 1:
                raise ValueError("Nontext symbol center has invalid OCR background supervision")
            key = f"{shape}/{fill}"
            counts[key] = counts.get(key, 0) + 1
    return counts


def validate_source_truths(document: dict, images: list[dict]) -> None:
    """Read supplemental rows without changing historical population expectations."""
    records = document["truths"]
    if (document.get("schema") != "graphreader.ocr-train-text-extent-truth.v1" or
        document.get("source_count") != len(images) or document.get("truth_count") != len(records) or
        document.get("split") != "train" or document.get("synthetic_only") is not True or
        document.get("private_data") is not False or document.get("sealed_data") is not False or
        document.get("coordinate_space") != "original_pixels"):
        raise ValueError("Supplemental source truth scope or counts changed")
    sources = {row["image_sha256"]: row for row in images}
    seen, text_ids, observed = set(), set(), set()
    for raw in records:
        _, source, ident, text_id, _ = _read_train_truth(raw, sources)
        if ident in seen or (source, text_id) in text_ids:
            raise ValueError("Supplemental source truth identity repeats")
        seen.add(ident)
        text_ids.add((source, text_id))
        observed.add(source)
    if len(sources) != len(images) or observed != set(sources):
        raise ValueError("Supplemental source truth inventory changed")


def prepare(output: Path, *, root: Path | None = None, seed: int = DEFAULT_SEED) -> dict:
    root = (root or Path(__file__).resolve().parents[3]).resolve()
    output = output.resolve()
    if not output.is_relative_to(root / "artifacts") or output.exists():
        raise ValueError("Use a new directory inside this checkout's artifacts")
    payloads, sources, images, truths = {}, [], [], []
    background_counts: dict[str, int] = {}
    for index, scene in enumerate(build_train_scenes(seed)):
        image, annotation, _ = render_contextual_scene(scene, split="train", source_profile="visible-content-v3")
        for style, count in verify_background(annotation, image.width, image.height).items():
            background_counts[style] = background_counts.get(style, 0) + count
        name = f"source-{index:02d}"
        files = {f"{name}.png": png_bytes(image), f"{name}.scene.json": canonical_json_bytes(scene),
                 f"{name}.annotation.json": canonical_json_bytes(annotation)}
        payloads.update(files)
        image_hash = sha256(files[f"{name}.png"]).hexdigest()
        refs = {kind: {"path": (output / file).relative_to(root).as_posix(), "sha256": sha256(files[file]).hexdigest()}
                for kind, file in (("image", f"{name}.png"), ("scene", f"{name}.scene.json"),
                                   ("annotation", f"{name}.annotation.json"))}
        sources.append({"split": "train", "seed": scene["seed"], **refs})
        family = "|".join(f"{axis}={scene['families'][axis]['key']}"
                          for axis in ("renderer", "font", "degradation", "template", "marker"))
        images.append({"image": f"{name}.png", "image_sha256": image_hash, "width": image.width,
                       "height": image.height, "split": "train", "family": family, "seed": scene["seed"]})
        for text in source_truths(annotation):
            x, y, w, h = text["rendered_pixel_box"]
            truths.append({"truth_id": sha256((image_hash + "\n" + text["text_id"]).encode()).hexdigest(),
                           "source_sha256": image_hash, "source_id": image_hash,
                           "text_id": text["text_id"], "region_id": text["region_id"],
                           "source_box_ltrb": [x, y, x + w, y + h],
                           "text": text["text"], "role": text["role"], "coordinate_space": "original_pixels"})
    if set(background_counts) != {f"{shape}/{fill}" for shape, fill in STYLE_SCHEDULE}:
        raise ValueError("Every intended style needs checked nontext background supervision")
    payloads["input-manifest.json"] = canonical_json_bytes({
        "schema": "graphreader.synthetic-runtime-raster-inputs.v1",
        "source": "project-owned-synthetic-five-axis-family-v1", "preset": "ocr-marker-style-train-v1",
        "seed": seed, "split": "train", "contains_truth": False, "contains_precomputed_masks": False, "images": images})
    truth_document = {
        "schema": "graphreader.ocr-train-text-extent-truth.v1", "synthetic_only": True,
        "private_data": False, "sealed_data": False, "split": "train", "coordinate_space": "original_pixels",
        "source_count": len(sources), "truth_count": len(truths), "truths": truths}
    validate_source_truths(truth_document, images)
    payloads["train-text-truth.json"] = canonical_json_bytes(truth_document)
    report = {"schema": "graphreader.ocr-marker-style-train-fixture.v1", "scope": "owned-synthetic-train-only",
              "sources": sources, "style_schedule": STYLE_SCHEDULE, "optimizer_steps": 0, "sealed_reads": 0,
              "background_checks_by_style": background_counts,
              "private_reads": 0, "model_inference_runs": 0, "production_approved": False}
    payloads["generator.json"] = canonical_json_bytes(report)
    output.mkdir(parents=True)
    for name, payload in payloads.items():
        with (output / name).open("xb") as stream:
            stream.write(payload)
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    args = parser.parse_args()
    report = prepare(args.output, seed=args.seed)
    print(f"Prepared {len(report['sources'])} train-only sources; optimizer steps=0, sealed reads=0")


if __name__ == "__main__":
    main()
