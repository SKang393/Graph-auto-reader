# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bind existing licensed official OCR bytes for a local synthetic seed diagnostic.

This does not create a production manifest, approval, model-store entry, or weights.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


DETECTOR = "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb"
RECOGNIZER = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743"
ALPHABET = "8b31115bc8675a58b670879950b550e7d9840984954d9584d251fd72a764477a"
NATIVE_HASHES = {
    "1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd": "existing-development-runtime-unapproved-for-release",
    "87c12460daba638b36e916ea2bb832d0759fbf094b8639919a7ce11b0cca5791": "reviewed-source-runtime-local-diagnostic",
    "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31": "reviewed-source-runtime-local-diagnostic",
}


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def pinned(path: Path, expected: str) -> str:
    if sha(path) != expected:
        raise ValueError(f"Pinned input changed: {path.name}")
    return expected


def prepare(root: Path, output: Path, native: Path) -> Path:
    root, output, native = root.resolve(), output.resolve(), native.resolve()
    conversion = root / "ml/ocr/official_bakeoff/runs/conversion"
    detector = conversion / "PP-OCRv5_mobile_det.onnx"
    recognizer = conversion / "en_PP-OCRv5_mobile_rec.onnx"
    pinned(detector, DETECTOR)
    pinned(recognizer, RECOGNIZER)
    notices = {
        "LICENSES/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt": "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4",
        "LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt": "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729",
    }
    for name, expected in notices.items():
        pinned(root / name, expected)
    config_path = root / "ml/ocr/official_bakeoff/runs/huggingface/rec/config.json"
    config = json.loads(config_path.read_text(encoding="utf-8"))
    alphabet = "".join(config["PostProcess"]["character_dict"]) + " "
    if hashlib.sha256(alphabet.encode("utf-8")).hexdigest() != ALPHABET or len(alphabet) != 437:
        raise ValueError("Official recognizer alphabet changed")
    native_sha = sha(native)
    if native_sha not in NATIVE_HASHES:
        raise ValueError("Native runtime is not an existing checksum-recorded project runtime")

    def manifest(model: Path, task: str, model_sha: str) -> dict:
        return {
            "manifest_version": 1, "model_id": model.stem, "model_version": "5.0.0",
            "task": task,
            "source": {"name": "PaddleOCR", "url": "https://github.com/PaddlePaddle/PaddleOCR",
                       "revision": "33cbdd9deb2e00f61e7966db70669b249c005a37"},
            "license": {"spdx": "Apache-2.0", "notice_path": "LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt", "reviewed": True},
            "sha256": model_sha, "files": [model.name], "commercial_use": True,
            "redistribution": True, "providers": ["cpu"],
            "benchmarks": [{"scope": "local-synthetic-seed-diagnostic", "production_approved": False,
                            "accuracy_status": "not-evaluated-by-this-tool"}],
        }

    det = manifest(detector, "ocr_detection", DETECTOR)
    det.update({
        "inputs": [{"name": "x", "element_type": "float32", "layout": "NCHW", "shape": [1, 3, "H", "W"], "channels": ["b", "g", "r"]}],
        "outputs": [{"name": "fetch_name_0", "element_type": "float32", "layout": "NCHW", "shape": [1, 1, "H", "W"], "channels": ["text_probability"], "activation": "probability_with_1e-5_clamp"}],
        "preprocessing": {"channel_order": "BGR", "channel_means": [0.485, 0.456, 0.406], "channel_scales": [1 / 0.229, 1 / 0.224, 1 / 0.225], "maximum_side_length": 960, "dimension_multiple": 128},
        "postprocessing": {"algorithm": "db_postprocess_v1", "score_mode": "fast", "probability_threshold": 0.30, "box_confidence_threshold": 0.60, "unclip_ratio": 1.5, "minimum_side_length": 3, "maximum_regions": 1000},
    })
    rec = manifest(recognizer, "ocr_recognition", RECOGNIZER)
    rec.update({
        "inputs": [{"name": "x", "element_type": "float32", "layout": "NCHW", "shape": ["N", 3, 48, "W"], "channels": ["b", "g", "r"]}],
        "outputs": [{"name": "fetch_name_0", "element_type": "float32", "layout": "NTC", "shape": ["N", "T", "C"], "alphabet": alphabet, "blank_class_index": 0}],
        "preprocessing": {"channel_order": "BGR", "channel_means": [0.5, 0.5, 0.5], "channel_scales": [2, 2, 2], "width_policy": "paddle_batch_max_wh_ratio_v1", "minimum_width": 320, "maximum_width": 4096},
        "postprocessing": {"algorithm": "ctc_greedy_alternatives_v1", "maximum_alternatives": 3},
    })
    outputs = {}
    descriptors = {}
    for role, model, data in [("detector", detector, det), ("recognizer", recognizer, rec)]:
        path = output / f"{role}.json"
        content = (json.dumps(data, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
        outputs[path] = content
        descriptors[role] = {"model_path": str(model), "model_id": data["model_id"],
                             "model_version": data["model_version"], "model_sha256": data["sha256"],
                             "manifest_path": str(path), "manifest_sha256": hashlib.sha256(content).hexdigest()}
    record = {"schema": "graphreader.local-synthetic-ocr-candidate.v1", "production_approved": False,
              "native_path": str(native), "native_sha256": native_sha, "native_scope": NATIVE_HASHES[native_sha],
              "license_inputs": [{"path": str(root / name), "sha256": value} for name, value in notices.items()],
              **descriptors}
    record_path = output / "candidate.json"
    outputs[record_path] = (json.dumps(record, indent=2) + "\n").encode("utf-8")
    for path, content in outputs.items():
        if path.exists() and path.read_bytes() != content:
            raise ValueError(f"Refusing to overwrite changed metadata: {path.name}")
    output.mkdir(parents=True, exist_ok=True)
    for path, content in outputs.items():
        if not path.exists():
            with path.open("xb") as stream:
                stream.write(content)
    return record_path


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--native", type=Path, required=True)
    args = parser.parse_args()
    result = prepare(args.root, args.output, args.native)
    print(json.dumps({"candidate": str(result), "sha256": sha(result)}))
