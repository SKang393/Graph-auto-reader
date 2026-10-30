# SPDX-License-Identifier: Apache-2.0
"""Synthetic candidate files only; no data, GPU, inference or optimizer."""
from hashlib import sha256
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import torch

from . import checkpoint_writer as writer
from .model import RealPositiveCenterNet
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet


class CheckpointWriterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=r"E:\Graph-auto-reader\artifacts\test-temp")
        self.directory = Path(self.temp.name)
        self.base = self.directory / "fixture-base.pt"
        self.output = self.directory / "fixture-candidate.pt"
        self.model = RealPositiveCenterNet(SpatialCenterNet(ScaleClassifierNet(ModelConfig())))
        with torch.no_grad():
            self.model.residual.weight.fill_(0.125)
            self.model.residual.bias.fill_(0.25)
        torch.save({"state_dict": self.model.local.state_dict()}, self.base)
        self.base_hash = sha256(self.base.read_bytes()).hexdigest()
        self.patches = [patch.object(writer, "_ALLOWED_BASES", (self.base,)),
                        patch.object(writer, "V37_CHECKPOINT_SHA256", self.base_hash)]
        for item in self.patches:
            item.start()

    def tearDown(self):
        for item in reversed(self.patches):
            item.stop()
        self.temp.cleanup()

    def _write(self):
        return writer.write_candidate_checkpoint(self.model, base_path=self.base, destination=self.output,
                                                   revision="fixture-v1", candidate_id="P1")

    def test_round_trip_hash_state_and_rename_lock(self):
        original = writer._rename_without_replacement
        def checked(stream, destination):
            original(stream, destination)
            with self.assertRaises((PermissionError, OSError)):
                with open(destination, "r+b"):
                    pass
        with patch.object(writer, "_rename_without_replacement", checked):
            receipt = self._write()
        self.assertEqual(receipt.sha256, sha256(self.output.read_bytes()).hexdigest())
        self.assertEqual(receipt.base_checkpoint_sha256, self.base_hash)
        self.assertEqual(writer._ROOT / receipt.relative_path, self.output)
        payload = torch.load(self.output, map_location="cpu", weights_only=True)
        self.assertEqual(payload["revision"], "fixture-v1")
        for name, value in self.model.state_dict().items():
            self.assertTrue(torch.equal(value, payload["state_dict"][name]))
        self.assertEqual(list(self.directory.glob("*.tmp")), [])

    def test_existing_destination_is_never_overwritten(self):
        self.output.write_bytes(b"existing")
        with self.assertRaises(OSError):
            self._write()
        self.assertEqual(self.output.read_bytes(), b"existing")
        self.assertEqual(list(self.directory.glob("*.tmp")), [])

    def test_corruption_during_publication_removes_owned_output(self):
        original = writer._rename_without_replacement
        def corrupt(stream, destination):
            stream.seek(0)
            stream.write(b"\x00")
            original(stream, destination)
        with patch.object(writer, "_rename_without_replacement", corrupt):
            with self.assertRaisesRegex(ValueError, "changed before finalization"):
                self._write()
        self.assertFalse(self.output.exists())
        self.assertEqual(list(self.directory.glob("*.tmp")), [])

    def test_changed_base_and_nonfinite_residual_fail_before_publish(self):
        original = self.model.local.confidence.output.bias.detach().clone()
        with torch.no_grad():
            self.model.local.confidence.output.bias.add_(1)
        with self.assertRaisesRegex(ValueError, "differ"):
            self._write()
        self.assertFalse(self.output.exists())
        with torch.no_grad():
            self.model.local.confidence.output.bias.copy_(original)
            self.model.residual.bias.fill_(float("nan"))
        with self.assertRaisesRegex(ValueError, "non-finite"):
            self._write()
        self.assertFalse(self.output.exists())

    def test_invalid_paths_and_wrong_base_identity_fail_closed(self):
        with self.assertRaises(ValueError):
            writer.write_candidate_checkpoint(self.model, base_path=self.base, destination=Path("relative.pt"),
                                              revision="fixture-v1", candidate_id="P1")
        with patch.object(writer, "V37_CHECKPOINT_SHA256", "0" * 64):
            with self.assertRaisesRegex(ValueError, "bound identity"):
                self._write()
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
