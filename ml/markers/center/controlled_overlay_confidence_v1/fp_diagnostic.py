# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Attribute frozen synthetic family-dev false positives at the fixed threshold."""
from __future__ import annotations

from collections import Counter
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image, ImageDraw
import torch

from .. import shape_coverage_evaluation as evaluation
from ..component_diversity_v27 import diagnose_head_swap as historical
from ..component_diversity_v27 import diagnose_score_localization as source
from ..dual_scale_context_v1.prepare import SOURCE, verified_bytes
from ..real_positive_context_v1.export_evaluate import DEV, DEV_SHA
from ..shape_coverage_cache import unpack_scene
from .baseline import ROOT, checked

REPORT_SHA = "056aaca3479a8c77b7d5eda37ee55b6d77013282d2079082cbe887ec5956cd19"
ONNX_SHA = "35b50adbd6961293b888c5de3f2e5594b9cda7a9cb95d6c68b425999271a1535"
DEV_OUTPUT = ROOT / "artifacts/goal22-runs/controlled-overlay-confidence-v1-synthetic-dev"
OUTPUT = ROOT / "artifacts/goal22-runs/controlled-overlay-confidence-v1-fp-diagnostic"


def distance_bin(distance: float) -> str:
    if not math.isfinite(distance) or distance < 0:
        raise ValueError("Distance must be finite and nonnegative")
    for upper in (5, 8, 16, 32):
        if distance <= upper:
            return f"le_{upper}"
    return "over_32"


def false_positive_indices(predictions, centers) -> list[int]:
    paired = {index for index, _ in evaluation.match_predictions(predictions, centers)}
    return [index for index in range(len(predictions)) if index not in paired]


def main() -> None:
    if OUTPUT.exists():
        raise FileExistsError("Diagnostic output already exists")
    report = json.loads(checked(DEV_OUTPUT / "report.json", REPORT_SHA))
    if (report["status"] != "failed_dev_unconsumed" or report["onnx_sha256"] != ONNX_SHA or
            report["operating_threshold"] != 0.1 or report["sealed_reads"] != 0 or
            report["production_approval"] is not False):
        raise ValueError("Frozen development result differs")
    onnx_path = DEV_OUTPUT / "marker-center.onnx"
    checked(onnx_path, ONNX_SHA)
    options = ort.SessionOptions()
    options.intra_op_num_threads = options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    session = ort.InferenceSession(str(onnx_path), sess_options=options, providers=["CPUExecutionProvider"])
    packed = torch.load(BytesIO(verified_bytes(DEV, DEV_SHA)), map_location="cpu", weights_only=True)
    scenes = tuple(unpack_scene(row, expected_split="validation") for row in packed["family"])
    if len(scenes) != 9 or sum(len(bound.scene.centers) for bound in scenes) != 206:
        raise ValueError("Frozen family denominator differs")
    paths, old_report, prior_report, hashes = source._authenticate(SOURCE, historical.SOURCE_DIAGNOSTIC_SHA256)
    coordinates_cache, _ = source._load_caches(paths, old_report, prior_report)
    baseline_report, _ = historical._load_v27_cache(SOURCE)
    counts, crops, verified_rows = Counter(), [], 0
    for index, bound in enumerate(scenes):
        scene, domain = bound.scene, bound.panel_domain.domain
        old = old_report["family_dev"]["cache_manifest"][index]
        reference = baseline_report["family_dev"]["cache_manifest"][index]
        coords = coordinates_cache[old["proposal_coordinates_key"]]
        if (old["scene_identity"] != reference["scene_identity"] or
                old["scene_identity"] != source._scene_identity(scene) or
                source.cache_helper._array_sha(coords) != reference["proposal_coordinates_sha256"]):
            raise ValueError("Scene or proposal identity differs")
        padded = np.pad(scene.tensor.numpy(), ((0, 0), (16, 16), (16, 16)))
        metadata = evaluation.canonical_json_bytes({"dtype": "float32", "shape": [len(coords), 3, 33, 33]})
        patch_hash = sha256()
        patch_hash.update(len(metadata).to_bytes(8, "little"))
        patch_hash.update(metadata)
        values = []
        for start in range(0, len(coords), 128):
            batch = coords[start:start + 128]
            if not np.equal(batch, np.floor(batch)).all():
                raise ValueError("Proposal coordinate is not integral")
            patches = np.stack([padded[:, int(y):int(y) + 33, int(x):int(x) + 33] for x, y in batch])
            patch_hash.update(patches.tobytes())
            values.append(session.run(["candidate_predictions"], {"candidate_patches": patches})[0])
        if patch_hash.hexdigest() != reference["proposal_patches_sha256"]:
            raise ValueError("Reconstructed proposal bytes differ")
        actual = np.concatenate(values)
        saved = np.load(DEV_OUTPUT / f"family-{index:03d}-predictions.npy", allow_pickle=False)
        if not np.array_equal(actual, saved):
            raise ValueError("Recomputed ONNX predictions differ from the frozen run")
        verified_rows += len(actual)
        predictions = evaluation.predictions(scene, coords, actual, domain, balanced=True, threshold=0.1)
        missing = false_positive_indices(predictions, scene.centers)
        expected = next(row for row in report["development"]["records"]
                        if row["scope"] == "family" and row["scene_index"] == index)["results"]["candidate_balanced"]
        if len(missing) != expected["false_positives"] or len(predictions) - len(missing) != expected["true_positives"]:
            raise ValueError("Recomputed scene counts differ")
        for number in missing:
            prediction = predictions[number]
            nearest_marker = min(math.hypot(prediction.x - x, prediction.y - y) for x, y in scene.centers)
            counts["nearest_marker_" + distance_bin(nearest_marker)] += 1
            nearby = [(math.hypot(prediction.x - x, prediction.y - y), kind)
                      for kind, x, y in scene.hard_negatives]
            if nearby:
                nearest, kind = min(nearby)
                counts["nearest_hard_negative_" + distance_bin(nearest)] += 1
                if nearest <= 5:
                    counts["hard_negative_kind_" + kind] += 1
            ink = scene.tensor[0].numpy()
            x, y = round(prediction.x), round(prediction.y)
            crop = np.pad(ink, 24)[y:y + 48, x:x + 48]
            if crop.shape != (48, 48):
                raise ValueError("Synthetic crop is outside the expected bounds")
            crops.append(Image.fromarray(np.uint8(np.clip(1 - crop, 0, 1) * 255), "L"))
    if len(crops) != 58 or verified_rows != 45396:
        raise ValueError("Frozen false-positive or proposal count differs")
    source._verify_inputs_unchanged(SOURCE, paths, hashes)
    verified_bytes(DEV, DEV_SHA)
    checked(onnx_path, ONNX_SHA)
    OUTPUT.mkdir(parents=True, exist_ok=False)
    columns, tile = 8, 112
    sheet = Image.new("RGB", (columns * tile, math.ceil(len(crops) / columns) * (tile + 20)), "white")
    draw = ImageDraw.Draw(sheet)
    for i, crop in enumerate(crops):
        x, y = (i % columns) * tile, (i // columns) * (tile + 20)
        sheet.paste(crop.resize((96, 96), Image.Resampling.NEAREST).convert("RGB"), (x + 8, y + 20))
        draw.text((x + 8, y + 3), str(i + 1), fill="black")
    sheet.save(OUTPUT / "synthetic-false-positives.png")
    result = {"schema": "graphreader.synthetic-family-fp-diagnostic.v1", "report_sha256": REPORT_SHA,
              "development_sha256": DEV_SHA, "onnx_sha256": ONNX_SHA,
              "source_sha256": sha256(Path(__file__).read_bytes()).hexdigest(),
              "contact_sheet_sha256": sha256((OUTPUT / "synthetic-false-positives.png").read_bytes()).hexdigest(),
              "scenes": 9, "truths": 206, "verified_proposals": verified_rows, "false_positives": len(crops),
              "counts": dict(counts), "optimizer_steps": 0, "real_payload_reads": 0,
              "sealed_payload_reads": 0, "production_approval": False}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (OUTPUT / "report.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"counts": result["counts"], "sha256": sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
