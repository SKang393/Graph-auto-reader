# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Verify project-local public fonts without host-font fallback."""
from hashlib import sha1, sha256
import json
import os
from pathlib import Path
import stat

from PIL import ImageFont

ROOT = Path(__file__).resolve().parents[3]
PACKAGE = Path(__file__).resolve().parent
MANIFEST_SHA256 = "0eb134a6e980e3042360b6477bf2ce178921d7dc23b3ded9ba12ee661b9470a2"
EXPECTED_FAMILIES = {"sans": "Noto Sans", "serif": "Noto Serif", "mono": "Noto Sans Mono"}
EXPECTED_FILES = {"LICENSE", "NotoSans-Regular.ttf", "NotoSerif-Regular.ttf", "NotoSansMono-Regular.ttf"}


def _object(data):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate font manifest field")
            result[key] = value
        return result
    value = json.loads(data, object_pairs_hook=pairs)
    if not isinstance(value, dict):
        raise ValueError("Font manifest must be an object")
    return value


def _safe_file(root, relative):
    if not isinstance(relative, str) or Path(relative).is_absolute() or ".." in Path(relative).parts:
        raise ValueError("Font asset path is unsafe")
    path = root
    for part in Path(relative).parts:
        path = path / part
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            raise ValueError("Font asset contains a link or reparse point")
    if not path.resolve(strict=True).is_relative_to(root) or not path.is_file():
        raise ValueError("Font asset escapes package")
    return path


def _verify_bytes(root, record):
    path = _safe_file(root, record["file"])
    payload = path.read_bytes()
    git_blob = sha1(b"blob " + str(len(payload)).encode("ascii") + b"\0" + payload).hexdigest()
    if sha256(payload).hexdigest() != record.get("sha256") or git_blob != record.get("upstream_git_blob_sha1"):
        raise ValueError("Font or license bytes differ from fixed upstream")
    return path


def verify(package: Path = PACKAGE) -> dict[str, Path]:
    """Return distinct checked family files; no system font discovery occurs."""
    root = Path(package).resolve(strict=True)
    if not root.is_relative_to(ROOT.resolve(strict=True)):
        raise ValueError("Font package must remain inside project")
    manifest_path = _safe_file(root, "manifest.json")
    raw = manifest_path.read_bytes()
    if sha256(raw).hexdigest() != MANIFEST_SHA256:
        raise ValueError("Font manifest hash differs")
    manifest = _object(raw)
    if (manifest.get("schema") != "graphreader.public-synthetic-fonts.v1" or
            manifest.get("source_commit") != "ffebf8c1ee449e544955a7e813c54f9b73848eac" or
            manifest.get("training_approved") is not False or manifest.get("legal_approval_claimed") is not False or
            manifest.get("historical_benchmark_independence") != "unproven"):
        raise ValueError("Font manifest scope differs")
    asset_dir = root / "assets"
    if not asset_dir.is_dir() or set(os.listdir(asset_dir)) != EXPECTED_FILES:
        raise ValueError("Font package asset inventory differs")
    license_record = manifest.get("license", {})
    if license_record.get("id") != "OFL-1.1" or license_record.get("file") != "assets/LICENSE":
        raise ValueError("Font license binding differs")
    _verify_bytes(root, license_record)
    rows = manifest.get("fonts")
    if not isinstance(rows, list) or len(rows) != 3:
        raise ValueError("Font family inventory differs")
    results, families, hashes = {}, set(), set()
    for row in rows:
        key = row.get("id")
        if (key not in EXPECTED_FAMILIES or key in results or row.get("family") != EXPECTED_FAMILIES[key] or
                row.get("style") != "Regular" or row.get("license_id") != "OFL-1.1" or row.get("license_file") != "assets/LICENSE"):
            raise ValueError("Font family or license metadata differs")
        path = _verify_bytes(root, row)
        font = ImageFont.truetype(str(path), 24)
        family, style = font.getname()
        if family != EXPECTED_FAMILIES[key] or style != "Regular" or family in families or row["sha256"] in hashes:
            raise ValueError("Resolved font metadata or byte independence differs")
        if any(font.getmask(char).getbbox() is None for char in "0123456789.-"):
            raise ValueError("Font lacks required numeric glyphs")
        advances = [font.getlength(char) for char in "iW01"]
        if key == "mono" and len(set(advances)) != 1:
            raise ValueError("Mono font has nonuniform advance widths")
        if key != "mono" and len(set(advances)) == 1:
            raise ValueError("Proportional font unexpectedly has uniform widths")
        results[key] = path
        families.add(family)
        hashes.add(row["sha256"])
    if set(results) != set(EXPECTED_FAMILIES):
        raise ValueError("Font family mapping is incomplete")
    return results


if __name__ == "__main__":
    paths = verify()
    print(json.dumps({"status": "verified", "families": sorted(paths), "training_approved": False}))
