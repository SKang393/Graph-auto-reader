# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import numpy as np
import pytest
import torch

from ml.markers.center.residual_negative_v38 import builder


def test_safety_rules_are_strict_and_float32():
    anchors = np.asarray([8.0, 8.0001, 8.1, 8.1], dtype=np.float32)
    decoded = np.asarray([5.1, 5.0, 5.0001, 4.9], dtype=np.float32)
    assert builder.apply_safety_rules(anchors, decoded).tolist() == [False, False, True, False]
    with pytest.raises(ValueError, match="float32"):
        builder.apply_safety_rules(anchors.astype(np.float64), decoded)


def test_exact_dedup_confirms_bytes_after_digest_hits():
    zero = torch.zeros((3, 33, 33), dtype=torch.float32)
    same_zero = zero.clone()
    one = torch.ones((3, 33, 33), dtype=torch.float32)
    entries = [(10, zero), (11, same_zero), (12, one)]
    kept = builder.exact_deduplicate_patches(entries, digest_fn=lambda _raw: b"forced-collision")
    assert kept == [10, 12]


def test_payload_matches_training_cache_contract():
    patches = [torch.full((3, 33, 33), 0.25, dtype=torch.float32) for _ in range(2)]
    payload = builder.build_cache_payload(patches)
    assert list(payload) == ["patches", "labels", "offsets", "radii", "hard_negative"]
    assert payload["patches"].shape == (2, 3, 33, 33)
    assert all(value.dtype == torch.float32 for value in payload.values())
    assert torch.count_nonzero(payload["labels"]) == 0
    assert torch.count_nonzero(payload["offsets"]) == 0
    assert torch.count_nonzero(payload["radii"]) == 0
    assert torch.all(payload["hard_negative"] == 1)


def _eligible_report():
    return {
        "status": "complete_train_only_negative_eligibility_diagnostic",
        "aggregate_counts": {
            "all_v37_exact_match_population": {"no_exact_match_to_any_of_five_groups": 96},
            "eligible_original_patch_rows": 90,
            "eligible_distinct_original_patches_after_exact_self_dedup": 90,
            "eligible_distinct_new_original_patches_after_v37_exclusion": 90,
            "eligible_distinct_original_patches_within_scope": {"component": 50, "family": 40},
            "eligibility_by_scope_and_rule": {
                "component": {"both_rules_pass": 50},
                "family": {"both_rules_pass": 40},
            },
        },
        "eligibility_contract": {"operating_point": 0.1, "miner_confidence_cutoff_applied": False},
        "bindings": {
            "completed_full_v37_match_report_sha256": builder.EXPECTED_V2_REPORT_SHA256,
            "completed_v3_report_sha256": builder.EXPECTED_V3_REPORT_SHA256,
            "fixed_v37_run_report_sha256": builder.EXPECTED_V37_RUN_REPORT_SHA256,
            "fixed_v37_threshold": 0.1,
            "fixed_v37_checkpoint_sha256": builder.EXPECTED_V37_CHECKPOINT_SHA256,
            "negative_miner_source_sha256": builder.EXPECTED_NEGATIVE_MINER_SHA256,
        },
        "execution": {"private_reads": 0, "sealed_reads": 0, "model_inference_runs": 0, "optimizer_steps": 0},
    }


def test_provenance_binding_rejects_checksum_or_population_drift():
    good = _eligible_report()
    builder.validate_eligibility_report(good, builder.EXPECTED_ELIGIBILITY_REPORT_SHA256)
    with pytest.raises(ValueError, match="eligibility report drifted"):
        builder.validate_eligibility_report(good, "0" * 64)
    changed = _eligible_report()
    changed["aggregate_counts"]["eligible_original_patch_rows"] = 91
    with pytest.raises(ValueError, match="eligibility report drifted"):
        builder.validate_eligibility_report(changed, builder.EXPECTED_ELIGIBILITY_REPORT_SHA256)
    changed = _eligible_report()
    changed["execution"]["sealed_reads"] = 1
    with pytest.raises(ValueError, match="prohibited reads"):
        builder.validate_eligibility_report(changed, builder.EXPECTED_ELIGIBILITY_REPORT_SHA256)


def test_protocol_contract_rejects_threshold_or_payload_drift():
    from pathlib import Path
    import json

    protocol_path = Path(__file__).parents[1] / "residual_negative_v38" / "protocol.json"
    protocol = json.loads(protocol_path.read_text(encoding="utf-8-sig"))
    builder.validate_protocol(protocol)
    protocol["eligibility_rules"]["proposal_anchor_distance_pixels"] = ">= 8.0"
    with pytest.raises(ValueError, match="protocol drifted"):
        builder.validate_protocol(protocol)
