# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import json
from pathlib import Path

import pytest

from ml.ocr.synthetic_tick_family_v1 import prepare


def fixture(root: Path, monkeypatch) -> Path:
    data = root / "dataset"
    for directory in ("images", "annotations", "scenes", "masks", "tables", "splits"):
        (data / directory).mkdir(parents=True)
    cases, scenes, artifacts = [], [], {}
    for index in range(10):
        split = "train" if index < 4 else "validation"
        scene_id = f"scene-{index}"
        font = root / f"{split}.font"
        font.write_bytes(split.encode())
        families = {axis: split for axis in prepare.FAMILY_AXES}
        scene = {"scene_id": scene_id, "seed": index, "design": "ab", "families": {
            axis: {"key": split, "split": split} for axis in prepare.FAMILY_AXES},
            "annotations": {"text_regions": [{"region_id": scene_id, "panel_id": scene_id,
                "role": "x_tick", "visible": True, "text": "1", "box": [10, 36, 20, 15]}]},
            "panels": [{"panel_id": scene_id, "ticks": [{"tick_id": scene_id, "axis": "x", "hidden": False,
                "value": 1, "center": [20, 30]}]}]}
        scenes.append(scene)
        annotation = {"scene_id": scene_id, "coordinate_space": "original_pixels", "canvas": {"width": 100, "height": 100},
            "font": {"resolved_path": str(font), "sha256": prepare.digest(font)},
            "panels": [{"panel_id": scene_id, "ticks": [{"tick_id": scene_id, "axis": "x", "panel_id": scene_id,
                "visible": True, "value": 1, "center": [20, 30]}],
                "texts": [{"text_id": scene_id, "region_id": scene_id, "panel_id": scene_id, "coordinate_space": "original_pixels",
                           "role": "x_tick", "text": "1", "visible": True,
                           "rendered_pixel_box": [10, 36, 3, 4], "position": [10, 36]}]}],
            "hard_negatives": [{"kind": "marker_like_letter"}]}
        files = {}
        for directory, suffix, contents in (("images", "png", scene_id.encode()),
                ("scenes", "json", json.dumps(scene).encode()),
                ("annotations", "json", json.dumps(annotation).encode()),
                ("masks", "png", b"mask"), ("tables", "csv", b"table")):
            relative = f"{directory}/{scene_id}.{suffix}"
            (data / relative).write_bytes(contents)
            files[relative] = artifacts[relative] = prepare.digest(data / relative)
        cases.append({"case_index": index, "split": split, "scene_id": scene_id, "seed": index, "design": "ab",
                      "families": families, "files": files})
    for relative in ("contact-sheet.png", "distribution-report.json", "sanity-report.json"):
        (data / relative).write_bytes(b"fixture")
        artifacts[relative] = prepare.digest(data / relative)
    for split, value in prepare._split_manifests(cases).items():
        relative = f"splits/{split}.json"
        (data / relative).write_text(json.dumps(value), encoding="utf-8")
        artifacts[relative] = prepare.digest(data / relative)
    (data / "seed-manifest.json").write_text(json.dumps({"preset": "real_range", "dataset_seed": prepare.SEED,
        "sanity_passed": True, "cases": cases, "artifact_sha256": artifacts}), encoding="utf-8")
    monkeypatch.setattr(prepare, "_fixed_scenes", lambda: scenes)
    return data


def rewrite_annotation(data, mutate):
    path = data / "annotations/scene-0.json"
    value = json.loads(path.read_text(encoding="utf-8"))
    mutate(value)
    path.write_text(json.dumps(value), encoding="utf-8")
    manifest_path = data / "seed-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["cases"][0]["files"]["annotations/scene-0.json"] = prepare.digest(path)
    manifest["artifact_sha256"]["annotations/scene-0.json"] = prepare.digest(path)
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")


def test_manifest_preserves_family_isolation_and_avoids_fabricated_negative_labels(tmp_path, monkeypatch):
    result = prepare.summarize(fixture(tmp_path, monkeypatch))
    assert result["splits"]["train"]["visible_text_roles"]["x_tick"] == 4
    assert result["splits"]["validation"]["visible_text_roles"]["x_tick"] == 6
    assert result["splits"]["validation"]["marker_artifact_candidates"]["marker_like_letter"] == 6
    assert "not verified OCR-negative" in result["hard_negative_scope"]
    assert result["optimizer_steps"] == 0


@pytest.mark.parametrize("relative", ["annotations/scene-0.json", "masks/scene-0.png", "splits/train.json"])
def test_tampered_seed_manifest_artifact_is_rejected(tmp_path, monkeypatch, relative):
    data = fixture(tmp_path, monkeypatch)
    (data / relative).write_bytes(b"changed")
    with pytest.raises(ValueError, match="artifact identity mismatch"):
        prepare.summarize(data)


@pytest.mark.parametrize("mutate", [
    lambda a: a["panels"][0]["texts"][0].pop("text"),
    lambda a: a["panels"][0]["texts"][0].update(text="2"),
    lambda a: (a["panels"][0]["texts"][0].update(text="2"),
               a["panels"][0]["ticks"][0].update(value=2)),
    lambda a: a["panels"][0]["texts"][0].update(rendered_pixel_box=[99, 36, 3, 4]),
    lambda a: a["panels"][0]["ticks"].clear(),
    lambda a: a["panels"][0]["texts"].clear(),
    lambda a: a["panels"][0]["texts"].append(dict(a["panels"][0]["texts"][0], text_id="new-id")),
])
def test_bad_tick_truth_is_rejected_even_if_rehashed(tmp_path, monkeypatch, mutate):
    data = fixture(tmp_path, monkeypatch)
    rewrite_annotation(data, mutate)
    with pytest.raises(ValueError, match="X-tick"):
        prepare.summarize(data)


def test_missing_case_rejected(tmp_path, monkeypatch):
    data = fixture(tmp_path, monkeypatch)
    path = data / "seed-manifest.json"
    value = json.loads(path.read_text(encoding="utf-8"))
    value["cases"].pop()
    path.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(ValueError, match="exactly ten"):
        prepare.summarize(data)


def test_overlapping_family_is_rejected_even_if_rehashed(tmp_path, monkeypatch):
    data = fixture(tmp_path, monkeypatch)
    expected = prepare._fixed_scenes()
    expected[4]["families"]["renderer"]["key"] = "train"
    scene_path = data / "scenes/scene-4.json"
    scene_path.write_text(json.dumps(expected[4]), encoding="utf-8")
    path = data / "seed-manifest.json"
    value = json.loads(path.read_text(encoding="utf-8"))
    value["cases"][4]["families"]["renderer"] = "train"
    value["cases"][4]["files"]["scenes/scene-4.json"] = prepare.digest(scene_path)
    value["artifact_sha256"]["scenes/scene-4.json"] = prepare.digest(scene_path)
    path.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises((ValueError, AssertionError), match="family"):
        prepare.summarize(data)


def test_unlisted_file_is_rejected(tmp_path, monkeypatch):
    data = fixture(tmp_path, monkeypatch)
    (data / "images/unlisted.png").write_bytes(b"unlisted")
    with pytest.raises(ValueError, match="disk inventory"):
        prepare.summarize(data)


@pytest.mark.parametrize("change", ["extra_case", "family", "file"])
def test_rehashed_split_manifest_conflict_is_rejected(tmp_path, monkeypatch, change):
    data = fixture(tmp_path, monkeypatch)
    path = data / "splits/train.json"
    value = json.loads(path.read_text(encoding="utf-8"))
    if change == "extra_case":
        value["cases"].append(dict(value["cases"][0], scene_id="scene-4"))
    elif change == "family":
        value["families"]["renderer"].append("validation")
    else:
        value["cases"][0]["files"].append("images/scene-4.png")
    path.write_text(json.dumps(value), encoding="utf-8")
    manifest = data / "seed-manifest.json"
    binding = json.loads(manifest.read_text(encoding="utf-8"))
    binding["artifact_sha256"]["splits/train.json"] = prepare.digest(path)
    manifest.write_text(json.dumps(binding), encoding="utf-8")
    with pytest.raises(ValueError, match="split manifest"):
        prepare.summarize(data)


def test_output_cannot_overwrite_or_escape_artifacts(tmp_path):
    with pytest.raises(ValueError):
        prepare.prepare(tmp_path)
