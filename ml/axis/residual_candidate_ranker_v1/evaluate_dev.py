# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score the frozen ranker on the previously inspected synthetic dev set.

Candidate rankings are written before the annotation files are opened. This is
offline CPU inference only, and makes no production acceptance claim.
"""
from collections import Counter
from hashlib import sha256
import json

import numpy as np

from ml.axis.candidate_ranker_dev_eval_v1 import evaluator
from ml.axis.residual_candidate_ranker_v1 import evaluate_train, runner

EXPORT = "artifacts/goal22-runs/axis-candidate-evidence-public-v3-dev-prep-20261031-v5/report.json"
EXPORT_SHA = "272e80e0b4c2a0a471f0eb7f11ac45c4991aa49835bcf1143194f205d03bd8bb"
MANIFEST = "artifacts/axis-roles-dev-corpus-v3-20261031/manifest.json"
MANIFEST_SHA = "c3c735a38dac5aa893331958ba09319e7fcfac534f8122738d8d07db37f507b1"
PRIOR = "artifacts/goal22-runs/axis-ranker-dev-eval-20261031-v3/report.json"
PRIOR_SHA = "362850c0788abdb95e03dc595e92f5988ae34438d86d74d23b03e3893c9dc7e6"
CHECKPOINT_SHA = "4c35bd9424f209fe7e9912560fc4fdcfb1d0580f1af591543bac2461ba4d0adb"
REPORT_SHA = "129d8f7284a877f76e52887b5e94d30ff382e500d136d2f5ce8c9c3af7a88f64"
HELPER_HASHES = {
    "ml/axis/candidate_ranker_dev_eval_v1/evaluator.py": "be387addf8e38a65b06635d6cbf1aa484accc5866f1520c0d2542cb14fefead5",
    "ml/axis/candidate_ranker_foundation_v1/features.py": "057d7dc587cf0b16fff2b9aead0908f6dedaa8cab7ec135611bae276728c0558",
    "ml/axis/candidate_ranker_foundation_v1/labels.py": "be2d216c3c92dba8d05f0740bbd634ab1f2fa2c762654b181dcefc1cb308f561",
}


def read_bound(relative: str, expected: str) -> dict:
    raw = (runner.ROOT / relative).read_bytes()
    if sha256(raw).hexdigest() != expected:
        raise ValueError(f"Bound synthetic evaluation input differs: {relative}")
    return json.loads(raw)


def summarize(pairs: list[tuple[float, float]], changed: int, fallbacks: int) -> dict:
    if not pairs or not all(np.isfinite((baseline, candidate)).all() and min(baseline, candidate) >= 0
                            for baseline, candidate in pairs):
        raise ValueError("Synthetic development errors are missing or invalid")
    return {
        "scored_panels": len(pairs), "changed_selection_panels": changed, "unscored_fallback_panels": fallbacks,
        "improved": sum(candidate < baseline - 1e-12 for baseline, candidate in pairs),
        "equal": sum(abs(candidate - baseline) <= 1e-12 for baseline, candidate in pairs),
        "worsened": sum(candidate > baseline + 1e-12 for baseline, candidate in pairs),
        "native_mean_normalized_corner_error": sum(baseline for baseline, _ in pairs) / len(pairs),
        "candidate_mean_normalized_corner_error": sum(candidate for _, candidate in pairs) / len(pairs),
    }


def predictions(export: dict, checkpoint: dict) -> list[dict]:
    if (export.get("status"), export.get("scope"), export.get("split"), export.get("source_count"),
            export.get("panel_count"), export.get("fallback_source_count"), export.get("failed_source_count")) != (
            "success", "public_synthetic_development", "dev", 32, 48, 2, 0):
        raise ValueError("Synthetic development export scope or denominators differ")
    output = []
    for panel in export["panel_results"]:
        if panel.get("status") != "success":
            raise ValueError("A synthetic development panel did not complete export")
        vectors = evaluator._validate_feature_rows(panel)
        selected = int(np.argmax(evaluate_train.score(vectors, checkpoint))) + 1
        output.append({key: panel[key] for key in ("source_id", "panel_id", "source_sha256", "panel_sha256")} |
                      {"rank": selected})
    if len(output) != 48 or len({(row["source_id"], row["panel_id"]) for row in output}) != 48:
        raise ValueError("Synthetic development panel identities differ")
    return output


def main() -> None:
    for path, digest in HELPER_HASHES.items():
        if sha256((runner.ROOT / path).read_bytes()).hexdigest() != digest:
            raise ValueError("Synthetic evaluation helper source differs: " + path)
    checkpoint = read_bound("artifacts/goal22-runs/axis-residual-ranker-v1/checkpoint.json", CHECKPOINT_SHA)
    training = read_bound("artifacts/goal22-runs/axis-residual-ranker-v1/report.json", REPORT_SHA)
    if (training.get("checkpoint_sha256") != CHECKPOINT_SHA or training.get("steps") != runner.STEPS or
            training.get("status") != "training_complete_unapproved" or training.get("cpu_fallback") is not False or
            training.get("sealed_reads") != 0 or checkpoint.get("production_approved") is not False):
        raise ValueError("Frozen training provenance differs")
    export = read_bound(EXPORT, EXPORT_SHA)
    prediction_rows = predictions(export, checkpoint)
    prediction_document = {"schema": "graphreader.axis-residual-dev-predictions.v1", "export_sha256": EXPORT_SHA,
                           "checkpoint_sha256": CHECKPOINT_SHA, "annotation_reads_before_prediction": 0,
                           "panels": prediction_rows}
    prediction_raw = (json.dumps(prediction_document, sort_keys=True, separators=(",", ":")) + "\n").encode()
    runner.write_new(runner.OUT / "dev-predictions.json", prediction_raw)

    # Truth is opened only after all candidate ranks have been fixed on disk.
    manifest = read_bound(MANIFEST, MANIFEST_SHA)
    prior = read_bound(PRIOR, PRIOR_SHA)
    if len(manifest.get("dev_cases", [])) != 32 or len(prior.get("panels", [])) != 48:
        raise ValueError("Synthetic manifest or previous evaluation denominator differs")
    annotations = {}
    cases = {}
    for case in manifest["dev_cases"]:
        index = case.get("index")
        if type(index) is not int or not 0 <= index < 32 or case["scene_id"] in cases:
            raise ValueError("Synthetic annotation identity differs")
        annotations[case["scene_id"]] = read_bound(
            f"artifacts/axis-roles-dev-corpus-v3-20261031/annotations/{index}.json", case["annotation_sha256"])
        cases[case["scene_id"]] = case
    associations = evaluator._preflight_annotation_panel_identities(manifest, export, annotations)
    old_panels = {(row["source_id"], row["panel_id"]): row for row in prior["panels"]}
    ranks = {(row["source_id"], row["panel_id"]): row for row in prediction_rows}
    if len(old_panels) != 48 or set(old_panels) != set(ranks):
        raise ValueError("Previous and frozen panel identities differ")
    pairs, changed, fallbacks = [], 0, 0
    statuses = Counter()
    for panel in export["panel_results"]:
        key = (panel["source_id"], panel["panel_id"])
        old, prediction = old_panels[key], ranks[key]
        if any(old[k] != panel[k] or prediction[k] != panel[k] for k in ("source_sha256", "panel_sha256")):
            raise ValueError("Synthetic panel source hashes differ")
        statuses[old["scoring_status"]] += 1
        if old["scoring_status"] == "fallback_not_scored":
            fallbacks += 1
            continue
        if old["scoring_status"] != "scored":
            raise ValueError("Synthetic development panel is unexpectedly unscored")
        matches = {match["panel_id"]: match["plot_id"] for match in associations[key[0]]["matches"]}
        if matches.get(key[1]) != old["plot_id"]:
            raise ValueError("Synthetic annotation association differs from prior evaluation")
        plots = evaluator._normalize_dev_annotation_plots(annotations[key[0]], key[0], cases[key[0]]["seed"])
        plot = next(item for item in plots if item["id"] == old["plot_id"])
        crop = [panel["encoded_crop_in_source_pixels"][k] for k in ("X", "Y", "Width", "Height")]
        snapshots = panel["candidate_snapshots"]
        selected = prediction["rank"] - 1
        if not 0 <= selected < len(snapshots):
            raise ValueError("Frozen candidate rank is outside the exported panel")
        baseline = evaluator._corner_metrics(plot, crop, snapshots[0])[1]
        candidate = evaluator._corner_metrics(plot, crop, snapshots[selected])[1]
        if abs(baseline - old["baseline_mean_normalized_corner_error"]) > 1e-12:
            raise ValueError("Native baseline differs from prior synthetic measurement")
        pairs.append((baseline, candidate))
        changed += selected != 0
    if statuses != Counter({"scored": 46, "fallback_not_scored": 2}):
        raise ValueError("Synthetic scored and fallback denominators differ")
    result = {"schema": "graphreader.axis-residual-ranker-dev-evaluation.v1", "split": "synthetic_dev",
              "source_count": 32, "panel_count": 48, "checkpoint_sha256": CHECKPOINT_SHA,
              "training_report_sha256": REPORT_SHA, "export_sha256": EXPORT_SHA, "manifest_sha256": MANIFEST_SHA,
              "prior_evaluation_sha256": PRIOR_SHA, "prediction_sha256": sha256(prediction_raw).hexdigest(),
              "annotation_reads_before_prediction": 0, "sealed_reads": 0, "production_approved": False,
              **summarize(pairs, changed, fallbacks)}
    raw = (json.dumps(result, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
    runner.write_new(runner.OUT / "dev-score.json", raw)
    print(raw.decode(), end="")


if __name__ == "__main__":
    main()
