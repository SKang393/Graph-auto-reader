# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare one frozen candidate on the small controlled diagnostic holdout."""
from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path

import numpy as np
import torch

from ..real_positive_context_v1.export_evaluate import load_candidate
from ..real_positive_context_v1.objective import _require_training_gpu
from .audit_overlap import load_partition
from .baseline import DATASET_SHA, ROOT, RUN, THRESHOLD, checked
from .train import AUDIT_SHA, BASE

CHECKPOINT_SHA = "d5eb62bfa4a7cafd362ae1b0e27f64081fe0f6a4b47a51c6ce6fa8932b3926b0"
CHECKPOINT = ROOT / "artifacts/goal22-runs/controlled-overlay-confidence-v1/marker-center.pt"
OUTPUT = ROOT / "artifacts/goal22-runs/controlled-overlay-confidence-v1-holdout.json"


def compare(labels: np.ndarray, base: np.ndarray, candidate: np.ndarray) -> dict:
    if (labels.ndim != 1 or not labels.size or base.shape != labels.shape or candidate.shape != labels.shape or
            not np.isin(labels, (0, 1)).all() or not np.isfinite(base).all() or not np.isfinite(candidate).all() or
            np.any((base < 0) | (base > 1)) or np.any((candidate < 0) | (candidate > 1))):
        raise ValueError("Invalid diagnostic probabilities or labels")
    positive, a, b = labels == 1, base >= THRESHOLD, candidate >= THRESHOLD
    def counts(selected):
        return {"true_positive": int((selected & positive).sum()),
                "false_positive": int((selected & ~positive).sum()),
                "false_negative": int((~selected & positive).sum()),
                "true_negative": int((~selected & ~positive).sum())}
    return {"patches": len(labels), "positive": int(positive.sum()), "distractor": int((~positive).sum()),
            "v37": counts(a), "candidate": counts(b),
            "positive_gained": int((~a & b & positive).sum()), "positive_lost": int((a & ~b & positive).sum()),
            "distractor_gained": int((~a & b & ~positive).sum()), "distractor_lost": int((a & ~b & ~positive).sum())}


def main() -> None:
    if OUTPUT.exists():
        raise FileExistsError("Holdout result already exists")
    dataset_dir = RUN / "controlled-domain-dataset-v1"
    audit_dir = RUN / "controlled-overlap-audit-v1"
    dataset = json.loads(checked(dataset_dir / "report.json", DATASET_SHA))
    audit = json.loads(checked(audit_dir / "report.json", AUDIT_SHA))
    if audit["dataset_sha256"] != DATASET_SHA:
        raise ValueError("Holdout selection is bound to a different dataset")
    entry = next(item for item in dataset["partitions"] if item["partition"] == "internal_train_holdout")
    arrays, identities = load_partition(dataset_dir, entry)
    indices = np.load(BytesIO(checked(audit_dir / "holdout_indices.npy", audit["files"]["holdout_indices"])), allow_pickle=False)
    if (indices.shape != (24,) or indices.dtype != np.int64 or len(set(indices.tolist())) != 24 or
            indices.min() < 0 or indices.max() >= len(identities) or
            len({identities[index][1] for index in indices}) != 24 or
            int(arrays["labels"][indices].sum()) != 10 or len(set(arrays["groups"][indices].tolist())) != 2):
        raise ValueError("Diagnostic holdout selection changed")
    candidate = load_candidate(CHECKPOINT, CHECKPOINT_SHA, BASE).to("cuda:0").eval()
    patches = torch.from_numpy(arrays["patches"][indices].copy()).to("cuda:0")
    _require_training_gpu(patches, *candidate.parameters())
    with torch.inference_mode():
        base = candidate.local(patches)[:, 0]
        actual = candidate(patches)[:, 0]
    _require_training_gpu(base, actual)
    torch.cuda.synchronize()
    summary = compare(arrays["labels"][indices], base.cpu().numpy(), actual.cpu().numpy())
    result = {"schema": "graphreader.controlled-overlay-holdout.v1", "scope": "small-post-hoc-diagnostic",
              "checkpoint_sha256": CHECKPOINT_SHA, "dataset_sha256": DATASET_SHA, "overlap_audit_sha256": AUDIT_SHA,
              "source_sha256": sha256(Path(__file__).read_bytes()).hexdigest(), "threshold": THRESHOLD,
              "device": torch.cuda.get_device_name(0), "architecture": torch.cuda.get_device_properties(0).gcnArchName,
              "cpu_fallback": False, "optimizer_steps": 0, "sealed_payload_reads": 0, "production_approval": False,
              "limitations": ["24 emitted patches from two groups; shared overlay families; not scene-level accuracy."],
              "summary": summary}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with OUTPUT.open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
