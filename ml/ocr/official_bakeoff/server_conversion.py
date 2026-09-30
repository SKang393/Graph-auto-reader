# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Convert the explicitly approved server OCR models without changing mobile evidence."""
from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import asdict
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import traceback
import zipfile

from ml.ocr.official_bakeoff import convert_models as shared
from ml.training_cpu_budget import TrainingCpuBudget

MODELS = {
    "PP-OCRv5_server_det": "ca867c897ecbca8873081573a802ad70d499cb94",
    "PP-OCRv5_server_rec": "b26c3587fda8da3c8ec0ce357214b4d661ff1558",
}
SHAPES = {
    "PP-OCRv5_server_det": ((1, 3, 32, 32), (1, 3, 64, 96),
                            (1, 3, 960, 960), (1, 3, 1280, 1280)),
    "PP-OCRv5_server_rec": ((1, 3, 48, 320), (1, 3, 48, 321),
                            (2, 3, 48, 640), (1, 3, 48, 4096)),
}


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def reference(path: Path) -> dict:
    return {"path": str(path.resolve()), "bytes": path.stat().st_size,
            "sha256": shared.hash_file(path)}


def verify_intake(path: Path) -> dict:
    intake = shared.load_strict_json(path)
    records = intake["models"]
    if len(records) != 2 or {entry["model"] for entry in records} != set(MODELS):
        raise ValueError("The intake must contain exactly the two approved server models")
    for entry in records:
        if (entry["owner"] != "PaddlePaddle" or entry["revision"] != MODELS[entry["model"]]
                or entry["license"] != "Apache-2.0"
                or entry["commercial_use"] is not True or entry["redistribution"] is not True
                or entry["production_approved"] is not False):
            raise ValueError("Model identity, license, or approval differs from authorized intake")
        for artifact in [*entry["files"], entry["notice"], entry["license_text"], entry["metadata"]]:
            local = Path(artifact["path"]).resolve(strict=True)
            if not local.is_relative_to(path.parent.resolve()):
                raise ValueError("Intake artifact escapes its reviewed directory")
            if shared.hash_file(local) != artifact["sha256"]:
                raise ValueError(f"Intake artifact changed: {local.name}")
    return intake


def validate_restored_python_intake(intake_path: Path, installer: Path,
                                   converter: Path, signature: dict) -> dict:
    """Verify rebuilt console-launcher content, whose ZIP timestamp is not stable."""
    from pip._vendor.distlib import scripts

    intake = shared.load_strict_json(intake_path)
    if (intake['schema'] != 'graphreader.local-toolchain-intake.v1'
            or intake['status'] != 'selected_toolchain_import_passed_conversion_pending'):
        raise ValueError('Unrecognized original toolchain intake')
    selected = intake['third_converter_candidate']
    if (selected['converter_version'] != shared.EXPECTED_PACKAGES['paddle2onnx']
            or selected['paddle_version'] != shared.EXPECTED_PACKAGES['paddlepaddle']
            or selected['converter_launcher_sha256'] != shared.EXPECTED_CONVERTER_LAUNCHER_SHA256
            or selected['venv_python_sha256'] != shared.EXPECTED_VENV_PYTHON_SHA256):
        raise ValueError('Original toolchain identity changed')
    if (installer.stat().st_size != shared.EXPECTED_PYTHON_INSTALLER_BYTES
            or shared.hash_file(installer) != shared.EXPECTED_PYTHON_INSTALLER_SHA256
            or shared.hash_file(Path(sys.executable)) != shared.EXPECTED_VENV_PYTHON_SHA256
            or signature.get('status') != 'Valid'
            or 'CN=Python Software Foundation' not in signature.get('signer', '')
            or 'O=Python Software Foundation' not in signature.get('signer', '')):
        raise ValueError('Restored Python installer, signature or executable is not reviewed')
    stub_path = Path(scripts.__file__).with_name('t64.exe')
    stub = stub_path.read_bytes()
    data = converter.read_bytes()
    expected_script = (scripts.SCRIPT_TEMPLATE % {
        'module': 'paddle2onnx.command', 'import_name': 'main', 'func': 'main'}).encode('utf-8')
    with zipfile.ZipFile(converter) as archive:
        entries = archive.infolist()
        if (len(entries) != 1 or entries[0].filename != '__main__.py'
                or entries[0].extra or entries[0].comment or archive.comment
                or archive.read('__main__.py') != expected_script):
            raise ValueError('Restored converter console entry point differs from reviewed package')
        shebang = data[len(stub):entries[0].header_offset]
        if (not data.startswith(stub)
                or shebang != b'#!' + os.fsencode(sys.executable) + b'\n' + os.linesep.encode('ascii')):
            raise ValueError('Restored converter executable or interpreter binding differs')
        timestamp = entries[0].date_time
    return {'original_intake': reference(intake_path), 'installer': reference(installer),
            'authenticode': signature, 'python': reference(Path(sys.executable)),
            'converter': reference(converter), 'verified_distlib_stub': reference(stub_path),
            'entry_point_sha256': hashlib.sha256(expected_script).hexdigest(),
            'launcher_zip_timestamp': timestamp,
            'historical_launcher_sha256': shared.EXPECTED_CONVERTER_LAUNCHER_SHA256,
            'review': 'Reinstalled console launcher has a fresh ZIP timestamp. Its complete native prefix, interpreter binding and sole entry-point script are verified against the locked, RECORD-verified environment. Historical mobile evidence is unchanged.'}


def paddle_runner(source: Path):
    import paddle.inference as paddle_inference
    config = paddle_inference.Config(str(source / "inference.json"), str(source / "inference.pdiparams"))
    config.disable_gpu()
    config.set_cpu_math_library_num_threads(os.cpu_count() or 1)
    config.disable_glog_info()
    config.disable_mkldnn()
    config.switch_ir_optim(True)
    predictor = paddle_inference.create_predictor(config)
    inputs, outputs = predictor.get_input_names(), predictor.get_output_names()
    if len(inputs) != 1 or len(outputs) != 1:
        raise ValueError("Paddle OCR must expose one input and one output")

    def run(value):
        handle = predictor.get_input_handle(inputs[0])
        handle.reshape(value.shape)
        handle.copy_from_cpu(value)
        if predictor.run() is False:
            raise ValueError("Paddle CPU execution failed")
        return predictor.get_output_handle(outputs[0]).copy_to_cpu()

    return inputs[0], outputs[0], run


def onnx_runner(path: Path):
    import onnxruntime as ort
    options = ort.SessionOptions()
    options.intra_op_num_threads = os.cpu_count() or 1
    options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.use_deterministic_compute = True
    options.enable_cpu_mem_arena = False
    options.enable_mem_pattern = False
    options.enable_mem_reuse = False
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    session = ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])
    if session.get_providers() != ["CPUExecutionProvider"]:
        raise ValueError("Parity execution must be CPU-only")
    inputs, outputs = session.get_inputs(), session.get_outputs()
    if len(inputs) != 1 or len(outputs) != 1:
        raise ValueError("ONNX OCR must expose one input and one output")
    return inputs[0].name, outputs[0].name, lambda value: session.run(
        [outputs[0].name], {inputs[0].name: value})[0]


def convert(model: str, source: Path, output: Path, converter: Path, budget: TrainingCpuBudget) -> dict:
    result = {"model": model, "revision": MODELS[model], "conversions": []}
    model_root = output / model
    model_root.mkdir()
    try:
        for index in (1, 2):
            destination = model_root / f"conversion-{index}.onnx"
            command = shared.conversion_command(converter, source, destination)
            with budget.work_block():
                completed = subprocess.run(command, capture_output=True, text=True,
                                           encoding="utf-8", errors="replace", check=False)
            (model_root / f"conversion-{index}.stdout.log").write_text(completed.stdout, encoding="utf-8")
            (model_root / f"conversion-{index}.stderr.log").write_text(completed.stderr, encoding="utf-8")
            shared.validate_converter_result(completed.returncode, completed.stdout, completed.stderr, destination)
            with budget.work_block():
                validation = shared.validate_onnx_model(destination)
            warnings = Counter(line.strip() for line in (completed.stdout + "\n" + completed.stderr).splitlines()
                               if "warning" in line.casefold())
            result["conversions"].append({"artifact": reference(destination), "command": command,
                                          "validation": validation, "warning_lines": dict(warnings)})
            write(model_root / "report.json", result)
        if result["conversions"][0]["artifact"]["sha256"] != result["conversions"][1]["artifact"]["sha256"]:
            raise ValueError("Independent ONNX exports are not byte-identical")
        result["byte_reproducible"] = True
        result["parity"] = parity(model, source, model_root / "conversion-1.onnx", budget, model_root)
        result["status"] = "conversion_and_raw_cpu_parity_passed_pending_warning_review"
    except Exception as error:
        result.update(status="failed", error=str(error), traceback=traceback.format_exc())
    write(model_root / "report.json", result)
    return result


def parity(model: str, source: Path, model_path: Path, budget: TrainingCpuBudget, output: Path) -> dict:
    import numpy as np
    with budget.work_block():
        paddle_input, paddle_output, run_paddle = paddle_runner(source)
        onnx_input, onnx_output, run_onnx = onnx_runner(model_path)
    rng = np.random.default_rng(shared.RNG_SEED)
    result = {"cases": [], "shapes": SHAPES[model], "seed": shared.RNG_SEED,
              "maximum_allowed": shared.PARITY_MAXIMUM_ABSOLUTE_DIFFERENCE,
              "provider": "CPUExecutionProvider", "logical_processors": os.cpu_count(),
              "paddle_input": paddle_input, "paddle_output": paddle_output,
              "onnx_input": onnx_input, "onnx_output": onnx_output, "passed": False}
    for index in range(shared.PARITY_CASES):
        shape = SHAPES[model][index % len(SHAPES[model])]
        with budget.work_block():
            sample = rng.uniform(-1, 1, size=shape).astype(np.float32)
            started = time.perf_counter()
            expected = run_paddle(sample)
            paddle_seconds = time.perf_counter() - started
            started = time.perf_counter()
            observed = run_onnx(sample)
            onnx_seconds = time.perf_counter() - started
            difference = shared.maximum_absolute_error(expected, observed)
            record = {"index": index, "shape": shape, "output_shape": expected.shape,
                      "sample_sha256": hashlib.sha256(sample.tobytes()).hexdigest(),
                      "maximum_absolute_difference": difference,
                      "paddle_seconds": paddle_seconds, "onnx_seconds": onnx_seconds}
        record["cpu_work_rest"] = asdict(budget.last_observation)
        result["cases"].append(record)
        write(output / "parity.json", result)
        del sample, expected, observed
    result["maximum_absolute_difference"] = max(row["maximum_absolute_difference"] for row in result["cases"])
    result["passed"] = result["maximum_absolute_difference"] <= result["maximum_allowed"]
    write(output / "parity.json", result)
    if not result["passed"]:
        raise ValueError(f"Raw CPU parity exceeded existing tolerance: {result['maximum_absolute_difference']}")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--intake", type=Path, required=True)
    parser.add_argument("--protocol", type=Path, required=True)
    parser.add_argument("--toolchain-root", type=Path, required=True)
    parser.add_argument("--toolchain-intake", type=Path, required=True)
    parser.add_argument("--installer", type=Path, required=True)
    parser.add_argument("--signature", type=Path, required=True)
    parser.add_argument("--wheelhouse", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(exist_ok=False, parents=True)
    started = time.perf_counter()
    report = {"schema": "graphreader.server-ocr-conversion.v1", "models": [],
              "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0,
              "production_approved": False, "source": reference(Path(__file__)),
              "protocol": reference(args.protocol), "intake": reference(args.intake)}
    shutil.copyfile(__file__, args.output / "server_conversion.py")
    shutil.copyfile(args.protocol, args.output / "protocol.json")
    budget = TrainingCpuBudget(80)
    try:
        protocol = shared.load_strict_json(args.protocol)
        if protocol["intake_sha256"] != shared.hash_file(args.intake):
            raise ValueError("Intake differs from frozen comparison protocol")
        if protocol["conversion_source_sha256"] != shared.hash_file(Path(__file__)):
            raise ValueError("Conversion harness differs from frozen protocol")
        with budget.work_block():
            intake = verify_intake(args.intake)
            converter = args.toolchain_root / "Scripts/paddle2onnx.exe"
            report["toolchain"] = shared.validate_toolchain(args.toolchain_root, converter)
            report["python_intake"] = validate_restored_python_intake(
                args.toolchain_intake, args.installer, converter,
                shared.load_strict_json(args.signature))
            report["wheelhouse"] = shared.inventory_wheelhouse(args.wheelhouse)
        for entry in intake["models"]:
            report["models"].append(convert(entry["model"], args.intake.parent / entry["model"],
                                            args.output, converter, budget))
            write(args.output / "report.json", report)
        report["status"] = ("conversion_and_raw_cpu_parity_passed_pending_warning_review"
                            if all(item["status"].startswith("conversion_and") for item in report["models"])
                            else "failed")
    except Exception as error:
        report.update(status="failed", error=str(error), traceback=traceback.format_exc())
    report["seconds"] = time.perf_counter() - started
    write(args.output / "report.json", report)
    print(report["status"], flush=True)
    return 0 if report["status"].startswith("conversion_and") else 1


if __name__ == "__main__":
    raise SystemExit(main())
