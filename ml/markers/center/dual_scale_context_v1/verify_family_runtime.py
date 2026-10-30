# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Prove each family training tensor is made from bound runtime output planes."""
from __future__ import annotations

import json
from io import BytesIO

import numpy as np
import torch

from .prepare import ATTR, PINS, verified_bytes

BINDING = "artifacts/goal22-runs/marker-v25-plot-domain/visible-content-v3-binding/binding-v3.json"
BINDING_SHA = "bcf821a2aaadc5ee8e18bd97dbe0488d120d2ac974a249c00d76b6cfccf0d98a"


def verify() -> dict:
    binding = json.loads(verified_bytes(BINDING, BINDING_SHA).decode("utf-8-sig"))
    binding_flags = {name: binding[name] for name in
                     ("training_input_ready", "complete_artifact_mask", "synthetic_only",
                      "private_data", "sealed_runs", "production_approved")}
    if binding_flags != {"training_input_ready": True, "complete_artifact_mask": False,
                         "synthetic_only": True, "private_data": False, "sealed_runs": 0,
                         "production_approved": False}:
        raise ValueError("Bound runtime readiness or authorization changed")
    attr = json.loads(verified_bytes(ATTR, PINS[ATTR]).decode("utf-8-sig"))
    if attr.get("development_panels_evaluated") != 0 or any(
            attr.get(k) != 0 for k in ("private_reads", "sealed_reads", "optimizer_steps")):
        raise ValueError("Attribution scope changed")
    refs = {row["path"]: row["sha256"] for row in attr["evidence"]}
    scene_ref = next(path for path in refs if path.endswith("/train-scenes.pt"))
    family = torch.load(BytesIO(verified_bytes(scene_ref, refs[scene_ref])), map_location="cpu", weights_only=True)["family"]
    panels = {}
    train_sources = set()
    source_flags = []
    for entry in binding["train"]:
        if entry["split"] != "train":
            raise ValueError("Unexpected split in training binding")
        verified_bytes(entry["manifest_path"], entry["manifest_sha256"])
        report = json.loads(verified_bytes(entry["report_path"], entry["report_sha256"]).decode("utf-8-sig"))
        if report["input_manifest_sha256"] != entry["manifest_sha256"]:
            raise ValueError("Runtime report and source manifest do not match")
        source_flags.append({"training_input_ready": report["training_input_ready"],
                             "complete_artifact_mask": report["complete_artifact_mask"],
                             "missing_stage": report["missing_stage"]})
        for case in report["cases"]:
            train_sources.add(case["image_sha256"])
            for panel in case["panels"]:
                key = panel["panel_id"]
                if key in panels:
                    raise ValueError("Duplicate runtime panel identity")
                panels[key] = (entry["report_path"].rsplit("/", 1)[0], case, panel)
    if len(family) != 28 or len(panels) != 28 or len(train_sources) != 20:
        raise ValueError("Family runtime inventory changed")
    dev_binding = binding["dev"]
    if dev_binding["split"] != "validation":
        raise ValueError("Unexpected development source split")
    verified_bytes(dev_binding["manifest_path"], dev_binding["manifest_sha256"])
    dev_report = json.loads(verified_bytes(dev_binding["report_path"], dev_binding["report_sha256"]).decode("utf-8-sig"))
    if dev_report["input_manifest_sha256"] != dev_binding["manifest_sha256"]:
        raise ValueError("Development report and source manifest do not match")
    dev_sources = {case["image_sha256"] for case in dev_report["cases"]}
    if len(dev_sources) != 3 or train_sources & dev_sources:
        raise ValueError("Training and development source identities are not disjoint")
    if any(flags != {"training_input_ready": False, "complete_artifact_mask": False,
                     "missing_stage": "representative-artifact-validation-and-training-input-binding"}
           for flags in source_flags):
        raise ValueError("Diagnostic runtime mask readiness changed")
    seen = set()
    for item in family:
        meta = item["metadata"]
        if meta["split"] != "train" or meta["panel_id"] in seen:
            raise ValueError("Unexpected family source or split")
        seen.add(meta["panel_id"])
        directory, case, panel = panels[meta["panel_id"]]
        if (panel["source_image_sha256"] != meta["source_sha256"] or
                panel["image_sha256"] != meta["panel_sha256"]):
            raise ValueError("Saved family source identity changed")
        shape = (panel["height"], panel["width"])
        planes = []
        for key, dtype in (("source_gray", "u1"), ("ocr_mask", "<f4"),
                           ("composed_artifact_candidate_mask", "<f4")):
            spec = panel[key]
            relative = f"{directory}/{case['image_sha256']}/{meta['panel_id']}/{spec['file']}"
            data = verified_bytes(relative, spec["sha256"])
            values = np.frombuffer(data, dtype=dtype)
            if len(values) != shape[0] * shape[1] or len(data) != spec["byte_count"]:
                raise ValueError("Runtime plane dimensions changed")
            planes.append(values.reshape(shape))
        tensor = np.stack((np.float32(1) - planes[0].astype(np.float32) / np.float32(255),
                           planes[1], planes[2]))
        if not np.array_equal(tensor, item["tensor"].numpy()):
            raise ValueError("Saved tensor is not identical to authenticated runtime planes")
    if seen != set(panels) or {item["metadata"]["source_sha256"] for item in family} != train_sources:
        raise ValueError("Family panel membership changed")
    return {"status": "runtime_plane_parity_pass", "training_panels": len(seen),
            "training_sources": len(train_sources), "development_source_identities": len(dev_sources),
            "train_development_source_overlap": 0,
            "training_runtime_reports": len(binding["train"]), "selected_rows": 9053,
            "runtime_mask_readiness": source_flags[0],
            "bound_v3_readiness": binding_flags,
            "runtime_binding_sha256": BINDING_SHA,
            "train_scene_archive_sha256": refs[scene_ref],
            "train_manifest_sha256": [entry["manifest_sha256"] for entry in binding["train"]],
            "train_report_sha256": [entry["report_sha256"] for entry in binding["train"]],
            "train_source_image_sha256": sorted(train_sources),
            "validation_source_image_sha256": sorted(dev_sources),
            "private_reads": 0, "sealed_reads": 0, "development_identity_reads": 1,
            "development_labels_or_pixels_used_for_training": False,
            "component_development_source_disjointness_verified": False,
            "optimizer_steps": 0, "model_inference_runs": 0}


if __name__ == "__main__":
    print(json.dumps(verify(), sort_keys=True, indent=2))
