# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bind V25 plot domains to authenticated annotation-free runtime panels."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from hashlib import sha256
import json
import math
from pathlib import Path
from typing import Any, Mapping, Sequence

import numpy as np

from ml.markers.gate_seal import canonical_json_bytes
from ml.markers.center.mask_preserving_v24 import (
    runtime_binding,
    runtime_binding_v2,
    runtime_family_scenes,
    runtime_inputs,
)
from ml.markers.center.mask_preserving_v24.runtime_family_scenes import (
    RuntimeFamilyScene,
    RuntimeFamilySceneJoin,
)
from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    RuntimeEvidenceBinding,
    RuntimeImplementationIdentity,
    RuntimeInputError,
    RuntimeModelIdentity,
    ValidatedRuntimePanelInput,
)

from .proposal_domain import PlotDomain


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
FAMILY_BINDING_SHA256 = "ddaccff59347f06045166c02ffa5f7c1488cd485db0a46115ce0411451c7c85f"
TRAIN_TENSOR_SET_SHA256 = "28c5f0b752beaff8273082fe6bab0be121547218cfa7462a0665374941fe0c7c"
DEV_TENSOR_SET_SHA256 = "7a8d688123e76c1b144ea5a19a00cb3a76a004c25c02194cb75345ddedae8120"
EXPECTED_TRAIN_SOURCES = 20
EXPECTED_TRAIN_PANELS = 23
EXPECTED_DEV_SOURCES = 3
EXPECTED_DEV_PANELS = 9


@dataclass(frozen=True)
class RuntimeDomainFailure:
    split: str
    source_sha256: str
    panel_id: str | None
    status: str
    stage: str | None
    error: str | None


class RuntimeDomainBindingError(RuntimeInputError):
    """A source-bound plot domain could not be produced without substitution."""

    def __init__(
        self,
        message: str,
        failures: Sequence[RuntimeDomainFailure] = (),
    ) -> None:
        super().__init__(message)
        self.failures = tuple(failures)


@dataclass(frozen=True)
class RuntimePanelDomain:
    runtime_input: ValidatedRuntimePanelInput
    domain: PlotDomain
    source_width: int
    source_height: int
    source_polygon: tuple[tuple[float, float], ...]
    source_to_panel_matrix: tuple[float, ...]
    panel_to_source_matrix: tuple[float, ...]
    manifest_sha256: str
    report_sha256: str
    axis_model: RuntimeModelIdentity
    evidence_sha256: str


@dataclass(frozen=True)
class RuntimeDomainBinding:
    train: tuple[RuntimePanelDomain, ...]
    dev: tuple[RuntimePanelDomain, ...]
    implementation: RuntimeImplementationIdentity
    binding_sha256: str
    expected_mapping_audit_sha256: str
    failures: tuple[RuntimeDomainFailure, ...] = ()


@dataclass(frozen=True)
class DomainBoundRuntimeScene:
    scene: RuntimeFamilyScene
    panel_domain: RuntimePanelDomain


@dataclass(frozen=True)
class RuntimeDomainSceneBinding:
    train: tuple[DomainBoundRuntimeScene, ...]
    dev: tuple[DomainBoundRuntimeScene, ...]


def load_runtime_domain_binding(
    path: Path,
    expected_sha256: str = FAMILY_BINDING_SHA256,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> RuntimeDomainBinding:
    """Load the frozen V25 family domains without reading annotation truth."""

    if expected_sha256.lower() != FAMILY_BINDING_SHA256:
        raise RuntimeDomainBindingError("V25 requires the preregistered family binding identity")
    root = repository_root.resolve()
    _, payload = runtime_inputs._read_bound_artifact(
        path, expected_sha256, root, "V25 family runtime binding"
    )
    try:
        document = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise RuntimeDomainBindingError("V25 family runtime binding is not valid JSON") from exception
    runtime_binding._object(document, runtime_binding_v2._SCOPE_FIELDS, "V25 family binding")
    if (
        document["schema"] != runtime_binding_v2.BINDING_SCHEMA
        or document["synthetic_only"] is not True
        or document["private_data"] is not False
        or type(document["sealed_runs"]) is not int
        or document["sealed_runs"] != 0
        or document["training_input_ready"] is not True
        or document["production_approved"] is not False
        or document["complete_artifact_mask"] is not False
    ):
        raise RuntimeDomainBindingError("V25 family binding has a foreign or approved scope")
    if (
        document["train_tensor_set_sha256"] != TRAIN_TENSOR_SET_SHA256
        or document["dev_tensor_set_sha256"] != DEV_TENSOR_SET_SHA256
    ):
        raise RuntimeDomainBindingError("V25 family tensor identities differ from preregistration")
    runtime_binding_v2._validate_bound_sources(document)

    raw_train = document["train"]
    if not isinstance(raw_train, list) or not raw_train:
        raise RuntimeDomainBindingError("V25 family binding requires ordered train inputs")
    train_bindings = tuple(runtime_binding._split(item, root) for item in raw_train)
    dev_binding = runtime_binding._split(document["dev"], root)
    implementation = runtime_binding._implementation(document["implementation"], root)
    axis_model = _axis_model(implementation)

    reports: dict[tuple[str, str], tuple[Mapping[str, Any], RuntimeEvidenceBinding]] = {}
    failures: list[RuntimeDomainFailure] = []
    for binding in (*train_bindings, dev_binding):
        _, report_payload = runtime_inputs._read_bound_artifact(
            binding.report_path,
            binding.report_sha256,
            root,
            f"{binding.split} domain report",
        )
        try:
            report = json.loads(report_payload)
        except (UnicodeDecodeError, json.JSONDecodeError) as exception:
            raise RuntimeDomainBindingError("runtime domain report is not valid JSON") from exception
        if not isinstance(report, dict):
            raise RuntimeDomainBindingError("runtime domain report must be an object")
        failures.extend(_collect_failures(binding.split, report))
        for source_sha, panel_id, panel, case in _report_panels(report):
            key = (source_sha, panel_id)
            if key in reports:
                raise RuntimeDomainBindingError("runtime domain reports repeat a panel identity")
            reports[key] = ({"panel": panel, "case": case}, binding)
    if failures:
        raise RuntimeDomainBindingError(
            "runtime axis evidence contains failed sources or panels; no domain was substituted",
            failures,
        )

    train_panels: list[ValidatedRuntimePanelInput] = []
    dev_panels: tuple[ValidatedRuntimePanelInput, ...] | None = None
    common_dev_identity: tuple[tuple[Any, ...], ...] | None = None
    seen_train_sources: set[str] = set()
    seen_train_panels: set[str] = set()
    seen_train_payloads: set[str] = set()
    seen_train_scenes: set[int] = set()
    seen_train_datasets: set[int] = set()
    train_families: list[str] = []
    for train_binding in train_bindings:
        inputs = runtime_inputs.load_bound_runtime_inputs(
            train_binding, dev_binding, implementation, repository_root=root
        )
        identity = runtime_binding_v2._panel_identity(inputs.dev)
        if common_dev_identity is None:
            common_dev_identity = identity
            dev_panels = inputs.dev
        elif identity != common_dev_identity:
            raise RuntimeDomainBindingError("runtime pairs changed the fixed development inputs")
        _add_disjoint_train_inputs(
            inputs.train,
            inputs.dev,
            seen_train_sources,
            seen_train_panels,
            seen_train_payloads,
            seen_train_scenes,
            seen_train_datasets,
        )
        train_panels.extend(inputs.train)
        train_families.extend(panel.family for panel in inputs.train)
    if dev_panels is None:
        raise RuntimeDomainBindingError("runtime domain binding has no development panels")
    all_panels = (*train_panels, *dev_panels)
    if len({panel.panel_id for panel in all_panels}) != len(all_panels):
        raise RuntimeDomainBindingError("runtime domain binding repeats a panel identity")
    dev_payloads = [panel.panel_sha256 for panel in dev_panels]
    if len(set(dev_payloads)) != len(dev_payloads):
        raise RuntimeDomainBindingError("development inputs repeat panel payloads")
    train_tensor_hashes = _panel_tensor_hashes(train_panels)
    dev_tensor_hashes = _panel_tensor_hashes(dev_panels)
    if (
        len(set(train_tensor_hashes)) != len(train_tensor_hashes)
        or len(set(dev_tensor_hashes)) != len(dev_tensor_hashes)
        or set(train_tensor_hashes) & set(dev_tensor_hashes)
    ):
        raise RuntimeDomainBindingError("runtime plane tensors repeat within or across splits")
    _validate_family_disjointness(train_families, [panel.family for panel in dev_panels])
    _validate_preregistered_counts(train_panels, dev_panels)
    if _tensor_set_sha256(train_panels) != TRAIN_TENSOR_SET_SHA256:
        raise RuntimeDomainBindingError("runtime train plane tensors differ from preregistration")
    if _tensor_set_sha256(dev_panels) != DEV_TENSOR_SET_SHA256:
        raise RuntimeDomainBindingError("runtime development plane tensors differ from preregistration")

    train_domains = tuple(
        _panel_domain(panel, reports, axis_model) for panel in train_panels
    )
    dev_domains = tuple(_panel_domain(panel, reports, axis_model) for panel in dev_panels)
    if len(reports) != len(train_domains) + len(dev_domains):
        raise RuntimeDomainBindingError("runtime report panel coverage is incomplete or foreign")
    return RuntimeDomainBinding(
        train_domains,
        dev_domains,
        implementation,
        expected_sha256.lower(),
        runtime_inputs._sha(document["mapping_audit_sha256"], "mapping_audit_sha256"),
    )


def bind_domains_to_joined_scenes(
    domains: RuntimeDomainBinding,
    joined: RuntimeFamilySceneJoin,
) -> RuntimeDomainSceneBinding:
    """Join authenticated domains after the existing truth join has succeeded."""

    if domains.failures:
        raise RuntimeDomainBindingError("failed runtime cases cannot be removed during truth join")
    actual_audit = runtime_binding.mapping_audit_sha256(joined)
    if actual_audit != domains.expected_mapping_audit_sha256:
        raise RuntimeDomainBindingError("joined truth mapping audit differs from the frozen binding")
    return RuntimeDomainSceneBinding(
        _bind_split(domains.train, joined.train, "train"),
        _bind_split(domains.dev, joined.dev, "validation"),
    )


def join_runtime_domain_truth(domains: RuntimeDomainBinding) -> RuntimeDomainSceneBinding:
    """Run the existing label join only after annotation-free domains validate."""

    train_scenes: list[RuntimeFamilyScene] = []
    audits = []
    dataset_order: list[int] = []
    panels_by_dataset: dict[int, list[ValidatedRuntimePanelInput]] = {}
    for domain in domains.train:
        dataset_seed = domain.runtime_input.dataset_seed
        if dataset_seed not in panels_by_dataset:
            dataset_order.append(dataset_seed)
            panels_by_dataset[dataset_seed] = []
        panels_by_dataset[dataset_seed].append(domain.runtime_input)
    try:
        for dataset_seed in dataset_order:
            scenes, split_audits, observed_seed = runtime_family_scenes._join_split(
                tuple(panels_by_dataset[dataset_seed]), "train"
            )
            if observed_seed != dataset_seed:
                raise RuntimeDomainBindingError("truth join changed a train dataset identity")
            train_scenes.extend(scenes)
            audits.extend(split_audits)
        dev_scenes, dev_audits, _ = runtime_family_scenes._join_split(
            tuple(domain.runtime_input for domain in domains.dev), "validation"
        )
    except runtime_family_scenes.RuntimeFamilySceneError as exception:
        raise RuntimeDomainBindingError(str(exception), domains.failures) from exception
    audits.extend(dev_audits)
    joined = RuntimeFamilySceneJoin(tuple(train_scenes), dev_scenes, tuple(audits))
    return bind_domains_to_joined_scenes(domains, joined)


def _panel_domain(
    panel: ValidatedRuntimePanelInput,
    reports: Mapping[
        tuple[str, str], tuple[Mapping[str, Any], RuntimeEvidenceBinding]
    ],
    axis_model: RuntimeModelIdentity,
) -> RuntimePanelDomain:
    key = (panel.source_sha256, panel.panel_id)
    if key not in reports:
        raise RuntimeDomainBindingError("validated runtime panel has no exact axis report")
    record, binding = reports[key]
    report_panel = record["panel"]
    case = record["case"]
    return _panel_domain_from_report(panel, case, report_panel, binding, axis_model)


def _panel_domain_from_report(
    panel: ValidatedRuntimePanelInput,
    case: Mapping[str, Any],
    report_panel: Mapping[str, Any],
    binding: RuntimeEvidenceBinding,
    axis_model: RuntimeModelIdentity,
) -> RuntimePanelDomain:
    if (
        case.get("image_sha256") != panel.source_sha256
        or report_panel.get("source_image_sha256") != panel.source_sha256
        or report_panel.get("panel_id") != panel.panel_id
        or report_panel.get("image_sha256") != panel.panel_sha256
        or report_panel.get("width") != panel.width
        or report_panel.get("height") != panel.height
    ):
        raise RuntimeDomainBindingError("axis report does not match the validated runtime panel")
    source_width = _positive_int(report_panel.get("source_width"), "source width")
    source_height = _positive_int(report_panel.get("source_height"), "source height")
    if case.get("width") != source_width or case.get("height") != source_height:
        raise RuntimeDomainBindingError("axis report source dimensions are inconsistent")
    if _box(report_panel.get("crop"), integer=True) != tuple(panel.crop):
        raise RuntimeDomainBindingError("axis report encoded crop differs from the runtime input")
    if _box(report_panel.get("requested_crop"), integer=False) != tuple(panel.requested_crop):
        raise RuntimeDomainBindingError("axis report requested crop differs from the runtime input")
    crop_x, crop_y, crop_width, crop_height = panel.crop
    if crop_x + crop_width > source_width or crop_y + crop_height > source_height:
        raise RuntimeDomainBindingError("runtime panel crop exceeds the source image")

    source_to_panel = _matrix(report_panel.get("source_to_panel_matrix"), "source-to-panel")
    panel_to_source = _matrix(report_panel.get("panel_to_source_matrix"), "panel-to-source")
    expected_forward = (1.0, 0.0, -crop_x, 0.0, 1.0, -crop_y, 0.0, 0.0, 1.0)
    expected_inverse = (1.0, 0.0, crop_x, 0.0, 1.0, crop_y, 0.0, 0.0, 1.0)
    if not _close_sequence(source_to_panel, expected_forward) or not _close_sequence(
        panel_to_source, expected_inverse
    ):
        raise RuntimeDomainBindingError("runtime crop matrices do not encode the exact crop translation")

    axis = report_panel.get("axis")
    if not isinstance(axis, dict) or set(axis) != {"envelope", "geometry"}:
        raise RuntimeDomainBindingError("runtime panel is missing exact axis evidence")
    envelope = axis["envelope"]
    geometry = axis["geometry"]
    if not isinstance(envelope, dict) or not isinstance(geometry, dict):
        raise RuntimeDomainBindingError("runtime panel axis evidence must be objects")
    source_envelopes = report_panel.get("composed_mask_source_envelopes")
    if not isinstance(source_envelopes, list):
        raise RuntimeDomainBindingError("runtime panel has no composed evidence envelopes")
    matching_envelopes = [item for item in source_envelopes if isinstance(item, dict) and item.get("stage") == "axis"]
    if len(matching_envelopes) != 1 or matching_envelopes[0] != envelope:
        raise RuntimeDomainBindingError("axis geometry envelope differs from composed runtime evidence")
    expected_model = {
        "model_id": axis_model.model_id,
        "version": axis_model.version,
        "sha256": axis_model.sha256,
        "provider": axis_model.provider,
    }
    if (
        envelope.get("contract_version") != 1
        or envelope.get("panel_id") != panel.panel_id
        or envelope.get("stage") != "axis"
        or envelope.get("stage_version") != axis_model.stage_version
        or envelope.get("input_sha256") != panel.panel_sha256
        or envelope.get("coordinate_space") != "original_pixels"
        or envelope.get("model") != expected_model
        or geometry.get("coordinate_space") != "original_pixels"
    ):
        raise RuntimeDomainBindingError("axis geometry identity differs from the runtime input")
    polygon = _plot_polygon(geometry.get("plot_polygon"))
    domain_identity_payload = {
        "schema": "graphreader.marker-plot-domain.v1",
        "binding_sha256": FAMILY_BINDING_SHA256,
        "manifest_sha256": binding.manifest_sha256,
        "report_sha256": binding.report_sha256,
        "split": panel.split,
        "source_sha256": panel.source_sha256,
        "panel_id": panel.panel_id,
        "panel_sha256": panel.panel_sha256,
        "crop": panel.crop,
        "source_to_panel_matrix": source_to_panel,
        "panel_to_source_matrix": panel_to_source,
        "axis_model": asdict(axis_model),
        "polygon": polygon,
    }
    evidence_sha = sha256(canonical_json_bytes(domain_identity_payload)).hexdigest()
    try:
        domain = PlotDomain.runtime_plot(
            panel.width, panel.height, polygon, identity=evidence_sha
        )
    except ValueError as exception:
        raise RuntimeDomainBindingError(str(exception)) from exception
    source_polygon = tuple(
        _map_point(panel_to_source, point[0], point[1]) for point in polygon
    )
    if any(
        x < 0 or x > source_width or y < 0 or y > source_height
        for x, y in source_polygon
    ):
        raise RuntimeDomainBindingError("axis polygon maps outside the immutable source image")
    for panel_point, source_point in zip(polygon, source_polygon, strict=True):
        if not _close_sequence(_map_point(source_to_panel, *source_point), panel_point):
            raise RuntimeDomainBindingError("axis polygon does not round-trip through the crop mapping")
    return RuntimePanelDomain(
        panel,
        domain,
        source_width,
        source_height,
        source_polygon,
        source_to_panel,
        panel_to_source,
        binding.manifest_sha256,
        binding.report_sha256,
        axis_model,
        evidence_sha,
    )


def _bind_split(
    domains: tuple[RuntimePanelDomain, ...],
    scenes: tuple[RuntimeFamilyScene, ...],
    split: str,
) -> tuple[DomainBoundRuntimeScene, ...]:
    by_panel = {domain.runtime_input.panel_id: domain for domain in domains}
    if len(by_panel) != len(domains) or len(scenes) != len(domains):
        raise RuntimeDomainBindingError(f"{split} truth/domain panel coverage differs")
    result: list[DomainBoundRuntimeScene] = []
    for scene in scenes:
        domain = by_panel.get(scene.panel_id)
        if domain is None:
            raise RuntimeDomainBindingError(f"{split} truth contains a foreign panel")
        panel = domain.runtime_input
        if (
            scene.split != split
            or panel.split != split
            or scene.dataset_seed != panel.dataset_seed
            or scene.family != panel.family
            or scene.seed != panel.scene_seed
            or scene.source_sha256 != panel.source_sha256
            or scene.panel_sha256 != panel.panel_sha256
            or scene.crop != panel.crop
            or scene.requested_crop != panel.requested_crop
            or tuple(scene.tensor.shape[1:]) != (panel.height, panel.width)
        ):
            raise RuntimeDomainBindingError("truth scene identity differs from its runtime domain")
        result.append(DomainBoundRuntimeScene(scene, domain))
    return tuple(result)


def _report_panels(report: Mapping[str, Any]):
    cases = report.get("cases")
    if not isinstance(cases, list):
        raise RuntimeDomainBindingError("runtime report cases must be an array")
    for case in cases:
        if not isinstance(case, dict):
            raise RuntimeDomainBindingError("runtime report case must be an object")
        source_sha = runtime_inputs._sha(case.get("image_sha256"), "runtime source")
        panels = case.get("panels")
        if not isinstance(panels, list):
            raise RuntimeDomainBindingError("runtime report panels must be an array")
        for panel in panels:
            if not isinstance(panel, dict) or not isinstance(panel.get("panel_id"), str):
                raise RuntimeDomainBindingError("runtime report panel identity is invalid")
            yield source_sha, panel["panel_id"], panel, case


def _collect_failures(split: str, report: Mapping[str, Any]) -> tuple[RuntimeDomainFailure, ...]:
    failures: list[RuntimeDomainFailure] = []
    cases = report.get("cases")
    if not isinstance(cases, list):
        raise RuntimeDomainBindingError("runtime report cases must be an array")
    for case in cases:
        if not isinstance(case, dict):
            raise RuntimeDomainBindingError("runtime report case must be an object")
        source_sha = runtime_inputs._sha(case.get("image_sha256"), "runtime source")
        if case.get("status") != "panels-completed":
            failures.append(RuntimeDomainFailure(split, source_sha, None, str(case.get("status")), _optional_string(case.get("stage")), _optional_string(case.get("error"))))
        panels = case.get("panels", [])
        if not isinstance(panels, list):
            raise RuntimeDomainBindingError("runtime report panels must be an array")
        for panel in panels:
            if not isinstance(panel, dict):
                raise RuntimeDomainBindingError("runtime report panel must be an object")
            if panel.get("status") != "seed-completed":
                failures.append(RuntimeDomainFailure(split, source_sha, _optional_string(panel.get("panel_id")), str(panel.get("status")), _optional_string(panel.get("stage")), _optional_string(panel.get("error"))))
    return tuple(failures)


def _axis_model(implementation: RuntimeImplementationIdentity) -> RuntimeModelIdentity:
    models = tuple(model for model in implementation.models if model.stage == "axis")
    if len(models) != 1:
        raise RuntimeDomainBindingError("runtime implementation requires one exact axis identity")
    return models[0]


def _add_disjoint_train_inputs(
    train: tuple[ValidatedRuntimePanelInput, ...],
    dev: tuple[ValidatedRuntimePanelInput, ...],
    sources: set[str],
    panels: set[str],
    payloads: set[str],
    scenes: set[int],
    datasets: set[int],
) -> None:
    current_sources = {panel.source_sha256 for panel in train}
    current_panels = {panel.panel_id for panel in train}
    current_payloads = [panel.panel_sha256 for panel in train]
    current_scenes = {panel.scene_seed for panel in train}
    current_datasets = {panel.dataset_seed for panel in train}
    if len(current_datasets) != 1 or datasets & current_datasets:
        raise RuntimeDomainBindingError("ordered train inputs repeat a dataset identity")
    if sources & current_sources or panels & current_panels or scenes & current_scenes:
        raise RuntimeDomainBindingError("ordered train inputs repeat source, panel, or scene identities")
    if len(set(current_payloads)) != len(current_payloads) or payloads & set(current_payloads):
        raise RuntimeDomainBindingError("ordered train inputs repeat panel payloads")
    if set(current_payloads) & {panel.panel_sha256 for panel in dev}:
        raise RuntimeDomainBindingError("train and development panel payloads overlap")
    sources.update(current_sources)
    panels.update(current_panels)
    payloads.update(current_payloads)
    scenes.update(current_scenes)
    datasets.update(current_datasets)


def _validate_family_disjointness(train: Sequence[str], dev: Sequence[str]) -> None:
    train_axes = {axis: set() for axis in runtime_binding_v2.FAMILY_AXES}
    dev_axes = {axis: set() for axis in runtime_binding_v2.FAMILY_AXES}
    for target, values in ((train_axes, train), (dev_axes, dev)):
        for value in values:
            parsed = runtime_binding_v2._parse_family_identity(value)
            for axis, family in parsed.items():
                target[axis].add(family)
    if any(train_axes[axis] & dev_axes[axis] for axis in train_axes):
        raise RuntimeDomainBindingError("train and development semantic family axes overlap")


def _validate_preregistered_counts(
    train: Sequence[ValidatedRuntimePanelInput],
    dev: Sequence[ValidatedRuntimePanelInput],
) -> None:
    counts = (
        len({panel.source_sha256 for panel in train}),
        len(train),
        len({panel.source_sha256 for panel in dev}),
        len(dev),
    )
    expected = (EXPECTED_TRAIN_SOURCES, EXPECTED_TRAIN_PANELS, EXPECTED_DEV_SOURCES, EXPECTED_DEV_PANELS)
    if counts != expected:
        raise RuntimeDomainBindingError("runtime source or panel counts differ from preregistration")


def _tensor_set_sha256(panels: Sequence[ValidatedRuntimePanelInput]) -> str:
    return sha256(canonical_json_bytes(sorted(_panel_tensor_hashes(panels)))).hexdigest()


def _panel_tensor_hashes(panels: Sequence[ValidatedRuntimePanelInput]) -> tuple[str, ...]:
    hashes: list[str] = []
    for panel in panels:
        gray = panel.gray8.astype(np.float32, copy=False) / np.float32(255.0)
        tensor = np.stack((np.float32(1.0) - gray, panel.ocr_mask, panel.artifact_mask), axis=0)
        hashes.append(sha256(tensor.tobytes(order="C")).hexdigest())
    return tuple(hashes)


def _plot_polygon(value: Any) -> tuple[tuple[float, float], ...]:
    if not isinstance(value, dict) or set(value) != {"bottom_left", "bottom_right", "top_right", "top_left", "points"}:
        raise RuntimeDomainBindingError("axis plot polygon has an invalid shape")
    keys = ("bottom_left", "bottom_right", "top_right", "top_left")
    points = tuple(_point(value[key], f"plot {key}") for key in keys)
    raw_points = value["points"]
    if not isinstance(raw_points, list) or len(raw_points) != 4:
        raise RuntimeDomainBindingError("axis plot polygon points are incomplete")
    listed = tuple(_point(point, "plot point") for point in raw_points)
    if listed != points:
        raise RuntimeDomainBindingError("axis plot polygon corner and point order differ")
    return points


def _point(value: Any, label: str) -> tuple[float, float]:
    if not isinstance(value, dict) or set(value) != {"x", "y", "is_finite"} or value["is_finite"] is not True:
        raise RuntimeDomainBindingError(f"{label} is not an exact finite point")
    if type(value["x"]) is bool or type(value["y"]) is bool:
        raise RuntimeDomainBindingError(f"{label} has invalid coordinates")
    try:
        point = (float(value["x"]), float(value["y"]))
    except (TypeError, ValueError) as exception:
        raise RuntimeDomainBindingError(f"{label} has invalid coordinates") from exception
    if not all(math.isfinite(coordinate) for coordinate in point):
        raise RuntimeDomainBindingError(f"{label} has nonfinite coordinates")
    return point


def _box(value: Any, *, integer: bool) -> tuple[Any, Any, Any, Any]:
    if not isinstance(value, dict) or set(value) != {"x", "y", "width", "height"}:
        raise RuntimeDomainBindingError("runtime crop has an invalid shape")
    if any(type(value[key]) is bool for key in ("x", "y", "width", "height")):
        raise RuntimeDomainBindingError("runtime crop has invalid coordinates")
    converter = int if integer else float
    try:
        result = tuple(converter(value[key]) for key in ("x", "y", "width", "height"))
    except (TypeError, ValueError) as exception:
        raise RuntimeDomainBindingError("runtime crop has invalid coordinates") from exception
    if integer and any(type(value[key]) is not int for key in ("x", "y", "width", "height")):
        raise RuntimeDomainBindingError("encoded runtime crop must use integer coordinates")
    if not all(math.isfinite(float(item)) for item in result):
        raise RuntimeDomainBindingError("runtime crop must be finite")
    return result


def _matrix(value: Any, label: str) -> tuple[float, ...]:
    if not isinstance(value, list) or len(value) != 9:
        raise RuntimeDomainBindingError(f"{label} matrix must contain nine values")
    if any(type(item) is bool for item in value):
        raise RuntimeDomainBindingError(f"{label} matrix has invalid values")
    try:
        result = tuple(float(item) for item in value)
    except (TypeError, ValueError) as exception:
        raise RuntimeDomainBindingError(f"{label} matrix has invalid values") from exception
    if not all(math.isfinite(item) for item in result):
        raise RuntimeDomainBindingError(f"{label} matrix must be finite")
    return result


def _map_point(matrix: tuple[float, ...], x: float, y: float) -> tuple[float, float]:
    w = matrix[6] * x + matrix[7] * y + matrix[8]
    if not math.isfinite(w) or abs(w) <= 1e-12:
        raise RuntimeDomainBindingError("runtime crop matrix maps a point to infinity")
    return (
        (matrix[0] * x + matrix[1] * y + matrix[2]) / w,
        (matrix[3] * x + matrix[4] * y + matrix[5]) / w,
    )


def _close_sequence(left: Sequence[float], right: Sequence[float]) -> bool:
    return len(left) == len(right) and all(
        math.isclose(float(a), float(b), rel_tol=0.0, abs_tol=1e-9)
        for a, b in zip(left, right, strict=True)
    )


def _positive_int(value: Any, label: str) -> int:
    if type(value) is not int or value <= 0:
        raise RuntimeDomainBindingError(f"{label} must be a positive integer")
    return value


def _optional_string(value: Any) -> str | None:
    return value if isinstance(value, str) and value else None


__all__ = [
    "DomainBoundRuntimeScene",
    "FAMILY_BINDING_SHA256",
    "RuntimeDomainBinding",
    "RuntimeDomainBindingError",
    "RuntimeDomainFailure",
    "RuntimeDomainSceneBinding",
    "RuntimePanelDomain",
    "bind_domains_to_joined_scenes",
    "join_runtime_domain_truth",
    "load_runtime_domain_binding",
]
