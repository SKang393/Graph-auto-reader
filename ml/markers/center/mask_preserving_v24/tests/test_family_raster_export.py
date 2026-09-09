# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import hashlib
import json

import numpy as np
from PIL import Image
import pytest

from ml.markers.center.mask_preserving_v24.export_family_rasters import export_family_rasters
from ml.markers.center.mask_preserving_v24.family_scenes import build_family_split


def test_export_crosses_only_raster_identity_and_matches_family_pixels(tmp_path):
    manifest_path = export_family_rasters(tmp_path, "train")
    manifest = json.loads(manifest_path.read_text())
    scenes = {scene.seed: scene for scene in build_family_split("train")}
    assert len(manifest["images"]) == len(scenes) == 4
    assert manifest["contains_truth"] is False
    assert manifest["contains_precomputed_masks"] is False
    for record in manifest["images"]:
        assert set(record) == {"image", "image_sha256", "width", "height", "split", "family", "seed"}
        path = tmp_path / record["image"]
        assert hashlib.sha256(path.read_bytes()).hexdigest() == record["image_sha256"]
        with Image.open(path) as image:
            ink = 1.0 - np.asarray(image.convert("L"), dtype=np.float32) / 255.0
            assert image.size == (record["width"], record["height"])
        scene = scenes[record["seed"]]
        assert record["family"] == scene.family
        assert record["split"] == scene.split == "train"
        np.testing.assert_array_equal(ink, scene.tensor[0].numpy())
    before = manifest_path.read_bytes()
    assert export_family_rasters(tmp_path, "train") == manifest_path
    assert manifest_path.read_bytes() == before


def test_sealed_export_is_rejected_before_writing(tmp_path):
    output = tmp_path / "unused"
    with pytest.raises(ValueError, match="Only synthetic train and dev"):
        export_family_rasters(output, "sealed")
    assert not output.exists()


def test_different_existing_manifest_is_preserved(tmp_path):
    manifest = tmp_path / "input-manifest.json"
    manifest.write_text("existing evidence\n")
    with pytest.raises(ValueError, match="Refusing to replace"):
        export_family_rasters(tmp_path, "dev")
    assert manifest.read_text() == "existing evidence\n"
    assert list(tmp_path.glob("*.png")) == []
