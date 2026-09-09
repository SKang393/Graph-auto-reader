# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from dataclasses import replace
from hashlib import sha256
from io import BytesIO
from pathlib import Path
from typing import Any, Mapping, cast

import numpy as np
from PIL import Image
import pytest
import torch

from ml.markers.center.mask_preserving_v24 import runtime_family_scenes as subject
from ml.markers.center.mask_preserving_v24.family_scenes import FamilyScene
from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    ValidatedRuntimeInputs,
    ValidatedRuntimePanelInput,
)


def _scene(seed: int, split: str, marker_key: str) -> dict[str, Any]:
    return {
        "seed": seed,
        "families": {
            axis: {"key": f"{marker_key}-{axis}", "split": split}
            for axis in ("renderer", "font", "degradation", "template", "marker")
        },
    }


def _family(scene: Mapping[str, Any]) -> str:
    return "|".join(
        f"{axis}={scene['families'][axis]['key']}"
        for axis in ("renderer", "font", "degradation", "template", "marker")
    )


def _source_sha(image: Image.Image) -> str:
    stream = BytesIO()
    image.convert("RGB").save(stream, format="PNG")
    return sha256(stream.getvalue()).hexdigest()


def _plane(value: np.ndarray) -> np.ndarray:
    result = np.asarray(value).copy()
    result.setflags(write=False)
    return result


def _panel(
    scene: Mapping[str, Any],
    image: Image.Image,
    split: str,
    panel_id: str,
    crop: tuple[int, int, int, int],
    *,
    dataset_seed: int = 393,
) -> ValidatedRuntimePanelInput:
    x, y, width, height = crop
    gray = np.asarray(image.convert("L"), dtype=np.uint8)[y:y + height, x:x + width]
    grid = np.arange(width * height, dtype=np.float32).reshape(height, width)
    ocr = np.mod(grid * np.float32(0.137), np.float32(1.0))
    artifact = np.mod(grid * np.float32(0.223) + np.float32(0.071), np.float32(1.0))
    source_sha = _source_sha(image)
    panel_sha = sha256(f"{source_sha}:{crop}".encode("ascii")).hexdigest()
    return ValidatedRuntimePanelInput(
        split=split,
        dataset_seed=dataset_seed,
        family=_family(scene),
        scene_seed=int(scene["seed"]),
        source_sha256=source_sha,
        panel_id=panel_id,
        panel_sha256=panel_sha,
        width=width,
        height=height,
        crop=crop,
        requested_crop=tuple(float(value) for value in crop),
        gray8=_plane(gray),
        ocr_mask=_plane(ocr),
        geometry_mask=_plane(np.minimum(artifact, np.float32(0.3))),
        artifact_mask=_plane(artifact),
    )


def _fixture(monkeypatch: pytest.MonkeyPatch) -> tuple[
    ValidatedRuntimeInputs,
    dict[str, Any],
    dict[str, Any],
    Image.Image,
    Image.Image,
]:
    train_scene = _scene(101, "train", "train")
    dev_scene = _scene(202, "validation", "dev")
    train_image = Image.fromarray(
        np.arange(12 * 8 * 3, dtype=np.uint8).reshape(8, 12, 3), mode="RGB"
    )
    dev_image = Image.new("RGB", (6, 6), (171, 171, 171))
    annotations = {
        101: {
            "markers": [
                {"center": [4.0, 3.0], "radius": 1.0},
                {"center": [8.0, 3.0], "radius": 0.5},
            ],
            "hard_negatives": [
                {"kind": "intersection", "center": [3.0, 2.0]},
                {"kind": "outside", "center": [11.0, 7.0]},
            ],
            "texts": [
                {"kind": "tick", "rendered_pixel_box": [2.5, 1.5, 1.0, 1.0]},
                {"kind": "title", "rendered_pixel_box": [10.5, 0.0, 1.0, 1.0]},
            ],
            "arrows": [
                {"kind": "inside", "line": [[7.5, 2.0], [8.5, 2.0]]},
                {"kind": "cross_crop", "line": [[4.5, 4.0], [7.5, 4.0]]},
            ],
        },
        202: {
            "markers": [{"center": [3.0, 3.0], "radius": 1.0}],
            "hard_negatives": [],
            "texts": [],
        },
    }
    images = {101: train_image, 202: dev_image}
    scenes = [train_scene, dev_scene]
    monkeypatch.setattr(subject, "_build_scenes", lambda *_args, **_kwargs: scenes)
    monkeypatch.setattr(
        subject,
        "render_scene",
        lambda scene: (images[int(scene["seed"])], annotations[int(scene["seed"])], None),
    )
    train = (
        _panel(train_scene, train_image, "train", "train-left", (2, 1, 5, 5)),
        _panel(train_scene, train_image, "train", "train-right", (7, 1, 3, 5)),
        _panel(train_scene, train_image, "train", "train-negative", (0, 6, 2, 2)),
    )
    dev = (_panel(dev_scene, dev_image, "validation", "dev-full", (0, 0, 6, 6)),)
    inputs = ValidatedRuntimeInputs(train, dev, cast(Any, object()))
    return inputs, train_scene, dev_scene, train_image, dev_image


def test_joins_nonzero_crops_with_exact_translation_and_actual_runtime_planes(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
):
    inputs, _, _, _, _ = _fixture(monkeypatch)
    monkeypatch.chdir(tmp_path)

    joined = subject.join_runtime_family_scenes(inputs)

    assert len(joined.train) == 3
    assert all(isinstance(scene, FamilyScene) for scene in joined.train)
    left, right, negative = joined.train
    assert left.centers == ((2.0, 2.0),)
    assert left.diameters == (2.0,)
    assert right.centers == ((1.0, 2.0),)
    assert right.diameters == (1.0,)
    assert negative.centers == ()
    assert negative.diameters == ()
    assert left.hard_negatives == (("intersection", 1.0, 1.0),)
    np.testing.assert_array_equal(left.tensor[1].numpy(), inputs.train[0].ocr_mask)
    np.testing.assert_array_equal(left.tensor[2].numpy(), inputs.train[0].artifact_mask)
    expected_ink = 1.0 - inputs.train[0].gray8.astype(np.float32) / 255.0
    np.testing.assert_array_equal(left.tensor[0].numpy(), expected_ink)
    assert left.sampling_identity == subject._sampling_identity(inputs.train[0])
    assert left.crop == (2, 1, 5, 5)
    assert left.dataset_seed == 393
    assert not list(tmp_path.rglob("*.png"))

    train_audit = next(item for item in joined.audit if item.split == "train")
    assert train_audit.source_marker_count == train_audit.mapped_marker_count == 2
    assert train_audit.source_hard_negative_count == 2
    assert train_audit.mapped_hard_negative_count == 1
    assert train_audit.source_descriptive_record_count == 4
    assert train_audit.mapped_descriptive_record_count == 2
    assert {(item.category, item.kind) for item in train_audit.omitted} == {
        ("hard_negatives", "outside"),
        ("texts", "title"),
        ("arrows", "cross_crop"),
    }


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        ("source_sha256", "0" * 64, "source coverage differs"),
        ("scene_seed", 999, "source seed, family, or dataset identity differs"),
        ("family", "foreign-family", "source seed, family, or dataset identity differs"),
        ("dataset_seed", 394, "dataset identities"),
    ],
)
def test_rejects_source_seed_family_and_dataset_identity_mismatch(
    monkeypatch: pytest.MonkeyPatch,
    field: str,
    value: object,
    message: str,
):
    inputs, _, _, _, _ = _fixture(monkeypatch)
    changed = replace(inputs.train[0], **{field: value})
    tampered = replace(inputs, train=(changed, *inputs.train[1:]))
    with pytest.raises(subject.RuntimeFamilySceneError, match=message):
        subject.join_runtime_family_scenes(tampered)


def test_rejects_marker_whose_full_radius_is_lost_or_clipped(
    monkeypatch: pytest.MonkeyPatch,
):
    inputs, _, _, _, _ = _fixture(monkeypatch)
    clipped_left = replace(
        inputs.train[0],
        crop=(4, 1, 3, 5),
        width=3,
        gray8=inputs.train[0].gray8[:, :3],
        ocr_mask=inputs.train[0].ocr_mask[:, :3],
        geometry_mask=inputs.train[0].geometry_mask[:, :3],
        artifact_mask=inputs.train[0].artifact_mask[:, :3],
    )
    tampered = replace(inputs, train=(clipped_left, *inputs.train[1:]))
    with pytest.raises(subject.RuntimeFamilySceneError, match="marker 0 lost_or_clipped"):
        subject.join_runtime_family_scenes(tampered)


def test_rejects_marker_target_mapped_to_duplicate_crops(monkeypatch: pytest.MonkeyPatch):
    inputs, _, _, _, _ = _fixture(monkeypatch)
    duplicate = replace(inputs.train[0], panel_id="duplicate-panel")
    tampered = replace(inputs, train=(inputs.train[0], duplicate, *inputs.train[1:]))
    with pytest.raises(subject.RuntimeFamilySceneError, match="mapped_to_multiple_panels"):
        subject.join_runtime_family_scenes(tampered)


def test_rejects_sealed_or_foreign_split(monkeypatch: pytest.MonkeyPatch):
    inputs, _, _, _, _ = _fixture(monkeypatch)
    sealed = replace(inputs.train[0], split="sealed")
    with pytest.raises(subject.RuntimeFamilySceneError, match="foreign or sealed split"):
        subject.join_runtime_family_scenes(replace(inputs, train=(sealed, *inputs.train[1:])))


def test_requires_complete_source_coverage_before_annotation_is_read(
    monkeypatch: pytest.MonkeyPatch,
):
    inputs, train_scene, dev_scene, train_image, dev_image = _fixture(monkeypatch)
    missing_scene = _scene(303, "train", "missing")
    missing_image = Image.new("RGB", (4, 4), (99, 99, 99))

    class UnreadableAnnotation(dict[str, object]):
        def get(self, *_args: object, **_kwargs: object) -> object:
            raise AssertionError("annotation was read before source coverage passed")

    scenes = [train_scene, missing_scene, dev_scene]
    images = {101: train_image, 202: dev_image, 303: missing_image}
    annotations: dict[int, Mapping[str, Any]] = {
        101: UnreadableAnnotation(), 202: {}, 303: UnreadableAnnotation()
    }
    monkeypatch.setattr(subject, "_build_scenes", lambda *_args, **_kwargs: scenes)
    monkeypatch.setattr(
        subject,
        "render_scene",
        lambda scene: (images[int(scene["seed"])], annotations[int(scene["seed"])], None),
    )
    with pytest.raises(subject.RuntimeFamilySceneError, match="source coverage differs"):
        subject.join_runtime_family_scenes(inputs)


def test_sampling_identity_changes_with_panel_or_crop_identity(monkeypatch: pytest.MonkeyPatch):
    inputs, _, _, _, _ = _fixture(monkeypatch)
    panel = inputs.train[0]
    baseline = subject._sampling_identity(panel)
    assert subject._sampling_identity(replace(panel, panel_sha256="f" * 64)) != baseline
    assert subject._sampling_identity(replace(panel, panel_id="different")) != baseline
    assert subject._sampling_identity(replace(panel, crop=(1, 1, 5, 5))) != baseline
