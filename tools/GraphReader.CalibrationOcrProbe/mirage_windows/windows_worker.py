# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed text-only, persistent diagnostic transport. No image/raw-response writes."""
import base64
import ctypes
import hashlib
import json
import os
from pathlib import Path
import sys
import time

sys.dont_write_bytecode = True
import windows_probe as support
from score_diagnostic import parse_score_diagnostic

ROOT = Path(__file__).resolve().parent
MAX_PIXELS = 4096 * 4096
MAX_LINE = 64 * 1024 * 1024 + 4096
MAX_RESPONSE = 16 * 1024 * 1024 + 4096
SUPPORT_SHA = "e4a7978e0c96e2aa798864c1157ff6182aab03ebfe8d2ebbe1da27c4e73cd350"
SCORE_DIAGNOSTIC_SHA = "a53be74d1f8c3f634f73dd40fa8ca22d797e832d99964d34145a130cba4a7cbe"
OPTIONS = b'{"sections":["text"]}'


def file_hash(path):
    with Path(path).open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("DUPLICATE_REQUEST_FIELD")
        result[key] = value
    return result


def decode_request(line):
    if not line.endswith(b"\n") or len(line) > MAX_LINE:
        raise ValueError("REQUEST_FRAME_INVALID")
    item = json.loads(line.decode("utf-8", "strict"), parse_constant=support.reject_constant, object_pairs_hook=unique_object)
    if not isinstance(item, dict) or set(item) != {"request_id", "width", "height", "rgb_base64"}:
        raise ValueError("REQUEST_FIELDS_INVALID")
    request_id = item["request_id"]
    width, height, encoded = item["width"], item["height"], item["rgb_base64"]
    if not isinstance(request_id, str) or not 1 <= len(request_id) <= 128 or any(ord(c) < 32 for c in request_id):
        raise ValueError("REQUEST_ID_INVALID")
    if type(width) is not int or type(height) is not int or not 1 <= width <= 4096 or not 1 <= height <= 4096:
        raise ValueError("REQUEST_DIMENSIONS_INVALID")
    pixels = width * height
    if pixels > MAX_PIXELS or not isinstance(encoded, str) or len(encoded) != 4 * ((pixels * 3 + 2) // 3):
        raise ValueError("REQUEST_RGB_LENGTH_INVALID")
    rgb = base64.b64decode(encoded, validate=True)
    if len(rgb) != pixels * 3:
        raise ValueError("REQUEST_RGB_LENGTH_INVALID")
    return request_id, width, height, rgb


def failure(request_id, code, budget, status=-1, raw=b"", elapsed_ms=0):
    return dict(request_id=request_id, status=status, elapsed_ms=elapsed_ms,
                raw_sha256=hashlib.sha256(raw).hexdigest(), raw_bytes=len(raw), strict_json_valid=False,
                parse_error=code, repairs=[], requires_review=True, text_rows=[], cpu_budget=budget)


def response_from_raw(request_id, status, raw, elapsed_ms, budget, terminated=True):
    if status < 0 or not terminated:
        return failure(request_id, "NATIVE_ERROR" if status < 0 else "OUTPUT_UNTERMINATED", budget, status, raw, elapsed_ms)
    try:
        parsed = support.inspect_response(raw)
    except (ValueError, UnicodeError, RecursionError):
        return failure(request_id, "RESPONSE_INVALID", budget, status, raw, elapsed_ms)
    strict = parsed["strict"]["valid"]
    if not strict:
        try:
            recovered = parse_score_diagnostic(raw)
        except (ValueError, UnicodeError, RecursionError):
            pass
        else:
            parsed["diagnostic"]["repairs"] = recovered["repairs"]
            try:
                parsed["text_rows"] = support.text_rows(recovered["payload"])
            except ValueError as error:
                parsed["text_rows"] = []
                parsed["schema_error"] = str(error)
    error = None if strict else "NATIVE_JSON_INVALID"
    if parsed.get("schema_error"):
        error = parsed["schema_error"]
    return dict(request_id=request_id, status=status, elapsed_ms=elapsed_ms,
                raw_sha256=hashlib.sha256(raw).hexdigest(), raw_bytes=len(raw), strict_json_valid=strict,
                parse_error=error, repairs=parsed["diagnostic"].get("repairs", []), requires_review=True,
                text_rows=parsed["text_rows"], cpu_budget=budget)


def write_frame(value):
    data = json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(data) > MAX_RESPONSE:
        value = failure(value.get("request_id", ""), "RESPONSE_TOO_LARGE", value.get("cpu_budget", {}),
                        status=value.get("status", -1))
        data = json.dumps(value, allow_nan=False, separators=(",", ":")).encode("utf-8")
    sys.stdout.buffer.write(data + b"\n")
    sys.stdout.buffer.flush()


def main():
    if len(sys.argv) != 1:
        return 2
    kernel, job, budget = support.apply_cpu_budget()
    try:
        pins = support.verify_assets()
        if file_hash(ROOT / "windows_probe.py") != SUPPORT_SHA:
            raise ValueError("SUPPORT_PIN_MISMATCH")
        if file_hash(ROOT / "score_diagnostic.py") != SCORE_DIAGNOSTIC_SHA:
            raise ValueError("SCORE_DIAGNOSTIC_PIN_MISMATCH")
        identities = dict(runtime_sha256=pins["libcactus.dll"], model_sha256=pins["mirage.cact"],
                          manifest_sha256=pins["release-manifest.json"], license_sha256=pins["LICENSE"],
                          adapter_sha256=pins["supplied/response_adapter.py"], header_sha256=pins["cactus.h"],
                          model_card_sha256=pins["model-card.md"], support_sha256=SUPPORT_SHA,
                          score_diagnostic_sha256=SCORE_DIAGNOSTIC_SHA,
                          worker_sha256=file_hash(__file__))
        os.environ["CACTUS_TELEMETRY"] = "0"
        for key in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
            os.environ[key] = "1"
        lib = support.bind_runtime()
        # Keep the loaded archive alive for the entire worker lifetime.
        weights = (ROOT / "mirage.cact").read_bytes()
        if lib.cactus_load(weights, len(weights)) < 0 or not lib.cactus_models() & 4:
            raise ValueError("MIRAGE_LOAD_FAILED")
        buffer = ctypes.create_string_buffer(16 * 1024 * 1024)
        write_frame(dict(type="ready", identities=identities, cpu_budget=budget, threads=1))
        while True:
            line = sys.stdin.buffer.readline(MAX_LINE + 1)
            if not line:
                break
            try:
                request_id, width, height, rgb = decode_request(line)
            except (ValueError, UnicodeError, RecursionError):
                write_frame(failure("", "REQUEST_INVALID", budget))
                if len(line) > MAX_LINE or not line.endswith(b"\n"):
                    break
                continue
            ctypes.memset(buffer, 0, len(buffer))
            started = time.perf_counter()
            status = lib.cactus_describe(rgb, width, height, OPTIONS, None, None, buffer, len(buffer))
            elapsed_ms = (time.perf_counter() - started) * 1000
            contents = buffer.raw
            end = contents.find(b"\0")
            raw = contents if end < 0 else contents[:end]
            reply = response_from_raw(request_id, status, raw, elapsed_ms, budget, terminated=end >= 0)
            write_frame(reply)
            # Request pixels/native bytes are transient memory only, never files or a cache.
            del line, rgb, contents, raw, reply
        return 0
    finally:
        kernel.CloseHandle(job)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, OSError, RuntimeError, RecursionError):
        # Never print filesystem/parser exception messages from a private-data worker.
        write_frame(dict(type="error", error="MIRAGE_WORKER_FAILED"))
        raise SystemExit(1)
