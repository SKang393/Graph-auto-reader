# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic-only tests; the fake ONNX session never runs an optimizer."""
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest import mock

import numpy as np
from PIL import Image

from ml.ocr.gpu_db_head_probe_v1 import feature_preflight as preflight
from ml.ocr.official_bakeoff import db_inverse_targets
from ml.synthetic.dataset import _split_manifests


def fixture():
    buffer = BytesIO()
    Image.new("RGB", (64, 64), "white").save(buffer, format="PNG")
    annotation = {"canvas": {"width": 64, "height": 64},
                  "panels": [{"texts": [{"text_id": "tick1", "text": "12", "role": "x_tick",
                    "visible": True, "partial": False, "coordinate_space": "original_pixels",
                    "rendered_pixel_box": [8, 8, 8, 8]}]}],
                  "hard_negatives": [{"coordinate_space": "original_pixels", "rendered_pixel_box": [40, 40, 4, 4]}]}
    return buffer.getvalue(), annotation


class PreflightTests(unittest.TestCase):
    def test_train_reader_never_opens_holdout_rasters_or_annotations(self):
        tmp_root = preflight.parity.ROOT / "artifacts" / "test-temp"
        tmp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=tmp_root) as tmp:
            root = Path(tmp)
            dataset = root / "dataset"
            for part in ("images", "annotations", "scenes", "splits"):
                (dataset / part).mkdir(parents=True, exist_ok=True)
            cases, rows = [], {"train": [], "validation": []}
            forbidden = set()
            for index in range(10):
                scene_id = f"{index:032x}"
                split = "train" if index < 4 else "validation"
                families = {axis: split for axis in ("renderer", "font", "degradation", "template", "marker")}
                image, annotation = fixture()
                image += bytes([index])
                annotation.update(scene_id=scene_id, coordinate_space="original_pixels")
                payloads = {f"images/{scene_id}.png": image,
                            f"annotations/{scene_id}.json": json.dumps(annotation).encode(),
                            f"scenes/{scene_id}.json": json.dumps({"scene_id": scene_id}).encode()}
                files = {key: sha256(value).hexdigest() for key, value in payloads.items()}
                cases.append({"scene_id": scene_id, "split": split, "seed": index, "design": "ab",
                              "families": families, "files": files})
                row = {"scene_id": scene_id, "families": families}
                for name, directory, ext in (("image", "images", "png"), ("annotation", "annotations", "json"),
                                              ("scene", "scenes", "json")):
                    rel = f"{directory}/{scene_id}.{ext}"
                    row[name] = {"path": rel, "sha256": files[rel]}
                    if split == "train":
                        (dataset / rel).write_bytes(payloads[rel])
                    else:
                        forbidden.add(dataset / rel)
                rows[split].append(row)
            splits = _split_manifests(cases)
            split_bytes = {name: json.dumps(value).encode() for name, value in splits.items()}
            seed = {"preset": "real_range", "dataset_seed": 20261030, "cases": cases,
                    "artifact_sha256": {f"splits/{name}.json": sha256(raw).hexdigest() for name, raw in split_bytes.items()}}
            for name, raw in split_bytes.items():
                (dataset / "splits" / f"{name}.json").write_bytes(raw)
            seed_raw = json.dumps(seed).encode()
            seed_sha = sha256(seed_raw).hexdigest()
            (dataset / "seed-manifest.json").write_bytes(seed_raw)
            manifest = {"schema": "graphreader.synthetic-tick-family-dataset.v1", "synthetic_only": True,
                        "seed_manifest_sha256": seed_sha, "splits": {name: {"cases": value} for name, value in rows.items()}}
            manifest_raw = json.dumps(manifest).encode()
            (root / "manifest.json").write_bytes(manifest_raw)
            actual_read = preflight.parity._read_locked
            reads = []

            def guarded_read(path, digest):
                reads.append(path)
                self.assertNotIn(path, forbidden, "attempted holdout raster, annotation or scene read")
                return actual_read(path, digest)

            with mock.patch.object(preflight.parity, "DATASET", root), \
                 mock.patch.object(preflight.parity, "DATASET_MANIFEST_SHA", sha256(manifest_raw).hexdigest()), \
                 mock.patch.object(preflight.parity, "SEED_MANIFEST_SHA", seed_sha), \
                 mock.patch.object(preflight.parity, "_read_locked", side_effect=guarded_read):
                records = preflight._train_inputs()
            self.assertEqual(len(records), 4)
            expected_train = {dataset / f"{kind}/{index:032x}.{extension}"
                              for index in range(4) for kind, extension in
                              (("images", "png"), ("annotations", "json"), ("scenes", "json"))}
            self.assertTrue(expected_train.issubset(set(reads)))
            self.assertTrue(set(reads).isdisjoint(forbidden))
            self.assertFalse(any(path.exists() for path in forbidden))

    def test_wrong_model_and_trunk_hash_fail_before_output(self):
        tmp_root = preflight.parity.ROOT / "artifacts" / "test-temp"
        tmp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=tmp_root) as tmp:
            root = Path(tmp)
            model = root / "synthetic-model.onnx"
            model.write_bytes(b"synthetic-model")
            with mock.patch.object(preflight, "_train_inputs", return_value=[]), \
                 mock.patch.object(preflight.parity, "MODEL", model), \
                 mock.patch.object(preflight.parity, "MODEL_SHA", "0" * 64), \
                 mock.patch.object(preflight.onnx, "load_model_from_string") as parser:
                output = root / "bad-model"
                with self.assertRaisesRegex(ValueError, "hash differs"):
                    preflight.run(output)
                parser.assert_not_called()
                self.assertFalse(output.exists())
            with mock.patch.object(preflight, "_train_inputs", return_value=[]), \
                 mock.patch.object(preflight.parity, "MODEL", model), \
                 mock.patch.object(preflight.parity, "MODEL_SHA", sha256(b"synthetic-model").hexdigest()), \
                 mock.patch.object(preflight.onnx, "load_model_from_string", return_value=object()), \
                 mock.patch.object(preflight.parity, "_trunk_bytes", return_value=b"wrong-trunk"), \
                 mock.patch.object(preflight, "TRUNK_SHA", "0" * 64), \
                 mock.patch.object(preflight.ort, "InferenceSession") as session:
                output = root / "bad-trunk"
                with self.assertRaisesRegex(ValueError, "trunk differs"):
                    preflight.run(output)
                session.assert_not_called()
                self.assertFalse(output.exists())

    def test_output_outside_artifacts_is_rejected_before_input_access(self):
        outside = preflight.parity.ROOT / "ml" / "not-an-artifact-output"
        self.assertFalse(outside.exists())
        with mock.patch.object(preflight, "_train_inputs") as inputs:
            with self.assertRaisesRegex(ValueError, "new project artifacts"):
                preflight.run(outside)
            inputs.assert_not_called()
        self.assertFalse(outside.exists())

    def test_pixel_mapping_and_ignore_mask(self):
        image, ann = fixture()
        tensor, target, mask, counts = preflight._prepare(image, ann)
        self.assertEqual(tensor.shape, (1, 3, 512, 512))
        expected = db_inverse_targets.build_inverse_targets(512, 512, [(64., 64., 128., 128.)]).target
        np.testing.assert_array_equal(target, expected)
        self.assertEqual(mask[0, 336, 336], 0)
        self.assertTrue((mask[target == 1] == 1).all())
        self.assertEqual(counts["visible_x_ticks"], 1)
        self.assertEqual(counts["marker_artifacts_ignored"], 1)

    def test_invalid_source_and_unsupported_labels_fail(self):
        image, ann = fixture()
        ann["panels"][0]["texts"][0]["rendered_pixel_box"] = [63, 0, 2, 2]
        with self.assertRaisesRegex(ValueError, "escapes"):
            preflight._prepare(image, ann)
        image, ann = fixture()
        ann["panels"][0]["texts"][0]["partial"] = True
        with self.assertRaisesRegex(ValueError, "visibility"):
            preflight._prepare(image, ann)
        image, ann = fixture()
        ann["canvas"]["width"] = 63
        with self.assertRaisesRegex(ValueError, "canvas"):
            preflight._prepare(image, ann)

    def test_mocked_extraction_writes_hashed_caches_and_prevents_overwrite(self):
        image, ann = fixture()
        row = {name: {"sha256": str(index) * 64} for index, name in enumerate(("image", "annotation", "scene"), 1)}

        class Session:
            def __init__(self, *_args, **_kwargs):
                pass
            def get_providers(self):
                return ["CPUExecutionProvider"]
            def run(self, _names, inputs):
                tensor = next(iter(inputs.values()))
                return [np.zeros((1, 96, tensor.shape[2] // 4, tensor.shape[3] // 4), dtype=np.float32)]

        tmp_root = preflight.parity.ROOT / "artifacts" / "test-temp"
        tmp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=tmp_root) as tmp:
            output = Path(tmp) / "new"
            with mock.patch.object(preflight, "_train_inputs", return_value=[(row, image, ann)] * 4), \
                 mock.patch.object(preflight.parity, "_read_locked", return_value=b"model"), \
                 mock.patch.object(preflight.onnx, "load_model_from_string", return_value=object()), \
                 mock.patch.object(preflight.parity, "_trunk_bytes", return_value=b"trunk"), \
                 mock.patch.object(preflight, "TRUNK_SHA", sha256(b"trunk").hexdigest()), \
                 mock.patch.object(preflight.ort, "InferenceSession", Session):
                report = preflight.run(output)
                self.assertEqual(report["optimizer_steps"], 0)
                self.assertFalse(report["runtime_tensor_parity_verified"])
                self.assertFalse(report["training_input_approved"])
                self.assertEqual(report["holdout_raster_or_label_reads"], 0)
                self.assertEqual(json.loads((output / "report.json").read_bytes()), report)
                for case in report["cases"]:
                    for item in case["arrays"].values():
                        self.assertEqual(sha256((output / item["file"]).read_bytes()).hexdigest(), item["sha256"])
                with self.assertRaisesRegex(ValueError, "new project"):
                    preflight.run(output)


if __name__ == "__main__":
    unittest.main()
