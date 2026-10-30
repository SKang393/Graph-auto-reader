# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Archive tests use invented in-memory data, without models or training."""
from __future__ import annotations

import hashlib
import hmac
from pathlib import Path
import struct
import tempfile
import unittest

from .positive_archive import PositiveArchive

_KEY = b"k" * 32
_RUN = b"r" * 16
_PROVENANCE = tuple(hashlib.sha256(bytes([i])).digest() for i in range(8))
_PATCH = struct.pack("<3267f", *([0.25] * 3267))
_PATCH2 = struct.pack("<3267f", *([0.75] * 3267))


def _build(batches=None, *, footer_counts=None, dimensions=(3, 33, 33), provenance=_PROVENANCE):
    if batches is None:
        batches = [(b"a" * 32, [(350.5, 22.0, _PATCH), (400.0, 23.0, _PATCH2)]),
                   (b"b" * 32, [(350.5, 22.0, _PATCH2)])]
    result = bytearray(struct.pack("<8s16sIII", b"GAPOS001", _RUN, *dimensions))
    for source, records in batches:
        result += b"\x01" + source + struct.pack("<I", len(records))
        for x, y, patch in records:
            result += struct.pack("<dd", x, y) + patch
    counts = footer_counts if footer_counts is not None else (len(batches), sum(len(r) for _, r in batches))
    result += b"\x00" + b"".join(provenance) + struct.pack("<QQ", *counts)
    result += hmac.new(_KEY, result, hashlib.sha256).digest()
    return bytes(result)


class PositiveArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=r"E:\Graph-auto-reader\.codex")
        self.path = Path(self.temp.name) / "fixture.bin"

    def tearDown(self):
        self.temp.cleanup()

    def _open(self, data, **kwargs):
        self.path.write_bytes(data)
        expected = dict(key=_KEY, file_sha256=hashlib.sha256(data).digest(), run_id=_RUN, provenance=_PROVENANCE)
        expected.update(kwargs)
        return PositiveArchive(self.path, **expected)

    def test_authenticated_handle_and_stable_rows(self):
        data = _build()
        with self._open(data) as archive:
            self.assertEqual(archive.count, 3)
            ids = archive.row_identities
            self.assertEqual(len(ids), 96)
            self.assertEqual(len({ids[i:i + 32] for i in (0, 32, 64)}), 3)
            self.assertEqual(archive.read_patch(0), _PATCH)
            self.assertEqual(archive.read_patch(1), _PATCH2)
            with self.assertRaises((PermissionError, OSError)):
                with open(self.path, "r+b"):
                    pass
            with self.assertRaises(IndexError):
                archive.read_patch(True)
        with self.assertRaises(ValueError):
            archive.read_patch(0)
        with self._open(data) as archive:
            self.assertEqual(archive.row_identities, ids)

    def test_existing_writer_denies_authentication(self):
        data = _build()
        self.path.write_bytes(data)
        with open(self.path, "r+b"):
            with self.assertRaises((PermissionError, OSError)):
                PositiveArchive(self.path, key=_KEY, file_sha256=hashlib.sha256(data).digest(),
                                run_id=_RUN, provenance=_PROVENANCE)

    def test_empty_image_batch_is_allowed_but_source_is_unique(self):
        data = _build(batches=[(b"a" * 32, []), (b"b" * 32, [(3.0, 7.0, _PATCH)])])
        with self._open(data) as archive:
            self.assertEqual(archive.count, 1)
            self.assertEqual(archive.read_patch(0), _PATCH)
        duplicate_image = _build(batches=[(b"a" * 32, []), (b"a" * 32, [(3.0, 7.0, _PATCH)])])
        with self.assertRaisesRegex(ValueError, "duplicate source"):
            self._open(duplicate_image)

    def test_trust_values_tampering_trailing_and_truncation_rejected(self):
        data = _build()
        for override in ({"key": b"z" * 32}, {"file_sha256": b"x" * 32},
                         {"run_id": b"z" * 16}, {"provenance": (b"z" * 32,) * 8},
                         {"provenance": _PROVENANCE[:7]}):
            with self.subTest(override=list(override)), self.assertRaises(ValueError):
                self._open(data, **override)
        tampered = bytearray(data)
        tampered[100] ^= 1
        for candidate in (bytes(tampered), data[:-1], data + b"x"):
            with self.subTest(length=len(candidate)), self.assertRaises(ValueError):
                self._open(candidate)

    def test_authenticated_invalid_contents_rejected(self):
        invalid_patches = [struct.pack("<f", value) + _PATCH[4:] for value in (float("nan"), float("inf"), -0.1, 1.1)]
        cases = [_build(dimensions=(1, 33, 33)), _build(footer_counts=(1, 3)),
                 _build(batches=[(b"a" * 32, [(float("nan"), 2.0, _PATCH)])]),
                 _build(batches=[(b"a" * 32, [(-1.0, 2.0, _PATCH)])]),
                 _build(batches=[(b"a" * 32, [(1_000_001.0, 2.0, _PATCH)])]),
                 _build(batches=[(b"a" * 32, [(1.0, 2.0, _PATCH), (1.0, 2.0, _PATCH2)])]),
                 _build(batches=[(b"a" * 32, [(1.0, 2.0, _PATCH)]),
                                 (b"a" * 32, [(1.0, 2.0, _PATCH2)])]),
                 _build(batches=[])]
        cases.extend(_build(batches=[(b"a" * 32, [(1.0, 2.0, patch)])]) for patch in invalid_patches)
        for candidate in cases:
            with self.subTest(index=cases.index(candidate)), self.assertRaises(ValueError):
                self._open(candidate)


if __name__ == "__main__":
    unittest.main()
