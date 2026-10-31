# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from hashlib import sha256
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest import mock

from ml.synthetic.camera_document_dev_dataset_v1 import prepare


def label_fixture():
    scene = {"scene_id": "scene", "canvas": {"width": 20, "height": 20},
             "panels": [{"panel_id": "panel-a", "ticks": [{"axis": "x", "hidden": False,
                       "label": "1", "value": 1, "center": [3.5, 1]}]}], "annotations": {"text_regions": [
        {"region_id": "text-a", "panel_id": "panel-a", "box": [1, 2, 5, 3], "text": "1", "role": "x_tick", "visible": True}]}}
    annotation = {"scene_id": "scene", "coordinate_space": "original_pixels", "canvas": {"width": 20, "height": 20},
                  "degradations": [{"kind": "camera_document", "geometry_transformed": True,
                                    "forward_matrix_3x3": [1, 0, 2, 0, 1, 3, 0, 0, 1]}],
                  "panels": [{"panel_id": "panel-a", "texts": [{"text_id": "text-a", "region_id": "text-a",
                    "panel_id": "panel-a", "box": [3, 5, 5, 3], "rendered_pixel_box": [3, 5, 4, 2],
                    "position": [3, 5], "text": "1", "role": "x_tick", "visible": True,
                    "coordinate_space": "original_pixels"}]}]}
    return scene, annotation


class DatasetTests(unittest.TestCase):
    def test_text_identity_role_and_transformed_position(self):
        scene, annotation = label_fixture()
        identities = set()
        roles, values = prepare.validate_labels(scene, annotation, identities)
        self.assertEqual(dict(roles), {"x_tick": 1})
        self.assertEqual(values, {"1"})
        self.assertEqual(identities, {"text-a"})
        annotation["panels"][0]["texts"][0]["position"] = [3, 6]
        with self.assertRaisesRegex(ValueError, "transform"):
            prepare.validate_labels(scene, annotation, set())

    def test_semantic_and_duplicate_mismatches_fail(self):
        scene, annotation = label_fixture()
        annotation["panels"][0]["texts"][0]["text"] = "2"
        with self.assertRaisesRegex(ValueError, "semantics"):
            prepare.validate_labels(scene, annotation, set())
        scene, annotation = label_fixture()
        with self.assertRaisesRegex(ValueError, "duplicated"):
            prepare.validate_labels(scene, annotation, {"text-a"})
        scene, annotation = label_fixture()
        annotation["panels"][0]["texts"][0]["rendered_pixel_box"] = [19, 5, 2, 2]
        with self.assertRaisesRegex(ValueError, "outside"):
            prepare.validate_labels(scene, annotation, set())
        scene, annotation = label_fixture()
        scene["panels"][0]["ticks"][0]["value"] = float("nan")
        with self.assertRaisesRegex(ValueError, "source tick"):
            prepare.validate_labels(scene, annotation, set())

    def test_project_output_and_unlisted_inventory_fail_closed(self):
        with self.assertRaisesRegex(ValueError, "new project artifacts"):
            prepare.generate(prepare.ROOT / "ml" / "not-a-dataset-output")
        temp_root = prepare.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as tmp:
            dataset = Path(tmp)
            snapshot = {"commit": "synthetic"}
            data = {"schema": prepare.SCHEMA, "profile": prepare.FAMILY,
                    "recipes": [list(item) for item in prepare.RECIPES], "training_approved": False,
                    "private_data": False, "sealed": False, "optimizer_steps": 0,
                    "source_snapshot": snapshot, "artifact_sha256": {}}
            payload = json.dumps(data).encode()
            (dataset / "manifest.json").write_bytes(payload)
            (dataset / "unexpected.txt").write_bytes(b"synthetic")
            with mock.patch.object(prepare, "_snapshot", return_value=snapshot):
                with self.assertRaisesRegex(ValueError, "inventory"):
                    prepare.verify(dataset, sha256(payload).hexdigest())


if __name__ == "__main__":
    unittest.main()
