# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Audit explicitly bound owned train/dev label layout without model inference."""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import sys
import time

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from ml.synthetic.condition_label_layout import VERSION, bind_condition_label_layout
from ml.synthetic.contextual_negatives import render_contextual_scene
from ml.synthetic.io import png_bytes


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bindings", type=Path, required=True)
    parser.add_argument("--bindings-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if sha(args.bindings) != args.bindings_sha256:
        raise ValueError("Authored binding checksum mismatch")
    document = json.loads(args.bindings.read_text(encoding="utf-8-sig"))
    if document["schema"] != "graphreader.synthetic.authored-condition-bindings.v1":
        raise ValueError("Unknown authored binding schema")
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / "artifacts"):
        raise ValueError("Audit output must be a new repository artifact directory")
    output.mkdir(parents=True, exist_ok=False)

    def identity(path):
        return {"path": path.resolve().relative_to(ROOT).as_posix(), "sha256": sha(path)}

    sources = []
    started = time.perf_counter()
    for index, entry in enumerate(document["sources"]):
        scene_path = ROOT / entry["source_scene"]["path"]
        if sha(scene_path) != entry["source_scene"]["sha256"]:
            raise ValueError("Authored scene changed")
        scene = json.loads(scene_path.read_text(encoding="utf-8-sig"))
        result = bind_condition_label_layout(scene, split=entry["split"], label_bindings=entry["label_bindings"])
        old_image, old_annotation, old_mask = render_contextual_scene(scene, split=entry["split"], source_profile="visible-content-v3")
        image, annotation, mask = render_contextual_scene(result.scene, split=entry["split"], source_profile="visible-content-v3")
        try:
            before_bytes = png_bytes(old_image)
            assert hashlib.sha256(before_bytes).hexdigest() == entry["image"]["sha256"] == sha(ROOT / entry["image"]["path"])
            assert result.scene["panels"] == scene["panels"]
            assert result.scene["degradations"] == scene["degradations"]
            assert np.array_equal(old_mask, mask)
            texts = lambda value: Counter((r["panel_id"], r["role"], r["text"], r["visible"]) for r in value["annotations"]["text_regions"])
            assert texts(scene) == texts(result.scene)
            for before, after in zip(old_annotation["panels"], annotation["panels"]):
                assert {k:v for k,v in before.items() if k != "texts"} == {k:v for k,v in after.items() if k != "texts"}
            for change in result.changes:
                text = next(t for p in annotation["panels"] for t in p["texts"] if t["region_id"] == change["region_id"])
                x,y,w,h = text["rendered_pixel_box"]
                left,top,right,bottom = change["phase_header_area"]
                assert left <= x < x+w <= right and top <= y < y+h <= bottom
            destination = output / "sources" / entry["split"] / f"{index:03d}-{scene['seed']}"
            destination.mkdir(parents=True)
            (destination / "image.png").write_bytes(png_bytes(image))
            (destination / "marker-mask.png").write_bytes(png_bytes(mask))
            for name, value in (("scene.json", result.scene), ("annotation.json", annotation)):
                (destination / name).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
            sources.append({"split":entry["split"],"seed":scene["seed"],"input_scene":entry["source_scene"],
                            "input_image":entry["image"],"scene":identity(destination/"scene.json"),
                            "image":identity(destination/"image.png"),"annotation":identity(destination/"annotation.json"),
                            "marker_mask":identity(destination/"marker-mask.png"),"changes":result.changes,
                            "unresolved":result.unresolved,"scientific_panel_content_and_mask_unchanged":True,
                            "points":sum(len(p["markers"]) for p in annotation["panels"])})
        finally:
            for value in (old_image,old_mask,image,mask): value.close()
    report = {"schema":"graphreader.synthetic.condition-layout-audit.v1","version":VERSION,"bindings":identity(args.bindings),
              "source_count":len(sources),"historical_source_images_reproduced":len(sources),
              "moved_labels":sum(len(r["changes"]) for r in sources),
              "out_of_phase_labels_repaired":sum(c["outside_phase_before"] for r in sources for c in r["changes"]),
              "unresolved":sum(len(r["unresolved"]) for r in sources),"points":sum(r["points"] for r in sources),
              "elapsed_seconds":time.perf_counter()-started,"sources":sources,
              "optimizer_steps":0,"model_inference_runs":0,"private_reads":0,"sealed_reads":0,"production_approved":False}
    (output/"report.json").write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({k:v for k,v in report.items() if k != "sources"}))


if __name__ == "__main__":
    main()
