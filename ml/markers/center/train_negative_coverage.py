# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Mine fixed-model mistakes on authenticated training scenes only."""
from __future__ import annotations

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import shutil
import time

import numpy as np
import onnxruntime as ort
import torch

from ml.markers.gate_seal import sha256_file
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from . import shape_coverage_cache as parent
from .component_diversity_v27 import train_p1 as v27
from .mask_preserving_v24 import mask_preserving as proposals

VERSION = "marker-center-train-negative-coverage-v1"
MODEL_PATH = "artifacts/goal22-runs/center-shape-v28/P1-retry1/marker-center.onnx"
MODEL_SHA256 = "a9db0b508a8c3b909d457adbe0e6aa4259369b6389cdad8e726164ea467ba099"
PARENT_PATH = "artifacts/goal22-runs/center-shape-coverage-cache-v3"
PARENT_MANIFEST_SHA256 = "d61e43d62891a5266dfd68dfd8286fdb0d28d685d2146763a9f49dfb24471700"
DIAGNOSTIC_PATH = "artifacts/goal22-runs/center-shape-training-coverage-v1"
SOURCE_PATHS = tuple(dict.fromkeys((*parent.SOURCE_PATHS,
    "ml/markers/center/train_negative_coverage.py")))


def select_negative_indices(scene, coordinates: np.ndarray, outputs: np.ndarray) -> np.ndarray:
    """Keep all confident, clearly separated background anchors at shipped 0.25."""
    if scene.split != "train":
        raise ValueError("Negative mining accepts train scenes only")
    centers = np.asarray(scene.centers, dtype=np.float32).reshape(-1, 2)
    if (coordinates.ndim != 2 or coordinates.shape[1] != 2 or
            outputs.shape != (len(coordinates), 4) or not len(centers) or
            not all(np.isfinite(a).all() for a in (coordinates, outputs, centers)) or
            not np.equal(coordinates, np.floor(coordinates)).all() or
            (outputs[:, 0] < 0).any() or (outputs[:, 0] > 1).any()):
        raise ValueError("Malformed proposal geometry or fixed model output")
    height, width = scene.tensor.shape[1:]
    if ((coordinates < 0).any() or (coordinates[:, 0] >= width).any() or
            (coordinates[:, 1] >= height).any()):
        raise ValueError("Proposal anchor is outside its source pixels")
    anchor_distance = np.linalg.norm(coordinates[:, None, :]-centers[None, :, :], axis=2).min(axis=1)
    decoded = coordinates + outputs[:, 1:3]*4.
    decoded_distance = np.linalg.norm(decoded[:, None, :]-centers[None, :, :], axis=2).min(axis=1)
    # Do not relabel ambiguous near-marker anchors to make a harder curriculum.
    return np.flatnonzero((outputs[:, 0] >= .25) & (anchor_distance > 8.) & (decoded_distance > 5.))


def selected_rows(scene, coordinates: np.ndarray, indices: np.ndarray) -> tuple[torch.Tensor, ...]:
    if scene.split != "train":
        raise ValueError("Only train pixels may become optimizer rows")
    if indices.ndim != 1 or len(set(indices.tolist())) != len(indices):
        raise ValueError("Selected proposal indices must be unique")
    if len(indices) == 0:
        return (torch.empty(0, 3, 33, 33), torch.empty(0), torch.empty(0, 2), torch.empty(0), torch.empty(0))
    if not np.issubdtype(indices.dtype, np.integer) or (indices < 0).any() or (indices >= len(coordinates)).any():
        raise ValueError("Selected proposal index is invalid")
    selected = coordinates[indices]
    centers = np.asarray(scene.centers,dtype=np.float32).reshape(-1,2)
    height,width = scene.tensor.shape[1:]
    if (not len(centers) or not np.isfinite(selected).all() or not np.isfinite(centers).all() or
            not np.equal(selected,np.floor(selected)).all() or (selected<0).any() or
            (selected[:,0]>=width).any() or (selected[:,1]>=height).any() or
            (np.linalg.norm(selected[:,None,:]-centers[None,:,:],axis=2).min(axis=1)<=8).any()):
        raise ValueError("A mined negative cannot touch a truth anchor or leave the source")
    padded = torch.nn.functional.pad(scene.tensor, (16, 16, 16, 16))
    patches = torch.stack([padded[:, int(y):int(y)+33, int(x):int(x)+33]
                           for x, y in selected])
    count = len(indices)
    values = (patches, torch.zeros(count), torch.zeros(count, 2), torch.zeros(count), torch.ones(count))
    parent.validate_rows(values)
    return values


def prepare_cache(repo: Path, output: Path) -> dict:
    started = time.perf_counter()
    if os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80" or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE":
        raise RuntimeError("Use the verified CPU guard with passive worker waits")
    torch.set_num_threads(int(os.environ["OMP_NUM_THREADS"]))
    torch.set_num_interop_threads(1)
    output.mkdir(parents=True, exist_ok=False)
    budget = WorkBudget(output/"CANCEL")
    source_hashes = {name: sha256_file(repo/name) for name in SOURCE_PATHS}
    for name in SOURCE_PATHS:
        destination = output/"source-snapshot"/name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repo/name, destination)
    model = repo/MODEL_PATH
    if sha256_file(model) != MODEL_SHA256:
        raise ValueError("Fixed mining model changed")
    options = ort.SessionOptions()
    options.intra_op_num_threads = int(os.environ["OMP_NUM_THREADS"])
    options.inter_op_num_threads = 1
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    session = ort.InferenceSession(str(model), sess_options=options, providers=["CPUExecutionProvider"])
    diagnostic = repo/DIAGNOSTIC_PATH
    report = json.loads((diagnostic/"report.json").read_text())
    if (report["model_sha256"] != MODEL_SHA256 or report["optimizer_steps"] != 0 or
            report["training_tensor_inventory_sha256"] != parent.HISTORICAL_INVENTORY or
            sha256_file(diagnostic/"train-scenes.pt") != report["train_scenes_sha256"]):
        raise ValueError("Training diagnostic binding changed")
    with budget.work_block():
        original, _, old_manifest = parent.load_cache(repo/PARENT_PATH, PARENT_MANIFEST_SHA256)
        del original
        base = v27.prepare_training(repository_root=repo)
        if base.training_tensor_inventory_sha256 != parent.HISTORICAL_INVENTORY:
            raise ValueError("Historical training inputs changed")
        packed_family = torch.load(diagnostic/"train-scenes.pt", map_location="cpu", weights_only=True)
        family = [parent.unpack_scene(item, expected_split="train") for item in packed_family]
        if len(family) != len(base.base.family_train):
            raise ValueError("Training family denominator changed")
        for cached, current in zip(family, base.base.family_train, strict=True):
            if (parent.tensor_digest(cached.scene.tensor) != parent.tensor_digest(current.scene.tensor) or
                    tuple(map(tuple,cached.scene.centers)) != tuple(map(tuple,current.scene.centers))):
                raise ValueError("Cached training scene or truth changed")
        component = base.base.component_train
        del base
    parts = [[] for _ in parent.NAMES]
    records = []
    totals = Counter()
    for scope, scenes in (("component", component), ("family", family)):
        for index, bound in enumerate(scenes):
            scene = bound.scene if scope == "family" else bound
            if scope == "family":
                path = diagnostic/f"family-{index:03d}.npz"
                expected = report["records"][index]
                if expected["scene_index"] != index or expected["split"] != "train" or sha256_file(path) != expected["cache_sha256"]:
                    raise ValueError("Frozen training proposal cache changed")
                with np.load(path, allow_pickle=False) as cached:
                    coordinates, values = cached["coordinates"], cached["outputs"]
            else:
                with budget.work_block():
                    batch = proposals.extract_proposals(scene.tensor)
                    coordinates = batch.coordinates.numpy()
                chunks = []
                for start in range(0, len(batch.patches), 128):
                    with budget.work_block():
                        chunks.append(session.run(["candidate_predictions"], {"candidate_patches":batch.patches[start:start+128].numpy()})[0])
                values = np.concatenate(chunks)
                del batch
            with budget.work_block():
                indices = select_negative_indices(scene, coordinates, values)
                rows = selected_rows(scene, coordinates, indices)
                for part, tensor in zip(parts, rows, strict=True):
                    part.append(tensor)
                counts = {"proposals":len(coordinates), "selected":len(indices), "truth":len(scene.centers)}
                totals.update({scope+"_"+key:value for key,value in counts.items()})
                path = output/f"{scope}-{index:03d}-selection.npz"
                np.savez_compressed(path, coordinates=coordinates, outputs=values, selected_indices=indices)
                records.append({"scope":scope,"scene_index":index,"split":scene.split,
                    "scene_tensor_sha256":parent.tensor_digest(scene.tensor),"counts":counts,
                    "cache_file":path.name,"cache_sha256":sha256_file(path)})
    with budget.work_block():
        values = tuple(torch.cat(part) for part in parts)
        counts = parent.validate_rows(values)
        if counts["positive"] != 0 or counts["hard_negative"] != counts["rows"]:
            raise ValueError("Mining must add only authenticated hard negatives")
        torch.save(dict(zip(parent.NAMES, values, strict=True)), output/"train-negative.pt")
        parent.write_json(output/"selection.json", records)
        for name,digest in source_hashes.items():
            if sha256_file(repo/name) != digest:
                raise ValueError("Mining source changed during preparation")
        manifest = {"version":VERSION,"parent_cache_path":PARENT_PATH,
            "parent_manifest_sha256":PARENT_MANIFEST_SHA256,"fixed_model_sha256":MODEL_SHA256,
            "training_diagnostic_sha256":sha256_file(diagnostic/"report.json"),
            "files":{p.name:sha256_file(p) for p in output.iterdir() if p.is_file()},
            "source_sha256":source_hashes,"counts":counts,"totals":dict(totals),
            "original_counts":old_manifest["counts"],"all_original_training_rows_retained":True,
            "all_development_bytes_and_truth_unchanged":True,"optimizer_steps":0,
            "private_reads":0,"sealed_reads":0,"seconds":time.perf_counter()-started,"cpu":budget.report()}
        parent.write_json(output/"manifest.json",manifest)
    return manifest


def load_cache(repo: Path, directory: Path, expected_sha256: str):
    if sha256_file(directory/"manifest.json") != expected_sha256:
        raise ValueError("Negative cache manifest changed")
    manifest = json.loads((directory/"manifest.json").read_text())
    if (manifest["version"] != VERSION or manifest["parent_cache_path"] != PARENT_PATH or
            manifest["parent_manifest_sha256"] != PARENT_MANIFEST_SHA256 or manifest["fixed_model_sha256"] != MODEL_SHA256):
        raise ValueError("Negative cache lineage changed")
    for name,digest in manifest["source_sha256"].items():
        if name not in SOURCE_PATHS or sha256_file(repo/name) != digest or sha256_file(directory/"source-snapshot"/name) != digest:
            raise ValueError("Negative preparation source changed")
    if set(manifest["source_sha256"]) != set(SOURCE_PATHS):
        raise ValueError("Negative preparation source inventory changed")
    for name,digest in manifest["files"].items():
        if Path(name).name != name or sha256_file(directory/name) != digest:
            raise ValueError("Negative cache file changed")
    scopes,dev,original = parent.load_cache(repo/PARENT_PATH,PARENT_MANIFEST_SHA256)
    for name,digest in original["source_sha256"].items():
        if sha256_file(repo/name) != digest or sha256_file(repo/PARENT_PATH/"source-snapshot"/name) != digest:
            raise ValueError("Original coverage preparation source changed")
    payload=torch.load(directory/"train-negative.pt",map_location="cpu",weights_only=True)
    values=tuple(payload[name] for name in parent.NAMES)
    if parent.validate_rows(values) != manifest["counts"] or (values[1] != 0).any() or (values[4] != 1).any():
        raise ValueError("Hard negative labels or population changed")
    scopes["mined_negative"]=values
    return scopes,dev,manifest


if __name__ == "__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    result=prepare_cache(Path.cwd(),args.output)
    print(json.dumps({key:result[key] for key in ("counts","totals","seconds")}))
