# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authenticated synthetic component-train features, not real negatives."""
from __future__ import annotations

from hashlib import sha256
from io import BytesIO
from pathlib import Path

import numpy as np
import torch

from ..real_positive_context_v1.model import V37_CHECKPOINT_SHA256
from ..real_positive_context_v1.objective import _require_training_gpu
from ..real_positive_context_v1.positive_archive import _open_read_only_no_writers
from ..real_positive_context_v1.runner_foundation import _v37_report, V37_FEATURE_REPORT_SHA256

ROOT = Path(__file__).resolve().parents[4]
DATA = ROOT / "artifacts/goal22-worktrees/resume-20260908"
REPORT = Path("artifacts/goal22-runs/reflected-spatial-features-20261002-v1/report.json")
V37 = Path("artifacts/goal22-runs/center-confidence-reflection-v37/P1/marker-center.pt")
VIEWS = ("identity", "horizontal", "vertical", "both")
_TOKEN = object()


def _load_bytes(path: Path, expected: str) -> bytes:
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(ROOT.resolve(strict=True)):
        raise ValueError("Component source is outside the project")
    with _open_read_only_no_writers(resolved) as stream:
        raw = stream.read()
    if sha256(raw).hexdigest() != expected:
        raise ValueError("Component source hash changed")
    return raw


class ComponentNegativeStore:
    """Read-only selected synthetic negatives from one fixed reflected view.

    This is not training admission. Caller must separately register the source,
    recipe, weights, deterministic schedule and GPU runner.
    """

    def __init__(self, features: torch.Tensor, *, fixture: bool, view: str, _token: object):
        if _token is not _TOKEN:
            raise ValueError("Use the authenticated component source")
        if (features.device.type != "cpu" or features.dtype != torch.float32 or features.ndim != 2 or
                features.shape[1] != 768 or features.shape[0] == 0 or not torch.isfinite(features).all()):
            raise ValueError("Component negatives must be finite CPU float32 spatial features")
        self._features = features.detach().contiguous().clone()
        self.fixture = fixture
        self.view = view

    @classmethod
    def open_authenticated(cls, view: str) -> "ComponentNegativeStore":
        if view not in VIEWS:
            raise ValueError("Unknown fixed component reflection")
        report_raw = _load_bytes(DATA / REPORT, V37_FEATURE_REPORT_SHA256)
        report = _v37_report(report_raw, V37_FEATURE_REPORT_SHA256)
        origin_ref = report["model"]
        origin = torch.load(BytesIO(_load_bytes(DATA / origin_ref["path"], origin_ref["sha256"])),
                            map_location="cpu", weights_only=True)["state_dict"]
        v37 = torch.load(BytesIO(_load_bytes(DATA / V37, V37_CHECKPOINT_SHA256)),
                         map_location="cpu", weights_only=True)["state_dict"]
        v37_base = {key.removeprefix("base."): value for key, value in v37.items() if key.startswith("base.")}
        if origin.keys() != v37_base.keys() or any(not torch.equal(value, v37_base[key]) for key, value in origin.items()):
            raise ValueError("Frozen component feature backbone differs from V37")
        group = next(item for item in report["groups"] if item["scope"] == "component:" + view)
        if group.get("rows") != 35838 or group.get("positive") != 3258 or group.get("negative") != 32580:
            raise ValueError("Component train population changed")
        labels_ref, features_ref = group["labels"], group["features"]
        labels = np.load(BytesIO(_load_bytes(DATA / labels_ref["path"], labels_ref["sha256"])), allow_pickle=False)
        features = np.load(BytesIO(_load_bytes(DATA / features_ref["path"], features_ref["sha256"])), allow_pickle=False)
        if (labels.dtype != np.float32 or labels.shape != (35838,) or
                features.dtype != np.float32 or features.shape != (35838, 768) or
                not np.isfinite(labels).all() or not np.isfinite(features).all() or
                not np.isin(labels, (0, 1)).all() or int((labels == 0).sum()) != 32580):
            raise ValueError("Component feature and label contract changed")
        return cls(torch.from_numpy(features[labels == 0].copy()), fixture=False, view=view, _token=_TOKEN)

    @classmethod
    def _synthetic_fixture(cls, features: torch.Tensor) -> "ComponentNegativeStore":
        return cls(features, fixture=True, view="synthetic_fixture", _token=_TOKEN)

    @property
    def count(self) -> int:
        return len(self._features)

    def gpu_batch(self, indices: tuple[int, ...], *, allow_synthetic_fixture: bool = False) -> torch.Tensor:
        _require_training_gpu()
        if self.fixture and not allow_synthetic_fixture:
            raise RuntimeError("Synthetic fixture is not an authenticated component source")
        if (type(indices) is not tuple or not indices or
                any(type(index) is not int or not 0 <= index < self.count for index in indices) or
                len(set(indices)) != len(indices)):
            raise ValueError("Component negative indices must be unique and in bounds")
        batch = self._features[list(indices)].to(device="cuda:0")
        _require_training_gpu(batch)
        return batch


def verify_v37_model(model) -> None:
    """Bind the complete frozen local model to the retained V37 checkpoint."""
    raw = _load_bytes(DATA / V37, V37_CHECKPOINT_SHA256)
    expected = torch.load(BytesIO(raw), map_location="cpu", weights_only=True)["state_dict"]
    current = model.local.state_dict()
    if current.keys() != expected.keys() or any(
            not torch.equal(value.detach().cpu(), expected[name]) for name, value in current.items()):
        raise RuntimeError("The frozen local model does not match V37")
