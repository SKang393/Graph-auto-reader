# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authenticate and consume a single immutable real-positive archive handle.

The caller authenticates the expected values and key separately. This reader
does not establish image, split, label or preprocessor provenance itself. No
patch is exposed until the entire archive, footer and EOF have been checked.
"""
from __future__ import annotations

from array import array
import ctypes
import hashlib
import hmac
import math
import os
import struct
import sys
from threading import RLock
from typing import BinaryIO

_MAGIC = b"GAPOS001"
_HEADER = struct.Struct("<8s16sIII")
_COORDS = struct.Struct("<dd")
_U32 = struct.Struct("<I")
_COUNTS = struct.Struct("<QQ")
_PATCH_BYTES = 3 * 33 * 33 * 4
_MAX_FILE_BYTES = 8 * 1024 * 1024 * 1024
_MAX_BATCHES = 100_000
_MAX_POSITIVES = 500_000
_MAX_COORDINATE = 1_000_000.0
_IDENTITY_DOMAIN = b"graphreader.real-positive-archive.row.v1\0"


def _open_read_only_no_writers(path: str | os.PathLike[str]) -> BinaryIO:
    """Windows symmetric share checks also reject preexisting write handles."""
    if os.name != "nt":
        raise OSError("Real-positive archive consumption requires Windows share enforcement")
    import msvcrt
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    create_file = kernel.CreateFileW
    create_file.argtypes = (ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                            ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p)
    create_file.restype = ctypes.c_void_p
    handle = create_file(os.fspath(path), 0x80000000, 0x00000001, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        fd = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
    except BaseException:
        kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
        kernel.CloseHandle(handle)
        raise
    try:
        return os.fdopen(fd, "rb", buffering=0)
    except BaseException:
        os.close(fd)
        raise


def _bytes_of_length(value: object, length: int, name: str) -> bytes:
    if type(value) is not bytes or len(value) != length:
        raise ValueError(f"Expected trusted {name} must be {length} bytes")
    return value


def _check_patch(data: bytes) -> None:
    values = array("f")
    if values.itemsize != 4:
        raise RuntimeError("Platform float32 format is unavailable")
    values.frombytes(data)
    if sys.byteorder != "little":
        values.byteswap()
    if any(not math.isfinite(value) or value < 0.0 or value > 1.0 for value in values):
        raise ValueError("Archive contains an invalid tensor value")


class PositiveArchive:
    """Verified patches remain on one no-writer handle until close.

    `row_identities` is an immutable concatenation of fixed 32-byte row IDs for
    `FixedSchedule`. `read_patch` returns only the requested authenticated CHW
    bytes (little-endian float32); no image hash or coordinate is exposed.
    Provenance order is binding, protocol, study assignment, selected inventory,
    OCR detector, OCR recognizer, marker center, and classifier.
    """

    def __init__(self, path: str | os.PathLike[str], *, key: bytes, file_sha256: bytes,
                 run_id: bytes, provenance: tuple[bytes, ...]) -> None:
        _bytes_of_length(key, 32, "HMAC key")
        _bytes_of_length(file_sha256, 32, "file SHA-256")
        _bytes_of_length(run_id, 16, "run ID")
        if type(provenance) is not tuple or len(provenance) != 8:
            raise ValueError("Exactly eight independently trusted provenance hashes are required")
        for digest in provenance:
            _bytes_of_length(digest, 32, "provenance digest")
        self._handle = _open_read_only_no_writers(path)
        self._lock = RLock()
        self._rows: list[tuple[int, bytes, bytes]] = []
        self._row_identities = b""
        try:
            self._authenticate(key, file_sha256, run_id, provenance)
        except BaseException:
            self._handle.close()
            self._rows.clear()
            raise

    def _authenticate(self, key: bytes, file_sha256: bytes, run_id: bytes,
                      provenance: tuple[bytes, ...]) -> None:
        size = os.fstat(self._handle.fileno()).st_size
        if size < _HEADER.size + 1 + 8 * 32 + _COUNTS.size + 32 or size > _MAX_FILE_BYTES:
            raise ValueError("Archive size is outside the allowed bounds")
        complete_digest = hashlib.sha256()
        mac = hmac.new(key, digestmod=hashlib.sha256)

        def read(length: int, *, mac_bytes: bool = True) -> bytes:
            data = self._handle.read(length)
            if len(data) != length:
                raise ValueError("Archive is truncated")
            complete_digest.update(data)
            if mac_bytes:
                mac.update(data)
            return data

        magic, actual_run, channels, height, width = _HEADER.unpack(read(_HEADER.size))
        if magic != _MAGIC or actual_run != run_id or (channels, height, width) != (3, 33, 33):
            raise ValueError("Archive header does not match the admitted input")
        row_ids: list[bytes] = []
        seen_coordinates: set[tuple[bytes, float, float]] = set()
        seen_batches: set[bytes] = set()
        seen_sources: set[bytes] = set()
        batches = positives = 0
        while True:
            tag = read(1)
            if tag == b"\x00":
                break
            if tag != b"\x01" or batches >= _MAX_BATCHES:
                raise ValueError("Archive batch tag or count is invalid")
            source = read(32)
            if source in seen_sources:
                raise ValueError("Archive contains duplicate source images")
            seen_sources.add(source)
            count_bytes = read(4)
            count = _U32.unpack(count_bytes)[0]
            if count > _MAX_POSITIVES - positives:
                raise ValueError("Archive positive count is outside the allowed bounds")
            if count > (size - self._handle.tell()) // (_COORDS.size + _PATCH_BYTES):
                raise ValueError("Archive batch is truncated")
            batch_hash = hashlib.sha256(tag + source + count_bytes)
            entries: list[tuple[int, bytes, bytes, int]] = []
            for ordinal in range(count):
                coords = read(_COORDS.size)
                x, y = _COORDS.unpack(coords)
                if not (math.isfinite(x) and math.isfinite(y) and 0.0 <= x <= _MAX_COORDINATE and
                        0.0 <= y <= _MAX_COORDINATE):
                    raise ValueError("Archive proposal coordinates are invalid")
                coordinate = (source, x, y)
                if coordinate in seen_coordinates:
                    raise ValueError("Archive contains duplicate proposals")
                seen_coordinates.add(coordinate)
                offset = self._handle.tell()
                patch = read(_PATCH_BYTES)
                _check_patch(patch)
                batch_hash.update(coords)
                batch_hash.update(patch)
                entries.append((offset, hashlib.sha256(patch).digest(), coords, ordinal))
            digest = batch_hash.digest()
            if digest in seen_batches:
                raise ValueError("Archive contains duplicate batches")
            seen_batches.add(digest)
            for offset, patch_digest, coords, ordinal in entries:
                identity = hashlib.sha256(_IDENTITY_DOMAIN + digest + coords + struct.pack("<Q", ordinal)).digest()
                self._rows.append((offset, patch_digest, identity))
                row_ids.append(identity)
            batches += 1
            positives += count
        footer_hashes = tuple(read(32) for _ in range(8))
        footer_batches, footer_positives = _COUNTS.unpack(read(_COUNTS.size))
        stored_mac = read(32, mac_bytes=False)
        if (not batches or not positives or footer_hashes != provenance or footer_batches != batches or
                footer_positives != positives or not hmac.compare_digest(stored_mac, mac.digest()) or
                not hmac.compare_digest(complete_digest.digest(), file_sha256) or self._handle.read(1)):
            raise ValueError("Archive authentication or footer validation failed")
        if len(set(row_ids)) != positives:
            raise ValueError("Archive row identities are not unique")
        self._row_identities = b"".join(row_ids)

    @property
    def row_identities(self) -> bytes:
        with self._lock:
            if self._handle.closed:
                raise ValueError("Archive is closed")
            return self._row_identities

    @property
    def count(self) -> int:
        with self._lock:
            if self._handle.closed:
                raise ValueError("Archive is closed")
            return len(self._rows)

    def read_patch(self, index: int) -> bytes:
        with self._lock:
            if self._handle.closed:
                raise ValueError("Archive is closed")
            if type(index) is not int or not 0 <= index < len(self._rows):
                raise IndexError("Archive row is outside the authenticated population")
            offset, digest, _ = self._rows[index]
            self._handle.seek(offset)
            data = self._handle.read(_PATCH_BYTES)
            if len(data) != _PATCH_BYTES or not hmac.compare_digest(hashlib.sha256(data).digest(), digest):
                self.close()
                raise ValueError("Authenticated patch changed while the archive was open")
            return data

    def close(self) -> None:
        with self._lock:
            self._handle.close()
            self._rows.clear()
            self._row_identities = b""

    def __enter__(self) -> "PositiveArchive":
        if self._handle.closed:
            raise ValueError("Archive is closed")
        return self

    def __exit__(self, exc_type, exc_value, traceback) -> None:
        self.close()
