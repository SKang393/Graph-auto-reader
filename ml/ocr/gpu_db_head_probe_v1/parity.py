# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authenticate V46 and compare its synthetic ONNX output to GPU DB head.

The sole input is a fixed project-generated train-split synthetic raster crop.
No optimizer, private/sealed corpus or model output is read or written. This
measures one shape only and does not authorize training.
The reported script hash describes observed bytes; it is not preregistered.
"""
from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper
import onnxruntime as ort
from PIL import Image
import torch

from ml.markers.center.real_positive_context_v1.positive_archive import _open_read_only_no_writers
from ml.ocr.official_bakeoff import frozen_trunk_head as frozen
from ml.synthetic.dataset import _split_manifests
from .probe import require_gpu

ROOT = Path(__file__).resolve().parents[3]
MODEL = ROOT / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/ocr-scale-coverage-v46/P1/detector-scale-coverage-v46-p1.onnx"
MODEL_SHA = "497a3527820ce0359496084e5afb1e0d3241aca79e192187f7ab737b27818b7f"
PROTOCOL = ROOT / "ml/ocr/official_bakeoff/SERVER_COMPARISON_PROTOCOL.json"
PROTOCOL_SHA = "cff28b41ee07ccb549afdae472dc38097020794597f3ec16f732c5d3a547a34c"
SHAPE = (1, 3, 32, 32)
IMAGE = ROOT / "artifacts/synthetic-tick-family-v1-20261030/dataset/images/fa85e1e8-4ea8-5c81-9434-75e0677bf29d.png"
IMAGE_SHA = "4d5b9837c5a8d3581c21261b89bbf2bf209911e0a543299f729787538f26e566"
DATASET = ROOT / "artifacts/synthetic-tick-family-v1-20261030"
DATASET_MANIFEST_SHA = "ba4d08a88d50946fcb8a7380d96210ed9671dde1b57644770c92d87581ec6729"
SEED_MANIFEST_SHA = "e9ccb2b32c763b9139d7e43bded2109df6d9cedea11df8c5c15426e972956578"


def _read_locked(path: Path, expected_sha: str) -> bytes:
    with _open_read_only_no_writers(path) as stream:
        data = stream.read()
    if sha256(data).hexdigest() != expected_sha:
        raise ValueError("Synthetic dataset metadata hash differs")
    return data


def _validate_membership(summary: dict, seed: dict, splits: dict, image_path: str, image_sha: str) -> None:
    if (summary.get("schema") != "graphreader.synthetic-tick-family-dataset.v1" or
            summary.get("synthetic_only") is not True or summary.get("seed_manifest_sha256") != SEED_MANIFEST_SHA or
            seed.get("preset") != "real_range" or seed.get("dataset_seed") != 20261030 or
            len(seed.get("cases", [])) != 10):
        raise ValueError("Synthetic dataset manifest identity differs")
    expected_splits = _split_manifests(seed["cases"])
    if splits != expected_splits:
        raise ValueError("Synthetic split manifest differs from bound cases")
    for name, count in (("train", 4), ("validation", 6)):
        expected_cases = [case for case in seed["cases"] if case["split"] == name]
        rows = summary.get("splits", {}).get(name, {}).get("cases")
        if not isinstance(rows, list) or len(rows) != count or len(expected_cases) != count:
            raise ValueError("Synthetic split population differs")
        for row, expected in zip(rows, expected_cases, strict=True):
            expected_image = f"images/{expected['scene_id']}.png"
            if (row.get("scene_id") != expected["scene_id"] or row.get("families") != expected["families"] or
                    row.get("image") != {"path": expected_image, "sha256": expected["files"][expected_image]}):
                raise ValueError("Synthetic summary differs from bound cases")
    train_rows = summary["splits"]["train"]["cases"]
    other_rows = summary["splits"]["validation"]["cases"]
    if (sum(row["image"] == {"path": image_path, "sha256": image_sha} for row in train_rows) != 1 or
            any(row["image"]["path"] == image_path or row["image"]["sha256"] == image_sha for row in other_rows) or
            any(image_path in row["files"] for row in splits["test"]["cases"])):
        raise ValueError("Synthetic image is not exclusive to the train split")


def _dataset_binding() -> None:
    summary = json.loads(_read_locked(DATASET / "manifest.json", DATASET_MANIFEST_SHA))
    seed = json.loads(_read_locked(DATASET / "dataset/seed-manifest.json", SEED_MANIFEST_SHA))
    splits = {name: json.loads(_read_locked(DATASET / "dataset/splits" / f"{name}.json",
                                          seed["artifact_sha256"][f"splits/{name}.json"]))
              for name in ("train", "validation", "test")}
    _validate_membership(summary, seed, splits, IMAGE.relative_to(DATASET / "dataset").as_posix(), IMAGE_SHA)


def _synthetic_input() -> np.ndarray:
    if not IMAGE.resolve(strict=True).is_relative_to(ROOT.resolve(strict=True)):
        raise ValueError("Synthetic image must be project-local")
    _dataset_binding()
    with _open_read_only_no_writers(IMAGE) as stream:
        raw = stream.read()
    if sha256(raw).hexdigest() != IMAGE_SHA:
        raise ValueError("Synthetic training-split image changed")
    with Image.open(BytesIO(raw)) as image:
        if image.width < 32 or image.height < 32:
            raise ValueError("Synthetic image is too small")
        left, top = (image.width - 32) // 2, (image.height - 32) // 2
        rgb = np.asarray(image.convert("RGB").crop((left, top, left + 32, top + 32)), dtype=np.uint8)
    bgr = rgb[:, :, ::-1].astype(np.float32) / np.float32(255.0)
    means = np.asarray([0.485, 0.456, 0.406], dtype=np.float32)
    scales = np.float32(1) / np.asarray([0.229, 0.224, 0.225], dtype=np.float32)
    return np.ascontiguousarray(((bgr - means) * scales).transpose(2, 0, 1)[None])


def _trunk_bytes(model: onnx.ModelProto) -> bytes:
    trunk = onnx.ModelProto()
    trunk.CopyFrom(model)
    indexes = frozen._ancestor_node_indexes(trunk, frozen.FEATURE_OUTPUT_NAME)
    nodes = [node for index, node in enumerate(trunk.graph.node) if index in indexes]
    del trunk.graph.node[:]
    trunk.graph.node.extend(nodes)
    del trunk.graph.output[:]
    trunk.graph.output.extend([helper.make_tensor_value_info(
        frozen.FEATURE_OUTPUT_NAME, TensorProto.FLOAT, ["N", frozen.FEATURE_CHANNELS, "H4", "W4"])])
    onnx.checker.check_model(trunk, full_check=True)
    return trunk.SerializeToString()


def run_parity() -> dict:
    require_gpu()
    if not MODEL.resolve(strict=True).is_relative_to(ROOT.resolve(strict=True)):
        raise ValueError("Detector must be project-local")
    protocol = PROTOCOL.read_bytes()
    if sha256(protocol).hexdigest() != PROTOCOL_SHA:
        raise ValueError("Numerical comparison protocol changed")
    tolerance = json.loads(protocol)["conversion"]["maximum_absolute_difference"]
    if type(tolerance) is not float or tolerance != 0.0001:
        raise ValueError("Numerical conversion tolerance changed")
    with _open_read_only_no_writers(MODEL) as model_lock, _open_read_only_no_writers(Path(__file__).resolve()) as probe_lock:
        probe_sha = sha256(probe_lock.read()).hexdigest()
        original = model_lock.read()
        if sha256(original).hexdigest() != MODEL_SHA:
            raise ValueError("V46 detector hash differs")
        # The model stays locked while these independently validating helpers read it.
        bundle = frozen.extract_frozen_trunk_head(MODEL, MODEL_SHA)
        _, source = frozen._load_reviewed_model(MODEL, MODEL_SHA)
        trunk = _trunk_bytes(source)
        options = ort.SessionOptions()
        options.intra_op_num_threads = options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
        options.log_severity_level = 3
        full = ort.InferenceSession(original, options, providers=["CPUExecutionProvider"])
        feature = ort.InferenceSession(trunk, options, providers=["CPUExecutionProvider"])
        if full.get_providers() != ["CPUExecutionProvider"] or feature.get_providers() != ["CPUExecutionProvider"]:
            raise RuntimeError("ONNX reference sessions must be CPU-only")
        values = _synthetic_input()
        expected = np.asarray(full.run([frozen.MODEL_OUTPUT_NAME], {frozen.MODEL_INPUT_NAME: values})[0], dtype=np.float32)
        features = np.asarray(feature.run([frozen.FEATURE_OUTPUT_NAME], {frozen.MODEL_INPUT_NAME: values})[0], dtype=np.float32)
        if features.shape != (1, 96, 8, 8) or not np.isfinite(features).all() or not np.isfinite(expected).all():
            raise RuntimeError("Synthetic ONNX output is invalid")
        head = bundle.head.to("cuda:0").eval()
        batch = torch.from_numpy(np.ascontiguousarray(features)).to("cuda:0")
        require_gpu(batch, *head.parameters(), *head.buffers())
        torch.cuda.synchronize(torch.device("cuda:0"))
        baseline = torch.cuda.memory_allocated(torch.device("cuda:0"))
        torch.cuda.reset_peak_memory_stats(torch.device("cuda:0"))
        with torch.inference_mode():
            output = head(batch)
        require_gpu(output)
        torch.cuda.synchronize(torch.device("cuda:0"))
        peak = max(0, torch.cuda.max_memory_allocated(torch.device("cuda:0")) - baseline)
        actual = output.cpu().numpy()
        if actual.shape != expected.shape or not np.isfinite(actual).all():
            raise RuntimeError("GPU reconstructed output is invalid")
        error = float(np.max(np.abs(expected - actual), initial=0.0))
        flips = int(np.count_nonzero((expected >= 0.3) != (actual >= 0.3)))
        return {"status": "pass" if error <= tolerance else "fail", "model_sha256": MODEL_SHA,
                "comparison_protocol_sha256": PROTOCOL_SHA, "probe_script_sha256": probe_sha,
                "probe_script_binding": "descriptive_sha256_not_preregistered",
                "dataset_manifest_sha256": DATASET_MANIFEST_SHA, "seed_manifest_sha256": SEED_MANIFEST_SHA,
                "trunk_sha256": sha256(trunk).hexdigest(), "input_sha256": sha256(values.tobytes()).hexdigest(),
                "input_shape": list(SHAPE), "synthetic_source_image_sha256": IMAGE_SHA,
                "synthetic_source_split": "train", "feature_shape": list(features.shape),
                "maximum_absolute_difference": error, "tolerance": tolerance, "decision_0_3_flips": flips,
                "onnx_reference_provider": "CPUExecutionProvider", "head_device": "RX 6600 XT gfx1032 cuda:0",
                "torch_version": str(torch.__version__), "hip_version": str(torch.version.hip),
                "onnxruntime_version": ort.__version__, "peak_additional_allocated_bytes": peak,
                "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0, "production_approved": False}


def main() -> int:
    result = run_parity()
    print(json.dumps(result, sort_keys=True))
    return 0 if result["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
