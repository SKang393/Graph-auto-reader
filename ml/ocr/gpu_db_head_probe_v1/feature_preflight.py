# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train-only synthetic feature/target diagnostic; never performs training.

The 512-pixel OpenCV 4.10 preprocessing below is a fixed diagnostic recipe,
not the production tensor. The script hash in its report is descriptive only.
The generated caches have not been admitted as training inputs.
"""
from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path

import cv2
import numpy as np
import onnx
import onnxruntime as ort
import PIL
from PIL import Image

from ml.ocr.gpu_db_head_probe_v1 import parity
from ml.ocr.official_bakeoff import db_inverse_targets as targets
from ml.ocr.official_bakeoff import frozen_trunk_head as frozen

MAX_SIDE = 512
ALIGN = 128
TRUNK_SHA = "6115e9e02bb20ea495b04e3f5329721115ba72b5e79cc7246773919c9699b081"


def _json(data: bytes) -> dict:
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON key")
            result[key] = value
        return result
    result = json.loads(data, object_pairs_hook=pairs)
    if not isinstance(result, dict):
        raise ValueError("Expected JSON object")
    return result


def _train_inputs() -> list[tuple[dict, bytes, dict]]:
    """Read only four bound train images/scenes/annotations and split metadata."""
    dataset = parity.DATASET / "dataset"
    summary = _json(parity._read_locked(parity.DATASET / "manifest.json", parity.DATASET_MANIFEST_SHA))
    seed = _json(parity._read_locked(dataset / "seed-manifest.json", parity.SEED_MANIFEST_SHA))
    splits = {name: _json(parity._read_locked(dataset / "splits" / f"{name}.json",
                 seed["artifact_sha256"][f"splits/{name}.json"]))
              for name in ("train", "validation", "test")}
    rows = summary["splits"]["train"]["cases"]
    if len(rows) != 4:
        raise ValueError("Expected four train scenes")
    records = []
    seen = set()
    for row in rows:
        scene_id = row["scene_id"]
        if not isinstance(scene_id, str) or not scene_id or any(c not in "0123456789abcdef-" for c in scene_id) or scene_id in seen:
            raise ValueError("Invalid or duplicate train identity")
        seen.add(scene_id)
        parity._validate_membership(summary, seed, splits, row["image"]["path"], row["image"]["sha256"])
        bound = next(case for case in seed["cases"] if case["scene_id"] == scene_id)
        inputs = {}
        for name, directory, extension in (("image", "images", "png"), ("annotation", "annotations", "json"), ("scene", "scenes", "json")):
            rel = f"{directory}/{scene_id}.{extension}"
            digest = bound["files"].get(rel)
            if row.get(name) != {"path": rel, "sha256": digest} or not isinstance(digest, str):
                raise ValueError("Train source binding differs")
            path = dataset / rel
            if not path.resolve(strict=True).is_relative_to(dataset.resolve(strict=True)):
                raise ValueError("Train source escapes dataset")
            inputs[name] = parity._read_locked(path, digest)
        annotation, scene = _json(inputs["annotation"]), _json(inputs["scene"])
        if (bound["split"] != "train" or annotation.get("scene_id") != scene_id or
                scene.get("scene_id") != scene_id or annotation.get("coordinate_space") != "original_pixels"):
            raise ValueError("Train annotation identity differs")
        records.append((row, inputs["image"], annotation))
    return records


def _box(value, width: int, height: int) -> tuple[float, float, float, float]:
    if not isinstance(value, list) or len(value) != 4 or any(type(v) not in (int, float) or not math.isfinite(v) for v in value):
        raise ValueError("Invalid rendered pixel box")
    x, y, w, h = map(float, value)
    if x < 0 or y < 0 or w <= 0 or h <= 0 or x + w > width or y + h > height:
        raise ValueError("Rendered box escapes source raster")
    return x, y, w, h


def _prepare(image_bytes: bytes, annotation: dict) -> tuple[np.ndarray, np.ndarray, np.ndarray, dict]:
    with Image.open(BytesIO(image_bytes)) as image:
        width, height = image.size
        if width < 32 or height < 32 or width * height > 4_000_000:
            raise ValueError("Source dimensions out of range")
        if annotation.get("canvas", {}).get("width") != width or annotation.get("canvas", {}).get("height") != height:
            raise ValueError("Annotation canvas differs from image")
        # Candidate preprocessing composites alpha over white before resize.
        rgba = image.convert("RGBA")
        white = Image.new("RGBA", image.size, (255, 255, 255, 255))
        rgb = np.asarray(Image.alpha_composite(white, rgba).convert("RGB"), dtype=np.uint8)
    ratio = MAX_SIDE / max(width, height)
    tensor_width = math.ceil(int(width * ratio) / ALIGN) * ALIGN
    tensor_height = math.ceil(int(height * ratio) / ALIGN) * ALIGN
    if min(tensor_width, tensor_height) < ALIGN or max(tensor_width, tensor_height) > MAX_SIDE:
        raise ValueError("Tensor dimensions out of range")
    bgr = np.ascontiguousarray(rgb[:, :, ::-1])
    resized = cv2.resize(bgr, (tensor_width, tensor_height), interpolation=cv2.INTER_LINEAR)
    sample = resized.astype(np.float32) / np.float32(255)
    means = np.asarray([0.485, 0.456, 0.406], dtype=np.float32)
    scales = np.float32(1) / np.asarray([0.229, 0.224, 0.225], dtype=np.float32)
    tensor = np.ascontiguousarray(((sample - means) * scales).transpose(2, 0, 1)[None])

    def map_box(box):
        x, y, w, h = _box(box, width, height)
        return (x * tensor_width / width, y * tensor_height / height,
                (x + w) * tensor_width / width, (y + h) * tensor_height / height)

    mapped = []
    x_ticks = 0
    ids = set()
    for panel in annotation["panels"]:
        for text in panel["texts"]:
            if text.get("visible") is not True or text.get("partial") is not False or text.get("coordinate_space") != "original_pixels":
                raise ValueError("Unsupported text visibility or coordinates")
            text_id = text.get("text_id")
            if not isinstance(text_id, str) or text_id in ids or not text.get("text"):
                raise ValueError("Text identity or label is invalid")
            ids.add(text_id)
            mapped.append(map_box(text["rendered_pixel_box"]))
            x_ticks += text.get("role") == "x_tick"
    if not mapped or not x_ticks:
        raise ValueError("No bound tick supervision")
    built = targets.build_inverse_targets(tensor_width, tensor_height, mapped)
    target = built.target.copy()
    mask = np.ones_like(target, dtype=np.float32)
    negatives = annotation["hard_negatives"]
    if not isinstance(negatives, list):
        raise ValueError("Marker artifact inventory missing")
    for item in negatives:
        if item.get("coordinate_space") != "original_pixels":
            raise ValueError("Marker artifact coordinates invalid")
        l, t, r, b = map_box(item["rendered_pixel_box"])
        l, t = max(0, math.floor(l) - 2), max(0, math.floor(t) - 2)
        r, b = min(tensor_width, math.ceil(r) + 2), min(tensor_height, math.ceil(b) + 2)
        mask[:, t:b, l:r] = 0
    mask[target == 1] = 1
    metrics = {"visible_texts": len(mapped), "visible_x_ticks": x_ticks,
               "marker_artifacts_ignored": len(negatives), "positive_pixels": int(target.sum()),
               "ignored_pixels": int((mask == 0).sum()), "target_contacts": len(built.contacts),
               "minimum_extent_clamps": sum(r.minimum_extent_clamped_x or r.minimum_extent_clamped_y for r in built.regions)}
    return tensor, target, mask, metrics


def run(output: Path) -> dict:
    output = output.resolve()
    if not output.is_relative_to((parity.ROOT / "artifacts").resolve()) or output.exists():
        raise ValueError("Output must be a new project artifacts directory")
    records = _train_inputs()
    model = parity._read_locked(parity.MODEL, parity.MODEL_SHA)
    trunk = parity._trunk_bytes(onnx.load_model_from_string(model))
    if sha256(trunk).hexdigest() != TRUNK_SHA:
        raise ValueError("Frozen V46 trunk differs")
    opts = ort.SessionOptions()
    opts.intra_op_num_threads = opts.inter_op_num_threads = 1
    opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    session = ort.InferenceSession(trunk, opts, providers=["CPUExecutionProvider"])
    if session.get_providers() != ["CPUExecutionProvider"]:
        raise RuntimeError("Unexpected inference provider")
    output.mkdir(parents=True, exist_ok=False)
    results = []
    for index, (row, image, annotation) in enumerate(records):
        tensor, target, mask, metrics = _prepare(image, annotation)
        feature = np.asarray(session.run([frozen.FEATURE_OUTPUT_NAME], {frozen.MODEL_INPUT_NAME: tensor})[0], dtype="<f4")
        expected = (1, 96, tensor.shape[2] // 4, tensor.shape[3] // 4)
        if feature.shape != expected or not np.isfinite(feature).all():
            raise ValueError("Invalid frozen-trunk feature")
        arrays = {}
        for name, array in (("features", feature), ("target", target), ("mask", mask)):
            raw = np.ascontiguousarray(array, dtype="<f4").tobytes()
            file = f"train-{index}.{name}.f32"
            with (output / file).open("xb") as stream:
                stream.write(raw)
            arrays[name] = {"file": file, "sha256": sha256(raw).hexdigest(), "shape": list(array.shape), "dtype": "float32-le"}
        results.append({"source_image_sha256": row["image"]["sha256"], "source_annotation_sha256": row["annotation"]["sha256"],
                        "source_scene_sha256": row["scene"]["sha256"], "tensor_shape": list(tensor.shape),
                        "tensor_sha256": sha256(tensor.tobytes()).hexdigest(), "arrays": arrays, "counts": metrics})
    report = {"schema": "graphreader.synthetic-tick-feature-preflight.v1", "status": "completed_diagnostic",
              "dataset_manifest_sha256": parity.DATASET_MANIFEST_SHA, "seed_manifest_sha256": parity.SEED_MANIFEST_SHA,
              "model_sha256": parity.MODEL_SHA, "trunk_sha256": TRUNK_SHA,
              "script_sha256": sha256(Path(__file__).read_bytes()).hexdigest(),
              "dependency_source_sha256": {"parity": sha256(Path(parity.__file__).read_bytes()).hexdigest(),
                                           "inverse_targets": sha256(Path(targets.__file__).read_bytes()).hexdigest(),
                                           "frozen_head": sha256(Path(frozen.__file__).read_bytes()).hexdigest()},
              "script_binding": "descriptive_not_preregistered", "provider": "CPUExecutionProvider",
              "onnxruntime_version": ort.__version__, "onnx_version": onnx.__version__, "opencv_version": cv2.__version__,
              "pillow_version": PIL.__version__, "numpy_version": np.__version__,
              "preprocessing": "candidate white-alpha-composite, BGR, OpenCV linear, maximum 512, ceiling alignment 128",
              "runtime_tensor_parity_verified": False, "training_input_approved": False,
              "target_policy": "inverse-unclip visible rendered text; ignore marker artifacts except positive target pixels",
              "holdout_raster_or_label_reads": 0, "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0,
              "train_scene_count": len(results), "cases": results}
    with (output / "report.json").open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(report, stream, sort_keys=True, indent=2, allow_nan=False)
        stream.write("\n")
    return report


if __name__ == "__main__":
    import sys
    if len(sys.argv) != 2:
        raise SystemExit("Expected one new project artifacts output directory")
    result = run(Path(sys.argv[1]))
    print(json.dumps({"status": result["status"], "train_scene_count": result["train_scene_count"],
                      "optimizer_steps": 0, "production_approved": False}, sort_keys=True))
