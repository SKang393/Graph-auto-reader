# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Recover only observed text-row score:-nan(ind), as unknown diagnostic evidence."""
import hashlib
import json
import math
import re

STRINGS = re.compile(r'"(?:[^"\\]|\\.)*"')
BAD_SCORE = re.compile(r'("score"\s*:\s*)(-nan\(ind\))(?=\s*[,}])', re.I)


def reject_constant(value):
    raise ValueError("NONFINITE_JSON_TOKEN")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("DUPLICATE_JSON_FIELD")
        result[key] = value
    return result


def check_finite(value):
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError("NONFINITE_JSON_NUMBER")
    if isinstance(value, dict):
        for child in value.values():
            check_finite(child)
    elif isinstance(value, list):
        for child in value:
            check_finite(child)


def parse_score_diagnostic(raw):
    text = raw.decode("utf-8", "strict")
    strings = [match.span() for match in STRINGS.finditer(text)]
    repairs, markers = [], {}

    def replace(match):
        offset = match.start(2)
        if any(start <= offset < end for start, end in strings):
            return match.group(0)
        marker = "__MIRAGE_UNKNOWN_SCORE_DIAGNOSTIC_" + str(len(repairs)) + "__"
        markers[marker] = len(repairs)
        repairs.append(dict(offset=offset, byte_offset=len(text[:offset].encode("utf-8")),
                            token=match.group(2), key="score", reason="nonfinite_score_unknown"))
        return match.group(1) + json.dumps(marker)

    payload = json.loads(BAD_SCORE.sub(replace, text), parse_constant=reject_constant, object_pairs_hook=unique_object)
    check_finite(payload)
    if not repairs or not isinstance(payload, dict) or not isinstance(payload.get("text"), list):
        raise ValueError("NO_OBSERVED_TEXT_SCORE_REPAIR")
    used = set()
    for index, row in enumerate(payload["text"]):
        if not isinstance(row, dict):
            raise ValueError("TEXT_ROW_INVALID")
        score = row.get("score")
        if isinstance(score, str) and score in markers:
            repair_index = markers[score]
            if repair_index in used:
                raise ValueError("AMBIGUOUS_SCORE_MARKER")
            used.add(repair_index)
            row["score"] = None
            repairs[repair_index]["text_row_index"] = index
    if len(used) != len(repairs):
        raise ValueError("NON_TEXT_SCORE_REPAIR_REFUSED")
    # Original strings and finite scores are never rewritten or interpreted.
    return dict(payload=payload, raw_sha256=hashlib.sha256(raw).hexdigest(), repairs=repairs,
                requires_review=True, production_approved=False)
