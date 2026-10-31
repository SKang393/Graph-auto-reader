# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

from PIL import Image, ImageDraw, ImageFont
import pytest

from ml.ocr.synthetic_tick_corpus_v2 import prepare
from ml.synthetic.dataset import generate_dataset
from ml.synthetic.io import canonical_json_bytes, png_bytes
from ml.synthetic.renderer import _draw_text


def _fixture():
    image = Image.new("RGB", (40, 30), "white")
    ImageDraw.Draw(image).text((10, 16), "1", fill="black")
    scene = {"scene_id": "scene", "panels": [{"panel_id": "panel", "ticks": [
        {"axis": "x", "hidden": False, "label": "1", "value": 1}] }],
        "annotations": {"text_regions": [{"region_id": "tick", "panel_id": "panel", "text": "1", "role": "x_tick", "visible": True, "box": [10, 16, 5, 8]}]}}
    annotation = {"scene_id": "scene", "coordinate_space": "original_pixels", "canvas": {"width": 40, "height": 30},
                  "panels": [{"panel_id": "panel", "texts": [{"text_id": "tick", "region_id": "tick", "panel_id": "panel", "text": "1", "role": "x_tick", "visible": True, "coordinate_space": "original_pixels", "position": [10, 16], "rendered_pixel_box": [10, 16, 5, 8], "partial": False}]}]}
    return scene, annotation, image


def test_real_range_is_explicitly_public_train_validation_only():
    prepare._require_public_train_validation_only()


def test_x_tick_crop_requires_exact_numeric_truth():
    scene, annotation, image = _fixture()
    rows = prepare._x_tick_records(scene, annotation, image)
    assert [(row["text_id"], row["text"], row["crop_xyxy"]) for row in rows] == [("tick", "1", [10, 16, 15, 24])]
    annotation["panels"][0]["texts"][0]["text"] = "2"
    with pytest.raises(ValueError, match="source binding"):
        prepare._x_tick_records(scene, annotation, image)
    annotation["panels"][0]["texts"][0]["text"] = "1"
    annotation["panels"][0]["texts"][0]["partial"] = True
    with pytest.raises(ValueError, match="source binding"):
        prepare._x_tick_records(scene, annotation, image)


def test_renderer_glyph_contract_accepts_production_text_and_rejects_contrasting_block():
    font = ImageFont.truetype("arial.ttf", 16)
    font_path = Path(font.path)
    image = Image.new("RGB", (80, 60), "white")
    source = {"scene_id": "scene", "annotations": {"text_regions": [{
        "region_id": "tick", "panel_id": "panel", "text": "12", "role": "x_tick", "visible": True,
        "box": [20, 20, 20, 20],
    }]}}
    generated = _draw_text(image, ImageDraw.Draw(image), {
        "text": "12", "position": [20, 20], "anchor": "lt", "role": "x_tick", "visible": True,
        "region_id": "tick", "text_id": "tick",
    }, font=font, default_position=(20, 20), default_role="x_tick", default_id="tick")
    annotation = {"font": {"resolved_path": str(font_path), "size_px": 16,
                            "sha256": sha256(font_path.read_bytes()).hexdigest()}}
    raw_box = [int(value) for value in generated["rendered_pixel_box"]]
    box = [raw_box[0], raw_box[1], raw_box[0] + raw_box[2], raw_box[1] + raw_box[3]]
    record = {"region_id": "tick", "rendered_pixel_box": generated["rendered_pixel_box"], "crop_xyxy": box}
    glyph_hash, reason = prepare._glyph_visibility(source, annotation, image, record)
    assert glyph_hash and reason is None
    blocked = Image.new("RGB", image.size, "white")
    ImageDraw.Draw(blocked).rectangle(tuple(box), fill="black")
    glyph_hash, reason = prepare._glyph_visibility(source, annotation, blocked, record)
    assert glyph_hash is None
    assert reason == "renderer_glyph_alpha_not_visible_in_final_crop"


def test_public_seed_20261048_obscured_digit_one_is_excluded_with_its_actual_font_binding():
    root = prepare.ROOT / "artifacts" / "test-temp"
    root.mkdir(parents=True, exist_ok=True)
    scene_id = "703d46dd-b78a-5c56-8f78-05957023f873"
    text_id = "51abd431-9f84-58c3-bce6-d664665b932d"
    with TemporaryDirectory(dir=root) as temp:
        generated = Path(temp)
        generate_dataset("real_range", 20261048, generated)
        scene = json.loads((generated / "scenes" / f"{scene_id}.json").read_text(encoding="utf-8"))
        annotation = json.loads((generated / "annotations" / f"{scene_id}.json").read_text(encoding="utf-8"))
        image = Image.open(generated / "images" / f"{scene_id}.png").convert("RGB")
        record = next(row for row in prepare._x_tick_records(scene, annotation, image) if row["text_id"] == text_id)
        assert record["text"] == "1"
        assert record["crop_xyxy"] == [130, 381, 135, 393]
        font_path = Path(annotation["font"]["resolved_path"])
        assert sha256(font_path.read_bytes()).hexdigest() == annotation["font"]["sha256"]
        assert set(image.crop(tuple(record["crop_xyxy"])).get_flattened_data()) == {(0, 0, 0)}
        glyph_hash, reason = prepare._glyph_visibility(scene, annotation, image, record)
        assert glyph_hash is None
        assert reason == "renderer_glyph_alpha_not_visible_in_final_crop"


def test_per_case_font_sha_is_required_before_sample_binding():
    assert prepare._font_sha({"font": {"sha256": "a" * 64}}) == "a" * 64
    with pytest.raises(ValueError, match="font provenance"):
        prepare._font_sha({"font": {"sha256": "A" * 64}})
    with pytest.raises(ValueError, match="font provenance"):
        prepare._font_sha({"font": {}})


def test_public_fixture_materialization_binds_case_sample_font_and_split():
    root = prepare.ROOT / "artifacts" / "test-temp"
    root.mkdir(parents=True, exist_ok=True)

    def fake_generate(_preset, _seed, destination):
        destination = Path(destination)
        cases = []
        for index in range(10):
            split = "train" if index < 5 else "validation"
            scene_id, text = f"scene-{index}", str(index + 1)
            font_sha = "a" * 64 if split == "train" else "b" * 64
            families = {axis: f"{split}-{axis}" for axis in prepare.FAMILY_AXES}
            image = Image.new("RGB", (20, 20), (240 - index, 240, 240))
            ImageDraw.Draw(image).rectangle((2, 3, 5, 7), fill="black")
            scene = {"scene_id": scene_id, "panels": [{"panel_id": "panel", "ticks": [{"axis": "x", "hidden": False, "label": text, "value": index + 1}]}], "annotations": {"text_regions": [{"region_id": f"text-{index}", "panel_id": "panel", "text": text, "role": "x_tick", "visible": True, "box": [2, 3, 4, 5]}]}}
            annotation = {"scene_id": scene_id, "coordinate_space": "original_pixels", "canvas": {"width": 20, "height": 20}, "font": {"sha256": font_sha, "source": "system", "bundled": False}, "panels": [{"panel_id": "panel", "texts": [{"text_id": f"text-{index}", "region_id": f"text-{index}", "panel_id": "panel", "text": text, "role": "x_tick", "visible": True, "coordinate_space": "original_pixels", "position": [2, 3], "rendered_pixel_box": [1, 2, 6, 7], "partial": False}]}]}
            payloads = {f"images/{scene_id}.png": png_bytes(image), f"scenes/{scene_id}.json": canonical_json_bytes(scene), f"annotations/{scene_id}.json": canonical_json_bytes(annotation)}
            files = {}
            for relative, payload in payloads.items():
                path = destination / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(payload)
                files[relative] = sha256(payload).hexdigest()
            cases.append({"split": split, "scene_id": scene_id, "families": families, "files": files})
        (destination / "seed-manifest.json").write_bytes(canonical_json_bytes({"preset": prepare.PRESET, "dataset_seed": _seed, "sanity_passed": True, "cases": cases}))

    with TemporaryDirectory(dir=root) as temp:
        output = Path(temp) / "corpus"
        with mock.patch.object(prepare, "SEEDS", (777,)), mock.patch.object(prepare, "_preflight_binding", return_value={"path": "fixture", "sha256": "c" * 64}), mock.patch.object(prepare, "_require_public_train_validation_only"), mock.patch.object(prepare, "generate_dataset", fake_generate), mock.patch.object(prepare, "_glyph_visibility", return_value=("d" * 64, None)):
            result = prepare.materialize(output)
        report = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        assert result["train_samples"] == result["validation_samples"] == 5
        assert {case["font_sha256"] for case in report["source_cases"] if case["split"] == "train"} == {"a" * 64}
        assert {sample["font_sha256"] for sample in report["samples"] if sample["split"] == "validation"} == {"b" * 64}
        assert all((output / sample["crop"]).is_file() for sample in report["samples"])
        assert report["font_bytes_disjoint_across_splits"] is True
        assert report["font_license_review_status"] == "unreviewed_host_font_bytes_no_rights_asserted"
        assert report["full_gate_preflight"] == {"path": "fixture", "sha256": "c" * 64}
        excluded = Path(temp) / "all-excluded"
        def exclude_first_scene(_scene, _annotation, _image, record):
            return ((None, "renderer_glyph_alpha_not_visible_in_final_crop") if record["region_id"] == "text-0"
                    else ("d" * 64, None))
        with mock.patch.object(prepare, "SEEDS", (777,)), mock.patch.object(prepare, "_preflight_binding", return_value={"path": "fixture", "sha256": "c" * 64}), mock.patch.object(prepare, "_require_public_train_validation_only"), mock.patch.object(prepare, "generate_dataset", fake_generate), mock.patch.object(prepare, "_glyph_visibility", side_effect=exclude_first_scene):
            result = prepare.materialize(excluded)
        excluded_report = json.loads((excluded / "manifest.json").read_text(encoding="utf-8"))
        assert result["train_samples"] == 4 and result["validation_samples"] == 5
        excluded_case = next(case for case in excluded_report["source_cases"] if case["scene_id"] == "scene-0")
        assert excluded_case["included"] is False and excluded_case["readable_x_tick_count"] == 0
        assert excluded_report["excluded_x_tick_labels"] == {"total": 1, "by_reason": {"renderer_glyph_alpha_not_visible_in_final_crop": 1}, "scope": "aggregate only; excluded labels are not training samples"}
        assert excluded_report["excluded_source_scenes"] == {"total": 1, "by_reason": {"source_scene_no_readable_x_tick_glyphs": 1}, "scope": "aggregate only; source-scene exclusions do not increment label totals"}
        assert not [sample for sample in excluded_report["samples"] if sample["scene_id"] == "scene-0"]


def test_cross_split_family_image_and_crop_overlap_is_rejected():
    families = {split: {axis: {split + axis} for axis in prepare.FAMILY_AXES} for split in ("train", "validation")}
    rows = [
        {"split": "train", "sample_id": "a", "text_id": "ta", "source_image_sha256": "ia", "crop_sha256": "same"},
        {"split": "validation", "sample_id": "b", "text_id": "tb", "source_image_sha256": "ib", "crop_sha256": "same"},
    ]
    fonts = {"train": {"font-train"}, "validation": {"font-validation"}}
    with pytest.raises(ValueError, match="crop"):
        prepare._assert_isolation(rows, families, fonts)
    rows[1]["crop_sha256"] = "different"
    families["validation"]["font"] = families["train"]["font"]
    with pytest.raises(ValueError, match="font family"):
        prepare._assert_isolation(rows, families, fonts)
    families["validation"]["font"] = {"validation-font"}
    fonts["validation"] = {"font-train"}
    with pytest.raises(ValueError, match="font byte"):
        prepare._assert_isolation(rows, families, fonts)
