# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train fixture coverage and existing consumer formats, without model execution."""
from hashlib import sha256
import json

import numpy as np
from PIL import Image
import pytest

from ml.ocr.official_bakeoff import train_marker_style_coverage as subject
from ml.ocr.official_bakeoff import text_extent_head_inputs as consumer
from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.io import canonical_json_bytes, png_bytes


def test_schedule_covers_all_styles_across_distinct_seed_calls():
    scenes = subject.build_train_scenes()
    assert len(scenes) == 9
    assert [int(s["seed"]) for s in scenes] == [(subject.DEFAULT_SEED + i) * 100 for i in range(9)]
    assert all(_scene_split(s) == "train" for s in scenes)
    actual = [(v["shape"], v["fill"]) for s in scenes for p in s["panels"] for v in p["series"]]
    assert actual == list(subject.STYLE_SCHEDULE)
    assert canonical_json_bytes(scenes) == canonical_json_bytes(subject.build_train_scenes())


def test_existing_default_schedule_is_unchanged():
    specs = PRESETS["smoke"][:4]
    old_default = _build_scenes(specs, 393, require_complete_style_catalog=False)
    explicit_zero = _build_scenes(specs, 393, require_complete_style_catalog=False, marker_style_offset=0)
    assert canonical_json_bytes(old_default) == canonical_json_bytes(explicit_zero)
    assert {v["shape"] for s in old_default for p in s["panels"] for v in p["series"]} == {"circle", "square"}


@pytest.mark.parametrize("offset", [-1, True, 1.5])
def test_invalid_offsets_are_rejected(offset):
    with pytest.raises(ValueError, match="marker_style_offset"):
        _build_scenes([subject.SPEC], 1, marker_style_offset=offset)


def test_offset_cannot_bypass_existing_complete_catalog_guard():
    with pytest.raises(AssertionError, match="3 series"):
        _build_scenes([subject.SPEC], 1, marker_style_offset=27)


def test_fixture_is_usable_train_only_and_background_supervision_is_complete(tmp_path):
    output = tmp_path / "artifacts" / "supplement"
    # Sentinels ensure the entry point never opens or rewrites historical inputs.
    historical = tmp_path / "artifacts" / "historical-dev"
    historical.mkdir(parents=True)
    sentinel = historical / "unchanged.bin"
    sentinel.write_bytes(b"historical fixture remains unchanged")
    report = subject.prepare(output, root=tmp_path)
    manifest = json.loads((output / "input-manifest.json").read_text())
    truth = json.loads((output / "train-text-truth.json").read_text())
    assert manifest["schema"] == "graphreader.synthetic-runtime-raster-inputs.v1"
    assert manifest["source"] == "project-owned-synthetic-five-axis-family-v1"
    assert manifest["split"] == "train" and len(manifest["images"]) == 9
    assert manifest["contains_truth"] is manifest["contains_precomputed_masks"] is False
    assert truth["schema"] == "graphreader.ocr-train-text-extent-truth.v1"
    assert truth["split"] == "train" and truth["synthetic_only"] is True
    assert truth["private_data"] is truth["sealed_data"] is False
    assert len(truth["truths"]) == truth["truth_count"] > 0
    assert len({t["truth_id"] for t in truth["truths"]}) == truth["truth_count"]
    subject.validate_source_truths(truth, manifest["images"])
    assert consumer.EXPECTED_TRAIN_SOURCES == 20 and consumer.EXPECTED_TRAIN_TRUTHS == 709
    with pytest.raises(consumer.TextExtentHeadInputError, match="scope or denominators"):
        consumer._build_train_split(truth, {}, ())
    assert set(report["background_checks_by_style"]) == {f"{s}/{f}" for s, f in subject.STYLE_SCHEDULE}
    rendered_ink_shapes = set()
    for source, entry in zip(report["sources"], manifest["images"], strict=True):
        for ref in (source["image"], source["scene"], source["annotation"]):
            assert sha256((tmp_path / ref["path"]).read_bytes()).hexdigest() == ref["sha256"]
        annotation = json.loads((tmp_path / source["annotation"]["path"]).read_text())
        target, mask = subject.source_targets(annotation, entry["width"], entry["height"])
        assert np.isfinite(target).all() and set(np.unique(target)) == {0, 1}
        assert np.all(mask == 1) and not target.flags.writeable and not mask.flags.writeable
        checked = subject.verify_background(annotation, entry["width"], entry["height"])
        assert checked and all(count > 0 for count in checked.values())
        assert (output / entry["image"]).name == entry["image"]
        assert sha256((output / entry["image"]).read_bytes()).hexdigest() == entry["image_sha256"]
        with Image.open(output / entry["image"]) as image:
            pixels = image.convert("RGB")
            text_boxes = [t["rendered_pixel_box"] for t in subject.source_truths(annotation)]
            for panel in annotation["panels"]:
                for marker in panel["markers"]:
                    x, y = marker["center"]
                    if any(a <= x <= a + w and b <= y <= b + h for a, b, w, h in text_boxes):
                        continue
                    if pixels.getpixel((int(x), int(y))) != (255, 255, 255):
                        assert target[0, int(y), int(x)] == 0 and mask[0, int(y), int(x)] == 1
                        rendered_ink_shapes.add(marker["shape"])
    assert {"diamond", "star", "asterisk", "cross"} <= rendered_ink_shapes
    first, _, _ = subject.render_contextual_scene(subject.build_train_scenes()[0], split="train", source_profile="visible-content-v3")
    assert png_bytes(first) == (output / manifest["images"][0]["image"]).read_bytes()
    assert sentinel.read_bytes() == b"historical fixture remains unchanged"
    assert report["optimizer_steps"] == report["model_inference_runs"] == report["sealed_reads"] == 0
    with pytest.raises(ValueError, match="new directory"):
        subject.prepare(output, root=tmp_path)
    with pytest.raises(ValueError, match="inside"):
        subject.prepare(tmp_path / "outside-artifacts", root=tmp_path)


@pytest.mark.parametrize("field,value", [
    ("source_id", None), ("source_id", "b" * 64),
    ("text_id", None), ("text_id", ""),
    ("region_id", None), ("region_id", ""),
    ("truth_id", None), ("truth_id", "bad"),
])
def test_supplemental_and_historical_callers_use_real_identifier_validation(field, value):
    source = "a" * 64
    row = {"source_sha256": source, "source_id": source, "truth_id": "c" * 64,
           "text_id": "text-1", "region_id": "region-1", "text": "Baseline", "role": "phase_heading",
           "coordinate_space": "original_pixels", "source_box_ltrb": [2, 2, 18, 14]}
    row[field] = value
    images = [{"image_sha256": source, "width": 20, "height": 20}]
    document = {"schema": consumer.TRAIN_TRUTH_SCHEMA, "source_count": 1, "truth_count": 1,
                "split": "train", "synthetic_only": True, "private_data": False, "sealed_data": False,
                "coordinate_space": "original_pixels", "truths": [row]}
    reason = {"source_id": "source identity", "text_id": "text_id",
              "region_id": "region_id", "truth_id": "truth identity"}[field]
    with pytest.raises(consumer.TextExtentHeadInputError, match=reason):
        subject.validate_source_truths(document, images)
    # Deliberately invalid historical input reaches the same row reader without
    # overriding the frozen population constants or constructing training data.
    historical = {**document, "source_count": 20, "truth_count": 709, "truths": [row] * 709}
    with pytest.raises(consumer.TextExtentHeadInputError, match=reason):
        consumer._build_train_split(historical, {source: images[0]}, ())
