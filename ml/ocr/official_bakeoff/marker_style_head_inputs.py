# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Capture and consume the explicit nine-source, train-only marker-style fixture.

prepare authenticates saved source truth and actual runtime crop evidence, then
writes an annotation-free request for --capture-marker-style-official-head-tensors.
targets authenticates that request and capture and writes inverse-unclip DB maps.
Neither command runs inference, an optimizer, development data, or sealed data.
Historical six-source and 20-source/709-truth profiles are not composed or changed.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass, replace
from hashlib import sha256
import json
from pathlib import Path
from typing import Any

import numpy as np

from ml.ocr import production_tiled_inputs as tiled
from ml.synthetic.dataset import _scene_split
from ml.synthetic.io import canonical_json_bytes
from . import legend_coverage_head_inputs as shared
from . import production_head_inputs as head
from . import text_extent_head_inputs as base
from . import train_marker_style_coverage as fixture
from .db_inverse_targets import build_inverse_targets

ROOT = Path(__file__).resolve().parents[3]
REQUEST_SCHEMA = "graphreader.marker-style-official-head-tensor-capture-request.v1"
REPORT_SCHEMA = "graphreader.marker-style-official-head-tensor-capture-report.v1"
SCOPE = "project-owned-synthetic-train-only-marker-style-model-free"
SOURCE_COUNT = 9
# Exact seven-path relocation; model/native/manifest/license hashes and settings unchanged.
EXPECTED_CANDIDATE_SHA256 = "5b1bb9b10915a1b590135cffd6d5bc89cc311c3d2c48e9dd374c3196ebf4943d"


class MarkerStyleHeadInputError(head.ProductionHeadInputError):
    """The saved fixture, runtime geometry, or capture does not authenticate."""


@dataclass(frozen=True)
class CapturePreparation:
    train: tiled.ProductionTiledSplit
    panels: tuple[head.CapturePanelInput, ...]
    request: dict[str, Any]


def _digest(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _load_fixture(path: Path, digest: str, root: Path) -> tuple[dict, dict, dict]:
    path, document = base._read_json(path, digest, root, "marker-style fixture")
    expected = {"schema": "graphreader.ocr-marker-style-train-fixture.v1",
                "scope": "owned-synthetic-train-only", "optimizer_steps": 0,
                "private_reads": 0, "sealed_reads": 0, "model_inference_runs": 0,
                "production_approved": False,
                "style_schedule": [list(style) for style in fixture.STYLE_SCHEDULE]}
    if any(document.get(key) != value for key, value in expected.items()):
        raise MarkerStyleHeadInputError("marker-style fixture scope or schedule changed")
    sources = base._array(document.get("sources"), "fixture sources")
    if len(sources) != SOURCE_COUNT:
        raise MarkerStyleHeadInputError("marker-style fixture requires nine sources")
    manifest_path = path.parent / "input-manifest.json"
    truth_path = path.parent / "train-text-truth.json"
    _, manifest = base._read_json(manifest_path, _digest(manifest_path), root, "fixture manifest")
    _, truth = base._read_json(truth_path, _digest(truth_path), root, "fixture truth")
    images = base._array(manifest.get("images"), "fixture images")
    if (manifest.get("schema") != "graphreader.synthetic-runtime-raster-inputs.v1" or
        manifest.get("source") != "project-owned-synthetic-five-axis-family-v1" or
        manifest.get("preset") != "ocr-marker-style-train-v1" or
        manifest.get("split") != "train" or manifest.get("contains_truth") is not False or
        manifest.get("contains_precomputed_masks") is not False or len(images) != SOURCE_COUNT):
        raise MarkerStyleHeadInputError("marker-style manifest scope or count changed")
    rebuilt_truths = []
    rebuilt_images = []
    for ordinal, source in enumerate(sources):
        if source.get("split") != "train":
            raise MarkerStyleHeadInputError("marker-style source is not train")
        saved = {}
        for kind in ("scene", "annotation", "image"):
            ref = base._object(source.get(kind), f"fixture {kind}")
            source_path = head._inside(root, root / base._string(ref, "path"))
            payload = base._read_exact(source_path, base._sha(ref.get("sha256"), kind), None, kind)
            saved[kind] = payload if kind == "image" else json.loads(payload)
        scene, annotation = saved["scene"], saved["annotation"]
        styles = [(series["shape"], series["fill"]) for panel in scene["panels"]
                  for series in panel["series"]]
        if (_scene_split(scene) != "train" or scene["seed"] != source.get("seed") or
            styles != list(fixture.STYLE_SCHEDULE[ordinal * 3:ordinal * 3 + 3])):
            raise MarkerStyleHeadInputError("marker-style scene binding changed")
        width, height = annotation["canvas"]["width"], annotation["canvas"]["height"]
        head._decode_production_pixels(saved["image"], width, height)
        image_sha = source["image"]["sha256"]
        image_path = head._inside(root, root / source["image"]["path"])
        if image_path.parent != path.parent:
            raise MarkerStyleHeadInputError("marker-style image escaped its fixture directory")
        family = "|".join(f"{axis}={scene['families'][axis]['key']}" for axis in shared.FAMILY_AXES)
        rebuilt_images.append({"image": image_path.name, "image_sha256": image_sha,
                               "width": width, "height": height, "split": "train",
                               "family": family, "seed": scene["seed"]})
        for text in fixture.source_truths(annotation):
            x, y, w, h = text["rendered_pixel_box"]
            rebuilt_truths.append({
                "truth_id": sha256((image_sha + "\n" + text["text_id"]).encode()).hexdigest(),
                "source_sha256": image_sha, "source_id": image_sha, "text_id": text["text_id"],
                "region_id": text["region_id"], "source_box_ltrb": [x, y, x + w, y + h],
                "text": text["text"], "role": text["role"], "coordinate_space": "original_pixels"})
    fixture.validate_source_truths(truth, images)
    if images != rebuilt_images or truth["truths"] != rebuilt_truths:
        raise MarkerStyleHeadInputError("marker-style manifest/truth differs from bound source annotations")
    return dict(document), dict(manifest), dict(truth)


def _train_split(truth: dict, sources: dict, domains: list) -> tiled.ProductionTiledSplit:
    by_source = {domain.runtime_input.source_sha256: domain for domain in domains}
    projections = {domain.runtime_input.panel_id: [] for domain in domains}
    truths = []
    for raw in truth["truths"]:
        item, source_sha, truth_id, text_id, box = base._read_train_truth(raw, sources)
        domain = by_source[source_sha]
        projection = tiled._project_truth(truth_id, text_id, box, domain)
        if projection is None or projection.status != "full":
            raise MarkerStyleHeadInputError("marker-style truth is missing or clipped in actual runtime crop")
        projections[projection.panel_id].append(projection)
        runtime = domain.runtime_input
        truths.append(tiled.SourceTextTruth(truth_id, "train", runtime.dataset_seed,
            runtime.family, runtime.scene_seed, source_sha, text_id, item["role"], box,
            (projection,), tiled._projection_status(box, (projection,))))
    panels = tuple(base._tiled_panel(domain, tuple(projections[domain.runtime_input.panel_id]))
                   for domain in sorted(domains, key=lambda value: value.runtime_input.panel_id))
    return base._split("train", truths, panels, SOURCE_COUNT)


def _detector_manifest_descriptor(candidate: dict, path: Path, digest: str) -> dict:
    """Permit only the exact absolute metadata path authenticated by the pinned candidate."""
    expected = base._object(candidate.get("detector"), "candidate detector")
    expected_path = Path(base._string(expected, "manifest_path"))
    if not expected_path.is_absolute() or path.resolve() != expected_path.resolve():
        raise MarkerStyleHeadInputError("detector manifest path differs from pinned candidate")
    if digest != base._sha(expected.get("manifest_sha256"), "candidate detector manifest"):
        raise MarkerStyleHeadInputError("detector manifest checksum differs from pinned candidate")
    base._read_exact(path, digest, None, "pinned detector manifest")
    return {"manifest_path": str(path.resolve()), "manifest_sha256": digest}


def prepare_capture_request(
    fixture_path: Path, expected_fixture_sha256: str,
    runtime_report_path: Path, expected_runtime_report_sha256: str,
    candidate_path: Path, expected_candidate_sha256: str, *,
    capture_binary_root: Path, repository_root: Path = ROOT,
    capture_source_path: Path | None = None,
) -> CapturePreparation:
    """Bind nine exact train sources to recorded, authenticated application crops."""
    root = repository_root.resolve()
    fixture_path = head._inside(root, fixture_path.resolve())
    _, manifest, truth = _load_fixture(fixture_path, expected_fixture_sha256, root)
    manifest_path = fixture_path.parent / "input-manifest.json"
    manifest_sha = _digest(manifest_path)
    shared._require_pinned_sha(expected_candidate_sha256, EXPECTED_CANDIDATE_SHA256,
                               "marker-style candidate")
    candidate_path, candidate = base._read_json(candidate_path, expected_candidate_sha256, root, "candidate")
    detector, detector_manifest_path, detector_manifest_sha = base._authenticate_candidate(candidate, root)
    manifest_descriptor = _detector_manifest_descriptor(candidate, detector_manifest_path, detector_manifest_sha)
    report_path, report = base._read_json(runtime_report_path, expected_runtime_report_sha256, root, "runtime report")
    sources = {row["image_sha256"]: row for row in manifest["images"]}
    panels, domains = shared._load_runtime_report(sources, manifest_path, manifest_sha,
        report, report_path, expected_runtime_report_sha256, candidate, expected_candidate_sha256,
        manifest["seed"], root, expected_sources=SOURCE_COUNT, expected_panels=SOURCE_COUNT)
    train = _train_split(truth, sources, domains)
    source_path = head._inside(root, capture_source_path or root / head.CAPTURE_SOURCE_PATH)
    binary_root = head._inside(root, capture_binary_root)
    descriptor = lambda path: {"path": head._repository_path(path, root), "sha256": _digest(path)}
    request = {
        "schema": REQUEST_SCHEMA, "scope": SCOPE, "synthetic_only": True,
        "private_data": False, "sealed_data": False, "truth_included": False,
        "model_inference": False, "training_input_ready": False, "production_approved": False,
        "capture_source": descriptor(source_path),
        "assemblies": [{"name": name, **descriptor(binary_root / f"{name}.dll")}
                       for name in head.CAPTURE_ASSEMBLY_NAMES],
        "binding": descriptor(fixture_path), "candidate": descriptor(candidate_path),
        "detector": {"model_path": detector["model_path"], "model_id": detector["model_id"],
                     "model_version": detector["model_version"], "model_sha256": detector["model_sha256"],
                     **manifest_descriptor},
        "native": {"path": candidate["native_path"], "sha256": candidate["native_sha256"],
                   "scope": candidate["native_scope"]}, "license_inputs": candidate["license_inputs"],
        "maximum_side_length": head.MAXIMUM_SIDE_LENGTH, "dimension_multiple": head.DIMENSION_MULTIPLE,
        "detector_configuration_fingerprint": head.DETECTOR_CONFIGURATION_FINGERPRINT,
        "reports": [{"split": "train", "manifest_path": head._repository_path(manifest_path, root),
                     "manifest_sha256": manifest_sha, "report_path": head._repository_path(report_path, root),
                     "report_sha256": expected_runtime_report_sha256.lower()}],
        "panels": [shared._capture_panel_record(panel, root) for panel in panels]}
    return CapturePreparation(train, tuple(panels), request)


def load_model_grid_inputs(
    request_path: Path, expected_request_sha256: str,
    report_path: Path, expected_report_sha256: str, *, repository_root: Path = ROOT,
) -> head.ProductionHeadSplit:
    """Read captured float tensors and construct inverse DB supervision on that grid."""
    root = repository_root.resolve()
    request_path, request = base._read_json(request_path, expected_request_sha256, root, "capture request")
    reports = base._array(request.get("reports"), "runtime reports")
    assemblies = base._array(request.get("assemblies"), "capture assemblies")
    if len(reports) != 1 or not assemblies:
        raise MarkerStyleHeadInputError("marker-style request report/assembly inventory changed")
    binary_roots = {head._inside(root, root / item["path"]).parent for item in assemblies}
    if len(binary_roots) != 1:
        raise MarkerStyleHeadInputError("capture assemblies do not share an immutable snapshot")
    preparation = prepare_capture_request(root / request["binding"]["path"], request["binding"]["sha256"],
        root / reports[0]["report_path"], reports[0]["report_sha256"],
        root / request["candidate"]["path"], request["candidate"]["sha256"],
        capture_binary_root=binary_roots.pop(), repository_root=root,
        capture_source_path=root / request["capture_source"]["path"])
    if request != preparation.request:
        raise MarkerStyleHeadInputError("saved capture request differs from authenticated inputs")
    report_path, report = base._read_json(report_path, expected_report_sha256, root, "tensor capture")
    expected = {"schema": REPORT_SCHEMA, "scope": SCOPE, "synthetic_only": True,
        "private_data": False, "sealed_data": False, "truth_used_by_capture": False,
        "model_inference": False, "training_input_ready": False, "production_approved": False,
        "request": {"path": head._repository_path(request_path, root), "sha256": expected_request_sha256.lower()},
        "binding_sha256": request["binding"]["sha256"], "panel_count": SOURCE_COUNT, "failed_panel_count": 0,
        "candidate_sha256": request["candidate"]["sha256"],
        "detector_model_sha256": request["detector"]["model_sha256"],
        "detector_manifest_sha256": request["detector"]["manifest_sha256"],
        "native_sha256": request["native"]["sha256"], "capture_source_sha256": request["capture_source"]["sha256"],
        "assemblies": request["assemblies"], "maximum_side_length": head.MAXIMUM_SIDE_LENGTH,
        "dimension_multiple": head.DIMENSION_MULTIPLE,
        "detector_configuration_fingerprint": head.DETECTOR_CONFIGURATION_FINGERPRINT}
    if any(report.get(key) != value for key, value in expected.items()):
        raise MarkerStyleHeadInputError("marker-style capture header changed")
    records = base._array(report.get("panels"), "captured panels")
    by_id = {row["panel_id"]: row for row in records}
    requested = {panel.panel_id: panel for panel in preparation.panels}
    if len(records) != SOURCE_COUNT or len(by_id) != SOURCE_COUNT or set(by_id) != set(requested):
        raise MarkerStyleHeadInputError("marker-style capture panel inventory changed")
    if any(row.get("detector_configuration_fingerprint") != head.DETECTOR_CONFIGURATION_FINGERPRINT
           for row in records):
        raise MarkerStyleHeadInputError("captured panel detector fingerprint changed")
    loaded = head._load_split(preparation.train, by_id, requested, report_path, root)
    panels = []
    for panel in loaded.panels:
        height, width = panel.tensor_shape[2:]
        boxes = [(p.panel_box[0] * width / panel.width, p.panel_box[1] * height / panel.height,
                  p.panel_box[2] * width / panel.width, p.panel_box[3] * height / panel.height)
                 for p in panel.projections]
        target = build_inverse_targets(width, height, boxes).target
        mask = np.ones_like(target)
        mask.setflags(write=False)
        panels.append(replace(panel, shrink_target=target, supervision_mask=mask, degenerate_regions=()))
    return replace(loaded, panels=tuple(panels), degenerate_supervised_region_count=0,
                   degenerate_source_truth_count=0)


def write_model_grid_inputs(output: Path, request_path: Path, request_sha: str,
                           report_path: Path, report_sha: str, *, repository_root: Path = ROOT) -> dict:
    root = repository_root.resolve()
    output = head._inside(root / "artifacts", output.resolve())
    if output.exists():
        raise MarkerStyleHeadInputError("use a new model-grid input directory")
    train = load_model_grid_inputs(request_path, request_sha, report_path, report_sha, repository_root=root)
    _, report = base._read_json(report_path, report_sha, root, "tensor capture")
    tensors = {row["panel_id"]: row["tensor"] for row in report["panels"]}
    output.mkdir(parents=True)
    records = []
    for panel in train.panels:
        refs = {}
        for name, values in (("target", panel.shrink_target), ("mask", panel.supervision_mask)):
            path = output / f"{panel.panel_id}.{name}.f32"
            payload = values.astype("<f4").tobytes(order="C")
            with path.open("xb") as stream:
                stream.write(payload)
            refs[name] = {"path": head._repository_path(path, root), "sha256": sha256(payload).hexdigest(),
                          "byte_count": len(payload), "shape": list(values.shape), "dtype": "float32-le"}
        tensor = tensors[panel.panel_id]
        sx, sy = panel.tensor_shape[3] / panel.width, panel.tensor_shape[2] / panel.height
        records.append({"split": "train", "source_sha256": panel.source_sha256,
            "panel_id": panel.panel_id, "panel_sha256": panel.panel_sha256,
            "crop": list(panel.crop), "panel_width": panel.width, "panel_height": panel.height,
            "panel_to_tensor_matrix": [sx, 0, 0, 0, sy, 0, 0, 0, 1],
            "source_to_tensor_matrix": [sx, 0, -panel.crop[0] * sx, 0, sy, -panel.crop[1] * sy, 0, 0, 1],
            "tensor": {**tensor, "path": head._repository_path(report_path.parent / tensor["file"], root)},
            "projected_truth_count": len(panel.projections), **refs})
    manifest = {"schema": "graphreader.marker-style-model-grid-inputs.v1", "split": "train",
        "synthetic_only": True, "private_data": False, "sealed_data": False, "optimizer_steps": 0,
        "production_approved": False, "source_count": train.source_count, "panel_count": train.panel_count,
        "truth_count": train.full_source_truth_count, "target_kind": "db-inverse-unclip-v1",
        "request": {"path": head._repository_path(request_path.resolve(), root), "sha256": request_sha},
        "capture": {"path": head._repository_path(report_path.resolve(), root), "sha256": report_sha},
        "panels": records}
    with (output / "manifest.json").open("xb") as stream:
        stream.write(canonical_json_bytes(manifest))
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare = commands.add_parser("prepare")
    for name in ("fixture", "runtime-report", "candidate"):
        prepare.add_argument(f"--{name}", type=Path, required=True)
        prepare.add_argument(f"--{name}-sha256", required=True)
    prepare.add_argument("--capture-binary-root", type=Path, required=True)
    prepare.add_argument("--output", type=Path, required=True)
    targets = commands.add_parser("targets")
    for name in ("request", "report"):
        targets.add_argument(f"--{name}", type=Path, required=True)
        targets.add_argument(f"--{name}-sha256", required=True)
    targets.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "prepare":
        preparation = prepare_capture_request(args.fixture, args.fixture_sha256,
            args.runtime_report, args.runtime_report_sha256, args.candidate, args.candidate_sha256,
            capture_binary_root=args.capture_binary_root)
        output = head._inside(ROOT / "artifacts", args.output.resolve())
        output.parent.mkdir(parents=True, exist_ok=True)
        payload = canonical_json_bytes(preparation.request)
        with output.open("xb") as stream:
            stream.write(payload)
        print(json.dumps({"request": str(output), "sha256": sha256(payload).hexdigest(), "panels": SOURCE_COUNT}))
    else:
        manifest = write_model_grid_inputs(args.output, args.request, args.request_sha256,
                                          args.report, args.report_sha256)
        print(json.dumps({"panels": manifest["panel_count"], "optimizer_steps": 0}))


if __name__ == "__main__":
    main()
