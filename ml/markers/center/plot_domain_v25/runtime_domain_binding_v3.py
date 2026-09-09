# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authenticate V3 synthetic runtime panels before exposing V25 plot domains.

This loader is separate from the historical V24/V25 binding.  It accepts only
an exact, externally reviewed binding hash and keeps runtime planes
annotation-free.  V3 truth is regenerated and joined only by the explicit
``join_runtime_domain_truth_v3`` call after every source PNG, report, panel,
crop, plane, implementation, and plot domain has validated.

The binding authenticates logical identities rather than requiring every crop
or tensor payload to be unique.  Distinct source scenes and panel GUIDs may
legitimately contain byte-identical background crops.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
from hashlib import sha256
import json
from pathlib import Path
from types import ModuleType
from typing import Any, Mapping, Sequence

from PIL import Image

from ml.markers import gate_seal
from ml.markers.gate_seal import canonical_json_bytes
from ml.markers.center.mask_preserving_v24 import (
    runtime_binding,
    runtime_family_scenes,
    runtime_inputs,
)
from ml.markers.center.mask_preserving_v24.runtime_family_scenes import (
    RuntimeFamilyScene,
    RuntimeFamilySceneJoin,
    _RenderedSource,
)
from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    RuntimeEvidenceBinding,
    RuntimeImplementationIdentity,
    RuntimeSourceIdentity,
    ValidatedRuntimePanelInput,
)
from ml.synthetic.dataset import (
    FAMILY_AXES,
    PRESETS,
    _build_scenes,
    _family_keys,
    _scene_split,
)
import ml.synthetic.runtime_graph_visible_content_v3 as visible_content_v3
from ml.synthetic.io import (
    canonical_json_bytes as synthetic_canonical_json_bytes,
    png_bytes as synthetic_png_bytes,
)

from . import proposal_domain
from .proposal_domain import PlotDomain
from . import runtime_domain_binding as historical_domain


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
BINDING_SCHEMA = "graphreader.marker-runtime-plot-domain-binding.v3"
PROTOCOL_SCHEMA = "graphreader.synthetic-runtime-graph-visible-content-protocol.v1"
EXPECTED_GENERATOR_VERSION = "runtime-graph-visible-content-v3"
EXPECTED_MANIFEST_SOURCE = "project-owned-synthetic-five-axis-family-v1"
EXPECTED_TRAIN_SOURCE_COUNT = 20
EXPECTED_DEV_SOURCE_COUNT = 3
EXPECTED_TRAIN_MARKER_TRUTH_COUNT = 500
EXPECTED_DEV_MARKER_TRUTH_COUNT = 206

# Every project source that can change scene construction or V3 pixels.  The
# binding supplies reviewed hashes for this fixed list.  Report production and
# panel loading implementations are bound separately below.
GENERATOR_SOURCE_PATHS = (
    Path("ml/synthetic/runtime_graph_visible_content_v3.py"),
    Path("ml/synthetic/runtime_graph_visible_content_v3_protocol.json"),
    Path("ml/synthetic/runtime_graph_axis_preserving_v2.py"),
    Path("ml/synthetic/runtime_graph_axis_preserving_v2_protocol.json"),
    Path("ml/synthetic/contact_sheet.py"),
    Path("ml/synthetic/dataset.py"),
    Path("ml/synthetic/fonts.py"),
    Path("ml/synthetic/io.py"),
    Path("ml/synthetic/renderer.py"),
    Path("ml/synthetic/scene.schema.json"),
    Path("ml/synthetic/schema.py"),
    Path("ml/synthetic/templates.py"),
)

_SCOPE_FIELDS = {
    "schema",
    "synthetic_only",
    "private_data",
    "sealed_runs",
    "training_input_ready",
    "production_approved",
    "complete_artifact_mask",
    "train",
    "dev",
    "implementation",
    "generator_profile",
    "canonical_json_source_sha256",
    "runtime_binding_source_sha256",
    "runtime_loader_source_sha256",
    "runtime_join_source_sha256",
    "historical_domain_source_sha256",
    "proposal_domain_source_sha256",
    "binding_v3_source_sha256",
    "panel_inventory_sha256",
    "train_tensor_set_sha256",
    "dev_tensor_set_sha256",
    "mapping_audit_sha256",
    "truth_mapping_sha256",
}
_PROFILE_FIELDS = {"generator_version", "protocol", "sources", "train", "dev"}
_PROTOCOL_REFERENCE_FIELDS = {"path", "sha256"}
_SPLIT_PROFILE_FIELDS = {
    "dataset_seeds",
    "scene_seeds",
    "source_count",
    "marker_truth_count",
    "resolved_scene_identity_set_sha256",
}


RuntimeDomainBindingError = historical_domain.RuntimeDomainBindingError
RuntimeDomainFailure = historical_domain.RuntimeDomainFailure
RuntimePanelDomain = historical_domain.RuntimePanelDomain
DomainBoundRuntimeScene = historical_domain.DomainBoundRuntimeScene
RuntimeDomainSceneBinding = historical_domain.RuntimeDomainSceneBinding


@dataclass(frozen=True)
class V3SplitProfile:
    dataset_seeds: tuple[int, ...]
    scene_seeds: tuple[int, ...]
    source_count: int
    marker_truth_count: int
    resolved_scene_identity_set_sha256: str


@dataclass(frozen=True)
class V3GeneratorProfile:
    generator_version: str
    protocol_path: Path
    protocol_sha256: str
    sources: tuple[RuntimeSourceIdentity, ...]
    train: V3SplitProfile
    dev: V3SplitProfile


@dataclass(frozen=True)
class RuntimeDomainBindingV3:
    train: tuple[RuntimePanelDomain, ...]
    dev: tuple[RuntimePanelDomain, ...]
    implementation: RuntimeImplementationIdentity
    profile: V3GeneratorProfile
    train_bindings: tuple[RuntimeEvidenceBinding, ...]
    dev_binding: RuntimeEvidenceBinding
    binding_sha256: str
    panel_inventory_sha256: str
    expected_mapping_audit_sha256: str
    expected_truth_mapping_sha256: str
    failures: tuple[RuntimeDomainFailure, ...] = ()


def load_runtime_domain_binding_v3(
    path: Path,
    expected_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> RuntimeDomainBindingV3:
    """Load annotation-free V3 planes and source-derived plot domains.

    ``expected_sha256`` is supplied by a separately reviewed configuration.
    No identity is learned from the binding being checked.
    """

    root = repository_root.resolve()
    _, payload = runtime_inputs._read_bound_artifact(
        path, expected_sha256, root, "V25 V3 runtime domain binding"
    )
    document = _json_object(payload, "V25 V3 runtime domain binding")
    runtime_binding._object(document, _SCOPE_FIELDS, "V25 V3 runtime domain binding")
    if (
        document["schema"] != BINDING_SCHEMA
        or document["synthetic_only"] is not True
        or document["private_data"] is not False
        or type(document["sealed_runs"]) is not int
        or document["sealed_runs"] != 0
        or document["training_input_ready"] is not True
        or document["production_approved"] is not False
        or document["complete_artifact_mask"] is not False
    ):
        raise RuntimeDomainBindingError("V25 V3 binding has a foreign or approved scope")

    _validate_helper_sources(document)
    profile = _parse_profile(document["generator_profile"], root)
    _validate_profile_sources(profile, root)
    _validate_profile_protocol(profile, root)
    _validate_profile_scene_identities(profile)

    raw_train = document["train"]
    if not isinstance(raw_train, list) or not raw_train:
        raise RuntimeDomainBindingError("V25 V3 binding requires ordered train exchanges")
    train_bindings = tuple(runtime_binding._split(item, root) for item in raw_train)
    dev_binding = runtime_binding._split(document["dev"], root)
    implementation = runtime_binding._implementation(document["implementation"], root)
    runtime_inputs._validate_implementation_shape(implementation)
    if implementation.synthetic_sources != profile.sources:
        raise RuntimeDomainBindingError(
            "runtime implementation source identities differ from the V3 generator profile"
        )
    runtime_inputs._validate_candidate(implementation, root)
    validator = _load_validator(implementation.validator_source_sha256, root)

    reports: dict[tuple[str, str], tuple[Mapping[str, Any], RuntimeEvidenceBinding]] = {}
    failures: list[RuntimeDomainFailure] = []
    train_panels: list[ValidatedRuntimePanelInput] = []
    dev_panels, dev_images, dev_report = _load_split_v3(
        dev_binding, implementation, validator, profile, root
    )
    _validate_exchange_profile(dev_panels, dev_images, profile.dev, "validation")
    _add_report(dev_report, dev_binding, reports, failures)
    train_datasets: set[int] = set()
    train_sources: set[str] = set()
    train_panel_ids: set[str] = set()
    train_scene_ids: set[int] = set()
    train_families: list[str] = []

    for train_binding in train_bindings:
        current_train, train_images, train_report = _load_split_v3(
            train_binding, implementation, validator, profile, root
        )
        _validate_exchange_profile(current_train, train_images, profile.train, "train")
        _add_logical_train_inputs(
            current_train,
            dev_panels,
            train_datasets,
            train_sources,
            train_panel_ids,
            train_scene_ids,
        )
        train_panels.extend(current_train)
        train_families.extend(panel.family for panel in current_train)
        _add_report(train_report, train_binding, reports, failures)

    if failures:
        raise RuntimeDomainBindingError(
            "V3 runtime axis evidence contains failed sources or panels",
            failures,
        )
    _validate_total_profile(train_panels, dev_panels, profile)
    _validate_family_disjointness(
        train_families, [panel.family for panel in dev_panels]
    )
    all_panels = (*train_panels, *dev_panels)
    if len({panel.panel_id for panel in all_panels}) != len(all_panels):
        raise RuntimeDomainBindingError("V3 runtime inputs repeat a panel GUID")

    actual_inventory = panel_inventory_sha256(all_panels)
    if actual_inventory != runtime_inputs._sha(
        document["panel_inventory_sha256"], "panel_inventory_sha256"
    ):
        raise RuntimeDomainBindingError("V3 report-derived panel inventory differs from binding")
    for actual, field in (
        (panel_tensor_multiset_sha256(train_panels), "train_tensor_set_sha256"),
        (panel_tensor_multiset_sha256(dev_panels), "dev_tensor_set_sha256"),
    ):
        if actual != runtime_inputs._sha(document[field], field):
            raise RuntimeDomainBindingError(f"V3 {field} differs from the frozen binding")

    axis_model = historical_domain._axis_model(implementation)
    train_domains = tuple(
        _panel_domain_v3(panel, reports, axis_model, expected_sha256.lower())
        for panel in train_panels
    )
    dev_domains = tuple(
        _panel_domain_v3(panel, reports, axis_model, expected_sha256.lower())
        for panel in dev_panels
    )
    if len(reports) != len(train_domains) + len(dev_domains):
        raise RuntimeDomainBindingError("V3 report panel coverage is incomplete or foreign")
    return RuntimeDomainBindingV3(
        train_domains,
        dev_domains,
        implementation,
        profile,
        train_bindings,
        dev_binding,
        expected_sha256.lower(),
        actual_inventory,
        runtime_inputs._sha(document["mapping_audit_sha256"], "mapping_audit_sha256"),
        runtime_inputs._sha(document["truth_mapping_sha256"], "truth_mapping_sha256"),
    )


def join_runtime_domain_truth_v3(
    domains: RuntimeDomainBindingV3,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> RuntimeDomainSceneBinding:
    """Regenerate exact V3 PNGs, then expose and map project-owned truth."""

    if not isinstance(domains, RuntimeDomainBindingV3) or domains.failures:
        raise RuntimeDomainBindingError("V3 truth join requires a complete V3 domain binding")
    root = repository_root.resolve()
    _validate_profile_sources(domains.profile, root)
    _validate_profile_protocol(domains.profile, root)
    _validate_profile_scene_identities(domains.profile)

    train_by_dataset = _group_panels_by_dataset(domains.train)
    rendered_groups: list[
        tuple[str, int, tuple[ValidatedRuntimePanelInput, ...], dict[str, _RenderedSource]]
    ] = []
    for dataset_seed in domains.profile.train.dataset_seeds:
        panels = train_by_dataset.get(dataset_seed)
        if not panels:
            raise RuntimeDomainBindingError("V3 train domain is missing a profiled dataset seed")
        rendered_groups.append(
            (
                "train",
                dataset_seed,
                panels,
                _regenerate_v3("train", dataset_seed, panels, domains.profile.train),
            )
        )
    if set(train_by_dataset) != set(domains.profile.train.dataset_seeds):
        raise RuntimeDomainBindingError("V3 train domain contains a foreign dataset seed")
    dev_by_dataset = _group_panels_by_dataset(domains.dev)
    if set(dev_by_dataset) != set(domains.profile.dev.dataset_seeds):
        raise RuntimeDomainBindingError("V3 development domain dataset identity differs")
    for dataset_seed in domains.profile.dev.dataset_seeds:
        panels = dev_by_dataset[dataset_seed]
        rendered_groups.append(
            (
                "validation",
                dataset_seed,
                panels,
                _regenerate_v3(
                    "validation", dataset_seed, panels, domains.profile.dev
                ),
            )
        )

    # All source bytes and identities have validated before this point.  Only
    # now may the annotations retained in _RenderedSource be consumed.
    train_scenes: list[RuntimeFamilyScene] = []
    dev_scenes: list[RuntimeFamilyScene] = []
    audits = []
    try:
        for split, dataset_seed, panels, rendered in rendered_groups:
            by_source: dict[str, list[ValidatedRuntimePanelInput]] = {}
            for panel in panels:
                by_source.setdefault(panel.source_sha256, []).append(panel)
            joined_by_panel: dict[str, RuntimeFamilyScene] = {}
            for source_sha, rendered_source in rendered.items():
                joined, audit = runtime_family_scenes._join_source(
                    rendered_source, by_source[source_sha], split, dataset_seed
                )
                joined_by_panel.update((scene.panel_id, scene) for scene in joined)
                audits.append(audit)
            ordered = [joined_by_panel[panel.panel_id] for panel in panels]
            (train_scenes if split == "train" else dev_scenes).extend(ordered)
    except (KeyError, runtime_family_scenes.RuntimeFamilySceneError) as exception:
        raise RuntimeDomainBindingError(str(exception)) from exception

    joined = RuntimeFamilySceneJoin(tuple(train_scenes), tuple(dev_scenes), tuple(audits))
    if _marker_truth_count(joined.train) != EXPECTED_TRAIN_MARKER_TRUTH_COUNT:
        raise RuntimeDomainBindingError("V3 train marker truth count differs from profile")
    if _marker_truth_count(joined.dev) != EXPECTED_DEV_MARKER_TRUTH_COUNT:
        raise RuntimeDomainBindingError("V3 development marker truth count differs from profile")
    if runtime_binding.mapping_audit_sha256(joined) != domains.expected_mapping_audit_sha256:
        raise RuntimeDomainBindingError("V3 mapping audit differs from the frozen binding")
    if truth_mapping_sha256(joined) != domains.expected_truth_mapping_sha256:
        raise RuntimeDomainBindingError("V3 truth mapping differs from the frozen binding")
    return RuntimeDomainSceneBinding(
        historical_domain._bind_split(domains.train, joined.train, "train"),
        historical_domain._bind_split(domains.dev, joined.dev, "validation"),
    )


def panel_inventory_sha256(panels: Sequence[ValidatedRuntimePanelInput]) -> str:
    """Hash complete logical panel records while allowing duplicate payloads."""

    records = []
    for panel in panels:
        records.append(
            {
                "split": panel.split,
                "dataset_seed": panel.dataset_seed,
                "family": panel.family,
                "scene_seed": panel.scene_seed,
                "source_sha256": panel.source_sha256,
                "panel_id": panel.panel_id,
                "panel_sha256": panel.panel_sha256,
                "width": panel.width,
                "height": panel.height,
                "crop": panel.crop,
                "requested_crop": panel.requested_crop,
                "gray8_sha256": sha256(panel.gray8.tobytes(order="C")).hexdigest(),
                "ocr_mask_sha256": sha256(panel.ocr_mask.tobytes(order="C")).hexdigest(),
                "geometry_mask_sha256": sha256(
                    panel.geometry_mask.tobytes(order="C")
                ).hexdigest(),
                "artifact_mask_sha256": sha256(
                    panel.artifact_mask.tobytes(order="C")
                ).hexdigest(),
            }
        )
    records.sort(
        key=lambda item: (
            item["split"], item["dataset_seed"], item["scene_seed"], item["panel_id"]
        )
    )
    return sha256(canonical_json_bytes(records)).hexdigest()


def panel_tensor_multiset_sha256(
    panels: Sequence[ValidatedRuntimePanelInput],
) -> str:
    """Hash the tensor multiset, retaining duplicate entries by count."""

    return sha256(
        canonical_json_bytes(sorted(historical_domain._panel_tensor_hashes(panels)))
    ).hexdigest()


def truth_mapping_sha256(joined: RuntimeFamilySceneJoin) -> str:
    records = []
    for scene in (*joined.train, *joined.dev):
        records.append(
            {
                "split": scene.split,
                "dataset_seed": scene.dataset_seed,
                "family": scene.family,
                "seed": scene.seed,
                "source_sha256": scene.source_sha256,
                "panel_id": scene.panel_id,
                "panel_sha256": scene.panel_sha256,
                "crop": scene.crop,
                "requested_crop": scene.requested_crop,
                "centers": scene.centers,
                "diameters": scene.diameters,
                "hard_negatives": scene.hard_negatives,
                "sampling_identity": scene.sampling_identity,
            }
        )
    return sha256(canonical_json_bytes(records)).hexdigest()


def _load_split_v3(
    binding: RuntimeEvidenceBinding,
    implementation: RuntimeImplementationIdentity,
    validator: ModuleType,
    profile: V3GeneratorProfile,
    root: Path,
) -> tuple[
    tuple[ValidatedRuntimePanelInput, ...],
    tuple[Mapping[str, Any], ...],
    Mapping[str, Any],
]:
    manifest_path, manifest_payload = runtime_inputs._read_bound_artifact(
        binding.manifest_path, binding.manifest_sha256, root, f"{binding.split} V3 manifest"
    )
    report_path, report_payload = runtime_inputs._read_bound_artifact(
        binding.report_path, binding.report_sha256, root, f"{binding.split} V3 report"
    )
    manifest = _json_object(manifest_payload, f"{binding.split} V3 manifest")
    report = _json_object(report_payload, f"{binding.split} V3 report")
    try:
        validator.REPOSITORY_ROOT = root
        split, dataset_seed, images = _validate_manifest_v3(
            manifest, manifest_path, validator
        )
        if split != binding.split:
            raise RuntimeDomainBindingError("V3 manifest split differs from its binding")
        if report.get("schema") != runtime_inputs.REPORT_SCHEMA:
            raise RuntimeDomainBindingError("V3 loader accepts only panel-aware v2 reports")
        case_map = validator._validate_report(
            report,
            sha256(manifest_payload).hexdigest(),
            images,
            report_path=report_path,
            manifest_path=manifest_path,
        )
    except RuntimeDomainBindingError:
        raise
    except Exception as exception:
        if isinstance(exception, getattr(validator, "EvidenceError")):
            raise RuntimeDomainBindingError(str(exception)) from exception
        raise

    runtime_inputs._validate_complete_diagnostic(report, len(images))
    runtime_inputs._validate_report_implementation(report, implementation)
    _regenerate_v3_records(split, dataset_seed, images, profile)
    panels: list[ValidatedRuntimePanelInput] = []
    for image in images:
        case = case_map[str(image["image_sha256"])]
        for panel in case["panels"]:
            panels.append(
                runtime_inputs._load_panel(
                    split,
                    dataset_seed,
                    image,
                    panel,
                    report_path,
                    implementation,
                    validator,
                    root,
                )
            )
    if not panels:
        raise RuntimeDomainBindingError("V3 runtime split has no complete panel inputs")
    return tuple(panels), tuple(images), report


def _validate_manifest_v3(
    manifest: Mapping[str, Any],
    manifest_path: Path,
    validator: ModuleType,
) -> tuple[str, int, list[dict[str, Any]]]:
    validator._require_exact_keys(manifest, validator.MANIFEST_KEYS, "V3 input manifest")
    split = manifest.get("split")
    if (
        manifest.get("schema") != validator.MANIFEST_SCHEMA
        or manifest.get("source") != EXPECTED_MANIFEST_SOURCE
        or manifest.get("preset") != "smoke"
        or split not in {"train", "validation"}
        or manifest.get("contains_truth") is not False
        or manifest.get("contains_precomputed_masks") is not False
    ):
        raise RuntimeDomainBindingError("V3 manifest has a foreign source or evidence scope")
    dataset_seed = validator._require_int(manifest.get("seed"), "V3 manifest seed")
    raw_images = manifest.get("images")
    if not isinstance(raw_images, list) or not raw_images:
        raise RuntimeDomainBindingError("V3 manifest images must be a nonempty array")

    images: list[dict[str, Any]] = []
    names: set[str] = set()
    hashes: set[str] = set()
    seeds: set[int] = set()
    for index, raw in enumerate(raw_images):
        if not isinstance(raw, dict):
            raise RuntimeDomainBindingError(f"V3 manifest image {index} must be an object")
        validator._require_exact_keys(raw, validator.IMAGE_KEYS, f"V3 manifest image {index}")
        name = raw.get("image")
        if not isinstance(name, str) or Path(name).name != name or Path(name).suffix.casefold() != ".png":
            raise RuntimeDomainBindingError("V3 manifest image must be a local PNG basename")
        image_hash = validator._require_sha256(
            raw.get("image_sha256"), f"V3 manifest image {index} SHA-256"
        )
        image_seed = validator._require_int(raw.get("seed"), f"V3 manifest image {index} seed")
        width = validator._require_int(raw.get("width"), f"V3 manifest image {index} width", minimum=1)
        height = validator._require_int(raw.get("height"), f"V3 manifest image {index} height", minimum=1)
        if raw.get("split") != split:
            raise RuntimeDomainBindingError("V3 manifest image split differs from manifest")
        if name.casefold() in names or image_hash in hashes or image_seed in seeds:
            raise RuntimeDomainBindingError("V3 manifest repeats image name, source hash, or scene seed")
        image_path = manifest_path.parent / name
        if not image_path.is_file() or sha256(image_path.read_bytes()).hexdigest() != image_hash:
            raise RuntimeDomainBindingError("V3 source PNG bytes differ from manifest")
        try:
            with Image.open(image_path) as image:
                if image.format != "PNG" or image.size != (width, height):
                    raise RuntimeDomainBindingError("V3 source PNG dimensions differ from manifest")
                image.load()
        except (OSError, ValueError) as exception:
            raise RuntimeDomainBindingError("V3 source PNG cannot be decoded") from exception
        names.add(name.casefold())
        hashes.add(image_hash)
        seeds.add(image_seed)
        images.append(
            {
                "image": name,
                "image_sha256": image_hash,
                "width": width,
                "height": height,
                "split": split,
                "family": str(raw.get("family")),
                "seed": image_seed,
            }
        )
    return str(split), dataset_seed, images


def _regenerate_v3_records(
    split: str,
    dataset_seed: int,
    images: Sequence[Mapping[str, Any]],
    profile: V3GeneratorProfile,
) -> dict[str, _RenderedSource]:
    expected = profile.train if split == "train" else profile.dev
    if dataset_seed not in expected.dataset_seeds:
        raise RuntimeDomainBindingError("V3 manifest uses a dataset seed outside its profile")
    scenes = _build_scenes(PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True)
    selected = [scene for scene in scenes if _scene_split(scene) == split]
    by_seed = {int(scene["seed"]): scene for scene in selected}
    image_seeds = {int(image["seed"]) for image in images}
    allowed_for_dataset = {
        seed for seed in expected.scene_seeds if seed // 100 == dataset_seed
    }
    if len(by_seed) != len(selected) or image_seeds != allowed_for_dataset:
        raise RuntimeDomainBindingError("V3 regenerated scene identities differ from manifest")

    regenerated: dict[str, _RenderedSource] = {}
    for image in images:
        scene = by_seed[int(image["seed"])]
        family = _family_identity(scene)
        if family != image["family"]:
            raise RuntimeDomainBindingError("V3 regenerated family differs from manifest")
        rgb, annotation = _render_v3_source(scene)
        payload = synthetic_png_bytes(rgb)
        source_sha = sha256(payload).hexdigest()
        expected_name = f"{split}-{int(scene['seed'])}-{source_sha[:12]}.png"
        if (
            source_sha != image["image_sha256"]
            or expected_name != image["image"]
            or rgb.size != (image["width"], image["height"])
        ):
            raise RuntimeDomainBindingError(
                f"V3 regenerated PNG identity differs for seed {scene['seed']}"
            )
        if source_sha in regenerated:
            raise RuntimeDomainBindingError("V3 scenes produced duplicate source identities")
        regenerated[source_sha] = _RenderedSource(
            scene, annotation, source_sha, rgb.width, rgb.height
        )
    return regenerated


def _regenerate_v3(
    split: str,
    dataset_seed: int,
    panels: tuple[ValidatedRuntimePanelInput, ...],
    profile: V3SplitProfile,
) -> dict[str, _RenderedSource]:
    images_by_source: dict[str, dict[str, Any]] = {}
    for panel in panels:
        record = images_by_source.setdefault(
            panel.source_sha256,
            {
                "image": f"{split}-{panel.scene_seed}-{panel.source_sha256[:12]}.png",
                "image_sha256": panel.source_sha256,
                "width": max(item.crop[0] + item.crop[2] for item in panels if item.source_sha256 == panel.source_sha256),
                "height": max(item.crop[1] + item.crop[3] for item in panels if item.source_sha256 == panel.source_sha256),
                "split": split,
                "family": panel.family,
                "seed": panel.scene_seed,
            },
        )
        if record["family"] != panel.family or record["seed"] != panel.scene_seed:
            raise RuntimeDomainBindingError("V3 panels disagree on their source identity")
    # Regenerate directly so full source dimensions are authoritative rather
    # than inferred from crop coverage.  The initial records are identity keys.
    scenes = _build_scenes(PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True)
    selected_scenes = [scene for scene in scenes if _scene_split(scene) == split]
    # The full profiled identity was already checked during annotation-free
    # loading.  Here the exact source bytes and semantic identities are checked
    # again before annotations become readable.
    selected = {int(scene["seed"]): scene for scene in selected_scenes}
    regenerated: dict[str, _RenderedSource] = {}
    expected_sources = set(images_by_source)
    for source_sha, record in images_by_source.items():
        scene = selected.get(int(record["seed"]))
        if scene is None or _family_identity(scene) != record["family"]:
            raise RuntimeDomainBindingError("V3 truth regeneration changed scene identity")
        rgb, annotation = _render_v3_source(scene)
        actual_sha = sha256(synthetic_png_bytes(rgb)).hexdigest()
        if actual_sha != source_sha:
            raise RuntimeDomainBindingError("V3 truth regeneration changed source PNG bytes")
        regenerated[source_sha] = _RenderedSource(
            scene, annotation, source_sha, rgb.width, rgb.height
        )
    if set(regenerated) != expected_sources:
        raise RuntimeDomainBindingError("V3 truth regeneration source coverage differs")
    for panel in panels:
        rendered = regenerated[panel.source_sha256]
        if panel.crop[0] + panel.crop[2] > rendered.width or panel.crop[1] + panel.crop[3] > rendered.height:
            raise RuntimeDomainBindingError("V3 panel crop exceeds regenerated source dimensions")
    return regenerated


def _panel_domain_v3(
    panel: ValidatedRuntimePanelInput,
    reports: Mapping[tuple[str, str], tuple[Mapping[str, Any], RuntimeEvidenceBinding]],
    axis_model,
    binding_sha256: str,
) -> RuntimePanelDomain:
    key = (panel.source_sha256, panel.panel_id)
    if key not in reports:
        raise RuntimeDomainBindingError("V3 runtime panel has no exact axis report")
    record, binding = reports[key]
    base = historical_domain._panel_domain_from_report(
        panel, record["case"], record["panel"], binding, axis_model
    )
    identity = sha256(
        canonical_json_bytes(
            {
                "schema": "graphreader.marker-plot-domain.v3",
                "binding_sha256": binding_sha256,
                "historical_validated_evidence_sha256": base.evidence_sha256,
                "source_sha256": panel.source_sha256,
                "panel_id": panel.panel_id,
                "panel_sha256": panel.panel_sha256,
                "crop": panel.crop,
                "polygon": base.domain.polygon,
            }
        )
    ).hexdigest()
    try:
        domain = PlotDomain.runtime_plot(
            panel.width, panel.height, base.domain.polygon, identity=identity
        )
    except ValueError as exception:
        raise RuntimeDomainBindingError(str(exception)) from exception
    return RuntimePanelDomain(
        base.runtime_input,
        domain,
        base.source_width,
        base.source_height,
        base.source_polygon,
        base.source_to_panel_matrix,
        base.panel_to_source_matrix,
        base.manifest_sha256,
        base.report_sha256,
        base.axis_model,
        identity,
    )


def _render_v3_source(scene: Mapping[str, Any]) -> tuple[Image.Image, Mapping[str, Any]]:
    """One narrow seam for the source-only V3 renderer.

    The current implementation is replaced by the reviewed source-only core as
    soon as that generator API freezes.  Keeping it here prevents validation,
    domain, or join code from depending on element-audit state.
    """

    if visible_content_v3.GENERATOR_VERSION != EXPECTED_GENERATOR_VERSION:
        raise RuntimeDomainBindingError("V3 renderer returned a foreign generator version")
    result = visible_content_v3.render_visible_content_source(scene)
    return result.image.convert("RGB"), result.annotation


def _parse_profile(value: Any, root: Path) -> V3GeneratorProfile:
    record = runtime_binding._object(value, _PROFILE_FIELDS, "V3 generator profile")
    if record["generator_version"] != EXPECTED_GENERATOR_VERSION:
        raise RuntimeDomainBindingError("V3 generator profile has a foreign version")
    protocol = runtime_binding._object(
        record["protocol"], _PROTOCOL_REFERENCE_FIELDS, "V3 generator protocol"
    )
    protocol_path = runtime_binding._relative_path(protocol["path"], root, "V3 protocol")
    sources = runtime_binding._records(
        RuntimeSourceIdentity, record["sources"], "V3 generator source"
    )
    parsed_sources = tuple(
        RuntimeSourceIdentity(
            runtime_binding._relative_path(item.relative_path, root, "V3 source").relative_to(root),
            runtime_inputs._sha(item.sha256, "V3 source SHA-256"),
        )
        for item in sources
    )
    return V3GeneratorProfile(
        record["generator_version"],
        protocol_path,
        runtime_inputs._sha(protocol["sha256"], "V3 protocol SHA-256"),
        parsed_sources,
        _parse_split_profile(record["train"], "train"),
        _parse_split_profile(record["dev"], "development"),
    )


def _parse_split_profile(value: Any, label: str) -> V3SplitProfile:
    record = runtime_binding._object(value, _SPLIT_PROFILE_FIELDS, f"V3 {label} profile")
    dataset_seeds = _int_tuple(record["dataset_seeds"], f"V3 {label} dataset seeds")
    scene_seeds = _int_tuple(record["scene_seeds"], f"V3 {label} scene seeds")
    source_count = _positive_int(record["source_count"], f"V3 {label} source count")
    truth_count = _positive_int(
        record["marker_truth_count"], f"V3 {label} marker truth count"
    )
    if len(scene_seeds) != source_count or len(set(scene_seeds)) != len(scene_seeds):
        raise RuntimeDomainBindingError(f"V3 {label} scene identities are incomplete")
    if len(set(dataset_seeds)) != len(dataset_seeds):
        raise RuntimeDomainBindingError(f"V3 {label} dataset identities repeat")
    return V3SplitProfile(
        dataset_seeds,
        scene_seeds,
        source_count,
        truth_count,
        runtime_inputs._sha(
            record["resolved_scene_identity_set_sha256"],
            f"V3 {label} scene identity set SHA-256",
        ),
    )


def _validate_profile_sources(profile: V3GeneratorProfile, root: Path) -> None:
    expected_protocol = (root / GENERATOR_SOURCE_PATHS[1]).resolve()
    if profile.protocol_path != expected_protocol:
        raise RuntimeDomainBindingError("V3 profile protocol path differs from the fixed source list")
    if tuple(item.relative_path for item in profile.sources) != GENERATOR_SOURCE_PATHS:
        raise RuntimeDomainBindingError("V3 profile must name the exact generator source list")
    for item in profile.sources:
        path = (root / item.relative_path).resolve()
        if root not in path.parents or not path.is_file():
            raise RuntimeDomainBindingError(f"V3 source is missing: {item.relative_path.as_posix()}")
        if sha256(path.read_bytes()).hexdigest() != item.sha256:
            raise RuntimeDomainBindingError(
                f"V3 source differs from profile: {item.relative_path.as_posix()}"
            )


def _validate_profile_protocol(profile: V3GeneratorProfile, root: Path) -> None:
    protocol_payload = profile.protocol_path.read_bytes()
    if sha256(protocol_payload).hexdigest() != profile.protocol_sha256:
        raise RuntimeDomainBindingError("V3 protocol bytes differ from profile")
    protocol = _json_object(protocol_payload, "V3 generator protocol")
    if (
        protocol.get("schema") != PROTOCOL_SCHEMA
        or protocol.get("generator_version") != EXPECTED_GENERATOR_VERSION
    ):
        raise RuntimeDomainBindingError("V3 protocol has a foreign schema or generator version")
    implementation = protocol.get("implementation")
    axis_v2 = protocol.get("axis_v2")
    if not isinstance(implementation, dict) or not isinstance(axis_v2, dict):
        raise RuntimeDomainBindingError("V3 protocol source identities are incomplete")
    expected_source_map = {item.relative_path.as_posix(): item.sha256 for item in profile.sources}
    references = (
        (implementation.get("path"), implementation.get("sha256")),
        (axis_v2.get("path"), axis_v2.get("sha256")),
        (axis_v2.get("protocol_path"), axis_v2.get("protocol_sha256")),
    )
    for path_value, hash_value in references:
        if expected_source_map.get(path_value) != hash_value:
            raise RuntimeDomainBindingError("V3 protocol source identity differs from profile")
    fixed = protocol.get("fixed_splits")
    budget = protocol.get("budget")
    if not isinstance(fixed, dict) or not isinstance(budget, dict):
        raise RuntimeDomainBindingError("V3 protocol split or budget is missing")
    if (
        budget.get("optimizer_steps_authorized") != 0
        or budget.get("private_reads") != 0
        or budget.get("sealed_runs") != 0
        or budget.get("production_approval") is not False
    ):
        raise RuntimeDomainBindingError("V3 protocol grants a forbidden evidence permission")
    _validate_protocol_split(fixed.get("train"), profile.train, "train")
    _validate_protocol_split(fixed.get("dev"), profile.dev, "development")


def _validate_profile_scene_identities(profile: V3GeneratorProfile) -> None:
    for split, split_profile in (("train", profile.train), ("validation", profile.dev)):
        rows: list[dict[str, Any]] = []
        for dataset_seed in split_profile.dataset_seeds:
            selected = [
                scene
                for scene in _build_scenes(
                    PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True
                )
                if _scene_split(scene) == split
            ]
            for scene in selected:
                rows.append(
                    {
                        "dataset_seed": dataset_seed,
                        "scene_seed": scene["seed"],
                        "scene_id": scene["scene_id"],
                        "resolved_scene_sha256": sha256(
                            synthetic_canonical_json_bytes(scene)
                        ).hexdigest(),
                    }
                )
        if tuple(int(item["scene_seed"]) for item in rows) != split_profile.scene_seeds:
            raise RuntimeDomainBindingError("V3 resolved scene order differs from profile")
        actual = sha256(synthetic_canonical_json_bytes(rows)).hexdigest()
        if actual != split_profile.resolved_scene_identity_set_sha256:
            raise RuntimeDomainBindingError("V3 resolved scene identities differ from profile")


def _validate_protocol_split(value: Any, expected: V3SplitProfile, label: str) -> None:
    if not isinstance(value, dict):
        raise RuntimeDomainBindingError(f"V3 protocol {label} split is missing")
    observed = {
        "dataset_seeds": tuple(value.get("dataset_seeds", ())),
        "scene_seeds": tuple(value.get("scene_seeds", ())),
        "source_count": value.get("scene_count"),
        "marker_truth_count": value.get("marker_truth_count"),
        "resolved_scene_identity_set_sha256": value.get("resolved_scene_identity_set_sha256"),
    }
    if observed != asdict(expected):
        raise RuntimeDomainBindingError(f"V3 protocol {label} split differs from profile")


def _validate_helper_sources(document: Mapping[str, Any]) -> None:
    for module, field in (
        (gate_seal, "canonical_json_source_sha256"),
        (runtime_binding, "runtime_binding_source_sha256"),
        (runtime_inputs, "runtime_loader_source_sha256"),
        (runtime_family_scenes, "runtime_join_source_sha256"),
        (historical_domain, "historical_domain_source_sha256"),
        (proposal_domain, "proposal_domain_source_sha256"),
    ):
        actual = sha256(Path(module.__file__).read_bytes()).hexdigest()
        if actual != runtime_inputs._sha(document[field], field):
            raise RuntimeDomainBindingError(f"{field} differs from executing source")
    actual_self = sha256(Path(__file__).read_bytes()).hexdigest()
    if actual_self != runtime_inputs._sha(document["binding_v3_source_sha256"], "binding_v3_source_sha256"):
        raise RuntimeDomainBindingError("binding_v3_source_sha256 differs from executing source")


def _load_validator(expected_sha256: str, root: Path) -> ModuleType:
    path = (root / runtime_inputs.VALIDATOR_RELATIVE_PATH).resolve()
    if root not in path.parents or not path.is_file():
        raise RuntimeDomainBindingError("V3 shared report validator is missing")
    payload = path.read_bytes()
    if sha256(payload).hexdigest() != runtime_inputs._sha(expected_sha256, "validator source"):
        raise RuntimeDomainBindingError("V3 shared report validator source differs")
    module = ModuleType("_graphreader_bound_v3_runtime_evidence_validator")
    module.__file__ = str(path)
    exec(compile(payload, str(path), "exec"), module.__dict__)
    return module


def _add_report(
    report: Mapping[str, Any],
    binding: RuntimeEvidenceBinding,
    reports: dict[tuple[str, str], tuple[Mapping[str, Any], RuntimeEvidenceBinding]],
    failures: list[RuntimeDomainFailure],
) -> None:
    failures.extend(historical_domain._collect_failures(binding.split, report))
    for source_sha, panel_id, panel, case in historical_domain._report_panels(report):
        key = (source_sha, panel_id)
        if key in reports:
            raise RuntimeDomainBindingError("V3 reports repeat a panel identity")
        reports[key] = ({"panel": panel, "case": case}, binding)


def _add_logical_train_inputs(
    train: tuple[ValidatedRuntimePanelInput, ...],
    dev: tuple[ValidatedRuntimePanelInput, ...],
    datasets: set[int],
    sources: set[str],
    panels: set[str],
    scenes: set[int],
) -> None:
    current_datasets = {panel.dataset_seed for panel in train}
    current_sources = {panel.source_sha256 for panel in train}
    current_panels = {panel.panel_id for panel in train}
    current_scenes = {panel.scene_seed for panel in train}
    if len(current_datasets) != 1 or datasets & current_datasets:
        raise RuntimeDomainBindingError("V3 train exchanges repeat a dataset identity")
    if sources & current_sources or panels & current_panels or scenes & current_scenes:
        raise RuntimeDomainBindingError("V3 train exchanges repeat source, panel, or scene identities")
    if current_sources & {panel.source_sha256 for panel in dev}:
        raise RuntimeDomainBindingError("V3 train and development source identities overlap")
    if current_panels & {panel.panel_id for panel in dev}:
        raise RuntimeDomainBindingError("V3 train and development panel identities overlap")
    if current_scenes & {panel.scene_seed for panel in dev}:
        raise RuntimeDomainBindingError("V3 train and development scene identities overlap")
    datasets.update(current_datasets)
    sources.update(current_sources)
    panels.update(current_panels)
    scenes.update(current_scenes)


def _validate_exchange_profile(
    panels: tuple[ValidatedRuntimePanelInput, ...],
    images: tuple[Mapping[str, Any], ...],
    profile: V3SplitProfile,
    label: str,
) -> None:
    if not panels or not images:
        raise RuntimeDomainBindingError(f"V3 {label} exchange is empty")
    seeds = {int(image["seed"]) for image in images}
    if not seeds <= set(profile.scene_seeds):
        raise RuntimeDomainBindingError(f"V3 {label} exchange contains a foreign scene")
    if len({str(image["image_sha256"]) for image in images}) != len(images):
        raise RuntimeDomainBindingError(f"V3 {label} exchange repeats a source identity")


def _validate_total_profile(
    train: Sequence[ValidatedRuntimePanelInput],
    dev: Sequence[ValidatedRuntimePanelInput],
    profile: V3GeneratorProfile,
) -> None:
    if (
        profile.train.source_count != EXPECTED_TRAIN_SOURCE_COUNT
        or profile.dev.source_count != EXPECTED_DEV_SOURCE_COUNT
        or profile.train.marker_truth_count != EXPECTED_TRAIN_MARKER_TRUTH_COUNT
        or profile.dev.marker_truth_count != EXPECTED_DEV_MARKER_TRUTH_COUNT
    ):
        raise RuntimeDomainBindingError("V3 profile changes fixed source or truth counts")
    train_sources = {panel.source_sha256 for panel in train}
    dev_sources = {panel.source_sha256 for panel in dev}
    train_scenes = {panel.scene_seed for panel in train}
    dev_scenes = {panel.scene_seed for panel in dev}
    if (
        len(train_sources) != profile.train.source_count
        or len(dev_sources) != profile.dev.source_count
        or train_scenes != set(profile.train.scene_seeds)
        or dev_scenes != set(profile.dev.scene_seeds)
    ):
        raise RuntimeDomainBindingError("V3 source inventory differs from fixed profile")
    if train_sources & dev_sources or train_scenes & dev_scenes:
        raise RuntimeDomainBindingError("V3 train and development logical identities overlap")


def _group_panels_by_dataset(
    domains: Sequence[RuntimePanelDomain],
) -> dict[int, tuple[ValidatedRuntimePanelInput, ...]]:
    groups: dict[int, list[ValidatedRuntimePanelInput]] = {}
    for domain in domains:
        panel = domain.runtime_input
        groups.setdefault(panel.dataset_seed, []).append(panel)
    return {key: tuple(value) for key, value in groups.items()}


def _validate_family_disjointness(train: Sequence[str], dev: Sequence[str]) -> None:
    train_axes = {axis: set() for axis in FAMILY_AXES}
    dev_axes = {axis: set() for axis in FAMILY_AXES}
    for target, values in ((train_axes, train), (dev_axes, dev)):
        for value in values:
            parsed = _parse_family_identity(value)
            for axis, family in parsed.items():
                target[axis].add(family)
    for axis in FAMILY_AXES:
        overlap = train_axes[axis] & dev_axes[axis]
        if overlap:
            raise RuntimeDomainBindingError(
                f"V3 train and development {axis} family identities overlap"
            )


def _parse_family_identity(value: Any) -> dict[str, str]:
    if not isinstance(value, str):
        raise RuntimeDomainBindingError("V3 runtime family identity must be a string")
    parts = value.split("|")
    if len(parts) != len(FAMILY_AXES):
        raise RuntimeDomainBindingError("V3 runtime family identity has invalid axes")
    parsed: dict[str, str] = {}
    for expected_axis, part in zip(FAMILY_AXES, parts, strict=True):
        axis, separator, family = part.partition("=")
        if separator != "=" or axis != expected_axis or not family or "=" in family:
            raise RuntimeDomainBindingError("V3 runtime family identity has invalid axes")
        parsed[axis] = family
    return parsed


def _family_identity(scene: Mapping[str, Any]) -> str:
    values = _family_keys(scene)
    return "|".join(f"{axis}={values[axis]}" for axis in FAMILY_AXES)


def _marker_truth_count(scenes: Sequence[RuntimeFamilyScene]) -> int:
    return sum(len(scene.centers) for scene in scenes)


def _int_tuple(value: Any, label: str) -> tuple[int, ...]:
    if not isinstance(value, list) or not value or any(type(item) is not int for item in value):
        raise RuntimeDomainBindingError(f"{label} must be a nonempty integer array")
    return tuple(value)


def _positive_int(value: Any, label: str) -> int:
    if type(value) is not int or value <= 0:
        raise RuntimeDomainBindingError(f"{label} must be a positive integer")
    return value


def _json_object(payload: bytes, label: str) -> dict[str, Any]:
    try:
        value = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise RuntimeDomainBindingError(f"{label} is not valid JSON") from exception
    if not isinstance(value, dict):
        raise RuntimeDomainBindingError(f"{label} must be an object")
    return value


__all__ = [
    "BINDING_SCHEMA",
    "DomainBoundRuntimeScene",
    "GENERATOR_SOURCE_PATHS",
    "RuntimeDomainBindingError",
    "RuntimeDomainBindingV3",
    "RuntimeDomainSceneBinding",
    "RuntimePanelDomain",
    "V3GeneratorProfile",
    "V3SplitProfile",
    "join_runtime_domain_truth_v3",
    "load_runtime_domain_binding_v3",
    "panel_inventory_sha256",
    "panel_tensor_multiset_sha256",
    "truth_mapping_sha256",
]
