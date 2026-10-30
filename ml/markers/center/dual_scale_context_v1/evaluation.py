# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Replay the unchanged full development denominator and proposal geometry."""
from __future__ import annotations

from collections import Counter
import hashlib
from pathlib import Path

import numpy as np
import torch

from ml.markers.gate_seal import canonical_json_bytes
from .. import shape_coverage_evaluation as prior
from .crops import crop_context


def evaluate(repo: Path, scenes: dict, model, session, budget, output: Path) -> dict:
    # Historical benchmark fixtures include procedural component masks. They
    # are used exclusively for the unchanged comparative evaluation, not fit.
    thresholds = tuple(sorted({.1, *prior.THRESHOLDS}))
    paths, old_report, old_prior, hashes = prior.source._authenticate(repo, prior.historical.SOURCE_DIAGNOSTIC_SHA256)
    coordinate_cache, _ = prior.source._load_caches(paths, old_report, old_prior)
    v27_report, v27_cache = prior.historical._load_v27_cache(repo)
    totals = {scope: {kind: {str(t): Counter() for t in thresholds} for kind in
              ("v27_historical", "v27_balanced", "baseline_balanced", "candidate_balanced")}
              for scope in scenes}
    parity = {"rows": 0, "maximum_absolute_error": 0., "confidence_decision_changes": 0}
    records = []
    max_geometry_difference = 0.
    for scope, items in scenes.items():
        for index, bound in enumerate(items):
            scene = bound.scene if hasattr(bound, "scene") else bound
            domain = bound.panel_domain.domain if hasattr(bound, "panel_domain") else None
            old = old_report[scope + "_dev"]["cache_manifest"][index]
            v27 = v27_report[scope + "_dev"]["cache_manifest"][index]
            coordinates = coordinate_cache[old["proposal_coordinates_key"]]
            old_values = v27_cache[v27["v27_candidate_predictions_key"]]
            if (old["scene_identity"] != v27["scene_identity"] or
                    old["scene_identity"] != prior.source._scene_identity(scene) or
                    prior.source.cache_helper._array_sha(coordinates) != v27["proposal_coordinates_sha256"] or
                    prior.source.cache_helper._array_sha(old_values) != v27["v27_candidate_predictions_sha256"]):
                raise ValueError("Development source identity changed")
            metadata = canonical_json_bytes({"dtype": "float32", "shape": [len(coordinates), 3, 33, 33]})
            digest = hashlib.sha256()
            digest.update(len(metadata).to_bytes(8, "little"))
            digest.update(metadata)
            baseline_parts, candidate_parts = [], []
            for start in range(0, len(coordinates), 128):
                with budget.work_block(), torch.inference_mode():
                    anchors = torch.from_numpy(coordinates[start:start+128].copy())
                    patches = crop_context(scene.tensor, anchors)
                    local = patches[:, :, 16:49, 16:49].contiguous()
                    digest.update(local.numpy().tobytes())
                    baseline = model.local(local).numpy()
                    expected = model(patches).numpy()
                    actual = session.run(["candidate_predictions"], {"candidate_patches": patches.numpy()})[0]
                    if actual.shape != expected.shape or not np.isfinite(actual).all():
                        raise ValueError("Malformed or nonfinite runtime predictions")
                    parity["rows"] += len(actual)
                    parity["maximum_absolute_error"] = max(parity["maximum_absolute_error"], float(np.abs(expected-actual).max()))
                    parity["confidence_decision_changes"] += int(((expected[:, 0] >= .1) != (actual[:, 0] >= .1)).sum())
                    max_geometry_difference = max(max_geometry_difference, float(np.abs(baseline[:, 1:] - actual[:, 1:]).max()))
                    baseline_parts.append(baseline)
                    candidate_parts.append(actual)
            if digest.hexdigest() != v27["proposal_patches_sha256"]:
                raise ValueError("Historical development local pixels changed")
            baseline_values = np.concatenate(baseline_parts)
            actual_values = np.concatenate(candidate_parts)
            np.save(output / f"{scope}-{index:03d}-predictions.npy", actual_values, allow_pickle=False)
            with budget.work_block():
                predictions = {
                    "v27_historical": prior.predictions(scene, coordinates, old_values, domain, balanced=False, threshold=min(thresholds)),
                    "v27_balanced": prior.predictions(scene, coordinates, old_values, domain, balanced=True, threshold=min(thresholds)),
                    "baseline_balanced": prior.predictions(scene, coordinates, baseline_values, domain, balanced=True, threshold=min(thresholds)),
                    "candidate_balanced": prior.predictions(scene, coordinates, actual_values, domain, balanced=True, threshold=min(thresholds)),
                }
                centers = np.asarray(scene.centers, dtype=np.float32).reshape(-1, 2)
                supported = int((np.linalg.norm(coordinates[:, None, :] - centers[None, :, :], axis=2).min(axis=0) <= 5).sum()) if len(centers) else 0
                per_scene = {}
                for kind, predictions_at_threshold in predictions.items():
                    for threshold in thresholds:
                        retained = tuple(p for p in predictions_at_threshold if p.confidence >= threshold)
                        pairs = prior.match_predictions(retained, scene.centers)
                        metrics = prior.bars.center_metrics(retained, scene.centers, 5.)
                        counts = {"scene_count": 1, "truth_count": len(scene.centers), "true_positives": len(pairs),
                                  "false_positives": len(retained)-len(pairs), "false_negatives": len(scene.centers)-len(pairs),
                                  "proposal_true_positives": supported, "duplicate_count": metrics.duplicate_count,
                                  "prohibited_structure_hits": sum(prior.legacy.prohibited_hits(retained, scene).values())}
                        totals[scope][kind][str(threshold)].update(counts)
                        if threshold == .1:
                            per_scene[kind] = counts
                records.append({"scope": scope, "scene_index": index, "identity": old["scene_identity"],
                                "proposal_count": len(coordinates), "results": per_scene})
    for scope in totals:
        values = totals[scope]["v27_historical"]["0.25"]
        expected = {"component": (1796, 238, 208), "family": (190, 39, 16)}[scope]
        if tuple(values[k] for k in ("true_positives", "false_positives", "false_negatives")) != expected:
            raise ValueError("Historical development baseline changed")
    prior.source._verify_inputs_unchanged(repo, paths, hashes)
    bar = prior.bars._shared_marker_acceptance_bar()
    results = {scope: {kind: {threshold: prior.summarize_counts(values) for threshold, values in groups.items()}
                       for kind, groups in kinds.items()} for scope, kinds in totals.items()}
    passes = prior.bars._passes_required_dev_gates(results["component"]["candidate_balanced"]["0.1"],
                                                    results["family"]["candidate_balanced"]["0.1"], bar)
    return {"results": results, "records": records, "parity": parity, "acceptance_bar": bar,
            "clears_shared_dev_bars": passes, "operating_threshold": .1,
            "geometry_maximum_absolute_change": max_geometry_difference,
            "threshold_sensitivity_is_descriptive_only": True}
