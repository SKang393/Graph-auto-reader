# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Retirement permits old accounting recovery, never a new read authorization."""
import json
import pytest
from ml.policy import sealed_reserve, sealed_first_read_admission as coordinator
from ml.policy.tests import test_sealed_first_read_admission as fixture
from ml.markers.gate_seal import sha256_file


@pytest.mark.parametrize('confirmed', [False, True])
def test_retired_reserve_recovers_only_prior_accounting(tmp_path, monkeypatch, confirmed):
    admission, training, gate, registry = fixture._prepare(tmp_path)
    metadata = coordinator.bound_reserve_metadata(admission)
    if confirmed:
        fixture._confirm(admission)
    else:
        coordinator.send_ack(admission, attempt_id=fixture.ATTEMPT_ID,
            request_nonce=fixture.NONCE, write_and_flush=lambda _frame: None)
    def forbidden(*args, **kwargs): pytest.fail('Recovery must not open a reserve archive')
    monkeypatch.setattr(sealed_reserve.zipfile, 'ZipFile', forbidden)
    coordinator.record_case_level_disclosure(
        admission, evidence_sha256='e'*64, disclosures=['truth'])
    with pytest.raises(coordinator.SealedFirstReadAdmissionError):
        coordinator.bound_reserve_metadata(admission)
    with pytest.raises(coordinator.SealedFirstReadAdmissionError):
        coordinator.send_ack(admission,attempt_id=fixture.ATTEMPT_ID,
            request_nonce=fixture.NONCE,write_and_flush=forbidden)
    for _ in range(2):
        recovered = coordinator.recover_admission(registry,tmp_path,admission_id=admission.admission_id,
            training_authorization=training,gate_seal=gate)
        assert fixture._authority(recovered)['read_status'] == ('confirmed' if confirmed else 'possible')
        state = json.loads(registry.read_text(encoding='utf-8'))
        assert state['sets'][0]['state'] == 'retired'
        assert sum(len(row['uses']) for row in state['sets']) == int(confirmed)
        if confirmed:
            assert state['sets'][0]['uses'][0]['aggregate_only'] is False
            assert state['sets'][0]['uses'][0]['disclosures'] == ['truth']
    fresh_training,fresh_gate = fixture._source_bound_objects(tmp_path,suffix='2')
    with pytest.raises(coordinator.SealedFirstReadAdmissionError,match='incompatible'):
        coordinator.prepare_admission(registry,tmp_path,expected_registry_sha256=sha256_file(registry),
            set_id=metadata['set_id'],candidate_sha256=fixture.CANDIDATE_SHA256,
            training_authorization=fresh_training,gate_seal=fresh_gate,
            required_acceptance_scope=fixture.ocr_sealed_acceptance.ACCEPTANCE_SCOPE,
            required_coverage_protocol_sha256=fixture.ocr_sealed_acceptance.PROTOCOL_SHA256,
            attempt_id=fixture.ATTEMPT_ID, attempt_binding=fixture.ATTEMPT_BINDING)
