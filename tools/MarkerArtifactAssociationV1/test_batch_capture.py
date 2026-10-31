# SPDX-License-Identifier: Apache-2.0
"""No-truth input-binding, fixed 2px matcher, and one-source generator tests."""
from hashlib import sha256
import json

import pytest

from tools.MarkerArtifactAssociationV1.batch_capture import (
    CAPTURE_REPORT_SHA256,
    bind_report_metadata,
    classify_artifact_proximity,
    _canonical,
)


def _fixture_batch():
    sources, events, panels = [], [], {}
    for index in range(64):
        source_id = f"source-{index:03d}"
        scene_id = f"scene-{index:03d}"
        panel_id = f"panel-id-{index:03d}"
        image_sha = f"{index + 1:064x}"
        file_name = f"panel-{index:04d}.json"
        panel = {
            "source_id": source_id, "case_id": f"case-{index}", "scene_id": scene_id,
            "scene_sha256": f"{index + 100:064x}", "seed": index, "panel_id": panel_id,
            "panel_sha256": image_sha, "provenance": {},
            "frame": {"width": 1200, "height": 350}, "center_markers": [],
            "ocr_regions": [], "classifier_inputs": [], "symbol_input_ids": [],
            "symbol_crop_input_ids": [], "original_pixel_content_bounds": {},
            "sampled_local_and_experimental_wide_views": [],
        }
        panel_sha = sha256(_canonical(panel)).hexdigest()
        panels[file_name] = (panel, panel_sha)
        provenance = {
            "identity": True, "source_sha256": image_sha, "panel_image_sha256": image_sha,
            "source_width": 1200, "source_height": 350,
            "source_to_panel_matrix": [1, 0, 0, 0, 1, 0, 0, 0, 1],
            "panel_to_source_matrix": [1, 0, 0, 0, 1, 0, 0, 0, 1],
        }
        sources.append({
            "source_index": index, "seed": index, "source_id": source_id,
            "scene_id": scene_id, "scene_sha256": panel["scene_sha256"],
            "image_sha256": image_sha, "status": "completed", "failure": None,
            "panel_count": 1,
            "panels": [{"panel_id": panel_id, "status": "completed", "marker_count": 0,
                        "provenance": provenance}],
        })
        events.append({"source_id": source_id, "panel_id": panel_id,
                       "file": file_name, "sha256": panel_sha})
    report = {
        "schema": "graphreader.marker-context-bound-input-full-capture-report.v1",
        "source_count": 64, "source_accounting_complete": True, "all_sources_succeeded": True,
        "truth_content_read": False, "truth_path_resolved": False,
        "private_input_supplied": False, "sealed_input_supplied": False,
        "dev_input_supplied": False, "training_approved": False, "optimizer_steps": 0,
        "capture_binding_sha256": "f" * 64, "sources": sources, "events": events,
    }
    return report, panels


def test_metadata_binder_requires_all_64_pinned_completed_identity_panels():
    report, panels = _fixture_batch()
    bound = bind_report_metadata(report, panels, report_sha256=CAPTURE_REPORT_SHA256,
                                 expected_seeds=list(range(64)))
    assert bound["source_count"] == bound["panel_count"] == 64
    assert bound["truth_content_read"] is False
    assert bound["sources"][0]["candidate_count"] == 0


@pytest.mark.parametrize("mutation", ["event_hash", "panel_identity", "transform", "scope"])
def test_metadata_binder_rejects_malformed_present_inputs(mutation):
    report, panels = _fixture_batch()
    if mutation == "event_hash":
        first = panels["panel-0000.json"]
        panels["panel-0000.json"] = (first[0], "0" * 64)
    elif mutation == "panel_identity":
        first = panels["panel-0000.json"]
        changed = dict(first[0], source_id="wrong-source")
        panels["panel-0000.json"] = (changed, sha256(_canonical(changed)).hexdigest())
    elif mutation == "transform":
        report["sources"][0]["panels"][0]["provenance"]["source_to_panel_matrix"] = [1, 0, 1, 0, 1, 0, 0, 0, 1]
    else:
        report["private_input_supplied"] = True
    with pytest.raises(ValueError):
        bind_report_metadata(report, panels, report_sha256=CAPTURE_REPORT_SHA256,
                             expected_seeds=list(range(64)))


def test_artifact_matching_is_inclusive_at_two_pixels_and_joint_with_markers():
    at_two = classify_artifact_proximity(
        candidates=[{"candidate_id": "c1", "center": [12.0, 10.0],
                     "detector_candidate": True, "symbol_input": False}],
        marker_points=[], artifact_points=[[10.0, 10.0]], overlay_points=[],
        source_width=100, source_height=100,
    )
    assert at_two["status_counts"] == {"matched_artifact_review_only": 1}

    joint_collision = classify_artifact_proximity(
        candidates=[{"candidate_id": "c1", "center": [10.0, 10.0],
                     "detector_candidate": True, "symbol_input": False}],
        marker_points=[[10.0, 10.0]], artifact_points=[[10.0, 10.0]], overlay_points=[],
        source_width=100, source_height=100,
    )
    assert joint_collision["status_counts"] == {"review_ambiguous": 1}

    duplicate = classify_artifact_proximity(
        candidates=[{"candidate_id": "c1", "center": [10.0, 10.0],
                     "detector_candidate": True, "symbol_input": False},
                    {"candidate_id": "c2", "center": [10.0, 10.0],
                     "detector_candidate": True, "symbol_input": False}],
        marker_points=[], artifact_points=[[10.0, 10.0]], overlay_points=[],
        source_width=100, source_height=100,
    )
    assert duplicate["status_counts"] == {"unmatched_review_only": 1}


def test_general_batch_producer_reproduces_single_fixed_overlay_capture():
    from tools.MarkerArtifactAssociationV1.batch_producer import produce_v5_source_visibility
    from tools.MarkerArtifactAssociationV1.full_capture_producer import EXPECTED_CAPTURE

    expected = {key: EXPECTED_CAPTURE[key] for key in
                ("seed", "source_id", "scene_id", "scene_sha256", "width", "height")}
    expected["image_sha256"] = EXPECTED_CAPTURE["source_raster_sha256"]
    result = produce_v5_source_visibility(seed=20820000, expected_source=expected)
    assert result["source_identity"]["image_sha256"] == EXPECTED_CAPTURE["source_raster_sha256"]
    assert result["source_identity"]["scene_sha256"] == EXPECTED_CAPTURE["scene_sha256"]
    assert result["rendered_overlay_center"] == [125, 116]
    assert len(result["glyph_results"]) == 1
    assert result["glyph_results"][0]["visibility_evidence"]["changed_pixel_count"] == 44


def _analysis_fixture():
    from tools.MarkerArtifactAssociationV1.batch_capture import (
        CAPTURE_REPORT_SHA256, TRAIN_SOURCE_BINDING_SHA256,
    )
    from ml.synthetic.marker_context_train_corpus_v5.plan import fixed_plan

    rows, raw_panels = [], {}
    for index, planned in enumerate(fixed_plan()):
        seed = planned["recipe"]["seed"]
        source_id, scene_id, panel_id = f"mock-source-{index}", f"mock-scene-{index}", f"mock-panel-{index}"
        raster_sha, scene_sha = f"{index + 1:064x}", f"{index + 100:064x}"
        file_name = f"panel-{index:04d}.json"
        panel = {
            "source_id": source_id, "case_id": f"mock-case-{index}", "scene_id": scene_id,
            "scene_sha256": scene_sha, "seed": seed, "panel_id": panel_id,
            "panel_sha256": raster_sha, "provenance": {},
            "frame": {"width": 1200, "height": 350}, "center_markers": [],
            "ocr_regions": [], "classifier_inputs": [], "symbol_input_ids": [],
            "symbol_crop_input_ids": [], "original_pixel_content_bounds": {},
            "sampled_local_and_experimental_wide_views": [],
        }
        raw = json.dumps(panel, sort_keys=True, separators=(",", ":")).encode()
        raw_panels[f"artifacts/goal22-runs/marker-context-bound-input-full-capture-v4/panels/{file_name}"] = raw
        rows.append({
            "seed": seed, "source_index": index, "source_id": source_id,
            "scene_id": scene_id, "scene_sha256": scene_sha, "source_raster_sha256": raster_sha,
            "width": 1200, "height": 350, "panel_id": panel_id,
            "panel_result_file": file_name, "panel_result_sha256": sha256(raw).hexdigest(),
            "candidate_count": 0, "source_to_panel_matrix": [1, 0, 0, 0, 1, 0, 0, 0, 1],
            "panel_to_source_matrix": [1, 0, 0, 0, 1, 0, 0, 0, 1],
        })
    manifest = {
        "schema": "graphreader.marker-artifact-v5-train-input-binding.v1",
        "split": "train", "capture_report_sha256": CAPTURE_REPORT_SHA256,
        "capture_binding_sha256": "a" * 64,
        "train_source_binding_sha256": TRAIN_SOURCE_BINDING_SHA256,
        "source_count": 64, "panel_count": 64,
        "truth_content_read": False, "truth_path_resolved": False,
        "dev_input_supplied": False, "private_input_supplied": False,
        "sealed_input_supplied": False, "training_approved": False,
        "sources": rows,
    }
    return manifest, raw_panels


def _mock_rendered_source(expected_source):
    return {
        "source_identity": expected_source,
        "source_raster_sha256": expected_source["image_sha256"],
        "positive_marker_points_for_suppression": [],
        "rendered_overlay_center": [4.0, 4.0],
        "glyph_results": [],
    }


def test_mocked_full_64_analysis_is_source_bound_and_aggregate_only(monkeypatch):
    import tools.MarkerArtifactAssociationV1.batch_capture as batch_capture
    import tools.MarkerArtifactAssociationV1.batch_producer as batch_producer

    input_manifest, panels = _analysis_fixture()
    expected_input_sha = sha256(_canonical(input_manifest)).hexdigest()
    result_reference = "artifacts/goal22-runs/marker-artifact-v5-mock-analysis-v1/report.json"
    source_runtime = batch_capture.build_source_runtime_manifest(
        input_manifest, result_reference=result_reference)
    assert source_runtime["capture_binding_sha256"] == input_manifest["capture_binding_sha256"]
    expected_source_sha = sha256(_canonical(source_runtime)).hexdigest()
    monkeypatch.setattr(batch_capture, "prepare_full_capture_input_manifest", lambda: input_manifest)
    original_read = batch_capture._read_project_file

    def read_bound_or_panel(relative):
        key = str(relative).replace("\\", "/")
        if key in panels:
            return panels[key]
        return original_read(relative)

    monkeypatch.setattr(batch_capture, "_read_project_file", read_bound_or_panel)
    monkeypatch.setattr(batch_producer, "produce_v5_source_visibility",
                        lambda *, seed, expected_source: _mock_rendered_source(expected_source))

    result = batch_capture.analyze_bound_full_capture(
        input_manifest, expected_manifest_sha256=expected_input_sha,
        source_runtime_manifest=source_runtime,
        expected_source_runtime_sha256=expected_source_sha,
        result_reference=result_reference,
    )
    assert result["result_reference"] == result_reference
    assert result["source_runtime_manifest_sha256"] == expected_source_sha
    assert result["source_count"] == result["completed_source_count"] == 64
    assert result["failed_source_count"] == 0
    assert result["source_accounting_complete"] is True
    assert result["artifact_status_counts"] == {}
    assert result["negative_training_rows"] == result["trainable_rows"] == 0
    assert result["training_approved"] is False


def test_mocked_full_64_analysis_fails_closed_on_one_reproduction_mismatch(monkeypatch):
    import tools.MarkerArtifactAssociationV1.batch_capture as batch_capture
    import tools.MarkerArtifactAssociationV1.batch_producer as batch_producer

    input_manifest, panels = _analysis_fixture()
    expected_input_sha = sha256(_canonical(input_manifest)).hexdigest()
    result_reference = "artifacts/goal22-runs/marker-artifact-v5-mock-analysis-v2/report.json"
    source_runtime = batch_capture.build_source_runtime_manifest(
        input_manifest, result_reference=result_reference)
    expected_source_sha = sha256(_canonical(source_runtime)).hexdigest()
    monkeypatch.setattr(batch_capture, "prepare_full_capture_input_manifest", lambda: input_manifest)
    original_read = batch_capture._read_project_file

    def read_bound_or_panel(relative):
        key = str(relative).replace("\\", "/")
        return panels[key] if key in panels else original_read(relative)

    monkeypatch.setattr(batch_capture, "_read_project_file", read_bound_or_panel)

    def render(*, seed, expected_source):
        output = _mock_rendered_source(expected_source)
        if seed == input_manifest["sources"][0]["seed"]:
            output["source_identity"] = {**expected_source, "image_sha256": "0" * 64}
        return output

    monkeypatch.setattr(batch_producer, "produce_v5_source_visibility", render)
    result = batch_capture.analyze_bound_full_capture(
        input_manifest, expected_manifest_sha256=expected_input_sha,
        source_runtime_manifest=source_runtime,
        expected_source_runtime_sha256=expected_source_sha,
        result_reference=result_reference,
    )
    assert result["source_count"] == 64
    assert result["completed_source_count"] == 63
    assert result["failed_source_count"] == 1
    assert result["all_sources_succeeded"] is False
    assert result["sources"][0]["status"] == "failed"
    assert result["sources"][0]["failure_type"] == "ValueError"
    assert result["sources"][0]["legend_glyph_count"] == 0
    assert result["negative_training_rows"] == result["trainable_rows"] == 0


def test_source_runtime_manifest_rejects_caller_forgery_before_any_source_work():
    import tools.MarkerArtifactAssociationV1.batch_capture as batch_capture

    input_manifest, _ = _analysis_fixture()
    result_reference = "artifacts/goal22-runs/marker-artifact-v5-mock-analysis-v3/report.json"
    source_runtime = batch_capture.build_source_runtime_manifest(
        input_manifest, result_reference=result_reference)
    tampered = dict(source_runtime)
    tampered["renderer_bundle_sha256"] = "0" * 64
    with pytest.raises(ValueError, match="source/runtime manifest differs"):
        batch_capture.verify_source_runtime_manifest(
            input_manifest, tampered,
            expected_source_runtime_sha256=sha256(_canonical(tampered)).hexdigest(),
            result_reference=result_reference,
        )


def test_caller_pinned_launcher_exclusive_report_and_hash_receipt(monkeypatch):
    import tempfile
    from pathlib import Path

    import tools.MarkerArtifactAssociationV1.batch_capture as batch_capture
    import tools.MarkerArtifactAssociationV1.run_batch_analysis as launcher

    input_manifest, _ = _analysis_fixture()
    run_root = batch_capture.ROOT / "artifacts" / "goal22-runs"
    run_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="marker-analysis-launch-test-", dir=run_root) as temp_name:
        temp_dir = Path(temp_name)
        relative_dir = temp_dir.relative_to(batch_capture.ROOT).as_posix()
        result_reference = f"{relative_dir}/report.json"
        input_path = temp_dir / "input-manifest.json"
        source_path = temp_dir / "source-runtime-manifest.json"
        binding_path = temp_dir / "caller-binding.json"
        source_runtime = batch_capture.build_source_runtime_manifest(
            input_manifest, result_reference=result_reference)
        input_raw = _canonical(input_manifest)
        source_raw = _canonical(source_runtime)
        input_path.write_bytes(input_raw)
        source_path.write_bytes(source_raw)
        caller = {
            "schema": "graphreader.marker-artifact-v5-analysis-caller-binding.v1",
            "input_manifest_path": input_path.relative_to(batch_capture.ROOT).as_posix(),
            "input_manifest_sha256": sha256(input_raw).hexdigest(),
            "source_runtime_manifest_path": source_path.relative_to(batch_capture.ROOT).as_posix(),
            "source_runtime_manifest_sha256": sha256(source_raw).hexdigest(),
            "result_reference": result_reference,
            "optimizer_steps": 0,
            "real_64_source_analysis_executed": False,
            "truth_read": False,
        }
        caller_raw = _canonical(caller)
        binding_path.write_bytes(caller_raw)
        caller_sha = sha256(caller_raw).hexdigest()
        called = []
        report = {
            "schema": "graphreader.marker-artifact-v5-train-visibility-audit.v1",
            "status": "completed", "source_count": 64,
            "completed_source_count": 64, "failed_source_count": 0,
            "training_approved": False, "optimizer_steps": 0,
            "truth_content_read": False, "private_input_supplied": False,
            "sealed_input_supplied": False,
        }

        def mocked_analysis(*args, **kwargs):
            called.append((args, kwargs))
            return report

        monkeypatch.setattr(batch_capture, "analyze_bound_full_capture", mocked_analysis)
        result = launcher.execute(binding_path.relative_to(batch_capture.ROOT).as_posix(), caller_sha)
        report_path = batch_capture.ROOT / result_reference
        receipt_path = batch_capture.ROOT / (result_reference + ".receipt.json")
        saved_report = report_path.read_bytes()
        saved_receipt = json.loads(receipt_path.read_text())
        assert sha256(saved_report).hexdigest() == result["report_sha256"]
        assert saved_receipt["report_sha256"] == result["report_sha256"]
        assert saved_receipt["caller_binding_sha256"] == caller_sha
        assert saved_receipt["result_reference"] == result_reference
        assert len(called) == 1

        with pytest.raises(ValueError, match="already exists"):
            launcher.execute(binding_path.relative_to(batch_capture.ROOT).as_posix(), caller_sha)
        assert len(called) == 1


def test_caller_pinned_launcher_rejects_unpinned_binding_before_analysis(monkeypatch):
    from pathlib import Path
    import tempfile

    import tools.MarkerArtifactAssociationV1.batch_capture as batch_capture
    import tools.MarkerArtifactAssociationV1.run_batch_analysis as launcher

    run_root = batch_capture.ROOT / "artifacts" / "goal22-runs"
    run_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="marker-analysis-pin-test-", dir=run_root) as temp_name:
        binding_path = Path(temp_name) / "caller-binding.json"
        binding_path.write_text("{}", encoding="utf-8")
        called = []
        monkeypatch.setattr(batch_capture, "analyze_bound_full_capture",
                            lambda *args, **kwargs: called.append(True))
        with pytest.raises(ValueError, match="external pin"):
            launcher.execute(binding_path.relative_to(batch_capture.ROOT).as_posix(), "0" * 64)
        assert called == []
