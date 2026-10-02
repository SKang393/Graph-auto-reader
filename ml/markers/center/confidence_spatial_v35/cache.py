# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Admit all checksum-bound training features without resampling or relabeling."""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import torch

from ml.markers.gate_seal import sha256_file


def load_training(repo: Path, report_path: Path, expected_sha256: str,
                  expected_rows: dict[str, int], expected_model_sha256: str, budget):
    if sha256_file(repo / report_path) != expected_sha256:
        raise ValueError("Frozen feature report changed")
    report = json.loads((repo / report_path).read_text(encoding="utf-8"))
    if (report.get("status") != "frozen_training_spatial_features_measured" or
            report.get("training_rows") != sum(expected_rows.values()) or
            report.get("model_parameters_and_buffers_unchanged") is not True or
            report.get("raw_reconstruction_max_absolute_error") != 0 or report.get("feature_width") != 768 or
            any(report.get(key) != 0 for key in ("optimizer_steps", "private_reads", "sealed_reads", "development_tensor_reads")) or
            report.get("model", {}).get("sha256") != expected_model_sha256):
        raise ValueError("Frozen feature identity, population or read scope changed")
    groups = report.get("groups", [])
    if ([group.get("scope") for group in groups] != list(expected_rows) or
            any(group.get("rows") != expected_rows[group["scope"]] for group in groups)):
        raise ValueError("Training group membership, order or row count changed")
    for item in [report["model"], report["parent_feature_report"], *report["evidence"]]:
        if sha256_file(repo / item["path"]) != item["sha256"]:
            raise ValueError("Frozen feature evidence changed")
    pieces = [[], [], []]
    for group in groups:
        with budget.work_block():
            for name in ("tensors", "features", "raw", "labels", "hard_negative"):
                item = group[name]
                if sha256_file(repo / item["path"]) != item["sha256"]:
                    raise ValueError(f"Training payload changed: {group['scope']}/{name}")
            arrays = [np.load(repo / group[name]["path"], allow_pickle=False)
                      for name in ("features", "labels", "hard_negative")]
            features, labels, hard = arrays
            count = group["rows"]
            if (features.shape != (count, 768) or labels.shape != (count,) or hard.shape != (count,) or
                    features.dtype != np.float32 or labels.dtype != np.float32 or
                    hard.dtype not in (np.dtype(np.float32), np.dtype(np.bool_)) or
                    any(not np.isfinite(value).all() for value in arrays) or
                    not np.isin(labels, (0, 1)).all() or not np.isin(hard, (0, 1)).all() or
                    int((labels == 1).sum()) != group["positive"] or
                    int((labels == 0).sum()) != group["negative"] or
                    group.get("raw_reconstruction_max_absolute_error") != 0):
                raise ValueError("Training feature or label contract changed")
            # Historical groups store boolean flags; newer groups store 0/1
            # float32 flags. Preserve every flag while making their dtype uniform.
            arrays[2] = hard.astype(np.float32, copy=False)
            for destination, value in zip(pieces, arrays, strict=True):
                destination.append(torch.from_numpy(value))
    with budget.work_block():
        training = tuple(torch.cat(values) for values in pieces)
    return training, report
