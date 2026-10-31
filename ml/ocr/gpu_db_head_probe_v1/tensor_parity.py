# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare fixed train-only preflight tensors to the C# production methods.

This is a diagnostic for the experimental 512/128 input configuration. It is
not a model inference, optimizer, production approval or full workflow replay.
"""
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path

import numpy as np
from PIL import Image

from ml.ocr.gpu_db_head_probe_v1 import feature_preflight as preflight
from ml.ocr.gpu_db_head_probe_v1 import parity, tensor_parity_identity

PREFLIGHT = parity.ROOT / "artifacts/synthetic-tick-feature-preflight-v1-20261030/report.json"
PREFLIGHT_SHA = "e3cf0880968123fb61164c068607298b90e119388363de6c60320b672d5927f3"


def compare_tensors(expected: np.ndarray, actual_bytes: bytes) -> dict:
    expected = np.ascontiguousarray(expected, dtype="<f4")
    if len(actual_bytes) != expected.nbytes or not np.isfinite(expected).all():
        raise ValueError("Comparison tensor has invalid shape or values")
    actual = np.frombuffer(actual_bytes, dtype="<f4").reshape(expected.shape)
    if not np.isfinite(actual).all():
        raise ValueError("Captured tensor contains nonfinite values")
    original_bytes = expected.tobytes()
    return {"byte_identical": actual_bytes == original_bytes,
            "different_bytes": sum(a != b for a, b in zip(original_bytes, actual_bytes, strict=True)),
            "different_float32_bits": int(np.count_nonzero(expected.view("<u4") != actual.view("<u4"))),
            "max_absolute_difference": float(np.max(np.abs(expected - actual), initial=0))}


def run(capture_dir: Path, output: Path, identity_path: Path, identity_sha: str) -> dict:
    root = (parity.ROOT / "artifacts").resolve()
    capture_dir, output = capture_dir.resolve(strict=True), output.resolve()
    if not capture_dir.is_relative_to(root) or not output.is_relative_to(root) or output.exists():
        raise ValueError("Capture and new report must be under project artifacts")
    identity = tensor_parity_identity.load(identity_path, identity_sha)
    capture_bytes = (capture_dir / "capture.json").read_bytes()
    capture = preflight._json(capture_bytes)
    tensor_parity_identity.check_capture(identity, capture, identity_sha)
    previous = preflight._json(parity._read_locked(PREFLIGHT, PREFLIGHT_SHA))
    if (capture.get("schema") != "graphreader.synthetic-tensor-parity-capture.v1" or
            capture.get("maximum_side_length") != 512 or capture.get("dimension_multiple") != 128 or
            capture.get("optimizer_steps") != 0 or capture.get("training_input_approved") is not False or
            capture.get("model_inference") is not False or capture.get("opencv_version") != "4.13.0" or
            len(capture.get("images", [])) != 4 or len(previous.get("cases", [])) != 4):
        raise ValueError("C# capture scope or library version differs")
    records = preflight._train_inputs()
    rows = []
    for index, ((source, png, annotation), captured, saved) in enumerate(zip(records, capture["images"], previous["cases"], strict=True)):
        if (source["image"]["sha256"] != captured.get("source_image_sha256") or
                source["image"]["sha256"] != saved.get("source_image_sha256") or
                source["annotation"]["sha256"] != saved.get("source_annotation_sha256") or
                captured.get("tensor_file") != f"train-{index}.tensor.f32" or
                captured.get("bgr_file") != f"train-{index}.bgr.u8"):
            raise ValueError("Capture source or file identity differs")
        tensor, _, _, _ = preflight._prepare(png, annotation)
        if sha256(tensor.tobytes()).hexdigest() != saved["tensor_sha256"] or list(tensor.shape) != saved["tensor_shape"]:
            raise ValueError("Regenerated preflight tensor differs from saved run")
        if captured.get("shape") != list(tensor.shape):
            raise ValueError("C# and preflight tensor dimensions differ")
        raw = parity._read_locked(capture_dir / captured["tensor_file"], captured["tensor_sha256"])
        result = compare_tensors(tensor, raw)
        with Image.open(BytesIO(png)) as image:
            rgba = image.convert("RGBA")
            rgb = np.asarray(Image.alpha_composite(Image.new("RGBA", image.size, (255, 255, 255, 255)), rgba).convert("RGB"), dtype=np.uint8)
            candidate_bgr = np.ascontiguousarray(rgb[:, :, ::-1]).tobytes()
        production_bgr = parity._read_locked(capture_dir / captured["bgr_file"], captured["bgr_sha256"])
        if len(production_bgr) != len(candidate_bgr):
            raise ValueError("Decoded image size differs")
        if production_bgr != candidate_bgr:
            raise ValueError("Decoded BGR image bytes differ")
        if not result["byte_identical"]:
            raise ValueError("512 tensor bytes differ")
        result.update(source_image_sha256=source["image"]["sha256"], shape=list(tensor.shape),
                      production_960_shape=captured["production_960_shape"],
                      different_decoded_bgr_bytes=sum(a != b for a, b in zip(candidate_bgr, production_bgr, strict=True)))
        rows.append(result)
    report = {"schema": "graphreader.synthetic-tensor-parity-comparison.v1", "scope": "four fixed synthetic train scenes",
              "capture_sha256": sha256(capture_bytes).hexdigest(), "preflight_report_sha256": PREFLIGHT_SHA,
              "run_identity_sha256": identity_sha, "run_identity_files": identity["files"],
              "runtime_boundary": "project-local binaries and runtime manifests bound; shared framework, OS and hardware not bound",
              "observed_runtime": {key: capture[key] for key in ("shared_framework", "os_description", "process_architecture")},
              "experimental_setting": "512 maximum side, alignment 128", "production_setting": "960 maximum side, alignment 128",
              "production_960_tensors_compared": False, "full_production_workflow_replayed": False,
              "all_512_tensors_byte_identical": all(row["byte_identical"] for row in rows),
              "production_approved": False, "training_input_approved": False, "optimizer_steps": 0,
              "holdout_raster_or_label_reads": 0, "private_reads": 0, "sealed_reads": 0, "cases": rows}
    tensor_parity_identity.load(identity_path, identity_sha)
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(report, stream, indent=2, sort_keys=True, allow_nan=False)
        stream.write("\n")
    return report


if __name__ == "__main__":
    import sys
    if len(sys.argv) != 5:
        raise SystemExit("Expected capture directory, new comparison report path, run identity and its pinned SHA-256")
    result = run(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]), sys.argv[4])
    print(json.dumps({"status": "compared", "all_512_tensors_byte_identical": result["all_512_tensors_byte_identical"],
                      "training_input_approved": False}, sort_keys=True))
