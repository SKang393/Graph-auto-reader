# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Dataset artifact, annotation, role, and coverage tests."""

from __future__ import annotations

import csv
import hashlib
from pathlib import Path

from PIL import Image

from ml.synthetic.dataset import (
    FAMILY_AXES,
    HARD_NEGATIVE_KINDS,
    REQUIRED_TEXT_ROLES,
    family_holdout_audit,
)
from ml.synthetic.tests.conftest import read_json
from ml.synthetic.templates import FILL_STATES, LINE_STYLES, MARKER_SHAPES, SCENE_FEATURES


def test_seed_manifest_hashes_every_declared_artifact(smoke_root: Path) -> None:
    manifest = read_json(smoke_root / "seed-manifest.json")
    assert manifest["dataset_seed"] == 393
    assert manifest["sanity_passed"] is True
    assert manifest["renderer_environment"]["font_files_bundled"] is False
    for relative_path, expected_hash in manifest["artifact_sha256"].items():
        payload = (smoke_root / relative_path).read_bytes()
        assert hashlib.sha256(payload).hexdigest() == expected_hash


def test_csv_marker_counts_and_required_masks_are_exact(smoke_root: Path) -> None:
    manifest = read_json(smoke_root / "seed-manifest.json")
    for case in manifest["cases"]:
        scene_id = case["scene_id"]
        with (smoke_root / "tables" / f"{scene_id}.csv").open(
            "r", encoding="utf-8", newline=""
        ) as stream:
            rows = list(csv.DictReader(stream))
        annotation = read_json(
            smoke_root / "annotations" / f"{scene_id}.json"
        )
        marker_centers = {
            marker["point_id"]: marker["center"]
            for panel in annotation["panels"]
            for marker in panel["markers"]
        }
        assert len(rows) == case["metrics"]["markers"]
        for row in rows:
            center = marker_centers[row["point_id"]]
            assert float(row["original_pixel_x"]) == center[0]
            assert float(row["original_pixel_y"]) == center[1]
        mask = Image.open(smoke_root / "masks" / f"{scene_id}.png")
        assert mask.mode in {"1", "L"}
        assert mask.getbbox() is not None


def test_sanity_report_spans_fixed_acceptance_matrix(smoke_root: Path) -> None:
    sanity = read_json(smoke_root / "sanity-report.json")
    coverage = sanity["coverage"]
    assert sanity["passed"] is True
    assert set(coverage["designs"]) == {
        "ab",
        "aba",
        "abab",
        "multiple_baseline",
        "multiple_probe",
        "alternating_treatments",
        "changing_criterion",
        "maintenance",
        "generalization",
        "staggered_starts",
        "shared_baseline",
    }
    assert {1, 6} <= set(coverage["panel_counts"])
    assert {2, 100} <= set(coverage["session_counts"])
    assert set(coverage["line_styles"]) == set(LINE_STYLES)
    assert set(coverage["text_roles"]) >= set(REQUIRED_TEXT_ROLES)
    assert set(coverage["hard_negative_kinds"]) == set(HARD_NEGATIVE_KINDS)
    assert set(coverage["scene_features"]) == set(SCENE_FEATURES)
    assert set(coverage["degradation_stage_counts"]) <= {1, 2}
    assert set(coverage["x_label_visibility"]) == {"visible", "partial", "hidden"}
    assert {"inside", "outside"} <= set(coverage["legend_positions"])
    assert "dotted" in coverage["divider_styles"]
    assert True in coverage["shared_axes_values"]
    assert True in coverage["hidden_zero_values"]
    assert all(coverage["decorations"].values())
    assert len(coverage["y_axis_profiles"]) >= 2
    assert len(coverage["stroke_widths"]) >= 2
    assert len(coverage["marker_radii"]) >= 2
    assert len(coverage["session_spacing_profiles"]) >= 2
    actual_styles = {
        (item["shape"], item["fill"]) for item in coverage["marker_styles"]
    }
    assert actual_styles == {
        (shape, fill) for shape in MARKER_SHAPES for fill in FILL_STATES
    }


def test_split_manifests_isolate_all_family_axes(smoke_root: Path) -> None:
    splits = {
        split: read_json(smoke_root / "splits" / f"{split}.json")
        for split in ("train", "validation", "test")
    }
    for axis in FAMILY_AXES:
        train = set(splits["train"]["families"][axis])
        validation = set(splits["validation"]["families"][axis])
        test = set(splits["test"]["families"][axis])
        assert train
        assert validation
        assert test
        assert train.isdisjoint(validation)
        assert train.isdisjoint(test)
        assert validation.isdisjoint(test)


def test_family_holdout_audit_is_aggregate_only_and_deterministic() -> None:
    first = family_holdout_audit()
    second = family_holdout_audit()
    assert first == second
    assert first["held_out_axes"] == list(FAMILY_AXES)
    assert first["train_dev_family_disjoint"] is True
    assert first["scope"] == {
        "synthetic_only": True,
        "model_loaded": False,
        "training_performed": False,
        "private_or_article_images": False,
        "sealed_reads": 0,
        "scene_ids_emitted": False,
        "truth_rows_emitted": False,
        "pixels_emitted": False,
    }
    for axis in FAMILY_AXES:
        evidence = first["axes"][axis]
        assert evidence["overlap_count"] == 0
        assert evidence["train_dev_disjoint"] is True
        assert len(evidence["train_family_set_sha256"]) == 64
        assert len(evidence["dev_family_set_sha256"]) == 64
    for split in ("train", "dev"):
        evidence = first["splits"][split]
        assert evidence["scene_count"] > 0
        assert evidence["marker_count"] > 0
        assert len(evidence["aggregate_sha256"]) == 64


def test_contact_sheet_and_annotations_are_reviewable(smoke_root: Path) -> None:
    sheet = Image.open(smoke_root / "contact-sheet.png")
    assert sheet.width >= 1200
    assert sheet.height >= 900
    annotations = sorted((smoke_root / "annotations").glob("*.json"))
    assert len(annotations) == 11
    for path in annotations:
        annotation = read_json(path)
        assert annotation["coordinate_space"] == "original_pixels"
        assert annotation["font"]["bundled"] is False
        assert len(annotation["degradations"]) in {1, 2}
