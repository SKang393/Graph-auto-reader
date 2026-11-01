# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Freeze truth-blind localized crops for a consumed synthetic dev comparison."""
from __future__ import annotations

from hashlib import sha256
import json
import math
from pathlib import Path
import stat
from typing import Any

ROOT = Path(__file__).resolve().parents[6]
SCORE_REL = "artifacts/stroke-seeded-xtick-v1-relaxed-width-numeric-confirmation-v1/score_rows.json"
SCORE_SHA = "30be7b889ac536cabdaba0b7155b590c08c6d5d2f0505cf1c34a7496725f21fe"
CANDIDATE_REL = "artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v2/candidate_rows.json"
CANDIDATE_SHA = "efb3da7ee27baaea3ffa10b2202c0419d174ffea3b375667264de378307ad1cd"
OVERLAP_REL = "artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v2/overlap_audit.json"
OVERLAP_SHA = "3b2b14340cb0dc4c40b5e0d8417cc83f226c14d32b1c6efbdec87f23d7d01056"
PAIRED_REL = "artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v2/paired_report.json"
PAIRED_SHA = "5612b128c796c224084493b3e4890cc6f5c0a22eb5d36fa1472ddd62c952d172"
TRAIN_MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2/manifest.json"
TRAIN_MANIFEST_SHA = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
TRAIN_PROTOCOL_REL = "artifacts/stroke-seeded-xtick-v1-seed-crop-localization-train-v1/protocol.json"
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-seed-crop-localization-dev-v1"
LIMIT = 20_000_000


class DevAdmissionError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def read_project(relative: str, expected: str | None = None, maximum: int = LIMIT) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts:
        raise DevAdmissionError("invalid project-relative input")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        try:
            info = current.lstat()
        except OSError as exc:
            raise DevAdmissionError("missing/unreadable input path component") from exc
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise DevAdmissionError("symlink/reparse input rejected")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise DevAdmissionError("non-directory input ancestor")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > maximum):
            raise DevAdmissionError("input is not a bounded regular file")
    with current.open("rb") as stream:
        raw = stream.read(maximum + 1)
    if len(raw) > maximum:
        raise DevAdmissionError("input exceeded byte bound")
    if expected is not None and digest(raw) != expected:
        raise DevAdmissionError(f"input hash mismatch: {relative}")
    return raw


def proposal_seed_in_crop(center: list[float]) -> tuple[float, float]:
    """Invert the fixed V6 crop geometry without consulting any truth labels."""
    if len(center) != 2 or not all(math.isfinite(float(value)) for value in center):
        raise DevAdmissionError("proposal center is invalid")
    cx, cy = map(float, center)
    left = round(cx - 24)
    top = round(cy - 2)
    return 2.0 * (cx - left), 2.0 * (cy - top)


def make_inputs() -> tuple[dict[str, Any], dict[str, Any]]:
    score_raw = read_project(SCORE_REL, SCORE_SHA)
    cand_raw = read_project(CANDIDATE_REL, CANDIDATE_SHA)
    overlap_raw = read_project(OVERLAP_REL, OVERLAP_SHA, 1_000_000)
    paired_raw = read_project(PAIRED_REL, PAIRED_SHA, 3_000_000)
    train_raw = read_project(TRAIN_MANIFEST_REL, TRAIN_MANIFEST_SHA)
    train_protocol_raw = read_project(TRAIN_PROTOCOL_REL, maximum=100_000)
    score, candidates, overlap, paired, train_manifest = (json.loads(raw) for raw in (score_raw, cand_raw, overlap_raw, paired_raw, train_raw))
    train_protocol = json.loads(train_protocol_raw)
    if score.get("truth_read") is not False or score.get("status") != "truth-blind-classifier-score-pass-complete":
        raise DevAdmissionError("source score artifact is not truth-blind and complete")
    if score.get("candidate_manifest_sha256") != CANDIDATE_SHA or score.get("overlap_audit_sha256") != OVERLAP_SHA:
        raise DevAdmissionError("score receipt does not bind frozen candidate/overlap artifacts")
    if paired.get("private_or_sealed_inputs_read") is not False or paired.get("training_performed") is not False:
        raise DevAdmissionError("prior paired report has unexpected data or training state")
    if overlap.get("eligible_rows") != 433 or overlap.get("quarantined_rows") != 9 or overlap.get("holdout_image_or_annotation_bytes_read") is not False:
        raise DevAdmissionError("frozen overlap audit counts or holdout-input status changed")
    if train_manifest.get("split") != "train" or train_protocol.get("schema") != "graphreader.seed-crop-localization-protocol.v1":
        raise DevAdmissionError("localizer source manifest or ROI protocol is not train-only")
    roi = [float(v) for v in train_protocol["roi_seed_relative_xyxy"]]
    train_hashes = {record["image_sha256"] for record in train_manifest["records"] if record["split"] == "train"}
    p1_holdout = {record["image_sha256"] for record in train_manifest["records"] if record["source_group_id"] in train_protocol["holdout_source_groups"]}
    cand_by_id = {row["proposal_id"]: row for row in candidates["rows"]}
    if len(cand_by_id) != len(candidates["rows"]):
        raise DevAdmissionError("candidate IDs are not unique")
    selected = [row for row in score["rows"] if row["p3_selected"] or row["p5_selected"]]
    if len(score["rows"]) != 433 or len(selected) != 116 or score["selected_union_rows"] != len(selected):
        raise DevAdmissionError("frozen score row/selected counts changed")
    rows = []
    overlap_rows = []
    seen_ids = set()
    for row in selected:
        proposal_id = row["proposal_id"]
        cand = cand_by_id.get(proposal_id)
        if cand is None or cand["crop_sha256"] != row["crop_sha256"] or cand["proposal"] != row["proposal"]:
            raise DevAdmissionError("score row does not bind exact frozen proposal metadata")
        if proposal_id in seen_ids:
            raise DevAdmissionError("selected proposal ID repeated")
        seen_ids.add(proposal_id)
        crop_raw = read_project(row["crop_path"], row["crop_sha256"], 1_000_000)
        seed_x, seed_y = proposal_seed_in_crop(row["proposal"]["center"])
        box = [seed_x + roi[0], seed_y + roi[1], seed_x + roi[2], seed_y + roi[3]]
        reasons = []
        if row["crop_sha256"] in train_hashes:
            reasons.append("crop_hash_overlaps_localizer_train_manifest")
        if row["crop_sha256"] in p1_holdout:
            reasons.append("crop_hash_overlaps_localizer_internal_holdout_metadata")
        clipped = box[0] < 0 or box[1] < 0 or box[2] > 256 or box[3] > 96
        if clipped:
            reasons.append("localized_roi_clipped")
        if reasons:
            overlap_rows.append({"proposal_id": proposal_id, "crop_sha256": row["crop_sha256"], "reasons": reasons})
        rows.append({"proposal_id": proposal_id, "source_id": row["source_id"], "panel_id": row["panel_id"],
                     "p3_score": float(row["p3_score"]), "p5_score": float(row["p5_score"]),
                     "p3_selected": bool(row["p3_selected"]), "p5_selected": bool(row["p5_selected"]),
                     "proposal": row["proposal"], "image_path": row["crop_path"], "image_sha256": digest(crop_raw),
                     "proposal_seed_crop_xy": [seed_x, seed_y], "localized_roi_xyxy": box,
                     "roi_clipped": clipped, "quarantine_reasons": reasons})
    if len(rows) != 116 or len({row["image_sha256"] for row in rows}) > len(rows):
        raise DevAdmissionError("candidate row count or hash invariant failed")
    input_doc = {"schema": "graphreader.seed-crop-localization-input.v1", "truth_read": False,
                 "development_probe": True, "train_roi_protocol_sha256": digest(train_protocol_raw), "rows": rows}
    audit = {"schema": "graphreader.seed-crop-localization-dev-overlap.v1", "rows": len(rows),
             "eligible_rows": len(rows) - len(overlap_rows), "quarantined_rows": len(overlap_rows),
             "quarantine": overlap_rows, "unique_crop_hashes": len({row["image_sha256"] for row in rows}),
             "crop_hash_overlap_localizer_train": sum(row["image_sha256"] in train_hashes for row in rows),
             "crop_hash_overlap_localizer_holdout": sum(row["image_sha256"] in p1_holdout for row in rows),
             "previous_classifier_overlap_audit_sha256": OVERLAP_SHA, "previous_candidate_manifest_sha256": CANDIDATE_SHA,
             "frozen_score_rows_sha256": SCORE_SHA, "train_roi_protocol_sha256": digest(train_protocol_raw),
             "source_identity_overlap": overlap["source_identity_overlap"],
             "source_id_overlap": overlap["source_id_overlap"],
             "full_panel_hash_overlap": overlap["full_panel_hash_overlap"],
             "p1_holdout_source_id_overlap": overlap["p1_holdout_source_id_overlap"],
             "p1_holdout_source_identity_overlap": overlap["p1_holdout_source_identity_overlap"],
             "holdout_image_or_annotation_bytes_read": False, "truth_read": False,
             "dev_sealed_private_inputs_read": False}
    return input_doc, audit


def main() -> int:
    output = ROOT / OUTPUT_REL
    output.mkdir(parents=True, exist_ok=True)
    input_doc, audit = make_inputs()
    input_path, audit_path = output / "input.json", output / "overlap_preflight.json"
    if input_path.exists() or audit_path.exists():
        raise FileExistsError("refusing to overwrite dev preflight artifacts")
    input_path.write_text(json.dumps(input_doc, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    audit["input_sha256"] = digest(input_path.read_bytes())
    audit_path.write_text(json.dumps(audit, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    protocol = {"schema": "graphreader.seed-crop-localization-dev-protocol.v1",
                "input_sha256": audit["input_sha256"], "train_roi_protocol_sha256": audit["train_roi_protocol_sha256"],
                "overlap_preflight_sha256": digest(audit_path.read_bytes()), "score_rows_sha256": SCORE_SHA,
                "candidate_manifest_sha256": CANDIDATE_SHA, "paired_classifier_report_sha256": PAIRED_SHA,
                "fixed_classifier_threshold": 0.5, "threshold_tuning": False, "training_performed": False,
                "truth_read": False, "dev_is_already_consumed": True, "formal_acceptance": False}
    protocol_path = output / "protocol.json"
    if protocol_path.exists():
        raise FileExistsError("refusing to overwrite dev protocol")
    protocol_path.write_text(json.dumps(protocol, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"rows": audit["rows"], "eligible": audit["eligible_rows"], "quarantined": audit["quarantined_rows"],
                      "unique_crop_hashes": audit["unique_crop_hashes"], "train_overlap": audit["crop_hash_overlap_localizer_train"],
                      "holdout_overlap": audit["crop_hash_overlap_localizer_holdout"], "source_id_overlap": audit["source_id_overlap"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
