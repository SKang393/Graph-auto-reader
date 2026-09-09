# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from dataclasses import dataclass
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
from PIL import Image
import pytest

from ml.markers.gate_seal import canonical_json_bytes
from ml.markers.center.mask_preserving_v24 import runtime_inputs
from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    RuntimeEvidenceBinding,
    RuntimeModelIdentity,
    RuntimeSourceIdentity,
    ValidatedRuntimePanelInput,
)
from ml.markers.center.plot_domain_v25 import runtime_domain_binding_v3 as subject
from ml.markers.center.plot_domain_v25.runtime_domain_binding_v3 import (
    EXPECTED_DEV_MARKER_TRUTH_COUNT,
    EXPECTED_DEV_SOURCE_COUNT,
    EXPECTED_GENERATOR_VERSION,
    EXPECTED_TRAIN_MARKER_TRUTH_COUNT,
    EXPECTED_TRAIN_SOURCE_COUNT,
    RuntimeDomainBindingError,
    V3GeneratorProfile,
    V3SplitProfile,
    _add_logical_train_inputs,
    _panel_domain_v3,
    _regenerate_v3_records,
    _validate_manifest_v3,
    _validate_profile_protocol,
    _validate_profile_scene_identities,
    _validate_profile_sources,
    panel_inventory_sha256,
    panel_tensor_multiset_sha256,
)
from ml.synthetic.io import (
    canonical_json_bytes as synthetic_canonical_json_bytes,
    png_bytes as synthetic_png_bytes,
)


SOURCE_SHA = "1" * 64
PANEL_SHA = "2" * 64
FAMILY = "renderer=a|font=b|degradation=c|template=d|marker=e"


def _panel(
    *,
    split: str = "train",
    dataset_seed: int = 393,
    scene_seed: int = 39300,
    source_sha: str = SOURCE_SHA,
    panel_id: str = "00000000-0000-0000-0000-000000000001",
) -> ValidatedRuntimePanelInput:
    shape = (3, 4)
    return ValidatedRuntimePanelInput(
        split=split,
        dataset_seed=dataset_seed,
        family=FAMILY,
        scene_seed=scene_seed,
        source_sha256=source_sha,
        panel_id=panel_id,
        panel_sha256=PANEL_SHA,
        width=4,
        height=3,
        crop=(0, 0, 4, 3),
        requested_crop=(0.0, 0.0, 4.0, 3.0),
        gray8=np.zeros(shape, dtype=np.uint8),
        ocr_mask=np.zeros(shape, dtype=np.float32),
        geometry_mask=np.zeros(shape, dtype=np.float32),
        artifact_mask=np.zeros(shape, dtype=np.float32),
    )


def _png(image: Image.Image) -> bytes:
    stream = BytesIO()
    image.convert("RGB").save(stream, format="PNG")
    return stream.getvalue()


def _profile(root: Path) -> V3GeneratorProfile:
    sources = tuple(
        RuntimeSourceIdentity(path, sha256((root / path).read_bytes()).hexdigest())
        for path in subject.GENERATOR_SOURCE_PATHS
    )
    return V3GeneratorProfile(
        EXPECTED_GENERATOR_VERSION,
        root / subject.GENERATOR_SOURCE_PATHS[1],
        sha256((root / subject.GENERATOR_SOURCE_PATHS[1]).read_bytes()).hexdigest(),
        sources,
        V3SplitProfile(
            (393, 394, 395, 396, 397),
            tuple(range(20)),
            EXPECTED_TRAIN_SOURCE_COUNT,
            EXPECTED_TRAIN_MARKER_TRUTH_COUNT,
            "3" * 64,
        ),
        V3SplitProfile(
            (393,),
            (20, 21, 22),
            EXPECTED_DEV_SOURCE_COUNT,
            EXPECTED_DEV_MARKER_TRUTH_COUNT,
            "4" * 64,
        ),
    )


def _write_profile_sources(root: Path) -> V3GeneratorProfile:
    for index, relative in enumerate(subject.GENERATOR_SOURCE_PATHS):
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(f"source-{index}".encode())
    source_hashes = {
        path.as_posix(): sha256((root / path).read_bytes()).hexdigest()
        for path in subject.GENERATOR_SOURCE_PATHS
    }
    protocol = {
        "schema": subject.PROTOCOL_SCHEMA,
        "generator_version": EXPECTED_GENERATOR_VERSION,
        "implementation": {
            "path": subject.GENERATOR_SOURCE_PATHS[0].as_posix(),
            "sha256": source_hashes[subject.GENERATOR_SOURCE_PATHS[0].as_posix()],
        },
        "axis_v2": {
            "path": subject.GENERATOR_SOURCE_PATHS[2].as_posix(),
            "sha256": source_hashes[subject.GENERATOR_SOURCE_PATHS[2].as_posix()],
            "protocol_path": subject.GENERATOR_SOURCE_PATHS[3].as_posix(),
            "protocol_sha256": source_hashes[subject.GENERATOR_SOURCE_PATHS[3].as_posix()],
        },
        "fixed_splits": {
            "train": {
                "dataset_seeds": [393, 394, 395, 396, 397],
                "scene_seeds": list(range(20)),
                "scene_count": 20,
                "marker_truth_count": 500,
                "resolved_scene_identity_set_sha256": "3" * 64,
            },
            "dev": {
                "dataset_seeds": [393],
                "scene_seeds": [20, 21, 22],
                "scene_count": 3,
                "marker_truth_count": 206,
                "resolved_scene_identity_set_sha256": "4" * 64,
            },
        },
        "budget": {
            "optimizer_steps_authorized": 0,
            "private_reads": 0,
            "sealed_runs": 0,
            "production_approval": False,
        },
    }
    protocol_path = root / subject.GENERATOR_SOURCE_PATHS[1]
    protocol_path.write_bytes(canonical_json_bytes(protocol))
    return _profile(root)


def test_logical_identity_guards_allow_identical_crop_and_tensor_payloads() -> None:
    first = _panel()
    second = _panel(
        dataset_seed=394,
        scene_seed=39400,
        source_sha="5" * 64,
        panel_id="00000000-0000-0000-0000-000000000002",
    )
    dev = _panel(
        split="validation",
        scene_seed=39304,
        source_sha="6" * 64,
        panel_id="00000000-0000-0000-0000-000000000003",
    )
    state = (set(), set(), set(), set())

    _add_logical_train_inputs((first,), (dev,), *state)
    _add_logical_train_inputs((second,), (dev,), *state)

    assert first.panel_sha256 == second.panel_sha256
    assert panel_tensor_multiset_sha256((first, second)) == panel_tensor_multiset_sha256(
        (second, first)
    )
    assert panel_inventory_sha256((first, second)) != panel_inventory_sha256((first, first))


@pytest.mark.parametrize("field", ["source_sha256", "panel_id", "scene_seed"])
def test_logical_identity_guards_reject_reused_source_panel_or_scene(field: str) -> None:
    first = _panel()
    changes = {
        "dataset_seed": 394,
        "scene_seed": 39400,
        "source_sha256": "5" * 64,
        "panel_id": "00000000-0000-0000-0000-000000000002",
    }
    changes[field] = getattr(first, field)
    second = ValidatedRuntimePanelInput(**{**first.__dict__, **changes})
    dev = _panel(
        split="validation",
        scene_seed=39304,
        source_sha="6" * 64,
        panel_id="00000000-0000-0000-0000-000000000003",
    )
    state = (set(), set(), set(), set())
    _add_logical_train_inputs((first,), (dev,), *state)

    with pytest.raises(RuntimeDomainBindingError, match="repeat source, panel, or scene"):
        _add_logical_train_inputs((second,), (dev,), *state)


def test_profile_authenticates_exact_source_list_protocol_and_permissions(tmp_path: Path) -> None:
    profile = _write_profile_sources(tmp_path)

    _validate_profile_sources(profile, tmp_path)
    _validate_profile_protocol(profile, tmp_path)

    (tmp_path / subject.GENERATOR_SOURCE_PATHS[-1]).write_bytes(b"changed")
    with pytest.raises(RuntimeDomainBindingError, match="differs from profile"):
        _validate_profile_sources(profile, tmp_path)


def test_profile_rejects_protocol_coverage_substitution(tmp_path: Path) -> None:
    profile = _write_profile_sources(tmp_path)
    protocol_path = profile.protocol_path
    protocol = json.loads(protocol_path.read_bytes())
    protocol["fixed_splits"]["train"]["marker_truth_count"] = 499
    protocol_path.write_bytes(canonical_json_bytes(protocol))
    changed = V3GeneratorProfile(
        profile.generator_version,
        profile.protocol_path,
        sha256(protocol_path.read_bytes()).hexdigest(),
        tuple(
            RuntimeSourceIdentity(
                item.relative_path,
                sha256((tmp_path / item.relative_path).read_bytes()).hexdigest(),
            )
            for item in profile.sources
        ),
        profile.train,
        profile.dev,
    )

    with pytest.raises(RuntimeDomainBindingError, match="train split differs"):
        _validate_profile_protocol(changed, tmp_path)


def test_resolved_scene_hash_is_recomputed_not_trusted(monkeypatch: pytest.MonkeyPatch) -> None:
    scenes = [
        {"seed": 10, "scene_id": "a", "payload": 1, "split": "train"},
        {"seed": 11, "scene_id": "b", "payload": 2, "split": "train"},
    ]
    rows = [
        {
            "dataset_seed": 7,
            "scene_seed": item["seed"],
            "scene_id": item["scene_id"],
            "resolved_scene_sha256": sha256(synthetic_canonical_json_bytes(item)).hexdigest(),
        }
        for item in scenes
    ]
    identity = sha256(synthetic_canonical_json_bytes(rows)).hexdigest()
    profile = V3GeneratorProfile(
        EXPECTED_GENERATOR_VERSION,
        Path("protocol"),
        "1" * 64,
        (),
        V3SplitProfile((7,), (10, 11), 2, 1, identity),
        V3SplitProfile((7,), (20,), 1, 1, "2" * 64),
    )
    monkeypatch.setattr(subject, "_build_scenes", lambda *_args, **_kwargs: scenes)
    monkeypatch.setattr(subject, "_scene_split", lambda scene: scene["split"])

    with pytest.raises(RuntimeDomainBindingError, match="resolved scene order"):
        _validate_profile_scene_identities(profile)

    profile = V3GeneratorProfile(
        profile.generator_version,
        profile.protocol_path,
        profile.protocol_sha256,
        profile.sources,
        V3SplitProfile((7,), (10, 11), 2, 1, "9" * 64),
        profile.dev,
    )
    with pytest.raises(RuntimeDomainBindingError, match="resolved scene identities"):
        _validate_profile_scene_identities(profile)


def test_resolved_scene_hash_matches_pinned_v3_protocol_serializer() -> None:
    root = Path(__file__).resolve().parents[5]
    protocol = json.loads(
        (root / "ml/synthetic/runtime_graph_visible_content_v3_protocol.json").read_bytes()
    )
    observed: dict[str, str] = {}
    wrong_serializer: dict[str, str] = {}
    for profile_name, scene_split in (("train", "train"), ("dev", "validation")):
        rows = []
        wrong_rows = []
        for dataset_seed in protocol["fixed_splits"][profile_name]["dataset_seeds"]:
            selected = [
                scene
                for scene in subject._build_scenes(
                    subject.PRESETS["smoke"],
                    dataset_seed,
                    require_complete_style_catalog=True,
                )
                if subject._scene_split(scene) == scene_split
            ]
            rows.extend(
                {
                    "dataset_seed": dataset_seed,
                    "scene_seed": scene["seed"],
                    "scene_id": scene["scene_id"],
                    "resolved_scene_sha256": sha256(
                        synthetic_canonical_json_bytes(scene)
                    ).hexdigest(),
                }
                for scene in selected
            )
            wrong_rows.extend(
                {
                    "dataset_seed": dataset_seed,
                    "scene_seed": scene["seed"],
                    "scene_id": scene["scene_id"],
                    "resolved_scene_sha256": sha256(canonical_json_bytes(scene)).hexdigest(),
                }
                for scene in selected
            )
        observed[profile_name] = sha256(
            synthetic_canonical_json_bytes(rows)
        ).hexdigest()
        wrong_serializer[profile_name] = sha256(canonical_json_bytes(wrong_rows)).hexdigest()

    assert observed == {
        "train": "36bb2b1285c592483a8a7cde8ecd636c8c95463756084b25ea5347e97c9db342",
        "dev": "5af3bee05ea0d7937b65475ee5488dfe840e5395cfbfb83c7d9deef88edc8678",
    }
    assert wrong_serializer != observed


def test_v3_regeneration_requires_exact_rgb_png_before_returning_annotations(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    image = Image.new("RGB", (64, 64))
    image.putdata(
        [
            ((x * 17 + y * 3) % 256, (x * 5 + y * 11) % 256, (x * 13 + y * 7) % 256)
            for y in range(64)
            for x in range(64)
        ]
    )
    payload = synthetic_png_bytes(image)
    assert payload != _png(image)
    image_sha = sha256(payload).hexdigest()
    scene = {
        "seed": 39300,
        "scene_id": "scene",
        "families": {
            axis: {"key": value, "split": "train"}
            for axis, value in zip(subject.FAMILY_AXES, ("a", "b", "c", "d", "e"), strict=True)
        },
    }
    annotation = object()
    monkeypatch.setattr(subject, "_build_scenes", lambda *_args, **_kwargs: [scene])
    monkeypatch.setattr(subject, "_scene_split", lambda _scene: "train")
    monkeypatch.setattr(subject, "_render_v3_source", lambda _scene: (image, annotation))
    profile = V3GeneratorProfile(
        EXPECTED_GENERATOR_VERSION,
        Path("protocol"),
        "1" * 64,
        (),
        V3SplitProfile((393,), (39300,), 1, 1, "2" * 64),
        V3SplitProfile((393,), (39304,), 1, 1, "3" * 64),
    )
    record = {
        "image": f"train-39300-{image_sha[:12]}.png",
        "image_sha256": image_sha,
        "width": 64,
        "height": 64,
        "family": FAMILY,
        "seed": 39300,
    }

    regenerated = _regenerate_v3_records("train", 393, [record], profile)

    assert regenerated[image_sha].annotation is annotation
    with pytest.raises(RuntimeDomainBindingError, match="PNG identity"):
        _regenerate_v3_records("train", 393, [{**record, "image_sha256": "8" * 64}], profile)


def test_regeneration_seam_uses_source_only_renderer_without_audit(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    image = Image.new("RGB", (2, 2), "white")
    annotation = {"markers": []}
    monkeypatch.setattr(
        subject.visible_content_v3,
        "render_visible_content_source",
        lambda _scene: SimpleNamespace(image=image, annotation=annotation, marker_mask=None),
    )
    monkeypatch.setattr(
        subject.visible_content_v3,
        "render_visible_content_scene",
        lambda _scene: pytest.fail("the expensive element audit must not run"),
    )

    actual_image, actual_annotation = subject._render_v3_source({})

    assert actual_image.tobytes() == image.tobytes()
    assert actual_annotation is annotation


def test_v3_manifest_uses_existing_family_source_and_authenticates_png(
    tmp_path: Path,
) -> None:
    image = Image.new("RGB", (4, 3), "white")
    payload = _png(image)
    image_sha = sha256(payload).hexdigest()
    name = f"train-39300-{image_sha[:12]}.png"
    (tmp_path / name).write_bytes(payload)
    validator_path = Path(__file__).resolve().parents[5] / runtime_inputs.VALIDATOR_RELATIVE_PATH
    validator = runtime_inputs._load_bound_validator(
        sha256(validator_path.read_bytes()).hexdigest()
    )

    manifest = {
        "schema": validator.MANIFEST_SCHEMA,
        "source": subject.EXPECTED_MANIFEST_SOURCE,
        "preset": "smoke",
        "split": "train",
        "seed": 393,
        "contains_truth": False,
        "contains_precomputed_masks": False,
        "images": [
            {
                "image": name,
                "image_sha256": image_sha,
                "width": 4,
                "height": 3,
                "split": "train",
                "family": FAMILY,
                "seed": 39300,
            }
        ],
    }

    split, seed, records = _validate_manifest_v3(manifest, tmp_path / "manifest.json", validator)
    assert (split, seed, records[0]["image_sha256"]) == ("train", 393, image_sha)
    assert manifest["source"] == validator.MANIFEST_SOURCE
    with pytest.raises(RuntimeDomainBindingError, match="foreign source"):
        _validate_manifest_v3(
            {**manifest, "source": "project-owned-synthetic-runtime-graph-visible-content-v3"},
            tmp_path / "manifest.json",
            validator,
        )


def _domain_evidence() -> tuple[dict, dict, RuntimeEvidenceBinding, RuntimeModelIdentity]:
    model = {
        "model_id": "OpenCvSharpExtern",
        "version": "axis-opencv-v1",
        "sha256": "3" * 64,
        "provider": "cpu",
    }
    envelope = {
        "contract_version": 1,
        "run_id": "run",
        "project_id": "project",
        "panel_id": _panel().panel_id,
        "stage": "axis",
        "stage_version": "axis-opencv-v1",
        "input_sha256": PANEL_SHA,
        "coordinate_space": "original_pixels",
        "model": model,
        "timing": {},
        "confidence": 0.9,
        "warnings": [],
        "transforms": [],
    }
    points = [
        {"x": 0.0, "y": 2.0, "is_finite": True},
        {"x": 3.0, "y": 2.0, "is_finite": True},
        {"x": 3.0, "y": 0.0, "is_finite": True},
        {"x": 0.0, "y": 0.0, "is_finite": True},
    ]
    polygon = {
        "bottom_left": points[0],
        "bottom_right": points[1],
        "top_right": points[2],
        "top_left": points[3],
        "points": points,
    }
    report_panel = {
        "panel_id": _panel().panel_id,
        "image_sha256": PANEL_SHA,
        "width": 4,
        "height": 3,
        "source_image_sha256": SOURCE_SHA,
        "source_width": 4,
        "source_height": 3,
        "crop": {"x": 0, "y": 0, "width": 4, "height": 3},
        "requested_crop": {"x": 0.0, "y": 0.0, "width": 4.0, "height": 3.0},
        "source_to_panel_matrix": [1, 0, 0, 0, 1, 0, 0, 0, 1],
        "panel_to_source_matrix": [1, 0, 0, 0, 1, 0, 0, 0, 1],
        "composed_mask_source_envelopes": [envelope],
        "axis": {
            "envelope": envelope,
            "geometry": {"coordinate_space": "original_pixels", "plot_polygon": polygon},
        },
    }
    case = {"image_sha256": SOURCE_SHA, "width": 4, "height": 3}
    binding = RuntimeEvidenceBinding("train", Path("manifest"), "4" * 64, Path("report"), "5" * 64)
    axis_model = RuntimeModelIdentity(
        "axis", "axis-opencv-v1", "OpenCvSharpExtern", "axis-opencv-v1", "3" * 64, "cpu"
    )
    return case, report_panel, binding, axis_model


def test_domain_identity_explicitly_binds_v3_binding_hash() -> None:
    case, report_panel, binding, axis_model = _domain_evidence()
    reports = {(SOURCE_SHA, _panel().panel_id): ({"case": case, "panel": report_panel}, binding)}

    first = _panel_domain_v3(_panel(), reports, axis_model, "6" * 64)
    second = _panel_domain_v3(_panel(), reports, axis_model, "7" * 64)

    assert first.domain.polygon == second.domain.polygon
    assert first.evidence_sha256 != second.evidence_sha256
    assert first.domain.identity == first.evidence_sha256


def test_scope_correction_does_not_overwrite_or_overclaim_prior_proxy() -> None:
    root = Path(__file__).resolve().parents[5]
    prior_path = root / "artifacts/goal22-runs/db-postprocess-replay/text-corpus-diagnosis/report.json"
    correction_path = prior_path.parent / "scope-correction.json"
    correction = json.loads(correction_path.read_bytes())

    assert sha256(prior_path.read_bytes()).hexdigest() == correction["prior_report"]["sha256"]
    assert correction["prior_report"]["preserved_byte_for_byte"] is True
    language = canonical_json_bytes(correction["correction"]).decode()
    assert "does not prove" in language
    assert correction["acceptance_effect"] == "none"
