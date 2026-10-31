# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import copy
import json
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

from ml.axis.candidate_ranker_foundation_v1 import features
from ml.axis.candidate_ranker_dev_eval_v1 import evaluator


SOURCE_SHA = "a" * 64
PANEL_SHA = "b" * 64
SCALE = [1.0] * 15 + [0.03125, 0.03125]


def snapshot(rank: int, offset: float, score: float) -> dict:
    base = [[offset, offset], [100 + offset, offset], [100 + offset, 100 + offset], [offset, 100 + offset]]
    return {
        "Rank": rank,
        "Score": score,
        "Intersection": {"X": base[3][0], "Y": base[3][1]},
        "XAxisLine": {"Start": {"X": base[3][0], "Y": base[3][1]}, "End": {"X": base[2][0], "Y": base[2][1]}},
        "YAxisLine": {"Start": {"X": base[3][0], "Y": base[3][1]}, "End": {"X": base[0][0], "Y": base[0][1]}},
        "BottomRight": {"X": base[2][0], "Y": base[2][1]},
        "TopRight": {"X": base[1][0], "Y": base[1][1]},
        "TopLeft": {"X": base[0][0], "Y": base[0][1]},
        "XSpan": 100.0,
        "YSpan": 100.0,
        "XCoverageFraction": 1.0,
        "YCoverageFraction": 1.0,
        "XRootMeanSquareError": 0.0,
        "YRootMeanSquareError": 0.0,
        "XSupportingCandidateIds": [f"x-{rank}"],
        "YSupportingCandidateIds": [f"y-{rank}"],
    }


def panel_row(source_id: str, panel_id: str, snapshots: list[dict], *, origin: str = "detected_panel") -> dict:
    matrix = features.snapshot_group(SOURCE_SHA, PANEL_SHA, 100, 100, snapshots)
    return {
        "source_id": source_id,
        "source_image_path": "artifacts/axis-roles-dev-corpus-v3-20261031/images/0.png",
        "source_sha256": SOURCE_SHA,
        "source_byte_length": 100,
        "source_width": 120,
        "source_height": 120,
        "panel_id": panel_id,
        "panel_origin_kind": origin,
        "panel_image_path": "artifacts/goal22-runs/axis-roles-dev-measure-run-v2/source/panel.png",
        "panel_sha256": PANEL_SHA,
        "panel_byte_length": 80,
        "panel_width": 100,
        "panel_height": 100,
        "encoded_crop_in_source_pixels": {"X": 20, "Y": 10, "Width": 100, "Height": 100},
        "status": "success",
        "candidate_snapshots": snapshots,
        "normalized_features": {
            "schema": features.SCHEMA,
            "feature_names": list(features.FEATURE_NAMES),
            "rows": [
                {"rank": index + 1, "features": row["features"]}
                for index, row in enumerate(matrix["rows"])
            ],
        },
    }


def export_panel(source_id: str, source_index: int, source_sha: str, panel_id: str, panel_sha: str,
                 *, fallback: bool = False) -> dict:
    rect = {"X": 0, "Y": 0, "Width": 120, "Height": 120, "Right": 120,
            "Bottom": 120, "Area": 14400, "IsValid": True}
    return {
        "source_id": source_id,
        "source_image_path": f"artifacts/axis-roles-dev-corpus-v3-20261031/images/{source_index}.png",
        "source_sha256": source_sha,
        "source_width": 120,
        "source_height": 120,
        "panel_id": panel_id,
        "panel_origin_kind": "full_image_review_fallback" if fallback else "detected_panel",
        "panel_image_path": f"artifacts/goal22-runs/axis-roles-dev-measure-run-v2/{source_id}/panelization/panel.png",
        "panel_sha256": panel_sha,
        "panel_width": 120,
        "panel_height": 120,
        "coordinate_space": "panel_original_pixels",
        "requested_crop_in_source_pixels": rect,
        "encoded_crop_in_source_pixels": rect,
        "source_to_panel_matrix_row_major": [1, 0, 0, 0, 1, 0, 0, 0, 1],
        "panel_to_source_matrix_row_major": [1, 0, 0, 0, 1, 0, 0, 0, 1],
        "status": "success",
        "candidate_snapshots": [snapshot(0, 0.0, 0.5)],
        "normalized_features": {},
        "failure": None,
    }


def checkpoint_and_reports() -> tuple[dict, dict, dict, dict]:
    checkpoint = {
        "schema": "graphreader.axis-linear-ranker-checkpoint.v1",
        "config_sha256": evaluator.TRAINING_CONFIG_SHA256,
        "feature_scale": SCALE,
        "weight": [-1.0] + [0.0] * 16,
        "bias": 0.0,
        "production_approved": False,
    }
    config = {
        "schema": "graphreader.axis-gpu-ranker-config.v1",
        "task": "axis-candidate-ranker",
        "candidate_id": "P1",
        "architecture": "linear-17-to-1",
        "feature_scale": SCALE,
        "group_count": 93,
        "candidate_count": 27776,
        "optimizer_steps": 372,
        "objective": "listwise-soft-target-cross-entropy",
        "dev_read": False,
        "reserved_read": False,
        "production_approved": False,
    }
    training = {
        "schema": "graphreader.axis-gpu-ranker-training-report.v1",
        "status": "training_complete_pending_finalization",
        "steps": 372,
        "checkpoint_sha256": evaluator.CHECKPOINT_SHA256,
        "config_sha256": evaluator.TRAINING_CONFIG_SHA256,
        "dev_read": False,
        "reserved_read": False,
        "production_approved": False,
    }
    finalization = {
        "schema": "graphreader.axis-gpu-ranker-finalization.v1",
        "status": "finalized",
        "report_sha256": evaluator.TRAINING_REPORT_SHA256,
        "checkpoint_sha256": evaluator.CHECKPOINT_SHA256,
        "production_approved": False,
    }
    return checkpoint, config, training, finalization


def provenance() -> dict:
    source_bindings = {path: "d" * 64 for path in evaluator.SCORER_SOURCE_PATHS}
    return {
        "checkpoint_sha256": evaluator.CHECKPOINT_SHA256,
        "training_report_sha256": evaluator.TRAINING_REPORT_SHA256,
        "training_config_sha256": evaluator.TRAINING_CONFIG_SHA256,
        "finalization_sha256": evaluator.FINALIZATION_SHA256,
        "dev_manifest_sha256": evaluator.DEV_MANIFEST_SHA256,
        "panel_export_report_sha256": "c" * 64,
        "panel_export_input_manifest_sha256": "e" * 64,
        "panel_export_runtime": {"runtime_test": "bound"},
        "evaluation_binding_sha256": "f" * 64,
        "scorer_protocol_sha256": "d" * 64,
        "scorer_source_bindings": source_bindings,
        "scorer_source_bundle_sha256": "a" * 64,
    }


def payload() -> tuple[dict, dict, dict, dict, dict, dict, dict]:
    source_id = "scene-1"
    baseline = snapshot(0, 10.0, 0.9)
    candidate = snapshot(1, 0.0, 0.5)
    panel = panel_row(source_id, "panel-1", [baseline, candidate])
    manifest = {"dev_cases": [{"scene_id": source_id, "seed": 7, "index": 0}]}
    report = {
        "source_summaries": [{"source_id": source_id, "source_status": "detected"}],
        "panel_results": [panel],
    }
    annotations = {
        source_id: {
            "scene_id": source_id,
            "seed": 7,
            "coordinate_space": "original_pixels",
            "schema_version": 1,
            "plots": [{"box": [20, 10, 100, 100], "coordinate_space": "original_pixels",
                       "panel_id": "plot-1", "polygon": [[20, 10], [120, 10], [120, 110], [20, 110]]}],
        }
    }
    checkpoint, config, training, finalization = checkpoint_and_reports()
    return manifest, report, annotations, checkpoint, config, training, finalization


class DevEvaluationTests(unittest.TestCase):
    def _skip_missing_local_prerequisites(
        self, root: Path, paths: list[str], expected_hashes: dict[str, str] | None = None,
    ) -> None:
        missing: list[str] = []
        for path, expected in (expected_hashes or {}).items():
            candidate = root / path
            if candidate.is_symlink() or (hasattr(candidate, "is_junction") and candidate.is_junction()):
                self.fail(f"present local input is not a regular file: {path}")
            if candidate.exists() and not candidate.is_file():
                self.fail(f"present local input is not a regular file: {path}")
            if candidate.is_file():
                self.assertEqual(expected, evaluator._sha(candidate.read_bytes()), f"present local input is invalid: {path}")
            else:
                missing.append(path)
        checked = set(expected_hashes or {})
        for path in paths:
            if path in checked:
                continue
            candidate = root / path
            if candidate.is_symlink() or (hasattr(candidate, "is_junction") and candidate.is_junction()):
                self.fail(f"present local input is not a regular file: {path}")
            if candidate.exists() and not candidate.is_file():
                self.fail(f"present local input is not a regular file: {path}")
            if not candidate.is_file():
                missing.append(path)
        if missing:
            self.skipTest("locally materialized Goal 22 evidence is absent: " + ", ".join(missing[:4]))

    def test_real_data_guard_skips_only_missing_prerequisites(self) -> None:
        temp_root = Path(__file__).resolve().parents[3] / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temp_root) as folder:
            root = Path(folder)
            with self.assertRaises(unittest.SkipTest):
                self._skip_missing_local_prerequisites(root, ["ignored/manifest.json"])
            (root / "ignored").mkdir()
            present = root / "ignored/manifest.json"
            present.write_text("tampered but present", encoding="utf-8")
            self._skip_missing_local_prerequisites(root, ["ignored/manifest.json"])
            with self.assertRaises(AssertionError):
                self._skip_missing_local_prerequisites(
                    root, ["ignored/manifest.json"], {"ignored/manifest.json": "0" * 64})
            (root / "ignored/manifest.json").unlink()
            (root / "ignored/manifest.json").mkdir()
            with self.assertRaisesRegex(AssertionError, "not a regular file"):
                self._skip_missing_local_prerequisites(root, ["ignored/manifest.json"])

    def test_real_data_guard_rejects_dangling_symlink_when_supported(self) -> None:
        temp_root = Path(__file__).resolve().parents[3] / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temp_root) as folder:
            root = Path(folder)
            target = root / "absent-target.json"
            link = root / "dangling.json"
            try:
                link.symlink_to(target)
            except (OSError, NotImplementedError) as exception:
                self.skipTest(f"symbolic links are unavailable on this host: {exception}")
            with self.assertRaisesRegex(AssertionError, "not a regular file"):
                self._skip_missing_local_prerequisites(root, ["dangling.json"])

    def test_fixed_public_annotations_preflight_all_panel_identities_without_scoring(self) -> None:
        root = Path(__file__).resolve().parents[3]
        export_relative_path = "artifacts/goal22-runs/axis-candidate-evidence-public-v3-dev-prep-20261031-v5/report.json"
        expected_report_sha = "272e80e0b4c2a0a471f0eb7f11ac45c4991aa49835bcf1143194f205d03bd8bb"
        self._skip_missing_local_prerequisites(
            root,
            [evaluator.DEV_MANIFEST_PATH, export_relative_path],
            {evaluator.DEV_MANIFEST_PATH: evaluator.DEV_MANIFEST_SHA256, export_relative_path: expected_report_sha},
        )
        manifest_path = root / evaluator.DEV_MANIFEST_PATH
        manifest_raw = manifest_path.read_bytes()
        self.assertEqual(evaluator.DEV_MANIFEST_SHA256, evaluator._sha(manifest_raw))
        manifest = evaluator._parse(manifest_raw, "fixed public dev manifest")
        annotation_paths = [
            f"artifacts/axis-roles-dev-corpus-v3-20261031/annotations/{case['index']}.json"
            for case in manifest["dev_cases"]
        ]
        self._skip_missing_local_prerequisites(
            root,
            annotation_paths,
            {path: case["annotation_sha256"] for path, case in zip(annotation_paths, manifest["dev_cases"], strict=True)},
        )
        export_path = root / export_relative_path
        export_raw = export_path.read_bytes()
        self.assertEqual("272e80e0b4c2a0a471f0eb7f11ac45c4991aa49835bcf1143194f205d03bd8bb", evaluator._sha(export_raw))
        export = evaluator._parse(export_raw, "fixed truth-free public dev export")
        self.assertEqual((32, 48), (export["source_count"], export["panel_count"]))

        annotations: dict[str, dict] = {}
        annotation_ids: set[str] = set()
        runtime_ids: set[str] = {panel["panel_id"] for panel in export["panel_results"]}
        self.assertEqual(48, len(runtime_ids))
        for case in manifest["dev_cases"]:
            annotation_path = root / f"artifacts/axis-roles-dev-corpus-v3-20261031/annotations/{case['index']}.json"
            raw = annotation_path.read_bytes()
            self.assertEqual(case["annotation_sha256"], evaluator._sha(raw))
            annotation = evaluator._parse(raw, "pinned public dev annotation")
            self.assertEqual(1, annotation["schema_version"])
            self.assertEqual(case["scene_id"], annotation["scene_id"])
            self.assertEqual("original_pixels", annotation["coordinate_space"])
            annotations[case["scene_id"]] = annotation
            for plot in annotation["plots"]:
                self.assertEqual({"box", "coordinate_space", "panel_id", "polygon"}, set(plot))
                annotation_ids.add(plot["panel_id"])

        # The saved annotation panel_id is a local plot identity, not an ID join
        # to current production panelization output. Association stays geometric.
        self.assertEqual(0, len(annotation_ids & runtime_ids))
        associations = evaluator._preflight_annotation_panel_identities(manifest, export, annotations)
        self.assertEqual(32, len(associations))
        self.assertEqual(46, sum(value["summary"]["labelable_groups"] for value in associations.values()))
        self.assertEqual(2, sum(value["summary"]["fallback_panels"] for value in associations.values()))
        self.assertEqual(2, sum(value["summary"]["missed_plots"] for value in associations.values()))
        self.assertEqual(0, sum(value["summary"]["ambiguous_plots"] for value in associations.values()))
        for association in associations.values():
            self.assertTrue(all(match["plot_id"] in annotation_ids for match in association["matches"]))
            self.assertTrue(all(match["panel_id"] in runtime_ids for match in association["matches"]))

    def test_annotation_plot_adapter_rejects_wrong_schema_and_coordinate_space(self) -> None:
        annotation = {
            "schema_version": 1, "scene_id": "scene", "seed": 7, "coordinate_space": "original_pixels",
            "plots": [{"box": [20, 10, 100, 100], "coordinate_space": "original_pixels", "panel_id": "plot",
                       "polygon": [[20, 10], [120, 10], [120, 110], [20, 110]]}],
        }
        normalized = evaluator._normalize_dev_annotation_plots(annotation, "scene", 7)
        self.assertEqual("plot", normalized[0]["id"])
        bad_shape = copy.deepcopy(annotation)
        bad_shape["plots"][0]["id"] = "invented"
        with self.assertRaisesRegex(evaluator.EvaluationError, "row shape"):
            evaluator._normalize_dev_annotation_plots(bad_shape, "scene", 7)
        bad_space = copy.deepcopy(annotation)
        bad_space["plots"][0]["coordinate_space"] = "panel_pixels"
        with self.assertRaisesRegex(evaluator.EvaluationError, "identity or coordinate space"):
            evaluator._normalize_dev_annotation_plots(bad_space, "scene", 7)

    def test_real_v5_export_rows_bind_to_snapshot_order_and_reject_tampering(self) -> None:
        root = Path(__file__).resolve().parents[3]
        report_relative_path = "artifacts/goal22-runs/axis-candidate-evidence-public-v3-dev-prep-20261031-v5/report.json"
        report_sha = "272e80e0b4c2a0a471f0eb7f11ac45c4991aa49835bcf1143194f205d03bd8bb"
        self._skip_missing_local_prerequisites(root, [report_relative_path], {report_relative_path: report_sha})
        report_path = root / report_relative_path
        raw = report_path.read_bytes()
        self.assertEqual(report_sha, evaluator._sha(raw))
        report = evaluator._parse(raw, "fixed truth-free public dev export")
        rows_seen = 0
        for panel in report["panel_results"]:
            self.assertEqual("success", panel["status"])
            rows = evaluator._validate_feature_rows(panel)
            rows_seen += len(rows)
        self.assertEqual(15374, rows_seen)

        panel = copy.deepcopy(next(panel for panel in report["panel_results"] if panel["status"] == "success"))
        panel["normalized_features"]["rows"][0]["rank"] = 2
        with self.assertRaisesRegex(evaluator.EvaluationError, "rank/order identity"):
            evaluator._validate_feature_rows(panel)

        panel = copy.deepcopy(next(panel for panel in report["panel_results"] if panel["status"] == "success"))
        panel["candidate_snapshots"][0]["Rank"] = 9
        with self.assertRaisesRegex(evaluator.EvaluationError, "rank/order identity"):
            evaluator._validate_feature_rows(panel)

        panel = copy.deepcopy(next(panel for panel in report["panel_results"] if panel["status"] == "success"))
        panel["normalized_features"]["rows"][0]["candidate_identity_sha256"] = "0" * 64
        with self.assertRaisesRegex(evaluator.EvaluationError, "row shape"):
            evaluator._validate_feature_rows(panel)

        panel = copy.deepcopy(next(panel for panel in report["panel_results"] if panel["status"] == "success"))
        panel["normalized_features"]["rows"][0]["features"][0] += 0.01
        with self.assertRaisesRegex(evaluator.EvaluationError, "feature values differ"):
            evaluator._validate_feature_rows(panel)

    def test_real_public_dev_score_payload_runs_in_memory_without_report_write(self) -> None:
        root = Path(__file__).resolve().parents[3]

        def load(path: str, digest: str, description: str) -> dict:
            raw = (root / path).read_bytes()
            self.assertEqual(digest, evaluator._sha(raw), description)
            return evaluator._parse(raw, description)

        report_path = "artifacts/goal22-runs/axis-candidate-evidence-public-v3-dev-prep-20261031-v5/report.json"
        report_sha = "272e80e0b4c2a0a471f0eb7f11ac45c4991aa49835bcf1143194f205d03bd8bb"
        report_hashes = {
            report_path: report_sha,
            evaluator.TRAINING_CONFIG_PATH: evaluator.TRAINING_CONFIG_SHA256,
            evaluator.TRAINING_REPORT_PATH: evaluator.TRAINING_REPORT_SHA256,
            evaluator.CHECKPOINT_PATH: evaluator.CHECKPOINT_SHA256,
            evaluator.FINALIZATION_PATH: evaluator.FINALIZATION_SHA256,
        }
        self._skip_missing_local_prerequisites(
            root,
            [evaluator.DEV_MANIFEST_PATH, *report_hashes],
            {evaluator.DEV_MANIFEST_PATH: evaluator.DEV_MANIFEST_SHA256, **report_hashes},
        )
        manifest = load(evaluator.DEV_MANIFEST_PATH, evaluator.DEV_MANIFEST_SHA256, "pinned public dev manifest")
        annotation_paths = [
            f"artifacts/axis-roles-dev-corpus-v3-20261031/annotations/{case['index']}.json"
            for case in manifest["dev_cases"]
        ]
        annotation_hashes = {
            path: case["annotation_sha256"] for path, case in zip(annotation_paths, manifest["dev_cases"], strict=True)
        }
        self._skip_missing_local_prerequisites(
            root,
            [*report_hashes, *annotation_paths],
            {**report_hashes, **annotation_hashes},
        )
        report = load(report_path, report_sha, "pinned truth-free v5 export report")
        config = load(evaluator.TRAINING_CONFIG_PATH, evaluator.TRAINING_CONFIG_SHA256, "pinned training config")
        training = load(evaluator.TRAINING_REPORT_PATH, evaluator.TRAINING_REPORT_SHA256, "pinned GPU training report")
        checkpoint = load(evaluator.CHECKPOINT_PATH, evaluator.CHECKPOINT_SHA256, "pinned ranker checkpoint")
        finalization = load(evaluator.FINALIZATION_PATH, evaluator.FINALIZATION_SHA256, "pinned training finalization")

        annotations: dict[str, dict] = {}
        for case in manifest["dev_cases"]:
            index = case["index"]
            path = f"artifacts/axis-roles-dev-corpus-v3-20261031/annotations/{index}.json"
            annotation = load(path, case["annotation_sha256"], "pinned public dev annotation")
            self.assertEqual(case["scene_id"], annotation["scene_id"])
            annotations[case["scene_id"]] = annotation

        # Exercise the actual scorer through association, feature verification,
        # checkpoint ranking and metric computation. Keep the result in memory.
        source_bindings = {path: "bound-by-test-input-hashes" for path in evaluator.SCORER_SOURCE_PATHS}
        provenance_row = provenance()
        provenance_row.update({
            "checkpoint_sha256": evaluator.CHECKPOINT_SHA256,
            "training_report_sha256": evaluator.TRAINING_REPORT_SHA256,
            "training_config_sha256": evaluator.TRAINING_CONFIG_SHA256,
            "finalization_sha256": evaluator.FINALIZATION_SHA256,
            "dev_manifest_sha256": evaluator.DEV_MANIFEST_SHA256,
            "panel_export_report_sha256": report_sha,
            "panel_export_input_manifest_sha256": report["input_manifest_sha256"],
            "evaluation_binding_sha256": "in-memory-test-no-binding-artifact",
            "scorer_protocol_sha256": evaluator._sha((root / evaluator.PROTOCOL_PATH).read_bytes()),
            "scorer_source_bindings": source_bindings,
        })
        result = evaluator._score_payload(
            manifest, report, annotations, checkpoint, config, training, finalization, provenance_row,
            expected_source_count=32, expected_panel_count=48)

        self.assertEqual("exploratory_dev_scored_no_acceptance_claim", result["status"])
        self.assertFalse(result["production_approved"])
        self.assertEqual(32, result["source_count"])
        self.assertEqual(48, result["panel_count"])
        self.assertEqual(46, result["scored_panel_count"])
        self.assertEqual({"success": 48, "failed": 0}, result["panel_export_status_counts"])
        self.assertEqual(2, result["association_counters"]["fallback_panels"])
        self.assertEqual(0, result["optimizer_steps"])

    def _runtime_fixture(self) -> tuple[dict, dict, dict]:
        production = {path: "1" * 64 for path in evaluator.DEV_REQUIRED_PRODUCTION_SOURCE_PATHS}
        tool_sources = {path: "2" * 64 for path in evaluator.DEV_REQUIRED_TOOL_SOURCE_PATHS}
        tool_runtime = {
            "tool.dll": "3" * 64,
            "app.dll": "4" * 64,
            "axis.dll": "5" * 64,
            "opencv.dll": "6" * 64,
            "opencv-native.dll": "7" * 64,
        }
        panelizer = {
            path: f"{index + 1:064x}"
            for index, path in enumerate(sorted(evaluator.DEV_REQUIRED_PANELIZER_RUNTIME_PATHS))
        }
        for index in range(evaluator.DEV_PANELIZER_RUNTIME_FILE_COUNT - len(panelizer)):
            panelizer[f"{evaluator.DEV_PANELIZER_RUNTIME_PREFIX}fixture-{index:02d}.bin"] = f"{index + 100:064x}"
        admission_sources = {path: "9" * 64 for path in evaluator.DEV_ADMISSION_SOURCE_PATHS}
        admission_runtime = dict(panelizer)
        admission_runtime["artifacts/goal22-runs/axis-unmasked-runtime-v1/unrelated.bin"] = "a" * 64
        runtime = {
            "tool_assembly_path": "tool.dll", "tool_assembly_sha256": tool_runtime["tool.dll"],
            "app_assembly_path": "app.dll", "app_assembly_sha256": tool_runtime["app.dll"],
            "axis_assembly_path": "axis.dll", "axis_assembly_sha256": tool_runtime["axis.dll"],
            "open_cv_managed_assembly_path": "opencv.dll", "open_cv_managed_assembly_sha256": tool_runtime["opencv.dll"],
            "open_cv_native_module_path": "opencv-native.dll", "open_cv_native_module_sha256": tool_runtime["opencv-native.dll"],
            "production_source_bindings": production,
            "panelizer_runtime_bindings": panelizer,
            "tool_source_bindings": tool_sources,
            "tool_runtime_bindings": tool_runtime,
            "open_cv_options": {
                "UseLineSegmentDetector": True, "UseProbabilisticHough": True,
                "CannyLowThreshold": 40.0, "CannyHighThreshold": 120.0, "HoughThreshold": 24,
                "HoughMinimumLineLengthPixels": 12.0, "HoughMaximumLineGapPixels": 4.0,
                "LsdRefinement": 1,
            },
            "geometry_options": {
                "MaximumAxisDeviationDegrees": 15.0, "MergeAngleToleranceDegrees": 4.0,
                "MergeDistancePixels": 3.0, "MinimumCandidateLengthPixels": 1.0,
                "MinimumAxisSpanFraction": 0.25, "TickMaximumLengthFraction": 0.08,
                "TickAxisDistancePixels": 4.0, "DividerMinimumSpanFraction": 0.45,
                "DividerMinimumCoverageFraction": 0.06, "DottedDividerMinimumSegments": 3,
                "DottedDividerMaximumSegmentFraction": 0.18, "PlotEdgeExclusionFraction": 0.025,
                "GridSpacingCoefficientOfVariation": 0.12, "GridMinimumAlignedLines": 3,
                "NeedsReviewConfidenceThreshold": 0.55,
            },
        }
        export_binding = {
            "schema": "graphreader.axis-candidate-panel-batch-input.v1",
            "scope": "public_synthetic_development", "split": "dev",
            "panelizationAdmissionBindingPath": evaluator.DEV_BINDING_PATH,
            "panelizationAdmissionBindingSha256": evaluator.DEV_BINDING_SHA256,
            "panelizationReceiptPath": evaluator.PANELIZATION_RECEIPT_PATH,
            "panelizationReceiptSha256": evaluator.PANELIZATION_RECEIPT_SHA256,
            "panelizationRunRoot": "artifacts/goal22-runs/axis-roles-dev-measure-run-v2",
            "expectedSourceCount": 32, "expectedDetectedSourceCount": 30,
            "expectedFallbackSourceCount": 2, "expectedFailedSourceCount": 0,
            "expectedPanelCount": 48, "selectedPanels": [],
            "productionSourceBindings": production, "panelizerRuntimeBindings": panelizer,
            "toolSourceBindings": tool_sources, "toolRuntimeBindings": tool_runtime,
            "openCvOptions": {
                "useLineSegmentDetector": True, "useProbabilisticHough": True,
                "cannyLowThreshold": 40.0, "cannyHighThreshold": 120.0, "houghThreshold": 24,
                "houghMinimumLineLengthPixels": 12.0, "houghMaximumLineGapPixels": 4.0,
                "lsdRefinement": "RefineStd",
            },
            "geometryOptions": {
                "maximumAxisDeviationDegrees": 15.0, "mergeAngleToleranceDegrees": 4.0,
                "mergeDistancePixels": 3.0, "minimumCandidateLengthPixels": 1.0,
                "minimumAxisSpanFraction": 0.25, "tickMaximumLengthFraction": 0.08,
                "tickAxisDistancePixels": 4.0, "dividerMinimumSpanFraction": 0.45,
                "dividerMinimumCoverageFraction": 0.06, "dottedDividerMinimumSegments": 3,
                "dottedDividerMaximumSegmentFraction": 0.18, "plotEdgeExclusionFraction": 0.025,
                "gridSpacingCoefficientOfVariation": 0.12, "gridMinimumAlignedLines": 3,
                "needsReviewConfidenceThreshold": 0.55,
            },
            "devCorpusManifestPath": evaluator.DEV_MANIFEST_PATH,
            "devCorpusManifestSha256": evaluator.DEV_MANIFEST_SHA256,
        }
        return runtime, export_binding, {"files": admission_sources, "runtime_files": admission_runtime}

    def test_export_runtime_requires_pinned_default_options_and_adapter(self) -> None:
        runtime, export_binding, admission = self._runtime_fixture()
        def validate_map(_root, expected, observed, name, _stack, *, required=None):
            if expected != observed or (required is not None and not required.issubset(expected)):
                raise evaluator.EvaluationError(f"{name} required source paths differ")
            return dict(expected)

        with patch.object(evaluator, "_validate_hash_map", side_effect=validate_map):
            verified = evaluator._verify_export_runtime(None, {"runtime": runtime}, export_binding, admission, None)
            self.assertEqual("tool.dll", verified["tool_assembly_path"])
            with self.assertRaisesRegex(evaluator.EvaluationError, "runtime receipt is missing"):
                evaluator._verify_export_runtime(None, {}, export_binding, admission, None)
            bad_input = copy.deepcopy(export_binding)
            bad_input["geometryOptions"]["mergeDistancePixels"] = 3.1
            with self.assertRaisesRegex(evaluator.EvaluationError, "options differ"):
                evaluator._verify_export_runtime(None, {"runtime": runtime}, bad_input, admission, None)
            bad_runtime = copy.deepcopy(runtime)
            bad_runtime["open_cv_options"]["CannyLowThreshold"] = 41.0
            with self.assertRaisesRegex(evaluator.EvaluationError, "options differ"):
                evaluator._verify_export_runtime(None, {"runtime": bad_runtime}, export_binding, admission, None)
            bad_runtime = copy.deepcopy(runtime)
            bad_runtime["tool_runtime_bindings"]["unexpected.dll"] = "9" * 64
            bad_input = copy.deepcopy(export_binding)
            bad_input["toolRuntimeBindings"]["unexpected.dll"] = "9" * 64
            with self.assertRaisesRegex(evaluator.EvaluationError, "unexpected assemblies"):
                evaluator._verify_export_runtime(None, {"runtime": bad_runtime}, bad_input, admission, None)
            bad_runtime = copy.deepcopy(runtime)
            bad_runtime["production_source_bindings"] = {"other.cs": "1" * 64}
            bad_input = copy.deepcopy(export_binding)
            bad_input["productionSourceBindings"] = {"other.cs": "1" * 64}
            with self.assertRaisesRegex(evaluator.EvaluationError, "required source paths"):
                evaluator._verify_export_runtime(None, {"runtime": bad_runtime}, bad_input, admission, None)
            bad_admission = copy.deepcopy(admission)
            bad_admission["files"].pop(next(iter(evaluator.DEV_ADMISSION_SOURCE_PATHS)))
            with self.assertRaisesRegex(evaluator.EvaluationError, "fixed dev admission source bundle differs"):
                evaluator._verify_export_runtime(None, {"runtime": runtime}, export_binding, bad_admission, None)
            bad_admission = copy.deepcopy(admission)
            bad_admission["runtime_files"].pop(next(iter(evaluator.DEV_REQUIRED_PANELIZER_RUNTIME_PATHS)))
            with self.assertRaisesRegex(evaluator.EvaluationError, "unexpected file count"):
                evaluator._verify_export_runtime(None, {"runtime": runtime}, export_binding, bad_admission, None)
            bad_admission = copy.deepcopy(admission)
            changed = next(iter(evaluator.DEV_REQUIRED_PANELIZER_RUNTIME_PATHS))
            bad_admission["runtime_files"][changed] = "f" * 64
            with self.assertRaisesRegex(evaluator.EvaluationError, "differ from the fixed dev admission map"):
                evaluator._verify_export_runtime(None, {"runtime": runtime}, export_binding, bad_admission, None)

    def test_train_target_metric_and_rank_order_are_reused(self) -> None:
        manifest, report, annotations, checkpoint, config, training, finalization = payload()
        scored = evaluator._score_payload(
            manifest, report, annotations, checkpoint, config, training, finalization,
            provenance(),
            expected_source_count=1, expected_panel_count=1)
        row = scored["panels"][0]
        self.assertEqual("scored", row["scoring_status"])
        self.assertEqual(1, row["baseline_rank"])
        self.assertEqual(2, row["candidate_rank"])
        self.assertAlmostEqual(0.0, row["candidate_mean_corner_error_pixels"])
        self.assertAlmostEqual(0.0, row["candidate_mean_normalized_corner_error"])
        self.assertAlmostEqual(2 ** 0.5 / 10, row["baseline_mean_normalized_corner_error"])
        self.assertEqual(1, scored["candidate_improved_panel_count"])
        self.assertFalse(scored["production_approved"])

    def test_fallback_and_failed_export_remain_in_panel_denominator(self) -> None:
        manifest, report, annotations, checkpoint, config, training, finalization = payload()
        fallback_source = "scene-fallback"
        manifest["dev_cases"].append({"scene_id": fallback_source, "seed": 8, "index": 1})
        report["source_summaries"].append({"source_id": fallback_source, "source_status": "no_panel_fallback"})
        fallback = panel_row(fallback_source, "panel-fallback", [snapshot(0, 0.0, 0.7)], origin="full_image_review_fallback")
        report["panel_results"].append(fallback)
        annotations[fallback_source] = {
            "schema_version": 1,
            "scene_id": fallback_source,
            "seed": 8,
            "coordinate_space": "original_pixels",
            "plots": [{"box": [20, 10, 100, 100], "coordinate_space": "original_pixels",
                       "panel_id": "fallback-plot", "polygon": [[20, 10], [120, 10], [120, 110], [20, 110]]}],
        }
        scored = evaluator._score_payload(
            manifest, report, annotations, checkpoint, config, training, finalization,
            provenance(),
            expected_source_count=2, expected_panel_count=2)
        self.assertEqual(2, scored["panel_count"])
        self.assertEqual(1, scored["scored_panel_count"])
        self.assertEqual("fallback_not_scored", scored["panels"][1]["scoring_status"])
        self.assertEqual(1, scored["association_counters"]["fallback_panels"])

    def test_detected_panel_failure_with_empty_candidates_is_counted_and_unscored(self) -> None:
        manifest, report, annotations, checkpoint, config, training, finalization = payload()
        failed_source = "scene-failed"
        manifest["dev_cases"].append({"scene_id": failed_source, "seed": 9, "index": 2})
        report["source_summaries"].append({"source_id": failed_source, "source_status": "detected"})
        failed = panel_row(failed_source, "panel-failed", [snapshot(0, 0.0, 0.5)])
        failed["status"] = "failed"
        failed["candidate_snapshots"] = []
        failed["normalized_features"] = None
        failed["failure"] = {"kind": "panel_processing_failure", "code": "TEST_EMPTY", "exception_type": "InvalidDataException"}
        report["panel_results"].append(failed)
        annotations[failed_source] = {
            "schema_version": 1,
            "scene_id": failed_source,
            "seed": 9,
            "coordinate_space": "original_pixels",
            "plots": [{"box": [20, 10, 100, 100], "coordinate_space": "original_pixels",
                       "panel_id": "plot-failed", "polygon": [[20, 10], [120, 10], [120, 110], [20, 110]]}],
        }
        scored = evaluator._score_payload(
            manifest, report, annotations, checkpoint, config, training, finalization,
            provenance(),
            expected_source_count=2, expected_panel_count=2)
        self.assertEqual(2, scored["panel_count"])
        self.assertEqual({"success": 1, "failed": 1}, scored["panel_export_status_counts"])
        self.assertEqual(1, scored["scored_panel_count"])
        failed_result = next(row for row in scored["panels"] if row["panel_id"] == "panel-failed")
        self.assertEqual("export_failed_or_empty", failed_result["scoring_status"])
        self.assertIsNone(failed_result["baseline_mean_corner_error_pixels"])

    def test_dev_export_gate_requires_full_bound_panel_inventory(self) -> None:
        manifest_cases = []
        summaries = []
        panel_results = []
        receipt_sources = []
        for source_index in range(32):
            source_id = f"scene-{source_index}"
            source_sha = f"{source_index:064x}"
            source_status = "detected" if source_index < 30 else "no_panel_fallback"
            manifest_cases.append({"scene_id": source_id, "index": source_index, "image_sha256": source_sha})
            summaries.append({"source_id": source_id, "source_sha256": source_sha, "source_status": source_status,
                              "source_width": 120, "source_height": 120})
            panel_id = f"panel-{source_index}-0"
            panel_sha = f"{source_index + 1:064x}"
            panel_results.append(export_panel(source_id, source_index, source_sha, panel_id, panel_sha,
                                              fallback=source_status == "no_panel_fallback"))
            receipt_sources.append({"id": source_id, "panels": [{"panel_id": panel_id, "panel_sha256": panel_sha}]})
        for source_index in range(16):
            panel_id = f"panel-{source_index}-1"
            panel_sha = f"{source_index + 100:064x}"
            panel_results.append(export_panel(f"scene-{source_index}", source_index, f"{source_index:064x}",
                                              panel_id, panel_sha))
            receipt_sources[source_index]["panels"].append({"panel_id": panel_id, "panel_sha256": panel_sha})
        export = {
            "schema": "graphreader.axis-candidate-panel-batch-evidence.v1",
            "status": "success",
            "scope": "public_synthetic_development",
            "split": "dev",
            "panelization_admission_binding_sha256": evaluator.DEV_BINDING_SHA256,
            "panelization_receipt_sha256": evaluator.PANELIZATION_RECEIPT_SHA256,
            "source_count": 32,
            "panel_count": 48,
            "selected_panel_count": 48,
            "source_summaries": summaries,
            "panel_results": panel_results,
        }
        binding = {
            "scope": "public_synthetic_development",
            "split": "dev",
            "devCorpusManifestPath": evaluator.DEV_MANIFEST_PATH,
            "devCorpusManifestSha256": evaluator.DEV_MANIFEST_SHA256,
            "selectedPanels": [],
            "panelizationAdmissionBindingPath": evaluator.DEV_BINDING_PATH,
            "panelizationAdmissionBindingSha256": evaluator.DEV_BINDING_SHA256,
            "panelizationReceiptPath": evaluator.PANELIZATION_RECEIPT_PATH,
            "panelizationReceiptSha256": evaluator.PANELIZATION_RECEIPT_SHA256,
            "panelizationRunRoot": "artifacts/goal22-runs/axis-roles-dev-measure-run-v2",
            "expectedSourceCount": 32,
            "expectedDetectedSourceCount": 30,
            "expectedFallbackSourceCount": 2,
            "expectedFailedSourceCount": 0,
            "expectedPanelCount": 48,
        }
        receipt = {"sources": receipt_sources}
        evaluator._require_dev_export(export, binding, {"dev_cases": manifest_cases}, receipt)
        failed_panel = panel_results[-1]
        failed_panel["status"] = "failed"
        failed_panel["candidate_snapshots"] = []
        failed_panel["normalized_features"] = None
        failed_panel["failure"] = {"code": "SYNTHETIC_TEST_FAILURE"}
        export["status"] = "completed_with_failures"
        evaluator._require_dev_export(export, binding, {"dev_cases": manifest_cases}, receipt)
        panel_results[-1]["panel_sha256"] = "f" * 64
        with self.assertRaisesRegex(evaluator.EvaluationError, "panel result path or source digest"):
            evaluator._require_dev_export(export, binding, {"dev_cases": manifest_cases}, receipt)

    def test_modified_feature_vector_is_rejected(self) -> None:
        manifest, report, annotations, checkpoint, config, training, finalization = payload()
        report["panel_results"][0]["normalized_features"]["rows"][0]["features"][0] += 0.01
        with self.assertRaisesRegex(evaluator.EvaluationError, "feature values differ"):
            evaluator._score_payload(
                manifest, report, annotations, checkpoint, config, training, finalization,
                provenance(),
                expected_source_count=1, expected_panel_count=1)

    def test_checkpoint_dimensions_and_approval_fail_closed(self) -> None:
        checkpoint, config, training, finalization = checkpoint_and_reports()
        bad = copy.deepcopy(checkpoint)
        bad["weight"].pop()
        with self.assertRaisesRegex(evaluator.EvaluationError, "feature dimensions"):
            evaluator._verify_checkpoint(bad, config, training, finalization)
        bad = copy.deepcopy(checkpoint)
        bad["production_approved"] = True
        with self.assertRaisesRegex(evaluator.EvaluationError, "identity or approval"):
            evaluator._verify_checkpoint(bad, config, training, finalization)


if __name__ == "__main__":
    unittest.main()
