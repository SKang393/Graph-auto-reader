# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from dataclasses import fields, replace, asdict
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from ml.markers.gate_seal import canonical_json_bytes, sha256_bytes, sha256_file
from ml.policy import ocr_sealed_evaluation as evaluation
from ml.policy.ocr_sealed_transport import (
    OcrSealedDisclosureError,
    OcrSealedUnclassifiedOutputError,
)
from ml.policy.sealed_first_read_admission import AdmissionReceipt
from ml.policy.tests import test_sealed_first_read_admission as admission_fixture
from ml.policy.tests import test_ocr_sealed_transport as wire_fixture


HASH = "0123456789abcdef" * 4
ROLES = (
    "annotation", "axistitle", "legendtext", "participant",
    "phaseheading", "xtick", "ytick",
)


def _geometry(count: int) -> dict[str, object]:
    return {
        "truth_region_count": count,
        "predicted_region_count": count,
        "true_positives": count,
        "false_positives": 0,
        "false_negatives": 0,
        "precision": 1.0,
        "recall": 1.0,
        "intersection_over_union_minimum": 0.5,
    }


def _full_metrics(count: int, characters: int) -> dict[str, object]:
    return {
        "truth_region_count": count,
        "predicted_region_count": count,
        "geometry_matched_region_count": count,
        "geometry_false_positive_count": 0,
        "geometry_false_negative_count": 0,
        "recognition_exact_count": count,
        "recognition_exact_accuracy": 1.0,
        "truth_character_count": characters,
        "matched_pair_edit_count": 0,
        "unmatched_truth_deletion_edit_count": 0,
        "unmatched_prediction_insertion_edit_count": 0,
        "character_error_count": 0,
        "character_error_rate": 0.0,
        "role_correct_count": count,
        "role_accuracy": 1.0,
        "by_expected_runtime_role": {
            role: {
                "truth_count": count - 6 if role == "annotation" else 1,
                "correct_count": count - 6 if role == "annotation" else 1,
                "accuracy": 1.0,
            }
            for role in ROLES
        },
        "intersection_over_union_minimum": 0.5,
    }


def _score(root: Path, candidate: Path) -> dict[str, object]:
    split_counts = {"train": (709, 3000), "validation": (183, 1019)}
    return {
        "schema": evaluation.FULL_OCR_SCORE_SCHEMA,
        "status": "diagnostic_only_unapproved",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "truth_isolation": {
            "runtime_received_truth": False,
            "all_runtime_and_candidate_evidence_authenticated_before_truth_regeneration": True,
        },
        "inputs": {
            "candidate": {
                "path": candidate.relative_to(root).as_posix(),
                "sha256": sha256_file(candidate),
            },
            "evaluator_sha256": evaluation.METRIC_REFERENCE_SHA256,
        },
        "metrics": {
            split: _full_metrics(*counts) for split, counts in split_counts.items()
        },
        "raw_detector_geometry": {
            split: _geometry(counts[0]) for split, counts in split_counts.items()
        },
        "successfully_recognized_region_geometry": {
            split: _geometry(counts[0]) for split, counts in split_counts.items()
        },
        "recognition_failures": {
            split: {"raw_regions_without_successful_recognition": 0,
                    "failed_panel_count": 0, "explicit_region_failure_count": 0,
                    "raw_regions_on_failed_panels": 0}
            for split in split_counts
        },
        "acceptance_bar_reference": {"sha256": evaluation.ACCEPTANCE_BARS_SHA256},
        "integrity": {
            "source_count": 23,
            "panel_count": 37,
            "full_source_truth_count": 892,
        },
    }


def _runtime_fixture(tmp_path: Path, *, composed=False) -> tuple[Path, Path, evaluation.AuthenticatedFullOcrEvidence]:
    root = tmp_path.resolve()
    artifacts = root / "artifacts"
    runtime = artifacts / "runtime"
    runtime.mkdir(parents=True)
    apphost = runtime / "worker.exe"
    apphost.write_bytes(b"apphost")
    assemblies = []
    runtime_files = [apphost]
    for index in range(4):
        assembly = runtime / f"assembly-{index}.dll"
        assembly.write_bytes(f"assembly-{index}".encode())
        runtime_files.append(assembly)
        assemblies.append({
            "name": f"Assembly{index}",
            "path": assembly.relative_to(root).as_posix(),
            "sha256": sha256_file(assembly),
        })
    candidate = artifacts / "candidate.json"
    candidate.write_text(json.dumps({
        "schema": ("graphreader.frozen-composed-ocr-candidate.v1" if composed
                   else "graphreader.frozen-db-head-ocr-candidate.v1"),
        "execution_assemblies": assemblies,
    }), encoding="utf-8")
    score_path = artifacts / "full-score.json"
    score_path.write_text(json.dumps(_score(root, candidate)), encoding="utf-8")
    evidence = evaluation.AuthenticatedFullOcrEvidence(
        schema=evaluation.COMPOSED_EVIDENCE_SCHEMA if composed else evaluation.FULL_OCR_EVIDENCE_SCHEMA,
        full_ocr_score_path=score_path,
        full_ocr_score_sha256=sha256_file(score_path),
        candidate_path=candidate,
        candidate_sha256=sha256_file(candidate),
        runtime_command_prefix=(str(apphost),),
        runtime_files=tuple(
            evaluation.AuthenticatedRuntimeFile(path, sha256_file(path))
            for path in runtime_files
        ),
        runtime_identity_sha256="0" * 64,
        acceptance_bars_sha256=evaluation.ACCEPTANCE_BARS_SHA256,
        metric_reference_sha256=evaluation.METRIC_REFERENCE_SHA256,
        acceptance_scope=evaluation.ACCEPTANCE_SCOPE,
        coverage_protocol_sha256=evaluation.COVERAGE_PROTOCOL_SHA256,
    )
    return root, candidate, replace(
        evidence,
        runtime_identity_sha256=evaluation._runtime_identity(evidence, root, candidate),
    )


def _bars() -> evaluation._CanonicalOcrBars:
    return evaluation._CanonicalOcrBars(0.95, 0.95, 0.95, 0.05, 0.95)


def test_runtime_identity_binds_launch_and_candidate_inventory_bytes(tmp_path: Path) -> None:
    root, candidate, evidence = _runtime_fixture(tmp_path)
    assert evaluation._runtime_identity(evidence, root, candidate) == evidence.runtime_identity_sha256
    evidence.runtime_files[-1].path.write_bytes(b"changed")
    with pytest.raises(evaluation.OcrSealedEvaluationError, match="RUNTIME_INVALID"):
        evaluation._runtime_identity(evidence, root, candidate)


def test_full_score_is_structurally_recomputed_without_boolean_bypass(tmp_path: Path) -> None:
    root, candidate, evidence = _runtime_fixture(tmp_path)
    assert "dev_pass" not in {item.name for item in fields(evaluation.AuthenticatedFullOcrEvidence)}
    evaluation._full_ocr_dev_evidence(root, evidence, candidate, _bars())

    score = json.loads(evidence.full_ocr_score_path.read_text(encoding="utf-8"))
    score["raw_detector_geometry"]["validation"]["false_positives"] = 1
    evidence.full_ocr_score_path.write_text(json.dumps(score), encoding="utf-8")
    with pytest.raises(evaluation.OcrSealedEvaluationError, match="PREFLIGHT_INVALID"):
        evaluation._full_ocr_dev_evidence(root, evidence, candidate, _bars())


def test_full_score_uses_canonical_acceptance_bars(tmp_path: Path) -> None:
    root, candidate, evidence = _runtime_fixture(tmp_path)
    impossible = replace(_bars(), recognition_exact_minimum=1.01)
    with pytest.raises(evaluation.OcrSealedEvaluationError, match="DEV_GATE_FAILED"):
        evaluation._full_ocr_dev_evidence(root, evidence, candidate, impossible)


@pytest.mark.parametrize("field,value", [
    ("failed_panel_count", 10), ("failed_panel_count", True),
    ("explicit_region_failure_count", 1), ("raw_regions_on_failed_panels", 1),
    ("raw_regions_without_successful_recognition", -1),
])
def test_full_score_rejects_invalid_failure_partition(tmp_path, field, value):
    root, candidate, evidence = _runtime_fixture(tmp_path)
    score = json.loads(evidence.full_ocr_score_path.read_text(encoding="utf-8"))
    score["recognition_failures"]["validation"][field] = value
    evidence.full_ocr_score_path.write_text(json.dumps(score), encoding="utf-8")
    with pytest.raises(evaluation.OcrSealedEvaluationError, match="PREFLIGHT_INVALID"):
        evaluation._full_ocr_dev_evidence(root, evidence, candidate, _bars())


def test_changed_preflight_is_rejected_before_recovery_mutates_admission(tmp_path, monkeypatch):
    root, _candidate, evidence = _runtime_fixture(tmp_path)
    registry = root / 'artifacts/registry.json'
    registry.write_text('{}', encoding='utf-8')
    stored_binding = {
        'candidate_sha256': evidence.candidate_sha256,
        'runtime_identity_sha256': evidence.runtime_identity_sha256,
        'full_ocr_score_sha256': evidence.full_ocr_score_sha256,
    }
    stored_attempt = sha256_bytes(canonical_json_bytes(stored_binding))
    changed_binding = {**stored_binding, 'runtime_identity_sha256': 'f' * 64}
    changed_attempt = sha256_bytes(canonical_json_bytes(changed_binding))
    recovered = []
    monkeypatch.setattr(evaluation, 'recover_admission', lambda *a, **k: recovered.append(True))
    with pytest.raises(evaluation.OcrSealedEvaluationError, match='ADMISSION_CONFLICT'):
        evaluation._resume_existing_evaluation(
            root, registry,
            existing={'admission_id': HASH, 'attempt_id': stored_attempt,
                      'attempt_binding_sha256': stored_attempt,
                      'attempt_binding': stored_binding},
            evidence=evidence, preflight_binding_sha256=changed_attempt,
            preflight_binding=changed_binding,
            training_authorization=SimpleNamespace(), gate_seal=SimpleNamespace())
    assert recovered == []


@pytest.mark.parametrize('scenario,status,read_status,uses', [
    ('pass','pass','confirmed',1), ('fail','fail','confirmed',1),
    ('preack','void','none',0), ('uncertain','possible_read','possible',0),
    ('confirmed_error','failed','confirmed',1),
    ('changed_runtime','failed','confirmed',1),
    ('disclosure','case_data_disclosure','confirmed',1),
    ('preack_disclosure','case_data_disclosure','none',0),
    ('preack_unclassified','void','none',0),
])
@pytest.mark.parametrize('composed', [False, True])
def test_parent_uses_real_admission_and_closes_exactly_one_fixture_read(
        tmp_path, monkeypatch, scenario, status, read_status, uses, composed):
    root,candidate,evidence = _runtime_fixture(tmp_path, composed=composed)
    monkeypatch.setattr(admission_fixture, 'CANDIDATE_SHA256', sha256_file(candidate))
    registry,set_ids = admission_fixture._registry(root)
    training,gate = admission_fixture._source_bound_objects(root)
    original_registry_sha = sha256_file(registry)
    runtime_original = evidence.runtime_files[-1].path.read_bytes()
    monkeypatch.setattr(evaluation, '_authenticate_preflight', lambda *a: (evidence,_bars()))
    monkeypatch.setattr(evaluation, '_canonical_bars', lambda *a: _bars())
    def forbid_archive(*a,**k): pytest.fail('Fixture accounting must never open archives')
    monkeypatch.setattr(evaluation.sealed_reserve.zipfile, 'ZipFile', forbid_archive)
    def transport(command,root,expected,acknowledge,confirm,**kwargs):
        assert command[1] == ('--evaluate-composed-ocr-sealed' if composed else '--evaluate-original-db-sealed')
        assert isinstance(expected, evaluation.ComposedOcrSealedRequestIdentity) is composed
        if scenario == 'preack': raise RuntimeError('fixture before ACK')
        if scenario == 'preack_disclosure':
            raise OcrSealedDisclosureError(('truth',),'e'*64)
        if scenario == 'preack_unclassified':
            raise OcrSealedUnclassifiedOutputError(({
                'channel':'stderr', 'sha256':'e'*64,
                'byte_count':17, 'complete':False,
            },))
        acknowledge(expected.attempt_id, wire_fixture.NONCE, lambda frame: None)
        if scenario == 'uncertain': raise RuntimeError('fixture uncertain delivery')
        confirm(expected.attempt_id, expected.candidate_sha256, wire_fixture.NONCE)
        if scenario == 'confirmed_error': raise RuntimeError('fixture after confirmed read')
        if scenario == 'disclosure': raise OcrSealedDisclosureError(('truth',),'e'*64)
        if scenario == 'changed_runtime': evidence.runtime_files[-1].path.write_bytes(b'changed during evaluation')
        envelope = wire_fixture.envelope()
        envelope.update({k:v for k,v in asdict(expected).items() if k != 'source_count'})
        if scenario == 'pass':
            envelope['aggregate']['metrics'] = {
                'raw_detector_geometry':_geometry(8), 'successfully_recognized_region_geometry':_geometry(8),
                'recognition_failures':{'raw_regions_without_successful_recognition':0},
                'full_ocr_metrics':_full_metrics(8,10)}
        if composed:
            previous = envelope['aggregate']['metrics']
            envelope['schema'] = evaluation.COMPOSED_RESULT_SCHEMA
            envelope['aggregate']['metrics'] = {
                'source_count': expected.source_count,
                'raw_detector_geometry': previous['raw_detector_geometry'],
                'assembled_geometry': previous['successfully_recognized_region_geometry'],
                'full_ocr_metrics': previous['full_ocr_metrics'],
                'recognition_failed_region_count': 0,
            }
        envelope['aggregate']['source_count'] = expected.source_count
        envelope['aggregate']['panel_count'] = expected.source_count
        return evaluation.OcrSealedTransportResult(envelope,HASH,0,0.1)
    arguments = dict(expected_registry_sha256=original_registry_sha,set_id=set_ids[0],
        candidate_path=candidate,candidate_sha256=sha256_file(candidate),
        training_authorization=training,gate_seal=gate,
        full_ocr_evidence_authenticator=SimpleNamespace(),worker_command_prefix=evidence.runtime_command_prefix)
    result = evaluation.evaluate_ocr_sealed_candidate(root,registry,**arguments,transport_runner=transport)
    assert (result.status,result.read_status) == (status,read_status)
    state = json.loads(registry.read_text(encoding='utf-8'))
    assert sum(len(row['uses']) for row in state['sets']) == uses
    if uses:
        assert training.consumed_path.is_file() and gate.consumed_path.is_file()
        for directory in (training.directory,gate.directory):
            assert json.loads((directory/'result.json').read_text(encoding='utf-8'))['report_sha256'] == result.outcome_sha256
    if scenario in {'disclosure', 'preack_disclosure'}:
        assert state['sets'][0]['state'] == 'retired'
        assert state['sets'][0]['retirement']['disclosures'] == ['truth']
    if scenario == 'preack_unclassified':
        assert state['sets'][0]['state'] == 'unused'
        authority = evaluation._admission_record(root, registry, result.admission_id)
        assert authority['status'] == 'void'
        assert authority['quarantine_binding_sha256'] is not None
        assert authority['quarantine_confirmed_read'] is False
    if scenario == 'changed_runtime':
        evidence.runtime_files[-1].path.write_bytes(runtime_original)
    recovered = evaluation.evaluate_ocr_sealed_candidate(root,registry,**arguments,transport_runner=forbid_archive)
    assert (recovered.status,recovered.read_status,recovered.outcome_sha256) == (status,read_status,result.outcome_sha256)


def test_failure_settlement_hashes_only_sanitized_error(monkeypatch: pytest.MonkeyPatch) -> None:
    observed = []

    def fail(_admission, error):
        observed.append(str(error))
        return AdmissionReceipt(HASH, "failed", "confirmed", HASH)

    monkeypatch.setattr(evaluation, "fail_admission", fail)
    receipt = evaluation._settle_failure(
        SimpleNamespace(), RuntimeError("private case name"), ack_write_invoked=True
    )
    assert receipt.read_status == "confirmed"
    assert observed == ["OCR_SEALED_EVALUATION_FAILED"]


def _patched_evaluate(
    monkeypatch: pytest.MonkeyPatch,
    root: Path,
    candidate: Path,
    evidence: evaluation.AuthenticatedFullOcrEvidence,
) -> tuple[SimpleNamespace, SimpleNamespace, SimpleNamespace]:
    registry = root / "artifacts" / "registry.json"
    registry.write_text("{}", encoding="utf-8")
    training = SimpleNamespace(binding={"revision": "r1", "candidate_id": "c1"})
    gate = SimpleNamespace(key=HASH, binding={})
    admission = SimpleNamespace(
        admission_id=HASH,
        registry_path=registry,
        training_authorization=training,
        gate_seal=gate,
    )
    monkeypatch.setattr(evaluation, "_authenticate_preflight", lambda *args: (evidence, _bars()))
    binding = {
        "candidate_sha256": evidence.candidate_sha256,
        "runtime_identity_sha256": evidence.runtime_identity_sha256,
        "full_ocr_score_sha256": evidence.full_ocr_score_sha256,
    }
    binding_sha256 = sha256_bytes(canonical_json_bytes(binding))
    monkeypatch.setattr(
        evaluation, "_preflight_binding",
        lambda *args, **kwargs: (binding_sha256, binding),
    )
    monkeypatch.setattr(evaluation, "_matching_admission", lambda *args, **kwargs: None)
    monkeypatch.setattr(evaluation, "prepare_admission", lambda *args, **kwargs: admission)
    return training, gate, admission


def test_post_prepare_exception_is_settled_and_sanitized(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root, candidate, evidence = _runtime_fixture(tmp_path)
    training, gate, _ = _patched_evaluate(monkeypatch, root, candidate, evidence)
    observed = []
    monkeypatch.setattr(
        evaluation, "bound_reserve_metadata",
        lambda _admission: (_ for _ in ()).throw(RuntimeError("private case name")),
    )

    def void(_admission, error):
        observed.append(str(error))
        return AdmissionReceipt(HASH, "void", "none", HASH)

    monkeypatch.setattr(evaluation, "void_pre_ack", void)
    result = evaluation.evaluate_ocr_sealed_candidate(
        root,
        root / "artifacts" / "registry.json",
        expected_registry_sha256=HASH,
        set_id=HASH,
        candidate_path=candidate,
        candidate_sha256=sha256_file(candidate),
        training_authorization=training,
        gate_seal=gate,
        full_ocr_evidence_authenticator=SimpleNamespace(),
        worker_command_prefix=evidence.runtime_command_prefix,
        transport_runner=lambda *args, **kwargs: None,
    )
    assert result.status == "void"
    assert observed == ["OCR_SEALED_EVALUATION_FAILED"]
    outcome = json.loads(result.outcome_path.read_text(encoding="utf-8"))
    assert outcome["failure_code"] == "OCR_SEALED_EVALUATION_FAILED"
    assert "private case name" not in result.outcome_path.read_text(encoding="utf-8")


def test_transport_disclosure_retires_with_typed_categories(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root, candidate, evidence = _runtime_fixture(tmp_path)
    training, gate, _ = _patched_evaluate(monkeypatch, root, candidate, evidence)
    metadata = {
        "set_id": HASH,
        "archive": {"path": "artifacts/synthetic-sealed-reserves/set.zip", "sha256": HASH},
        "chain": {"archive_manifest_sha256": HASH, "case_count": 1},
        "scope": {
            "acceptance_scope": evaluation.ACCEPTANCE_SCOPE,
            "coverage_protocol_sha256": evaluation.COVERAGE_PROTOCOL_SHA256,
        },
    }
    monkeypatch.setattr(evaluation, "bound_reserve_metadata", lambda _admission: metadata)
    retired = []
    monkeypatch.setattr(
        evaluation,
        "record_case_level_disclosure",
        lambda *args, **kwargs: retired.append(kwargs),
    )
    monkeypatch.setattr(
        evaluation, "send_ack",
        lambda _admission, *, write_and_flush, **kwargs: (
            write_and_flush("ACK\n") or AdmissionReceipt(HASH, "ack_intent", "possible", HASH)
        ),
    )
    monkeypatch.setattr(
        evaluation, "fail_admission",
        lambda *_args, **_kwargs: AdmissionReceipt(HASH, "possible_read", "possible", HASH),
    )

    def disclosed(_command, _root, expected, acknowledge, _confirm, **_kwargs):
        acknowledge(expected.attempt_id, "b" * 64, lambda _frame: None)
        raise OcrSealedDisclosureError(("truth", "pixel"), "a" * 64)

    result = evaluation.evaluate_ocr_sealed_candidate(
        root,
        root / "artifacts" / "registry.json",
        expected_registry_sha256=HASH,
        set_id=HASH,
        candidate_path=candidate,
        candidate_sha256=sha256_file(candidate),
        training_authorization=training,
        gate_seal=gate,
        full_ocr_evidence_authenticator=SimpleNamespace(),
        worker_command_prefix=evidence.runtime_command_prefix,
        transport_runner=disclosed,
    )
    assert result.status == "case_data_disclosure"
    assert sorted(retired[0]["disclosures"]) == ["pixel", "truth"]
    outcome = json.loads(result.outcome_path.read_text(encoding="utf-8"))
    assert outcome["aggregate_only"] is False
    assert outcome["failure_code"] == "OCR_SEALED_TRANSPORT_CASE_DATA_DISCLOSURE"


def test_unclassified_output_is_quarantined_without_disclosure_categories(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root, candidate, evidence = _runtime_fixture(tmp_path)
    training, gate, _ = _patched_evaluate(monkeypatch, root, candidate, evidence)
    metadata = {
        "set_id": HASH,
        "archive": {"path": "artifacts/synthetic-sealed-reserves/set.zip", "sha256": HASH},
        "chain": {"archive_manifest_sha256": HASH, "case_count": 1},
        "scope": {"acceptance_scope": evaluation.ACCEPTANCE_SCOPE,
                  "coverage_protocol_sha256": evaluation.COVERAGE_PROTOCOL_SHA256},
    }
    monkeypatch.setattr(evaluation, "bound_reserve_metadata", lambda _admission: metadata)
    monkeypatch.setattr(
        evaluation, "send_ack",
        lambda _admission, *, write_and_flush, **kwargs: (
            write_and_flush("ACK\n") or AdmissionReceipt(HASH, "ack_intent", "possible", HASH)
        ),
    )
    monkeypatch.setattr(
        evaluation, "fail_admission",
        lambda *_args, **_kwargs: AdmissionReceipt(HASH, "possible_read", "possible", HASH),
    )
    quarantined = []
    monkeypatch.setattr(
        evaluation, "record_unclassified_output",
        lambda *args, **kwargs: quarantined.append(kwargs),
    )
    channels = ({"channel": "stderr", "sha256": "e" * 64,
                 "byte_count": 17, "complete": False},)

    def unclassified(_command, _root, expected, acknowledge, _confirm, **_kwargs):
        acknowledge(expected.attempt_id, "b" * 64, lambda _frame: None)
        raise OcrSealedUnclassifiedOutputError(channels)

    result = evaluation.evaluate_ocr_sealed_candidate(
        root, root / "artifacts" / "registry.json",
        expected_registry_sha256=HASH, set_id=HASH, candidate_path=candidate,
        candidate_sha256=sha256_file(candidate), training_authorization=training,
        gate_seal=gate, full_ocr_evidence_authenticator=SimpleNamespace(),
        worker_command_prefix=evidence.runtime_command_prefix,
        transport_runner=unclassified,
    )
    assert result.status == "possible_read"
    assert quarantined == [{"code": "OCR_SEALED_TRANSPORT_UNCLASSIFIED_OUTPUT",
                            "channels": channels}]
    outcome = json.loads(result.outcome_path.read_text(encoding="utf-8"))
    assert outcome["disclosures"] == []
    assert outcome["aggregate_only"] is True


@pytest.mark.parametrize("tamper", [
    None, "arithmetic", "verdict", "read_status", "attempt_id",
    "request_attempt", "completed_hash",
])
def test_resume_finishes_partial_canonical_closure_without_worker(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, tamper
) -> None:
    root, candidate, evidence = _runtime_fixture(tmp_path)
    registry = root / "artifacts" / "registry.json"
    registry.write_text("{}", encoding="utf-8")
    admission = SimpleNamespace(
        admission_id=HASH,
        registry_path=registry,
        training_authorization=SimpleNamespace(),
        gate_seal=SimpleNamespace(),
    )
    directory = root / "artifacts" / "ocr-sealed-evaluations" / HASH
    directory.mkdir(parents=True)
    request_path = directory / "request.json"
    preflight_binding = {
        "candidate_sha256": evidence.candidate_sha256,
        "runtime_identity_sha256": evidence.runtime_identity_sha256,
        "full_ocr_score_sha256": evidence.full_ocr_score_sha256,
    }
    attempt_id = sha256_bytes(canonical_json_bytes(preflight_binding))
    request_path.write_bytes(canonical_json_bytes({
        "schema": evaluation.REQUEST_SCHEMA,
        "attempt_id": attempt_id, "admission_binding_sha256": HASH,
        "candidate_sha256": evidence.candidate_sha256, "set_id": HASH,
        "archive_sha256": HASH, "archive_manifest_sha256": HASH, "source_count": 3,
    }))
    request_sha256 = sha256_file(request_path)
    outcome = evaluation._outcome(
        status="pass", read_status="confirmed", admission=admission,
        attempt_id=attempt_id, preflight_binding_sha256=attempt_id,
        preflight_binding=preflight_binding,
        request_relative=request_path.relative_to(root).as_posix(),
        request_sha256=request_sha256,
        aggregate={"source_count": 3, "panel_count": 9, "metrics": {
            "raw_detector_geometry": _geometry(183),
            "successfully_recognized_region_geometry": _geometry(183),
            "recognition_failures": {"raw_regions_without_successful_recognition": 0},
            "full_ocr_metrics": _full_metrics(183, 1019),
        }},
        verdicts={key: True for key in (
            "text_region_detection_precision", "text_region_detection_recall",
            "recognition_exact_match", "character_error_rate", "role_accuracy",
        )},
        transport=None, failure_code=None,
    )
    if tamper == "arithmetic":
        outcome["aggregate"]["metrics"]["full_ocr_metrics"]["recognition_exact_count"] -= 1
    elif tamper == "verdict":
        outcome["status"] = "fail"
    elif tamper == "read_status":
        outcome["read_status"] = "none"
    elif tamper == "attempt_id":
        outcome["attempt_id"] = "f" * 64
    (directory / "aggregate-outcome.json").write_bytes(canonical_json_bytes(outcome))
    if tamper == "request_attempt":
        request = json.loads(request_path.read_text(encoding="utf-8"))
        request["attempt_id"] = "f" * 64
        request_path.write_bytes(canonical_json_bytes(request))
    monkeypatch.setattr(evaluation, "_canonical_bars", lambda root: _bars())
    monkeypatch.setattr(evaluation, "recover_admission", lambda *args, **kwargs: admission)
    records = iter([
        {"status": "completed" if tamper == "completed_hash" else "confirmed_read",
         "read_status": "confirmed", "aggregate_result_sha256": "f" * 64,
         "attempt_id": attempt_id, "attempt_binding": preflight_binding,
         "admission_id": HASH, "candidate_sha256": evidence.candidate_sha256,
         "disclosures": []},
        {"status": "completed", "read_status": "confirmed",
         "attempt_id": attempt_id, "attempt_binding": preflight_binding,
         "admission_id": HASH, "candidate_sha256": evidence.candidate_sha256,
         "disclosures": []},
    ])
    monkeypatch.setattr(evaluation, "_admission_record", lambda *args: next(records))
    completed = []
    closed = []
    monkeypatch.setattr(
        evaluation, "complete_admission",
        lambda *args, **kwargs: completed.append(kwargs),
    )
    monkeypatch.setattr(
        evaluation, "_close_components",
        lambda *args, **kwargs: closed.append(kwargs),
    )
    if tamper is not None:
        with pytest.raises(evaluation.OcrSealedEvaluationError, match="CLOSURE_CONFLICT"):
            result = evaluation._resume_existing_evaluation(
                root,
                registry,
                existing={"admission_id": HASH, "attempt_id": attempt_id,
                          "attempt_binding_sha256": attempt_id,
                          "attempt_binding": preflight_binding},
                evidence=evidence,
                preflight_binding_sha256=attempt_id,
                preflight_binding=preflight_binding,
                training_authorization=SimpleNamespace(),
                gate_seal=SimpleNamespace(),
            )
        assert not completed and not closed
        return
    result = evaluation._resume_existing_evaluation(
        root,
        registry,
        existing={"admission_id": HASH, "attempt_id": attempt_id,
                  "attempt_binding_sha256": attempt_id,
                  "attempt_binding": preflight_binding},
        evidence=evidence,
        preflight_binding_sha256=attempt_id,
        preflight_binding=preflight_binding,
        training_authorization=SimpleNamespace(),
        gate_seal=SimpleNamespace(),
    )
    assert result.status == "pass"
    assert len(completed) == len(closed) == 1
