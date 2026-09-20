# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Complete frozen development evaluation at the existing operating point."""
from __future__ import annotations

from collections import Counter
import hashlib
import math
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import torch

from ml.markers.gate_seal import canonical_json_bytes
from .balanced_support_diagnostic import balanced_center_support
from .component_diversity_v27 import diagnose_head_swap as historical
from .component_diversity_v27 import diagnose_score_localization as source
from .line_aware_v1.pipeline import MarkerPrediction
from .mask_preserving_v24 import mask_preserving as legacy
from .mask_preserving_v24 import train_p1 as bars
from .plot_domain_v25 import diagnose_dev as matching

THRESHOLDS = (.25, .4, .55, .7)


def match_predictions(items, centers):
    # The frozen diagnostic matcher consumes traced candidates, whose public
    # prediction is the same MarkerPrediction value used by runtime decoding.
    return matching.greedy_match_pairs(tuple(SimpleNamespace(prediction=p) for p in items), centers)


def predictions(scene, coordinates, output, domain, *, balanced: bool):
    if output.shape != (len(coordinates), 4) or not np.isfinite(output).all():
        raise ValueError("Invalid proposal output")
    candidates = []
    ink = scene.tensor[0].numpy()
    for i in np.flatnonzero(output[:, 0] >= .25):
        x = float(coordinates[i, 0] + output[i, 1]*4.)
        y = float(coordinates[i, 1] + output[i, 2]*4.)
        if domain is not None and not domain.contains(x, y):
            continue
        supported = balanced_center_support(ink, x, y) if balanced else legacy._consensus(scene, x, y)
        if supported:
            candidates.append(MarkerPrediction(x, y, float(np.clip(output[i, 3], 2.5, 8.)), float(output[i, 0])))
    accepted = []
    for item in sorted(candidates, key=lambda p: (-p.confidence, p.y, p.x)):
        if not any(math.hypot(item.x-p.x, item.y-p.y) < max(5., 1.25*max(item.radius, p.radius)) for p in accepted):
            accepted.append(item)
    return tuple(sorted(accepted, key=lambda p: (p.y, p.x, -p.confidence)))


def summarize_counts(counts: Counter) -> dict:
    result = dict(counts)
    result["precision"] = counts["true_positives"]/max(1, counts["true_positives"]+counts["false_positives"])
    result["recall"] = counts["true_positives"]/max(1, counts["truth_count"])
    result["proposal_recall"] = counts["proposal_true_positives"]/max(1, counts["truth_count"])
    result["prohibited_structure_hit_rate"] = counts["prohibited_structure_hits"]/max(1, counts["true_positives"]+counts["false_positives"])
    return result


def evaluate(repo: Path, scenes: dict, model, session, budget, output: Path) -> dict:
    paths, old_report, prior_report, hashes = source._authenticate(repo, historical.SOURCE_DIAGNOSTIC_SHA256)
    coordinates_cache, _ = source._load_caches(paths, old_report, prior_report)
    baseline_report, baseline_cache = historical._load_v27_cache(repo)
    totals, records = {}, []
    parity = {"rows": 0, "maximum_absolute_error": 0., "confidence_decision_changes": 0}
    for scope, items in scenes.items():
        totals[scope] = {kind: {str(t): Counter() for t in THRESHOLDS} for kind in (
            "v27_historical", "v27_balanced", "candidate_historical", "candidate_balanced")}
        for index, bound in enumerate(items):
            scene = bound.scene if hasattr(bound, "scene") else bound
            domain = bound.panel_domain.domain if hasattr(bound, "panel_domain") else None
            old = old_report[scope+"_dev"]["cache_manifest"][index]
            baseline = baseline_report[scope+"_dev"]["cache_manifest"][index]
            coordinates = coordinates_cache[old["proposal_coordinates_key"]]
            baseline_values = baseline_cache[baseline["v27_candidate_predictions_key"]]
            if (old["scene_identity"] != baseline["scene_identity"] or
                    old["scene_identity"] != source._scene_identity(scene) or
                    source.cache_helper._array_sha(coordinates) != baseline["proposal_coordinates_sha256"] or
                    source.cache_helper._array_sha(baseline_values) != baseline["v27_candidate_predictions_sha256"]):
                raise ValueError("Frozen development proposals or baseline changed")
            padded = np.pad(scene.tensor.numpy(), ((0, 0), (16, 16), (16, 16)))
            metadata = canonical_json_bytes({"dtype": "float32", "shape": [len(coordinates), 3, 33, 33]})
            patch_hash = hashlib.sha256()
            patch_hash.update(len(metadata).to_bytes(8, "little")); patch_hash.update(metadata)
            values = []
            for start in range(0, len(coordinates), 128):
                with budget.work_block(), torch.inference_mode():
                    batch_coordinates = coordinates[start:start+128]
                    if not np.equal(batch_coordinates, np.floor(batch_coordinates)).all():
                        raise ValueError("Non-integral frozen grid coordinate")
                    patches = np.stack([padded[:, int(y):int(y)+33, int(x):int(x)+33] for x, y in batch_coordinates])
                    patch_hash.update(patches.tobytes())
                    expected = model(torch.from_numpy(patches)).numpy()
                    actual = session.run(["candidate_predictions"], {"candidate_patches": patches})[0]
                    if actual.shape != expected.shape or not np.isfinite(actual).all():
                        raise ValueError("Non-finite or malformed runtime predictions")
                    parity["rows"] += len(actual)
                    parity["maximum_absolute_error"] = max(parity["maximum_absolute_error"], float(np.abs(expected-actual).max()))
                    parity["confidence_decision_changes"] += int(((expected[:, 0] >= .25) != (actual[:, 0] >= .25)).sum())
                    values.append(actual)
            if patch_hash.hexdigest() != baseline["proposal_patches_sha256"]:
                raise ValueError("The exact historical development pixels changed")
            actual = np.concatenate(values)
            np.save(output/f"{scope}-{index:03d}-predictions.npy", actual, allow_pickle=False)
            with budget.work_block():
                all_predictions = {
                    "v27_historical": predictions(scene, coordinates, baseline_values, domain, balanced=False),
                    "v27_balanced": predictions(scene, coordinates, baseline_values, domain, balanced=True),
                    "candidate_historical": predictions(scene, coordinates, actual, domain, balanced=False),
                    "candidate_balanced": predictions(scene, coordinates, actual, domain, balanced=True)}
                centers = np.asarray(scene.centers, dtype=np.float32).reshape(-1, 2)
                supported = int((np.linalg.norm(coordinates[:, None, :]-centers[None, :, :], axis=2).min(axis=0) <= 5).sum()) if len(centers) else 0
                per_scene = {}
                for kind, items_at_threshold in all_predictions.items():
                    for threshold in THRESHOLDS:
                        retained = tuple(p for p in items_at_threshold if p.confidence >= threshold)
                        pairs = match_predictions(retained, scene.centers)
                        metrics = bars.center_metrics(retained, scene.centers, 5.)
                        values_at_threshold = {"scene_count": 1, "truth_count": len(scene.centers),
                            "true_positives": len(pairs), "false_positives": len(retained)-len(pairs),
                            "false_negatives": len(scene.centers)-len(pairs), "proposal_true_positives": supported,
                            "duplicate_count": metrics.duplicate_count,
                            "prohibited_structure_hits": sum(legacy.prohibited_hits(retained, scene).values())}
                        totals[scope][kind][str(threshold)].update(values_at_threshold)
                        if threshold == .25:
                            per_scene[kind] = values_at_threshold
                records.append({"scope": scope, "scene_index": index, "identity": old["scene_identity"],
                                "proposal_count": len(coordinates), "results": per_scene})
    # Original V27 results must be reproduced before comparing new geometry.
    for scope in totals:
        result = totals[scope]["v27_historical"]["0.25"]
        expected = {"component": (1796, 238, 208), "family": (190, 39, 16)}[scope]
        if tuple(result[k] for k in ("true_positives", "false_positives", "false_negatives")) != expected:
            raise ValueError("Historical full development results were not reproduced")
    source._verify_inputs_unchanged(repo, paths, hashes)
    bar = bars._shared_marker_acceptance_bar()
    results = {scope: {kind: {threshold: summarize_counts(c) for threshold, c in thresholds.items()}
                      for kind, thresholds in kinds.items()} for scope, kinds in totals.items()}
    clears = bars._passes_required_dev_gates(results["component"]["candidate_balanced"]["0.25"],
                                           results["family"]["candidate_balanced"]["0.25"], bar)
    return {"results": results, "records": records, "parity": parity, "acceptance_bar": bar,
            "clears_shared_dev_bars": clears, "threshold_sensitivity_is_descriptive_only": True,
            "operating_threshold": .25, "geometry": "multiradius_enclosed_balanced_v2"}
