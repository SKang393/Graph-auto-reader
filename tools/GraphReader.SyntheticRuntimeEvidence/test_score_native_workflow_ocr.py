# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from copy import deepcopy
import json

import pytest

import score_native_workflow_ocr as scorer


def region(identifier="text"):
    return {"region_id": identifier, "coordinate_space": "original_pixels",
            "polygon": {"points": [{"x": 2, "y": 3}, {"x": 8, "y": 3},
                                    {"x": 8, "y": 7}, {"x": 2, "y": 7}]},
            "detection_confidence": 0.9, "text": "20", "role": "YTick"}


def fixture(root):
    folder = root / "execution"
    frozen = folder / "frozen-inputs"
    frozen.mkdir(parents=True)

    def put(path, value):
        target = root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(value), encoding="utf-8")
        return scorer.reference(root, target)

    source_path = root / "source.png"
    source_path.write_bytes(b"test-only synthetic bytes")
    source_hash = scorer.digest(source_path)
    (frozen / "sources").mkdir()
    (frozen / "sources" / (source_hash + ".png")).write_bytes(source_path.read_bytes())
    protocol = put("protocol.json", {"scope": "synthetic"})
    model = {"payload": scorer.reference(root, source_path), "manifest": protocol}
    binding = put("execution/frozen-inputs/candidate-binding.json", {
        "protocol": protocol, "managed_files": [protocol], "native_files": [protocol],
        **{key: model for key in ("ocr_detection", "ocr_recognition", "marker_center", "marker_classifier")}})
    inputs = put("execution/frozen-inputs/input-manifest.json", {
        "protocol_sha256": protocol["sha256"],
        "sources": [{"sha256": source_hash, "relative_path": "source.png", "width": 100, "height": 100}]})
    envelope = {"candidate_binding_sha256": binding["sha256"], "input_manifest_sha256": inputs["sha256"],
                "synthetic_only": True, "private_corpus_access": False, "sealed_corpus_access": False}
    calibration = {**envelope, "panel_source": {
        "source": {"image": {"sha256": source_hash}}, "panel_image_sha256": "panel-image",
        "encoded_crop_in_source_pixels": {"x": 10, "y": 20, "width": 30, "height": 40},
        "panel_to_source_matrix": [1, 0, 10, 0, 1, 20, 0, 0, 1]},
        "observation": {"panel_id": "panel", "ocr": {
            "panel_id": "panel", "input_sha256": "panel-image", "regions": [region()]}}}
    raw = {**envelope, "schema": "graphreader.synthetic-workflow-raw-ocr-observation.v1",
           "observation": {"panel_id": "panel", "input_sha256": "panel-image", "width": 30, "height": 40,
                           "supplied_regions": False, "raw_detector_regions": [region()]}}
    report = {"schema": "graphreader.real-acceptance-frozen-synthetic-report.v1",
              "scope": "local-synthetic-frozen-candidate-diagnostic", "raw_ocr_observation_enabled": True,
              **{key: False for key in ("private_corpus_access", "sealed_corpus_access", "truth_consumed_by_inference",
                                       "production_approved", "model_selection_performed")},
              "candidate_binding_sha256": binding["sha256"], "input_manifest_sha256": inputs["sha256"],
              "protocol_sha256": protocol["sha256"], "source_count": 1,
              "cases": [{"image_sha256": source_hash, "status": "failed"}]}

    def save():
        cal_ref = put("execution/calibration.json", calibration)
        raw_ref = put("execution/raw.json", raw)
        report["calibration_diagnostic_files"] = [{**cal_ref, "path": "calibration.json", "panel_id": "panel"}]
        report["raw_ocr_diagnostic_files"] = [{**raw_ref, "path": "raw.json", "panel_id": "panel", "input_sha256": "panel-image"}]
        return put("execution/report.json", report)

    return report, calibration, raw, save, put


def test_authenticates_failed_source_and_maps_crop_once(tmp_path):
    _, _, _, save, _ = fixture(tmp_path)
    report, sources, panels, observations = scorer.authenticate_runtime(tmp_path, save())
    source_hash = next(iter(sources))
    assert report["cases"][0]["status"] == "failed"
    assert len(panels) == 1
    for kind in observations:
        box = observations[kind][source_hash][0].box
        assert (box.left, box.top, box.right, box.bottom) == (12, 23, 18, 27)
    assert observations["raw_detector"][source_hash][0].text == ""
    assert observations["assembled_ocr"][source_hash][0].text == "20"


@pytest.mark.parametrize("key", ["private_corpus_access", "sealed_corpus_access", "truth_consumed_by_inference",
                                  "production_approved", "model_selection_performed"])
def test_forbidden_scope_rejected_before_other_artifacts(tmp_path, key):
    report, _, _, save, _ = fixture(tmp_path)
    report[key] = True
    ref = save()
    (tmp_path / "protocol.json").unlink()
    with pytest.raises(scorer.EvidenceError, match="scope declaration"):
        scorer.authenticate_runtime(tmp_path, ref)


@pytest.mark.parametrize("mutation, message", [
    (lambda raw: raw["observation"].update(supplied_regions=True), "supplied regions"),
    (lambda raw: raw["observation"].update(input_sha256="wrong"), "raw input identity"),
    (lambda raw: raw["observation"]["raw_detector_regions"].append(region()), "duplicate/empty region"),
    (lambda raw: raw.update(private_corpus_access=True), "private/sealed"),
])
def test_invalid_raw_observation_fails_closed(tmp_path, mutation, message):
    _, _, raw, save, _ = fixture(tmp_path)
    mutation(raw)
    with pytest.raises(scorer.EvidenceError, match=message):
        scorer.authenticate_runtime(tmp_path, save())


def test_missing_raw_panel_cannot_be_replaced_by_assembled_output(tmp_path):
    report, _, _, save, put = fixture(tmp_path)
    save()
    report["raw_ocr_diagnostic_files"] = []
    ref = put("execution/report.json", report)
    with pytest.raises(scorer.EvidenceError, match="missing raw panel"):
        scorer.authenticate_runtime(tmp_path, ref)


def test_tampered_file_and_escaped_execution_path_rejected(tmp_path):
    _, _, _, save, _ = fixture(tmp_path)
    ref = save()
    (tmp_path / "execution/raw.json").write_text("{}", encoding="utf-8")
    with pytest.raises(scorer.EvidenceError, match="checksum mismatch"):
        scorer.authenticate_runtime(tmp_path, ref)
    with pytest.raises(scorer.EvidenceError, match="escaped"):
        scorer.authenticated_path(tmp_path, {"path": "../protocol.json", "sha256": scorer.digest(tmp_path / "protocol.json")},
                                  base=tmp_path / "execution")


def test_raw_geometry_never_depends_on_recognized_text_or_role():
    truth = scorer.metric.FullTextTruth("t", "source", scorer.metric.Box(2, 3, 8, 7), "99", "y_tick", "ytick")
    raw = scorer.predictions([region()], "p", "source", (0, 0, 30, 40), raw=True)
    changed = deepcopy(region())
    changed.update(text="completely different", role="Annotation")
    other = scorer.predictions([changed], "p", "source", (0, 0, 30, 40), raw=True)
    result = scorer.score_geometry([truth], {"source": raw, "no-truth": other}, {"source", "no-truth"})
    assert raw == other
    assert result["matched_regions"] == 1
    assert result["false_positives"] == 1
    assert result["precision"] == 0.5 and result["recall"] == 1
    assert "recognition_exact_accuracy" not in result


def test_generator_panel_omission_is_not_ignored(tmp_path):
    _, _, _, save, put = fixture(tmp_path)
    _, sources, panels, _ = scorer.authenticate_runtime(tmp_path, save())
    annotation = put("annotation.json", {})
    scene = put("scene.json", {"panels": [{}, {}]})
    generator = put("generator.json", {"private_reads": 0, "sealed_reads": 0, "sources": [{
        "split": "dev", "image": scorer.reference(tmp_path, tmp_path / "source.png"),
        "annotation": annotation, "scene": scene}]})
    with pytest.raises(scorer.EvidenceError, match="physical panel"):
        scorer.load_truths(tmp_path, generator, sources, panels)
