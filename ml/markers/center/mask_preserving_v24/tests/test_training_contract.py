# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import json
import hashlib
from pathlib import Path

from ml.markers.center.mask_preserving_v24.train_p1 import (
    ANTI_ALIAS_BLUR_RADII,
    FAMILY_HARD_NEGATIVE_RADIUS_PX,
    RUNNER_SOURCE_PATHS,
    _passes_required_dev_gates,
)
from ml.markers.gate_seal import source_bundle_sha256
from ml.markers.center.real_range_generator_v1.negative_sampler import CONNECTOR_ENDPOINT_OFFSET_PX, TOPOLOGY_HARD_RADIUS_PX, TOPOLOGY_RADIUS_PX, TOPOLOGY_SAMPLER_RADIUS_PX

ROOT = Path(__file__).resolve().parents[5]
REAL_DEV_REPORT_RELATIVE = Path("docs/GOAL-22-PHASE-4-V24-RETRY9-REAL-DEV-STAGES.json")
REAL_DEV_REPORT_PATH = ROOT / REAL_DEV_REPORT_RELATIVE
REAL_DEV_REPORT_SHA256 = "64984bb2ffd25fa596a41965fb4975e6991446fe80cc4df389649303717d5019"
REAL_DEV_BLOCKER = "Retry9 passes synthetic dev but fails the aggregate-only real-dev marker gate at precision 0.07516660639561916 and recall 0.7260479041916168. Aggregate morphology diagnosis measures a 5.496320014967361 real-to-synthetic negative threshold-crossing ratio and confirms sparse, anti-aliased, elongated, off-center negative coverage is still insufficient. Repair the synthetic generator and train-only sampler, re-pass synthetic gates, then run another real-dev check; do not tune thresholds or select candidates on private data."

def test_retry10_training_contract_is_fixed_and_preregistered():
    config = json.loads((ROOT / "ml/markers/center/mask_preserving_v24/training/p1.json").read_text())
    assert config["seed"] == 20260903
    assert config["confidence_threshold"] == 0.25
    assert config["selection_thresholds"] == [0.40, 0.55, 0.70]
    assert config["optimizer_steps_expected"] == 10620
    assert config["optimizer_steps_maximum"] == 10620
    assert config["training_example_count_expected"] == 37741
    assert config["positive_example_count_expected"] == 3431
    assert config["hard_negative_example_count_expected"] == 7202
    assert config["real_range_training_example_count_expected"] == 35838
    assert config["family_training_example_count_expected"] == 1903
    assert config["family_positive_example_count_expected"] == 173
    assert config["family_hard_negative_example_count_expected"] == 346
    assert config["family_hard_negative_radius_px"] == FAMILY_HARD_NEGATIVE_RADIUS_PX
    assert config["retry_count"] == 10
    assert config["retry_reason"] == "add five-axis family-disjoint synthetic coverage after frozen retry9 failed family dev precision, recall, and prohibited-hit gates"
    assert config["negative_sampler"]["total_expected"] == 32580
    assert config["negative_sampler"]["source_sha256"] == "80625357ae4bf6167963c66d0fd6a215fe00c5c936d4542994fb32a275e71a43"
    assert config["negative_sampler"]["selected_index_sha256"] == "d7460b95bbdbb89d79a12cafe7632604f02b8087e9986fb7a9d3ea940287567f"
    assert config["negative_sampler"]["expected_capacities"] == {"artifact": 14469, "faint_low": 8384, "faint_p05": 5497, "generic_connector_band": 50373, "generic": 127516, "hard_existing": 6012, "ocr_heavy": 20547}
    assert config["negative_sampler"]["topology"] == {"radius_px": 12.0, "input_audit_radius_px": 16.0, "expected_capacity": {"topology_junction": 4505, "topology_fragment": 4574}, "expected_selected": {"topology_junction": 4505, "topology_fragment": 4574}, "selected_index_sha256": "671e6e7c7affbbb79171cc31d76863fe8b541904b3727cfd633da2bed7fab95c", "hard": {"radius_px": 4.0, "legacy_capacity": 6012, "expected_capacity": {"topology_junction": 417, "topology_fragment": 484}, "expected_selected": {"topology_junction": 417, "topology_fragment": 484}, "hard_training_total": 6856}}
    assert config["negative_sampler"]["topology"]["radius_px"] == TOPOLOGY_SAMPLER_RADIUS_PX
    assert config["negative_sampler"]["topology"]["input_audit_radius_px"] == TOPOLOGY_RADIUS_PX
    assert config["negative_sampler"]["topology"]["hard"]["radius_px"] == TOPOLOGY_HARD_RADIUS_PX
    assert config["real_range_hard_negative_example_count_expected"] == 6856
    assert sum(config["negative_sampler"]["quotas"].values()) == 32580
    assert config["sealed_runs"] == 0 and config["private_data"] is False
    assert config["real_dev_reads"] == 0 and config["real_sealed_reads"] == 0
    assert config["retry3_morphology_gap_sha256"] == "3b0e9981eb3d21787679f1df1151a3c0bc395ce7966e6c681c6de5755c3fb769"
    assert config["anti_aliasing"] == {"blur_radii_px": [0.0, 0.25, 0.25, 0.25, 0.35, 0.57, 0.57], "scene_index_schedule": "ANTI_ALIAS_BLUR_RADII[index % len(ANTI_ALIAS_BLUR_RADII)]"}
    assert tuple(config["anti_aliasing"]["blur_radii_px"]) == ANTI_ALIAS_BLUR_RADII
    assert config["generator_audit_sha256"] == "1d71d76956e24f0c1a230c9c27e59aecc0d0cd64a04ca9c0d26ef171838ce26b"
    assert config["train_split_sha256"] == "57dc4850c6882ab1ddabe0c4e76bc03f1cb03963c3d0de16b703152071e773d9"
    assert config["dev_split_sha256"] == "72dda9b9031f3050d72f5946105576cad89fe938f36f619f84ef4c9cafa8e566"
    assert config["negative_audit_sha256"] == "364b0e62a5261581bca519e4f740cf9b4c9ec6fc120edee817f0275c1ff224e0"
    assert config["retry7_diagnosis_sha256"] == "1761fd27f0cd1aa9e6a1e3b2b8f0d3c4fa84cb5195dd8c72cea3f17d042683ac"
    assert config["retry7_morphology_diagnosis_sha256"] == "16b9ed50655b6767affdb927106c5b2661d2fce388849d8c80aa5ac0165ebf78"
    assert config["retry7_morphology_gap_sha256"] == "163ae1471792925b6b23c3a6fd26d1ae6d16637864180eaafd179875964afa36"
    assert config["retry8_result_sha256"] == "483f5f989ad73d5280da4cf248cc71c2c2f315dec24827b5cb7a2a59512f37cf"
    assert config["retry8_diagnosis_sha256"] == "0d26acdc0f7eb00b9a053a3bedfbba4f7de4dc755d7a78d3275629e3972577ff"
    assert config["expected_runner_source_bundle_sha256"] == "ca5f214ab8978e91867e4fee2a526e7d7506ef3d3afe8096b0841b5401c59966"
    assert config["retry4_diagnosis_sha256"] == "a19745f7904c8ec316a78a4e220e3133fc5f77fa80f471ed5337976bdbb6594b"
    assert config["retry4_generic_fp_diagnosis_sha256"] == "24d86878dc335803b2aacd6bab5105496cbb2fb51734b4eb0d8ead4feea5d172"
    assert config["retry5_diagnosis_sha256"] == "8f38fd10be6130c34b05aa9544491f59c9c95d8b962bd0010f9dbdf287c8228a"
    assert config["retry5_generic_fp_diagnosis_sha256"] == "701f43ec266ae63689200610ea68d4e5a18b1017fd0951d88f496252ef1076d8"
    assert config["retry6_diagnosis_sha256"] == "34a3bbdf68cd049162b40964ad66c4bfe17cf0f46c306f969497002755e12b0e"
    assert config["negative_sampler"]["connector"] == {"endpoint_offset_px": 8.0, "max_distance_px": 4.0, "target_count": 3674, "expected_capacity": 3671, "expected_selected": 3671, "selected_index_sha256": "fd20045f034c9d5c4882e81b28bfa3f357befe6150183817bdd772f3a04ceef2", "generic_remainder_selected": 2689}
    assert config["negative_sampler"]["generic_connector_band"] == {"radius_px": 4.0, "expected_capacity": 50373, "expected_selected": 6720, "selected_index_sha256": "4e58e9e353a0ff912bccb28845e7e1d619d4903929f9cf49c6244dc5017fc96a"}
    assert config["negative_sampler"]["connector"]["endpoint_offset_px"] == CONNECTOR_ENDPOINT_OFFSET_PX


def test_both_independent_dev_gates_are_required():
    bar = {
        "proposal_recall_minimum": 0.95,
        "precision_minimum": 0.95,
        "recall_minimum": 0.95,
        "prohibited_structure_hit_rate_maximum": 0.02,
    }
    passing = {
        "proposal_recall": 1.0,
        "precision": 0.96,
        "recall": 0.96,
        "prohibited_structure_hit_rate": 0.01,
    }
    failing = {**passing, "precision": 0.94}

    assert _passes_required_dev_gates(passing, passing, bar)
    assert not _passes_required_dev_gates(failing, passing, bar)
    assert not _passes_required_dev_gates(passing, failing, bar)

def test_runner_source_bundle_is_relative_and_present():
    assert all(not path.is_absolute() for path in RUNNER_SOURCE_PATHS)
    assert all((ROOT / path).is_file() for path in RUNNER_SOURCE_PATHS)
    # Retry10 keeps its historical configuration; the coverage candidate binds
    # the current dispatcher and v2 input loader through its separate config.
    config = json.loads((ROOT / "ml/markers/center/mask_preserving_v24/training/p1_retry11.json").read_text())
    ledger = json.loads((ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_text())
    entry = next(item for item in ledger["revisions"] if item["revision"] == config["revision"])
    assert entry["p1_runner_source_bundle_sha256"] == "b8736824df79aadeacded8fec996c932b92f8c1802fd6aced73907958c6f1cf3"
    assert entry["execution_authorized"] is False
    assert entry["authorized_candidate_id"] is None
    assert entry["current_candidate_status"] in {"failed_dev", "dev_passed"}
    assert entry["status"] == "candidate_1_" + entry["current_candidate_status"]
    assert source_bundle_sha256(ROOT, RUNNER_SOURCE_PATHS) == config["expected_runner_source_bundle_sha256"]

def test_current_evidence_bindings_and_authorization_match_files():
    config_path = ROOT / "ml/markers/center/mask_preserving_v24/training/p1_retry11.json"
    config = json.loads(config_path.read_text())
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    for path_key, hash_key in (
        ("morphology_diagnosis_path", "morphology_diagnosis_sha256"),
        ("morphology_gap_path", "morphology_gap_sha256"),
        ("retry3_morphology_gap_path", "retry3_morphology_gap_sha256"),
        ("retry4_diagnosis_path", "retry4_diagnosis_sha256"),
        ("retry4_generic_fp_diagnosis_path", "retry4_generic_fp_diagnosis_sha256"),
        ("retry5_diagnosis_path", "retry5_diagnosis_sha256"),
        ("retry5_generic_fp_diagnosis_path", "retry5_generic_fp_diagnosis_sha256"),
        ("retry6_diagnosis_path", "retry6_diagnosis_sha256"),
    ):
        assert digest(ROOT / config[path_key]) == config[hash_key]
    audit = json.loads((ROOT / config["generator_audit_path"]).read_text())
    assert audit["splits"]["train"]["aggregate_sha256"] == config["train_split_sha256"]
    assert audit["splits"]["dev"]["aggregate_sha256"] == config["dev_split_sha256"]
    assert digest(ROOT / config["negative_sampler"]["source_path"]) == config["negative_sampler"]["source_sha256"]
    assert digest(ROOT / config["family_dev_baseline_path"]) == config["family_dev_baseline_sha256"]
    ledger = json.loads((ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_text())
    entry = next(item for item in ledger["revisions"] if item["revision"] == config["revision"])
    assert digest(config_path) == entry["candidate_config_sha256"]["P1"]
    assert entry["expected_runner_source_bundle_sha256"] == config["expected_runner_source_bundle_sha256"]
    if entry["execution_authorized"]:
        assert digest(ROOT / config["generator_audit_path"]) == config["generator_audit_sha256"]
        assert digest(ROOT / config["negative_audit_path"]) == config["negative_audit_sha256"]
        assert digest(ROOT / "ml/markers/center/real_range_generator_v1/generator.py") == entry["negative_generator_sha256"]
        assert digest(ROOT / "ml/markers/center/real_range_generator_v1/negative_proposal_audit.py") == entry["synthetic_negative_audit_source_sha256"]
        assert digest(ROOT / "ml/markers/center/real_range_generator_v1/AUDIT.json") == entry["negative_generator_audit_sha256"]
        assert digest(ROOT / "ml/markers/center/real_range_generator_v1/NEGATIVE_PROPOSAL_AUDIT.json") == entry["synthetic_negative_audit_sha256"]
    assert entry["synthetic_negative_proposal_count"] == 232798
    assert entry["morphology_diagnosis_sha256"] == config["morphology_diagnosis_sha256"]
    assert entry["morphology_gap_sha256"] == config["morphology_gap_sha256"]
    assert entry["current_candidate_status"] in {"failed_dev", "dev_passed"}
    assert entry["status"] == "candidate_1_" + entry["current_candidate_status"]
    assert entry["execution_authorized"] is False
    assert entry["authorized_candidate_id"] is None
    assert entry["real_dev_authorized"] is False
    assert entry["real_dev_reads"] == 120
    assert entry["real_sealed_authorized"] is False
    assert entry["real_sealed_reads"] == 0
    assert entry["sealed_runs"] == 0
    assert entry["consumed_candidate_ids"] == []
    assert entry["dev_passed_candidate_ids"] == (
        ["P1"] if entry["current_candidate_status"] == "dev_passed" else []
    )
    assert entry["retry9_dev_passed_candidate_ids"] == ["P1"]
    assert entry["candidate_consumed"] is False
    assert entry["p1_result_path"].endswith("P1_RETRY9_RESULT.json")
    assert digest(ROOT / entry["p1_result_path"]) == entry["p1_result_sha256"]
    assert entry["p1_result_sha256"] == "5dee4ac84639597faac2d2bdf4f74db10d8225e858d8f884f34b7c5725dc2255"
    assert entry["p1_candidate_report_path"].endswith("marker-v24-retry9/P1-rerun/candidate-report.json")
    assert digest(ROOT / entry["p1_candidate_report_path"]) == entry["p1_candidate_report_sha256"]
    assert entry["p1_candidate_report_sha256"] == entry["p1_result_sha256"]
    assert entry["retry3_p1_result_path"].endswith("P1_RETRY3_RESULT.json")
    assert entry["retry3_p1_result_sha256"] == "edf2ba744146fdcb6407b68d5765784f733b2f2e8739ecceedfd651edb372711"
    assert entry["retry3_p1_checkpoint_sha256"] == "70b9947bdaa78d5465f7cd2026a4bc00fd3805507551c002daf763e5dbc0b318"
    assert entry["retry3_p1_onnx_sha256"] == "0d80d1994d7b33241c795c9e6f92c802750555a62c3cd3335777eb969fb5083a"
    assert entry["p1_runner_source_bundle_sha256"] == "b8736824df79aadeacded8fec996c932b92f8c1802fd6aced73907958c6f1cf3"
    assert digest(ROOT / entry["retry3_morphology_diagnosis_path"]) == entry["retry3_morphology_diagnosis_sha256"]
    assert entry["retry3_morphology_accepted_generic_false_positives"] == 16
    assert entry["retry3_morphology_scene_count"] == 167
    assert entry["retry3_morphology_optimizer_steps"] == 0
    assert entry["retry3_morphology_private_data"] is False
    assert entry["retry3_morphology_real_dev_reads"] == 0
    assert entry["retry3_morphology_real_sealed_reads"] == 0
    assert entry["retry3_morphology_sealed_runs"] == 0
    assert entry["retry3_morphology_threshold_change_proposed"] is False
    assert digest(ROOT / entry["retry3_morphology_gap_path"]) == entry["retry3_morphology_gap_sha256"]
    assert entry["retry3_morphology_gap_sha256"] == "3b0e9981eb3d21787679f1df1151a3c0bc395ce7966e6c681c6de5755c3fb769"
    assert entry["retry3_morphology_gap_real_dev_projects"] == 120
    assert entry["retry3_morphology_gap_real_dev_failures"] == 0
    assert entry["retry3_morphology_gap_real_sealed_reads"] == 0
    assert entry["retry3_morphology_gap_real_dev_proposals"] == 1358010
    assert entry["retry3_morphology_gap_real_dev_positive_proposals"] == 9849
    assert entry["retry3_morphology_gap_real_dev_negative_below_threshold"] == 1337707
    assert entry["retry3_morphology_gap_real_dev_negative_above_threshold"] == 10454
    assert entry["retry3_morphology_gap_real_dev_runtime_ms"] == 444918.7276
    assert entry["retry3_morphology_gap_synthetic_negative_below_threshold"] == 233674
    assert entry["retry3_morphology_gap_synthetic_negative_above_threshold"] == 646
    assert entry["retry3_morphology_gap_real_to_synthetic_above_rate_ratio"] == 2.812662201375141
    assert entry["retry3_morphology_gap_case_level_output"] is False
    assert entry["retry3_morphology_gap_truth_rows_output"] is False
    assert entry["retry3_morphology_gap_pixel_output"] is False
    assert entry["retry3_morphology_gap_training_use"] is False
    assert entry["retry3_morphology_gap_candidate_selection"] is False
    assert entry["retry4_p1_result_sha256"] == "abcc814dce1d268870d110aaa68775d0cccb5ff96aecfbdd0781e63a5a6bb174"
    assert entry["retry4_p1_checkpoint_sha256"] == "4d98a1de07282f734c6249c164400c237be9560370315b68c4729e9d15e5293c"
    assert entry["retry4_p1_onnx_sha256"] == "697fbcfb961e4c2af36a1a3d68cf5be874412b2939b03c42b59aaa82c4b0de96"
    assert entry["retry5_p1_checkpoint_sha256"] == "a7eaba24b5e65e19c97f303aaf1c5622e5a68a1dabbf44e1df11386c15489832"
    assert entry["retry5_p1_onnx_sha256"] == "d3445f0b1bf0e97a98942133d45341cae75548887be853743e887832cacad7bd"
    assert entry["p1_checkpoint_sha256"] == "357119c66f0c6fba2c73478d458f900521b475c1af8722814fac4e758c61a3b1"
    assert entry["p1_onnx_sha256"] == "4dece2eeb87229d5d57e0d2d714c1915ebecf8e9475b0d466a03dd970993fdb4"
    assert entry["p1_true_positives"] == 1992
    assert entry["p1_false_positives"] == 69
    assert entry["p1_false_negatives"] == 12
    assert entry["p1_precision"] == 0.9665211062590975
    assert entry["p1_recall"] == 0.9940119760479041
    assert entry["p1_f1"] == 0.9800738007380074
    assert entry["p1_prohibited_structure_hits"] == 0
    assert entry["p1_onnx_parity_maximum_absolute_error"] == 9.5367431640625e-07
    assert entry["retry5_p1_opened_seal_sha256"] == "db3c4ded697e108a7af55d3eada605c6bbe3cc0dd17775727fc401c885c41386"
    assert entry["retry5_p1_result_seal_sha256"] == "117bb7a12b5b742a47eb8a3fb3cd8e5899692ba1ec5119a48d5c0432e21a7c40"
    assert entry["p1_opened_seal_sha256"] == "0fc36f3ec59d2ff1c785f926d33aa762a67c336c5f50a97ab5eb195540b6d611"
    assert entry["p1_result_seal_sha256"] == "da9b5bcac78e99923689511b7ea2bc9772644ad0c1e953d4acc9bbfb8ffb12ca"
    assert digest(ROOT / entry["retry6_diagnosis_path"]) == entry["retry6_diagnosis_sha256"]
    assert entry["retry6_accepted_false_positive_generic"] == 85
    assert entry["retry6_accepted_false_positive_topology_junction"] == 1
    assert entry["retry6_accepted_false_positive_connector_anchor"] == 0
    assert entry["retry6_accepted_false_positive_topology_fragment"] == 0
    assert entry["retry6_above_threshold_generic"] == 2171
    assert entry["retry6_above_threshold_artifact"] == 98
    assert entry["retry6_above_threshold_connector_anchor"] == 33
    assert entry["retry6_above_threshold_topology_junction"] == 8
    assert entry["retry6_above_threshold_topology_fragment"] == 3
    assert entry["retry6_prohibited_hit_kind"] == "topology_junction"
    assert entry["retry6_prohibited_hit_source"] == "topology_junction"
    assert entry["retry6_prohibited_hit_confidence"] == 0.3208221197128296
    assert entry["retry6_prohibited_hit_distance_px"] == 1.9674727110252526
    assert entry["retry6_diagnosis_topology_input_audit_radius_px"] == 16.0
    assert digest(ROOT / entry["retry4_diagnosis_path"]) == entry["retry4_diagnosis_sha256"]
    assert digest(ROOT / entry["retry4_generic_fp_diagnosis_path"]) == entry["retry4_generic_fp_diagnosis_sha256"]
    assert entry["retry4_accepted_false_positive_count"] == 138
    assert entry["retry4_accepted_false_positive_generic"] == 136
    assert entry["retry4_accepted_false_positive_artifact"] == 1
    assert entry["retry4_accepted_false_positive_topology_junction"] == 1
    assert entry["retry4_accepted_false_positive_topology_fragment"] == 0
    assert entry["retry4_topology_above_threshold_junction"] == 1
    assert entry["retry4_topology_capacity_junction"] == 8331
    assert entry["retry4_topology_above_threshold_fragment"] == 0
    assert entry["retry4_topology_capacity_fragment"] == 8049
    assert entry["retry4_generic_root_cause_connecting_line"] == 102
    assert entry["retry4_generic_root_cause_masked_context"] == 13
    assert entry["retry4_generic_root_cause_marker_field"] == 21
    assert entry["retry4_generic_root_cause_exhaustive_count"] == 136
    assert digest(ROOT / entry["retry5_diagnosis_path"]) == entry["retry5_diagnosis_sha256"]
    assert digest(ROOT / entry["retry5_generic_fp_diagnosis_path"]) == entry["retry5_generic_fp_diagnosis_sha256"]
    assert entry["retry5_accepted_false_positive_count"] == 184
    assert entry["retry5_accepted_false_positive_generic"] == 183
    assert entry["retry5_accepted_false_positive_topology_fragment"] == 1
    assert entry["retry5_accepted_false_positive_topology_junction"] == 0
    assert entry["retry5_accepted_false_positive_connector_anchor"] == 0
    assert entry["retry5_above_threshold_generic"] == 4818
    assert entry["retry5_above_threshold_artifact"] == 169
    assert entry["retry5_above_threshold_connector_anchor"] == 4
    assert entry["retry5_above_threshold_ocr"] == 2
    assert entry["retry5_above_threshold_topology_fragment"] == 1
    assert entry["retry5_above_threshold_topology_junction"] == 0
    assert entry["retry5_generic_root_cause_near_connecting_line"] == 115
    assert entry["retry5_generic_root_cause_masked_context"] == 23
    assert entry["retry5_generic_root_cause_marker_field"] == 45
    assert entry["retry5_generic_root_cause_exhaustive_count"] == 183
    assert entry["p1_dev_gate_passed"] is True
    assert entry["negative_sampler_selected_index_sha256"] == "d7460b95bbdbb89d79a12cafe7632604f02b8087e9986fb7a9d3ea940287567f"
    assert entry["negative_sampler_connector_endpoint_offset_px"] == 8.0
    assert entry["negative_sampler_connector_anchor_max_distance_px"] == 4.0
    assert entry["negative_sampler_connector_anchor_target_count"] == 3674
    assert entry["negative_sampler_connector_anchor_capacity"] == 3671
    assert entry["negative_sampler_connector_anchor_selected"] == 3671
    assert entry["negative_sampler_connector_anchor_selected_index_sha256"] == "fd20045f034c9d5c4882e81b28bfa3f357befe6150183817bdd772f3a04ceef2"
    assert entry["negative_sampler_topology_radius_px"] == 12.0
    assert entry["negative_sampler_topology_input_audit_radius_px"] == 16.0
    assert entry["negative_sampler_topology_capacity"] == {"topology_junction": 4505, "topology_fragment": 4574}
    assert entry["negative_sampler_topology_selected"] == {"topology_junction": 4505, "topology_fragment": 4574}
    assert entry["negative_sampler_topology_selected_index_sha256"] == "671e6e7c7affbbb79171cc31d76863fe8b541904b3727cfd633da2bed7fab95c"
    assert entry["negative_sampler_topology_hard_radius_px"] == 4.0
    assert entry["negative_sampler_topology_hard_legacy_capacity"] == 6012
    assert entry["negative_sampler_topology_hard_capacity"] == {"topology_junction": 417, "topology_fragment": 484}
    assert entry["negative_sampler_topology_hard_selected"] == {"topology_junction": 417, "topology_fragment": 484}
    assert entry["negative_sampler_hard_training_total"] == 6856
    assert entry["negative_sampler_generic_remainder_selected"] == 2689
    assert entry["generic_connector_band_radius_px"] == 4.0
    assert entry["generic_connector_band_capacity"] == 50373
    assert entry["generic_connector_band_selected"] == 6720
    assert entry["generic_connector_band_selected_index_sha256"] == "4e58e9e353a0ff912bccb28845e7e1d619d4903929f9cf49c6244dc5017fc96a"
    assert entry["real_dev_result_path"] == "docs/GOAL-22-PHASE-4-V24-RETRY9-REAL-DEV-STAGES.json"
    assert entry["real_dev_result_sha256"] == REAL_DEV_REPORT_SHA256
    assert digest(ROOT / entry["real_dev_result_path"]) == entry["real_dev_result_sha256"]
    assert entry["real_dev_projects"] == 120
    assert entry["real_dev_successful_projects"] == 120
    assert entry["real_dev_failure_count"] == 0
    assert entry["real_dev_true_positives"] == 1455
    assert entry["real_dev_false_positives"] == 17902
    assert entry["real_dev_false_negatives"] == 549
    assert entry["real_dev_precision"] == 0.07516660639561916
    assert entry["real_dev_recall"] == 0.7260479041916168
    assert entry["real_dev_pre_nms_true_positives"] == 1586
    assert entry["real_dev_pre_nms_false_positives"] == 48994
    assert entry["real_dev_pre_nms_false_negatives"] == 418
    assert entry["real_dev_above_threshold_outputs"] == 51444
    assert entry["real_dev_above_threshold_true_positives"] == 1586
    assert entry["real_dev_above_threshold_false_positives"] == 49858
    assert entry["real_dev_final_outputs"] == 19357
    assert entry["real_dev_elapsed_ms"] == 449148.3793999999
    assert entry["real_dev_model_sha256"] == "4dece2eeb87229d5d57e0d2d714c1915ebecf8e9475b0d466a03dd970993fdb4"
    assert entry["real_dev_case_level_output"] is False
    assert entry["real_dev_truth_rows_output"] is False
    assert entry["real_dev_pixel_output"] is False
    assert entry["real_dev_training_use"] is False
    assert entry["real_dev_candidate_selection"] is False
    assert entry["retry3_vs_retry2_precision_delta"] == 0.03542454589060041
    assert entry["retry3_vs_retry2_recall_delta"] == -0.04940119760479042
    assert entry["retry3_vs_retry2_false_positives_delta"] == -2808
    assert entry["retry3_vs_retry2_above_threshold_false_candidates_delta"] == -9920
    assert entry["spatial_morphology_diagnostic_required"] is False
    assert entry["retry7_morphology_diagnosis_required"] is False
    assert entry["retry7_vs_retry3_precision_delta"] == -0.09009361841827799
    assert entry["retry7_vs_retry3_recall_delta"] == 0.190119760479042
    assert entry["retry7_vs_retry3_false_positives_delta"] == 15134
    assert entry["retry7_vs_retry3_above_threshold_false_candidates_delta"] == 55302
    assert entry["retry3_real_dev_result_path"].endswith("V24-RETRY3-REAL-DEV-STAGES.json")
    assert entry["retry3_real_dev_model_sha256"] == "0d80d1994d7b33241c795c9e6f92c802750555a62c3cd3335777eb969fb5083a"
    assert entry["retry2_dev_gate_passed"] is True
    assert entry["retry9_execution_blocker"] == REAL_DEV_BLOCKER

def test_retry9_real_dev_report_is_clone_safe_aggregate_only_and_bound():
    report = json.loads(REAL_DEV_REPORT_PATH.read_text())
    ledger = json.loads((ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_text())
    entry = next(item for item in ledger["revisions"] if item["revision"] == "marker-center-mask-preserving-v24")
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

    assert not REAL_DEV_REPORT_RELATIVE.is_absolute()
    assert REAL_DEV_REPORT_PATH.is_file()
    assert digest(REAL_DEV_REPORT_PATH) == REAL_DEV_REPORT_SHA256
    assert entry["real_dev_result_path"] == "docs/GOAL-22-PHASE-4-V24-RETRY9-REAL-DEV-STAGES.json"
    assert entry["real_dev_result_sha256"] == REAL_DEV_REPORT_SHA256
    assert report["real_dev_projects"] == 120
    assert report["successful_projects"] == 120
    assert report["real_sealed_reads"] == 0
    assert report["true_positives"] == 1455
    assert report["false_positives"] == 17902
    assert report["false_negatives"] == 549
    assert report["precision"] == 0.07516660639561916
    assert report["recall"] == 0.7260479041916168
    assert report["stage_counters"]["outputs_above_0_25"] == 51444
    assert report["stage_counters"]["final_candidates"] == 19357
    assert report["pre_nms_true_positives"] == 1586
    assert report["pre_nms_false_positives"] == 48994
    assert report["pre_nms_false_negatives"] == 418
    assert report["total_runtime_ms"] == 449148.3793999999
    assert report["model_sha256"] == "4dece2eeb87229d5d57e0d2d714c1915ebecf8e9475b0d466a03dd970993fdb4"
    assert report["case_level_output"] is False
    assert report["truth_rows_output"] is False
    assert report["pixel_output"] is False
    assert report["training_use"] is False
    assert report["candidate_selection"] is False
    assert entry["retry9_status"] == "dev_passed_retry9_unconsumed_real_dev_failed"
    assert entry["retry9_status"] == "dev_passed_retry9_unconsumed_real_dev_failed"
    assert entry["candidate_consumed"] is False
    assert entry["retry9_execution_authorized"] is False
    assert entry["real_dev_authorized"] is False
    assert entry["real_dev_reads"] == 120
    assert entry["real_sealed_authorized"] is False
    assert entry["real_sealed_reads"] == 0
    assert entry["production_approval"] is False
    assert entry["retry9_execution_blocker"] == REAL_DEV_BLOCKER

    morphology_path = ROOT / entry["retry9_morphology_diagnosis_path"]
    morphology = json.loads(morphology_path.read_text())
    assert digest(morphology_path) == "a6a2c23f706722c9e5e26af3a4c14ab7f92f9e0f21e7329c9bb03f5112b5b4de"
    assert morphology["scope"]["synthetic_only"] is True
    assert morphology["scope"]["positive_label_distance_px"] == 3.0
    assert morphology["scope"]["real_dev_reads"] == 0
    assert morphology["scope"]["real_sealed_reads"] == 0
    assert morphology["negative_strata_counts"]["generic_connector_band"]["above_threshold"] == 965

    gap_path = ROOT / entry["retry9_morphology_gap_path"]
    gap = json.loads(gap_path.read_text())
    assert digest(gap_path) == entry["retry9_morphology_gap_sha256"] == "d6a137fd50c84a5442f42f1e80e0b951d3c66a82822b0b4c33455791593ef56a"
    assert gap["scope"]["real_sealed_reads"] == 0
    assert gap["scope"]["optimizer_steps_on_private_data"] == 0
    assert gap["scope"]["candidate_selected_on_real_dev"] is False
    assert gap["rate_comparison"]["real_to_synthetic_rate_ratio"] == 5.496320014967361
    assert gap["real_dev_morphology_run"]["negative_above_threshold_count"] == 48129
    assert gap["comparison_constraints"]["label_radius_difference"].startswith("Synthetic proposal labels use a 3-pixel radius")
    assert "not an aligned-radius causal test" in gap["comparison_constraints"]["interpretation"]
    assert entry["retry9_morphology_real_sealed_reads"] == 0
    assert entry["retry9_morphology_threshold_change_proposed"] is False
    assert entry["retry9_morphology_candidate_selection"] is False

def test_retry8_failed_dev_binding_is_unconsumed_and_closed():
    result_path = ROOT / "ml/markers/center/mask_preserving_v24/P1_RETRY8_RESULT.json"
    result = json.loads(result_path.read_text())
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    ledger = json.loads((ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_text())
    entry = next(item for item in ledger["revisions"] if item["revision"] == result["revision"])
    assert digest(result_path) == "483f5f989ad73d5280da4cf248cc71c2c2f315dec24827b5cb7a2a59512f37cf"
    assert result["status"] == "failed_dev"
    assert result["synthetic_only"] is True
    assert result["private_data"] is False
    assert result["selected"] == {
        "duplicate_count": 0,
        "f1": 0.953750299544692,
        "false_negatives": 14,
        "false_positives": 179,
        "precision": 0.9174734900875979,
        "prohibited_structure_hits": 0,
        "proposal_recall": 1.0,
        "proposal_true_positives": 2004,
        "recall": 0.9930139720558883,
        "scene_count": 167,
        "threshold": 0.25,
        "true_positives": 1990,
    }
    assert result["checkpoint_sha256"] == "f66e8d83a14d48220468f7af934130660ac78dd901c04e1d48ade157b385e2d2"
    assert result["onnx_sha256"] == "d6f8e9bc64c34f1bb646b6d150e1ccead45e26684836a413a5b904da7f40b5ab"
    assert result["optimizer_steps"] == 10080
    assert result["elapsed_ms"] == 1479275.054
    assert result["onnx_parity_maximum_absolute_error"] == 1.430511474609375e-06
    assert result["real_dev_reads"] == 0
    assert result["real_sealed_reads"] == 0
    assert result["sealed_runs"] == 0
    assert entry["retry9_status"] == "dev_passed_retry9_unconsumed_real_dev_failed"
    assert entry["retry9_execution_authorized"] is False
    assert entry["retry9_authorized_candidate_id"] is None
    assert entry["consumed_candidate_ids"] == []
    assert entry["retry9_dev_passed_candidate_ids"] == ["P1"]
    assert entry["candidate_consumed"] is False
    assert entry["p1_dev_gate_passed"] is True
    assert entry["real_dev_reads"] == 120
    assert entry["real_dev_authorized"] is False
    assert entry["real_sealed_reads"] == 0
    assert entry["real_sealed_authorized"] is False
    assert entry["sealed_runs"] == 0
    assert entry["public_gate_authorized"] is False
    assert entry["production_approval"] is False
    assert entry["release_eligible"] is False
    assert entry["retry8_p1_result_sha256"] == "483f5f989ad73d5280da4cf248cc71c2c2f315dec24827b5cb7a2a59512f37cf"
    assert entry["retry8_p1_checkpoint_sha256"] == "f66e8d83a14d48220468f7af934130660ac78dd901c04e1d48ade157b385e2d2"
    assert entry["retry8_p1_onnx_sha256"] == "d6f8e9bc64c34f1bb646b6d150e1ccead45e26684836a413a5b904da7f40b5ab"
    assert entry["retry8_p1_opened_seal_sha256"] == "7d462c8c400a5d2b021ee239e5e192d96dfebdf2415c82d5a5e5fadff1a9832a"
    assert entry["retry8_p1_result_seal_sha256"] == "bf3a08ade1ebce70cda5028424c8233f2dcce1f0b35bbf42de756d60d5dd3633"
    assert entry["retry8_p1_runner_source_bundle_sha256"] == "ffd479f41f0fe6525b24e1ac6df1d2e2acd187d58313b526feea3e1c4008dab7"
    diagnosis_path = ROOT / "ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY8_DIAGNOSIS.json"
    diagnosis = json.loads(diagnosis_path.read_text())
    assert digest(diagnosis_path) == "0d26acdc0f7eb00b9a053a3bedfbba4f7de4dc755d7a78d3275629e3972577ff"
    assert diagnosis["binding"]["configuration_sha256"] == "80624cf563b4a547b8c81c5021b785da9cfee8739b4e512ab33c79d1bd7fdb88"
    assert diagnosis["binding"]["runner_source_bundle_sha256"] == "ffd479f41f0fe6525b24e1ac6df1d2e2acd187d58313b526feea3e1c4008dab7"
    assert diagnosis["binding"]["opened_seal_sha256"] == "7d462c8c400a5d2b021ee239e5e192d96dfebdf2415c82d5a5e5fadff1a9832a"
    assert diagnosis["scope"]["scene_count"] == 167
    assert diagnosis["scope"]["threshold"] == 0.25
    assert diagnosis["scope"]["optimizer_steps"] == 0
    assert diagnosis["scope"]["private_data"] is False
    assert diagnosis["scope"]["real_dev_reads"] == 0
    assert diagnosis["scope"]["real_sealed_reads"] == 0
    assert diagnosis["fixed_threshold_metrics"]["true_positives"] == 1990
    assert diagnosis["fixed_threshold_metrics"]["false_positives"] == 179
    assert diagnosis["fixed_threshold_metrics"]["false_negatives"] == 14
    assert diagnosis["fixed_threshold_metrics"]["precision"] == 0.9174734900875979
    assert diagnosis["fixed_threshold_metrics"]["recall"] == 0.9930139720558883
    assert diagnosis["fixed_threshold_metrics"]["prohibited_structure_hits"] == 0
    assert entry["retry8_diagnosis_path"].endswith("V24_RETRY8_DIAGNOSIS.json")
    assert entry["retry8_diagnosis_sha256"] == "0d26acdc0f7eb00b9a053a3bedfbba4f7de4dc755d7a78d3275629e3972577ff"
    assert entry["retry8_diagnosis_scene_count"] == 167
    assert entry["retry8_diagnosis_threshold"] == 0.25
    assert entry["retry8_diagnosis_optimizer_steps"] == 0
    assert entry["retry8_diagnosis_private_data"] is False
    assert entry["retry8_diagnosis_real_dev_reads"] == 0
    assert entry["retry8_diagnosis_real_sealed_reads"] == 0
    assert entry["retry8_diagnosis_sealed_runs"] == 0
    assert entry["retry8_accepted_false_positive_generic"] == 171
    assert entry["retry8_accepted_false_positive_artifact"] == 1
    assert entry["retry8_accepted_false_positive_connector_anchor"] == 1
    assert entry["retry8_accepted_false_positive_topology_fragment"] == 4
    assert entry["retry8_accepted_false_positive_topology_junction"] == 2
    assert entry["retry8_prohibited_structure_hits"] == 0
    assert entry["retry9_execution_blocker"] == REAL_DEV_BLOCKER

def test_retry9_void_records_sampler_capacity_mismatch_without_consuming_budget():
    config_path = ROOT / "ml/markers/center/mask_preserving_v24/training/p1.json"
    config = json.loads(config_path.read_text())
    ledger = json.loads((ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_text())
    entry = next(item for item in ledger["revisions"] if item["revision"] == config["revision"])
    assert config["negative_sampler"]["expected_capacities"]["generic"] == 127516
    assert config["negative_sampler"]["source_sha256"] == "80625357ae4bf6167963c66d0fd6a215fe00c5c936d4542994fb32a275e71a43"
    assert entry["retry9_void_report_path"].endswith("marker-v24-retry9/P1-run/candidate-report.json")
    assert entry["retry9_void_report_sha256"] == "2ff09a580e132555b5b6536e632cdc082710420921c8ba131c92ee0d8fdf486e"
    assert entry["retry9_void_seal_path"].endswith("P1/void-attempts/daafc116b94145f784240a8d824d5955/void.json")
    assert entry["retry9_void_seal_sha256"] == "5e6554a33929075606c948a95a595f8be163a10d72e6502ae2361582f5a746d8"
    assert entry["retry9_void_phase"] == "initialization"
    assert entry["retry9_void_exception_type"] == "RuntimeError"
    assert entry["retry9_void_exception_message"] == "negative sampler contract changed"
    assert entry["retry9_void_sealed_split_read"] is False
    assert entry["retry9_void_budget_consumed"] is False
    assert entry["retry9_void_optimizer_steps"] == 0
    assert entry["retry9_void_configured_generic_capacity"] == 177889
    assert entry["retry9_void_actual_generic_capacity"] == 127516
    assert entry["retry9_status"] == "dev_passed_retry9_unconsumed_real_dev_failed"
    assert entry["retry9_execution_authorized"] is False
    assert entry["retry9_authorized_candidate_id"] is None
    assert entry["candidate_consumed"] is False
    assert entry["consumed_candidate_ids"] == []
    assert entry["retry9_dev_passed_candidate_ids"] == ["P1"]
    assert entry["p1_dev_gate_passed"] is True
    assert entry["real_dev_reads"] == 120
    assert entry["real_sealed_reads"] == 0
    assert entry["sealed_runs"] == 0
    assert entry["p1_void_attempt_count"] == 2
    assert entry["p1_void_record_sha256"] == "5e6554a33929075606c948a95a595f8be163a10d72e6502ae2361582f5a746d8"
    assert entry["retry9_void_report_sha256"] == "2ff09a580e132555b5b6536e632cdc082710420921c8ba131c92ee0d8fdf486e"
    assert entry["retry9_void_seal_sha256"] == "5e6554a33929075606c948a95a595f8be163a10d72e6502ae2361582f5a746d8"
    assert entry["retry9_void_configured_generic_capacity"] == 177889
    assert entry["retry9_void_actual_generic_capacity"] == 127516
    assert entry["negative_sampler_capacities"]["generic"] == 127516
    assert entry["retry8_negative_sampler_capacities"]["generic"] == 177889
    assert entry["retry9_execution_blocker"] == REAL_DEV_BLOCKER

def test_train_examples_preserve_mask_crossing_positive():
    from ml.markers.center.mask_preserving_v24.train_p1 import _examples
    from ml.markers.center.real_range_generator_v1.generator import build_split
    import torch
    scene = next(
        item for item in build_split("train")
        if any(
            float(item.tensor[1, int(y)-2:int(y)+3, int(x)-2:int(x)+3].max()) >= .35
            for x, y in item.centers
        )
    )
    patches, labels, _, radii, _ = _examples((scene,), 10, torch.Generator().manual_seed(20260904))
    assert patches.shape[1:] == (3, 33, 33)
    assert bool((labels > 0.5).any())
    assert bool((patches[labels > 0.5, 1].sum(dim=(1,2)) > 0).any())
    assert float(radii.max()) <= max(scene.diameters) / 2.0

def test_retry7_records_unconsumed_synthetic_dev_pass():
    result_path = ROOT / "ml/markers/center/mask_preserving_v24/P1_RETRY7_RESULT.json"
    result = json.loads(result_path.read_text())
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    ledger = json.loads((ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_text())
    entry = next(item for item in ledger["revisions"] if item["revision"] == result["revision"])
    assert result["status"] == "dev_passed"
    assert result["synthetic_only"] is True
    assert result["private_data"] is False
    assert result["checkpoint_sha256"] == "a66085d55d9d361a9d98db6105e3dcf2269dbff103cc542e91ff9fbf4fd0d350"
    assert result["onnx_sha256"] == "7932b008a9c4372c832215f2f8732c59c59012a25aa4ad2d12cfeaed404bbe3c"
    assert result["selected"] == {
        "duplicate_count": 0,
        "f1": 0.9853996535511013,
        "false_negatives": 13,
        "false_positives": 46,
        "precision": 0.9774177712322042,
        "prohibited_structure_hits": 0,
        "proposal_recall": 1.0,
        "proposal_true_positives": 2004,
        "recall": 0.9935129740518962,
        "scene_count": 167,
        "threshold": 0.25,
        "true_positives": 1991,
    }
    assert result["optimizer_steps"] == 10080
    assert result["hard_negative_example_count"] == 6856
    assert result["onnx_parity_maximum_absolute_error"] == 4.76837158203125e-07
    assert result["elapsed_ms"] == 1468203.664
    assert result["real_dev_reads"] == 0
    assert result["real_sealed_reads"] == 0
    assert result["sealed_runs"] == 0
    assert digest(result_path) == "6fc74bc7e0aa6c36d7dd0aac51af014ad5875261f9e7a1cb113e13727287d9be"
    assert entry["retry9_status"] == "dev_passed_retry9_unconsumed_real_dev_failed"
    assert entry["retry9_dev_passed_candidate_ids"] == ["P1"]
    assert entry["consumed_candidate_ids"] == []
    assert entry["candidate_consumed"] is False
    assert entry["retry9_execution_authorized"] is False
    assert entry["retry9_authorized_candidate_id"] is None
    assert entry["real_dev_authorized"] is False
    assert entry["real_dev_reads"] == 120
    assert entry["real_dev_gate_passed"] is False
    assert entry["real_sealed_authorized"] is False
    assert entry["real_sealed_reads"] == 0
    assert entry["sealed_runs"] == 0
    assert entry["public_gate_authorized"] is False
    assert entry["public_gate_evaluations"] == 0
    assert entry["production_approval"] is False
    assert entry["release_eligible"] is False
    assert entry["p1_result_path"].endswith("P1_RETRY9_RESULT.json")
    assert entry["p1_result_sha256"] == "5dee4ac84639597faac2d2bdf4f74db10d8225e858d8f884f34b7c5725dc2255"
    assert digest(ROOT / entry["p1_result_path"]) == entry["p1_result_sha256"]
    assert entry["p1_checkpoint_sha256"] == "357119c66f0c6fba2c73478d458f900521b475c1af8722814fac4e758c61a3b1"
    assert entry["p1_onnx_sha256"] == "4dece2eeb87229d5d57e0d2d714c1915ebecf8e9475b0d466a03dd970993fdb4"
    assert entry["p1_opened_seal_sha256"] == "0fc36f3ec59d2ff1c785f926d33aa762a67c336c5f50a97ab5eb195540b6d611"
    assert entry["p1_result_seal_sha256"] == "da9b5bcac78e99923689511b7ea2bc9772644ad0c1e953d4acc9bbfb8ffb12ca"
    assert entry["retry6_p1_result_sha256"] == "610487133e59a71b457c261cbffa0af9d64ddbdac96e5e5697b3f411a761c3a7"
    assert entry["retry6_p1_checkpoint_sha256"] == "e23503d79ca58c535fcdcff5cf344d87205e3e2ad901c208d7a4049dc530d5e8"
    assert entry["retry6_p1_onnx_sha256"] == "31d473d6c24bf21edc1cbfb25f7da35eabfed7cbf8afc13bf52bef23d06bfeb9"
    diagnosis_path = ROOT / "ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY7_DIAGNOSIS.json"
    diagnosis = json.loads(diagnosis_path.read_text())
    assert digest(diagnosis_path) == "1761fd27f0cd1aa9e6a1e3b2b8f0d3c4fa84cb5195dd8c72cea3f17d042683ac"
    assert entry["retry7_diagnosis_path"].endswith("V24_RETRY7_DIAGNOSIS.json")
    assert entry["retry7_diagnosis_sha256"] == "1761fd27f0cd1aa9e6a1e3b2b8f0d3c4fa84cb5195dd8c72cea3f17d042683ac"
    assert digest(ROOT / entry["retry7_diagnosis_path"]) == entry["retry7_diagnosis_sha256"]
    assert diagnosis["scope"] == {
        "case_ids_or_pixels_emitted": False,
        "label_positive_distance_px": 3.0,
        "optimizer_steps": 0,
        "private_data": False,
        "real_dev_reads": 0,
        "real_sealed_reads": 0,
        "retry_mode": "retry7",
        "scene_count": 167,
        "split": "real-range-generator-v1-dev",
        "synthetic_only": True,
        "threshold": 0.25,
        "truth_count": 2004,
    }
    assert diagnosis["fixed_threshold_metrics"] == {
        "accepted": 2037,
        "accepted_false_positive_attribution": {"generic": 46},
        "false_negatives": 13,
        "false_positives": 46,
        "precision": 0.9774177712322042,
        "prohibited_structure_hits": 0,
        "recall": 0.9935129740518962,
        "true_positives": 1991,
    }
    assert diagnosis["prohibited_hit_attribution"] == {
        "by_prohibited_kind": {},
        "by_source_group": {},
        "total": 0,
    }
    assert entry["retry7_diagnosis_scene_count"] == 167
    assert entry["retry7_diagnosis_threshold"] == 0.25
    assert entry["retry7_diagnosis_optimizer_steps"] == 0
    assert entry["retry7_diagnosis_private_data"] is False
    assert entry["retry7_diagnosis_real_dev_reads"] == 0
    assert entry["retry7_diagnosis_real_sealed_reads"] == 0
    assert entry["retry7_diagnosis_sealed_runs"] == 0
    assert entry["retry7_diagnosis_threshold_change_proposed"] is False
    assert entry["retry7_accepted_false_positive_generic"] == 46
    assert entry["retry7_accepted_false_positive_topology_junction"] == 0
    assert entry["retry7_accepted_false_positive_topology_fragment"] == 0
    assert entry["retry7_accepted_false_positive_connector_anchor"] == 0
    assert entry["retry7_prohibited_structure_hits"] == 0
    assert entry["p1_runner_source_bundle_sha256"] == "b8736824df79aadeacded8fec996c932b92f8c1802fd6aced73907958c6f1cf3"
    assert entry["retry7_p1_runner_source_bundle_sha256"] == "f884c1cbaa51ff8a0a859cf89662c9bba3dcc7c94a0f2d920184ddbcdab68951"
    real_dev_path = ROOT / "docs/GOAL-22-PHASE-4-V24-RETRY7-REAL-DEV-STAGES.json"
    real_dev = json.loads(real_dev_path.read_text())
    assert digest(real_dev_path) == "a127305927e73f73450c351a60d6835e90f9125468a7bcb7f7a098cfe80ce4ff"
    assert real_dev["real_dev_projects"] == 120
    assert real_dev["successful_projects"] == 120
    assert real_dev["failure_count"] == 0
    assert real_dev["real_sealed_reads"] == 0
    assert real_dev["true_positives"] == 1484
    assert real_dev["false_positives"] == 21112
    assert real_dev["false_negatives"] == 520
    assert real_dev["precision"] == 0.06567534076827757
    assert real_dev["recall"] == 0.7405189620758483
    assert real_dev["stage_counters"]["outputs_above_0_25"] == 68275
    assert real_dev["stage_counters"]["final_candidates"] == 22596
    assert real_dev["pre_nms_true_positives"] == 1619
    assert real_dev["pre_nms_false_positives"] == 65987
    assert real_dev["pre_nms_false_negatives"] == 385
    assert real_dev["above_threshold_decoded_match"]["true_positives"] == 1619
    assert real_dev["above_threshold_decoded_match"]["false_positives"] == 66656
    assert real_dev["above_threshold_decoded_match"]["false_negatives"] == 385
    assert real_dev["total_runtime_ms"] == 452598.69680000015
    assert real_dev["model_sha256"] == "7932b008a9c4372c832215f2f8732c59c59012a25aa4ad2d12cfeaed404bbe3c"
    assert real_dev["case_level_output"] is False
    assert real_dev["truth_rows_output"] is False
    assert real_dev["pixel_output"] is False
    assert real_dev["training_use"] is False
    assert real_dev["candidate_selection"] is False
    morphology_path = ROOT / "ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY7_MORPHOLOGY_DIAGNOSIS.json"
    morphology = json.loads(morphology_path.read_text())
    assert digest(morphology_path) == "16b9ed50655b6767affdb927106c5b2661d2fce388849d8c80aa5ac0165ebf78"
    assert morphology["accepted_generic_false_positive_count"] == 46
    assert morphology["binding"]["model_sha256"] == "7932b008a9c4372c832215f2f8732c59c59012a25aa4ad2d12cfeaed404bbe3c"
    assert morphology["scope"]["scene_count"] == 167
    assert morphology["scope"]["threshold"] == 0.25
    assert morphology["scope"]["optimizer_steps"] == 0
    assert morphology["scope"]["private_data"] is False
    assert morphology["scope"]["real_dev_reads"] == 0
    assert morphology["scope"]["real_sealed_reads"] == 0
    assert morphology["scope"]["synthetic_only"] is True
    assert morphology["threshold_change_proposed"] is False
    gap_path = ROOT / "docs/GOAL-22-PHASE-4R-V24-RETRY7-MORPHOLOGY-GAP.json"
    gap = json.loads(gap_path.read_text())
    assert digest(gap_path) == "163ae1471792925b6b23c3a6fd26d1ae6d16637864180eaafd179875964afa36"
    assert gap["real_dev_morphology_run"] == {
        "successful_projects": 120,
        "failure_count": 0,
        "proposal_count": 1358010,
        "positive_proposal_count": 9849,
        "negative_below_threshold_count": 1283477,
        "negative_above_threshold_count": 64684,
        "mean_project_runtime_ms": 3828.7179225000004,
        "total_runtime_ms": 459446.15070000006,
        "case_level_output": False,
        "truth_rows_output": False,
        "pixel_output": False,
        "training_use": False,
        "candidate_selection": False,
    }
    assert gap["rate_comparison"]["synthetic_negative_below_threshold_count"] == 229465
    assert gap["rate_comparison"]["synthetic_negative_above_threshold_count"] == 1746
    assert gap["rate_comparison"]["real_to_synthetic_rate_ratio"] == 6.353592565545439
    assert gap["confidence_comparison"]["synthetic_positive_probability"]["median"] == 0.9933367967605591
    assert gap["confidence_comparison"]["real_positive_probability"]["median"] == 0.07347214221954346
    assert gap["scope"]["real_sealed_reads"] == 0
    assert gap["scope"]["optimizer_steps_on_private_data"] == 0
    assert gap["scope"]["threshold_change_proposed"] is False
    assert gap["scope"]["candidate_selected_on_real_dev"] is False
    assert gap["diagnosis"]["responsible_subsystem"] == "synthetic marker proposal generator"
    assert "anti-aliased marker and line rendering" in gap["diagnosis"]["permitted_next_change"]
    assert entry["retry7_morphology_diagnosis_path"].endswith("V24_RETRY7_MORPHOLOGY_DIAGNOSIS.json")
    assert entry["retry7_morphology_diagnosis_sha256"] == "16b9ed50655b6767affdb927106c5b2661d2fce388849d8c80aa5ac0165ebf78"
    assert entry["retry7_morphology_gap_path"].endswith("V24-RETRY7-MORPHOLOGY-GAP.json")
    assert entry["retry7_morphology_gap_sha256"] == "163ae1471792925b6b23c3a6fd26d1ae6d16637864180eaafd179875964afa36"
    assert entry["retry7_morphology_synthetic_positive_count"] == 3258
    assert entry["retry7_morphology_synthetic_negative_below_threshold"] == 229465
    assert entry["retry7_morphology_synthetic_negative_above_threshold"] == 1746
    assert entry["retry7_morphology_accepted_generic_false_positives"] == 46
    assert entry["retry7_morphology_real_dev_projects"] == 120
    assert entry["retry7_morphology_real_dev_failures"] == 0
    assert entry["retry7_morphology_real_dev_proposals"] == 1358010
    assert entry["retry7_morphology_real_dev_positive_proposals"] == 9849
    assert entry["retry7_morphology_real_dev_negative_below_threshold"] == 1283477
    assert entry["retry7_morphology_real_dev_negative_above_threshold"] == 64684
    assert entry["retry7_morphology_real_dev_runtime_ms"] == 459446.1507
    assert entry["retry7_morphology_real_to_synthetic_above_rate_ratio"] == 6.353592565545439
    assert entry["retry7_morphology_synthetic_positive_median_probability"] == 0.9933367967605591
    assert entry["retry7_morphology_real_positive_median_probability"] == 0.07347214221954346
    assert entry["retry7_morphology_gap_blocker"] == "deterministic anti-aliased marker and line rendering across both synthetic splits, regenerate audits, and re-pass synthetic gates before another model or real-dev run"
    assert entry["retry9_execution_blocker"] == REAL_DEV_BLOCKER


def test_unapproved_input_hold_rejects_training_before_reservation(tmp_path: Path):
    import pytest
    from ml.markers.training_budget import acquire_training_candidate

    # Exercise a held authorization in an isolated fixture. The real repository
    # owns retry10 evidence, so a rejection test must never try to reserve it.
    ledger_path = tmp_path / "ml/markers/training-budgets/production-repair-v1.json"
    ledger_path.parent.mkdir(parents=True)
    ledger_path.write_text(json.dumps({"revisions": [{
        "task": "marker-center", "revision": "marker-center-mask-preserving-v24",
        "status": "waiting_production_input_contract", "execution_authorized": False,
        "authorized_candidate_id": None,
    }]}))
    with pytest.raises(RuntimeError, match="not authorized by the canonical ledger"):
        acquire_training_candidate(
            tmp_path,
            task="marker-center",
            revision="marker-center-mask-preserving-v24",
            candidate_id="P1",
            config_path=Path("ml/markers/center/mask_preserving_v24/training/p1.json"),
            runner_source_paths=RUNNER_SOURCE_PATHS,
        )
    assert not (tmp_path / "ml/markers/training-seals").exists()


def test_retry10_outcome_is_bound_and_cannot_inherit_retry9_dev_approval():
    ledger = json.loads((ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_bytes())
    entry = next(item for item in ledger["revisions"] if item["revision"] == "marker-center-mask-preserving-v24")
    result_bytes = (ROOT / entry["retry10_p1_result_path"]).read_bytes()
    report = json.loads(result_bytes)
    seal_root = ROOT / entry["retry10_p1_seal_archive_path"]
    result_seal = json.loads((seal_root / "result.json").read_bytes())
    opened = json.loads((seal_root / "opened.json").read_bytes())
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

    assert hashlib.sha256(result_bytes).hexdigest() == entry["retry10_p1_result_sha256"]
    assert result_seal["report_sha256"] == entry["retry10_p1_result_sha256"]
    assert result_seal["opened_sha256"] == digest(seal_root / "opened.json")
    assert opened["binding"]["candidate_config_sha256"] == entry["retry10_closed_candidate_config_sha256"]["P1"]
    assert opened["binding"]["source_snapshot_sha256"] == digest(seal_root / "source-snapshot.json")
    assert report["status"] == result_seal["status"] == "failed_dev"
    assert not _passes_required_dev_gates(report["selected"], report["family_selected"], report["acceptance_bar"])
    assert entry["execution_authorized"] is False
    assert entry["retry9_dev_passed_candidate_ids"] == ["P1"]
    assert entry["real_dev_authorized"] is entry["real_sealed_authorized"] is False
    assert report["sealed_runs"] == report["real_dev_reads"] == report["real_sealed_reads"] == 0
    assert report["production_approval"] is report["private_data"] is False
    assert report["runtime_training_inputs"]["annotation_masks_used"] is False
    assert not (seal_root / "consumed.json").exists()


def test_coverage_outcome_matches_its_own_config_seals_and_dev_gates():
    ledger = json.loads((ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_bytes())
    entry = next(item for item in ledger["revisions"] if item["revision"] == "marker-center-mask-preserving-v24")
    result_path = ROOT / entry["current_candidate_result_path"]
    result_bytes = result_path.read_bytes()
    report = json.loads(result_bytes)
    config_path = ROOT / entry["candidate_config_paths"]["P1"]
    config = json.loads(config_path.read_bytes())
    seal_root = ROOT / "ml/markers/training-seals/marker-center/marker-center-mask-preserving-v24/P1"
    closed = json.loads((seal_root / "result.json").read_bytes())
    opened = json.loads((seal_root / "opened.json").read_bytes())
    snapshot = json.loads((seal_root / "source-snapshot.json").read_bytes())
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

    assert result_path.name == "P1_RETRY11_RESULT.json"
    assert config_path.name == "p1_retry11.json"
    identity = {"task": "marker-center", "revision": "marker-center-mask-preserving-v24", "candidate_id": "P1"}
    assert report["schema"] == "graphreader.marker-center-mask-preserving-v24-candidate.v1"
    assert all(report[key] == value and opened["binding"][key] == value for key, value in identity.items())
    assert snapshot["identity"] == identity
    assert opened["binding"]["candidate_config_path"] == config_path.relative_to(ROOT).as_posix()
    assert opened["binding"]["runner_source_bundle_sha256"] == config["expected_runner_source_bundle_sha256"]
    assert digest(result_path) == entry["current_candidate_result_sha256"] == entry["retry11_p1_result_sha256"] == closed["report_sha256"]
    assert digest(config_path) == report["candidate_config_sha256"] == opened["binding"]["candidate_config_sha256"]
    assert closed["opened_sha256"] == digest(seal_root / "opened.json")
    assert opened["binding"]["source_snapshot_sha256"] == digest(seal_root / "source-snapshot.json")
    assert report["optimizer_steps"] == 12672
    assert report["training_example_count"] == 44946
    for key in ("positive_example_count", "hard_negative_example_count", "real_range_training_example_count", "family_training_example_count", "family_positive_example_count", "family_hard_negative_example_count"):
        assert report[key] == config[key + "_expected"]
    assert report["family_sampling"]["selected_index_sha256"] == config["family_selected_index_sha256"]
    assert report["acceptance_bar"] == config["acceptance_bar"] == {
        "proposal_recall_minimum": .95, "precision_minimum": .95,
        "recall_minimum": .95, "prohibited_structure_hit_rate_maximum": .02,
    }
    assert report["selected"]["threshold"] == report["family_selected"]["threshold"] == config["confidence_threshold"] == .25
    runtime = report["runtime_training_inputs"]
    assert runtime["binding_path"] == config["runtime_training_input_binding_path"]
    assert runtime["binding_sha256"] == config["runtime_training_input_binding_sha256"]
    assert digest(ROOT / runtime["binding_path"]) == runtime["binding_sha256"]
    assert runtime["binding_schema"] == "graphreader.marker-runtime-training-input-binding.v2"
    for split in ("train", "dev"):
        assert report[f"family_{split}_tensor_set_sha256"] == config[f"family_{split}_tensor_set_sha256"]
        assert runtime[f"family_{split}_panel_count"] == entry[f"family_{split}_panel_count"]
        assert runtime[f"family_{split}_source_count"] == entry[f"family_{split}_scene_count"]
    assert report["onnx_provider"] == config["provider"] == "CPUExecutionProvider"
    parity = report["onnx_dynamic_candidate_counts"]
    assert [row["candidate_count"] for row in parity] == [1, 8, 37]
    assert all(0 <= row["maximum_absolute_error"] < float("inf") for row in parity)
    assert report["onnx_parity_maximum_absolute_error"] == max(row["maximum_absolute_error"] for row in parity)
    passed = _passes_required_dev_gates(report["selected"], report["family_selected"], report["acceptance_bar"])
    assert report["dev_gate_passed"] == passed
    expected_status = "dev_passed" if passed and report["onnx_parity_maximum_absolute_error"] <= config["onnx_parity_tolerance"] else "failed_dev"
    assert report["status"] == closed["status"] == entry["current_candidate_status"] == expected_status
    assert entry["dev_passed_candidate_ids"] == (["P1"] if expected_status == "dev_passed" else [])
    assert report["sealed_runs"] == report["real_dev_reads"] == report["real_sealed_reads"] == 0
    assert all(runtime[key] is False for key in ("annotation_masks_used", "complete_artifact_mask", "production_approved"))
    assert report["production_approval"] is report["private_data"] is False
    assert entry["execution_authorized"] is entry["real_dev_authorized"] is entry["real_sealed_authorized"] is False
    assert all(entry[key] is False for key in ("public_gate_authorized", "candidate_consumed", "production_approval", "release_eligible"))
    assert not (seal_root / "consumed.json").exists()
