# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from collections import Counter
from dataclasses import replace

import numpy as np
import pytest

from ml.markers.classifier.runtime_diagnostic import diagnostic_cases, diagnostic_pair


def test_balanced_coverage_does_not_assign_shapes_to_different_splits() -> None:
    cases = diagnostic_cases()
    assert len(cases) == 2160
    assert len({tuple(case.record().values()) for case in cases}) == 2160
    assert set(Counter(case.shape for case in cases).values()) == {240}
    conditions = lambda shape: {(case.fill, case.context, case.radius, case.stroke_width, case.center)
                                for case in cases if case.shape == shape}
    assert all(conditions(shape) == conditions("circle") for shape in {case.shape for case in cases})


def test_ambiguous_or_visually_identical_fill_is_not_invented() -> None:
    cases = diagnostic_cases()
    assert all(case.expected_fill is None for case in cases
               if case.fill == "degraded" or case.shape in ("asterisk", "cross"))
    assert Counter(case.expected_fill for case in cases) == {None: 1040, "open": 560, "filled": 560}


@pytest.mark.parametrize("context", ["isolated", "horizontal_line", "oblique_line", "neighbor", "axis_contact"])
def test_original_raster_and_reference_are_deterministic_ink(context: str) -> None:
    case = replace(diagnostic_cases()[0], context=context)
    a = diagnostic_pair(case)
    b = diagnostic_pair(case)
    for actual, repeated in zip(a, b):
        assert actual.shape == (1, 32, 32) and actual.dtype == np.float32
        assert np.isfinite(actual).all() and actual.min() >= 0 and actual.max() <= 1
        assert actual.max() > 0
        np.testing.assert_array_equal(actual, repeated)
    assert np.max(np.abs(a[0] - a[1])) > 0


def test_fill_is_visible_for_closed_glyph_and_not_visible_for_cross() -> None:
    case = diagnostic_cases()[0]
    for native, filled in zip(diagnostic_pair(case), diagnostic_pair(replace(case, fill="filled"))):
        assert np.max(np.abs(native - filled)) > 0
    for opened, filled in zip(diagnostic_pair(replace(case, shape="cross")),
                              diagnostic_pair(replace(case, shape="cross", fill="filled"))):
        np.testing.assert_array_equal(opened, filled)
