# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""One exact original owned-input reproduction; no source image copy or option variation."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
import time

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parent
SUPPORT_SHA = "e4a7978e0c96e2aa798864c1157ff6182aab03ebfe8d2ebbe1da27c4e73cd350"
if hashlib.sha256((ROOT / "windows_probe.py").read_bytes()).hexdigest() != SUPPORT_SHA:
    raise ValueError("REVIEWED_SUPPORT_CHANGED")
import windows_probe as support

SOURCE = Path("E:/Graph-auto-reader/artifacts/goal19-realesrgan/public-synthetic/seed-393/images/fce8d985-cb56-55f9-a1f6-0b1cd18f27d0.png")
SOURCE_SHA = "75edb079ccfa8a3a2806bef467ec2dd10d13905bee762fd48ee9be21361be98d"


def main():
    if len(sys.argv) != 1:
        return 2
    output = ROOT / "windows-original-default"
    output.mkdir(exist_ok=False)
    timer = threading.Timer(170, lambda: os._exit(3))
    timer.daemon = True
    timer.start()
    kernel, job, budget = support.apply_cpu_budget()
    started = time.perf_counter()
    try:
        identities = support.verify_assets()
        if SOURCE.stat().st_size != 2_327_711:
            raise ValueError("ORIGINAL_SOURCE_SIZE_CHANGED")
        original = SOURCE.read_bytes()
        if hashlib.sha256(original).hexdigest() != SOURCE_SHA:
            raise ValueError("ORIGINAL_SOURCE_HASH_CHANGED")
        os.environ["CACTUS_TELEMETRY"] = "0"
        for key in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
            os.environ[key] = "1"
        from PIL import Image
        stage = time.perf_counter()
        # Header dimensions are checked before RGB conversion/allocation.
        import io
        with Image.open(io.BytesIO(original)) as opened:
            if opened.size != (1200, 1700):
                raise ValueError("ORIGINAL_DIMENSIONS_CHANGED")
            image = opened.convert("RGB")
        rgb = image.tobytes()
        decode_ms = (time.perf_counter() - stage) * 1000
        stage = time.perf_counter()
        lib = support.bind_runtime()
        dll_ms = (time.perf_counter() - stage) * 1000
        stage = time.perf_counter()
        weights = (ROOT / "mirage.cact").read_bytes()
        load_status = lib.cactus_load(weights, len(weights))
        model_ms = (time.perf_counter() - stage) * 1000
        if load_status < 0 or not lib.cactus_models() & 4:
            raise RuntimeError("MIRAGE_LOAD_FAILED")
        buffer = ctypes.create_string_buffer(16 * 1024 * 1024)
        stage = time.perf_counter()
        status = lib.cactus_describe(rgb, 1200, 1700, b"{}", None, None, buffer, len(buffer))
        elapsed_ms = (time.perf_counter() - stage) * 1000
        contents = buffer.raw
        end = contents.find(b"\0")
        raw = contents if end < 0 else contents[:end]
        (output / "raw-response.bin").write_bytes(raw)
        inspection = support.inspect_response(raw) if status >= 0 and end >= 0 else dict(
            strict=dict(valid=False, parse_error="NATIVE_ERROR_OR_UNTERMINATED_OUTPUT"), diagnostic=dict(valid=False),
            text_rows=[], requires_review=True, production_approved=False)
        report = dict(scope="exact-original-owned-diagnostic-only", mode="default", options={}, status=status,
                      load_status=load_status, source_sha256=SOURCE_SHA, source_bytes=len(original), image_copy=False,
                      width=1200, height=1700, rgb_sha256=hashlib.sha256(rgb).hexdigest(),
                      raw_sha256=hashlib.sha256(raw).hexdigest(), raw_bytes=len(raw), nul_terminated=end >= 0,
                      identities=identities, support_sha256=SUPPORT_SHA, cpu_budget=budget, threads=1,
                      timings_ms=dict(decode=decode_ms, dll_load=dll_ms, model_read_load=model_ms,
                                      describe=elapsed_ms, total=(time.perf_counter() - started) * 1000), **inspection)
        (output / "result.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
        print(json.dumps(report, allow_nan=False), flush=True)
        return 0
    finally:
        timer.cancel()
        kernel.CloseHandle(job)


if __name__ == "__main__":
    raise SystemExit(main())
