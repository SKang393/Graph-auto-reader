# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fetch only the authorized, fixed Windows artifacts. Never load native code."""
import base64
import csv
import hashlib
import io
import json
import pathlib
import urllib.request
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent
REV = "4e822c9036be2eb0e47ef556df32a818bb1572ef"
BASE = f"https://huggingface.co/Cactus-Compute/cactus/resolve/{REV}/releases/4.1.0/"
MANIFEST_SHA = "32e182f6f811acde1109084ee5f5c9a606098b95cc8774f27ece8bc1a506de3b"
MODEL_SHA = "4249a0f7515272ff76b9414bec17d7e7daf84b15b66c7fb4f17fce1e66f9f237"
DLL_SHA = "2b32201652e9bde6ee28a221464f52703b8b02bc03c16f18266aeedcdc61bdb2"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def fetch(name, url, expected_sha, size):
    path = ROOT / name
    if path.exists():
        data = path.read_bytes()
    else:
        with urllib.request.urlopen(url, timeout=60) as response:
            data = response.read(size + 1)
        if len(data) != size or sha(data) != expected_sha:
            raise ValueError("Pinned download mismatch: " + name)
        path.write_bytes(data)
    if len(data) != size or sha(data) != expected_sha:
        raise ValueError("Pinned existing artifact mismatch: " + name)
    return data


manifest_bytes = (ROOT / "release-manifest.json").read_bytes()
assert sha(manifest_bytes) == MANIFEST_SHA
manifest = json.loads(manifest_bytes)
assert manifest["version"] == "4.1.0" and manifest["api"] == "cactus"
identities = []
for local, remote in [
    ("runtime.whl", "python/cactus_runtime-4.1.0-py3-none-win_amd64.whl"),
    ("cactus.h", "windows-x86_64/cactus.h"),
    ("LICENSE", "LICENSE"),
]:
    pin = manifest["files"][remote]
    data = fetch(local, BASE + remote, pin["sha256"], pin["size"])
    identities.append(dict(name=local, source=BASE + remote, size=len(data), sha256=sha(data)))

model_url = "https://huggingface.co/Cactus-Compute/mirage/resolve/3d03cee88c0acbe4e71b94b567ab14e6e55f40c9/mirage.cact"
model = fetch("mirage.cact", model_url, MODEL_SHA, 21_611_043)
identities.append(dict(name="mirage.cact", source=model_url, size=len(model), sha256=sha(model)))

with zipfile.ZipFile(ROOT / "runtime.whl") as archive:
    names = archive.namelist()
    records = [name for name in names if name.endswith(".dist-info/RECORD")]
    assert len(records) == 1
    for name, digest, size in csv.reader(io.StringIO(archive.read(records[0]).decode())):
        if not digest:
            continue
        data = archive.read(name)
        assert digest.startswith("sha256=") and len(data) == int(size)
        assert base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b"=").decode() == digest[7:]
    dlls = [name for name in names if name.lower().endswith(".dll")]
    assert len(dlls) == 1
    data = archive.read(dlls[0])
    assert len(data) == 1_870_336 and sha(data) == DLL_SHA
    (ROOT / "libcactus.dll").write_bytes(data)
    identities.append(dict(name="libcactus.dll", source="verified runtime.whl:" + dlls[0], size=len(data), sha256=sha(data)))

report = dict(status="prepared_no_native_execution", engine_revision=REV, manifest_sha256=MANIFEST_SHA,
              runtime_version="4.1.0", license="Apache-2.0 declaration, no runtime source approval inferred",
              model_sha256=MODEL_SHA, private_payload_reads=0, native_executions=0, identities=identities)
(ROOT / "prepared-assets.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
