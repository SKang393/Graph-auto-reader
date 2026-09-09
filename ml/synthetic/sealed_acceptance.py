# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed synthetic coverage contract for Goal 22 sealed acceptance reserves."""

from __future__ import annotations

from collections import Counter
from dataclasses import replace
import hashlib
import json
from pathlib import Path
from typing import Any, Mapping, Sequence

from ml.synthetic.dataset import CaseSpec, PRESETS
from ml.synthetic.io import canonical_json_bytes
from ml.synthetic.templates import FILL_STATES, MARKER_SHAPES


PROTOCOL_SCHEMA = "graphreader.goal22-sealed-coverage.v1"
ACCEPTANCE_SCOPE = "goal22.five-axis-family.real-range.v1"
ACCEPTANCE_PURPOSE = "sealed_acceptance"
ACCEPTANCE_PRESET = "smoke-plus-real-range-hand-drawn-v1"
PROTOCOL_PATH = Path("ml/policy/goal22-sealed-coverage-v1.json")
# Filled after the canonical protocol document is written. The protocol does not
# contain this value, so the binding has no self-hash cycle.
PROTOCOL_SHA256 = "b31b530b7e9e5e4528a31865621bba107161afca8c6dd58617aa3989401bfa0f"

GENERATOR_SOURCE_PATHS = (
    Path("ml/policy/goal22-sealed-coverage-v1.json"),
    Path("ml/synthetic/dataset.py"),
    Path("ml/synthetic/fonts.py"),
    Path("ml/synthetic/io.py"),
    Path("ml/synthetic/prepare_sealed_reserve.py"),
    Path("ml/synthetic/renderer.py"),
    Path("ml/synthetic/scene.schema.json"),
    Path("ml/synthetic/schema.py"),
    Path("ml/synthetic/sealed_acceptance.py"),
    Path("ml/synthetic/templates.py"),
)

HELD_OUT_FAMILIES = {
    "degradation": {"key": "camera_skew", "split": "test"},
    "font": {"key": "system_handwritten", "split": "test"},
    "marker": {"key": "irregular", "split": "test"},
    "renderer": {"key": "hand_drawn", "split": "test"},
    "template": {"key": "hand_drawn_grid", "split": "test"},
}

_PROTOCOL_FIELDS = {
    "schema", "protocol_id", "acceptance_scope", "purpose", "dataset",
    "ordered_case_specs", "required_coverage", "held_out_families",
    "generator_source_paths", "font_provenance", "seed_policy", "evidence_use",
}
_DATASET_FIELDS = {"source_presets", "transform"}
_COVERAGE_FIELDS = {
    "case_count", "panel_count", "series_count", "marker_count",
    "marker_style_catalog", "design_counts", "panel_count_case_counts",
    "session_count_case_counts", "canvas_width_case_counts",
    "panel_height_case_counts", "output_mode_case_counts",
    "resolved_feature_counts",
}
_SEED_POLICY_FIELDS = {
    "supplied_at_generation", "case_seed_formula",
    "distinct_dataset_seeds_produce_distinct_archives",
    "fail_closed_if_any_case_is_infeasible",
}
_EVIDENCE_USE_FIELDS = {
    "aggregate_only", "synthetic_only", "private_data",
    "training_permitted", "production_approval",
}
_FONT_PROVENANCE_FIELDS = {
    "requested", "disallowed_shared_resolved_files",
    "single_dependency_required", "dependency_bytes_verified_at_generation",
}
_ANNOTATION_FONT_FIELDS = {
    "requested", "resolved_file", "resolved_path", "family", "style",
    "size_px", "source", "sha256", "bundled",
}
_CASE_SPEC_FIELDS = {
    "design", "renderer_family", "panel_count", "session_count", "features",
    "canvas_width", "panel_height", "marker_radius", "stroke_width",
    "presentation", "degradations", "output_mode",
}


class SealedAcceptanceError(RuntimeError):
    """Raised when Goal 22 acceptance coverage or identity is incomplete."""


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _expected_font_provenance() -> dict[str, Any]:
    return {
        "requested": "handwritten",
        "disallowed_shared_resolved_files": ["arial.ttf", "dejavusans.ttf"],
        "single_dependency_required": True,
        "dependency_bytes_verified_at_generation": True,
    }


def acceptance_case_specs() -> tuple[CaseSpec, ...]:
    """Return the exact fixed corpus specification without rendering it."""

    return tuple(
        replace(spec, renderer_family="hand_drawn")
        for spec in (*PRESETS["smoke"], *PRESETS["real_range"])
    )


def case_spec_document(spec: CaseSpec) -> dict[str, Any]:
    return {
        "design": spec.design,
        "renderer_family": spec.renderer_family,
        "panel_count": spec.panel_count,
        "session_count": spec.session_count,
        "features": list(spec.features),
        "canvas_width": spec.canvas_width,
        "panel_height": spec.panel_height,
        "marker_radius": spec.marker_radius,
        "stroke_width": spec.stroke_width,
        "presentation": dict(spec.presentation) if spec.presentation is not None else None,
        "degradations": (
            [dict(stage) for stage in spec.degradations]
            if spec.degradations is not None else None
        ),
        "output_mode": spec.output_mode,
    }


def _string_counts(values: Sequence[Any]) -> dict[str, int]:
    return {
        str(key): count
        for key, count in sorted(Counter(values).items(), key=lambda item: str(item[0]))
    }


def _resolved_features(spec: CaseSpec) -> list[str]:
    requested = set(spec.features)
    if spec.design == "multiple_probe":
        requested.add("sparse_probes")
    if spec.design == "staggered_starts":
        requested.add("missing_sessions")
    requested.add("irregular_spacing")
    return [
        feature
        for feature in (
            "missing_sessions", "blank_phase_gaps", "sparse_probes",
            "irregular_spacing",
        )
        if feature in requested
    ]


def expected_coverage_document() -> dict[str, Any]:
    specs = acceptance_case_specs()
    resolved_features = [feature for spec in specs for feature in _resolved_features(spec)]
    return {
        "case_count": 21,
        "panel_count": 31,
        "series_count": 37,
        "marker_count": 838,
        "marker_style_catalog": [
            {"shape": shape, "fill": fill}
            for shape in MARKER_SHAPES
            for fill in FILL_STATES
        ],
        "design_counts": _string_counts([spec.design for spec in specs]),
        "panel_count_case_counts": _string_counts([spec.panel_count for spec in specs]),
        "session_count_case_counts": _string_counts([spec.session_count for spec in specs]),
        "canvas_width_case_counts": _string_counts([spec.canvas_width for spec in specs]),
        "panel_height_case_counts": _string_counts([spec.panel_height for spec in specs]),
        "output_mode_case_counts": _string_counts([spec.output_mode for spec in specs]),
        "resolved_feature_counts": _string_counts(resolved_features),
    }


def expected_protocol_document() -> dict[str, Any]:
    return {
        "schema": PROTOCOL_SCHEMA,
        "protocol_id": "goal22-sealed-coverage-v1",
        "acceptance_scope": ACCEPTANCE_SCOPE,
        "purpose": "aggregate_only_marker_center_acceptance",
        "dataset": {
            "source_presets": ["smoke", "real_range"],
            "transform": "replace_only_renderer_family_with_hand_drawn",
        },
        "ordered_case_specs": [
            case_spec_document(spec) for spec in acceptance_case_specs()
        ],
        "required_coverage": expected_coverage_document(),
        "held_out_families": HELD_OUT_FAMILIES,
        "generator_source_paths": [path.as_posix() for path in GENERATOR_SOURCE_PATHS],
        "font_provenance": _expected_font_provenance(),
        "seed_policy": {
            "supplied_at_generation": True,
            "case_seed_formula": "dataset_seed * 100 + ordinal",
            "distinct_dataset_seeds_produce_distinct_archives": True,
            "fail_closed_if_any_case_is_infeasible": True,
        },
        "evidence_use": {
            "aggregate_only": True,
            "synthetic_only": True,
            "private_data": False,
            "training_permitted": False,
            "production_approval": False,
        },
    }


def _object(value: Any, fields: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != fields:
        raise SealedAcceptanceError(f"{label} has an invalid shape")
    return value


def parse_supported_protocol(payload: bytes) -> dict[str, Any]:
    if _sha256_bytes(payload) != PROTOCOL_SHA256:
        raise SealedAcceptanceError("coverage protocol differs from its supported SHA-256")
    try:
        document = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise SealedAcceptanceError("coverage protocol is not valid JSON") from error
    _object(document, _PROTOCOL_FIELDS, "coverage protocol")
    _object(document["dataset"], _DATASET_FIELDS, "coverage protocol dataset")
    _object(document["required_coverage"], _COVERAGE_FIELDS, "coverage protocol requirements")
    _object(document["seed_policy"], _SEED_POLICY_FIELDS, "coverage protocol seed policy")
    _object(document["evidence_use"], _EVIDENCE_USE_FIELDS, "coverage protocol evidence use")
    _object(
        document["font_provenance"],
        _FONT_PROVENANCE_FIELDS,
        "coverage protocol font provenance",
    )
    specs = document["ordered_case_specs"]
    if not isinstance(specs, list) or any(
        not isinstance(spec, dict) or set(spec) != _CASE_SPEC_FIELDS for spec in specs
    ):
        raise SealedAcceptanceError("coverage protocol case specifications are invalid")
    if (
        document["schema"] != PROTOCOL_SCHEMA
        or document["protocol_id"] != "goal22-sealed-coverage-v1"
        or document["acceptance_scope"] != ACCEPTANCE_SCOPE
        or document["purpose"] != "aggregate_only_marker_center_acceptance"
        or document["dataset"] != {
            "source_presets": ["smoke", "real_range"],
            "transform": "replace_only_renderer_family_with_hand_drawn",
        }
        or document["held_out_families"] != HELD_OUT_FAMILIES
        or document["generator_source_paths"]
        != [path.as_posix() for path in GENERATOR_SOURCE_PATHS]
        or document["font_provenance"] != _expected_font_provenance()
        or document["seed_policy"] != {
            "supplied_at_generation": True,
            "case_seed_formula": "dataset_seed * 100 + ordinal",
            "distinct_dataset_seeds_produce_distinct_archives": True,
            "fail_closed_if_any_case_is_infeasible": True,
        }
        or document["evidence_use"] != {
            "aggregate_only": True,
            "synthetic_only": True,
            "private_data": False,
            "training_permitted": False,
            "production_approval": False,
        }
    ):
        raise SealedAcceptanceError(
            "coverage protocol has invalid Goal 22 safety or source identities"
        )
    return document


def _protocol_case_specs(protocol: Mapping[str, Any]) -> tuple[CaseSpec, ...]:
    return tuple(
        CaseSpec(
            design=spec["design"],
            renderer_family=spec["renderer_family"],
            panel_count=spec["panel_count"],
            session_count=spec["session_count"],
            features=tuple(spec["features"]),
            canvas_width=spec["canvas_width"],
            panel_height=spec["panel_height"],
            marker_radius=spec["marker_radius"],
            stroke_width=spec["stroke_width"],
            presentation=spec["presentation"],
            degradations=(
                tuple(spec["degradations"])
                if spec["degradations"] is not None else None
            ),
            output_mode=spec["output_mode"],
        )
        for spec in protocol["ordered_case_specs"]
    )


def load_supported_protocol(repository_root: Path) -> tuple[dict[str, Any], bytes]:
    root = repository_root.resolve()
    path = (root / PROTOCOL_PATH).resolve()
    if root not in path.parents or not path.is_file():
        raise SealedAcceptanceError("supported coverage protocol is missing")
    payload = path.read_bytes()
    protocol = parse_supported_protocol(payload)
    if (
        protocol["ordered_case_specs"]
        != [case_spec_document(spec) for spec in acceptance_case_specs()]
        or protocol["required_coverage"] != expected_coverage_document()
    ):
        raise SealedAcceptanceError(
            "live synthetic presets differ from the supported coverage protocol"
        )
    return protocol, payload


def require_supported_identity(
    acceptance_scope: Any,
    coverage_protocol_path: Any,
    coverage_protocol_sha256: Any,
) -> None:
    if (
        acceptance_scope != ACCEPTANCE_SCOPE
        or coverage_protocol_path != PROTOCOL_PATH.as_posix()
        or coverage_protocol_sha256 != PROTOCOL_SHA256
    ):
        raise SealedAcceptanceError(
            "acceptance reserve does not use the supported Goal 22 coverage identity"
        )


def _case_mapping(value: Any, label: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise SealedAcceptanceError(f"{label} must be an object")
    return value


def _validated_annotation_font(
    annotation: Mapping[str, Any],
    scene: Mapping[str, Any],
    protocol: Mapping[str, Any],
    ordinal: int,
) -> Mapping[str, Any]:
    font = _case_mapping(annotation.get("font"), f"acceptance font provenance {ordinal}")
    if set(font) != _ANNOTATION_FONT_FIELDS:
        raise SealedAcceptanceError(
            f"acceptance font provenance {ordinal} has an invalid shape"
        )
    fonts = annotation.get("fonts")
    if not isinstance(fonts, list) or fonts != [font]:
        raise SealedAcceptanceError(
            f"acceptance font provenance {ordinal} is not a single dependency"
        )
    requirement = protocol["font_provenance"]
    requested = font.get("requested")
    resolved_file = font.get("resolved_file")
    resolved_path = font.get("resolved_path")
    family = font.get("family")
    style = font.get("style")
    sha256 = font.get("sha256")
    size_px = font.get("size_px")
    presentation = _case_mapping(
        scene.get("presentation"), f"acceptance presentation {ordinal}"
    )
    expected_size = presentation.get(
        "font_size_px", presentation.get("font_size", 14)
    )
    if (
        requested != requirement["requested"]
        or not isinstance(resolved_file, str)
        or not resolved_file
        or resolved_file.casefold()
        in set(requirement["disallowed_shared_resolved_files"])
        or not isinstance(resolved_path, str)
        or not resolved_path
        or not isinstance(family, str)
        or not family
        or not isinstance(style, str)
        or not style
        or type(size_px) is not int
        or size_px != expected_size
        or font.get("source") != "system"
        or font.get("bundled") is not False
        or not isinstance(sha256, str)
        or len(sha256) != 64
        or any(character not in "0123456789abcdef" for character in sha256)
    ):
        raise SealedAcceptanceError(
            f"acceptance font provenance {ordinal} does not identify a distinct system handwritten dependency"
        )
    return font


def verify_acceptance_font_dependencies(
    cases: Sequence[Mapping[str, Any]],
) -> tuple[dict[str, str], ...]:
    """Verify recorded font hashes against the exact host bytes before archiving."""

    dependencies: dict[tuple[str, str], dict[str, str]] = {}
    for ordinal, raw_case in enumerate(cases):
        case = _case_mapping(raw_case, f"acceptance case {ordinal}")
        annotation = _case_mapping(
            case.get("annotation"), f"acceptance annotation {ordinal}"
        )
        font = _case_mapping(
            annotation.get("font"), f"acceptance font provenance {ordinal}"
        )
        resolved_path = font.get("resolved_path")
        recorded_sha256 = font.get("sha256")
        if not isinstance(resolved_path, str) or not isinstance(recorded_sha256, str):
            raise SealedAcceptanceError(
                f"acceptance font provenance {ordinal} cannot verify dependency bytes"
            )
        path = Path(resolved_path)
        if not path.is_file():
            raise SealedAcceptanceError(
                f"acceptance font dependency {ordinal} is unavailable for byte verification"
            )
        actual_sha256 = _sha256_bytes(path.read_bytes())
        if actual_sha256 != recorded_sha256:
            raise SealedAcceptanceError(
                f"acceptance font dependency {ordinal} differs from its recorded SHA-256"
            )
        key = (str(font.get("resolved_file")), recorded_sha256)
        dependencies[key] = {
            "resolved_file": key[0],
            "sha256": key[1],
        }
    if len(dependencies) != 1:
        raise SealedAcceptanceError(
            "acceptance cases do not share one exact handwritten font dependency"
        )
    return tuple(dependencies[key] for key in sorted(dependencies))


def validate_acceptance_cases(
    config: Mapping[str, Any],
    source_rows: Sequence[Mapping[str, str]],
    protocol_payload: bytes,
    cases: Sequence[Mapping[str, Any]],
) -> dict[str, Any]:
    """Validate semantic coverage after generic byte and image checks succeed."""

    protocol = parse_supported_protocol(protocol_payload)
    require_supported_identity(
        config.get("acceptance_scope"),
        config.get("coverage_protocol_path"),
        config.get("coverage_protocol_sha256"),
    )
    if config.get("purpose") != ACCEPTANCE_PURPOSE or config.get("preset") != ACCEPTANCE_PRESET:
        raise SealedAcceptanceError("acceptance generation config has the wrong purpose or preset")
    source_paths = [row.get("path") for row in source_rows]
    if source_paths != protocol["generator_source_paths"]:
        raise SealedAcceptanceError("acceptance generator source snapshot is incomplete")
    protocol_row = next(
        (row for row in source_rows if row.get("path") == PROTOCOL_PATH.as_posix()), None
    )
    if protocol_row is None or protocol_row.get("sha256") != PROTOCOL_SHA256:
        raise SealedAcceptanceError("acceptance protocol is absent from the source snapshot")

    specs = _protocol_case_specs(protocol)
    required = protocol["required_coverage"]
    if len(cases) != len(specs) or len(cases) != required["case_count"]:
        raise SealedAcceptanceError("acceptance archive has incomplete case coverage")

    scene_ids: set[str] = set()
    design_values: list[str] = []
    panel_count_values: list[int] = []
    session_count_values: list[int] = []
    canvas_width_values: list[int] = []
    panel_height_values: list[int] = []
    output_modes: list[str] = []
    resolved_features: list[str] = []
    used_styles: set[tuple[str, str]] = set()
    panel_count = 0
    series_count = 0
    marker_count = 0
    font_dependencies: set[tuple[str, str]] = set()
    dataset_seed = config.get("dataset_seed")

    for ordinal, (raw_case, spec) in enumerate(zip(cases, specs, strict=True)):
        case = _case_mapping(raw_case, f"acceptance case {ordinal}")
        scene = _case_mapping(case.get("scene"), f"acceptance scene {ordinal}")
        annotation = _case_mapping(
            case.get("annotation"), f"acceptance annotation {ordinal}"
        )
        expected_seed = dataset_seed * 100 + ordinal
        scene_id = scene.get("scene_id")
        if (
            not isinstance(scene_id, str)
            or not scene_id
            or scene_id in scene_ids
            or scene.get("seed") != expected_seed
            or annotation.get("scene_id") != scene_id
            or annotation.get("seed") != expected_seed
            or scene.get("design") != spec.design
            or annotation.get("design") != spec.design
            or scene.get("families") != HELD_OUT_FAMILIES
        ):
            raise SealedAcceptanceError(
                f"acceptance case {ordinal} differs from its ordered identity"
            )
        scene_ids.add(scene_id)

        layout = _case_mapping(scene.get("layout"), f"acceptance layout {ordinal}")
        canvas = _case_mapping(scene.get("canvas"), f"acceptance canvas {ordinal}")
        annotation_canvas = _case_mapping(
            annotation.get("canvas"), f"acceptance annotation canvas {ordinal}"
        )
        expected_height = 80 + spec.panel_count * spec.panel_height
        expected_features = _resolved_features(spec)
        if (
            layout.get("panel_count") != spec.panel_count
            or layout.get("session_count") != spec.session_count
            or layout.get("features") != expected_features
            or canvas.get("width") != spec.canvas_width
            or canvas.get("height") != expected_height
            or annotation_canvas.get("width") != spec.canvas_width
            or annotation_canvas.get("height") != expected_height
            or case.get("image_mode") != spec.output_mode
        ):
            raise SealedAcceptanceError(
                f"acceptance case {ordinal} differs from its required range dimensions"
            )
        presentation = _case_mapping(
            scene.get("presentation"), f"acceptance presentation {ordinal}"
        )
        if spec.presentation is not None and any(
            presentation.get(key) != value for key, value in spec.presentation.items()
        ):
            raise SealedAcceptanceError(
                f"acceptance case {ordinal} changed a prescribed presentation value"
            )
        style = _case_mapping(scene.get("style"), f"acceptance style {ordinal}")
        if (
            spec.marker_radius is not None
            and style.get("marker_radius") != float(spec.marker_radius)
        ) or (
            spec.stroke_width is not None
            and style.get("stroke_width") != spec.stroke_width
        ):
            raise SealedAcceptanceError(
                f"acceptance case {ordinal} changed a prescribed drawing scale"
            )
        if spec.degradations is not None and scene.get("degradations") != [
            dict(stage) for stage in spec.degradations
        ]:
            raise SealedAcceptanceError(
                f"acceptance case {ordinal} changed prescribed degradation stages"
            )
        font = _validated_annotation_font(annotation, scene, protocol, ordinal)
        font_dependencies.add((str(font["resolved_file"]), str(font["sha256"])))

        panels = scene.get("panels")
        annotation_panels = annotation.get("panels")
        if (
            not isinstance(panels, list)
            or not isinstance(annotation_panels, list)
            or len(panels) != spec.panel_count
            or len(annotation_panels) != spec.panel_count
        ):
            raise SealedAcceptanceError(
                f"acceptance case {ordinal} has inconsistent panel coverage"
            )
        case_series = [series for panel in panels for series in panel.get("series", [])]
        case_points = [point for panel in panels for point in panel.get("points", [])]
        annotation_markers = [
            marker for panel in annotation_panels for marker in panel.get("markers", [])
        ]
        if len(annotation_markers) != len(case_points):
            raise SealedAcceptanceError(
                f"acceptance case {ordinal} annotation marker coverage is incomplete"
            )
        for series in case_series:
            if not isinstance(series, Mapping):
                raise SealedAcceptanceError("acceptance series record is invalid")
            used_styles.add((str(series.get("shape")), str(series.get("fill"))))

        design_values.append(spec.design)
        panel_count_values.append(spec.panel_count)
        session_count_values.append(spec.session_count)
        canvas_width_values.append(spec.canvas_width)
        panel_height_values.append(spec.panel_height)
        output_modes.append(spec.output_mode)
        resolved_features.extend(expected_features)
        panel_count += len(panels)
        series_count += len(case_series)
        marker_count += len(case_points)

    observed = {
        "case_count": len(cases),
        "panel_count": panel_count,
        "series_count": series_count,
        "marker_count": marker_count,
        "marker_style_catalog": [
            style
            for style in required["marker_style_catalog"]
            if (style["shape"], style["fill"]) in used_styles
        ],
        "design_counts": _string_counts(design_values),
        "panel_count_case_counts": _string_counts(panel_count_values),
        "session_count_case_counts": _string_counts(session_count_values),
        "canvas_width_case_counts": _string_counts(canvas_width_values),
        "panel_height_case_counts": _string_counts(panel_height_values),
        "output_mode_case_counts": _string_counts(output_modes),
        "resolved_feature_counts": _string_counts(resolved_features),
    }
    if observed != required:
        raise SealedAcceptanceError("acceptance archive misses required semantic coverage")
    if len(font_dependencies) != 1:
        raise SealedAcceptanceError(
            "acceptance cases do not share one exact handwritten font dependency"
        )
    return observed


__all__ = [
    "ACCEPTANCE_PRESET",
    "ACCEPTANCE_PURPOSE",
    "ACCEPTANCE_SCOPE",
    "GENERATOR_SOURCE_PATHS",
    "HELD_OUT_FAMILIES",
    "PROTOCOL_PATH",
    "PROTOCOL_SCHEMA",
    "PROTOCOL_SHA256",
    "SealedAcceptanceError",
    "acceptance_case_specs",
    "case_spec_document",
    "expected_coverage_document",
    "expected_protocol_document",
    "load_supported_protocol",
    "parse_supported_protocol",
    "require_supported_identity",
    "validate_acceptance_cases",
    "verify_acceptance_font_dependencies",
]
