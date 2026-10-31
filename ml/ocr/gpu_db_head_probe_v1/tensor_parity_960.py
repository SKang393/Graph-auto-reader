# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare independently prepared production-size tensors on four train scenes.

This compares only original-image decode and detector tensor preprocessing;
it does not replay OCR masking, crops, model inference, or training.
"""
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

from ml.ocr.gpu_db_head_probe_v1 import feature_preflight as preflight
from ml.ocr.gpu_db_head_probe_v1 import parity, tensor_parity, tensor_parity_identity


def candidate_tensor(png: bytes, maximum_side: int) -> np.ndarray:
    if maximum_side not in (512, 960):
        raise ValueError("Unsupported fixed tensor recipe")
    with Image.open(BytesIO(png)) as image:
        width, height = image.size
        if width < 32 or height < 32 or width * height > 4_000_000:
            raise ValueError("Synthetic raster dimensions invalid")
        rgba = image.convert("RGBA")
        rgb = np.asarray(Image.alpha_composite(Image.new("RGBA", image.size, (255, 255, 255, 255)), rgba).convert("RGB"), dtype=np.uint8)
    ratio = maximum_side / max(width, height)
    tensor_width = math.ceil(int(width * ratio) / 128) * 128
    tensor_height = math.ceil(int(height * ratio) / 128) * 128
    if min(tensor_width, tensor_height) < 128 or max(tensor_width, tensor_height) > 1024:
        raise ValueError("Synthetic tensor dimensions invalid")
    bgr = np.ascontiguousarray(rgb[:, :, ::-1])
    resized = cv2.resize(bgr, (tensor_width, tensor_height), interpolation=cv2.INTER_LINEAR)
    sample = resized.astype(np.float32) / np.float32(255)
    means = np.asarray([0.485, 0.456, 0.406], dtype=np.float32)
    scales = np.float32(1) / np.asarray([0.229, 0.224, 0.225], dtype=np.float32)
    return np.ascontiguousarray(((sample - means) * scales).transpose(2, 0, 1)[None])


def run(capture_dir: Path, output: Path, identity_path: Path, identity_sha: str) -> dict:
    root = (parity.ROOT / "artifacts").resolve()
    capture_dir, output = capture_dir.resolve(strict=True), output.resolve()
    if not capture_dir.is_relative_to(root) or not output.is_relative_to(root) or output.exists():
        raise ValueError("Capture and new report must be under project artifacts")
    identity = tensor_parity_identity.load(identity_path, identity_sha)
    capture_bytes = (capture_dir / "capture.json").read_bytes()
    capture = preflight._json(capture_bytes)
    tensor_parity_identity.check_capture(identity, capture, identity_sha)
    previous = preflight._json(parity._read_locked(tensor_parity.PREFLIGHT, tensor_parity.PREFLIGHT_SHA))
    if (capture.get("schema") != "graphreader.synthetic-tensor-parity-capture.v1" or
            capture.get("maximum_side_length") != 512 or capture.get("dimension_multiple") != 128 or
            capture.get("optimizer_steps") != 0 or capture.get("training_input_approved") is not False or
            capture.get("model_inference") is not False or capture.get("opencv_version") != "4.13.0" or
            len(capture.get("images", [])) != 4 or len(previous.get("cases", [])) != 4):
        raise ValueError("C# capture scope or library version differs")
    rows = []
    for index, ((source, png, _annotation), captured, saved) in enumerate(
            zip(preflight._train_inputs(), capture["images"], previous["cases"], strict=True)):
        if (source["image"]["sha256"] != captured.get("source_image_sha256") or
                source["image"]["sha256"] != saved.get("source_image_sha256") or
                captured.get("production_960_file") != f"train-{index}.production-960.f32"):
            raise ValueError("Capture source or file identity differs")
        earlier = candidate_tensor(png, 512)
        if sha256(earlier.tobytes()).hexdigest() != saved["tensor_sha256"] or list(earlier.shape) != saved["tensor_shape"]:
            raise ValueError("Independent candidate differs from saved 512 preprocessing")
        tensor = candidate_tensor(png, 960)
        if captured.get("production_960_shape") != list(tensor.shape):
            raise ValueError("Production 960 tensor dimensions differ")
        raw = parity._read_locked(capture_dir / captured["production_960_file"], captured["production_960_sha256"])
        result = tensor_parity.compare_tensors(tensor, raw)
        if not result["byte_identical"]:
            raise ValueError("960 tensor bytes differ")
        result.update(source_image_sha256=source["image"]["sha256"], shape=list(tensor.shape),
                      tensor_byte_count=len(raw), candidate_tensor_sha256=sha256(tensor.tobytes()).hexdigest(),
                      production_tensor_sha256=captured["production_960_sha256"])
        rows.append(result)
    report = {"schema": "graphreader.synthetic-tensor-parity-960-comparison.v1",
              "scope": "four fixed synthetic train scenes", "capture_sha256": sha256(capture_bytes).hexdigest(),
              "preflight_512_report_sha256": tensor_parity.PREFLIGHT_SHA,
              "run_identity_sha256": identity_sha, "run_identity_files": identity["files"],
              "runtime_boundary": "project-local binaries and runtime manifests bound; shared framework, OS and hardware not bound",
              "observed_runtime": {key: capture[key] for key in ("shared_framework", "os_description", "process_architecture")},
              "setting": "960 maximum side, alignment 128; ceiling alignment can produce 1024 pixels",
              "all_tensors_byte_identical": all(row["byte_identical"] for row in rows),
              "total_tensor_bytes": sum(row["tensor_byte_count"] for row in rows),
              "full_production_workflow_replayed": False, "training_input_approved": False,
              "production_approved": False, "optimizer_steps": 0, "holdout_raster_or_label_reads": 0,
              "private_reads": 0, "sealed_reads": 0, "cases": rows}
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
    print(json.dumps({"status": "compared", "all_tensors_byte_identical": result["all_tensors_byte_identical"],
                      "training_input_approved": False}, sort_keys=True))
