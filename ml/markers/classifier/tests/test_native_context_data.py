# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from collections import Counter
from dataclasses import replace

import numpy as np
import pytest

from ml.markers.classifier.native_context_data import ARTIFACTS, FAMILIES, cases, prepare_case
from ml.markers.classifier.runtime_diagnostic import SHAPES, CONTEXTS


def test_complete_shape_context_coverage_with_disjoint_source_families():
    train, dev = cases("train"), cases("dev")
    assert len(train) == 7840 and len(dev) == 3136
    assert not {c.sample_id for c in train} & {c.sample_id for c in dev}
    for attribute in ("name", "font", "scale", "blur", "centers", "crop_radius_factors", "line_angle"):
        assert not {getattr(f, attribute) for f in FAMILIES if f.split == "train"} & {
            getattr(f, attribute) for f in FAMILIES if f.split == "dev"}
    for split in (train, dev):
        markers = [c for c in split if not c.artifact]
        assert set(c.shape for c in markers) == set(SHAPES)
        assert len(set(Counter(c.shape for c in markers).values())) == 1
        assert all({c.context for c in markers if c.shape == shape} == set(CONTEXTS) for shape in SHAPES)
        assert {c.context for c in split if c.artifact} == set(ARTIFACTS)


@pytest.mark.parametrize("split", ["train", "dev"])
@pytest.mark.parametrize("context", CONTEXTS + ARTIFACTS)
def test_preparation_is_deterministic_original_raster_ink(split, context):
    case = next(c for c in cases(split) if c.context == context)
    patch, record = prepare_case(case)
    again, repeated = prepare_case(case)
    assert patch.shape == (1,32,32) and patch.dtype == np.float32
    assert np.isfinite(patch).all() and 0 <= patch.min() < patch.max() <= 1
    np.testing.assert_array_equal(patch, again)
    assert record == repeated
    assert record["crop_radius"] == case.radius * case.crop_radius_factor
    if record["font"]:
        assert record["font"]["source"] == "system" and not record["font"]["bundled"]


def test_visually_undefined_fill_is_explicit_unknown():
    for case in cases("train"):
        if case.shape in ("cross", "asterisk"):
            assert case.observable_fill is None and case.record()["fill_index"] == 2
        if case.artifact:
            assert case.record()["shape_index"] == case.record()["fill_index"] == -1


@pytest.mark.parametrize("split", ["sealed", "private", "test", "validation"])
def test_unregistered_splits_are_not_accessible(split):
    with pytest.raises(ValueError, match="train/dev"):
        cases(split)


def test_forged_source_family_and_scientific_labels_are_rejected():
    case = cases("train")[0]
    for changed in (replace(case, split="dev"), replace(case, shape="invented"),
                    replace(case, radius=99), replace(case, context="bare_legend_circle")):
        with pytest.raises(ValueError):
            prepare_case(changed)
