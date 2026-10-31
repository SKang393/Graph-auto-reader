# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
from tempfile import TemporaryDirectory

import numpy as np
import pytest

from ml.ocr.synthetic_reject_glyph_v1 import generate
from ml.ocr.synthetic_reject_glyph_v1 import protocol


def _fonts():
    _, paths, _ = generate._verify_fixed_sources()
    return paths


def test_phrase_filter_rejects_every_mixed_or_v5_alphabet_label():
    for text in ("Session 1", "Phase 2", "Response%", "-Phase", "Outcome.1", "", "  ", "Two  spaces", "café"):
        with pytest.raises(ValueError):
            generate.validate_phrase(text)
    for text in ("Intervention", "Primary outcome", "Reintroduction"):
        generate.validate_phrase(text)


def test_train_validation_phrase_and_font_families_are_disjoint():
    catalog, _ = generate._load_phrase_catalog()
    manifest = generate.build_manifest()
    train = manifest["split_families"]["train"]
    validation = manifest["split_families"]["validation"]
    assert train["id"] != validation["id"]
    assert set(train["font_ids"]).isdisjoint(validation["font_ids"])
    assert set(train["phrase_ids"]).isdisjoint(validation["phrase_ids"])
    assert manifest["split_disjointness"] == {
        "phrase_ids": True, "phrase_text": True, "font_ids": True,
        "font_sha256": True, "family_ids": True,
    }
    train_text = {row["text"] for row in catalog["train"]}
    validation_text = {row["text"] for row in catalog["validation"]}
    assert train_text.isdisjoint(validation_text)
    assert manifest["training_approved"] is False
    assert manifest["production_approved"] is False
    assert manifest["private_reads"] == manifest["sealed_reads"] == manifest["test_reads"] == 0
    assert manifest["admission_status"] == "blocked_pending_independent_review_and_numeric_foundation_integration"
    assert manifest["v5_preclassifier_eligibility"]["maximum_component_count"] == 8
    assert all(sample["render_spec"]["actual_font_size_px"] >= 14 for sample in manifest["samples"])


def test_clean_phrase_renders_and_all_runtime_segments_get_class_thirteen():
    font_path = _fonts()["serif"]
    raster, text_mask, size = generate.render_phrase("Probe", font_path, 20)
    result = generate.inspect_crop(raster, text_mask)
    assert raster.shape == text_mask.shape == (32, 128)
    assert raster.dtype == text_mask.dtype == np.uint8
    assert text_mask.any()
    assert size <= 20
    assert result["status"] == "eligible"
    assert len(result["components"]) > 0
    assert all(component["class_index"] == protocol.REJECT_CLASS_INDEX for component in result["components"])
    assert all(component["encoded"].shape == protocol.ENCODED_SHAPE for component in result["components"])
    assert all(component["encoded"].dtype == np.float32 for component in result["components"])
    assert all(component["geometry"][0] < protocol.STRUCTURAL_REJECT_MINIMUM_HEIGHT_RATIO for component in result["components"])


def test_host_or_arbitrary_font_fallback_is_rejected():
    with pytest.raises(ValueError, match="exact verified public_fonts_v1 asset"):
        generate.render_phrase("Outcome", Path(__file__), 16)


def test_empty_and_contaminated_crops_are_explicitly_excluded():
    empty = np.full((32, 128), 255, dtype=np.uint8)
    assert generate.inspect_crop(empty)["reason"] == "empty_foreground"

    raster, mask, _ = generate.render_phrase("Outcome", _fonts()["sans"], 20)
    contaminated = raster.copy()
    outside = np.argwhere(mask == 0)
    assert len(outside) > 0
    y, x = outside[len(outside) // 2]
    contaminated[y, x] = 0
    result = generate.inspect_crop(contaminated, mask)
    assert result["status"] == "excluded"
    assert result["reason"] == "contaminated_crop"
    assert result["components"] == ()


def test_v5_structural_height_short_circuit_is_not_class_thirteen_supervision():
    tall = np.full((32, 128), 255, dtype=np.uint8)
    tall[4:28, 64] = 0
    result = generate.inspect_crop(tall)
    assert result["status"] == "excluded"
    assert result["reason"] == "v5_structural_height_reject_before_classifier"
    assert result["components"] == ()
    assert len(result["excluded_components"]) == 1
    exclusion = result["excluded_components"][0]
    assert exclusion["class_index"] is None
    assert exclusion["geometry"][0] >= protocol.STRUCTURAL_REJECT_MINIMUM_HEIGHT_RATIO
    assert exclusion["reason"] == "v5_structural_height_reject_before_classifier"


def test_v5_over_eight_components_short_circuit_excludes_entire_crop():
    many = np.full((32, 128), 255, dtype=np.uint8)
    for index in range(9):
        x = 4 + index * 4
        many[14:16, x] = 0
    result = generate.inspect_crop(many)
    assert result["status"] == "excluded"
    assert result["reason"] == "v5_component_count_limit_before_classifier"
    assert result["components"] == ()
    assert len(result["excluded_components"]) == 9
    assert all(row["class_index"] is None for row in result["excluded_components"])


def test_materialized_dataset_hashes_match_manifest_and_fresh_output_is_required():
    temp_root = protocol.ROOT / "artifacts" / "test-temp"
    temp_root.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(dir=temp_root) as temporary:
        output = Path(temporary) / "reject-corpus"
        manifest = generate.generate_corpus(output)
        saved = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        assert saved["counts"] == manifest["counts"]
        assert saved["source_bindings"]["ml/ocr/component_ensemble_v5/dataset.py"] == protocol.V5_SOURCE_SHA256["ml/ocr/component_ensemble_v5/dataset.py"]
        assert saved["font_source"]["license"]["sha256"] == "0dab92d0544f7b233403f14b84a663bdbfa746982eda629e7f4f9ffe1b036feb"
        for sample in saved["samples"]:
            raster_path = output / sample["raster_path"]
            assert sha256(raster_path.read_bytes()).hexdigest() == sample["raster_png_sha256"]
            assert sample["sample_kind"] == protocol.SAMPLE_KIND
            for component in sample["components"]:
                tensor_path = output / component["tensor_path"]
                raw = tensor_path.read_bytes()
                assert sha256(raw).hexdigest() == component["encoded_tensor_sha256"]
                assert len(raw) == 1 * 24 * 26 * 4
                assert component["class_index"] == protocol.REJECT_CLASS_INDEX
        with pytest.raises(FileExistsError, match="fresh"):
            generate.generate_corpus(output)


def test_source_and_font_pins_are_explicit_and_reject_data_is_not_production_admitted():
    manifest = generate.build_manifest()
    assert manifest["schema"] == protocol.SCHEMA
    assert manifest["class_index"] == 13
    assert manifest["font_source"]["historical_benchmark_independence"] == "unproven"
    assert manifest["source_bindings"]["ml/ocr/component_ensemble_v5/model.py"] == protocol.V5_SOURCE_SHA256["ml/ocr/component_ensemble_v5/model.py"]
    assert manifest["source_bindings"]["ml/ocr/component_geometric_v4/dataset.py"] == protocol.V5_SOURCE_SHA256["ml/ocr/component_geometric_v4/dataset.py"]
    assert manifest["source_bindings"]["ml/ocr/component_ensemble_v5/pipeline.py"] == protocol.V5_SOURCE_SHA256["ml/ocr/component_ensemble_v5/pipeline.py"]
    assert manifest["source_bindings"]["ml/synthetic/public_fonts_v1/manifest.json"] == protocol.FONT_MANIFEST_SHA256
    assert manifest["counts"]["eligible_samples"] > 0
