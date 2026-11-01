# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Reconstruct V37 identity-feature row provenance from pinned train metadata.

This is a metadata-only audit. It does not load tensor/feature payloads, run
models, or open dev/private/sealed data. Row identity is reconstructed from the
frozen source ordering rules and train-only per-scene inventories, not stored
inside the V37 feature arrays themselves.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re

import numpy as np


ROOT = Path(__file__).resolve().parents[4]
RESUME = Path("artifacts/goal22-worktrees/resume-20260908")
RUNS = RESUME / "artifacts/goal22-runs"
V35_REPORT = RUNS / "frozen-spatial-features-20261002-v1/report.json"
V37_REPORT = RUNS / "reflected-spatial-features-20261002-v1/report.json"
V26_COVERAGE = RUNS / "marker-v26-target-preflight/negative-coverage-v1/frozen-v25-train-proposal-metadata.npz"
V27_SELECTION = RUNS / "marker-component-selection-coverage-preflight/v3/proposed-selection-metadata.npz"
SHAPE_MANIFEST = RUNS / "center-shape-coverage-cache-v3/manifest.json"
SHAPE_ROWS = RUNS / "center-shape-coverage-cache-v3/coverage.json"
CROWDED_MANIFEST = RUNS / "center-crowded-coverage-cache-v1/manifest.json"
CROWDED_ROWS = RUNS / "center-crowded-coverage-cache-v1/crowded.json"
MINED_MANIFEST = RUNS / "center-crowded-negative-cache-20260930-v1/manifest.json"
MINED_SELECTION = RUNS / "center-crowded-negative-cache-20260930-v1/selection.json"

# SHA pins are independent of the artifact's own metadata. Large tensor/feature
# payloads are intentionally not opened by this audit.
INPUT_SHA256 = {
    "v35_report": "3a98504c7b31f16af8f4b127ab438cba3fe7918d51c9417021221f96bb6ad7cc",
    "v37_report": "284ebfa305b7b827c59383711122b1668398460ddde2489007252335ee55f500",
    "v26_coverage": "5b8afc209e1c2a6f849ef044056b46903c79692d7264a724651a3a158369576b",
    "v27_selection": "d31a25f48170a375fae22f196344d1c8fc85d2297ddc3394c8303d08d34a246b",
    "shape_manifest": "d61e43d62891a5266dfd68dfd8286fdb0d28d685d2146763a9f49dfb24471700",
    "shape_rows": "902e1e9236e5b8b2bdeac66e55e1a4e643b91e976a89e74f78bc5d76397ca8ff",
    "crowded_manifest": "1458a7f285f382b968b32e6b4ceb3e431544483bfcb2b0f5de3661278f3d8f03",
    "crowded_rows": "00444918baf6af502dc3cd03f89c9d7d76775a7ed6a21c1364d4fe4934e7feb7",
    "mined_manifest": "d1944d243cefb21d0f05d8cb2f940208db78fba248d329b75d9285e09f1b7a70",
    "mined_selection": "1fac75028eedc671b13dd45e72348069c34601a50ca04db476aec43cfca2aef6",
}
SOURCE_SHA256 = {
    "ml/markers/center/confidence_reflection_v37/cache.py": "009947a9afad2f12178c1add41283ff6b1d7bd2840b0dda54eafe96dc9c61dde",
    "ml/markers/center/confidence_spatial_v35/cache.py": "2b02a7daa6040aae23e19deff405c8e63ac1eef85e107916106127e4394d3171",
    "ml/markers/center/component_diversity_v27/train_p1.py": "0ce0b00232b49425cdd89dd2b4c08293d6d7314057fed90e7378c3104abfab0c",
    "ml/markers/center/localization_confidence_v26/train_p1.py": "27cff76d1ddb16b4d97f1a65420eef76639adc07b0f76b9de585994c650463e3",
    "ml/markers/center/shape_coverage_cache.py": "25156ffe89ed9be64522baa951e526a13838ea9e1eca465349e81c0850148fda",
    "ml/markers/center/crowded_coverage_cache.py": "5db027428ac9b2d403702b01727a7049d29dbaf3b64f884e0917d7d2b6ae3fec",
    "ml/markers/center/crowded_negative_coverage.py": "1e180927c9c986626273cd2009e3cd7241cad6485211ccd25899f7504a718f34",
    "ml/markers/center/real_range_generator_v1/generator.py": "2244094e8d8e3a8cacb83e11c5e7189ded46b25b1ce9ecdad1bd80e5dec78b41",
    "ml/markers/center/plot_domain_v25/protocol.py": "114cd44e1c8ccceb984bcfb9a5c3db88b13496ab57be71f957d475a56f16d8b0",
    "ml/markers/center/shape_coverage_data.py": "d0efc0371b88d357646d5e06cc523aa49a5835526e8b066e376a8bf79c428284",
    "ml/markers/center/crowded_coverage_data.py": "6b93111bec88bc4eeeee51a871ccecf9a025f4e4a5bfe16000ffd0d4a22b4c29",
    "ml/markers/center/train_negative_coverage.py": "c30e14062bb2eb5bb1c136181504eaf39008f6b8d0f30093621a57c7a5b542e3",
}

ROWS = {
    "component": (35838, 3258, 32580),
    "family": (9053, 823, 8230),
    "coverage": (42747, 3863, 38884),
    "crowded": (182614, 16732, 165882),
    "crowded_mined_negative": (6366, 0, 6366),
}
FAMILY_ORDER_BINDING_SHA256 = "bcf821a2aaadc5ee8e18bd97dbe0488d120d2ac974a249c00d76b6cfccf0d98a"
SHA_RE = re.compile(r"^[0-9a-f]{64}$")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _safe_regular(root: Path, relative: Path) -> Path:
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("input path is not a fixed relative path")
    path = root / relative
    current = root
    for part in relative.parts:
        current = current / part
        if current.is_symlink():
            raise ValueError(f"reparse/symlink input rejected: {relative}")
        stat = current.stat()
        if getattr(stat, "st_file_attributes", 0) & 0x400:
            raise ValueError(f"reparse input rejected: {relative}")
    if not path.is_file():
        raise ValueError(f"required regular input missing: {relative}")
    return path


def _bound(root: Path, relative: Path, expected: str) -> Path:
    path = _safe_regular(root, relative)
    if sha256_file(path) != expected:
        raise ValueError(f"fixed input hash mismatch: {relative}")
    return path


def _source_closure(root: Path) -> dict[str, str]:
    found = {name: sha256_file(_bound(root, Path(name), digest))
             for name, digest in SOURCE_SHA256.items()}
    return found


def _check_input_hashes(root: Path) -> dict[str, str]:
    paths = {
        "v35_report": V35_REPORT, "v37_report": V37_REPORT,
        "v26_coverage": V26_COVERAGE, "v27_selection": V27_SELECTION,
        "shape_manifest": SHAPE_MANIFEST, "shape_rows": SHAPE_ROWS,
        "crowded_manifest": CROWDED_MANIFEST, "crowded_rows": CROWDED_ROWS,
        "mined_manifest": MINED_MANIFEST, "mined_selection": MINED_SELECTION,
    }
    return {name: sha256_file(_bound(root, path, INPUT_SHA256[name]))
            for name, path in paths.items()}


def _json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def _map_selected_metadata(scope: str, metadata, selected) -> list[dict]:
    prefix = scope + "_"
    scene = metadata[prefix + "scene_index"]
    proposal = metadata[prefix + "proposal_index"]
    distance = metadata[prefix + "nearest_truth_distance_px"]
    coordinates = metadata[prefix + "coordinates"]
    if (scene.ndim != 1 or proposal.shape != scene.shape or distance.shape != scene.shape
            or selected.shape != scene.shape or selected.dtype != np.bool_
            or coordinates.shape != (len(scene), 2) or scene.dtype != np.int32
            or proposal.dtype != np.int32 or coordinates.dtype != np.float32):
        raise ValueError(f"{scope} proposal metadata shape or dtype changed")
    if not np.isfinite(distance).all():
        raise ValueError(f"{scope} nearest-distance metadata is non-finite")
    output, row = [], 0
    scene_ids = sorted(int(x) for x in np.unique(scene))
    expected_scenes = 167 if scope == "component" else 28
    if scene_ids != list(range(expected_scenes)):
        raise ValueError(f"{scope} train scene indices are not complete and ordered")
    positive_count = 0
    for index in scene_ids:
        positions = np.flatnonzero(scene == index)
        local_proposal = proposal[positions]
        if not np.array_equal(local_proposal, np.arange(len(positions), dtype=np.int32)):
            raise ValueError(f"{scope} proposals are not in ascending scene-local order")
        keep = selected[positions]
        indices = local_proposal[keep]
        local_distances = distance[positions][keep]
        positive_count += int((local_distances <= 3.0).sum())
        proposal_metadata_sha = hashlib.sha256(
            b"row-identity-scene-v1\0" + scope.encode() + b"\0" + index.to_bytes(4, "little")
            + proposal[positions].tobytes() + coordinates[positions].tobytes()
            + distance[positions].tobytes() + selected[positions].tobytes()
        ).hexdigest()
        if scope == "component":
            seed = 4100 + index
            identity = f"component/train/seed-{seed}"
        else:
            seed = None
            identity = f"family/train/panel-ordinal-{index:03d}"
        output.append({
            "scene_index": index, "scene_id": identity, "component_seed": seed,
            "proposal_metadata_sha256": proposal_metadata_sha,
            "row_start": row, "row_count": int(len(indices)),
            "proposal_indices": indices.astype(int).tolist(),
        })
        row += len(indices)
    expected_rows, expected_positive, _ = ROWS[scope]
    if row != expected_rows or positive_count != expected_positive:
        raise ValueError(f"{scope} selected row count or label count differs")
    return output


def _records_to_ranges(scope: str, records: list[dict], *, coordinate_ids: bool,
                       expected: tuple[int, int, int] | None = None,
                       expected_scenes: int | None = None) -> list[dict]:
    result, row = [], 0
    seen = set()
    for record in records:
        if record.get("split") != "train":
            raise ValueError(f"{scope} sidecar contains a non-train record")
        sample_id = record.get("sample_id")
        if not isinstance(sample_id, str) or not SHA_RE.fullmatch(sample_id) or sample_id in seen:
            raise ValueError(f"{scope} sample ID is missing, malformed, or duplicated")
        seen.add(sample_id)
        for field in ("original_raster_sha256", "tensor_sha256", "patches_sha256", "labels_sha256"):
            if not isinstance(record.get(field), str) or not SHA_RE.fullmatch(record[field]):
                raise ValueError(f"{scope} source record lacks {field}")
        coordinates = record.get("selected_proposal_coordinates")
        if not isinstance(coordinates, list) or any(
            not isinstance(point, list) or len(point) != 2 or
            any(type(value) not in (int, float) for value in point)
            for point in coordinates
        ):
            raise ValueError(f"{scope} selected coordinate inventory is malformed")
        positive, negative = record.get("positive_rows"), record.get("negative_rows")
        if type(positive) is not int or type(negative) is not int or len(coordinates) != positive + negative:
            raise ValueError(f"{scope} row-count/coordinate inventory differs")
        result.append({
            "scene_id": f"{scope}/train/sample-{sample_id}",
            "sample_id": sample_id, "source_raster_sha256": record["original_raster_sha256"],
            "source_tensor_sha256": record["tensor_sha256"],
            "patches_sha256": record["patches_sha256"], "labels_sha256": record["labels_sha256"],
            "row_start": row, "row_count": len(coordinates),
            "candidate_coordinates": coordinates if coordinate_ids else None,
        })
        row += len(coordinates)
    expected_rows, expected_positive, expected_negative = expected or ROWS[scope]
    if row != expected_rows or sum(r["positive_rows"] for r in records) != expected_positive or sum(
        r["negative_rows"] for r in records
    ) != expected_negative:
        raise ValueError(f"{scope} train row or label totals differ")
    expected_scenes = expected_scenes or {"coverage": 2160, "crowded": 3456}[scope]
    if len(records) != expected_scenes:
        raise ValueError(f"{scope} train scene count differs")
    return result


def _map_mined_negatives(root: Path, records: list[dict]) -> list[dict]:
    expected_order = [("component", i) for i in range(167)] + [("family", i) for i in range(28)]
    actual_order = [(item.get("scope"), item.get("scene_index")) for item in records]
    if actual_order != expected_order or any(item.get("split") != "train" for item in records):
        raise ValueError("mined-negative scenes are not the exact ordered train population")
    result, row = [], 0
    for record in records:
        scope, index = record["scope"], record["scene_index"]
        if record.get("counts", {}).get("truth") < 0:
            raise ValueError("invalid train truth count")
        if record.get("counts", {}).get("selected") <= 0:
            raise ValueError("empty mined-negative selection is not traceable")
        relative = MINED_SELECTION.parent / record["cache_file"]
        path = _safe_regular(root, relative)
        digest = sha256_file(path)
        if digest != record.get("cache_sha256"):
            raise ValueError("per-scene mined selection hash changed")
        with np.load(path, allow_pickle=False) as saved:
            if set(saved.files) != {"coordinates", "outputs", "selected_indices"}:
                raise ValueError("mined selection archive schema changed")
            coordinates, outputs, indices = (saved[name] for name in ("coordinates", "outputs", "selected_indices"))
        count = int(record["counts"]["selected"])
        if (coordinates.ndim != 2 or coordinates.shape[1] != 2 or
                outputs.ndim != 2 or outputs.shape[0] != len(coordinates) or
                indices.shape != (count,) or indices.dtype != np.int64 or
                np.any(indices < 0) or np.any(indices >= len(coordinates)) or
                not np.array_equal(indices, np.unique(indices))):
            raise ValueError("mined selection indices are invalid")
        scene_sha = record.get("scene_tensor_sha256")
        if not isinstance(scene_sha, str) or not SHA_RE.fullmatch(scene_sha):
            raise ValueError("mined source scene hash is malformed")
        if scope == "component":
            source_id = f"component/train/seed-{4100 + index}/{scene_sha}"
        else:
            source_id = f"family/train/panel-ordinal-{index:03d}/{scene_sha}"
        result.append({
            "scope": scope, "scene_id": source_id, "scene_index": index,
            "row_start": row, "row_count": count,
            "proposal_indices": indices.astype(int).tolist(),
            "scene_tensor_sha256": scene_sha, "selection_sha256": digest,
        })
        row += count
    if row != ROWS["crowded_mined_negative"][0]:
        raise ValueError("mined-negative row count differs")
    return result


def build_report(root: Path = ROOT) -> dict:
    root = root.resolve()
    input_hashes = _check_input_hashes(root)
    source_hashes = _source_closure(root)
    # Reports are hash-bound only. Their development summaries are deliberately
    # not parsed, and no dev file or payload is opened.
    v26_path = _bound(root, V26_COVERAGE, INPUT_SHA256["v26_coverage"])
    v27_path = _bound(root, V27_SELECTION, INPUT_SHA256["v27_selection"])
    with np.load(v26_path, allow_pickle=False) as metadata, np.load(v27_path, allow_pickle=False) as selection:
        component = _map_selected_metadata("component", metadata, selection["component_proposed_selected"])
        family = _map_selected_metadata("family", metadata, selection["family_proposed_selected"])

    shape_records = _json(_safe_regular(root, SHAPE_ROWS))
    crowded_records = _json(_safe_regular(root, CROWDED_ROWS))
    mined_records = _json(_safe_regular(root, MINED_SELECTION))
    shape = _records_to_ranges("coverage", shape_records, coordinate_ids=True)
    crowded = _records_to_ranges("crowded", crowded_records, coordinate_ids=True)
    mined = _map_mined_negatives(root, mined_records)
    mined_by_scene = {(item["scope"], item["scene_index"]): item for item in mined}
    for scope, ranges in (("component", component), ("family", family)):
        for item in ranges:
            source = mined_by_scene[(scope, item["scene_index"])]
            item["scene_tensor_sha256"] = source["scene_tensor_sha256"]
            suffix = (f"seed-{4100 + item['scene_index']}" if scope == "component"
                      else f"panel-ordinal-{item['scene_index']:03d}")
            item["scene_id"] = f"{scope}/train/{suffix}/{source['scene_tensor_sha256']}"
    groups = {
        "component": component, "family": family, "coverage": shape,
        "crowded": crowded, "crowded_mined_negative": mined,
    }
    rows = {name: sum(item["row_count"] for item in items) for name, items in groups.items()}
    if rows != {name: value[0] for name, value in ROWS.items()}:
        raise ValueError("reconstructed group row counts do not match frozen identity rows")
    return {
        "schema": "graphreader.marker-center-v37-train-row-identity-audit.v1",
        "status": "train_identity_reconstructed_from_metadata",
        "scope": {"train": True, "dev": False, "private": False, "sealed": False,
                  "optimizer_steps": 0, "feature_payloads_loaded": False,
                  "development_payloads_opened": False},
        "claim": "V37 feature rows contain no scene IDs. Their source identity is reconstructed by the pinned V35/V37 row-preserving loaders and train-only per-scope ordering metadata. The report identifies per-scene contiguous row ranges and each row's source-local candidate index or coordinate.",
        "limitations": [
            "This verifies metadata ordering and identity joins, not pixel-to-feature recomputation or payload-to-sidecar slice equality.",
            "Component IDs use stable synthetic train seeds; family IDs use the frozen train panel ordinal and tensor hash. They do not expose human source names.",
            "The V37/V35 report bytes are SHA-bound, but report JSON is not parsed so its unrelated development summary is not materialized.",
        ],
        "input_sha256": input_hashes, "source_sha256": source_hashes,
        "family_train_order_binding_sha256": FAMILY_ORDER_BINDING_SHA256,
        "feature_group_rows": rows,
        "identity_row_count": sum(rows.values()),
        "groups": groups,
    }


def write_exclusive(path: Path, payload: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(payload)


def _output_path(root: Path, value: Path) -> Path:
    if value.is_absolute() or ".." in value.parts:
        raise ValueError("output must be a safe project-relative path")
    if value.parts[:2] != ("artifacts", "goal22-runs"):
        raise ValueError("output must be under artifacts/goal22-runs")
    target = root / value
    current = root
    for part in value.parts[:-1]:
        current = current / part
        if not current.exists():
            current.mkdir()
        if current.is_symlink() or getattr(current.stat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("reparse output ancestor rejected")
        if not current.is_dir():
            raise ValueError("output ancestor is not a directory")
    return target


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = build_report()
    payload = json.dumps(report, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    write_exclusive(_output_path(ROOT, args.output), payload)
    print(json.dumps({"status": report["status"], "rows": report["feature_group_rows"],
                      "report_sha256": hashlib.sha256(payload).hexdigest()}))


if __name__ == "__main__":
    main()
