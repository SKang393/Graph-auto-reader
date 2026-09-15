# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import pytest
from authenticate_text_extent_evidence import _same_evidence
from ml.policy.ocr_sealed_evaluation import OcrSealedEvaluationError


def test_recomputation_ignores_only_elapsed_time():
    _same_evidence({'metrics': {'exact': 10}, 'elapsed_milliseconds': 2},
                   {'metrics': {'exact': 10}, 'elapsed_milliseconds': 5})


@pytest.mark.parametrize('changed', [{'metrics': {'exact': 11}},
                                   {'metrics': {'exact': 10}, 'approved': True}, {}])
def test_recomputation_rejects_changed_science_or_scope(changed):
    with pytest.raises(OcrSealedEvaluationError, match='RECOMPUTATION_MISMATCH'):
        _same_evidence({'metrics': {'exact': 10}}, changed)


def test_recomputation_rejects_boolean_as_count():
    with pytest.raises(OcrSealedEvaluationError, match='RECOMPUTATION_MISMATCH'):
        _same_evidence({'metrics': {'exact': 1}}, {'metrics': {'exact': True}})
