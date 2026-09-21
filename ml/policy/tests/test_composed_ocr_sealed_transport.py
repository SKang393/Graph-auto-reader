# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from dataclasses import asdict
import json

import pytest

from ml.policy.ocr_sealed_transport import (
    COMPOSED_RESULT_SCHEMA, ComposedOcrSealedRequestIdentity, OcrSealedTransportError,
    run_ocr_sealed_worker, validate_result_envelope,
)
from ml.policy.tests import test_ocr_sealed_transport as legacy


def identity():
    return ComposedOcrSealedRequestIdentity(**asdict(legacy.identity()))


def envelope():
    value = legacy.envelope()
    value["schema"] = COMPOSED_RESULT_SCHEMA
    previous = value["aggregate"]["metrics"]
    value["aggregate"]["metrics"] = {
        "source_count": 2,
        "raw_detector_geometry": legacy.geometry(8, 5, 5),
        "assembled_geometry": previous["successfully_recognized_region_geometry"],
        "full_ocr_metrics": previous["full_ocr_metrics"],
        "recognition_failed_region_count": 0,
    }
    return value


def test_recovered_regions_do_not_become_negative_failures():
    value = envelope()
    assert validate_result_envelope(value, identity()) == value
    assert value["aggregate"]["metrics"]["assembled_geometry"]["predicted_region_count"] == 7


def test_merged_fragments_and_explicit_failures_are_independent():
    value = envelope()
    value["aggregate"]["metrics"]["raw_detector_geometry"] = legacy.geometry(8, 12, 8)
    value["aggregate"]["metrics"]["recognition_failed_region_count"] = 2
    assert validate_result_envelope(value, identity()) == value


@pytest.mark.parametrize("value,expected", [(envelope(), legacy.identity()), (legacy.envelope(), identity())])
def test_schema_cannot_be_substituted(value, expected):
    with pytest.raises(OcrSealedTransportError):
        validate_result_envelope(value, expected)


@pytest.mark.parametrize("mutation", [
    lambda m: m.update(source_count=1),
    lambda m: m.update(recognition_failed_region_count=-1),
    lambda m: m.update(recognition_failed_region_count=True),
    lambda m: m.update(recognition_failed_region_count=100000),
    lambda m: m.update(case_text="must-not-leak"),
    lambda m: m["raw_detector_geometry"].update(recall=float("nan")),
    lambda m: m["assembled_geometry"].update(false_positives=2),
    lambda m: m.update(assembled_geometry=legacy.geometry(8, 8, 8)),
    lambda m: m["full_ocr_metrics"].update(character_error_rate=0),
    lambda m: m["full_ocr_metrics"]["by_expected_runtime_role"].pop("xtick"),
])
def test_malformed_or_case_level_metrics_fail_closed(mutation):
    value = envelope()
    mutation(value["aggregate"]["metrics"])
    with pytest.raises(OcrSealedTransportError) as error:
        validate_result_envelope(value, identity())
    assert "must-not-leak" not in str(error.value)


def test_composed_mode_does_not_change_legacy_wire_identity_fields():
    assert asdict(identity()) == asdict(legacy.identity())
    with pytest.raises(TypeError):
        ComposedOcrSealedRequestIdentity(**asdict(legacy.identity()), composed=True)


def test_handshake_and_positive_receipt_still_precede_acceptance(tmp_path):
    events, ack, receipt = legacy.callbacks()
    value = envelope()
    result = run_ocr_sealed_worker(legacy.command("valid", json.dumps(value)),
                                 tmp_path, identity(), ack, receipt, timeout_seconds=2)
    assert result.envelope == value
    assert len(events) == 2


@pytest.mark.parametrize("scenario", ["missing_receipt", "bad_receipt", "duplicate_request", "wrong_first_identity"])
def test_composed_transport_does_not_relax_read_authorization(tmp_path, scenario):
    _, ack, receipt = legacy.callbacks()
    with pytest.raises(OcrSealedTransportError):
        run_ocr_sealed_worker(legacy.command(scenario, json.dumps(envelope())),
                             tmp_path, identity(), ack, receipt, timeout_seconds=2)
