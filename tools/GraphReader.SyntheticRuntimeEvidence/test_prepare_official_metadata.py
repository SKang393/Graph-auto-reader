# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

import prepare_official_metadata as metadata


def _sha(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def _write(path: Path, payload: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)


def test_prepare_binds_existing_probability_tolerance_without_approval(tmp_path, monkeypatch):
    detector_bytes = b"controlled-detector"
    recognizer_bytes = b"controlled-recognizer"
    native_bytes = b"controlled-native-runtime"
    alphabet = "a" * 436 + " "
    detector_sha = _sha(detector_bytes)
    recognizer_sha = _sha(recognizer_bytes)
    native_sha = _sha(native_bytes)

    monkeypatch.setattr(metadata, "DETECTOR", detector_sha)
    monkeypatch.setattr(metadata, "RECOGNIZER", recognizer_sha)
    monkeypatch.setattr(metadata, "ALPHABET", _sha(alphabet.encode("utf-8")))
    monkeypatch.setattr(metadata, "NATIVE_HASHES", {native_sha: "controlled-test-runtime"})

    conversion = tmp_path / "ml/ocr/official_bakeoff/runs/conversion"
    _write(conversion / "PP-OCRv5_mobile_det.onnx", detector_bytes)
    _write(conversion / "en_PP-OCRv5_mobile_rec.onnx", recognizer_bytes)
    config = tmp_path / "ml/ocr/official_bakeoff/runs/huggingface/rec/config.json"
    _write(config, json.dumps({"PostProcess": {"character_dict": ["a"] * 436}}).encode())

    repository_root = Path(__file__).resolve().parents[2]
    for notice in (
        "PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt",
        "PaddlePaddle-PP-OCRv5-Models-Notice.txt",
    ):
        _write(tmp_path / "LICENSES" / notice, (repository_root / "LICENSES" / notice).read_bytes())

    native = tmp_path / "runtime" / "OpenCvSharpExtern.dll"
    _write(native, native_bytes)
    output = tmp_path / "official-metadata-margin-v2"
    candidate_path = metadata.prepare(tmp_path, output, native)

    detector_bytes_first = (output / "detector.json").read_bytes()
    detector = json.loads(detector_bytes_first)
    candidate = json.loads(candidate_path.read_bytes())

    assert detector["outputs"] == [{
        "name": "fetch_name_0",
        "element_type": "float32",
        "layout": "NCHW",
        "shape": [1, 1, "H", "W"],
        "channels": ["text_probability"],
        "activation": "probability_with_1e-5_clamp",
    }]
    assert detector["postprocessing"] == {
        "algorithm": "db_postprocess_v1",
        "score_mode": "fast",
        "probability_threshold": 0.30,
        "box_confidence_threshold": 0.60,
        "unclip_ratio": 1.5,
        "minimum_side_length": 3,
        "maximum_regions": 1000,
    }
    assert detector["benchmarks"] == [{
        "scope": "local-synthetic-seed-diagnostic",
        "production_approved": False,
        "accuracy_status": "not-evaluated-by-this-tool",
    }]
    assert candidate["production_approved"] is False
    assert candidate["detector"] == {
        "model_path": str((conversion / "PP-OCRv5_mobile_det.onnx").resolve()),
        "model_id": "PP-OCRv5_mobile_det",
        "model_version": "5.0.0",
        "model_sha256": detector_sha,
        "manifest_path": str((output / "detector.json").resolve()),
        "manifest_sha256": _sha(detector_bytes_first),
    }
    assert candidate["native_sha256"] == native_sha
    assert candidate["native_scope"] == "controlled-test-runtime"

    assert metadata.prepare(tmp_path, output, native) == candidate_path
    assert (output / "detector.json").read_bytes() == detector_bytes_first

    # A different native payload cannot inherit a known runtime's scope or
    # leave partially bound metadata behind.
    native.write_bytes(b"unreviewed-native-runtime")
    unknown_output = tmp_path / "unknown-runtime"
    with pytest.raises(ValueError, match="not an existing checksum-recorded"):
        metadata.prepare(tmp_path, unknown_output, native)
    assert not unknown_output.exists()
