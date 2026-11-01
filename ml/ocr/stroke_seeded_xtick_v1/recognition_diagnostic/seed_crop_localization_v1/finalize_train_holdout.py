# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Finalize train-only localization metrics after both reader passes complete."""
from __future__ import annotations

from collections import Counter, defaultdict
from hashlib import sha256
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[5]
ARTIFACT_REL = "artifacts/stroke-seeded-xtick-v1-seed-crop-localization-train-v1"
NUMBER = re.compile(r"^-?\d+(?:\.\d+)?%?$", re.ASCII)


def digest(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def reader_metrics(rows: list[dict], truth: dict[str, dict], *, text_key: str,
                   confidence_key: str | None, failure_key: str | None, threshold: float | None) -> dict:
    counts = Counter()
    per_source: dict[str, Counter] = defaultdict(Counter)
    per_hash: dict[str, list[str | None]] = defaultdict(list)
    for row in rows:
        target = truth[row["CaseId"]]
        text = row[text_key]
        counts[f"{target['kind']}_rows"] += 1
        per_hash[row["ImageHash"]].append(text)
        if failure_key and row[failure_key]:
            counts["explicit_failures"] += 1
        if target["kind"] == "positive":
            correct = text is not None and text.strip() == target["text"]
            counts["exact"] += int(correct)
            counts["wrong_nonempty"] += int(bool(text) and not correct)
            counts["empty"] += int(not bool(text))
            per_source[row["SourceGroup"]]["positive"] += 1
            per_source[row["SourceGroup"]]["exact"] += int(correct)
            confidence_ok = True if threshold is None else (
                confidence_key is not None and row[confidence_key] is not None and row[confidence_key] >= threshold)
            counts["exact_numeric_confirmation"] += int(correct and confidence_ok and bool(NUMBER.fullmatch(text.strip())))
        else:
            counts["background_nonempty"] += int(bool(text))
            counts["background_numeric_grammar"] += int(bool(text and NUMBER.fullmatch(text.strip())))
            if bool(text):
                per_source[row["SourceGroup"]]["background_nonempty"] += 1
    repeated = [values for values in per_hash.values() if len(values) > 1]
    return {
        "counts_by_occurrence": dict(sorted(counts.items())),
        "positive_exact_rate": counts["exact"] / counts["positive_rows"] if counts["positive_rows"] else None,
        "positive_confirmation_rate": counts["exact_numeric_confirmation"] / counts["positive_rows"] if counts["positive_rows"] else None,
        "unique_image_hashes": len(per_hash),
        "repeated_hash_groups": len(repeated),
        "repeated_hash_groups_with_output_variation": sum(len(set(values)) > 1 for values in repeated),
        "per_source": {source: dict(sorted(values.items())) for source, values in sorted(per_source.items())},
    }


def assemble(recognition: dict, truth_doc: dict, input_doc: dict, input_sha: str) -> dict:
    if recognition.get("completed") is not True or recognition.get("schema") != "graphreader.seed-crop-localization-recognition.v1":
        raise ValueError("recognition pass is incomplete or wrong schema")
    if recognition.get("input_sha256") != input_sha or input_doc.get("truth_read") is not False:
        raise ValueError("recognition/input binding mismatch")
    if recognition.get("inference_provider") != "CPUExecutionProvider":
        raise ValueError("unexpected reader provider")
    truth_rows = truth_doc.get("rows")
    output_rows = recognition.get("recognitions")
    if not isinstance(truth_rows, list) or not isinstance(output_rows, list) or len(truth_rows) != len(input_doc.get("rows", [])) or len(output_rows) != len(truth_rows):
        raise ValueError("truth, input and recognition row counts differ")
    truth = {row["case_id"]: row for row in truth_rows}
    if set(truth) != {row["CaseId"] for row in output_rows}:
        raise ValueError("case ID coverage differs across truth and reader output")
    input_rows = {row["case_id"]: row for row in input_doc["rows"]}
    if set(input_rows) != set(truth):
        raise ValueError("inference row absent from truth table")
    return {
        "schema": "graphreader.seed-crop-localization-train-holdout-result.v1",
        "acceptance_status": "train-source internal diagnostic only; no production activation",
        "input_rows": len(input_doc["rows"]), "unique_images": recognition["distinct_image_hashes"],
        "positive_rows": sum(row["kind"] == "positive" for row in truth_rows),
        "positive_unique_images": len({input_rows[row["case_id"]]["image_sha256"] for row in truth_rows if row["kind"] == "positive"}),
        "background_rows": sum(row["kind"] == "background_only" for row in truth_rows),
        "background_unique_images": len({input_rows[row["case_id"]]["image_sha256"] for row in truth_rows if row["kind"] == "background_only"}),
        "localized_roi": {
            "positive_box_coverage_rows": sum(
                row["kind"] == "positive" and
                input_rows[row["case_id"]]["localized_roi_xyxy"][0] <= row["target_crop_xyxy"][0] and
                input_rows[row["case_id"]]["localized_roi_xyxy"][1] <= row["target_crop_xyxy"][1] and
                row["target_crop_xyxy"][2] <= input_rows[row["case_id"]]["localized_roi_xyxy"][2] and
                row["target_crop_xyxy"][3] <= input_rows[row["case_id"]]["localized_roi_xyxy"][3]
                for row in truth_rows),
            "clipped_rows": sum(row["roi_clipped"] for row in input_rows.values()),
        },
        "numeric_v5": {
            "localized_roi": reader_metrics(output_rows, truth, text_key="NumericText", confidence_key="NumericConfidence",
                failure_key="NumericFailure", threshold=0.65),
            "whole_seed_baseline": reader_metrics(output_rows, truth, text_key="FullNumericText", confidence_key="FullNumericConfidence",
                failure_key="FullNumericFailure", threshold=0.65),
        },
        "official_ppocr": {
            "localized_roi": reader_metrics(output_rows, truth, text_key="OfficialText", confidence_key="OfficialConfidence",
                failure_key=None, threshold=0.0),
            "whole_seed_baseline": reader_metrics(output_rows, truth, text_key="FullOfficialText", confidence_key="FullOfficialConfidence",
                failure_key="FullOfficialFailure", threshold=0.0),
        },
        "input_sha256": input_sha,
        "recognition_output_sha256": None,
    }


def main() -> int:
    root = ROOT / ARTIFACT_REL
    inp_bytes = (root / "input.json").read_bytes()
    input_doc = json.loads(inp_bytes)
    rec_path = root / "recognition_v3.json"
    rec_bytes = rec_path.read_bytes()
    recognition = json.loads(rec_bytes)
    truth_bytes = (root / "truth.json").read_bytes()
    truth_doc = json.loads(truth_bytes)
    report = assemble(recognition, truth_doc, input_doc, sha256(inp_bytes).hexdigest())
    report["recognition_output_sha256"] = sha256(rec_bytes).hexdigest()
    report["truth_input_sha256"] = sha256(truth_bytes).hexdigest()
    protocol_path = root / "protocol.json"
    report["generated_protocol_sha256"] = digest(protocol_path)
    source_paths = {
        "prepare_localization.py": ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/prepare_localization.py",
        "finalize_train_holdout.py": Path(__file__).resolve(),
        "test_prepare_localization.py": ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/test_prepare_localization.py",
        "test_finalize_train_holdout.py": ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/test_finalize_train_holdout.py",
        "protocol_train.json": ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/protocol_train.json",
        "Localization.csproj": ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/Localization.csproj",
        "Program.cs": ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/Program.cs",
        "OcrCropBatcher.cs": ROOT / "src/GraphReader.Ocr/OcrCropBatcher.cs",
        "LocalOnnxComponentTextRecognizer.cs": ROOT / "src/GraphReader.Ocr/LocalOnnxComponentTextRecognizer.cs",
        "OcrV8OfficialTextRecognizer.cs": ROOT / "src/GraphReader.Ocr/OcrV8OfficialTextRecognizer.cs",
        "OcrV8SourcePostprocessor.cs": ROOT / "src/GraphReader.Ocr/OcrV8SourcePostprocessor.cs",
        "OcrV8ProductionCompositionFactory.cs": ROOT / "src/GraphReader.Ocr/OcrV8ProductionCompositionFactory.cs",
    }
    report["executed_source_sha256"] = {name: digest(path) for name, path in source_paths.items()}
    report_path = root / "train_holdout_report_v2.json"
    if report_path.exists():
        raise FileExistsError(f"refusing to overwrite {report_path}")
    report_path.write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"report": report_path.as_posix(), "positive_rows": report["positive_rows"],
                      "background_rows": report["background_rows"], "numeric": report["numeric_v5"],
                      "official": report["official_ppocr"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
