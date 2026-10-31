# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from hashlib import sha256
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest import mock

from ml.ocr.gpu_db_head_probe_v1 import tensor_parity_identity as identity


class IdentityTests(unittest.TestCase):
    def test_changed_bound_script_is_rejected(self):
        temp_root = identity.parity.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as tmp:
            root = Path(tmp)
            (root / "artifacts").mkdir()
            source = root / "source.py"
            source.write_bytes(b"correct")
            payload = json.dumps({"schema": "graphreader.synthetic-tensor-parity-run-identity.v1",
                "files": {"source": {"path": "source.py", "sha256": sha256(b"correct").hexdigest()}}}).encode()
            descriptor = root / "artifacts" / "identity.json"
            descriptor.write_bytes(payload)
            with mock.patch.object(identity.parity, "ROOT", root), mock.patch.object(identity, "FILES", {"source": "source.py"}):
                identity.load(descriptor, sha256(payload).hexdigest())
                source.write_bytes(b"changed")
                with self.assertRaisesRegex(ValueError, "hash differs"):
                    identity.load(descriptor, sha256(payload).hexdigest())

    def test_substituted_capture_assembly_and_identity_are_rejected(self):
        files = {key: {"sha256": "a" * 64} for key in set(identity.CAPTURE_FIELDS.values()) | identity.CAPTURE_EXTRA}
        capture = {field: "a" * 64 for field in identity.CAPTURE_FIELDS}
        capture["additional_runtime_sha256"] = {key: "a" * 64 for key in identity.CAPTURE_EXTRA}
        capture["run_identity_sha256"] = "b" * 64
        identity.check_capture({"files": files}, capture, "b" * 64)
        capture["detector_assembly_sha256"] = "c" * 64
        with self.assertRaisesRegex(ValueError, "assembly"):
            identity.check_capture({"files": files}, capture, "b" * 64)
        with self.assertRaisesRegex(ValueError, "run identity"):
            identity.check_capture({"files": files}, capture, "d" * 64)
        capture["detector_assembly_sha256"] = "a" * 64
        capture["additional_runtime_sha256"]["project_imaging"] = "c" * 64
        with self.assertRaisesRegex(ValueError, "transitive runtime identity"):
            identity.check_capture({"files": files}, capture, "b" * 64)
