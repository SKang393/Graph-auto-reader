# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
import sys
import stat
from types import SimpleNamespace
from tempfile import TemporaryDirectory
from pathlib import Path
from unittest import TestCase, main
from unittest.mock import patch

from ml.synthetic.axis_roles_dev_dataset_v4 import prepare

PIL_WAS_IMPORTED_BY_PARENT_MODULE = "PIL" in sys.modules
V3_WRAPPER_IMPORTED_IN_PARENT = "ml.synthetic.axis_roles_dev_dataset_v3.prepare" in sys.modules
from PIL import Image  # noqa: E402


class AxisRolesDevV4Tests(TestCase):
    def test_protocol_is_fixed_development_catalog_only(self) -> None:
        protocol = prepare.load_protocol()
        recipes = protocol["recipes"]
        self.assertEqual(len(recipes), 16)
        self.assertEqual(len({item["seed"] for item in recipes}), 16)
        self.assertEqual({item["overlay"] for item in recipes}, {"none", "grid", "frame", "grid_frame"})
        self.assertEqual({item["participant_format"] for item in recipes}, {"full", "numeric"})
        self.assertTrue(all(set(item) == {"index", "seed", "design", "overlay", "participant_format"} for item in recipes))
        self.assertIn("historical independence and near-duplicate exclusion are unproven", protocol["claims_and_limits"][1])
        self.assertFalse(any("reserved" in item for item in recipes))

    def test_font_and_license_are_bound_to_upstream_assets(self) -> None:
        protocol = prepare.load_protocol()
        bindings = protocol["upstream_source_bindings"]
        font = protocol["font_provenance"]
        self.assertEqual(font["sha256"], bindings["ml/synthetic/camera_document_profile_v1/assets/NotoSans-Regular.ttf"])
        self.assertEqual(font["license_sha256"], bindings["ml/synthetic/camera_document_profile_v1/assets/LICENSE"])
        self.assertEqual(font["license"], "OFL-1.1")
        self.assertIn("ffebf8c1ee449e544955a7e813c54f9b73848eac", font["source_url"])
        self.assertIn("no broader legal review is asserted", font["claims"])

    def test_executed_parent_package_initializer_is_bound(self) -> None:
        expected = "6875b53c0bbbd101cb06ff7987f06ea66520493f5361456ebc1d724870d13add"
        protocol = prepare.load_protocol()
        self.assertEqual(protocol["upstream_source_bindings"]["ml/synthetic/__init__.py"], expected)
        self.assertEqual(prepare._sha((prepare.ROOT / "ml/synthetic/__init__.py").read_bytes()), expected)

    def test_parent_import_does_not_load_pil_or_v3_wrapper(self) -> None:
        self.assertFalse(PIL_WAS_IMPORTED_BY_PARENT_MODULE)
        self.assertFalse(V3_WRAPPER_IMPORTED_IN_PARENT)
        self.assertNotIn("PIL", prepare.__dict__)

    def test_child_processes_are_independent_and_deterministic(self) -> None:
        original_new = Image.new
        parent_image = Image.new("RGB", (3, 2), (19, 37, 61))
        first_recipe, second_recipe = prepare.load_protocol()["recipes"][:2]
        first_pair = prepare.run_children([first_recipe, second_recipe], max_workers=2)
        self.assertEqual(len(first_pair), 2)
        self.assertNotEqual(first_pair[0]["child_pid"], first_pair[1]["child_pid"])
        self.assertNotIn(first_pair[0]["child_pid"], {__import__("os").getpid()})
        self.assertNotIn(first_pair[1]["child_pid"], {__import__("os").getpid()})
        repeated = prepare.run_children([first_recipe], max_workers=1)[0]
        self.assertEqual(first_pair[0]["hashes"], repeated["hashes"])
        self.assertEqual(first_pair[0]["scene"], repeated["scene"])
        self.assertEqual(first_pair[0]["annotation"], repeated["annotation"])
        self.assertEqual(first_pair[0]["overlay"], repeated["overlay"])
        self.assertEqual(first_pair[0]["image_png"], repeated["image_png"])
        self.assertIs(Image.new, original_new)
        self.assertEqual(parent_image.getpixel((1, 1)), (19, 37, 61))
        self.assertNotIn("ml.synthetic.axis_roles_dev_dataset_v3.prepare", sys.modules)

    def test_references_are_checked_by_parent_too(self) -> None:
        recipe = prepare.load_protocol()["recipes"][0]
        result = prepare.run_children([recipe], max_workers=1)[0]
        prepare._validate_scene_references(result["scene"], result["annotation"], result["overlay"], recipe, prepare.load_protocol())
        broken_annotation = deepcopy(result["annotation"])
        broken_annotation["panels"][0]["panel_id"] = "not-the-source-panel"
        with self.assertRaisesRegex(ValueError, "annotation panel references"):
            prepare._validate_scene_references(result["scene"], broken_annotation, result["overlay"], recipe, prepare.load_protocol())

    def test_unlisted_recipe_fails_before_starting_children(self) -> None:
        recipe = dict(prepare.load_protocol()["recipes"][0])
        recipe["seed"] += 1
        with self.assertRaisesRegex(ValueError, "not in the fixed development catalog"):
            prepare.run_children([recipe], max_workers=1)

    def test_source_binding_is_hash_checked(self) -> None:
        with TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "source.txt"
            source.write_bytes(b"bound bytes")
            protocol = {"upstream_source_bindings": {"source.txt": sha256(b"bound bytes").hexdigest()}}
            with patch.object(prepare, "ROOT", root):
                prepare._source_bindings(protocol)
                source.write_bytes(b"changed bytes")
                with self.assertRaisesRegex(ValueError, "hash differs"):
                    prepare._source_bindings(protocol)

    def test_protocol_bytes_are_independently_pinned(self) -> None:
        with TemporaryDirectory() as temp:
            tampered_protocol = Path(temp) / "protocol.json"
            tampered_protocol.write_text('{"schema":"changed"}\n', encoding="utf-8")
            with patch.object(prepare, "PROTOCOL_PATH", tampered_protocol):
                with self.assertRaisesRegex(ValueError, "Pinned v4 protocol bytes differ"):
                    prepare.load_protocol()

    def test_inventory_rejects_reparse_entry_before_descending(self) -> None:
        class FakeEntry:
            def __init__(self, path: Path, info: SimpleNamespace) -> None:
                self.path = str(path)
                self.info = info

            def stat(self, follow_symlinks: bool = True) -> SimpleNamespace:
                self.assert_no_follow = not follow_symlinks
                return self.info

        class FakeScan:
            def __init__(self, entries: list[FakeEntry]) -> None:
                self.entries = entries

            def __enter__(self) -> "FakeScan":
                return self

            def __exit__(self, *_args: object) -> None:
                return None

            def __iter__(self):
                return iter(self.entries)

        with TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "nested").mkdir()
            (root / "known.txt").write_bytes(b"known")
            (root / "nested" / "file.txt").write_bytes(b"file")
            self.assertEqual(prepare._safe_inventory(root), {"known.txt", "nested/file.txt"})

            junction = root / "external-junction"
            fake_entry = FakeEntry(junction, SimpleNamespace(st_mode=stat.S_IFDIR, st_file_attributes=0x400))
            fake_scan = FakeScan([fake_entry])
            with patch.object(prepare.os, "scandir", return_value=fake_scan) as scandir:
                with self.assertRaisesRegex(ValueError, "reparse point"):
                    prepare._safe_inventory(root)
            self.assertEqual(scandir.call_count, 1)
            self.assertTrue(fake_entry.assert_no_follow)


if __name__ == "__main__":
    main()
