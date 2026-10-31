# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic V4 procedural train-only data, labelled from the glyph mask."""

from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import random
from typing import Any, Mapping

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parents[2]
OUTPUT_RELATIVE = "artifacts/fragmented-xtick-v4-diverse-train-v1"
CANVAS = (256, 96)
BACKGROUND, INK = 245, 24
POSITIVE_COUNT, NEGATIVE_COUNT = 192, 96
FONT_RELATIVES = (
    "src/GraphReader.App/Assets/Fonts/NotoSans-Regular.ttf",
    "src/GraphReader.App/Assets/Fonts/NotoSans-Medium.ttf",
    "src/GraphReader.App/Assets/Fonts/NotoSans-SemiBold.ttf",
)
FONT_SIZES = (12, 14, 16, 18, 20, 22, 24)
LABELS = ("6", "9", "13", "18", "45", "90", "250", "2.75", "-8", "0.125", "35.5", "750")
P4_TRAIN_LABELS = ("10", "12", "20", "25", "50", "100", "2.5", "-1")
FAMILY = "fragmented-xtick-v4-diverse-train"


class V4MaterializationError(RuntimeError):
    pass


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def font_path(relative: str) -> Path:
    path = REPO_ROOT / relative
    if not path.is_file() or path.is_symlink():
        raise V4MaterializationError("required project-local font is unavailable")
    try:
        path.resolve(strict=True).relative_to(REPO_ROOT.resolve(strict=True))
    except (ValueError, OSError) as error:
        raise V4MaterializationError("font path escapes project root") from error
    return path


def cases() -> tuple[dict[str, Any], ...]:
    rows = []
    for i in range(POSITIVE_COUNT + NEGATIVE_COUNT):
        seed, positive = 24105001 + i, i < POSITIVE_COUNT
        row: dict[str, Any] = {
            "case_id": f"{FAMILY}-train-{seed}", "seed": seed, "split": "train",
            "kind": "positive" if positive else "background_only", "materialized": False,
        }
        row["case_identity_sha256"] = digest(canonical_json({"family": FAMILY, "seed": seed, "kind": row["kind"]}))
        if positive:
            text = LABELS[i % len(LABELS)]
            row.update(text=text, font_relative=FONT_RELATIVES[(i // len(LABELS)) % len(FONT_RELATIVES)],
                       font_size=FONT_SIZES[(i // (len(LABELS) * len(FONT_RELATIVES))) % len(FONT_SIZES)],
                       origin=[5 + (i * 43) % 150, 3 + (i * 19) % 62],
                       gap_after=(i // 5) % max(1, len(text) - 1) if len(text) > 1 else None,
                       gap_pixels=(i // 3) % 5)
        else:
            row.update(text=None, font_relative=None, font_size=None, origin=None, gap_after=None, gap_pixels=None)
        rows.append(row)
    return tuple(rows)


def _draw_label(mask: Image.Image, case: Mapping[str, Any], font: ImageFont.FreeTypeFont) -> None:
    x, y = case["origin"]
    draw = ImageDraw.Draw(mask)
    for index, char in enumerate(case["text"]):
        draw.text((x, y), char, fill=255, font=font)
        x += max(1, round(float(font.getlength(char))))
        if index == case["gap_after"]:
            x += int(case["gap_pixels"])


def _draw_confusers(image: Image.Image, seed: int, anchor: tuple[int, int]) -> None:
    rng, draw = random.Random(seed), ImageDraw.Draw(image)
    y = max(8, min(88, anchor[1] + rng.randint(-18, 20)))
    points = [(8, y)]
    for x in (72, 136, 198, 248):
        y = max(4, min(91, y + rng.randint(-7, 7)))
        points.append((x, y))
    draw.line(points, fill=rng.choice((INK, 36, 54)), width=rng.choice((1, 1, 2, 3)))
    for _ in range(rng.randint(2, 5)):
        x, y, length = rng.randint(10, 246), rng.randint(12, 83), rng.randint(3, 13)
        draw.line((x, y, x + rng.randint(-2, 2), max(1, min(95, y + rng.choice((-1, 1)) * length))), fill=INK, width=rng.choice((1, 2)))
    trace = [(rng.randint(5, 30), rng.randint(18, 78))]
    trace.extend((x, rng.randint(10, 86)) for x in (72, 132, 192, 250))
    draw.line(trace, fill=rng.choice((INK, 40, 64)), width=rng.choice((1, 1, 2)))
    for _ in range(rng.randint(1, 4)):
        x, y, r = rng.randint(5, 250), rng.randint(5, 90), rng.choice((2, 3, 4))
        box = (x-r, y-r, x+r, y+r)
        shape = rng.randrange(3)
        if shape == 0: draw.ellipse(box, outline=INK, width=1)
        elif shape == 1: draw.rectangle(box, outline=INK, width=1)
        else: draw.polygon(((x, y-r), (x+r, y+r), (x-r, y+r)), outline=INK)


def render_case(case: Mapping[str, Any]) -> tuple[Image.Image, dict[str, Any]]:
    if case.get("split") != "train" or case.get("materialized") is not False or not str(case.get("case_id", "")).startswith(FAMILY + "-train-"):
        raise V4MaterializationError("only fresh V4 train cases may be rendered")
    image, mask = Image.new("L", CANVAS, BACKGROUND), Image.new("L", CANVAS, 0)
    positive = case.get("kind") == "positive"
    if positive:
        text, relative, size, origin = case.get("text"), case.get("font_relative"), case.get("font_size"), case.get("origin")
        if text not in LABELS or text in P4_TRAIN_LABELS or relative not in FONT_RELATIVES or size not in FONT_SIZES or not isinstance(origin, list):
            raise V4MaterializationError("invalid positive case or P4 label overlap")
        font = ImageFont.truetype(str(font_path(relative)), size=int(size))
        _draw_label(mask, case, font)
        bounds = mask.getbbox()
        if bounds is None or bounds[2] > CANVAS[0] or bounds[3] > CANVAS[1]:
            raise V4MaterializationError("glyph mask is empty or clipped")
        _draw_confusers(image, int(case["seed"]), tuple(origin))
        image.paste(INK, mask=mask)
        target: dict[str, Any] | None = {"role": "XTick", "text": text, "bounding_box_xyxy": list(bounds),
            "visible_glyph_pixel_count": sum(value > 0 for value in mask.tobytes()), "glyph_mask_sha256": digest(mask.tobytes())}
    elif case.get("kind") == "background_only":
        seed = int(case["seed"])
        _draw_confusers(image, seed, (20 + seed % 190, 8 + seed % 78))
        target = None
    else:
        raise V4MaterializationError("unknown case kind")
    annotation = {"schema": "graphreader.ocr-fragmented-xtick-v4-train-annotation.v1", "case_id": case["case_id"],
        "case_identity_sha256": case["case_identity_sha256"], "split": "train", "kind": case["kind"],
        "coordinate_space": "image_pixels_xyxy", "target": target, "font_relative": case.get("font_relative"),
        "font_size": case.get("font_size"), "font_sha256": digest(font_path(case["font_relative"]).read_bytes()) if positive else None,
        "materialized": True}
    return image, annotation


def png_bytes(image: Image.Image) -> bytes:
    stream = BytesIO()
    image.save(stream, format="PNG", compress_level=9, optimize=False)
    return stream.getvalue()


def materialize() -> dict[str, Any]:
    output = REPO_ROOT / OUTPUT_RELATIVE
    if output.exists() or output.is_symlink(): raise V4MaterializationError("V4 train output must be fresh")
    output.mkdir(parents=True)
    (output / "images").mkdir(); (output / "annotations").mkdir()
    records = []
    for case in cases():
        image, annotation = render_case(case)
        image_data = png_bytes(image); annotation["image_sha256"] = digest(image_data)
        annotation_data = canonical_json(annotation)
        image_rel, annotation_rel = f"images/{case['case_id']}.png", f"annotations/{case['case_id']}.json"
        (output / image_rel).write_bytes(image_data); (output / annotation_rel).write_bytes(annotation_data)
        records.append({"case_id": case["case_id"], "case_identity_sha256": case["case_identity_sha256"], "split": "train",
            "kind": case["kind"], "image_path": image_rel, "image_sha256": digest(image_data),
            "annotation_path": annotation_rel, "annotation_sha256": digest(annotation_data)})
    positive = sum(row["kind"] == "positive" for row in records)
    negative = len(records) - positive
    manifest = {"schema": "graphreader.ocr-fragmented-xtick-v4-train.v1", "family": FAMILY, "split": "train",
        "generator_sha256": digest(Path(__file__).read_bytes()), "record_count": len(records), "positive_count": positive,
        "background_only_count": negative, "canvas": list(CANVAS), "labels": list(LABELS),
        "distribution": {"font_weights": ["regular", "medium", "semibold"], "font_sizes": list(FONT_SIZES),
            "label_lengths": sorted({len(label) for label in LABELS}), "positions": "deterministically varied x/y origins",
            "glyph_gaps_pixels": [0, 1, 2, 3, 4], "graph_confusers": ["sloped axes", "variable ticks", "polylines", "hollow circles", "hollow squares", "hollow triangles"]},
        "font_files": list(FONT_RELATIVES), "font_sha256": {p: digest(font_path(p).read_bytes()) for p in FONT_RELATIVES},
        "font_sizes": list(FONT_SIZES), "case_identities": [{"case_id": r["case_id"], "sha256": r["case_identity_sha256"], "split": r["split"]} for r in records],
        "probe_inputs_read": False, "dev_sealed_private_inputs_read": False, "records": records}
    data = canonical_json(manifest); (output / "manifest.json").write_bytes(data)
    return {"output_root": output.relative_to(REPO_ROOT).as_posix(), "manifest_sha256": digest(data), "record_count": len(records),
            "positive_count": positive, "background_only_count": negative}


if __name__ == "__main__": print(json.dumps(materialize(), indent=2, sort_keys=True))
