# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Hosted-only byte-identical materialization; not an approval or parity gate."""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from ml.ocr.official_bakeoff.audit_archives import CANDIDATES, audit_archive
from ml.ocr.official_bakeoff.convert_models import conversion_command, parse_locked_requirements, validate_converter_result

EXPECTED_MODEL = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743"
EXPECTED_WHEEL = "f62aaab2bd8d3ad4f4f7781bdeed43403546057b7afcdc10a4b33847b2617f1f"
MAX_TEMP_BYTES = 3 * 1024 * 1024 * 1024
MAX_SECONDS = 1200


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def check_budget(work: Path, deadline: float) -> None:
    if time.monotonic() >= deadline:
        raise RuntimeError("Materialization exceeded its time budget")
    total = 0
    for path in work.rglob("*"):
        try:
            if path.is_file():
                total += path.stat().st_size
        except FileNotFoundError:
            # pip may remove a temporary file while this inventory is walking it.
            continue
    if total > MAX_TEMP_BYTES:
        raise RuntimeError("Materialization exceeded its temporary disk budget")


def download(url: str, destination: Path, expected: str, limit: int, deadline: float) -> None:
    digest = hashlib.sha256()
    count = 0
    with urllib.request.urlopen(url, timeout=30) as response, destination.open("xb") as output:
        while chunk := response.read(1024 * 1024):
            count += len(chunk)
            if count > limit or time.monotonic() >= deadline:
                raise RuntimeError("Download exceeded its size or time budget")
            digest.update(chunk)
            output.write(chunk)
    if digest.hexdigest() != expected:
        raise RuntimeError("Downloaded artifact checksum mismatch")


def run(command: list[str], work: Path, deadline: float, log: Path, env: dict[str, str]) -> None:
    with log.open("xb") as output:
        child = subprocess.Popen(command, cwd=ROOT, env=env, stdout=output, stderr=subprocess.STDOUT,
                                 creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            while child.poll() is None:
                check_budget(work, deadline)
                time.sleep(1)
            if child.returncode != 0:
                raise RuntimeError(f"Materialization command failed with exit code {child.returncode}; see {log.name}")
        finally:
            if child.poll() is None:
                subprocess.run(["taskkill", "/PID", str(child.pid), "/T", "/F"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False,
                               creationflags=subprocess.CREATE_NO_WINDOW)
                child.wait(timeout=15)
    check_budget(work, deadline)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if os.name != "nt" or os.environ.get("GITHUB_ACTIONS") != "true" or sys.version_info[:3] != (3, 11, 9):
        raise RuntimeError("Requires hosted Windows GitHub Actions and CPython 3.11.9")
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    if not run_id.isascii() or not run_id.isdecimal():
        raise RuntimeError("Invalid hosted run identity")
    temp = Path(os.environ["RUNNER_TEMP"]).resolve(strict=True)
    output = args.output.resolve()
    if not output.is_relative_to(temp) or output.exists():
        raise RuntimeError("Output must be a new path under RUNNER_TEMP")
    work = temp / f"graphreader-calibration-materialize-{run_id}"
    work.mkdir(exist_ok=False)
    deadline = time.monotonic() + MAX_SECONDS
    tmp = work / "tmp"
    wheels = work / "wheelhouse"
    tmp.mkdir()
    wheels.mkdir()
    env = os.environ.copy()
    env.update({"TEMP": str(tmp), "TMP": str(tmp), "TMPDIR": str(tmp), "PIP_NO_CACHE_DIR": "1"})
    lock = ROOT / "ml/ocr/official_bakeoff/requirements-conversion.txt"
    requirements = (ROOT / "ml/ocr/official_bakeoff/requirements-conversion.in").read_text(encoding="utf-8")
    match = re.search(r"(?m)^paddlepaddle @ (https://[^\s#]+\.whl)#sha256=([0-9a-f]{64})$", requirements)
    if not match or match.group(2) != EXPECTED_WHEEL or EXPECTED_WHEEL not in parse_locked_requirements(lock)["paddlepaddle"]["allowed_sha256"]:
        raise RuntimeError("Pinned Paddle wheel or conversion lock differs")
    wheel = wheels / match.group(1).rsplit("/", 1)[-1]
    download(match.group(1), wheel, EXPECTED_WHEEL, 1024 * 1024 * 1024, deadline)
    check_budget(work, deadline)
    environment = work / "venv"
    run([sys.executable, "-m", "venv", str(environment)], work, deadline, work / "venv.log", env)
    python = environment / "Scripts/python.exe"
    run([str(python), "-m", "pip", "install", "--require-hashes", "--only-binary=:all:",
         "--no-cache-dir", "--timeout", "30", "--retries", "2", "--find-links", str(wheels),
         "--requirement", str(lock)], work, deadline, work / "install.log", env)
    candidate = next(item for item in CANDIDATES if item.model_id == "en_PP-OCRv5_mobile_rec")
    archive = work / candidate.archive_name
    download(candidate.url, archive, candidate.archive_sha256, 16 * 1024 * 1024, deadline)
    audited = audit_archive(candidate, archive)
    if not audited["archive_hash_matches"] or not audited["member_inventory_matches"]:
        raise RuntimeError("Official reader archive or member inventory differs")
    model_root = work / "source"
    model_root.mkdir()
    with tarfile.open(archive, "r:") as bundle:
        for name, expected in candidate.member_sha256.items():
            member = bundle.getmember(name)
            source = bundle.extractfile(member)
            if source is None:
                raise RuntimeError("Reviewed reader member is missing")
            target = model_root / Path(name).name
            with source, target.open("xb") as destination:
                shutil.copyfileobj(source, destination)
            if sha256(target) != expected:
                raise RuntimeError("Reviewed reader member checksum differs")
    converted = work / "en_PP-OCRv5_mobile_rec.onnx"
    converter = environment / "Scripts/paddle2onnx.exe"
    log = work / "conversion.log"
    run(conversion_command(converter, model_root, converted), work, deadline, log, env)
    if log.stat().st_size > 4 * 1024 * 1024:
        raise RuntimeError("Converter log exceeds bound")
    validate_converter_result(0, log.read_text(encoding="utf-8", errors="replace"), "", converted)
    if sha256(converted) != EXPECTED_MODEL:
        raise RuntimeError("Converted reader does not match the already-reviewed model hash")
    output.parent.mkdir(parents=True, exist_ok=True)
    with converted.open("rb") as source, output.open("xb") as destination:
        shutil.copyfileobj(source, destination)
    if sha256(output) != EXPECTED_MODEL:
        raise RuntimeError("Materialized reader copy checksum differs")
    print(f"Materialized exact reviewed reader: {EXPECTED_MODEL}; no production approval granted")


if __name__ == "__main__":
    main()
