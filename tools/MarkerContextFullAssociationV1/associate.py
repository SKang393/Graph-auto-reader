# SPDX-License-Identifier: Apache-2.0
"""Fixed E-drive, v5-full train-only association runner. Never admits supervision."""
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import struct
import sys
import types

ROOT = Path(r"E:\Graph-auto-reader")
RUNS = ROOT / "artifacts/goal22-runs"
PACKAGE = ROOT / "tools/MarkerContextFullAssociationV1"
PROTOCOL_PATH = PACKAGE / "protocol.json"
PROTOCOL_SHA256 = "4e351d67be11ec23cb4f91ff7f5cb23e15de125048701dbea885f17e0ae44d7b"
LAUNCH_PROTOCOL_PATH = PACKAGE / "launcher-protocol.json"
LAUNCH_BINDING_PATH = RUNS / "marker-context-train-association-v5-full-launch-binding.json"
ASSOCIATION_PATH = PACKAGE / "association.py"
ASSOCIATION_SHA256 = "90eb3f1017b30c47f86361975408931ab86f76bd093864d89ea91ce37aebe991"
CAPTURE_BINDING_PATH = ROOT / "artifacts/goal22-runs/marker-context-bound-input-full-preflight-v4/capture-binding.json"
CAPTURE_BINDING_SHA256 = "792d6ec1bc59f16f92cb03adae1b59d2ca0d35ba85218f6eb9a9218d2594327b"
LABELER_PATH = ROOT / "ml/synthetic/marker_context_catalog_v1/labels.py"
LABELER_SHA256 = "27a36dc116e0424c0c932683c5f16d4650c0334b4362743cfd14741da48699e1"
OUTPUT = RUNS / "marker-context-train-association-v9-fake-sidecar-test"
LAUNCH_BINDING_PATH = RUNS / "marker-context-train-association-v9-fake-launch-binding.json"
REPARSE = 0x400


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def digest_file(path):
    value = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def _win_attrs(path):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetFileAttributesW.argtypes = [ctypes.c_wchar_p]
    kernel.GetFileAttributesW.restype = ctypes.c_uint32
    return kernel.GetFileAttributesW(str(path))


def check_path(path, *, must_exist=True):
    path = Path(path)
    if not path.is_absolute() or not path.resolve(strict=False).is_relative_to(ROOT):
        raise ValueError("All paths must remain inside E project")
    if os.name == "nt":
        cursor = Path(path.anchor)
        for part in path.parts[1:]:
            cursor /= part
            flags = _win_attrs(cursor)
            if flags == 0xFFFFFFFF:
                if must_exist:
                    raise OSError(ctypes.get_last_error(), f"Cannot inspect bound path: {cursor}")
                continue
            if flags & REPARSE:
                raise ValueError(f"Reparse-point path denied: {cursor}")
    if must_exist and not path.is_file():
        raise FileNotFoundError(path)
    return path


def locked_bytes(path):
    path = check_path(path)
    if os.name != "nt":
        raise OSError("Windows read lock denying writes/deletes is required")
    import msvcrt
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                                   ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    handle = kernel.CreateFileW(str(path), 0x80000000, 1, None, 3, 0x00200080, None)
    if handle == ctypes.c_void_p(-1).value:
        raise OSError(ctypes.get_last_error(), f"Cannot lock input: {path}")
    try:
        fd = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
    except BaseException:
        kernel.CloseHandle(ctypes.c_void_p(handle))
        raise
    with os.fdopen(fd, "rb") as stream:
        return stream.read()


def _load_json(raw, label):
    try:
        return json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{label} is not valid JSON") from error


def _safe_relative(base, filename):
    if not isinstance(filename, str) or not filename or Path(filename).name != filename or "/" in filename or "\\" in filename:
        raise ValueError("Bound artifact filename must be a basename")
    return check_path(base / filename)


def _same_identity(row, expected):
    for key in ("source_id", "scene_id", "scene_sha256", "image_sha256", "seed", "case_id"):
        if row.get(key) != expected.get(key):
            raise ValueError(f"source identity differs for {key}")


def require_capture_complete(report):
    if (report.get("schema") != "graphreader.marker-context-bound-input-full-capture-report.v1" or
        report.get("source_count") != 64 or report.get("source_accounting_complete") is not True or
        report.get("status") not in ("completed_all_sources", "completed_with_source_failures") or
        report.get("training_approved") is not False or report.get("marker_classifier_model_loaded") is not False or
        report.get("classification_executed") is not False or report.get("truth_content_read") is not False or
        report.get("private_input_supplied") is not False or report.get("sealed_input_supplied") is not False or
        report.get("dev_input_supplied") is not False or report.get("optimizer_steps") != 0):
        raise ValueError("capture is incomplete or outside the preregistered diagnostic scope")
    if report.get("model_inference_started") is not True or report.get("initialization_probe_completed") is not True:
        raise ValueError("capture inference provenance is incomplete")
    return True


def persist_freeze_before_truth(path, freeze_raw, truth_reader):
    """Persist and fsync the preregistered capture/rule identity before sidecar open."""
    with Path(path).open("xb") as stream:
        stream.write(freeze_raw)
        stream.flush()
        os.fsync(stream.fileno())
    return truth_reader()


def _marker_center(value, width, height):
    if not isinstance(value, dict) or set(value) not in ({"x", "y"}, {"x", "y", "is_finite"}) or \
       ("is_finite" in value and value["is_finite"] is not True):
        raise ValueError("marker center must use the production x/y contract")
    x, y = value["x"], value["y"]
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) for v in (x, y)) or \
       not (0 <= x < width and 0 <= y < height):
        raise ValueError("marker center outside authenticated panel")
    return [float(x), float(y)]


def _verify_tensor(values, expected_sha256):
    if not isinstance(values, list) or len(values) != 32 * 32:
        raise ValueError("captured marker tensor must be 32x32 single-channel float32")
    packed = bytearray(len(values) * 4)
    for index, value in enumerate(values):
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not 0 <= value <= 1:
            raise ValueError("captured marker tensor contains invalid ink probability")
        try:
            struct.pack_into("<f", packed, index * 4, float(value))
        except (OverflowError, struct.error) as error:
            raise ValueError("captured marker tensor cannot be encoded as float32") from error
    if digest(packed) != expected_sha256:
        raise ValueError("captured marker tensor hash differs from serialized values")


def _fake_truth_fixture(source_ids):
    if not isinstance(source_ids, list) or len(source_ids) != 64 or \
       any(not isinstance(source_id, str) or not source_id for source_id in source_ids) or \
       len(set(source_ids)) != 64:
        raise ValueError("fake-sidecar test requires 64 unique captured source IDs")
    return {"schema": "graphreader.marker-context-association-fake-sidecar.v1",
            "scope": "integration-test-only-empty-truth-no-source-annotations",
            "training_approved": False,
            "sources": [{"source_id": source_id, "markers": [], "overlays": []} for source_id in source_ids]}


def _validate_capture_and_extract(report, report_raw, binding, binding_raw, capture_root, capture_protocol,
                                  association_protocol, identity, raster_manifest, complete, association_module):
    if digest(report_raw) != report.get("_expected_report_sha256"):
        raise ValueError("capture report digest differs")
    if digest(binding_raw) != CAPTURE_BINDING_SHA256:
        raise ValueError("fixed capture binding digest differs")
    if binding.get("schema") != "graphreader.marker-context-bound-input-full-capture-binding.v1" or \
       binding.get("protocol_sha256") != association_protocol["capture_protocol_sha256"] or \
       binding.get("runtime_binding_sha256") != association_protocol["capture_runtime_binding_sha256"] or \
       binding.get("capture_output_path") != association_protocol["capture_output_path"] or \
       not isinstance(binding.get("source_images"), list) or len(binding["source_images"]) != 64:
        raise ValueError("fixed capture descriptor identity differs")
    if (report.get("capture_binding_sha256") != CAPTURE_BINDING_SHA256 or
        report.get("protocol_sha256") != association_protocol["capture_protocol_sha256"] or
        report.get("parent_binding_sha256") != binding.get("base_binding_sha256") or
        report.get("runtime_binding_sha256") != association_protocol["capture_runtime_binding_sha256"]):
        raise ValueError("capture descriptor/runtime binding differs")
    require_capture_complete(report)

    if complete.get("schema") != "marker-context-train-corpus-complete-v5" or complete.get("mode") != "full" or \
       complete.get("training_approved") is not False or complete.get("source_count") != 64 or \
       complete.get("truth_sha256") != association_protocol["truth_sha256"] or \
       complete.get("identity_sha256") != association_protocol["identity_sha256"] or \
       complete.get("raster_manifest_sha256") != association_protocol["raster_manifest_sha256"]:
        raise ValueError("v5 completion metadata differs")
    if identity.get("schema") != "marker-context-train-identity-v5" or identity.get("mode") != "full" or \
       identity.get("training_approved") is not False or len(identity.get("sources", [])) != 64:
        raise ValueError("v5 identity manifest differs")
    if raster_manifest.get("schema") != "marker-context-train-raster-v5" or raster_manifest.get("mode") != "full" or \
       raster_manifest.get("training_approved") is not False or len(raster_manifest.get("sources", [])) != 64:
        raise ValueError("v5 raster manifest differs")
    protocol_sources = capture_protocol.get("sources")
    if not isinstance(protocol_sources, list) or len(protocol_sources) != 64:
        raise ValueError("fixed full-capture protocol lacks 64 source identities")
    protocol_by_id = {row.get("source_id"): row for row in protocol_sources}
    identity_by_id = {row.get("source_id"): row for row in identity["sources"]}
    raster_by_id = {row.get("source_id"): row for row in raster_manifest["sources"]}
    binding_by_id = {row.get("source_id"): row for row in binding.get("source_images", [])}
    report_sources = report.get("sources")
    if any(len(rows) != 64 for rows in (protocol_by_id, identity_by_id, raster_by_id, binding_by_id)) or \
       not isinstance(report_sources, list) or len(report_sources) != 64:
        raise ValueError("source identity inventory is incomplete or duplicated")
    if set(protocol_by_id) != set(identity_by_id) or set(protocol_by_id) != set(raster_by_id) or \
       set(protocol_by_id) != set(binding_by_id):
        raise ValueError("protocol/corpus/binding source IDs differ")

    for source_id, expected in protocol_by_id.items():
        ident, raster_row, bound_source = identity_by_id[source_id], raster_by_id[source_id], binding_by_id[source_id]
        _same_identity(ident, expected)
        if raster_row.get("source_id") != source_id or raster_row.get("sha256") != expected.get("image_sha256") or \
           raster_row.get("width") != 1200 or raster_row.get("height") != 350:
            raise ValueError("raster source identity differs")
        if bound_source.get("sha256") != expected.get("image_sha256") or bound_source.get("width") != 1200 or \
           bound_source.get("height") != 350 or bound_source.get("path") != expected.get("path"):
            raise ValueError("capture source binding differs")
        raster_file = check_path(ROOT / expected["path"])
        if digest(locked_bytes(raster_file)) != expected["image_sha256"]:
            raise ValueError("authenticated raster bytes changed")
        capture_copy = _safe_relative(capture_root / "sources", Path(expected["path"]).name)
        if digest(locked_bytes(capture_copy)) != expected["image_sha256"]:
            raise ValueError("captured source copy differs from authenticated raster")

    source_rows = {row.get("source_id"): row for row in report_sources}
    if len(source_rows) != 64 or set(source_rows) != set(protocol_by_id):
        raise ValueError("capture report source rows are missing or duplicated")
    capture_candidates = {source_id: {"source_id": source_id, "capture_status": row.get("status"),
                                      "panel_status_counts": {"completed": 0, "failed": 0, "not_processed": 0},
                                      "failure_type_counts": {},
                                      "candidates": []}
                         for source_id, row in source_rows.items()}
    for source_id, row in source_rows.items():
        _same_identity(row, protocol_by_id[source_id])
        if row.get("status") not in ("completed", "failed") or type(row.get("panel_count")) is not int or \
           not isinstance(row.get("panels"), list) or row["panel_count"] != len(row["panels"]):
            raise ValueError("invalid source/panel capture accounting")
        source_failure = row.get("failure")
        if source_failure is not None:
            if not isinstance(source_failure, str) or not source_failure:
                raise ValueError("invalid source failure record")
            failure_type = source_failure.split(":", 1)[0]
            capture_candidates[source_id]["failure_type_counts"][failure_type] = \
                capture_candidates[source_id]["failure_type_counts"].get(failure_type, 0) + 1
        for panel in row["panels"]:
            status = panel.get("status")
            if status not in capture_candidates[source_id]["panel_status_counts"]:
                raise ValueError("unknown panel status")
            capture_candidates[source_id]["panel_status_counts"][status] += 1
            if status == "failed":
                failure_type = panel.get("error_type")
                if not isinstance(failure_type, str) or not failure_type:
                    raise ValueError("failed panel lacks failure type")
                capture_candidates[source_id]["failure_type_counts"][failure_type] = \
                    capture_candidates[source_id]["failure_type_counts"].get(failure_type, 0) + 1

    events = report.get("events")
    if not isinstance(events, list) or report.get("event_count") != len(events):
        raise ValueError("capture event inventory differs")
    seen_panel_events = set()
    event_bytes_total = 0
    for event_ref in events:
        source_id, panel_id = event_ref.get("source_id"), event_ref.get("panel_id")
        if source_id not in source_rows or not isinstance(panel_id, str):
            raise ValueError("event source/panel identity invalid")
        event_key = (source_id, panel_id)
        if event_key in seen_panel_events:
            raise ValueError("duplicate panel event")
        seen_panel_events.add(event_key)
        source_row = source_rows[source_id]
        panel_row = next((p for p in source_row["panels"] if p.get("panel_id") == panel_id), None)
        if panel_row is None or panel_row.get("status") != "completed":
            raise ValueError("event does not identify a completed panel")
        event_path = _safe_relative(capture_root / "panels", event_ref.get("file"))
        event_raw = locked_bytes(event_path)
        if digest(event_raw) != event_ref.get("sha256"):
            raise ValueError("panel event content hash differs")
        event_bytes_total += len(event_raw)
        event = _load_json(event_raw, "panel event")
        expected = protocol_by_id[source_id]
        if event.get("source_id") != source_id or event.get("case_id") != expected["case_id"] or \
           event.get("scene_id") != expected["scene_id"] or event.get("scene_sha256") != expected["scene_sha256"] or \
           event.get("seed") != expected["seed"] or event.get("panel_id") != panel_id:
            raise ValueError("panel event frozen input identity differs")
        provenance = event.get("provenance")
        if not isinstance(provenance, dict) or provenance.get("source_sha256") != expected["image_sha256"] or \
           provenance.get("panel_image_sha256") != event.get("panel_sha256") or \
           provenance.get("source_width") != 1200 or provenance.get("source_height") != 350:
            raise ValueError("panel source provenance differs")
        if panel_row.get("provenance") != provenance:
            raise ValueError("event and report panel provenance differ")
        matrix = provenance.get("panel_to_source_matrix")
        forward = provenance.get("source_to_panel_matrix")
        crop = provenance.get("encoded_crop_source_pixels")
        if not isinstance(crop, dict) or any(isinstance(crop.get(key), bool) or
               not isinstance(crop.get(key), (int, float)) or not math.isfinite(crop[key])
               for key in ("x", "y", "width", "height")) or crop["width"] <= 0 or crop["height"] <= 0 or \
           crop["x"] < 0 or crop["y"] < 0 or crop["x"] + crop["width"] > 1200 or crop["y"] + crop["height"] > 350:
            raise ValueError("panel crop provenance invalid")
        want_matrix = [1, 0, crop["x"], 0, 1, crop["y"], 0, 0, 1]
        want_forward = [1, 0, -crop["x"], 0, 1, -crop["y"], 0, 0, 1]
        is_identity = crop["x"] == 0 and crop["y"] == 0 and crop["width"] == 1200 and crop["height"] == 350
        if matrix != want_matrix or forward != want_forward or provenance.get("identity") is not is_identity or \
           not isinstance(event.get("panel_sha256"), str) or len(event["panel_sha256"]) != 64 or \
           any(c not in "0123456789abcdef" for c in event["panel_sha256"]):
            raise ValueError("panel coordinate transform or hash invalid")
        sample_rows = event.get("sampled_local_and_experimental_wide_views")
        classifier_inputs = event.get("classifier_inputs")
        centers = event.get("center_markers")
        if not isinstance(sample_rows, list) or not isinstance(classifier_inputs, list) or not isinstance(centers, list):
            raise ValueError("panel candidate arrays missing")
        if panel_row.get("marker_count") != len(centers) or panel_row.get("sampler_view_count") != len(sample_rows):
            raise ValueError("event candidate counts differ from source panel ledger")
        by_id = {}
        for marker in classifier_inputs:
            mid = marker.get("marker_id")
            if not isinstance(mid, str) or not mid or mid in by_id:
                raise ValueError("classifier input identities invalid")
            by_id[mid] = marker
        if len(sample_rows) != len(classifier_inputs):
            raise ValueError("sampler/classifier input count differs")
        sample_ids = set()
        symbol_plot_ids, symbol_crop_ids = event.get("symbol_input_ids"), event.get("symbol_crop_input_ids")
        if not isinstance(symbol_plot_ids, list) or not isinstance(symbol_crop_ids, list) or \
           any(not isinstance(value, str) for value in symbol_plot_ids + symbol_crop_ids):
            raise ValueError("symbol input identities invalid")
        symbol_ids = set(symbol_plot_ids) | set(symbol_crop_ids)
        if len(symbol_plot_ids) != len(set(symbol_plot_ids)) or len(symbol_crop_ids) != len(set(symbol_crop_ids)):
            raise ValueError("duplicate symbol input identities")
        center_ids = set()
        for marker in centers:
            mid = marker.get("marker_id")
            if not isinstance(mid, str) or not mid or mid in center_ids:
                raise ValueError("detector marker identities invalid")
            center_ids.add(mid)
        if not center_ids.issubset(set(by_id)) or not symbol_ids.issubset(set(by_id)):
            raise ValueError("detector outputs are absent from classifier inputs")
        frame = event.get("frame")
        if not isinstance(frame, dict) or frame.get("width") != crop["width"] or frame.get("height") != crop["height"]:
            raise ValueError("captured frame dimensions differ from source crop")
        for sampled in sample_rows:
            marker = sampled.get("marker")
            if not isinstance(marker, dict):
                raise ValueError("sampled marker missing")
            mid = marker.get("marker_id")
            if mid not in by_id or mid in sample_ids:
                raise ValueError("sampler/classifier marker identity differs")
            sample_ids.add(mid)
            model_marker = by_id[mid]
            if marker.get("center") != model_marker.get("center") or marker.get("radius") != model_marker.get("radius"):
                raise ValueError("sampled center/radius differs from classifier input")
            center = marker.get("center")
            center_xy = _marker_center(center, crop["width"], crop["height"])
            if marker.get("coordinate_space") != "original_pixels":
                raise ValueError("candidate coordinate space differs")
            sx, sy = association_module.transform_panel_to_source(center_xy, matrix, 1200, 350)
            candidate_id = f"{source_id}/{panel_id}/{mid}"
            for hash_key in ("local_sha256", "wide_sha256"):
                val = sampled.get(hash_key)
                if not isinstance(val, str) or len(val) != 64 or any(c not in "0123456789abcdef" for c in val):
                    raise ValueError("sample tensor hash malformed")
            _verify_tensor(sampled.get("local"), sampled["local_sha256"])
            _verify_tensor(sampled.get("wide"), sampled["wide_sha256"])
            capture_candidates[source_id]["candidates"].append({
                "candidate_id": candidate_id,
                "panel_id": panel_id,
                "marker_id": mid,
                "center": [sx, sy],
                "symbol_input": mid in symbol_ids,
                "detector_candidate": mid in center_ids,
                "local_sha256": sampled["local_sha256"],
                "wide_sha256": sampled["wide_sha256"],
            })
        if sample_ids != set(by_id):
            raise ValueError("sampler did not preserve full classifier input order/inventory")
        successful_panel_ids = {row.get("panel_id") for row in source_row["panels"] if row.get("status") == "completed"}
        if (source_id, panel_id) not in seen_panel_events or panel_id not in successful_panel_ids:
            raise ValueError("completed panel missing event")

    for source_id, row in source_rows.items():
        completed_ids = {p.get("panel_id") for p in row["panels"] if p.get("status") == "completed"}
        actual_ids = {p for sid, p in seen_panel_events if sid == source_id}
        if actual_ids != completed_ids:
            raise ValueError("completed panel/event accounting mismatch")
        source_status_counts = report.get("source_status_counts", {})
        expected_status = "completed" if row["status"] == "completed" else "failed"
        if capture_candidates[source_id]["capture_status"] != expected_status:
            raise ValueError("source status differs")
    counts = report.get("source_status_counts", {})
    if counts.get("completed", 0) + counts.get("failed", 0) != 64 or \
       counts.get("completed") != sum(r.get("status") == "completed" for r in report_sources) or \
       counts.get("failed") != sum(r.get("status") == "failed" for r in report_sources):
        raise ValueError("source status denominator differs")
    if report.get("all_sources_succeeded") is not (counts.get("failed") == 0) or \
       report.get("input_image_sha256_join") != ";".join(row["sha256"] for row in binding["source_images"]):
        raise ValueError("capture success flag or fixed input join differs")
    panel_counts = report.get("panel_status_counts", {})
    summed_panels = {key: sum(s["panel_status_counts"][key] for s in capture_candidates.values())
                     for key in ("completed", "failed", "not_processed")}
    if panel_counts != summed_panels:
        raise ValueError("panel status denominator differs")
    if report.get("event_output_bytes") != event_bytes_total:
        raise ValueError("capture event byte denominator differs")

    return capture_candidates


def _load_pinned_module(name, source_path, source_bytes):
    value = types.ModuleType(name)
    value.__file__ = str(source_path)
    sys.modules[name] = value
    exec(compile(source_bytes, str(source_path), "exec", dont_inherit=True), value.__dict__)
    return value


def main():
    if os.name != "nt" or not sys.flags.isolated or not sys.dont_write_bytecode:
        raise ValueError("Use isolated Windows Python with -I -B")
    if len(sys.argv) != 5 or sys.argv[4] != "--fake-sidecar-self-test" or \
       any(len(value) != 64 or any(c not in "0123456789abcdef" for c in value) for value in sys.argv[1:4]):
        raise ValueError("This revision accepts only the fake-sidecar association self-test, never real truth")
    protocol_raw = locked_bytes(PROTOCOL_PATH)
    if digest(protocol_raw) != PROTOCOL_SHA256:
        raise ValueError("association protocol source hash changed")
    protocol = _load_json(protocol_raw, "association protocol")
    association_bytes = locked_bytes(ASSOCIATION_PATH)
    if digest(association_bytes) != ASSOCIATION_SHA256:
        raise ValueError("association implementation source hash changed")
    if protocol.get("schema") != "graphreader.marker-context-full-train-association-protocol.v1" or \
       protocol.get("match_tolerance_px") != 2.0 or protocol.get("training_approved") is not False or \
       protocol.get("optimizer_steps") != 0 or protocol.get("source_count") != 64:
        raise ValueError("fixed association protocol differs")
    association_module = _load_pinned_module("marker_context_full_association_v1", ASSOCIATION_PATH, association_bytes)
    launch_protocol_raw = locked_bytes(LAUNCH_PROTOCOL_PATH)
    if digest(launch_protocol_raw) != sys.argv[2]:
        raise ValueError("external launcher protocol SHA differs")
    launch_protocol = _load_json(launch_protocol_raw, "launcher protocol")
    runner_sha = digest(Path(__file__).read_bytes())
    launcher_path = ROOT / launch_protocol.get("launcher_path", "")
    launcher_sha = digest(locked_bytes(launcher_path))
    if (launch_protocol.get("schema") != "graphreader.marker-context-full-train-association-launch-protocol.v3" or
        launch_protocol.get("mode") != "fake_sidecar_association_test_only" or
        launch_protocol.get("runner_path") != "tools/MarkerContextFullAssociationV1/associate.py" or
        launch_protocol.get("runner_sha256") != runner_sha or
        launch_protocol.get("association_path") != "tools/MarkerContextFullAssociationV1/association.py" or
        launch_protocol.get("association_sha256") != ASSOCIATION_SHA256 or
        launch_protocol.get("association_protocol_path") != "tools/MarkerContextFullAssociationV1/protocol.json" or
        launch_protocol.get("association_protocol_sha256") != PROTOCOL_SHA256 or
        launch_protocol.get("launcher_sha256") != launcher_sha or
        launch_protocol.get("capture_report_path") != protocol["capture_output_path"] + "/report.json" or
        launch_protocol.get("launch_binding_path") != "artifacts/goal22-runs/marker-context-train-association-v9-fake-launch-binding.json" or
        launch_protocol.get("capture_binding_path") != "artifacts/goal22-runs/marker-context-bound-input-full-preflight-v4/capture-binding.json" or
        launch_protocol.get("capture_binding_sha256") != CAPTURE_BINDING_SHA256 or
        launch_protocol.get("capture_protocol_sha256") != protocol["capture_protocol_sha256"] or
        launch_protocol.get("capture_runtime_binding_sha256") != protocol["capture_runtime_binding_sha256"] or
        launch_protocol.get("training_approved") is not False):
        raise ValueError("launcher bundle identity differs")
    launch_binding_raw = locked_bytes(LAUNCH_BINDING_PATH)
    if digest(launch_binding_raw) != sys.argv[3]:
        raise ValueError("launcher binding digest differs")
    launch_binding = _load_json(launch_binding_raw, "launcher binding")
    expected_launch_fields = {
        "schema": "graphreader.marker-context-full-train-association-launch-binding.v3",
        "launcher_protocol_sha256": sys.argv[2],
        "capture_report_sha256": sys.argv[1],
        "runner_sha256": runner_sha,
        "association_sha256": ASSOCIATION_SHA256,
        "association_protocol_sha256": PROTOCOL_SHA256,
        "launcher_sha256": launcher_sha,
        "mode": "fake_sidecar_association_test_only",
        "output_path": "artifacts/goal22-runs/marker-context-train-association-v9-fake-sidecar-test",
        "truth_content_read": False,
        "training_approved": False,
    }
    if any(launch_binding.get(key) != value for key, value in expected_launch_fields.items()):
        raise ValueError("launcher binding fields differ")
    python_path = launch_binding.get("python_executable_path")
    python_sha = launch_binding.get("python_executable_sha256")
    if (not isinstance(python_path, str) or not isinstance(python_sha, str) or len(python_sha) != 64 or
        any(c not in "0123456789abcdef" for c in python_sha) or
        os.path.normcase(os.path.realpath(python_path)) != os.path.normcase(os.path.realpath(sys.executable)) or
        launch_binding.get("python_version") != sys.version or digest_file(python_path) != python_sha):
        raise ValueError("Python interpreter identity differs from locked launcher binding")
    capture_binding_raw = locked_bytes(CAPTURE_BINDING_PATH)
    if digest(capture_binding_raw) != CAPTURE_BINDING_SHA256:
        raise ValueError("capture descriptor hash differs")
    capture_binding = _load_json(capture_binding_raw, "capture binding")
    capture_protocol_path = ROOT / protocol["capture_protocol_path"]
    capture_protocol_raw = locked_bytes(capture_protocol_path)
    if digest(capture_protocol_raw) != protocol["capture_protocol_sha256"]:
        raise ValueError("capture protocol source hash differs")
    capture_protocol = _load_json(capture_protocol_raw, "capture protocol")
    if capture_protocol.get("source_count") != 64 or capture_protocol.get("scope") != "64-source-public-synthetic-train-raster-capture-no-marker-classifier":
        raise ValueError("capture protocol split differs")
    completion_raw = locked_bytes(ROOT / protocol["complete_path"])
    if digest(completion_raw) != protocol["complete_sha256"]:
        raise ValueError("v5 completion receipt SHA differs")
    complete = _load_json(completion_raw, "v5 completion receipt")
    corpus_binding = locked_bytes(ROOT / protocol["corpus_binding_path"])
    identity_raw = locked_bytes(ROOT / protocol["identity_path"])
    raster_raw = locked_bytes(ROOT / protocol["raster_manifest_path"])
    if digest(corpus_binding) != protocol["corpus_binding_sha256"] or \
       digest(identity_raw) != protocol["identity_sha256"] or digest(raster_raw) != protocol["raster_manifest_sha256"]:
        raise ValueError("v5 corpus metadata hash differs")
    identity = _load_json(identity_raw, "v5 identity")
    raster = _load_json(raster_raw, "v5 raster manifest")

    capture_root = ROOT / protocol["capture_output_path"]
    report_path = check_path(capture_root / "report.json")
    report_raw = locked_bytes(report_path)
    expected_report_sha = sys.argv[1]
    if digest(report_raw) != expected_report_sha:
        raise ValueError("capture report differs from supplied reviewed SHA")
    report = _load_json(report_raw, "full capture report")
    report["_expected_report_sha256"] = expected_report_sha
    extracted = _validate_capture_and_extract(report, report_raw, capture_binding, capture_binding_raw,
        capture_root, capture_protocol, protocol, identity, raster, complete, association_module)
    protocol_by_image = {row["image_sha256"]: row for row in capture_protocol["sources"]}
    tensor_ledger = [
        {"source_id": source["source_id"], "candidate_id": candidate["candidate_id"],
         "local_sha256": candidate["local_sha256"], "wide_sha256": candidate["wide_sha256"]}
        for source in extracted.values() for candidate in source["candidates"]
    ]
    tensor_ledger_sha256 = digest(canonical(tensor_ledger))
    candidate_count = len(tensor_ledger)

    if not RUNS.is_dir():
        raise FileNotFoundError(RUNS)
    check_path(OUTPUT, must_exist=False)
    if OUTPUT.exists():
        raise FileExistsError("fake-sidecar test output must be fresh")
    OUTPUT.mkdir()
    freeze = {"schema": "graphreader.marker-context-full-train-association-fake-pretruth-freeze.v1",
              "capture_report_sha256": expected_report_sha,
              "capture_protocol_sha256": protocol["capture_protocol_sha256"],
              "capture_binding_sha256": CAPTURE_BINDING_SHA256,
              "candidate_count": candidate_count,
              "candidate_tensor_ledger_sha256": tensor_ledger_sha256,
              "match_tolerance_px": 2.0,
              "match_metric": "euclidean_original_source_pixels_inclusive",
              "truth_content_read": False, "fake_sidecar_only": True,
              "training_approved": False, "optimizer_steps": 0}
    freeze_raw = canonical(freeze)
    with (OUTPUT / "pretruth-freeze.json").open("xb") as stream:
        stream.write(freeze_raw); stream.flush(); os.fsync(stream.fileno())

    source_ids = [source["source_id"] for source in extracted.values()]
    fake_truth_raw = canonical(_fake_truth_fixture(source_ids))
    fake_truth_path = OUTPUT / "fake-truth-sidecar.json"
    with fake_truth_path.open("xb") as stream:
        stream.write(fake_truth_raw); stream.flush(); os.fsync(stream.fileno())
    fake_truth = _load_json(locked_bytes(fake_truth_path), "synthetic fake-truth fixture")
    if fake_truth.get("schema") != "graphreader.marker-context-association-fake-sidecar.v1" or \
       fake_truth.get("scope") != "integration-test-only-empty-truth-no-source-annotations" or \
       fake_truth.get("training_approved") is not False or len(fake_truth.get("sources", [])) != 64:
        raise ValueError("fake-sidecar test fixture scope differs")
    fake_by_source = {row["source_id"]: {"markers": row["markers"], "overlays": row["overlays"]}
                      for row in fake_truth["sources"]}
    fake_result = association_module.associate_full(list(extracted.values()), fake_by_source)
    if (fake_result.get("source_count") != 64 or fake_result.get("candidate_count") != candidate_count or
        fake_result.get("truth_marker_count") != 0 or fake_result.get("overlay_count") != 0 or
        fake_result.get("trainable_rows") != 0 or fake_result.get("negative_training_rows") != 0 or
        fake_result.get("training_approved") is not False or fake_result.get("optimizer_steps") != 0):
        raise ValueError("fake-sidecar association summary differs")
    result_raw = canonical(fake_result)
    with (OUTPUT / "fake-association-result.json").open("xb") as stream:
        stream.write(result_raw); stream.flush(); os.fsync(stream.fileno())
    receipt = {"schema": "graphreader.marker-context-full-train-association-fake-test-receipt.v1",
               "status": "fake_sidecar_only_diagnostic", "capture_report_sha256": expected_report_sha,
               "launcher_protocol_sha256": sys.argv[2], "launch_binding_sha256": sys.argv[3],
               "fake_sidecar_sha256": digest(fake_truth_raw), "pretruth_freeze_sha256": digest(freeze_raw),
               "association_result_sha256": digest(result_raw), "source_count": 64,
               "event_count": report["event_count"], "candidate_count": candidate_count,
               "truth_marker_count": 0, "match_tolerance_px": 2.0,
               "real_truth_sidecar_opened": False, "truth_content_read": False,
               "trainable_rows": 0, "negative_training_rows": 0,
               "training_approved": False, "optimizer_steps": 0}
    receipt_raw = canonical(receipt)
    with (OUTPUT / "receipt.json").open("xb") as stream:
        stream.write(receipt_raw); stream.flush(); os.fsync(stream.fileno())
    print(json.dumps({"status": "fake_sidecar_only_diagnostic", "receipt_sha256": digest(receipt_raw),
                      "source_count": 64, "event_count": report["event_count"],
                      "candidate_count": candidate_count, "truth_marker_count": 0,
                      "real_truth_sidecar_opened": False, "training_approved": False,
                      "optimizer_steps": 0}))



if __name__ == "__main__":
    main()

