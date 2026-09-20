# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Hash-bound owned train/dev derivative inventory, without model inference."""
from __future__ import annotations

import argparse
from collections import Counter
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import shutil
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from ml.synthetic.contextual_negatives import VERSION, render_contextual_scene
from ml.synthetic.io import png_bytes
from ml.synthetic.runtime_graph_visible_content_v3 import render_visible_content_source


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def desc(path):
    return {"path": path.relative_to(ROOT).as_posix(), "sha256": sha(path)}


def owned_path(relative):
    path = (ROOT / relative).resolve()
    if not path.is_relative_to((ROOT / "artifacts/goal22-runs").resolve()):
        raise ValueError("Input must belong to the local owned diagnostic run directory")
    return path


def checked(item):
    path = owned_path(item["path"])
    if sha(path) != item["sha256"]:
        raise ValueError(f"Input checksum differs: {item['path']}")
    return path


def write(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def audit(input_path, input_sha256, output, *, legacy_validation_is_dev=False):
    started = time.perf_counter()
    source_path = checked({"path": input_path, "sha256": input_sha256})
    inventory = json.loads(source_path.read_bytes())
    if inventory["scope"] != "project-owned-synthetic-only" or any(inventory[k] for k in
            ("private_corpus_access", "sealed_corpus_access", "truth_consumed_by_inference")):
        raise ValueError("The source inventory must be owned synthetic evidence")
    sources = inventory["provenance"]["source_scenes"]
    split_roles = {"train": "train", "dev": "dev"}
    if legacy_validation_is_dev:
        split_roles["validation"] = "dev"
    if not sources or any(s["split"] not in split_roles for s in sources):
        raise ValueError("Every source must have an explicit train/dev split")
    output = owned_path(output)
    output.mkdir(exist_ok=False)
    bindings = []
    for path in [Path(__file__).resolve(), *(ROOT / "ml/synthetic" / name for name in (
            "contextual_negatives.py", "renderer.py", "runtime_graph_visible_content_v3.py",
            "runtime_graph_axis_preserving_v2.py", "schema.py", "scene.schema.json", "fonts.py", "io.py"))]:
        snapshot = output / "source-snapshot" / path.relative_to(ROOT)
        snapshot.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, snapshot)
        bindings.append({**desc(path), "snapshot": desc(snapshot)})
    write(output / "definition.json", {
        "profile": VERSION, "source_profile": "visible-content-v3", "source_inventory": desc(source_path),
        "hypothesis": "Historical injected glyphs omit the visible context of their negative label; derive negatives from native graph structures instead.",
        "isolated_change": "Replace standalone hard-negative painting with labels on existing graph structures. Keep all sources, true points, text, calibration, degradation and split membership.",
        "validation": "Authenticate and reproduce every original PNG; require identical native annotations and marker masks; report all replacement requests and omitted negative candidates.",
        "acceptance": "Generator correctness only. No model-quality gate or production approval is asserted.",
        "explicit_split_role_mapping": split_roles,
        "source_bindings": bindings, "model_inference_runs": 0, "optimizer_steps": 0,
        "private_reads": 0, "sealed_reads": 0, "sealed_budget": 0,
    })
    results = []
    for index, source in enumerate(sources):
        scene_path, image_path = checked(source["scene"]), checked(source["image"])
        scene = json.loads(scene_path.read_bytes())
        # Ownership/schema checks run before either rendering operation.
        split = split_roles[source["split"]]
        corrected, annotation, mask = render_contextual_scene(scene, split=split, source_profile="visible-content-v3")
        baseline = render_visible_content_source(scene)
        if hashlib.sha256(png_bytes(baseline.image)).hexdigest() != source["image"]["sha256"]:
            raise ValueError("Historical source pixels were not reproduced")
        if png_bytes(mask) != png_bytes(baseline.marker_mask):
            raise ValueError("Marker-mask geometry changed")
        comparison = deepcopy(annotation)
        comparison.pop("contextual_negative_profile")
        comparison["hard_negatives"] = baseline.annotation["hard_negatives"]
        if comparison != baseline.annotation:
            raise ValueError("Native graph annotation changed")
        directory = output / "derived-sources" / split / f"{index:03d}-{scene['seed']}"
        directory.mkdir(parents=True)
        shutil.copyfile(scene_path, directory / "source-scene.json")
        (directory / "image.png").write_bytes(png_bytes(corrected))
        (directory / "marker-mask.png").write_bytes(png_bytes(mask))
        write(directory / "annotation.json", annotation)
        markers = [m for p in annotation["panels"] for m in p["markers"]] + annotation["markers"]
        negatives = baseline.annotation["hard_negatives"]
        old_circles = [n for n in negatives if n["kind"] == "legend_symbol"]
        same_style = sum(any(m["shape"] == n["geometry"]["shape"] and m["fill"] == n["geometry"]["fill"] for m in markers)
                         for n in old_circles)
        results.append({
            "split": split, "source_split_label": source["split"], "source_scene": desc(scene_path), "original_image": desc(image_path),
            "image": desc(directory / "image.png"), "annotation": desc(directory / "annotation.json"),
            "marker_mask": desc(directory / "marker-mask.png"), "scene_seed": scene["seed"],
            "source_panels": len(scene["panels"]), "truth_points": len(markers),
            "original_png_reproduced": True, "native_annotations_unchanged": True, "marker_mask_unchanged": True,
            "replaced_requests": dict(Counter(n["kind"] for n in negatives)),
            "contextual_negative_counts": dict(Counter(n["kind"] for n in annotation["hard_negatives"])),
            "negative_candidate_omissions": dict(Counter(n["reason"] for n in annotation["contextual_negative_profile"]["omitted_native_negative_candidates"])),
            "orphan_legend_glyph_matching_a_true_shape_and_fill": same_style,
        })
    by_split = {}
    for split in ("train", "dev"):
        rows = [r for r in results if r["split"] == split]
        by_split[split] = {"sources": len(rows), "panels": sum(r["source_panels"] for r in rows),
                           "truth_points": sum(r["truth_points"] for r in rows)}
        for key in ("replaced_requests", "contextual_negative_counts", "negative_candidate_omissions"):
            counter = Counter()
            for row in rows:
                counter.update(row[key])
            by_split[split][key] = dict(counter)
        by_split[split]["orphan_legend_glyph_matching_a_true_shape_and_fill"] = sum(r["orphan_legend_glyph_matching_a_true_shape_and_fill"] for r in rows)
    report = {"status": "generator_checks_passed_not_model_acceptance", "profile": VERSION,
              "definition": desc(output / "definition.json"), "sources": results, "by_split": by_split,
              "elapsed_seconds": time.perf_counter() - started, "model_inference_runs": 0,
              "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0, "production_approved": False}
    write(output / "report.json", report)
    print(json.dumps({"report": desc(output / "report.json"), "by_split": by_split, "seconds": report["elapsed_seconds"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True)
    parser.add_argument("--input-sha256", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--legacy-validation-is-dev", action="store_true",
                        help="Explicitly map this owned inventory's historical validation label to its development role")
    arguments = parser.parse_args()
    audit(arguments.input, arguments.input_sha256, arguments.output,
          legacy_validation_is_dev=arguments.legacy_validation_is_dev)
