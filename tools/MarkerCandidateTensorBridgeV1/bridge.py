# SPDX-License-Identifier: Apache-2.0
"""Train-only review join for captured marker sampler tensors.

This module does not read files, annotations, pixels, model weights, or split
ledgers. Its caller must provide bytes from a separately bound train capture
and generator. It verifies the supplied byte identities and emits review-only
rows. No row is admitted to training.
"""

from __future__ import annotations

from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import struct
import zlib
from typing import Any


SCHEMA = "graphreader.marker-candidate-tensor-bridge.v2"
EVIDENCE_SCHEMA = "graphreader.marker-context-crop-evidence.v1"
MATCH_TOLERANCE_PX = 2.0
FULL_CAPTURE_REPORT_SHA256 = "c46e2a909df60d8665a7121c0a60a2bf0f6a64e60ce36b36dad19d40b8ff8de3"
FULL_CAPTURE_COMPLETE_SHA256 = "957e2bec1d45b49665e05c6442e90fa4285d27e02140b8458f2f5066de001019"
FULL_CAPTURE_BINDING_SHA256 = "792d6ec1bc59f16f92cb03adae1b59d2ca0d35ba85218f6eb9a9218d2594327b"
CORPUS_BUILDER_BINDING_SHA256 = "f095bac6f76a9a2d444869ba4d75538e76237c399d3b98997c97292b4cebebd4"
IDENTITY_METADATA_SHA256 = "2ca7fce16eee67d7f040e62f6979ebc76c83bcec6bafad03e67e381ce83d2055"
ASSOCIATION_REPORT_SHA256 = "91a4cfd05f91d155bd95d30877d6feb17f742573d4f4ec0a91f27a450ec59244"
ARTIFACT_REPORT_SHA256 = "0db3f20d13b9aa730148111d4e88064532f74caa0c02ea9c337b65eacd4f5c51"
LOCAL = {"width": 32, "height": 32, "radius_scale": 2.25,
         "minimum_half_extent_frame_pixels": 4.0, "padding_value": 0.0}
WIDE = {"width": 32, "height": 32, "radius_scale": 6.75,
        "minimum_half_extent_frame_pixels": 12.0, "padding_value": 0.0}
HEX = frozenset("0123456789abcdef")
CANONICAL_PROJECT_ROOT = Path(r"E:\Graph-auto-reader")


def _sha(value: Any, name: str) -> str:
    if (not isinstance(value, str) or len(value) != 64 or
            any(char not in HEX for char in value)):
        raise ValueError(f"{name} must be a lowercase SHA-256")
    return value


def _hash(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _canonical(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      allow_nan=False).encode("utf-8")


def _strict_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("JSON contains a duplicate field")
        result[key] = value
    return result


def _reject_reparse_ancestors(path: Path, name: str) -> None:
    absolute = Path(path).absolute()
    ancestors = [absolute]
    while ancestors[-1].parent != ancestors[-1]:
        ancestors.append(ancestors[-1].parent)
    for current in reversed(ancestors):
        try:
            attributes = getattr(current.lstat(), "st_file_attributes", 0)
        except OSError as error:
            raise ValueError(f"{name} path is missing: {current}") from error
        if current.is_symlink() or bool(attributes & 0x400):
            raise ValueError(f"{name} path contains a reparse point")


def _png_dimensions(raw: bytes) -> tuple[int, int]:
    if (not isinstance(raw, bytes) or len(raw) < 33 or
            raw[:8] != b"\x89PNG\r\n\x1a\n" or raw[12:16] != b"IHDR" or
            struct.unpack(">I", raw[8:12])[0] != 13):
        raise ValueError("bound source and panel images must be valid PNG payloads")
    if (zlib.crc32(raw[12:29]) & 0xFFFFFFFF) != struct.unpack(">I", raw[29:33])[0]:
        raise ValueError("PNG IHDR CRC differs")
    width, height = struct.unpack(">II", raw[16:24])
    if width <= 0 or height <= 0:
        raise ValueError("PNG dimensions are invalid")
    return width, height


SOURCE_BINDINGS = {
    "tools/MarkerArtifactAssociationV1/association.py": "432796f6e2e879ca5dc59e583b80ffa7ccca527bffebd279c34501b45f5a6c69",
    "tools/MarkerArtifactAssociationV1/batch_capture.py": "0484cdb65176ec4765ae10b46d9f765d2c3e5c1ed20bee4133c95ba41099c644",
    "tools/MarkerArtifactAssociationV1/batch_producer.py": "30c012425a1c7712c2a714982341735a893927d6ba62a49c992fd1c23380b918",
    "tools/MarkerArtifactAssociationV1/full_capture_producer.py": "5591497f1c67a5762a5cc5f809e0a64dc53d8bdd7339c44289652c39bfad5cc8",
    "ml/synthetic/marker_context_train_corpus_v5/plan.py": "51a5501881f9cf5d2e65526679a4248c9b11bd244bcc4406ac3f60540857462e",
    "ml/synthetic/marker_context_train_corpus_v5/builder.py": "f5dfd871446132b4c777e564d7416f3feec02a0178c3c1ee80a9139806be9e6f",
    "ml/synthetic/marker_context_catalog_v1/labels.py": "27a36dc116e0424c0c932683c5f16d4650c0334b4362743cfd14741da48699e1",
    "tools/GraphReader.MarkerContextEvidence/MarkerContextEvidenceCapture.cs": "403e4450c1839449e9895fea7660c00362c863274b05ab85e48c272cdd965c01",
    "src/GraphReader.Markers/Classification/MarkerPatchExtractor.cs": "5c15dff2dd3f3dfd0685ba9f5380eb1e82dc0c37ee16438795fa00a033fd13ca",
    "src/GraphReader.Markers/Classification/MarkerClassificationService.cs": "c6835d703a6228712e895614fd5001d168276001fecd225e1b786ef71e651f47",
    "src/GraphReader.Markers/Classification/MarkerClassificationContracts.cs": "c38f541a828801cd271f47e1c3a1cc1ff687f8b5b41e67ec33ae41485a4cbad9",
    "src/GraphReader.App/Integration/Workflow/ProductionMarkerClassificationAdapter.cs": "7c4ade83e37d1df7df93792f16d6ab3eeb097d19391191d525595b6b105c598a",
    "tools/GraphReader.MarkerBoundInputTrainCapture/CenterRunner.cs": "f43f9cc9edd9aa402208c75d06bb39fb07120d5f5e29504f2b2ef2d836e5aadf",
    "tools/GraphReader.MarkerDualViewTrainCapture/SamplerCapture.cs": "c54ea7d2bdd5788cc9ecc5760439683cc77653bbe9a2b617818303c61ece1eb9",
}


def verify_source_bindings(project_root: Path) -> dict[str, str]:
    """Verify the exact source closure used by the sampler/association protocol."""
    _reject_reparse_ancestors(project_root, "project root")
    root = project_root.resolve(strict=True)
    canonical = CANONICAL_PROJECT_ROOT.resolve(strict=True)
    if root != canonical:
        raise ValueError("source closure must be verified from the canonical E project root")
    result = {}
    for relative, expected in SOURCE_BINDINGS.items():
        path = root.joinpath(*relative.split("/"))
        current = root
        for part in relative.split("/"):
            current = current / part
            _reject_reparse_ancestors(current, f"bound source {relative}")
        if not path.is_file() or path.resolve(strict=True).parent != path.parent.resolve(strict=True):
            raise ValueError(f"bound source is not a regular file: {relative}")
        raw = path.read_bytes()
        actual = _hash(raw)
        if actual != expected:
            raise ValueError(f"bound source SHA-256 differs: {relative}")
        result[relative] = actual
    return result


def validate_train_source_groups(source_rows: list[dict[str, Any]]) -> dict[str, int]:
    """Reject mixed splits and source IDs reused across a train-group inventory."""
    if not isinstance(source_rows, list) or not source_rows:
        raise ValueError("source group inventory must be nonempty")
    ids: set[str] = set()
    group_splits: dict[str, str] = {}
    for row in source_rows:
        if not isinstance(row, dict) or row.get("split") != "train":
            raise ValueError("candidate tensor bridge accepts train sources only")
        source_id, group_id = row.get("source_id"), row.get("source_group_id")
        if (not isinstance(source_id, str) or not source_id or source_id in ids or
                not isinstance(group_id, str) or not group_id):
            raise ValueError("source or source-group identity is missing or duplicated")
        ids.add(source_id)
        previous = group_splits.setdefault(group_id, row["split"])
        if previous != row["split"]:
            raise ValueError("one source group is assigned to multiple splits")
    return {"source_count": len(ids), "source_group_count": len(group_splits),
            "train_source_count": len(ids), "dev_source_count": 0, "sealed_source_count": 0}


def tensor_f32le(values: Any, *, expected_count: int = 1024) -> bytes:
    """Encode the C# sampler's JSON float array as its original binary32 values."""
    if not isinstance(values, list) or len(values) != expected_count:
        raise ValueError("sampler tensor value count differs from the fixed 32x32 shape")
    converted = []
    for value in values:
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise ValueError("sampler tensor contains a non-finite or non-numeric value")
        value32 = struct.unpack("<f", struct.pack("<f", float(value)))[0]
        if not math.isfinite(value32) or not 0.0 <= value32 <= 1.0:
            raise ValueError("raw ink-probability sampler tensor is outside [0,1]")
        converted.append(value32)
    return struct.pack(f"<{expected_count}f", *converted)


def _point(value: Any, width: int, height: int, name: str) -> tuple[float, float]:
    if isinstance(value, dict) and set(value) == {"x", "y"}:
        value = [value["x"], value["y"]]
    if (not isinstance(value, list) or len(value) != 2 or
            any(isinstance(item, bool) or not isinstance(item, (int, float)) or
                not math.isfinite(item) for item in value)):
        raise ValueError(f"{name} must be a finite source-pixel point")
    x, y = float(value[0]), float(value[1])
    if not (0 <= x < width and 0 <= y < height):
        raise ValueError(f"{name} is outside the bound panel")
    return x, y


def _distance(a: tuple[float, float], b: tuple[float, float]) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def _frame_hash(values: Any, width: int, height: int, channels: int) -> str:
    count = width * height * channels
    if not isinstance(values, list) or len(values) != count:
        raise ValueError("captured source plane size differs from its declared dimensions")
    return _hash(tensor_f32le(values, expected_count=count))


def _mask_hash(values: Any, width: int, height: int, name: str) -> str:
    if not isinstance(values, list) or len(values) != width * height:
        raise ValueError(f"{name} mask snapshot dimensions differ")
    return _hash(tensor_f32le(values, expected_count=width * height))


def _matrix_is_identity(value: Any) -> bool:
    if not isinstance(value, dict) or set(value) != {
            "m11", "m12", "m13", "m21", "m22", "m23"}:
        return False
    expected = {"m11": 1.0, "m12": 0.0, "m13": 0.0,
                "m21": 0.0, "m22": 1.0, "m23": 0.0}
    return all(type(value[key]) in (int, float) and math.isfinite(value[key]) and
               float(value[key]) == target for key, target in expected.items())


def _verify_evidence(raw: bytes, *, source: dict[str, Any], candidate: dict[str, Any],
                     view: str, expected_frame_sha256: str,
                     expected_ocr_mask_sha256: str | None,
                     expected_artifact_mask_sha256: str | None) -> tuple[bytes, str, dict[str, str]]:
    if not isinstance(raw, bytes) or not raw:
        raise ValueError(f"{view} sampler evidence must be nonempty raw bytes")
    document = json.loads(raw, object_pairs_hook=_strict_object)
    if not isinstance(document, dict) or set(document) != {
            "input", "outputShapeNchw", "productionTensor",
            "patchToFrameMatrix3x3", "patchToOriginalMatrix3x3"}:
        raise ValueError(f"{view} sampler evidence fields differ from the frozen schema")
    input_row = document["input"]
    if not isinstance(input_row, dict) or set(input_row) != {
            "schema", "scope", "caseId", "sourceImage", "width", "height",
            "channelCount", "channelsFirstPixels", "ocrMask", "artifactMask",
            "originalToFrame", "markerCenterOriginalPixels", "markerRadiusOriginalPixels",
            "markerId", "options"}:
        raise ValueError(f"{view} sampler input fields differ from the frozen schema")
    if (input_row["schema"] != EVIDENCE_SCHEMA or input_row["scope"] not in {
            "synthetic_fixture", "full_train_capture_inline"} or
            input_row["sourceImage"] != "Original" or input_row["width"] != source["width"] or
            input_row["height"] != source["height"] or input_row["channelCount"] != 1 or
            input_row["markerId"] != candidate["candidate_id"] or
            input_row["caseId"] != candidate["sampler_case_id"] or
            not _matrix_is_identity(input_row["originalToFrame"])):
        raise ValueError(f"{view} sampler identity or source-frame mapping differs")
    center = _point(input_row["markerCenterOriginalPixels"], source["width"], source["height"],
                    f"{view} sampler center")
    if center != tuple(candidate["center"]) or input_row["markerRadiusOriginalPixels"] != candidate["radius"]:
        raise ValueError(f"{view} sampler marker geometry differs from detector candidate")
    options = input_row["options"]
    expected = LOCAL if view == "local" else WIDE
    if not isinstance(options, dict) or set(options) != {
            "width", "height", "outputChannels", "radiusScale",
            "minimumHalfExtentFramePixels", "paddingValue", "originalPixelContentBounds"}:
        raise ValueError(f"{view} sampler options fields differ")
    expected_bounds = candidate.get("local_content_bounds") if view == "local" else None
    if (options["width"] != expected["width"] or options["height"] != expected["height"] or
            options["outputChannels"] != 1 or options["radiusScale"] != expected["radius_scale"] or
            options["minimumHalfExtentFramePixels"] != expected["minimum_half_extent_frame_pixels"] or
            options["paddingValue"] != expected["padding_value"] or
            options["originalPixelContentBounds"] != expected_bounds):
        raise ValueError(f"{view} sampler options differ from the pinned recipe")
    if document["outputShapeNchw"] != [1, 1, 32, 32]:
        raise ValueError(f"{view} sampler output shape differs from [1,1,32,32]")
    expected_patch = _patch_to_original(center, float(candidate["radius"]), expected)
    if not _matrix_close(document["patchToOriginalMatrix3x3"], expected_patch) or not _matrix_close(
            document["patchToFrameMatrix3x3"], expected_patch):
        raise ValueError(f"{view} sampler coordinate matrix differs from the bound candidate geometry")
    tensor = tensor_f32le(document["productionTensor"])
    frame_sha = _frame_hash(input_row["channelsFirstPixels"], source["width"], source["height"], 1)
    if frame_sha != expected_frame_sha256:
        raise ValueError(f"{view} sampler source-plane hash differs from the bound panel frame")
    ocr_sha = _mask_hash(input_row["ocrMask"], source["width"], source["height"], "OCR")
    artifact_sha = _mask_hash(input_row["artifactMask"], source["width"], source["height"], "artifact")
    if expected_ocr_mask_sha256 is not None and ocr_sha != expected_ocr_mask_sha256:
        raise ValueError(f"{view} OCR mask provenance hash differs")
    if expected_artifact_mask_sha256 is not None and artifact_sha != expected_artifact_mask_sha256:
        raise ValueError(f"{view} artifact mask provenance hash differs")
    return tensor, _hash(tensor), {"ocr_mask_snapshot_sha256": ocr_sha,
                                   "artifact_mask_snapshot_sha256": artifact_sha,
                                   "source_plane_f32le_sha256": frame_sha}


def _patch_to_original(center: tuple[float, float], radius: float,
                       options: dict[str, float | int]) -> list[float]:
    half = max(float(options["minimum_half_extent_frame_pixels"]),
               radius * float(options["radius_scale"]))
    scale = (2.0 * half) / float(options["width"])
    offset_x = center[0] - half + (half / float(options["width"]))
    offset_y = center[1] - half + (half / float(options["height"]))
    return [scale, 0.0, offset_x, 0.0, scale, offset_y, 0.0, 0.0, 1.0]


def _matrix_close(actual: Any, expected: list[float]) -> bool:
    return (isinstance(actual, list) and len(actual) == 9 and
            all(not isinstance(value, bool) and isinstance(value, (int, float)) and
                math.isfinite(value) and math.isclose(float(value), target, rel_tol=1e-12, abs_tol=1e-12)
                for value, target in zip(actual, expected, strict=True)))


def _verify_visibility_proof(glyph: dict[str, Any], source: dict[str, Any]) -> str:
    proof = glyph.get("visibility_evidence")
    if not isinstance(proof, dict):
        raise ValueError("legend glyph lacks upstream visibility evidence")
    supplied = dict(proof)
    digest = supplied.pop("proof_sha256", None)
    if digest != _hash(_canonical(supplied)):
        raise ValueError("legend glyph visibility proof digest differs")
    if (not isinstance(glyph.get("glyph_id"), str) or not glyph["glyph_id"] or
            not isinstance(glyph.get("source_reference"), str) or not glyph["source_reference"]):
        raise ValueError("legend glyph identity and source reference are required")
    required = {
        "schema": "graphreader.legend-glyph-visibility-proof.v1",
        "method": "single_glyph_ablation_rgb_diff",
        "scene_sha256": source["scene_sha256"],
        "annotation_sha256": source["annotation_sha256"],
        "source_reference": glyph.get("source_reference"),
        "glyph_id": glyph.get("glyph_id"),
        "source_raster_sha256": source["source_raster_sha256"],
        "width": source["width"], "height": source["height"],
        "outside_box_changed_pixel_count": 0,
        "center": glyph.get("center"),
        "glyph_box": glyph.get("glyph_box"),
    }
    if any(proof.get(key) != value for key, value in required.items()):
        raise ValueError("legend visibility proof is not bound to this train source/glyph")
    if glyph.get("visibility_proof_sha256") != digest:
        raise ValueError("legend glyph digest differs from the reviewed artifact report")
    if type(proof.get("changed_pixel_count")) is not int or proof["changed_pixel_count"] <= 0:
        raise ValueError("legend visibility proof lacks positive pixel-change evidence")
    for field in ("rendered_rgb_sha256", "ablated_rgb_sha256", "renderer_bundle_sha256"):
        _sha(proof.get(field), f"visibility {field}")
    box = glyph.get("glyph_box")
    if (not isinstance(box, list) or len(box) != 4 or
            any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in box) or
            box[2] <= 0 or box[3] <= 0 or box[0] < 0 or box[1] < 0 or
            box[0] + box[2] > source["width"] or box[1] + box[3] > source["height"]):
        raise ValueError("legend glyph box is invalid")
    _point(glyph.get("center"), source["width"], source["height"], "legend glyph center")
    return digest


def _full_capture_candidate_evidence(panel: dict[str, Any], raw_panel: bytes,
                                    source: dict[str, Any]) -> dict[str, dict[str, bytes]]:
    """Adapt the actual full-capture inline view arrays to the core validator.

    The wrapper is derived only from the hash-bound panel JSON. It is not a
    separate sampler receipt and carries no independent factory/normalization
    claim.
    """
    expected_panel_keys = {
        "source_id", "case_id", "scene_id", "scene_sha256", "seed", "panel_id",
        "panel_sha256", "provenance", "frame", "center_markers", "ocr_regions",
        "classifier_inputs", "symbol_input_ids", "symbol_crop_input_ids",
        "original_pixel_content_bounds", "sampled_local_and_experimental_wide_views",
    }
    if set(panel) != expected_panel_keys:
        raise ValueError("saved full-capture panel fields differ from the pinned schema")
    if (panel["source_id"] != source["source_id"] or panel["scene_id"] != source["scene_id"] or
            panel["scene_sha256"] != source["scene_sha256"] or panel["panel_id"] != source["panel_id"] or
            panel["panel_sha256"] != source["panel_image_sha256"] or
            _hash(raw_panel) != source["panel_result_sha256"]):
        raise ValueError("full-capture panel identity/hash differs from source binding")
    provenance = panel["provenance"]
    if not isinstance(provenance, dict) or set(provenance) != {
            "source_sha256", "panel_image_sha256", "source_width", "source_height",
            "requested_crop_source_pixels", "encoded_crop_source_pixels",
            "source_to_panel_matrix", "panel_to_source_matrix", "identity"}:
        raise ValueError("full-capture panel provenance fields differ")
    identity = [1, 0, 0, 0, 1, 0, 0, 0, 1]
    if (provenance["identity"] is not True or provenance["source_sha256"] != source["source_raster_sha256"] or
            provenance["panel_image_sha256"] != source["panel_image_sha256"] or
            provenance["source_width"] != source["width"] or provenance["source_height"] != source["height"] or
            provenance["source_to_panel_matrix"] != identity or provenance["panel_to_source_matrix"] != identity):
        raise ValueError("full-capture panel source transform is not the bound identity mapping")
    frame = panel["frame"]
    if not isinstance(frame, dict) or set(frame) != {
            "width", "height", "channel_count", "channels_first_pixels", "source_image",
            "original_to_frame", "ocr_mask", "artifact_mask"}:
        raise ValueError("full-capture frame fields differ from the pinned schema")
    width, height = source["width"], source["height"]
    frame_map = frame["original_to_frame"]
    if (frame["width"] != width or frame["height"] != height or frame["channel_count"] != 1 or
            frame["source_image"] != 0 or not isinstance(frame_map, dict) or
            set(frame_map) != {"m11", "m12", "m13", "m21", "m22", "m23", "determinant", "is_invertible"} or
            any(frame_map.get(key) != value for key, value in {
                "m11": 1, "m12": 0, "m13": 0, "m21": 0, "m22": 1,
                "m23": 0, "determinant": 1, "is_invertible": True}.items())):
        raise ValueError("full-capture frame is not the bound original-pixel identity frame")
    frame_bytes = tensor_f32le(frame["channels_first_pixels"], expected_count=width * height)
    if _hash(frame_bytes) != source["frame_f32le_sha256"]:
        raise ValueError("full-capture frame plane differs from source binding")

    def mask_values(name: str) -> list[Any]:
        mask = frame[name]
        if (not isinstance(mask, dict) or set(mask) != {"width", "height", "values"} or
                mask["width"] != width or mask["height"] != height):
            raise ValueError(f"full-capture {name} dimensions differ")
        return mask["values"]

    def validate_bounds(value: Any, candidate: dict[str, Any]) -> None:
        if value is None:
            return
        keys = {"x", "y", "width", "height", "left", "top", "right", "bottom", "is_valid"}
        if not isinstance(value, dict) or set(value) != keys or value.get("is_valid") is not True:
            raise ValueError("full-capture local content bound has an unknown rectangle schema")
        numeric = ("x", "y", "width", "height", "left", "top", "right", "bottom")
        if any(isinstance(value[key], bool) or not isinstance(value[key], (int, float)) or
               not math.isfinite(value[key]) for key in numeric):
            raise ValueError("full-capture local content bound is non-finite")
        x, y, w, h = (float(value[key]) for key in ("x", "y", "width", "height"))
        if (w <= 0 or h <= 0 or value["left"] != x or value["top"] != y or
                value["right"] != x + w or value["bottom"] != y + h or
                x < 0 or y < 0 or x + w > width or y + h > height or
                not (x <= candidate["center"]["x"] < x + w and
                     y <= candidate["center"]["y"] < y + h)):
            raise ValueError("full-capture local content bound is inconsistent or excludes its marker")

    ocr_mask = mask_values("ocr_mask")
    artifact_mask = mask_values("artifact_mask")
    # Hash now to validate finite, fixed-shape provenance-only snapshots.
    ocr_sha = _hash(tensor_f32le(ocr_mask, expected_count=width * height))
    artifact_sha = _hash(tensor_f32le(artifact_mask, expected_count=width * height))

    bounds = panel["original_pixel_content_bounds"]
    if not isinstance(bounds, dict):
        raise ValueError("full-capture content bounds must be an object")
    candidates = panel["center_markers"]
    inputs = panel["classifier_inputs"]
    view_rows = panel["sampled_local_and_experimental_wide_views"]
    if not all(isinstance(value, list) for value in (candidates, inputs, view_rows)):
        raise ValueError("full-capture candidate and sampler inventories must be arrays")
    if len(candidates) != len(inputs) or len(candidates) != len(view_rows):
        raise ValueError("full-capture candidate/input/view denominators differ")
    if [row.get("marker_id") for row in candidates if isinstance(row, dict)] != [
            row.get("marker_id") for row in inputs if isinstance(row, dict)]:
        raise ValueError("full-capture classifier-input order differs from detector candidates")
    if set(bounds) - {row.get("marker_id") for row in candidates if isinstance(row, dict)}:
        raise ValueError("full-capture content bounds refer to an unknown candidate")

    result: dict[str, dict[str, bytes]] = {}
    for candidate, classifier_input, row in zip(candidates, inputs, view_rows, strict=True):
        if not isinstance(candidate, dict) or not isinstance(classifier_input, dict) or not isinstance(row, dict):
            raise ValueError("full-capture candidate/view row is malformed")
        candidate_id = candidate.get("marker_id")
        if (not isinstance(candidate_id, str) or row.get("marker") != classifier_input or
                classifier_input.get("marker_id") != candidate_id or
                row.get("local_content_bounds") != bounds.get(candidate_id)):
            raise ValueError("full-capture sampler row is not bound to its classifier input/bounds")
        if candidate_id in result:
            raise ValueError("full-capture candidate IDs are duplicated")
        center, radius = candidate["center"], candidate.get("radius")
        if (not isinstance(center, dict) or candidate.get("coordinate_space") != "original_pixels" or
                center.get("is_finite") is not True):
            raise ValueError("full-capture candidate geometry is malformed")
        validate_bounds(row.get("local_content_bounds"), candidate)
        point = {"x": center.get("x"), "y": center.get("y")}
        local_bounds = row.get("local_content_bounds")
        local_raw: dict[str, bytes] = {}
        for view, key, scale, minimum, bounds_for_view, digest_field in (
                ("local", "local", 2.25, 4.0, local_bounds, "local_sha256"),
                ("wide", "wide", 6.75, 12.0, None, "wide_sha256")):
            tensor = row.get(key)
            tensor_bytes = tensor_f32le(tensor)
            declared_sha = _sha(row.get(digest_field), f"full-capture {view} sampler SHA")
            if _hash(tensor_bytes) != declared_sha:
                raise ValueError(f"full-capture {view} tensor bytes differ from their declared hash")
            evidence_doc = {
                "input": {
                    "schema": EVIDENCE_SCHEMA, "scope": "full_train_capture_inline",
                    "caseId": hashlib.sha256(f"{source['source_id']}\0{source['panel_id']}\0{candidate_id}".encode()).hexdigest()[:40],
                    "sourceImage": "Original", "width": width, "height": height, "channelCount": 1,
                    "channelsFirstPixels": frame["channels_first_pixels"], "ocrMask": ocr_mask,
                    "artifactMask": artifact_mask,
                    "originalToFrame": {key: frame_map[key] for key in ("m11", "m12", "m13", "m21", "m22", "m23")},
                    "markerCenterOriginalPixels": point, "markerRadiusOriginalPixels": radius,
                    "markerId": candidate_id,
                    "options": {"width": 32, "height": 32, "outputChannels": 1,
                                "radiusScale": scale, "minimumHalfExtentFramePixels": minimum,
                                "paddingValue": 0.0, "originalPixelContentBounds": bounds_for_view},
                },
                "outputShapeNchw": [1, 1, 32, 32], "productionTensor": tensor,
                "patchToFrameMatrix3x3": _patch_to_original((float(point["x"]), float(point["y"])), float(radius),
                    {"width": 32, "height": 32, "radius_scale": scale, "minimum_half_extent_frame_pixels": minimum}),
                "patchToOriginalMatrix3x3": _patch_to_original((float(point["x"]), float(point["y"])), float(radius),
                    {"width": 32, "height": 32, "radius_scale": scale, "minimum_half_extent_frame_pixels": minimum}),
            }
            local_raw[view] = _canonical(evidence_doc)
        result[candidate_id] = {
            "local": {"raw_bytes": local_raw["local"], "sha256": _hash(local_raw["local"])},
            "wide": {"raw_bytes": local_raw["wide"], "sha256": _hash(local_raw["wide"])},
        }
    return result


def _load_bound_classifier(project_root: Path, verified_sources: dict[str, str]):
    relative = "ml/synthetic/marker_context_catalog_v1/labels.py"
    source_path = project_root.joinpath(*relative.split("/"))
    raw = source_path.read_bytes()
    if _hash(raw) != verified_sources.get(relative):
        raise ValueError("label classifier changed after source-closure verification")
    namespace = {"__name__": "_graphreader_pinned_marker_labels", "__file__": str(source_path)}
    exec(compile(raw, str(source_path), "exec"), namespace)
    classify = namespace.get("classify")
    if not callable(classify):
        raise ValueError("pinned label source did not provide classify")
    return classify


def _derive_bound_inventories(*, source: dict[str, Any] | None, complete_receipt_bytes: bytes,
                              identity_metadata_bytes: bytes, capture_report_bytes: bytes,
                              truth_sidecar_bytes: bytes, artifact_report_bytes: bytes,
                              classify_annotations
                              ) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]], dict[str, Any]] | tuple[dict[str, dict[str, Any]], list[dict[str, Any]]]:
    """Derive exhaustive labels only from the pinned full-corpus evidence bundle."""
    for raw, expected, name in (
            (complete_receipt_bytes, FULL_CAPTURE_COMPLETE_SHA256, "corpus COMPLETE receipt"),
            (identity_metadata_bytes, IDENTITY_METADATA_SHA256, "identity metadata"),
            (capture_report_bytes, FULL_CAPTURE_REPORT_SHA256, "full capture report"),
            (artifact_report_bytes, ARTIFACT_REPORT_SHA256, "artifact visibility report")):
        if not isinstance(raw, bytes) or _hash(raw) != expected:
            raise ValueError(f"{name} bytes differ from the independently pinned evidence")
    complete = json.loads(complete_receipt_bytes, object_pairs_hook=_strict_object)
    identity_doc = json.loads(identity_metadata_bytes, object_pairs_hook=_strict_object)
    capture = json.loads(capture_report_bytes, object_pairs_hook=_strict_object)
    truth_doc = json.loads(truth_sidecar_bytes, object_pairs_hook=_strict_object)
    artifact_report = json.loads(artifact_report_bytes, object_pairs_hook=_strict_object)
    if (not isinstance(complete, dict) or complete.get("schema") != "marker-context-train-corpus-complete-v5" or
            complete.get("mode") != "full" or complete.get("source_count") != 64 or
            complete.get("builder_binding_sha256") != CORPUS_BUILDER_BINDING_SHA256 or
            complete.get("truth_sha256") != _hash(truth_sidecar_bytes) or
            complete.get("training_approved") is not False):
        raise ValueError("corpus COMPLETE receipt does not bind the supplied truth sidecar")
    if (not isinstance(identity_doc, dict) or identity_doc.get("schema") != "marker-context-train-identity-v5" or
            identity_doc.get("mode") != "full" or identity_doc.get("training_approved") is not False):
        raise ValueError("identity metadata schema/scope differs")
    if (not isinstance(capture, dict) or capture.get("schema") != "graphreader.marker-context-bound-input-full-capture-report.v1" or
            capture.get("capture_binding_sha256") != FULL_CAPTURE_BINDING_SHA256 or
            capture.get("source_count") != 64 or capture.get("all_sources_succeeded") is not True or
            capture.get("training_approved") is not False or capture.get("truth_content_read") is not False):
        raise ValueError("full-capture report identity/scope differs")
    if (not isinstance(truth_doc, dict) or truth_doc.get("schema") != "marker-context-train-truth-v5" or
            truth_doc.get("mode") != "full" or not isinstance(truth_doc.get("sources"), list) or
            len(truth_doc["sources"]) != 64 or truth_doc.get("training_approved") is not False):
        raise ValueError("truth sidecar source inventory is incomplete or out of scope")
    if (not isinstance(artifact_report, dict) or artifact_report.get("schema") !=
            "graphreader.marker-artifact-v5-train-visibility-audit.v1" or
            artifact_report.get("split") != "train" or artifact_report.get("source_count") != 64 or
            artifact_report.get("source_accounting_complete") is not True or
            artifact_report.get("all_sources_succeeded") is not True or
            artifact_report.get("training_approved") is not False):
        raise ValueError("artifact report inventory is incomplete or out of scope")
    identity_rows, capture_rows, truth_rows, artifact_rows = (
        identity_doc.get("sources"), capture.get("sources"), truth_doc["sources"], artifact_report.get("sources"))
    events = capture.get("events")
    if any(not isinstance(rows, list) or len(rows) != 64 for rows in
           (identity_rows, capture_rows, artifact_rows, events)):
        raise ValueError("one or more independently bound 64-source inventories are incomplete")

    def unique_map(rows: list[Any], field: str, name: str) -> dict[str, dict[str, Any]]:
        result = {}
        for row in rows:
            if not isinstance(row, dict) or not isinstance(row.get(field), str) or row[field] in result:
                raise ValueError(f"{name} has malformed or duplicate {field}")
            result[row[field]] = row
        return result

    identities = unique_map(identity_rows, "source_id", "identity metadata")
    captures = unique_map(capture_rows, "source_id", "capture report")
    artifacts = unique_map(artifact_rows, "source_id", "artifact report")
    if set(identities) != set(captures) or set(identities) != set(artifacts):
        raise ValueError("independent identity/capture/artifact source sets differ")
    event_map: dict[tuple[str, str], dict[str, Any]] = {}
    for event in events:
        if not isinstance(event, dict) or not all(isinstance(event.get(k), str) for k in ("source_id", "panel_id", "sha256")):
            raise ValueError("capture event identity is malformed")
        key = event["source_id"], event["panel_id"]
        if key in event_map:
            raise ValueError("capture contains duplicate source/panel events")
        event_map[key] = event
    truth_by_case_image: dict[tuple[str, str], dict[str, Any]] = {}
    for row in truth_rows:
        if not isinstance(row, dict) or not all(isinstance(row.get(k), str) for k in ("case_id", "image_sha256")):
            raise ValueError("truth sidecar row identity is malformed")
        key = row["case_id"], row["image_sha256"]
        if key in truth_by_case_image:
            raise ValueError("truth sidecar contains duplicate case/image rows")
        truth_by_case_image[key] = row
    if len(truth_by_case_image) != 64:
        raise ValueError("truth sidecar identities are incomplete")
    identity_case_images = [(row.get("case_id"), row.get("image_sha256")) for row in identity_rows
                            if isinstance(row, dict)]
    if (len(identity_case_images) != 64 or len(set(identity_case_images)) != 64 or
            set(identity_case_images) != set(truth_by_case_image)):
        raise ValueError("truth sidecar case/image identities do not bijectively join metadata")

    derived_by_source: dict[str, dict[str, Any]] = {}
    inventory_rows = []

    for source_id, identity in identities.items():
        captured, artifact = captures[source_id], artifacts[source_id]
        identity_capture_fields = ("case_id", "scene_id", "scene_sha256", "image_sha256", "seed")
        artifact_identity_fields = ("scene_id", "scene_sha256", "image_sha256", "seed")
        if (any(identity.get(key) != captured.get(key) for key in identity_capture_fields) or
                any(identity.get(key) != artifact.get(key) for key in artifact_identity_fields)):
            raise ValueError("identity/capture/artifact metadata join differs")
        if identity.get("source_id_status") != "predeclared_not_yet_imported":
            raise ValueError("identity metadata contains an unexpected source-import status")
        key = identity.get("case_id"), identity.get("image_sha256")
        truth_row = truth_by_case_image.get(key)
        if truth_row is None or truth_row.get("scene_id", truth_row.get("scene", {}).get("scene_id")) != identity.get("scene_id"):
            raise ValueError("truth sidecar case/image/scene join differs")
        annotation = truth_row.get("annotation")
        scene = truth_row.get("scene")
        if not isinstance(annotation, dict) or not isinstance(scene, dict):
            raise ValueError("truth sidecar lacks complete annotation/scene objects")
        if (_hash(_canonical(scene)) != identity.get("scene_sha256") or
                annotation.get("scene_id") != identity.get("scene_id")):
            raise ValueError("truth sidecar scene bytes/annotation identity differ")
        annotation_sha = _hash(_canonical(annotation))
        if (annotation.get("coordinate_space") != "original_pixels" or
                annotation.get("canvas", {}).get("width") != scene.get("canvas", {}).get("width") or
                annotation.get("canvas", {}).get("height") != scene.get("canvas", {}).get("height")):
            raise ValueError("truth sidecar annotation coordinate space/canvas differs")
        classified = classify_annotations(annotation, expected_split="train")
        positives = [{
            "truth_id": item["id"], "source_id": source_id, "annotation_sha256": annotation_sha,
            "coordinate_space": "original_source_pixels", "center": item["center"],
            "connection": "connected" if item["bin"] == "connected_positive" else "isolated",
        } for item in classified["records"] if item["bin"] in ("connected_positive", "isolated_positive")]
        annotation_glyphs = [item for item in annotation.get("hard_negatives", [])
                             if isinstance(item, dict) and item.get("kind") == "legend_symbol"]
        reported_glyphs = artifact.get("glyphs")
        if not isinstance(reported_glyphs, list) or len(reported_glyphs) != len(annotation_glyphs) or \
                artifact.get("legend_glyph_count") != len(annotation_glyphs):
            raise ValueError("artifact report omitted or added a source legend glyph")
        annotation_glyphs_by_id = {item.get("hard_negative_id"): item for item in annotation_glyphs}
        if len(annotation_glyphs_by_id) != len(annotation_glyphs) or set(annotation_glyphs_by_id) != {
                item.get("glyph_id") for item in reported_glyphs if isinstance(item, dict)}:
            raise ValueError("annotation and artifact-report glyph identity inventories differ")
        validated_glyphs = []
        allowed_statuses = {"matched_artifact_review_only", "unmatched_review_only",
                            "review_ambiguous", "review_overlay_conflict"}
        for glyph_row in reported_glyphs:
            glyph = annotation_glyphs_by_id[glyph_row["glyph_id"]]
            geometry = glyph.get("geometry", {})
            center, box = geometry.get("center"), geometry.get("box")
            if (glyph_row.get("source_reference") != glyph.get("source_reference") or
                    glyph_row.get("center") != center or glyph_row.get("glyph_box") != box or
                    glyph_row.get("association_status") not in allowed_statuses or
                    type(glyph_row.get("visible_changed_pixels")) is not int or glyph_row["visible_changed_pixels"] <= 0):
                raise ValueError("artifact report glyph identity/visibility evidence differs from annotation")
            proof_sha = _sha(glyph_row.get("visibility_proof_sha256"), "artifact visibility proof")
            _sha(glyph_row.get("rendered_rgb_sha256"), "rendered RGB hash")
            _sha(glyph_row.get("ablated_rgb_sha256"), "ablated RGB hash")
            validated_glyphs.append({
                "kind": "legend_symbol", "source_id": source_id,
                "scene_sha256": identity["scene_sha256"], "annotation_sha256": annotation_sha,
                "glyph_id": glyph_row["glyph_id"], "source_reference": glyph_row["source_reference"],
                "center": center, "glyph_box": box,
                "association_status": glyph_row["association_status"],
                "artifact_report_verified": True, "visibility_proof_sha256": proof_sha,
                "visible_changed_pixels": glyph_row["visible_changed_pixels"],
            })
        overlay = truth_row.get("overlay")
        if (not isinstance(overlay, dict) or overlay.get("split") != "train" or
                overlay.get("scene_id") != identity["scene_id"] or
                overlay.get("scene_sha256") != identity["scene_sha256"] or
                overlay.get("status") != "review_only" or overlay.get("materialized") is not True or
                overlay.get("optimization_allowed") is not False):
            raise ValueError("source overlay sidecar is incomplete or not review-only")
        overlay_center = overlay.get("center")
        if (not isinstance(overlay_center, list) or len(overlay_center) != 2 or
                any(type(value) is not int for value in overlay_center) or
                not (0 <= overlay_center[0] < annotation["canvas"]["width"] and
                     0 <= overlay_center[1] < annotation["canvas"]["height"])):
            raise ValueError("source overlay center is invalid")
        derived_by_source[source_id] = {"annotation_sha256": annotation_sha,
            "positive_truths": positives, "visible_glyphs": validated_glyphs,
            "generic_overlays": [{"center": overlay_center}], "scene_sha256": identity["scene_sha256"]}
        inventory_rows.append({"source_id": source_id, "case_id": identity["case_id"],
            "image_sha256": identity["image_sha256"], "scene_sha256": identity["scene_sha256"],
            "annotation_sha256": annotation_sha, "positive_count": len(positives),
            "positive_ids_sha256": _hash(_canonical(sorted(item["truth_id"] for item in positives))),
            "glyph_count": len(validated_glyphs),
            "glyph_ids_sha256": _hash(_canonical(sorted(item["glyph_id"] for item in validated_glyphs))),
            "overlay_center": overlay_center})
        captured_event_panels = captured.get("panels")
        if (captured.get("status") != "completed" or captured.get("failure") not in (None, "") or
                captured.get("panel_count") != 1 or not isinstance(captured_event_panels, list) or
                len(captured_event_panels) != 1 or captured_event_panels[0].get("status") != "completed"):
            raise ValueError("capture source lacks exactly one completed panel")
        panel = captured_event_panels[0]
        event = event_map.get((source_id, panel.get("panel_id")))
        if (event is None or not isinstance(event.get("sha256"), str) or len(event["sha256"]) != 64 or
                panel.get("provenance", {}).get("source_sha256") != identity.get("image_sha256") or
                panel.get("provenance", {}).get("panel_image_sha256") != identity.get("image_sha256")):
            raise ValueError("capture event/panel/source identity join differs")
        if (artifact.get("status") != "completed" or artifact.get("scene_id") != identity.get("scene_id") or
                artifact.get("scene_sha256") != identity.get("scene_sha256") or
                artifact.get("panel_id") != panel.get("panel_id") or
                artifact.get("panel_result_sha256") != event["sha256"]):
            raise ValueError("artifact report source/panel identity join differs")
        if source is not None and source_id == source.get("source_id"):
            if any(source.get(key) != identity.get(identity_key) for key, identity_key in (
                    ("scene_id", "scene_id"), ("scene_sha256", "scene_sha256"),
                    ("source_raster_sha256", "image_sha256"))):
                raise ValueError("selected source differs from its independent identity row")
            if (source.get("panel_id") != panel.get("panel_id") or
                    source.get("panel_result_sha256") != event["sha256"] or
                    source.get("candidate_count") != panel.get("marker_count")):
                raise ValueError("selected panel differs from its capture report identity")

    if source is None:
        inventory_rows.sort(key=lambda row: row["source_id"])
        return derived_by_source, inventory_rows

    selected_id = source.get("source_id")
    identity = identities.get(selected_id)
    if identity is None:
        raise ValueError("selected source is absent from the full identity inventory")
    selected = derived_by_source[selected_id]
    if source.get("annotation_sha256") != selected["annotation_sha256"]:
        raise ValueError("selected annotation SHA differs from complete truth sidecar")
    positive_truths = selected["positive_truths"]
    visible_glyphs = selected["visible_glyphs"]
    generic_overlays = selected["generic_overlays"]
    annotation_sha = selected["annotation_sha256"]
    inventory_rows.sort(key=lambda row: row["source_id"])
    full_inventory_sha = _hash(_canonical(inventory_rows))
    inventory = {
        "schema": "graphreader.marker-truth-inventory-binding.v1",
        "complete_receipt_sha256": FULL_CAPTURE_COMPLETE_SHA256,
        "truth_sidecar_sha256": _hash(truth_sidecar_bytes),
        "identity_metadata_sha256": IDENTITY_METADATA_SHA256,
        "capture_report_sha256": FULL_CAPTURE_REPORT_SHA256,
        "artifact_report_sha256": ARTIFACT_REPORT_SHA256,
        "source_count": len(inventory_rows), "full_source_inventory_sha256": full_inventory_sha,
        "source_id": selected_id, "case_id": identity["case_id"],
        "image_sha256": identity["image_sha256"], "scene_sha256": identity["scene_sha256"],
        "annotation_sha256": annotation_sha,
        "positive_truth_count": len(positive_truths),
        "positive_truth_ids_sha256": _hash(_canonical(sorted(item["truth_id"] for item in positive_truths))),
        "visible_glyph_count": len(visible_glyphs),
        "visible_glyph_ids_sha256": _hash(_canonical(sorted(item["glyph_id"] for item in visible_glyphs))),
        "overlay_count": len(generic_overlays), "overlay_center": overlay_center,
    }
    inventory["inventory_binding_sha256"] = _hash(_canonical(inventory))
    return positive_truths, visible_glyphs, generic_overlays, inventory


def materialize_bound_full_capture_panel(*, source: dict[str, Any], panel_capture_bytes: bytes,
                                         source_raster_bytes: bytes, panel_raster_bytes: bytes,
                                         complete_receipt_bytes: bytes, identity_metadata_bytes: bytes,
                                         capture_report_bytes: bytes, truth_sidecar_bytes: bytes,
                                         artifact_report_bytes: bytes,
                                         project_root: Path) -> dict[str, Any]:
    """Join one train source using only exhaustively bound sidecar/report inventories.

    No caller-supplied positive, glyph, or overlay list is accepted. The labels
    are derived from the complete hash-bound truth sidecar and checked against
    the full 64-source identity, capture, and artifact reports.
    """
    source_closure = verify_source_bindings(project_root)
    source_closure_sha256 = _hash(_canonical(source_closure))
    classify_annotations = _load_bound_classifier(project_root, source_closure)
    positive_truths, visible_glyphs, generic_overlays, inventory = _derive_bound_inventories(
        source=source, complete_receipt_bytes=complete_receipt_bytes,
        identity_metadata_bytes=identity_metadata_bytes, capture_report_bytes=capture_report_bytes,
        truth_sidecar_bytes=truth_sidecar_bytes, artifact_report_bytes=artifact_report_bytes,
        classify_annotations=classify_annotations)
    if source.get("source_group_id") != source.get("source_id"):
        raise ValueError("source_group_id must be derived from the bound source identity")
    panel = json.loads(panel_capture_bytes, object_pairs_hook=_strict_object)
    evidence = _full_capture_candidate_evidence(panel, panel_capture_bytes, source)
    result = _materialize_source_core(source=source, panel_result_bytes=panel_capture_bytes,
        source_raster_bytes=source_raster_bytes, panel_raster_bytes=panel_raster_bytes,
        candidate_evidence=evidence, positive_truths=positive_truths,
        visible_glyphs=visible_glyphs, generic_overlays=generic_overlays)
    result["sampler_input_format"] = "full_capture_panel_inline_v1"
    result["source_closure_sha256"] = source_closure_sha256
    result["grouping_basis"] = "source_id_only; participant/study independence is unverified"
    result["truth_inventory_binding"] = inventory
    result["limitations"].append(
        "Sampler tensors are read inline from hash-bound panel bytes; adapter-generated evidence envelopes are not independent capture files.")
    return result


def _materialize_source_core(*, source: dict[str, Any], panel_result_bytes: bytes,
                             source_raster_bytes: bytes, panel_raster_bytes: bytes,
                             candidate_evidence: dict[str, dict[str, bytes]],
                             positive_truths: list[dict[str, Any]],
                             visible_glyphs: list[dict[str, Any]],
                             generic_overlays: list[dict[str, Any]]) -> dict[str, Any]:
    """Internal join core; callers must enter through bound inventory validation."""
    required_source = {
        "source_id", "source_group_id", "split", "scene_id", "scene_sha256",
        "annotation_sha256", "source_raster_sha256", "panel_id", "panel_image_sha256",
        "panel_result_sha256", "frame_f32le_sha256", "width", "height",
        "capture_report_sha256", "capture_complete_sha256", "capture_binding_sha256",
        "builder_binding_sha256", "identity_metadata_sha256", "association_report_sha256", "artifact_report_sha256",
        "capture_status", "panel_status_counts", "candidate_count",
    }
    if not isinstance(source, dict) or set(source) != required_source:
        raise ValueError("source binding fields differ from the frozen schema")
    if (source["split"] != "train" or not isinstance(source["source_group_id"], str) or
            not source["source_group_id"]):
        raise ValueError("only a fixed train source group is accepted")
    if (source["capture_report_sha256"] != FULL_CAPTURE_REPORT_SHA256 or
            source["capture_complete_sha256"] != FULL_CAPTURE_COMPLETE_SHA256 or
            source["capture_binding_sha256"] != FULL_CAPTURE_BINDING_SHA256 or
            source["builder_binding_sha256"] != CORPUS_BUILDER_BINDING_SHA256 or
            source["identity_metadata_sha256"] != IDENTITY_METADATA_SHA256 or
            source["association_report_sha256"] != ASSOCIATION_REPORT_SHA256 or
            source["artifact_report_sha256"] != ARTIFACT_REPORT_SHA256):
        raise ValueError("source reports differ from the fixed review-only evidence")
    if (type(source["width"]) is not int or type(source["height"]) is not int or
            source["width"] <= 0 or source["height"] <= 0):
        raise ValueError("source dimensions are invalid")
    for name in ("scene_sha256", "annotation_sha256", "source_raster_sha256",
                 "panel_image_sha256", "panel_result_sha256", "frame_f32le_sha256",
                 "capture_report_sha256", "capture_complete_sha256", "capture_binding_sha256",
                 "builder_binding_sha256", "identity_metadata_sha256", "association_report_sha256",
                 "artifact_report_sha256"):
        _sha(source[name], name)
    if (_hash(source_raster_bytes) != source["source_raster_sha256"] or
            _hash(panel_raster_bytes) != source["panel_image_sha256"]):
        raise ValueError("source or panel raster bytes differ from pinned hashes")
    if (_png_dimensions(source_raster_bytes) != (source["width"], source["height"]) or
            _png_dimensions(panel_raster_bytes) != (source["width"], source["height"])):
        raise ValueError("raster dimensions differ from the bound source/panel dimensions")
    if source["source_raster_sha256"] != source["panel_image_sha256"]:
        raise ValueError("this v1 bridge only accepts the fixed full-source identity panel")
    counts = source["panel_status_counts"]
    if (source["capture_status"] != "completed" or not isinstance(counts, dict) or
            set(counts) != {"completed", "failed", "not_processed"} or
            any(type(value) is not int or value < 0 for value in counts.values()) or
            counts["completed"] != 1 or counts["failed"] != 0 or counts["not_processed"] != 0):
        raise ValueError("candidate labels require one fully completed panel capture")
    panel = json.loads(panel_result_bytes, object_pairs_hook=_strict_object)
    if _hash(panel_result_bytes) != source["panel_result_sha256"]:
        raise ValueError("panel result bytes differ from their bound SHA-256")
    if (not isinstance(panel, dict) or panel.get("source_id") != source["source_id"] or
            panel.get("panel_id") != source["panel_id"] or
            panel.get("panel_sha256") != source["panel_image_sha256"]):
        raise ValueError("panel result identity differs from the source binding")
    raw_candidates = panel.get("center_markers")
    if not isinstance(raw_candidates, list):
        raise ValueError("panel result lacks the candidate inventory")
    if type(source["candidate_count"]) is not int or source["candidate_count"] != len(raw_candidates):
        raise ValueError("bound candidate count differs from panel result")
    symbol_input_ids = panel.get("symbol_input_ids", [])
    symbol_crop_input_ids = panel.get("symbol_crop_input_ids", [])
    if (not isinstance(symbol_input_ids, list) or not isinstance(symbol_crop_input_ids, list) or
            any(not isinstance(value, str) for value in symbol_input_ids + symbol_crop_input_ids)):
        raise ValueError("symbol-input identity lists are malformed")
    symbol_ids = set(symbol_input_ids) | set(symbol_crop_input_ids)
    panel_bounds = panel.get("original_pixel_content_bounds", {})
    if not isinstance(panel_bounds, dict):
        raise ValueError("original-pixel content bounds must be a marker-ID map")

    candidates: list[dict[str, Any]] = []
    seen_ids: set[str] = set()
    for raw in raw_candidates:
        if not isinstance(raw, dict) or not isinstance(raw.get("marker_id"), str):
            raise ValueError("panel candidate identity is malformed")
        candidate_id = raw["marker_id"]
        if not candidate_id or candidate_id in seen_ids:
            raise ValueError("candidate IDs must be unique within a panel")
        seen_ids.add(candidate_id)
        center_obj = raw.get("center")
        center = _point([center_obj.get("x"), center_obj.get("y")] if isinstance(center_obj, dict) else None,
                        source["width"], source["height"], "detector candidate center")
        radius = raw.get("radius")
        if isinstance(radius, bool) or not isinstance(radius, (int, float)) or not math.isfinite(radius) or radius <= 0:
            raise ValueError("detector candidate radius is invalid")
        candidate_key = hashlib.sha256(
            f"{source['source_id']}\0{source['panel_id']}\0{candidate_id}".encode("utf-8")).hexdigest()
        candidates.append({
            "candidate_id": candidate_id, "candidate_key_sha256": candidate_key,
            "sampler_case_id": candidate_key[:40], "center": center, "radius": float(radius),
            "local_content_bounds": panel_bounds.get(candidate_id),
            "detector_candidate": True, "symbol_input": candidate_id in symbol_ids,
        })
    if set(candidate_evidence) != seen_ids:
        raise ValueError("sampler evidence inventory must match every detector candidate exactly")

    tensor_rows: dict[str, dict[str, Any]] = {}
    frame_sha = source["frame_f32le_sha256"]
    _sha(frame_sha, "frame_f32le_sha256")
    for candidate in candidates:
        evidence_pair = candidate_evidence[candidate["candidate_id"]]
        if not isinstance(evidence_pair, dict) or set(evidence_pair) != {"local", "wide"}:
            raise ValueError("each candidate requires exactly local and wide sampler evidence")
        evidence_raw = {}
        for view in ("local", "wide"):
            reference = evidence_pair[view]
            if not isinstance(reference, dict) or set(reference) != {"raw_bytes", "sha256"}:
                raise ValueError(f"{view} sampler evidence requires raw bytes and an external SHA-256")
            expected_evidence_sha = _sha(reference["sha256"], f"{view} evidence SHA-256")
            if not isinstance(reference["raw_bytes"], bytes) or _hash(reference["raw_bytes"]) != expected_evidence_sha:
                raise ValueError(f"{view} sampler evidence raw bytes differ from the bound SHA-256")
            evidence_raw[view] = reference["raw_bytes"]
        local, local_sha, provenance = _verify_evidence(
            evidence_raw["local"], source=source, candidate=candidate, view="local",
            expected_frame_sha256=frame_sha, expected_ocr_mask_sha256=None,
            expected_artifact_mask_sha256=None)
        wide, wide_sha, wide_provenance = _verify_evidence(
            evidence_raw["wide"], source=source, candidate=candidate, view="wide",
            expected_frame_sha256=frame_sha, expected_ocr_mask_sha256=provenance["ocr_mask_snapshot_sha256"],
            expected_artifact_mask_sha256=provenance["artifact_mask_snapshot_sha256"])
        tensor_rows[candidate["candidate_id"]] = {
            "local_bytes": local, "local_sha256": local_sha,
            "wide_bytes": wide, "wide_sha256": wide_sha,
            "local_evidence_sha256": evidence_pair["local"]["sha256"],
            "wide_evidence_sha256": evidence_pair["wide"]["sha256"],
            "mask_snapshots": provenance,
        }

    truths = []
    truth_ids: set[str] = set()
    if not isinstance(positive_truths, list) or not isinstance(visible_glyphs, list) or not isinstance(generic_overlays, list):
        raise ValueError("truth, glyph, and overlay inventories must be lists")
    for item in positive_truths:
        if not isinstance(item, dict) or item.get("coordinate_space") != "original_source_pixels":
            raise ValueError("positive truth must be an explicit classified marker record")
        connection = item.get("connection")
        if connection is None:
            connection = {"connected_positive": "connected", "isolated_positive": "isolated"}.get(item.get("bin"))
        if connection not in ("connected", "isolated"):
            raise ValueError("positive truth must preserve connected/isolated annotation status")
        truth_id = item.get("truth_id")
        if not isinstance(truth_id, str) or not truth_id or truth_id in truth_ids:
            raise ValueError("positive truth IDs must be unique and nonempty")
        truth_ids.add(truth_id)
        if item.get("source_id") != source["source_id"] or item.get(
                "annotation_sha256") != source["annotation_sha256"]:
            raise ValueError("positive marker annotation provenance differs from the source")
        truths.append({"kind": "marker", "truth_id": truth_id,
                       "center": _point(item.get("center"), source["width"], source["height"], "positive marker") ,
                       "connection": connection})
    glyphs = []
    glyph_ids: set[str] = set()
    for item in visible_glyphs:
        if not isinstance(item, dict) or item.get("kind") != "legend_symbol":
            raise ValueError("only explicit legend_symbol glyphs can enter review matching")
        if item.get("association_status") not in {
                "matched_artifact_review_only", "unmatched_review_only",
                "review_ambiguous", "review_overlay_conflict"}:
            raise ValueError("visible glyph has an unknown or missing artifact-report status")
        if item.get("source_id") != source["source_id"] or item.get(
                "scene_sha256") != source["scene_sha256"] or item.get(
                "annotation_sha256") != source["annotation_sha256"]:
            raise ValueError("legend glyph annotation provenance differs from the source")
        glyph_id = item.get("glyph_id")
        if not isinstance(glyph_id, str) or not glyph_id or glyph_id in glyph_ids:
            raise ValueError("legend glyph IDs must be unique and nonempty")
        glyph_ids.add(glyph_id)
        if item.get("artifact_report_verified") is True:
            proof_sha = _sha(item.get("visibility_proof_sha256"), "artifact-report visibility proof")
            if type(item.get("visible_changed_pixels")) is not int or item["visible_changed_pixels"] <= 0:
                raise ValueError("hash-bound artifact report lacks visible glyph pixel evidence")
        else:
            proof_sha = _verify_visibility_proof(item, source)
        glyphs.append({"kind": "artifact", "truth_id": glyph_id,
                       "center": _point(item.get("center"), source["width"], source["height"], "legend glyph"),
                       "proof_sha256": proof_sha, "source_reference": item.get("source_reference"),
                       "review_label_eligible": item.get("association_status") == "matched_artifact_review_only"})

    overlays = []
    for item in generic_overlays:
        if not isinstance(item, dict):
            raise ValueError("generic overlay entries must be explicit points")
        overlays.append(_point(item.get("center"), source["width"], source["height"], "overlay"))

    candidate_points = [row["center"] for row in candidates]
    duplicate_indices: set[int] = set()
    by_point: dict[tuple[float, float], list[int]] = {}
    for index, point in enumerate(candidate_points):
        by_point.setdefault(point, []).append(index)
    for indexes in by_point.values():
        if len(indexes) > 1:
            duplicate_indices.update(indexes)
    capture_complete = (source["capture_status"] == "completed" and counts["failed"] == 0 and
                        counts["not_processed"] == 0)
    overlay_indices = {
        index for index, candidate in enumerate(candidates)
        if candidate["symbol_input"] or any(_distance(candidate["center"], overlay) <= MATCH_TOLERANCE_PX
                                             for overlay in overlays)
    }
    eligible = [index for index in range(len(candidates)) if capture_complete and
                index not in duplicate_indices and index not in overlay_indices]
    joint_truths = truths + glyphs
    links_by_candidate: dict[int, list[int]] = {}
    links_by_truth: dict[int, list[int]] = {}
    for candidate_index in eligible:
        for truth_index, truth in enumerate(joint_truths):
            if _distance(candidates[candidate_index]["center"], truth["center"]) <= MATCH_TOLERANCE_PX:
                links_by_candidate.setdefault(candidate_index, []).append(truth_index)
                links_by_truth.setdefault(truth_index, []).append(candidate_index)
    ambiguous_candidates: set[int] = set()
    ambiguous_truths: set[int] = set()
    for candidate_index, truth_indexes in links_by_candidate.items():
        if len(truth_indexes) > 1:
            ambiguous_candidates.add(candidate_index)
            ambiguous_truths.update(truth_indexes)
        for truth_index in truth_indexes:
            if len(links_by_truth.get(truth_index, [])) > 1:
                ambiguous_candidates.add(candidate_index)
                ambiguous_truths.add(truth_index)
                ambiguous_candidates.update(links_by_truth[truth_index])

    rows = []
    status_counts: Counter[str] = Counter()
    for index, candidate in enumerate(candidates):
        label = None
        truth_id = None
        glyph_id = None
        visibility_sha = None
        if not capture_complete:
            status = "unknown_capture_incomplete"
        elif candidate["symbol_input"]:
            status = "excluded_symbol_input"
        elif index in overlay_indices:
            status = "excluded_generic_overlay"
        elif index in duplicate_indices:
            status = "unknown_duplicate_candidate_center"
        elif index in ambiguous_candidates:
            status = "unknown_ambiguous_collision"
        elif len(links_by_candidate.get(index, [])) == 1:
            truth = joint_truths[links_by_candidate[index][0]]
            if truth["kind"] == "marker":
                status = f"review_positive_{truth['connection']}"
                label, truth_id = "marker_positive_review_only", truth["truth_id"]
            elif truth["review_label_eligible"]:
                status = "review_visible_legend_glyph"
                label, glyph_id, visibility_sha = "artifact_candidate_review_only", truth["truth_id"], truth["proof_sha256"]
            else:
                status = "unknown_unmatched_visible_glyph"
        else:
            status = "unknown_unmatched"
        status_counts[status] += 1
        tensors = tensor_rows[candidate["candidate_id"]]
        rows.append({
            "row_id": candidate["candidate_key_sha256"],
            "source_id": source["source_id"], "source_group_id": source["source_group_id"],
            "split": "train", "panel_id": source["panel_id"],
            "candidate_id": candidate["candidate_id"], "center": list(candidate["center"]),
            "radius": candidate["radius"], "coordinate_space": "original_source_pixels",
            "status": status, "review_label": label, "truth_id": truth_id,
            "glyph_id": glyph_id, "visibility_proof_sha256": visibility_sha,
            "local_tensor_sha256": tensors["local_sha256"],
            "wide_tensor_sha256": tensors["wide_sha256"],
            "local_evidence_sha256": tensors["local_evidence_sha256"],
            "wide_evidence_sha256": tensors["wide_evidence_sha256"],
            "mask_snapshots": tensors["mask_snapshots"],
            "training_admitted": False,
        })
    if len(rows) != len(candidates) or len({row["row_id"] for row in rows}) != len(rows):
        raise ValueError("candidate row accounting or stable row identity is not unique")
    return {
        "schema": SCHEMA, "status": "completed_review_only",
        "source_id": source["source_id"], "source_group_id": source["source_group_id"],
        "split": "train", "source_raster_sha256": source["source_raster_sha256"],
        "panel_image_sha256": source["panel_image_sha256"],
        "panel_result_sha256": source["panel_result_sha256"],
        "candidate_count": len(rows), "candidate_status_counts": dict(sorted(status_counts.items())),
        "rows": rows,
        "tensor_bytes_by_candidate": tensor_rows,
        "matched_positive_review_rows": sum(row["review_label"] == "marker_positive_review_only" for row in rows),
        "visible_legend_review_rows": sum(row["review_label"] == "artifact_candidate_review_only" for row in rows),
        "unknown_or_excluded_rows": sum(row["review_label"] is None for row in rows),
        "trainable_rows": 0, "negative_training_rows": 0,
        "training_approved": False, "optimizer_steps": 0,
        "truth_scope": "synthetic_train_generator_annotations_only",
        "dev_input_supplied": False, "private_input_supplied": False,
        "sealed_input_supplied": False,
        "limitations": [
            "Raw sampler outputs are authenticated by panel and frame-plane hashes; decoder/factory provenance is not independently reconstructed here.",
            "The local and wide arrays are pre-normalization MarkerPatchExtractor outputs; classifier normalization is not applied or verified.",
            "Visible glyph inventories come from a hash-bound exhaustive artifact report; ablation pixels are not re-rendered by this bridge.",
            "All labels are review-only, and unmatched candidates remain unknown rather than negative.",
        ],
    }
