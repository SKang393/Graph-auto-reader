# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Multi-seed runtime binding identity, split, and content barriers."""

from dataclasses import asdict, replace
from hashlib import sha256
import json
from pathlib import Path

import numpy as np
import pytest
import torch

from ml.markers.center.mask_preserving_v24 import runtime_binding_v2 as binding
from ml.markers.center.mask_preserving_v24 import runtime_binding, runtime_family_scenes
from ml.markers.center.mask_preserving_v24.runtime_family_scenes import (
    RuntimeFamilyScene,
    RuntimeFamilySceneError,
    RuntimeFamilySceneJoin,
    RuntimePanelMappingAudit,
    RuntimeSourceMappingAudit,
)
from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    RuntimeAlgorithmIdentity,
    RuntimeAssemblyIdentity,
    RuntimeEvidenceBinding,
    RuntimeImplementationIdentity,
    RuntimeInputError,
    RuntimeModelIdentity,
    RuntimeSourceIdentity,
    SYNTHETIC_SOURCE_RELATIVE_PATHS,
    ValidatedRuntimeInputs,
    ValidatedRuntimePanelInput,
)
from ml.synthetic.dataset import FAMILY_AXES


def _family(prefix: str, overrides: dict[str, str] | None = None) -> str:
    values = {axis: f"{prefix}-{axis}" for axis in FAMILY_AXES}
    values.update(overrides or {})
    return "|".join(f"{axis}={values[axis]}" for axis in FAMILY_AXES)


def _panel(prefix: str, dataset_seed: int, scene_seed: int, family: str, split: str):
    plane = np.zeros((2, 3), dtype=np.uint8)
    probability = np.zeros((2, 3), dtype=np.float32)
    return ValidatedRuntimePanelInput(
        split=split,
        dataset_seed=dataset_seed,
        family=family,
        scene_seed=scene_seed,
        source_sha256=sha256(f"{prefix}-source".encode()).hexdigest(),
        panel_id=f"{prefix}-panel",
        panel_sha256=sha256(f"{prefix}-panel-bytes".encode()).hexdigest(),
        width=3,
        height=2,
        crop=(0, 0, 3, 2),
        requested_crop=(0.0, 0.0, 3.0, 2.0),
        gray8=plane,
        ocr_mask=probability,
        geometry_mask=probability,
        artifact_mask=probability,
    )


def _joined_scene(panel: ValidatedRuntimePanelInput) -> RuntimeFamilyScene:
    return RuntimeFamilyScene(
        split=panel.split,
        family=panel.family,
        seed=panel.scene_seed,
        tensor=torch.full((3, 2, 3), float(panel.scene_seed)),
        centers=(),
        diameters=(),
        hard_negatives=(),
        source_sha256=panel.source_sha256,
        panel_id=panel.panel_id,
        panel_sha256=panel.panel_sha256,
        crop=panel.crop,
        requested_crop=panel.requested_crop,
        dataset_seed=panel.dataset_seed,
        sampling_identity=f"sample-{panel.panel_id}",
    )


def _audit(panel: ValidatedRuntimePanelInput) -> RuntimeSourceMappingAudit:
    return RuntimeSourceMappingAudit(
        split=panel.split,
        source_sha256=panel.source_sha256,
        source_marker_count=0,
        mapped_marker_count=0,
        source_hard_negative_count=0,
        mapped_hard_negative_count=0,
        source_descriptive_record_count=0,
        mapped_descriptive_record_count=0,
        panels=(RuntimePanelMappingAudit(panel.panel_id, 0, 0, 0),),
        omitted=(),
    )


@pytest.fixture
def exchange(tmp_path, monkeypatch):
    root = tmp_path
    artifacts = root / "artifacts"
    artifacts.mkdir()
    digest = "a" * 64
    assembly = RuntimeAssemblyIdentity("fixture", digest)
    identity = RuntimeImplementationIdentity(
        digest,
        tuple(RuntimeSourceIdentity(path, digest) for path in SYNTHETIC_SOURCE_RELATIVE_PATHS),
        Path("artifacts/candidate.json"),
        digest,
        digest,
        "development-only",
        (assembly,),
        RuntimeAlgorithmIdentity("artifact", "v1", digest, digest, "v1", "{}", (assembly,)),
        tuple(
            RuntimeModelIdentity(stage, "v1", name, "v1", digest, "CPUExecutionProvider")
            for stage, name in (("axis", "axis"), ("ocr", "detector"), ("ocr", "recognizer"))
        ),
    )
    panels = {
        "train394": _panel("train394", 394, 39401, _family("train394"), "train"),
        "train393": _panel("train393", 393, 39301, _family("train393"), "train"),
        "dev": _panel("dev393", 393, 39305, _family("dev"), "validation"),
    }
    train_bindings = [
        RuntimeEvidenceBinding(
            "train", Path("artifacts/train394-manifest.json"), digest,
            Path("artifacts/train394-report.json"), digest,
        ),
        RuntimeEvidenceBinding(
            "train", Path("artifacts/train393-manifest.json"), digest,
            Path("artifacts/train393-report.json"), digest,
        ),
    ]
    dev_binding = RuntimeEvidenceBinding(
        "validation", Path("artifacts/dev-manifest.json"), digest,
        Path("artifacts/dev-report.json"), digest,
    )
    load_calls = []
    join_calls = []
    state = {
        "foreign_implementation": False,
        "join_failure": None,
        "tensor_values": {},
        "extra_train_panels": {},
    }

    def load(train, dev, implementation, *, repository_root):
        load_calls.append((train, dev, implementation, repository_root))
        key = "train394" if "394" in train.manifest_path.name else "train393"
        returned_implementation = (
            replace(implementation, native_scope="foreign")
            if state["foreign_implementation"] else implementation
        )
        train_panels = (panels[key], *state["extra_train_panels"].get(key, ()))
        return ValidatedRuntimeInputs(
            train_panels, (panels["dev"],), returned_implementation
        )

    def join_split(runtime_panels, expected_split):
        join_calls.append(expected_split)
        if state["join_failure"] == expected_split:
            raise RuntimeFamilySceneError(f"{expected_split} runtime source coverage differs")
        assert all(panel.split == expected_split for panel in runtime_panels)
        assert len({panel.dataset_seed for panel in runtime_panels}) == 1
        scenes = []
        for panel in runtime_panels:
            scene = _joined_scene(panel)
            if panel.panel_id in state["tensor_values"]:
                scene = replace(
                    scene,
                    tensor=torch.full(
                        (3, 2, 3), float(state["tensor_values"][panel.panel_id])
                    ),
                )
            scenes.append(scene)
        return tuple(scenes), tuple(_audit(panel) for panel in runtime_panels), runtime_panels[0].dataset_seed

    monkeypatch.setattr(binding.runtime_inputs, "load_bound_runtime_inputs", load)
    monkeypatch.setattr(binding.runtime_family_scenes, "_join_split", join_split)

    def expected_join():
        train = tuple(_joined_scene(panels[key]) for key in ("train394", "train393"))
        dev = (_joined_scene(panels["dev"]),)
        audits = tuple(_audit(panels[key]) for key in ("train394", "train393", "dev"))
        return RuntimeFamilySceneJoin(train, dev, audits)

    document = {
        "schema": binding.BINDING_SCHEMA,
        "synthetic_only": True,
        "private_data": False,
        "sealed_runs": 0,
        "training_input_ready": True,
        "production_approved": False,
        "complete_artifact_mask": False,
        "train": [asdict(item) for item in train_bindings],
        "dev": asdict(dev_binding),
        "implementation": asdict(identity),
        "v1_binding_source_sha256": sha256(Path(runtime_binding.__file__).read_bytes()).hexdigest(),
        "loader_source_sha256": sha256(Path(binding.runtime_inputs.__file__).read_bytes()).hexdigest(),
        "join_source_sha256": sha256(Path(runtime_family_scenes.__file__).read_bytes()).hexdigest(),
        "binding_v2_source_sha256": sha256(Path(binding.__file__).read_bytes()).hexdigest(),
        "train_tensor_set_sha256": "",
        "dev_tensor_set_sha256": "",
        "mapping_audit_sha256": "",
    }

    def write():
        joined = expected_join()
        document["train_tensor_set_sha256"] = runtime_binding.tensor_set_sha256(joined.train)
        document["dev_tensor_set_sha256"] = runtime_binding.tensor_set_sha256(joined.dev)
        document["mapping_audit_sha256"] = runtime_binding.mapping_audit_sha256(joined)
        serializable = json.loads(json.dumps(document, default=lambda value: value.as_posix()))
        payload = json.dumps(serializable).encode()
        path = artifacts / "binding-v2.json"
        path.write_bytes(payload)
        return path, sha256(payload).hexdigest(), joined

    return root, document, panels, load_calls, join_calls, state, write


def test_ordered_train_pairs_share_dev_and_join_dev_once(exchange):
    root, _, _, load_calls, join_calls, _, write = exchange
    path, digest, expected = write()

    actual = binding.load_runtime_family_binding_v2(path, digest, repository_root=root)

    assert [scene.dataset_seed for scene in actual.train] == [394, 393]
    assert [scene.panel_id for scene in actual.train] == [
        scene.panel_id for scene in expected.train
    ]
    assert [scene.panel_id for scene in actual.dev] == [scene.panel_id for scene in expected.dev]
    assert actual.audit == expected.audit
    assert runtime_binding.tensor_set_sha256(actual.train) == runtime_binding.tensor_set_sha256(
        expected.train
    )
    assert len(load_calls) == 2
    assert load_calls[0][1] == load_calls[1][1]
    assert join_calls == ["train", "validation", "train"]


@pytest.mark.parametrize(
    "field",
    [
        "v1_binding_source_sha256",
        "loader_source_sha256",
        "join_source_sha256",
        "binding_v2_source_sha256",
    ],
)
def test_changed_parser_loader_join_or_v2_source_fails_before_runtime_read(exchange, field):
    root, document, _, load_calls, _, _, write = exchange
    document[field] = "b" * 64
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="executing implementation"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)
    assert not load_calls


@pytest.mark.parametrize(
    "field,value",
    [
        ("production_approved", True),
        ("private_data", True),
        ("sealed_runs", 1),
        ("training_input_ready", False),
        ("complete_artifact_mask", True),
    ],
)
def test_foreign_or_approved_scope_fails_closed(exchange, field, value):
    root, document, _, load_calls, _, _, write = exchange
    document[field] = value
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="foreign or approved"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)
    assert not load_calls


def test_binding_file_tamper_and_unknown_fields_fail(exchange):
    root, document, _, load_calls, _, _, write = exchange
    path, digest, _ = write()
    path.write_bytes(path.read_bytes() + b" ")
    with pytest.raises(RuntimeInputError, match="bytes differ"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)
    document["unexpected"] = True
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="invalid binding shape"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)
    assert not load_calls


@pytest.mark.parametrize(
    "identity,field",
    [
        ("source", "source_sha256"),
        ("panel", "panel_id"),
        ("scene", "scene_seed"),
        ("dataset", "dataset_seed"),
    ],
)
def test_duplicate_train_identities_are_rejected(exchange, identity, field):
    root, _, panels, _, _, _, write = exchange
    panels["train393"] = replace(
        panels["train393"], **{field: getattr(panels["train394"], field)}
    )
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match=f"duplicate {identity} identities"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


def test_duplicate_train_panel_png_is_rejected_despite_distinct_outer_identities(exchange):
    root, _, panels, _, _, _, write = exchange
    panels["train393"] = replace(
        panels["train393"], panel_sha256=panels["train394"].panel_sha256
    )
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="duplicate panel PNG payloads"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


def test_duplicate_panel_png_within_one_train_binding_is_rejected(exchange):
    root, _, panels, _, _, state, write = exchange
    original = panels["train394"]
    state["extra_train_panels"]["train394"] = (
        replace(
            original,
            source_sha256=sha256(b"distinct-source").hexdigest(),
            panel_id="distinct-panel",
            scene_seed=39499,
        ),
    )
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="duplicate panel PNG payloads"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


def test_train_dev_panel_png_overlap_is_rejected_despite_distinct_outer_identities(exchange):
    root, _, panels, _, _, _, write = exchange
    panels["train393"] = replace(
        panels["train393"], panel_sha256=panels["dev"].panel_sha256
    )
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="validation panel PNG payloads overlap"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


def test_duplicate_train_tensor_is_rejected_despite_distinct_png_and_outer_identities(exchange):
    root, _, panels, _, _, state, write = exchange
    state["tensor_values"][panels["train393"].panel_id] = panels["train394"].scene_seed
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="duplicate runtime tensors"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


def test_duplicate_tensor_within_one_train_binding_is_rejected(exchange):
    root, _, panels, _, _, state, write = exchange
    original = panels["train394"]
    extra = replace(
        original,
        source_sha256=sha256(b"second-distinct-source").hexdigest(),
        panel_id="second-distinct-panel",
        panel_sha256=sha256(b"second-distinct-panel-png").hexdigest(),
        scene_seed=39498,
    )
    state["extra_train_panels"]["train394"] = (extra,)
    state["tensor_values"][extra.panel_id] = original.scene_seed
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="duplicate runtime tensors"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


def test_train_dev_tensor_overlap_is_rejected_despite_distinct_png_and_outer_identities(exchange):
    root, _, panels, _, _, state, write = exchange
    state["tensor_values"][panels["train393"].panel_id] = panels["dev"].scene_seed
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="validation runtime tensors overlap"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


@pytest.mark.parametrize("axis", FAMILY_AXES)
def test_each_semantic_family_axis_must_be_train_dev_disjoint(exchange, axis):
    root, _, panels, _, _, _, write = exchange
    dev_axis_value = f"dev-{axis}"
    panels["train393"] = replace(
        panels["train393"], family=_family("train393", {axis: dev_axis_value})
    )
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match=f"validation {axis} family identities overlap"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


def test_empty_train_and_incomplete_regenerated_split_are_rejected(exchange):
    root, document, _, load_calls, _, state, write = exchange
    original_train = document["train"]
    document["train"] = []
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="nonempty ordered train array"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)
    assert not load_calls

    document["train"] = original_train
    state["join_failure"] = "train"
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="source coverage differs"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


def test_mismatched_common_implementation_is_rejected(exchange):
    root, _, _, _, _, state, write = exchange
    state["foreign_implementation"] = True
    path, digest, _ = write()
    with pytest.raises(RuntimeInputError, match="different common implementation"):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)


@pytest.mark.parametrize(
    "field",
    ["train_tensor_set_sha256", "dev_tensor_set_sha256", "mapping_audit_sha256"],
)
def test_final_tensor_and_mapping_hashes_are_mandatory(exchange, field):
    root, document, _, _, _, _, write = exchange
    path, digest, _ = write()
    document[field] = "b" * 64
    payload = json.dumps(
        json.loads(json.dumps(document, default=lambda value: value.as_posix()))
    ).encode()
    path.write_bytes(payload)
    digest = sha256(payload).hexdigest()
    with pytest.raises(RuntimeInputError, match=field):
        binding.load_runtime_family_binding_v2(path, digest, repository_root=root)
