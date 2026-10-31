# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from pathlib import Path
import shutil
from tempfile import TemporaryDirectory
import unittest
from unittest import mock

from PIL import ImageFont

from ml.synthetic.public_fonts_v1 import fonts


class PublicFontTests(unittest.TestCase):
    def setUp(self):
        temp_root = fonts.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        self.temporary = TemporaryDirectory(dir=temp_root)
        self.addCleanup(self.temporary.cleanup)
        self.package = Path(self.temporary.name) / "fonts"
        shutil.copytree(fonts.PACKAGE, self.package)

    def test_valid_assets_have_distinct_verified_families(self):
        paths = fonts.verify(self.package)
        self.assertEqual(set(paths), {"sans", "serif", "mono"})
        families = {ImageFont.truetype(str(path), 24).getname()[0] for path in paths.values()}
        self.assertEqual(families, {"Noto Sans", "Noto Serif", "Noto Sans Mono"})

    def test_changed_font_and_license_bytes_fail(self):
        font = self.package / "assets" / "NotoSerif-Regular.ttf"
        original = font.read_bytes()
        font.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
        with self.assertRaisesRegex(ValueError, "bytes differ"):
            fonts.verify(self.package)
        font.write_bytes(original)
        (self.package / "assets" / "LICENSE").write_bytes(b"different license")
        with self.assertRaisesRegex(ValueError, "bytes differ"):
            fonts.verify(self.package)

    def test_manifest_or_asset_inventory_tampering_fails(self):
        manifest = self.package / "manifest.json"
        original = manifest.read_bytes()
        manifest.write_bytes(original + b" ")
        with self.assertRaisesRegex(ValueError, "manifest hash"):
            fonts.verify(self.package)
        manifest.write_bytes(original)
        (self.package / "assets" / "unlisted.ttf").write_bytes(b"extra")
        with self.assertRaisesRegex(ValueError, "asset inventory"):
            fonts.verify(self.package)

    def test_wrong_family_metadata_cannot_pass(self):
        fake_font = mock.Mock()
        fake_font.getname.return_value = ("Unrelated Family", "Regular")
        with mock.patch.object(fonts.ImageFont, "truetype", return_value=fake_font):
            with self.assertRaisesRegex(ValueError, "metadata"):
                fonts.verify(self.package)


if __name__ == "__main__":
    unittest.main()
