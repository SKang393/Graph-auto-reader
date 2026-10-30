# SPDX-License-Identifier: Apache-2.0
"""Optional synthetic interoperability test using the actual C# writer source.

Build cross_language_fixture.csproj first and set GRAPH_READER_CROSS_LANGUAGE=1.
The test itself runs an already-built DLL and never builds or reads real data.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest

from .positive_archive import PositiveArchive

_ROOT = Path(r"E:\Graph-auto-reader")
_DLL = _ROOT / "ml/markers/center/real_positive_context_v1/bin/Release/net10.0/GraphReader.ArchiveFixture.dll"


@unittest.skipUnless(os.environ.get("GRAPH_READER_CROSS_LANGUAGE") == "1", "Explicit synthetic C# fixture run not enabled")
class CrossLanguageArchiveTests(unittest.TestCase):
    def test_csharp_writer_python_reader(self):
        if not _DLL.is_file():
            self.fail("Synthetic C# fixture must be built before the test")
        with tempfile.TemporaryDirectory(dir=_ROOT / "artifacts/test-temp") as directory:
            path = Path(directory) / "fixture.bin"
            env = dict(os.environ, DOTNET_CLI_HOME=directory, TEMP=directory, TMP=directory,
                       DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_NOLOGO="1")
            completed = subprocess.run(["dotnet", str(_DLL), str(_ROOT), str(path)], cwd=_ROOT,
                                       env=env, check=True, capture_output=True, text=True, timeout=60)
            receipt = json.loads(completed.stdout)
            self.assertEqual((receipt["BatchCount"], receipt["PositiveCount"]), (2, 2))
            provenance = tuple(bytes.fromhex(str(i) * 64) for i in range(1, 9))
            with PositiveArchive(path, key=bytes(range(32)), file_sha256=bytes.fromhex(receipt["Sha256"]),
                                 run_id=bytes.fromhex(receipt["RunId"]), provenance=provenance) as archive:
                self.assertEqual(archive.count, 2)
                self.assertEqual(len(archive.row_identities), 64)
                self.assertEqual(archive.read_patch(0), struct.pack("<3267f", *([0.25] * 3267)))
                self.assertEqual(archive.read_patch(1), struct.pack("<3267f", *([0.75] * 3267)))


if __name__ == "__main__":
    unittest.main()
