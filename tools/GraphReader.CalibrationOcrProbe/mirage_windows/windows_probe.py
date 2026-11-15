# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Isolated owned Windows ABI diagnostic. No application or calibration integration."""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sys
import threading
import time

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "supplied"))
from response_adapter import parse_response

PINS = {
    "libcactus.dll": "2b32201652e9bde6ee28a221464f52703b8b02bc03c16f18266aeedcdc61bdb2",
    "mirage.cact": "4249a0f7515272ff76b9414bec17d7e7daf84b15b66c7fb4f17fce1e66f9f237",
    "release-manifest.json": "32e182f6f811acde1109084ee5f5c9a606098b95cc8774f27ece8bc1a506de3b",
    "LICENSE": "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30",
    "model-card.md": "9c69381d336583e6b4069663f4028aeee9c0512d5abc6781c63d96ff54554d4f",
    "cactus.h": "9be7eb25b1d89180495d04a0d2c27053f282dac85add50cacc48d73302819d9d",
    "supplied/response_adapter.py": "224a74de1ffccc11f438fbb6fbc7b2f1b74ef86434e254d418a557b6f199eccc",
    "supplied/test_adapter.py": "c1f6b832574cb782149459ee12835a6556689e8f8a8d57f67cb07f44a70f7052",
    "supplied/synthetic.png": "6f1e161799715d031cd8391263df264030dec2a1baecf3b56fff8ea146c00ca2",
}


def verify_assets():
    for name, expected in PINS.items():
        with (ROOT / name).open("rb") as source:
            actual = hashlib.file_digest(source, "sha256").hexdigest()
        if actual != expected:
            raise ValueError("PIN_MISMATCH: " + name)
    return dict(PINS)


class CpuRate(ctypes.Structure):
    _fields_ = [("flags", wintypes.DWORD), ("rate", wintypes.DWORD)]


def apply_cpu_budget():
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    signatures = {
        "GetCurrentProcess": ([], wintypes.HANDLE),
        "GetActiveProcessorCount": ([wintypes.WORD], wintypes.DWORD),
        "SetPriorityClass": ([wintypes.HANDLE, wintypes.DWORD], wintypes.BOOL),
        "GetPriorityClass": ([wintypes.HANDLE], wintypes.DWORD),
        "CreateJobObjectW": ([ctypes.c_void_p, wintypes.LPCWSTR], wintypes.HANDLE),
        "SetInformationJobObject": ([wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD], wintypes.BOOL),
        "QueryInformationJobObject": ([wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p], wintypes.BOOL),
        "AssignProcessToJobObject": ([wintypes.HANDLE, wintypes.HANDLE], wintypes.BOOL),
        "IsProcessInJob": ([wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.BOOL)], wintypes.BOOL),
        "CloseHandle": ([wintypes.HANDLE], wintypes.BOOL),
    }
    for name, (arguments, result) in signatures.items():
        function = getattr(kernel, name)
        function.argtypes, function.restype = arguments, result
    process = kernel.GetCurrentProcess()
    logical = kernel.GetActiveProcessorCount(0xFFFF)
    rate = 5000 // logical if logical else 0
    if not 0 < rate <= 5000:
        raise RuntimeError("CPU_TOPOLOGY_UNSUPPORTED")
    if not kernel.SetPriorityClass(process, 0x4000):
        raise RuntimeError("CPU_PRIORITY_UNAVAILABLE")
    job = kernel.CreateJobObjectW(None, None)
    if not job:
        raise RuntimeError("CPU_JOB_UNAVAILABLE")
    try:
        settings = CpuRate(5, rate)
        if not kernel.SetInformationJobObject(job, 15, ctypes.byref(settings), ctypes.sizeof(settings)):
            raise RuntimeError("CPU_JOB_RATE_UNAVAILABLE")
        if not kernel.AssignProcessToJobObject(job, process):
            raise RuntimeError("CPU_JOB_ASSIGNMENT_UNAVAILABLE")
        actual, member = CpuRate(), wintypes.BOOL()
        if not kernel.QueryInformationJobObject(job, 15, ctypes.byref(actual), ctypes.sizeof(actual), None):
            raise RuntimeError("CPU_JOB_QUERY_FAILED")
        if not kernel.IsProcessInJob(process, job, ctypes.byref(member)) or not member.value:
            raise RuntimeError("CPU_JOB_MEMBERSHIP_FAILED")
        if actual.flags != 5 or actual.rate != rate or kernel.GetPriorityClass(process) != 0x4000:
            raise RuntimeError("CPU_BUDGET_VERIFICATION_FAILED")
        budget = dict(logical_processors=logical, rate=rate, maximum_logical_cores=logical * rate / 10000,
                      flags=actual.flags, verified=True, priority_class="BelowNormal")
        return kernel, job, budget
    except BaseException:
        kernel.CloseHandle(job)
        raise


def reject_constant(value):
    raise ValueError("NONFINITE_JSON_TOKEN")


def check_finite(value):
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError("NONFINITE_JSON_NUMBER")
    if isinstance(value, dict):
        for child in value.values():
            check_finite(child)
    elif isinstance(value, list):
        for child in value:
            check_finite(child)


def token_offsets(raw):
    text = raw.decode("utf-8", "strict")
    strings = [match.span() for match in re.finditer(r'"(?:[^"\\]|\\.)*"', text)]
    result = []
    for match in re.finditer(r'-?nan\(ind\)|-?Infinity|\bNaN\b', text, re.I):
        if any(start <= match.start() < end for start, end in strings):
            continue
        key = re.search(r'"([^"\\]+)"\s*:\s*$', text[:match.start()])
        result.append(dict(character_offset=match.start(), byte_offset=len(text[:match.start()].encode("utf-8")),
                           key=key.group(1) if key else None, token=match.group()))
    return result


def text_rows(payload):
    if not isinstance(payload, dict) or not isinstance(payload.get("text"), list):
        raise ValueError("TEXT_SCHEMA_INVALID")
    rows = []
    for row in payload["text"]:
        if not isinstance(row, dict) or not isinstance(row.get("string"), str):
            raise ValueError("TEXT_ROW_INVALID")
        box = row.get("bbox")
        if not isinstance(box, list) or len(box) != 4 or any(
            isinstance(x, bool) or not isinstance(x, (int, float)) or not math.isfinite(x) or not 0 <= x <= 1000 for x in box
        ) or box[0] >= box[2] or box[1] >= box[3]:
            raise ValueError("TEXT_BOX_INVALID")
        score = row.get("score")
        if score is not None and (isinstance(score, bool) or not isinstance(score, (int, float)) or
                                  not math.isfinite(score) or not 0 <= score <= 1):
            raise ValueError("TEXT_SCORE_INVALID")
        rows.append(dict(string=row["string"], bbox=box, score=score))
    return rows


def inspect_response(raw):
    strict, diagnostic, payload = dict(valid=False, parse_error=None), dict(valid=False, parse_error=None, repairs=[]), None
    try:
        payload = json.loads(raw.decode("utf-8"), parse_constant=reject_constant)
        check_finite(payload)
        strict["valid"] = True
    except (ValueError, UnicodeError) as error:
        strict["parse_error"] = type(error).__name__
        if isinstance(error, json.JSONDecodeError):
            strict["character_offset"] = error.pos
    try:
        adapted = parse_response(raw)
        diagnostic.update(valid=True, repairs=adapted["repairs"])
        if payload is None:
            payload = adapted["payload"]
    except (ValueError, UnicodeError) as error:
        diagnostic["parse_error"] = type(error).__name__
    rows, schema_error = [], None
    if payload is not None:
        try:
            rows = text_rows(payload)
        except ValueError as error:
            schema_error = str(error)
    return dict(strict=strict, diagnostic=diagnostic, text_rows=rows, schema_error=schema_error,
                nonfinite_tokens=token_offsets(raw), requires_review=True, production_approved=False)


def bind_runtime():
    lib = ctypes.CDLL(str(ROOT / "libcactus.dll"))
    lib.cactus_load.argtypes = [ctypes.c_char_p, ctypes.c_ulonglong]
    lib.cactus_load.restype = ctypes.c_int
    lib.cactus_models.argtypes, lib.cactus_models.restype = [], ctypes.c_int
    lib.cactus_last_error.argtypes, lib.cactus_last_error.restype = [], ctypes.c_char_p
    lib.cactus_set_threads.argtypes, lib.cactus_set_threads.restype = [ctypes.c_int], None
    lib.cactus_describe.argtypes = [ctypes.c_char_p, ctypes.c_int, ctypes.c_int, ctypes.c_char_p,
                                  ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_int]
    lib.cactus_describe.restype = ctypes.c_int
    lib.cactus_set_threads(1)
    return lib


def owned_check(mode):
    output = ROOT / ("windows-" + mode)
    output.mkdir(exist_ok=False)
    timer = threading.Timer(170, lambda: os._exit(3))
    timer.daemon = True
    timer.start()
    kernel, job, budget = apply_cpu_budget()
    started = time.perf_counter()
    try:
        identities = verify_assets()
        os.environ["CACTUS_TELEMETRY"] = "0"
        for key in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
            os.environ[key] = "1"
        from PIL import Image
        stage = time.perf_counter()
        with Image.open(ROOT / "supplied/synthetic.png") as opened:
            if opened.size != (600, 400):
                raise ValueError("OWNED_INPUT_DIMENSIONS_CHANGED")
            image = opened.convert("RGB")
        rgb = image.tobytes()
        decode_ms = (time.perf_counter() - stage) * 1000
        stage = time.perf_counter()
        lib = bind_runtime()
        dll_ms = (time.perf_counter() - stage) * 1000
        stage = time.perf_counter()
        weights = (ROOT / "mirage.cact").read_bytes()
        load_status = lib.cactus_load(weights, len(weights))
        model_ms = (time.perf_counter() - stage) * 1000
        if load_status < 0 or not lib.cactus_models() & 4:
            raise RuntimeError("MIRAGE_LOAD_FAILED")
        options = {} if mode == "default" else {"sections": ["text"]}
        encoded_options = json.dumps(options, allow_nan=False, separators=(",", ":")).encode()
        buffer = ctypes.create_string_buffer(16 * 1024 * 1024)
        stage = time.perf_counter()
        status = lib.cactus_describe(rgb, image.width, image.height, encoded_options, None, None, buffer, len(buffer))
        elapsed_ms = (time.perf_counter() - stage) * 1000
        contents = buffer.raw
        end = contents.find(b"\0")
        raw = contents if end < 0 else contents[:end]
        (output / "raw-response.bin").write_bytes(raw)
        inspection = inspect_response(raw) if status >= 0 and end >= 0 else dict(
            strict=dict(valid=False, parse_error="NATIVE_ERROR_OR_UNTERMINATED_OUTPUT"), diagnostic=dict(valid=False),
            text_rows=[], requires_review=True, production_approved=False)
        report = dict(scope="owned-synthetic-diagnostic-only", mode=mode, options=options, status=status, load_status=load_status,
                      raw_sha256=hashlib.sha256(raw).hexdigest(), raw_bytes=len(raw), nul_terminated=end >= 0,
                      identities=identities, rgb_sha256=hashlib.sha256(rgb).hexdigest(), width=image.width, height=image.height,
                      cpu_budget=budget, threads=1, timings_ms=dict(decode=decode_ms, dll_load=dll_ms, model_read_load=model_ms,
                      describe=elapsed_ms, total=(time.perf_counter() - started) * 1000), **inspection)
        (output / "result.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
        print(json.dumps(report, allow_nan=False), flush=True)
    finally:
        timer.cancel()
        kernel.CloseHandle(job)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", required=True, choices=["default", "text-only"])
    owned_check(parser.parse_args().mode)
