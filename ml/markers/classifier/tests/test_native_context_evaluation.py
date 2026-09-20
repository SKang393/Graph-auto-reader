# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import numpy as np
import pytest

from ml.markers.classifier.native_context_cache import observable_targets
from ml.markers.classifier.native_context_evaluation import summarize


def example():
    rows = [dict(artifact=i == 2, shape_index=0 if i < 2 else -1,
                 fill_index=0 if i < 2 else -1, family="dev", context="context",
                 shape="circle" if i < 2 else None, patch_sha256=str(i)) for i in range(3)]
    for row, target in zip(rows, observable_targets(rows), strict=True):
        row.update(target)
    output = np.zeros((3, 25), np.float32)
    output[:, 0] = output[:, 9] = 1
    output[:, 12] = [.5, .49, .5]
    return rows, output


def test_rejected_true_marker_remains_in_denominator_and_threshold_is_inclusive():
    rows, output = example()
    result = summarize(rows, output)
    assert result["totals"]["markers"] == 2
    assert result["totals"]["true_markers_retained"] == 1
    assert result["totals"]["retained_marker_recall"] == .5
    assert result["totals"]["artifacts_accepted"] == 0
    assert result["gates"]["retention_recall"] is False
    assert result["totals"]["shape_correct"] == 2


def test_ambiguous_shape_accuracy_still_scores_both_authored_answers():
    rows, output = example()
    rows[1]["shape_index"] = 4
    rows[1]["patch_sha256"] = rows[0]["patch_sha256"]
    for row, target in zip(rows, observable_targets(rows), strict=True):
        row.update(target)
    result = summarize(rows, output)
    assert result["totals"]["shape_correct"] == 1
    assert result["totals"]["shape_accuracy"] == .5
    assert result["totals"]["shape_ambiguous_rows"] == 2


def test_invalid_probability_output_cannot_produce_metrics():
    rows, output = example()
    output[0, 0] = float("nan")
    with pytest.raises(ValueError, match="invalid"):
        summarize(rows, output)
    output[0, 0] = .25
    with pytest.raises(ValueError, match="contract"):
        summarize(rows, output)
