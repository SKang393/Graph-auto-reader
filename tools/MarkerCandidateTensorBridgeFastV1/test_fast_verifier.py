from __future__ import annotations

import copy
import hashlib
import json
import os
import time
import unittest
from pathlib import Path

from tools.MarkerCandidateTensorBridgeV1 import bridge
from tools.MarkerCandidateTensorBridgeV1 import runner as v1_runner
from tools.MarkerCandidateTensorBridgeV1.test_bridge import full_capture_fixture
from tools.MarkerCandidateTensorBridgeFastV1.fast_verifier import FastVerifier


ROOT = Path(r"E:\Graph-auto-reader")
PROTOCOL = Path(__file__).with_name("protocol.json")
ONE_SOURCE_ROOT = ROOT / "artifacts/goal22-runs/marker-candidate-tensor-bridge-seed-20820000-v1"
ONE_SOURCE_DESCRIPTOR = ROOT / "artifacts/goal22-runs/marker-candidate-tensor-bridge-one-source-prep-v1/source-descriptor.json"
EXPECTED_ONE_SOURCE_RECEIPT_SHA256 = "563d564b9106b679f82766676810f6c4e5aab838c289574abb9091fc3f27b636"
EXPECTED_ONE_SOURCE_DESCRIPTOR_SHA256 = "afd243a07ac92a7c9e1769a46517b868cf0611de7f58714fe99536c0b9a22063"


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def assert_no_reparse_ancestors(test: unittest.TestCase, path: Path) -> None:
    absolute = path.absolute()
    ancestors = [absolute]
    while ancestors[-1].parent != ancestors[-1]:
        ancestors.append(ancestors[-1].parent)
    for entry in reversed(ancestors):
        stat = entry.lstat()
        attributes = getattr(stat, "st_file_attributes", 0)
        test.assertFalse(entry.is_symlink(), f"reparse symlink: {entry}")
        test.assertFalse(attributes & 0x400, f"reparse point: {entry}")


def verifier() -> FastVerifier:
    raw = PROTOCOL.read_bytes()
    return FastVerifier(project_root=ROOT, expected_protocol_sha256=digest(raw))


class FastVerifierTests(unittest.TestCase):
    def test_fast_core_preserves_v1_rows_labels_order_and_tensor_bytes(self):
        source, panel_bytes, source_raster, truths, glyphs, overlays = full_capture_fixture()
        full_panel = json.loads(panel_bytes)
        old_evidence = bridge._full_capture_candidate_evidence(
            full_panel, panel_bytes, source)
        expected = bridge._materialize_source_core(
            source=source, panel_result_bytes=panel_bytes,
            source_raster_bytes=source_raster, panel_raster_bytes=source_raster,
            candidate_evidence=old_evidence, positive_truths=truths,
            visible_glyphs=glyphs, generic_overlays=overlays)

        fast = verifier()
        calls = {"full_plane": 0, "candidate_tensor": 0}
        original_encoder = fast.bridge.tensor_f32le

        def counted(values, *, expected_count=1024):
            if expected_count == source["width"] * source["height"]:
                calls["full_plane"] += 1
            else:
                calls["candidate_tensor"] += 1
            return original_encoder(values, expected_count=expected_count)

        fast.bridge.tensor_f32le = counted
        actual = fast.materialize_source_core(
            source=source, panel_bytes=panel_bytes,
            source_raster_bytes=source_raster, panel_raster_bytes=source_raster,
            positive_truths=truths, visible_glyphs=glyphs, generic_overlays=overlays)

        self.assertEqual(3, calls["full_plane"], "frame and two masks must validate once per source")
        self.assertEqual(len(full_panel["center_markers"]) * 2, calls["candidate_tensor"],
                         "each candidate view validates once")
        self.assertEqual(expected["candidate_count"], actual["candidate_count"])
        for key in ("matched_positive_review_rows", "visible_legend_review_rows",
                    "unknown_or_excluded_rows", "trainable_rows", "negative_training_rows",
                    "training_approved", "optimizer_steps", "truth_scope", "dev_input_supplied",
                    "private_input_supplied", "sealed_input_supplied"):
            self.assertEqual(expected[key], actual[key], key)
        self.assertEqual(expected["candidate_status_counts"], actual["candidate_status_counts"])
        def without_evidence_hashes(rows):
            return [{key: value for key, value in row.items()
                     if key not in {"local_evidence_sha256", "wide_evidence_sha256"}}
                    for row in rows]
        self.assertEqual(without_evidence_hashes(expected["rows"]), without_evidence_hashes(actual["rows"]))
        self.assertEqual(expected["limitations"], actual["limitations"][:len(expected["limitations"])])
        self.assertEqual(expected["tensor_bytes_by_candidate"].keys(),
                         actual["tensor_bytes_by_candidate"].keys())
        for candidate_id in expected["tensor_bytes_by_candidate"]:
            old = expected["tensor_bytes_by_candidate"][candidate_id]
            new = actual["tensor_bytes_by_candidate"][candidate_id]
            self.assertEqual(old["local_bytes"], new["local_bytes"])
            self.assertEqual(old["wide_bytes"], new["wide_bytes"])
            self.assertEqual(old["local_sha256"], new["local_sha256"])
            self.assertEqual(old["wide_sha256"], new["wide_sha256"])
            self.assertEqual(old["mask_snapshots"], new["mask_snapshots"])
        self.assertEqual(1, actual["shared_plane_hashes"]["source_plane_f32le_sha256"] ==
                         source["frame_f32le_sha256"])
        self.assertNotEqual(expected["rows"][0]["local_evidence_sha256"],
                            actual["rows"][0]["local_evidence_sha256"])
        self.assertEqual("full_capture_panel_shared_plane_fast_v1", actual["sampler_input_format"])
        self.assertEqual(fast.source_closure_sha256, actual["source_closure_sha256"])
        self.assertEqual(fast.source_closure, actual["source_closure"])

    def _run_bad_panel(self, mutate, expected_message: str):
        source, panel_bytes, source_raster, truths, glyphs, overlays = full_capture_fixture()
        panel = json.loads(panel_bytes)
        mutate(panel)
        replacement = json.dumps(panel, separators=(",", ":"), allow_nan=False).encode()
        source["panel_result_sha256"] = digest(replacement)
        with self.assertRaisesRegex(ValueError, expected_message):
            verifier().materialize_source_core(
                source=source, panel_bytes=replacement,
                source_raster_bytes=source_raster, panel_raster_bytes=source_raster,
                positive_truths=truths, visible_glyphs=glyphs, generic_overlays=overlays)

    def test_rejects_panel_frame_and_mask_mutations(self):
        self._run_bad_panel(lambda p: p.update(panel_id="other-panel"), "identity/hash differs")

        def bad_frame(panel):
            panel["frame"]["channels_first_pixels"][0] = 0.25
        self._run_bad_panel(bad_frame, "frame plane differs")

        def bad_mask(panel):
            panel["frame"]["ocr_mask"]["values"][0] = 2.0
        self._run_bad_panel(bad_mask, "outside \\[0,1\\]")

    def test_rejects_bad_geometry_duplicate_ids_and_tensor_hashes(self):
        def bad_transform(panel):
            panel["provenance"]["source_to_panel_matrix"][2] = 1
        self._run_bad_panel(bad_transform, "identity mapping")

        def duplicate_id(panel):
            panel["center_markers"][1]["marker_id"] = panel["center_markers"][0]["marker_id"]
            panel["classifier_inputs"][1]["marker_id"] = panel["center_markers"][0]["marker_id"]
            panel["sampled_local_and_experimental_wide_views"][1]["marker"]["marker_id"] = panel["center_markers"][0]["marker_id"]
        self._run_bad_panel(duplicate_id, "uniquely bound")

        def wrong_tensor_hash(panel):
            panel["sampled_local_and_experimental_wide_views"][0]["local_sha256"] = "0" * 64
        self._run_bad_panel(wrong_tensor_hash, "tensor bytes differ")

    def test_rejects_non_token_evidence(self):
        source, _panel, *_ = full_capture_fixture()
        candidate = {"candidate_id": "candidate-a", "center": (2.0, 2.0), "radius": 1.0}
        with self.assertRaisesRegex(ValueError, "internally constructed"):
            verifier().bridge._verify_evidence(
                b"caller-authored", source=source, candidate=candidate, view="local",
                expected_frame_sha256=source["frame_f32le_sha256"],
                expected_ocr_mask_sha256=None, expected_artifact_mask_sha256=None)

    @unittest.skipUnless(os.environ.get("GRAPHREADER_RUN_CAPTURE_PARITY") == "1",
                         "one-panel captured-output check is opt-in while full batch may be active")
    def test_optional_existing_one_source_capture_tensor_identity(self):
        receipt_path = ONE_SOURCE_ROOT / "receipt.json"
        rows_path = ONE_SOURCE_ROOT / "review-rows.json"
        for path in (receipt_path, rows_path, ONE_SOURCE_DESCRIPTOR):
            if not path.is_file():
                self.skipTest(f"optional bound one-source artifact is absent: {path.name}")
        receipt_bytes = receipt_path.read_bytes()
        self.assertEqual(EXPECTED_ONE_SOURCE_RECEIPT_SHA256, digest(receipt_bytes))
        saved_receipt = json.loads(receipt_bytes)
        descriptor_bytes = ONE_SOURCE_DESCRIPTOR.read_bytes()
        self.assertEqual(EXPECTED_ONE_SOURCE_DESCRIPTOR_SHA256, digest(descriptor_bytes))
        self.assertEqual(saved_receipt["descriptor_sha256"], digest(descriptor_bytes))
        preflight = v1_runner.preflight_descriptor_paths(
            descriptor_bytes=descriptor_bytes, project_root=ROOT)
        self.assertEqual("paths_valid_contents_unopened", preflight["status"])
        descriptor = json.loads(descriptor_bytes)
        source = descriptor["source"]
        self.assertEqual(saved_receipt["source_id"], source["source_id"])
        # The historical one-source receipt pins a runner-protocol revision
        # that is no longer the current runner-protocol file. The bound
        # materialization below independently validates the unchanged pinned
        # bridge/source closure and all saved input bytes; it does not claim a
        # current runner-protocol match.
        paths = {key: v1_runner._resolve_input(ROOT, relative)
                 for key, relative in descriptor["inputs"].items()}
        input_bytes = {key: path.read_bytes() for key, path in paths.items()}
        self.assertEqual(saved_receipt["input_sha256"],
                         {key: digest(value) for key, value in input_bytes.items()})
        saved = json.loads(rows_path.read_bytes())
        self.assertEqual(saved_receipt["review_rows_sha256"], digest(rows_path.read_bytes()))
        start = time.perf_counter()
        fast = verifier().materialize_bound_full_capture_panel(
            source=source, panel_capture_bytes=input_bytes["panel_capture"],
            source_raster_bytes=input_bytes["source_raster"],
            panel_raster_bytes=input_bytes["panel_raster"],
            complete_receipt_bytes=input_bytes["complete_receipt"],
            identity_metadata_bytes=input_bytes["identity_metadata"],
            capture_report_bytes=input_bytes["capture_report"],
            truth_sidecar_bytes=input_bytes["truth_sidecar"],
            artifact_report_bytes=input_bytes["artifact_report"], project_root=ROOT)
        elapsed = time.perf_counter() - start
        fast_rows = fast["tensor_bytes_by_candidate"]
        saved_refs = saved["tensor_files"]
        self.assertEqual([row["candidate_id"] for row in saved["rows"]],
                         [row["candidate_id"] for row in fast["rows"]])
        self.assertEqual(set(saved_refs), set(fast_rows))
        def without_evidence_hashes(rows):
            return [{key: value for key, value in row.items()
                     if key not in {"local_evidence_sha256", "wide_evidence_sha256"}}
                    for row in rows]
        self.assertEqual(without_evidence_hashes(saved["rows"]),
                         without_evidence_hashes(fast["rows"]))
        self.assertEqual(saved["truth_inventory_binding"], fast["truth_inventory_binding"])
        for candidate_id, row in fast_rows.items():
            saved_row = next(item for item in saved["rows"] if item["candidate_id"] == candidate_id)
            self.assertEqual(saved_row["local_tensor_sha256"], row["local_sha256"])
            self.assertEqual(saved_row["wide_tensor_sha256"], row["wide_sha256"])
            for view in ("local", "wide"):
                reference = saved_refs[candidate_id][view]
                self.assertEqual({"bytes", "encoding", "path", "sha256"}, set(reference))
                self.assertEqual("raw_float32_le", reference["encoding"])
                relative = Path(reference["path"])
                self.assertFalse(relative.is_absolute())
                self.assertNotIn("..", relative.parts)
                self.assertEqual(Path("tensors"), relative.parent)
                tensor_path = rows_path.parent / relative
                expected_tensor_dir = rows_path.parent / "tensors"
                assert_no_reparse_ancestors(self, rows_path.parent)
                assert_no_reparse_ancestors(self, expected_tensor_dir)
                assert_no_reparse_ancestors(self, tensor_path)
                resolved_root = rows_path.parent.resolve(strict=True)
                resolved_tensor_dir = expected_tensor_dir.resolve(strict=True)
                resolved = tensor_path.resolve(strict=True)
                self.assertEqual(resolved_root, resolved_tensor_dir.parent)
                self.assertEqual(resolved_tensor_dir, resolved.parent)
                raw = tensor_path.read_bytes()
                self.assertEqual(reference["bytes"], len(raw))
                self.assertEqual(reference["sha256"], digest(raw))
                self.assertEqual(row[f"{view}_bytes"], raw)
        print(f"FAST_ONE_PANEL_SECONDS={elapsed:.6f}; candidate_count={len(fast_rows)}")


if __name__ == "__main__":
    unittest.main()
