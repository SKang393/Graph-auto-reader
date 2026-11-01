# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from pathlib import Path
from collections import Counter
from types import ModuleType, SimpleNamespace

SOURCE = Path(__file__).with_name("curate_wide_train_only.py")
module = ModuleType("curate_wide_train_only_test_subject")
module.__file__ = str(SOURCE)
exec(compile(SOURCE.read_bytes(), str(SOURCE), "exec", dont_inherit=True), module.__dict__)


def test_train_allowlist_excludes_prior_holdout_and_nontrain_sources():
    allowed = [f"train-{index}" for index in range(19)]
    p3 = {"train_source_groups": allowed, "holdout_component_ids": ["held-1", "held-2"]}
    manifest = {"sources": [
        {"source_id": source_id, "split": "train", "recipe": {"split_role": "train"}}
        for source_id in [*allowed, "held-1", "not-selected"]
    ]}
    selected = module.admit_train_sources(manifest, p3)
    assert {row["source_id"] for row in selected} == set(allowed)
    assert all(row["source_id"] not in {"held-1", "not-selected"} for row in selected)


def test_only_strict_positive_and_text_free_negative_semantics_are_admitted():
    assert module.strict_label("positive", "unique_visible_tick") == 1
    assert module.strict_label("background_only", "no_ground_truth_tick_nearby") == 0
    assert module.strict_label("excluded", "unique_visible_tick") is None
    assert module.strict_label("positive", "ambiguous") is None
    assert module.strict_label("background_only", "unique_visible_tick") is None


def test_candidate_dedup_merges_source_groups_and_quarantines_conflicts():
    base_hash = "a" * 64
    same_hash = "b" * 64
    conflict_hash = "c" * 64
    def row(case, group, image_hash, kind, state):
        return {"case_id": case, "source_group_id": group, "image_sha256": image_hash,
                "kind": kind, "matching_state": state}
    rows = [row("x1", "g1", same_hash, "positive", "unique_visible_tick"),
            row("x2", "g2", same_hash, "positive", "unique_visible_tick"),
            row("x3", "g3", base_hash, "background_only", "no_ground_truth_tick_nearby"),
            row("x4", "g4", conflict_hash, "positive", "unique_visible_tick"),
            row("x5", "g5", conflict_hash, "background_only", "no_ground_truth_tick_nearby"),
            row("x6", "g6", "d" * 64, "positive", "ambiguous")]
    result = module.reconcile_candidates({base_hash: {0}}, rows)
    assert len(result["kept"]) == 1
    assert result["kept"][0]["source_group_ids"] == ["g1", "g2"]
    assert len(result["same_label_baseline_duplicates"]) == 2
    assert sum(row.get("duplicate_of_p3_baseline", False)
               for row in result["same_label_baseline_duplicates"]) == 1
    assert len(result["conflicts_quarantined"]) == 2


def test_candidate_hash_with_ambiguous_occurrence_is_quarantined():
    image_hash = "e" * 64
    rows = [
        {"case_id": "pos", "source_group_id": "g1", "image_sha256": image_hash,
         "kind": "positive", "matching_state": "unique_visible_tick"},
        {"case_id": "amb", "source_group_id": "g2", "image_sha256": image_hash,
         "kind": "excluded", "matching_state": "ambiguous"},
    ]
    result = module.reconcile_candidates({}, rows)
    assert not result["kept"]
    assert result["conflicts_quarantined"][0]["quarantine_reason"] == "same_image_has_ambiguous_truth_occurrence"


def test_full_manifest_assembly_with_small_synthetic_inputs():
    facts = {
        "config": {"recipe_id": "test-wide", "admission_policy": {"new_rows_only": ["positive"]}},
        "config_sha256": "1" * 64, "p3_config_sha256": "2" * 64,
        "v6_manifest_sha256": "3" * 64, "curation_manifest_sha256": "4" * 64,
        "dependency_hashes": {"helper.py": "5" * 64}, "executed_script_sha256": "6" * 64,
        "runtime": {"python": "test"}, "allowed_groups": {"g1"},
        "source_receipts": [{"source_id": "g1"}], "v6_verified_counts": Counter({"positive": 1}),
        "baseline_labels": {"a" * 64: {1}, "b" * 64: {0}},
        "baseline_groups": {"a" * 64: ["g1"], "b" * 64: ["g1"]},
        "baseline_count": 2, "wide_proposal_count": 3,
        "panel_records": {"panel": {"panel_id": "panel", "proposals": [{"unused": True}]}},
        "kind_counts": Counter({"positive": 1, "excluded": 2}), "strict_candidate_count": 1,
        "reconciled": {"kept": [{"label": 1, "source_group_ids": ["g1"]}],
            "same_label_baseline_duplicates": [{"duplicate_of_p3_baseline": True}, {"case_id": "duplicate"}],
            "conflicts_quarantined": [{"case_id": "conflict"}]},
        "source_counts": {"g1": Counter({"positive": 1})},
        "scanner": SimpleNamespace(SCAN_RADIUS=12, AXIS_CORE_EXCLUSION=2, EDGE_GUARD=4, MAX_INK_GRAY=200),
        "elapsed_seconds": 1.25,
    }
    report = module.assemble_manifest(facts)
    assert report["scan"]["baseline_parity_proposals"] == 2
    assert report["scan"]["wide_proposals"] == 3
    assert report["curation"]["deduplicated_new_class_counts"] == {"1": 1}
    assert report["curation"]["same_label_duplicates_of_frozen_p3_baseline"] == 1
    assert report["curation"]["same_hash_candidate_duplicate_occurrences"] == 1
    assert report["prior_holdout_payloads_opened"] == 0
    assert report["training_performed"] is False


if __name__ == "__main__":
    for name, check in sorted(globals().items()):
        if name.startswith("test_"):
            check()
    print("5 focused curation checks passed")
