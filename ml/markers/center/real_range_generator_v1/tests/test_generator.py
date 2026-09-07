# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from ml.markers.center.real_range_generator_v1.generator import (
    TOPOLOGY_TARGETS,
    audit,
    build_split,
    family_holdout_audit,
)
from ml.markers.center.real_range_generator_v1.negative_proposal_audit import audit as negative_proposal_audit


def test_sparse_fragment_variant_preserves_truths_and_reserves_coverage():
    from ml.markers.center.real_range_generator_v1.negative_proposal_audit import sparse_fragment_audit

    result = sparse_fragment_audit()
    assert all(result["gates"].values())
    assert result["sampler"]["sparse_capacity"] == result["sampler"]["sparse_selected"] == 1738
    assert result["sampler"]["generic_remainder_selected"] == 1500
    assert result["scope"]["model_loaded"] is False
    for split in ("train", "dev"):
        record = result["splits"][split]
        assert record["sparse_proposal_count"] == 2087
        reference = result["input_range_coverage"]["reference_median"]
        assert record["morphology"]["center5x5_mean"]["median"] < reference
        assert result["input_range_coverage"]["within_synthetic_p05_to_p95"][split]
        assert record["morphology"]["gray_band_fraction"]["median"] > 0.1
        assert record["morphology"]["covariance_eigen_ratio"]["p95"] > 10
    assert result["splits"]["train"]["aggregate_sha256"] != result["splits"]["dev"]["aggregate_sha256"]


def test_determinism_and_disjoint_seeds() -> None:
    a, b = build_split("train"), build_split("train")
    assert [s.seed for s in a] == [s.seed for s in b]
    assert [s.tensor.numpy().tobytes() for s in a] == [s.tensor.numpy().tobytes() for s in b]
    assert set(s.seed for s in a).isdisjoint(s.seed for s in build_split("dev"))


def test_independent_layouts_are_repeatable_and_do_not_reuse_training_geometry():
    from ml.markers.center.real_range_generator_v1.generator import _hash_scenes

    train = build_split("train", independent_layout=True)
    dev = build_split("dev", independent_layout=True)
    historical_train = build_split("train")
    assert {scene.centers for scene in train}.isdisjoint(scene.centers for scene in dev)
    assert {scene.centers for scene in historical_train}.isdisjoint(scene.centers for scene in dev)
    assert [scene.diameters for scene in dev] == [scene.diameters for scene in historical_train]
    expected_hash = _hash_scenes(dev)
    build_split.cache_clear()
    assert _hash_scenes(build_split("dev", independent_layout=True)) == expected_hash
    for scene in dev:
        xs = [x for x, _ in scene.centers]
        assert xs == sorted(xs)
        assert all(0 <= x < 224 and 0 <= y < 168 for x, y in scene.centers)


def test_independent_layout_audit_records_family_and_layout_disjointness():
    result = audit(independent_layout=True)
    evidence = result["layout_family_audit"]
    assert evidence["independent_layout_required"] is True
    assert evidence["train_dev_family_disjoint"] is True
    assert evidence["train_dev_layout_disjoint"] is True
    assert evidence["train_dev_seed_disjoint"] is True
    assert evidence["family_overlap_count"] == 0
    assert evidence["layout_overlap_count"] == 0
    assert len(evidence["train_layout_sha256"]) == 64
    assert len(evidence["dev_layout_sha256"]) == 64
    assert evidence["family_identity"] == "sha256(centers,diameters)"


def test_shared_family_holdout_audit_covers_marker_train_and_dev_without_truth():
    evidence = family_holdout_audit()
    assert evidence["schema"] == "graphreader.marker-center-family-holdout-audit.v1"
    assert evidence["marker_generator"] == "real_range_generator_v1"
    assert evidence["train_dev_family_disjoint"] is True
    assert evidence["scope"]["model_loaded"] is False
    assert evidence["scope"]["training_performed"] is False
    assert evidence["scope"]["sealed_reads"] == 0
    assert evidence["scope"]["scene_ids_emitted"] is False
    assert evidence["scope"]["truth_rows_emitted"] is False
    assert evidence["scope"]["pixels_emitted"] is False
    for axis in ("renderer", "font", "degradation", "template", "marker"):
        assert evidence["axes"][axis]["overlap_count"] == 0
        assert evidence["axes"][axis]["train_dev_disjoint"] is True


def test_range_and_masks_match_required_aggregate() -> None:
    result = audit()
    for split in result["splits"].values():
        assert split["marker_count"] == 2004
        assert split["tensor_shapes"] == [[3, 168, 224]]
        assert split["diameter_px"]["minimum"] == 1.0
        assert split["diameter_px"]["maximum"] == 48.0
        assert split["diameter_px"]["p05"] == 6.0
        assert split["diameter_px"]["median"] == 12.0
        assert split["diameter_px"]["p90"] == 24.0
        assert split["diameter_px"]["p95"] == 27.0
        assert split["rendered_diameter_px"]["minimum"] == 1.0
        assert split["rendered_diameter_px"]["maximum"] >= 48.0
        assert split["mask_center_hits"] == {"ocr": 75, "artifact": 332}
        assert split["mask_center_hit_rates"]["ocr"] == 75 / 2004
        assert split["mask_center_hit_rates"]["artifact"] == 332 / 2004
    assert all(scene.tensor.dtype.is_floating_point for name in ("train", "dev") for scene in build_split(name))
    assert {str(scene.tensor.dtype) for name in ("train", "dev") for scene in build_split(name)} == {"torch.float32"}
    masks = result["mask_overlap_scenarios"]
    assert masks["markers_per_split"] == 2004
    assert (masks["ocr_hard_hits"], masks["artifact_hard_hits"]) == (75, 332)
    assert result["truth_center_patch_distribution"]["shape"] == [3, 33, 33]
    quantiles = result["truth_center_patch_distribution"]["channel_mean_quantiles"]
    assert "ink_center_5x5" in quantiles
    assert .07 <= quantiles["ink"]["p05"] <= .11
    assert .20 <= quantiles["ink"]["median"] <= .30
    assert quantiles["artifact_mask"]["p90"] >= .12121
    assert quantiles["artifact_mask"]["maximum"] >= .23967
    assert all(result["distribution_gates"].values())


def test_mask_counts_are_measured_from_tensor_windows() -> None:
    for split in ("train", "dev"):
        ocr = artifact = 0
        for scene in build_split(split):
            for x_float, y_float in scene.centers:
                x, y = int(round(x_float)), int(round(y_float))
                ocr += int(float(scene.tensor[1, y - 2:y + 3, x - 2:x + 3].max()) >= .35)
                artifact += int(float(scene.tensor[2, y - 2:y + 3, x - 2:x + 3].max()) >= .35)
        assert (ocr, artifact) == (75, 332)


def test_scope_is_aggregate_only_and_negatives_present() -> None:
    result = audit()
    scope = result["scope"]
    assert all(scope[key] is False for key in
               ("model_loaded", "training_performed", "private_or_article_images",
                "candidate_revision_created", "scene_ids_emitted", "truth_rows_emitted",
                "pixels_emitted"))
    assert result["hard_negative_kinds"] == ["text", "line_intersection", "axis", "faint_line", "ocr_heavy", "topology_junction", "topology_fragment"]
    assert result["hard_negative_representatives"] == {
        "faint_line": {"x": 32.0, "y": 155.0},
        "ocr_heavy": {"x": 208.0, "y": 155.0},
    }


def test_topology_audit_covers_real_dev_morphology_medians() -> None:
    result = negative_proposal_audit()
    assert result["topology"]["target_real_dev_medians"] == TOPOLOGY_TARGETS
    assert all(result["topology_distribution_gates"].values())
    for split in ("train", "dev"):
        topology = result["topology"]["proposals"][split]
        assert topology["proposal_counts"]["topology_junction"] > 0
        assert topology["proposal_counts"]["topology_fragment"] > 0
        for kind in ("topology_junction", "topology_fragment"):
            for key, values in topology[kind].items():
                assert set(values) == {"minimum", "p05", "median", "p90", "p95", "maximum"}


def test_v24_ink_supported_proposal_stream_is_aggregate_and_deterministic() -> None:
    result = negative_proposal_audit()
    names = {"ink_mean", "ink_center_5x5_mean", "ink_max", "ocr_mean",
             "ocr_max", "artifact_mean", "artifact_max"}
    streams = [result["splits"][name] for name in ("train", "dev")]
    for stream in streams:
        assert stream["proposal_count"] > stream["positive_count"] > 0
        assert stream["negative_count"] == stream["proposal_count"] - stream["positive_count"]
        assert stream["sampled_negative_count_max10_per_positive"] == stream["positive_count"] * 10
        assert set(stream["negative_patch_feature_quantiles"]) == names
        for values in stream["negative_patch_feature_quantiles"].values():
            assert set(values) == {"minimum", "p05", "median", "p90", "p95", "maximum"}
    assert all(all(gates.values()) for gates in result["distribution_gates"].values())
    assert result["hard_negative_kinds"][-2:] == ["topology_junction", "topology_fragment"]
    assert result["hard_negative_representatives"]["faint_line"]["y"] == 155.0
    assert result["hard_negative_representatives"]["ocr_heavy"]["x"] == 208.0
    assert all(len(stream["proposal_coordinates_aggregate_sha256"]) == 64 for stream in streams)
    assert all(all(gates.values()) for gates in result["positive_morphology_gates"].values())
    assert result["positive_morphology_gate_split"] == "train_and_dev"
    assert result["sampler"]["negative_total"] == 32580
    assert result["sampler"]["topology_all_eligible_retained"] is True
    assert result["sampler"]["generic_connector_band_radius_px"] == 4.0
    assert result["sampler"]["generic_connector_band_capacity"] == 50373
    assert result["sampler"]["generic_connector_band_selected"] == 6720
    assert result["sampler"]["generic_connector_band_selected_index_sha256"] == "4e58e9e353a0ff912bccb28845e7e1d619d4903929f9cf49c6244dc5017fc96a"
