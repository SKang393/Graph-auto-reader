# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bound synthetic train features for an unregistered diagnostic objective."""
from __future__ import annotations

from io import BytesIO
import numpy as np
import torch

from ..real_positive_context_v1.objective import _require_training_gpu
from ..real_positive_context_v1.runner_foundation import _v37_report, V37_FEATURE_REPORT_SHA256
from ..real_positive_preservation_v1.component_source import DATA, REPORT, V37, VIEWS, _load_bytes
from ..real_positive_context_v1.model import V37_CHECKPOINT_SHA256

_TOKEN = object()
_COHORTS = {"component_positive": ("component", 1, 35838, 3258),
            "family_negative": ("family", 0, 9053, 8230)}


class SyntheticTrainFeatures:
    """Read-only anchor-labeled features, not decoded-object or real truth."""

    def __init__(self, features: torch.Tensor, cohort: str, view: str, *, fixture: bool,
                 binding: dict, _token: object):
        if _token is not _TOKEN or cohort not in _COHORTS:
            raise ValueError("Use an authenticated synthetic train source")
        if (features.device.type != "cpu" or features.dtype != torch.float32 or features.ndim != 2 or
                features.shape[1] != 768 or features.shape[0] == 0 or not torch.isfinite(features).all()):
            raise ValueError("Expected finite CPU float32 train features")
        self._features = features.detach().contiguous()
        self.cohort, self.view, self.fixture, self.binding = cohort, view, fixture, binding

    @classmethod
    def load(cls, cohort: str, view: str) -> "SyntheticTrainFeatures":
        if cohort not in _COHORTS or view not in VIEWS:
            raise ValueError("Only the fixed component-positive and family-negative train cohorts are available")
        report = _v37_report(_load_bytes(DATA / REPORT, V37_FEATURE_REPORT_SHA256), V37_FEATURE_REPORT_SHA256)
        # The cached features were produced by the V27 backbone retained unchanged in V37.
        original_ref = report["model"]
        original = torch.load(BytesIO(_load_bytes(DATA / original_ref["path"], original_ref["sha256"])),
                              map_location="cpu", weights_only=True)["state_dict"]
        v37 = torch.load(BytesIO(_load_bytes(DATA / V37, V37_CHECKPOINT_SHA256)),
                         map_location="cpu", weights_only=True)["state_dict"]
        v37_base = {key.removeprefix("base."): value for key, value in v37.items() if key.startswith("base.")}
        if original.keys() != v37_base.keys() or any(not torch.equal(value, v37_base[key]) for key, value in original.items()):
            raise ValueError("Synthetic feature backbone does not match V37")
        scope, label, rows, selected_count = _COHORTS[cohort]
        matches = [item for item in report["groups"] if item["scope"] == scope + ":" + view]
        if len(matches) != 1:
            raise ValueError("Synthetic feature group is missing or ambiguous")
        group = matches[0]
        label_ref, feature_ref = group["labels"], group["features"]
        labels = np.load(BytesIO(_load_bytes(DATA / label_ref["path"], label_ref["sha256"])), allow_pickle=False)
        features = np.load(BytesIO(_load_bytes(DATA / feature_ref["path"], feature_ref["sha256"])), allow_pickle=False)
        if (group["rows"] != rows or labels.dtype != np.float32 or labels.shape != (rows,) or
                not np.isin(labels, (0, 1)).all() or int((labels == label).sum()) != selected_count or
                features.dtype != np.float32 or features.shape != (rows, 768) or not np.isfinite(features).all()):
            raise ValueError("Synthetic train features or anchor-label denominators changed")
        binding = {"report_sha256": V37_FEATURE_REPORT_SHA256, "v37_sha256": V37_CHECKPOINT_SHA256,
                   "backbone_sha256": original_ref["sha256"], "feature_sha256": feature_ref["sha256"],
                   "labels_sha256": label_ref["sha256"], "scope": scope, "view": view,
                   "anchor_label": label, "rows": selected_count}
        return cls(torch.from_numpy(features[labels == label].copy()), cohort, view,
                   fixture=False, binding=binding, _token=_TOKEN)

    @classmethod
    def _synthetic_fixture(cls, features: torch.Tensor, cohort: str) -> "SyntheticTrainFeatures":
        return cls(features, cohort, "fixture", fixture=True, binding={}, _token=_TOKEN)

    @property
    def count(self) -> int:
        return len(self._features)

    def gpu_batch(self, indices: tuple[int, ...], *, allow_synthetic_fixture: bool = False) -> torch.Tensor:
        _require_training_gpu()
        if self.fixture and not allow_synthetic_fixture:
            raise RuntimeError("Synthetic fixtures are not authenticated train sources")
        if (type(indices) is not tuple or not indices or len(set(indices)) != len(indices) or
                any(type(index) is not int or not 0 <= index < self.count for index in indices)):
            raise ValueError("Feature indices must be unique and in bounds")
        batch = self._features[list(indices)].to(device="cuda:0")
        _require_training_gpu(batch)
        return batch
