# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Build-only GPU inference baseline for native-parity controlled patches."""

from __future__ import annotations

import hashlib
from io import BytesIO
import json
from pathlib import Path
import time

import numpy as np

ROOT = Path(__file__).resolve().parents[4]
RUN = ROOT / "artifacts/manual-marker-input-diagnostic-v1"
DATASET_SHA = "d1390de8797a75bc0c821996a327938b1dcd729de4f7b1f67d130e3c5fe29de6"
V37_SHA = "2daeedb92fdfb6c425cca6035040ca0e857c6d6bb1ba548281729fee71c24cd4"
BASE_SHA = "774ef9455e9ce7b489e640c5237b81275f24e42c2d2d0d3d87be4e953b3eb365"
THRESHOLD = 0.1


def summarize(labels: np.ndarray, base: np.ndarray, candidate: np.ndarray) -> dict:
    if (labels.ndim != 1 or base.shape != labels.shape or candidate.shape != labels.shape or
            not np.isin(labels, (0, 1)).all() or not np.isfinite(base).all() or
            not np.isfinite(candidate).all() or np.any((base < 0) | (base > 1)) or
            np.any((candidate < 0) | (candidate > 1))):
        raise ValueError("baseline arrays differ")
    positive = labels == 1
    base_selected, candidate_selected = base >= THRESHOLD, candidate >= THRESHOLD
    return {"patches": int(labels.size), "positive": int(positive.sum()), "distractor": int((~positive).sum()),
            "v27_true_positive": int((base_selected & positive).sum()),
            "v27_false_positive": int((base_selected & ~positive).sum()),
            "v37_true_positive": int((candidate_selected & positive).sum()),
            "v37_false_positive": int((candidate_selected & ~positive).sum()),
            "positive_gained": int((~base_selected & candidate_selected & positive).sum()),
            "positive_lost": int((base_selected & ~candidate_selected & positive).sum()),
            "distractor_gained": int((~base_selected & candidate_selected & ~positive).sum()),
            "distractor_lost": int((base_selected & ~candidate_selected & ~positive).sum())}


def checked(path: Path, expected: str) -> bytes:
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != expected:
        raise ValueError("baseline input hash differs")
    return raw


def main() -> None:
    import torch

    from ml.markers.center.confidence_spatial_v35.model import SpatialCenterNet
    from ml.markers.center.real_positive_context_v1.objective import _require_training_gpu
    from ml.markers.center.scale_classifier_v16.model import ModelConfig, ScaleClassifierNet

    output = RUN / "controlled-baseline-build-v1.json"
    if output.exists():
        raise FileExistsError("baseline output already exists")
    dataset_dir = RUN / "controlled-domain-dataset-v1"
    report = json.loads(checked(dataset_dir / "report.json", DATASET_SHA))
    if (report["schema"] != "graphreader.controlled-marker-domain-dataset.v1" or
            report["scope"] != "train-only-controlled-overlays" or report["optimizer_steps"] != 0 or
            report["training_approved"] is not False or report["sealed_payload_reads"] != 0 or
            report["native_unmarked_content_labeled_negative"] is not False):
        raise ValueError("baseline dataset scope differs")
    entry = next(part for part in report["partitions"] if part["partition"] == "internal_train_build")
    if (entry["patches"], entry["positive"], entry["distractor"], entry["groups"]) != (214, 108, 106, 11):
        raise ValueError("baseline build population differs")
    source = dataset_dir / "internal_train_build"
    arrays = {name: np.load(BytesIO(checked(source / f"{name}.npy", entry["files"][name])), allow_pickle=False)
              for name in ("patches", "labels", "groups")}
    patches, labels, groups = arrays["patches"], arrays["labels"], arrays["groups"]
    if (patches.dtype != np.float32 or patches.shape != (214, 3, 33, 33) or labels.shape != (214,) or
            not np.isin(labels, (0, 1)).all() or int(labels.sum()) != 108 or
            len(set(groups.tolist())) != 11 or not np.isfinite(patches).all() or patches.min() < 0 or patches.max() > 1):
        raise ValueError("baseline build tensors differ")
    base_path = ROOT / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/center-confidence-reflection-v37/P1/marker-center.pt"
    checkpoint = torch.load(BytesIO(checked(base_path, V37_SHA)), map_location="cpu", weights_only=True)
    if not isinstance(checkpoint, dict) or not isinstance(checkpoint.get("state_dict"), dict):
        raise ValueError("V37 checkpoint contract differs")
    model = SpatialCenterNet(ScaleClassifierNet(ModelConfig()))
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    origin_path = ROOT / "artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/marker-v27-component-diversity/P1-recovery1/marker-center-component-diversity-v27-p1.pt"
    origin = torch.load(BytesIO(checked(origin_path, BASE_SHA)), map_location="cpu", weights_only=True)
    if not isinstance(origin, dict) or not isinstance(origin.get("state_dict"), dict):
        raise ValueError("V27 checkpoint contract differs")
    retained = model.base.state_dict()
    if retained.keys() != origin["state_dict"].keys() or any(
            not torch.equal(value, origin["state_dict"][name]) for name, value in retained.items()):
        raise ValueError("V37 backbone differs from the verified V27 model")
    _require_training_gpu()
    model = model.eval().requires_grad_(False).to(torch.device("cuda:0"))
    _require_training_gpu(*model.parameters())
    torch.use_deterministic_algorithms(True)
    started = time.perf_counter()
    inputs = torch.from_numpy(patches).to(torch.device("cuda:0"))
    _require_training_gpu(inputs)
    with torch.inference_mode():
        v27 = model.base(inputs)[:, 0]
        v37 = model(inputs)[:, 0]
    _require_training_gpu(v27, v37)
    torch.cuda.synchronize()
    elapsed = time.perf_counter() - started
    summary = summarize(labels, v27.cpu().numpy(), v37.cpu().numpy())
    properties = torch.cuda.get_device_properties(0)
    result = {"schema": "graphreader.controlled-marker-baseline.v1", "scope": "train-build-only-diagnostic",
              "dataset_sha256": DATASET_SHA, "v37_checkpoint_sha256": V37_SHA, "v27_backbone_sha256": BASE_SHA,
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "device": properties.name, "architecture": properties.gcnArchName, "torch": str(torch.__version__),
              "hip": str(torch.version.hip), "cpu_fallback": False, "threshold": THRESHOLD,
              "elapsed_seconds": elapsed, "optimizer_steps": 0, "holdout_reads": 0,
              "development_reads": 0, "sealed_payload_reads": 0, "production_approval": False, "summary": summary}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with output.open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
