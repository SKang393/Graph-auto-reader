# SPDX-License-Identifier: Apache-2.0
"""Review-pending full-train association caller; real sidecar remains disabled by protocol."""
import hashlib
import json
import os
from pathlib import Path
import sys
import types

ROOT = Path(r"E:\Graph-auto-reader")
RUNS = ROOT / "artifacts/goal22-runs"
PACKAGE = ROOT / "tools/MarkerContextFullAssociationV1"
LAUNCH_PROTOCOL_PATH = PACKAGE / "launcher-protocol.json"
LAUNCH_BINDING_PATH = RUNS / "marker-context-train-association-v13-launch-binding.json"
OUTPUT = RUNS / "marker-context-train-association-v13-real-sidecar"
REAL_PROTOCOL_PATH = PACKAGE / "real-association-protocol-v4.json"
REAL_PROTOCOL_SHA256 = "90c248f4f0241c6e5f556d23d5291e0515ba1b264a7bb68c7ec7c922d893553e"
BASE_RUNNER_PATH = PACKAGE / "associate.py"
BASE_RUNNER_SHA256 = "29d4ebf7df6608d52fbe5f1fe8996c479fab236abd61f1d9b1e15f8d9b4d72b6"
TRUTH_ADAPTER_PATH = PACKAGE / "truth_association.py"
TRUTH_ADAPTER_SHA256 = "09a7daec3ff99154869ab752c8469e4438773f89b700263323c68cbc983d3d36"
ASSOCIATION_PATH = PACKAGE / "association.py"
ASSOCIATION_SHA256 = "90eb3f1017b30c47f86361975408931ab86f76bd093864d89ea91ce37aebe991"
CAPTURE_BINDING_PATH = ROOT / "artifacts/goal22-runs/marker-context-bound-input-full-preflight-v4/capture-binding.json"
CAPTURE_BINDING_SHA256 = "792d6ec1bc59f16f92cb03adae1b59d2ca0d35ba85218f6eb9a9218d2594327b"


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def load_pinned(name, path, raw, expected):
    if digest(raw) != expected:
        raise ValueError(f"{name} source hash differs")
    module = types.ModuleType(name)
    module.__file__ = str(path)
    sys.modules[name] = module
    exec(compile(raw, str(path), "exec", dont_inherit=True), module.__dict__)
    return module


def check_output_directory(path, *, must_exist=True, base):
    path = Path(path)
    if not path.is_absolute() or not path.resolve(strict=False).is_relative_to(ROOT):
        raise ValueError("Output directory must remain inside E project")
    cursor = Path(path.anchor)
    for part in path.parts[1:]:
        cursor /= part
        attrs = base._win_attrs(cursor)
        if attrs == 0xFFFFFFFF:
            if must_exist:
                raise OSError("Cannot inspect output directory path")
            continue
        if attrs & base.REPARSE:
            raise ValueError("Reparse-point output directory denied")
    if must_exist and not path.is_dir():
        raise NotADirectoryError(path)
    return path


def main():
    if os.name != "nt" or not sys.flags.isolated or not sys.dont_write_bytecode:
        raise ValueError("Use isolated Windows Python with -I -B")
    if len(sys.argv) != 5 or sys.argv[4] not in ("--fake-sidecar-self-test", "--real-truth-association") or \
       any(len(value) != 64 or any(c not in "0123456789abcdef" for c in value) for value in sys.argv[1:4]):
        raise ValueError("Pass reviewed report/protocol/binding digests and an explicit association mode")
    mode = sys.argv[4]

    base_raw = BASE_RUNNER_PATH.read_bytes()
    base = load_pinned("marker_context_full_capture_validator_v10", BASE_RUNNER_PATH, base_raw, BASE_RUNNER_SHA256)
    real_protocol_raw = base.locked_bytes(REAL_PROTOCOL_PATH)
    if digest(real_protocol_raw) != REAL_PROTOCOL_SHA256:
        raise ValueError("real association protocol changed")
    real_protocol = base._load_json(real_protocol_raw, "real association protocol")
    if (real_protocol.get("schema") != "graphreader.marker-context-full-train-real-association-protocol.v1" or
        real_protocol.get("status") != "prepared_for_independent_review_not_executed" or
        real_protocol.get("truth_access_performed") is not False or real_protocol.get("source_count") != 64 or
        real_protocol.get("match_tolerance_px") != 2.0 or real_protocol.get("training_approved") is not False or
        real_protocol.get("optimizer_steps") != 0):
        raise ValueError("real association contract differs")
    if mode == "--real-truth-association" and real_protocol.get("real_truth_access_enabled") is not True:
        raise ValueError("real truth access remains disabled pending independent review")

    launch_protocol_raw = base.locked_bytes(LAUNCH_PROTOCOL_PATH)
    if digest(launch_protocol_raw) != sys.argv[2]:
        raise ValueError("launcher protocol SHA differs")
    launch_protocol = base._load_json(launch_protocol_raw, "real association launcher protocol")
    runner_sha = digest(Path(__file__).read_bytes())
    launcher_path = ROOT / launch_protocol.get("launcher_path", "")
    launcher_sha = digest(base.locked_bytes(launcher_path))
    if (launch_protocol.get("schema") != "graphreader.marker-context-full-train-association-launch-protocol.v4" or
        launch_protocol.get("mode") != "real_association_review_pending" or
        launch_protocol.get("runner_path") != "tools/MarkerContextFullAssociationV1/associate_real_v13.py" or
        launch_protocol.get("runner_sha256") != runner_sha or
        launch_protocol.get("real_association_protocol_path") != "tools/MarkerContextFullAssociationV1/real-association-protocol-v4.json" or
        launch_protocol.get("real_association_protocol_sha256") != REAL_PROTOCOL_SHA256 or
        launch_protocol.get("capture_validator_path") != "tools/MarkerContextFullAssociationV1/associate.py" or
        launch_protocol.get("capture_validator_sha256") != BASE_RUNNER_SHA256 or
        launch_protocol.get("truth_adapter_path") != "tools/MarkerContextFullAssociationV1/truth_association.py" or
        launch_protocol.get("truth_adapter_sha256") != TRUTH_ADAPTER_SHA256 or
        launch_protocol.get("association_sha256") != ASSOCIATION_SHA256 or
        launch_protocol.get("capture_report_path") != real_protocol["capture_report_path"] or
        launch_protocol.get("capture_binding_sha256") != CAPTURE_BINDING_SHA256 or
        launch_protocol.get("real_truth_access_enabled") is not real_protocol.get("real_truth_access_enabled") or
        launch_protocol.get("training_approved") is not False):
        raise ValueError("real association launcher binding differs")

    launch_binding_raw = base.locked_bytes(LAUNCH_BINDING_PATH)
    if digest(launch_binding_raw) != sys.argv[3]:
        raise ValueError("launch binding SHA differs")
    launch_binding = base._load_json(launch_binding_raw, "real association launch binding")
    requested_operation = "fake_sidecar_self_test" if mode == "--fake-sidecar-self-test" else "real_truth_association"
    expected_binding = {
        "schema": "graphreader.marker-context-full-train-association-launch-binding.v4",
        "launcher_protocol_sha256": sys.argv[2],
        "capture_report_sha256": sys.argv[1],
        "runner_sha256": runner_sha,
        "base_capture_validator_sha256": BASE_RUNNER_SHA256,
        "truth_adapter_sha256": TRUTH_ADAPTER_SHA256,
        "real_association_protocol_sha256": REAL_PROTOCOL_SHA256,
        "association_sha256": ASSOCIATION_SHA256,
        "launcher_sha256": launcher_sha,
        "mode": "real_association_review_pending",
        "requested_operation": requested_operation,
        "output_path": real_protocol["output_path"],
        "truth_content_read": False,
        "training_approved": False,
    }
    if any(launch_binding.get(key) != value for key, value in expected_binding.items()):
        raise ValueError("real association launch binding fields differ")
    python_path = launch_binding.get("python_executable_path")
    if (not isinstance(python_path, str) or
        os.path.normcase(os.path.realpath(python_path)) != os.path.normcase(os.path.realpath(sys.executable)) or
        launch_binding.get("python_version") != sys.version or
        base.digest_file(python_path) != launch_binding.get("python_executable_sha256")):
        raise ValueError("Python runtime identity differs from launch binding")

    association_protocol_raw = base.locked_bytes(base.PROTOCOL_PATH)
    if digest(association_protocol_raw) != base.PROTOCOL_SHA256:
        raise ValueError("fixed 2 px association protocol changed")
    protocol = base._load_json(association_protocol_raw, "association protocol")
    if (protocol.get("capture_output_path") + "/report.json" != real_protocol["capture_report_path"] or
        protocol.get("truth_sha256") != real_protocol["truth_sha256"] or
        protocol.get("match_tolerance_px") != 2.0 or protocol.get("training_approved") is not False):
        raise ValueError("association and real-truth protocols disagree")
    capture_binding_raw = base.locked_bytes(CAPTURE_BINDING_PATH)
    if digest(capture_binding_raw) != CAPTURE_BINDING_SHA256:
        raise ValueError("capture binding changed")
    capture_binding = base._load_json(capture_binding_raw, "capture binding")
    capture_protocol_raw = base.locked_bytes(ROOT / protocol["capture_protocol_path"])
    if digest(capture_protocol_raw) != protocol["capture_protocol_sha256"]:
        raise ValueError("capture protocol changed")
    capture_protocol = base._load_json(capture_protocol_raw, "capture protocol")
    completion_raw = base.locked_bytes(ROOT / protocol["complete_path"])
    if digest(completion_raw) != protocol["complete_sha256"]:
        raise ValueError("v5 completion metadata changed")
    complete = base._load_json(completion_raw, "v5 completion metadata")
    identity_raw = base.locked_bytes(ROOT / protocol["identity_path"])
    raster_raw = base.locked_bytes(ROOT / protocol["raster_manifest_path"])
    corpus_binding_raw = base.locked_bytes(ROOT / protocol["corpus_binding_path"])
    if (digest(identity_raw) != protocol["identity_sha256"] or
        digest(raster_raw) != protocol["raster_manifest_sha256"] or
        digest(corpus_binding_raw) != protocol["corpus_binding_sha256"]):
        raise ValueError("v5 public corpus metadata changed")
    identity = base._load_json(identity_raw, "v5 identity metadata")
    raster = base._load_json(raster_raw, "v5 raster metadata")
    report_path = base.check_path(ROOT / protocol["capture_output_path"] / "report.json")
    report_raw = base.locked_bytes(report_path)
    if digest(report_raw) != sys.argv[1] or digest(report_raw) != real_protocol["capture_report_sha256"]:
        raise ValueError("fixed completed capture report SHA differs")
    report = base._load_json(report_raw, "full capture report")
    report["_expected_report_sha256"] = sys.argv[1]
    association_raw = base.locked_bytes(ASSOCIATION_PATH)
    association_module = load_pinned("marker_context_full_association_v10", ASSOCIATION_PATH,
                                     association_raw, ASSOCIATION_SHA256)
    extracted = base._validate_capture_and_extract(report, report_raw, capture_binding, capture_binding_raw,
        ROOT / protocol["capture_output_path"], capture_protocol, protocol, identity, raster, complete,
        association_module)
    candidates = [candidate for source in extracted.values() for candidate in source["candidates"]]
    tensor_ledger = [{"source_id": source["source_id"], "candidate_id": candidate["candidate_id"],
                      "local_sha256": candidate["local_sha256"], "wide_sha256": candidate["wide_sha256"]}
                     for source in extracted.values() for candidate in source["candidates"]]
    tensor_ledger_sha = digest(canonical(tensor_ledger))

    labeler_path = ROOT / protocol["truth_labeler_path"]
    labeler_raw = base.locked_bytes(labeler_path)
    if digest(labeler_raw) != real_protocol["truth_labeler_sha256"]:
        raise ValueError("v5 train labeler hash differs")
    for name in ("ml", "ml.synthetic", "ml.synthetic.marker_context_catalog_v1"):
        if name in sys.modules:
            raise ValueError("project package unexpectedly imported before source verification")
        module = types.ModuleType(name); module.__path__ = []; sys.modules[name] = module
    labels = load_pinned("ml.synthetic.marker_context_catalog_v1.labels", labeler_path, labeler_raw,
                         real_protocol["truth_labeler_sha256"])
    truth_adapter_raw = base.locked_bytes(TRUTH_ADAPTER_PATH)
    truth_adapter = load_pinned("marker_context_full_truth_adapter_v10", TRUTH_ADAPTER_PATH,
                                 truth_adapter_raw, TRUTH_ADAPTER_SHA256)

    if real_protocol.get("output_path") != "artifacts/goal22-runs/marker-context-train-association-v13-real-review-pending":
        raise ValueError("fresh real association output path differs")
    if not RUNS.is_dir():
        raise FileNotFoundError(RUNS)
    output = ROOT / real_protocol["output_path"]
    check_output_directory(output, must_exist=False, base=base)
    if output.exists():
        raise FileExistsError("real association output must be fresh")
    output.mkdir()
    check_output_directory(output, base=base)
    fake_raw = None
    fake_protocol_sources = None
    if mode == "--fake-sidecar-self-test":
        fake_protocol_sources, fake_rows = [], []
        for index, source in enumerate(capture_protocol["sources"]):
            source_id, image_sha = source.get("source_id"), source.get("image_sha256")
            if not isinstance(source_id, str) or not isinstance(image_sha, str):
                raise ValueError("capture source identity cannot support fake adapter fixture")
            scene_id = f"synthetic-fake-scene-{index:03d}"
            case_id = f"synthetic-fake-case-{index:03d}"
            scene = {"scene_id": scene_id, "fixture": "empty-fake-sidecar-only"}
            scene_sha = digest(canonical(scene))
            annotation = {"scene_id": scene_id, "contextual_negative_profile":
                          {"source_scene_canonical_sha256": scene_sha}}
            overlay = {"scene_id": scene_id, "scene_sha256": scene_sha, "split": "train",
                       "status": "review_only", "optimization_allowed": False,
                       "materialized": True, "center": [1, 1]}
            fake_protocol_sources.append({"source_id": source_id, "image_sha256": image_sha,
                                          "case_id": case_id, "scene_id": scene_id,
                                          "scene_sha256": scene_sha})
            fake_rows.append({"case_id": case_id, "image_sha256": image_sha, "scene": scene,
                              "annotation": annotation, "overlay": overlay})
        fake_raw = canonical({"schema": "marker-context-train-truth-v5", "mode": "full",
                              "training_approved": False, "sources": fake_rows})
    freeze_record = {
        "schema": "graphreader.marker-context-full-train-association-pretruth-freeze.v1",
        "capture_report_sha256": sys.argv[1],
        "capture_protocol_sha256": protocol["capture_protocol_sha256"],
        "capture_binding_sha256": CAPTURE_BINDING_SHA256,
        "capture_runtime_binding_sha256": protocol["capture_runtime_binding_sha256"],
        "real_association_protocol_sha256": REAL_PROTOCOL_SHA256,
        "truth_sha256": digest(fake_raw) if fake_raw is not None else real_protocol["truth_sha256"],
        "truth_sidecar_kind": "synthetic-fake-empty-association-fixture" if fake_raw is not None else "public-synthetic-v5-train-sidecar",
        "truth_adapter_sha256": TRUTH_ADAPTER_SHA256,
        "truth_labeler_sha256": real_protocol["truth_labeler_sha256"],
        "source_count": 64,
        "event_count": report["event_count"],
        "candidate_count": len(candidates),
        "candidate_tensor_ledger_sha256": tensor_ledger_sha,
        "panel_status_counts": report["panel_status_counts"],
        "match_tolerance_px": 2.0,
        "match_metric": "euclidean_original_source_pixels_inclusive",
        "truth_content_read": False,
        "training_approved": False,
        "optimizer_steps": 0,
    }
    freeze_path = output / "pretruth-freeze.json"

    def persist_freeze(raw):
        base.check_path(freeze_path, must_exist=False)
        with freeze_path.open("xb") as stream:
            stream.write(raw); stream.flush(); os.fsync(stream.fileno())

    if mode == "--fake-sidecar-self-test":
        fake_path = output / "fake-truth-sidecar.json"

        def read_fake():
            base.check_path(fake_path, must_exist=False)
            with fake_path.open("xb") as stream:
                stream.write(fake_raw); stream.flush(); os.fsync(stream.fileno())
            return base.locked_bytes(fake_path)

        def fake_classifier(annotation, *, expected_split):
            if expected_split != "train":
                raise ValueError("fake fixture classifier split differs")
            return {"scene_id": annotation["scene_id"], "optimization_allowed": False, "records": []}

        result = truth_adapter.associate_after_freeze(
            freeze_record, persist_freeze, read_fake, digest(fake_raw), fake_protocol_sources,
            list(extracted.values()), fake_classifier, association_module)
    else:
        truth_path = base.check_path(ROOT / protocol["truth_path"])
        result = truth_adapter.associate_after_freeze(freeze_record, persist_freeze,
            lambda: base.locked_bytes(truth_path), real_protocol["truth_sha256"],
            capture_protocol["sources"], list(extracted.values()), labels.classify, association_module)
    result_raw = canonical(result)
    with (output / "association-summary.json").open("xb") as stream:
        stream.write(result_raw); stream.flush(); os.fsync(stream.fileno())
    receipt = {
        "schema": "graphreader.marker-context-full-train-association-real-caller-receipt.v1",
        "mode": "fake_sidecar_only_test" if mode == "--fake-sidecar-self-test" else "real_truth_association",
        "capture_report_sha256": sys.argv[1], "launcher_protocol_sha256": sys.argv[2],
        "launch_binding_sha256": sys.argv[3], "pretruth_freeze_sha256": digest(canonical(freeze_record)),
        "association_summary_sha256": digest(result_raw), "source_count": 64,
        "candidate_count": len(candidates), "truth_marker_count": result["truth_marker_count"],
        "real_truth_sidecar_opened": mode == "--real-truth-association",
        "truth_content_read": mode == "--real-truth-association",
        "trainable_rows": 0, "negative_training_rows": 0,
        "training_approved": False, "optimizer_steps": 0,
    }
    receipt_raw = canonical(receipt)
    with (output / "receipt.json").open("xb") as stream:
        stream.write(receipt_raw); stream.flush(); os.fsync(stream.fileno())
    print(json.dumps({"status": receipt["mode"], "receipt_sha256": digest(receipt_raw),
                      "source_count": 64, "candidate_count": len(candidates),
                      "truth_marker_count": result["truth_marker_count"],
                      "real_truth_sidecar_opened": receipt["real_truth_sidecar_opened"],
                      "training_approved": False, "optimizer_steps": 0}))


if __name__ == "__main__":
    main()
