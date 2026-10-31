# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Frozen-trunk, production-size synthetic preflight; no optimizer or approval."""
from collections import Counter, defaultdict
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import time

import cv2
import numpy as np
import onnx
import onnxruntime as ort
from PIL import Image
import psutil

from ml.ocr.gpu_db_head_probe_v1 import (conflict_pixel_counterfactual as conflict,
    feature_preflight as preflight, loss_mask_counterfactual as conservative, parity,
    target_geometry_audit as geometry, tensor_parity, tensor_parity_960,
    tensor_parity_identity as identity)
from ml.ocr.official_bakeoff import db_inverse_targets as targets, frozen_trunk_head as frozen

PARITY = parity.ROOT / "artifacts/synthetic-tensor-parity-960-comparison-v3-20261030.json"
PARITY_SHA = "97d5712832b6ca3e07c9d4ad086dc4525cec37873e6daec61ff59eb4ab59e982"
IDENTITY = parity.ROOT / "artifacts/synthetic-tensor-parity-identity-v3-20261030.json"
IDENTITY_SHA = "81c3ea76acec8ce59f749387e603e98de99bbaad8c0326b1f105ef42af0c6bd0"
CAPTURE_SHA = "068a5a086367fd0b868291a1d7ba942dcfc6ab1e08887558fbf004dbb73af264"
TRUNK_SHA = "6115e9e02bb20ea495b04e3f5329721115ba72b5e79cc7246773919c9699b081"


def validate_parity(report, bound_identity):
    if (report.get("schema") != "graphreader.synthetic-tensor-parity-960-comparison.v1" or
            report.get("capture_sha256") != CAPTURE_SHA or report.get("run_identity_sha256") != IDENTITY_SHA or
            report.get("run_identity_files") != bound_identity["files"] or report.get("all_tensors_byte_identical") is not True or
            report.get("full_production_workflow_replayed") is not False or report.get("training_input_approved") is not False or
            report.get("optimizer_steps") != 0 or report.get("holdout_raster_or_label_reads") != 0 or
            report.get("private_reads") != 0 or report.get("sealed_reads") != 0):
        raise ValueError("Bound production-size tensor parity scope differs")
    rows = report.get("cases")
    if not isinstance(rows, list) or len(rows) != 4:
        raise ValueError("Bound tensor parity population differs")
    seen = set()
    for row in rows:
        digest = row.get("source_image_sha256")
        if (not isinstance(digest, str) or len(digest) != 64 or digest in seen or row.get("byte_identical") is not True or
                row.get("different_bytes") != 0 or row.get("different_float32_bits") != 0 or
                row.get("candidate_tensor_sha256") != row.get("production_tensor_sha256")):
            raise ValueError("Bound tensor parity case differs")
        seen.add(digest)
    return rows


def prepare(image_bytes: bytes, annotation: dict):
    tensor = tensor_parity_960.candidate_tensor(image_bytes, 960)
    with Image.open(BytesIO(image_bytes)) as image:
        width, height = image.size
    if annotation.get("coordinate_space") != "original_pixels" or annotation.get("canvas", {}).get("width") != width or annotation.get("canvas", {}).get("height") != height:
        raise ValueError("Annotation canvas or coordinate space differs")
    th, tw = tensor.shape[2:]
    def mapped(box):
        x, y, w, h = preflight._box(box, width, height)
        return (x * tw / width, y * th / height, (x + w) * tw / width, (y + h) * th / height)
    boxes, identities = [], []
    seen = set()
    for panel in annotation["panels"]:
        for text in panel["texts"]:
            text_id, role = text.get("text_id"), text.get("role")
            if (text.get("visible") is not True or text.get("partial") is not False or
                    text.get("coordinate_space") != "original_pixels" or not isinstance(text_id, str) or
                    text_id in seen or not isinstance(role, str) or not role or not text.get("text")):
                raise ValueError("Unsupported text identity or visibility")
            seen.add(text_id)
            boxes.append(mapped(text["rendered_pixel_box"]))
            identities.append((text_id, role))
    if not boxes or not any(role == "x_tick" for _, role in identities):
        raise ValueError("No bound x-tick supervision")
    built = targets.build_inverse_targets(tw, th, boxes)
    mask = np.ones_like(built.target, dtype=np.float32)
    negatives = annotation["hard_negatives"]
    if not isinstance(negatives, list):
        raise ValueError("Marker artifact inventory missing")
    for item in negatives:
        if item.get("coordinate_space") != "original_pixels":
            raise ValueError("Marker artifact coordinates differ")
        l, t, r, b = mapped(item["rendered_pixel_box"])
        l, t = max(0, math.floor(l) - 2), max(0, math.floor(t) - 2)
        r, b = min(tw, math.ceil(r) + 2), min(th, math.ceil(b) + 2)
        mask[:, t:b, l:r] = 0
    mask[built.target == 1] = 1
    return tensor, built, mask, identities


def _sum_maps(results, key):
    return dict(sorted(sum((Counter(item[key]) for item in results), Counter()).items()))


def _sum_nested(results, key):
    roles = defaultdict(Counter)
    for item in results:
        for role, counts in item[key].items():
            roles[role].update(counts)
    return {role: dict(sorted(counts.items())) for role, counts in sorted(roles.items())}


def run(output: Path) -> dict:
    started = time.perf_counter()
    process = psutil.Process()
    initial_working_set = process.memory_info().rss
    output = output.resolve()
    if not output.is_relative_to((parity.ROOT / "artifacts").resolve()) or output.exists():
        raise ValueError("Output must be a new project artifacts directory")
    bound = identity.load(IDENTITY, IDENTITY_SHA)
    compared = preflight._json(parity._read_locked(PARITY, PARITY_SHA))
    parity_rows = validate_parity(compared, bound)
    previous = preflight._json(parity._read_locked(tensor_parity.PREFLIGHT, tensor_parity.PREFLIGHT_SHA))
    target_sha = sha256(Path(targets.__file__).read_bytes()).hexdigest()
    if previous.get("dependency_source_sha256", {}).get("inverse_targets") != target_sha:
        raise ValueError("Target-builder source differs from bound preflight")
    if previous.get("opencv_version") != cv2.__version__:
        raise ValueError("OpenCV version differs from bound preflight")
    sources = preflight._train_inputs()
    if len(sources) != 4 or len(previous.get("cases", [])) != 4:
        raise ValueError("Expected four authenticated training scenes")
    model = parity._read_locked(parity.MODEL, parity.MODEL_SHA)
    trunk = parity._trunk_bytes(onnx.load_model_from_string(model))
    if sha256(trunk).hexdigest() != TRUNK_SHA:
        raise ValueError("Frozen V46 trunk differs")
    options = ort.SessionOptions()
    options.intra_op_num_threads = options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    session = ort.InferenceSession(trunk, options, providers=["CPUExecutionProvider"])
    if session.get_providers() != ["CPUExecutionProvider"]:
        raise ValueError("Unexpected inference provider")
    output.mkdir(parents=True, exist_ok=False)
    results, geometry_rows, conservative_rows, conflict_rows = [], [], [], []
    for index, ((source, image, annotation), saved, compared_row) in enumerate(zip(sources, previous["cases"], parity_rows, strict=True)):
        image_sha, annotation_sha = source["image"]["sha256"], source["annotation"]["sha256"]
        if (image_sha != saved["source_image_sha256"] or annotation_sha != saved["source_annotation_sha256"] or
                image_sha != compared_row["source_image_sha256"]):
            raise ValueError("Training input identity differs from parity")
        tensor, built, mask, ids = prepare(image, annotation)
        if (list(tensor.shape) != compared_row["shape"] or sha256(tensor.tobytes()).hexdigest() != compared_row["candidate_tensor_sha256"] or
                tensor.nbytes != compared_row["tensor_byte_count"]):
            raise ValueError("Production-size tensor differs from bound parity")
        feature = np.asarray(session.run([frozen.FEATURE_OUTPUT_NAME], {frozen.MODEL_INPUT_NAME: tensor})[0], dtype="<f4")
        if feature.shape != (1, 96, tensor.shape[2] // 4, tensor.shape[3] // 4) or not np.isfinite(feature).all():
            raise ValueError("Frozen feature shape or values differ")
        geometry_rows.append(geometry.analyze(built, ids))
        _, conservative_row = conservative.evaluate(built, mask, ids)
        _, conflict_row = conflict.evaluate(built, mask, ids)
        conservative_rows.append(conservative_row)
        conflict_rows.append(conflict_row)
        arrays = {}
        for name, value in (("features", feature), ("target", built.target), ("mask", mask)):
            raw = np.ascontiguousarray(value, dtype="<f4").tobytes()
            filename = f"train-{index}.{name}.f32"
            with (output / filename).open("xb") as stream:
                stream.write(raw)
            arrays[name] = {"file": filename, "sha256": sha256(raw).hexdigest(), "shape": list(value.shape), "dtype": "float32-le"}
        results.append({"source_image_sha256": image_sha, "source_annotation_sha256": annotation_sha,
                        "tensor_sha256": compared_row["candidate_tensor_sha256"], "shape": list(tensor.shape), "arrays": arrays})
    geom_keys = ("contact_kinds", "contact_role_pairs", "clamped_roles")
    geometry_totals = {key: sum(row[key] for row in geometry_rows) for key in geometry_rows[0]
                       if key not in geom_keys and not key.startswith("maximum_")}
    geometry_totals.update({key: _sum_maps(geometry_rows, key) for key in geom_keys})
    geometry_totals["maximum_clamped_extent_area_to_continuous_inverse_area"] = max(
        row["maximum_clamped_extent_area_to_continuous_inverse_area"] for row in geometry_rows)
    conservative_totals = {key: sum(row[key] for row in conservative_rows) for key in conservative_rows[0]
                           if key not in ("roles_total", "roles_omitted")}
    for key in ("roles_total", "roles_omitted"):
        conservative_totals[key] = _sum_maps(conservative_rows, key)
    conservative_totals["roles_retained"] = {role: count - conservative_totals["roles_omitted"].get(role, 0)
                                             for role, count in conservative_totals["roles_total"].items()}
    conflict_keys = ("roles", "original_contacts_by_kind", "unresolved_original_direct_contacts_by_kind")
    conflict_totals = {key: sum(row[key] for row in conflict_rows) for key in conflict_rows[0] if key not in conflict_keys}
    conflict_totals.update({key: _sum_maps(conflict_rows, key) for key in conflict_keys[1:]})
    conflict_totals["roles"] = _sum_nested(conflict_rows, "roles")
    identity.load(IDENTITY, IDENTITY_SHA)
    memory = process.memory_info()
    report = {"schema": "graphreader.synthetic-tick-production-size-feature-preflight.v1",
              "status": "completed_diagnostic", "train_scene_count": 4, "cases": results,
              "dataset_manifest_sha256": parity.DATASET_MANIFEST_SHA, "seed_manifest_sha256": parity.SEED_MANIFEST_SHA,
              "model_sha256": parity.MODEL_SHA, "trunk_sha256": TRUNK_SHA,
              "tensor_parity_report_sha256": PARITY_SHA, "tensor_parity_identity_sha256": IDENTITY_SHA,
              "tensor_capture_sha256": CAPTURE_SHA, "preflight_512_report_sha256": tensor_parity.PREFLIGHT_SHA,
              "source_sha256": {"script": sha256(Path(__file__).read_bytes()).hexdigest(),
                  "target_builder": target_sha, "geometry_audit": sha256(Path(geometry.__file__).read_bytes()).hexdigest(),
                  "conservative_mask": sha256(Path(conservative.__file__).read_bytes()).hexdigest(),
                  "conflict_mask": sha256(Path(conflict.__file__).read_bytes()).hexdigest()},
              "source_binding": "script and target/mask helper hashes are descriptive; parity identity was pinned before its capture",
              "target_geometry": geometry_totals, "candidate_conservative_mask": conservative_totals,
              "candidate_conflict_pixel_mask": conflict_totals,
              "candidate_mask_margins": "2 pixels in the 960/128 tensor, diagnostic only",
              "provider": "CPUExecutionProvider", "onnxruntime_version": ort.__version__, "opencv_version": cv2.__version__,
              "cpu_threads": 1, "duration_seconds": round(time.perf_counter() - started, 4),
              "initial_working_set_bytes": initial_working_set, "peak_process_working_set_bytes": getattr(memory, "peak_wset", None),
              "memory_scope": "process lifetime peak working set, including imports; not GPU memory",
              "full_production_workflow_replayed": False, "private_reads": 0, "sealed_reads": 0,
              "holdout_raster_or_label_reads": 0, "optimizer_steps": 0, "training_input_approved": False,
              "production_approved": False}
    with (output / "report.json").open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(report, stream, sort_keys=True, indent=2, allow_nan=False)
        stream.write("\n")
    return report


if __name__ == "__main__":
    import sys
    if len(sys.argv) != 2:
        raise SystemExit("Expected new project artifacts output directory")
    result = run(Path(sys.argv[1]))
    print(json.dumps({"status": result["status"], "train_scene_count": result["train_scene_count"],
                      "duration_seconds": result["duration_seconds"], "training_input_approved": False}, sort_keys=True))
