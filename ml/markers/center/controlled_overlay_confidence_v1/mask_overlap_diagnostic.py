# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare synthetic text/artifact-mask overlap for frozen TP and FP centers."""
from __future__ import annotations

from collections import Counter
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch

from .. import shape_coverage_evaluation as evaluation
from ..component_diversity_v27 import diagnose_head_swap as historical
from ..component_diversity_v27 import diagnose_score_localization as source
from ..dual_scale_context_v1.prepare import SOURCE, verified_bytes
from ..real_positive_context_v1.export_evaluate import DEV, DEV_SHA
from ..shape_coverage_cache import unpack_scene
from . import fp_diagnostic as previous

OUTPUT = previous.ROOT / "artifacts/goal22-runs/controlled-overlay-mask-overlap-v1.json"


def mask_overlap(planes: np.ndarray, x: float, y: float) -> tuple[bool, bool]:
    if planes.ndim != 3 or planes.shape[0] != 2 or not np.isfinite(planes).all():
        raise ValueError("Expected two finite synthetic mask planes")
    px, py = round(x), round(y)
    if not 0 <= px < planes.shape[2] or not 0 <= py < planes.shape[1]:
        raise ValueError("Prediction is outside its synthetic image")
    return bool(planes[0, py, px] >= .5), bool(planes[1, py, px] >= .5)


def main() -> None:
    if OUTPUT.exists():
        raise FileExistsError("Diagnostic output already exists")
    report = json.loads(previous.checked(previous.DEV_OUTPUT / "report.json", previous.REPORT_SHA))
    previous.checked(previous.DEV_OUTPUT / "marker-center.onnx", previous.ONNX_SHA)
    options = ort.SessionOptions()
    options.intra_op_num_threads = options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    session = ort.InferenceSession(str(previous.DEV_OUTPUT / "marker-center.onnx"),
                                   sess_options=options, providers=["CPUExecutionProvider"])
    packed = torch.load(BytesIO(verified_bytes(DEV, DEV_SHA)), map_location="cpu", weights_only=True)
    scenes = tuple(unpack_scene(row, expected_split="validation") for row in packed["family"])
    paths, old_report, prior_report, hashes = source._authenticate(SOURCE, historical.SOURCE_DIAGNOSTIC_SHA256)
    cache, _ = source._load_caches(paths, old_report, prior_report)
    baseline_report, _ = historical._load_v27_cache(SOURCE)
    totals, rows = Counter(), 0
    for index, bound in enumerate(scenes):
        scene = bound.scene
        old = old_report["family_dev"]["cache_manifest"][index]
        reference = baseline_report["family_dev"]["cache_manifest"][index]
        coords = cache[old["proposal_coordinates_key"]]
        if (old["scene_identity"] != reference["scene_identity"] or
                old["scene_identity"] != source._scene_identity(scene) or
                source.cache_helper._array_sha(coords) != reference["proposal_coordinates_sha256"]):
            raise ValueError("Scene or proposal identity differs")
        padded = np.pad(scene.tensor.numpy(), ((0, 0), (16, 16), (16, 16)))
        metadata = evaluation.canonical_json_bytes({"dtype": "float32", "shape": [len(coords), 3, 33, 33]})
        patch_hash = sha256(len(metadata).to_bytes(8, "little") + metadata)
        values = []
        for start in range(0, len(coords), 128):
            batch = coords[start:start + 128]
            if not np.equal(batch, np.floor(batch)).all():
                raise ValueError("Non-integral proposal")
            patches = np.stack([padded[:, int(y):int(y) + 33, int(x):int(x) + 33] for x, y in batch])
            patch_hash.update(patches.tobytes())
            values.append(session.run(["candidate_predictions"], {"candidate_patches": patches})[0])
        actual = np.concatenate(values)
        if (patch_hash.hexdigest() != reference["proposal_patches_sha256"] or
                not np.array_equal(actual, np.load(previous.DEV_OUTPUT / f"family-{index:03d}-predictions.npy", allow_pickle=False))):
            raise ValueError("Frozen patch or prediction bytes differ")
        rows += len(actual)
        predictions = evaluation.predictions(scene, coords, actual, bound.panel_domain.domain,
                                             balanced=True, threshold=.1)
        matched = {prediction for prediction, _ in evaluation.match_predictions(predictions, scene.centers)}
        expected = next(row for row in report["development"]["records"]
                        if row["scope"] == "family" and row["scene_index"] == index)["results"]["candidate_balanced"]
        if len(matched) != expected["true_positives"] or len(predictions) - len(matched) != expected["false_positives"]:
            raise ValueError("Recorded scene counts differ")
        planes = scene.tensor[1:3].numpy()
        for number, prediction in enumerate(predictions):
            group = "matched" if number in matched else "unmatched"
            text, artifact = mask_overlap(planes, prediction.x, prediction.y)
            totals[group] += 1
            if text:
                totals[group + "_text"] += 1
            if artifact:
                totals[group + "_artifact"] += 1
            if text and artifact:
                totals[group + "_both"] += 1
    if len(scenes) != 9 or rows != 45396 or totals["matched"] != 203 or totals["unmatched"] != 58:
        raise ValueError("Frozen family denominator differs")
    source._verify_inputs_unchanged(SOURCE, paths, hashes)
    verified_bytes(DEV, DEV_SHA)
    previous.checked(previous.DEV_OUTPUT / "marker-center.onnx", previous.ONNX_SHA)
    result = {"schema": "graphreader.synthetic-marker-mask-overlap.v1", "counts": dict(totals),
              "development_sha256": DEV_SHA, "dev_report_sha256": previous.REPORT_SHA,
              "onnx_sha256": previous.ONNX_SHA, "source_sha256": sha256(Path(__file__).read_bytes()).hexdigest(),
              "verified_proposals": rows, "scenes": len(scenes), "optimizer_steps": 0,
              "real_payload_reads": 0, "sealed_payload_reads": 0, "production_approval": False}
    raw = (json.dumps(result, indent=2, sort_keys=True) + "\n").encode()
    with OUTPUT.open("xb") as stream:
        stream.write(raw)
    print(json.dumps({"counts": result["counts"], "report_sha256": sha256(raw).hexdigest()}))


if __name__ == "__main__":
    main()
