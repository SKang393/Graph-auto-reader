# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Audit the frozen V26 train confidence boundary without optimization.

The diagnostic reconstructs the exact selected V26 train rows, measures local
same-label and opposite-label patch distances inside each assigned-truth
annulus, and performs one batched forward pass through the frozen checkpoint.
It emits aggregate synthetic-train evidence only.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
from dataclasses import replace
import hashlib
import json
from pathlib import Path
import sys
import time
from typing import Any, Iterable, Mapping, Sequence


REPO_ROOT = Path(__file__).resolve().parents[2]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

import numpy as np
import torch

from ml.markers.center.localization_confidence_v26 import train_p1 as v26  # noqa: E402
from ml.markers.center.plot_domain_v25 import train_p1 as v25  # noqa: E402


RESULT_PATH = Path("ml/markers/center/localization_confidence_v26/P1_RESULT.json")
RESULT_SHA256 = "82f7906c71f6fa09f9850c92c5d373adc9f2611fe015d3562c3fdbcd6516f53e"
CONFIG_PATH = Path("ml/markers/center/localization_confidence_v26/training/p1.json")
CONFIG_SHA256 = "31f8fee2ecb4fc01b2c1234761423dcbb668e98c90157abd4e1179c6749236db"
CANDIDATE_REPORT_PATH = Path(
    "artifacts/goal22-runs/marker-v26-annulus/P1-run/candidate-report.json"
)
CANDIDATE_REPORT_SHA256 = "37a985d7cd321a53305d715764383e66feaf3bc80adebf038e292e01d0df96fb"
CHECKPOINT_PATH = Path(
    "artifacts/goal22-runs/marker-v26-annulus/P1-run/"
    "marker-center-annulus-reservation-v26-p1.pt"
)
CHECKPOINT_SHA256 = "9c961215dd79eb8686309a8234cfb5365a599b385cfb69c3a708568f7c58c81e"
BASE_V25_CONFIG_PATH = v26.BASE_V25_CONFIG_PATH
BASE_V25_CONFIG_SHA256 = v26.BASE_V25_CONFIG_SHA256
BASE_V25_RESULT_PATH = v26.BASE_V25_RESULT_PATH
BASE_V25_RESULT_SHA256 = v26.BASE_V25_RESULT_SHA256
SNAPSHOT_PATH = Path(
    "ml/markers/training-seals/marker-center/"
    "marker-center-annulus-reservation-v26/P1/source-snapshot.json"
)
SNAPSHOT_SHA256 = "7ac89bec76bf7fed6ff08d2334a5a615128bc5047192a461b63757118f78080d"
COVERAGE_REPORT_PATH = v26.COVERAGE_REPORT_PATH
COVERAGE_REPORT_SHA256 = v26.COVERAGE_REPORT_SHA256
COVERAGE_CACHE_PATH = v26.COVERAGE_CACHE_PATH
COVERAGE_CACHE_SHA256 = v26.COVERAGE_CACHE_SHA256
ANNULUS_REPORT_PATH = v26.ANNULUS_REPORT_PATH
ANNULUS_REPORT_SHA256 = v26.ANNULUS_REPORT_SHA256
ANNULUS_CACHE_PATH = v26.ANNULUS_CACHE_PATH
ANNULUS_CACHE_SHA256 = v26.ANNULUS_CACHE_SHA256
EXPECTED_ROWS = {"component": 35_838, "family": 9_053}
EXPECTED_POSITIVES = {"component": 3_258, "family": 823}
CHANNEL_NAMES = ("ink", "text_mask", "artifact_mask")
ANNULI = (
    ("negative_gt3_le5", 3.0, 5.0),
    ("negative_gt5_le8", 5.0, 8.0),
    ("negative_gt8_le12", 8.0, 12.0),
)

# These exact changes occurred after V26 P1. The diagnostic records them and
# proves they cannot silently alter the reconstructed selected-row identities.
ALLOWED_POST_RUN_SOURCE_DRIFT = {
    "ml/synthetic/renderer.py": {
        "current_sha256": "c49c070afc505f92bfff7ff55b22e669c3ec2b76b56ff807b72a026ca0535283",
        "execution_scope": (
            "executed for V3 family train/dev regeneration: "
            "runtime_graph_visible_content_v3.render_visible_content_source calls "
            "renderer.render_scene, and non-halftone degradation stages call the "
            "renderer._degrade function imported by runtime_graph_axis_preserving_v2; "
            "regenerated PNG hashes must match the frozen manifests"
        ),
    },
    "ml/markers/training_budget.py": {
        "current_sha256": "278642eb3b6decdd6ed77a41dd635092567e67b03b912d503af2790189ebf8c4",
        "execution_scope": "imported by the historical runner; no budget function is called",
    },
    "ml/markers/gate_seal.py": {
        "current_sha256": "0015612194e6c082cedd0b9d830dc427e94567302b39e5f3fe43dd090c0eb725",
        "execution_scope": (
            "canonical_json_bytes executes for panel-inventory, tensor-multiset, "
            "truth-mapping, and plot-domain aggregate hashes; each exact frozen "
            "aggregate hash must match"
        ),
    },
}


class DiagnosticError(RuntimeError):
    """The frozen evidence, reconstruction, or diagnostic contract changed."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _read_json(path: Path, expected_sha256: str, label: str) -> dict[str, Any]:
    if _sha256(path) != expected_sha256:
        raise DiagnosticError(f"{label} bytes changed")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise DiagnosticError(f"could not read {label}") from error
    if not isinstance(value, dict):
        raise DiagnosticError(f"{label} must be a JSON object")
    return value


def _check_deadline(started: float, limit_seconds: float, stage: str) -> None:
    elapsed = time.perf_counter() - started
    if elapsed > limit_seconds:
        raise DiagnosticError(
            f"elapsed-time bound exceeded after {stage}: {elapsed:.3f}s > {limit_seconds:.3f}s"
        )


def _validate_with_canonical_source_rebinding(
    document: Mapping[str, Any],
    *,
    expected_historical_sha256: str,
    authenticated_current_sha256: str,
    helper_validator: Any,
) -> None:
    """Retain the frozen validator while rebinding its one moved helper source."""
    field = "canonical_json_source_sha256"
    if document.get(field) != expected_historical_sha256:
        raise DiagnosticError(
            "V3 binding canonical JSON source does not match the frozen snapshot"
        )
    rebound_document = dict(document)
    rebound_document[field] = authenticated_current_sha256
    helper_validator(rebound_document)


def _validate_with_renderer_source_rebinding(
    profile: Any,
    root: Path,
    *,
    renderer_relative_path: Path,
    expected_historical_sha256: str,
    authenticated_current_sha256: str,
    profile_validator: Any,
) -> None:
    """Retain the frozen profile validator while rebinding one executed source."""
    matching = [
        (index, source)
        for index, source in enumerate(profile.sources)
        if source.relative_path == renderer_relative_path
    ]
    if len(matching) != 1:
        raise DiagnosticError("V3 profile renderer source identity is missing or repeated")
    renderer_index, renderer_source = matching[0]
    if renderer_source.sha256 != expected_historical_sha256:
        raise DiagnosticError(
            "V3 profile renderer source does not match the frozen snapshot"
        )
    rebound_sources = list(profile.sources)
    rebound_sources[renderer_index] = replace(
        renderer_source, sha256=authenticated_current_sha256
    )
    rebound_profile = replace(profile, sources=tuple(rebound_sources))
    profile_validator(rebound_profile, root)


def _authenticate_sources(root: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    snapshot = _read_json(root / SNAPSHOT_PATH, SNAPSHOT_SHA256, "V26 source snapshot")
    raw_sources = snapshot.get("sources")
    if not isinstance(raw_sources, list):
        raise DiagnosticError("V26 source snapshot sources changed")
    historical = {
        str(row.get("path")): str(row.get("sha256"))
        for row in raw_sources
        if isinstance(row, dict)
    }
    authenticated: list[dict[str, Any]] = []
    runner_paths = tuple(dict.fromkeys(path.as_posix() for path in v26.RUNNER_SOURCE_PATHS))
    for relative in runner_paths:
        if relative not in historical:
            raise DiagnosticError(f"historical source missing from snapshot: {relative}")
        current = _sha256(root / relative)
        expected = historical[relative]
        row: dict[str, Any] = {
            "path": relative,
            "historical_sha256": expected,
            "current_sha256": current,
        }
        drift = ALLOWED_POST_RUN_SOURCE_DRIFT.get(relative)
        if current != expected:
            if drift is None or current != drift["current_sha256"]:
                raise DiagnosticError(f"unauthorized historical source drift: {relative}")
            row["execution_scope"] = drift["execution_scope"]
            row["post_run_drift_authenticated"] = True
        else:
            row["post_run_drift_authenticated"] = False
        authenticated.append(row)
    if set(ALLOWED_POST_RUN_SOURCE_DRIFT) - set(runner_paths):
        raise DiagnosticError("post-run drift allowlist contains a non-runner source")
    return authenticated, snapshot


def _authenticate_frozen_inputs(root: Path) -> dict[str, Any]:
    result = _read_json(root / RESULT_PATH, RESULT_SHA256, "V26 result")
    config = _read_json(root / CONFIG_PATH, CONFIG_SHA256, "V26 configuration")
    candidate = _read_json(
        root / CANDIDATE_REPORT_PATH, CANDIDATE_REPORT_SHA256, "V26 candidate report"
    )
    coverage_report = _read_json(
        root / COVERAGE_REPORT_PATH, COVERAGE_REPORT_SHA256, "negative coverage report"
    )
    annulus_report = _read_json(
        root / ANNULUS_REPORT_PATH, ANNULUS_REPORT_SHA256, "annulus selection report"
    )
    base_result = _read_json(
        root / BASE_V25_RESULT_PATH, BASE_V25_RESULT_SHA256, "V25 result"
    )
    expected_result = {
        "task": "marker-center",
        "revision": v26.REVISION,
        "candidate_id": v26.CANDIDATE_ID,
        "status": "failed_dev_unconsumed",
        "candidate_consumed": False,
        "checkpoint_sha256": CHECKPOINT_SHA256,
        "candidate_config_sha256": CONFIG_SHA256,
        "private_reads": 0,
        "sealed_runs": 0,
        "production_approval": False,
    }
    if any(result.get(key) != value for key, value in expected_result.items()):
        raise DiagnosticError("V26 result identity or scope changed")
    if result.get("candidate_report") != {
        "path": CANDIDATE_REPORT_PATH.as_posix(),
        "sha256": CANDIDATE_REPORT_SHA256,
    } or result.get("source_snapshot") != {
        "path": SNAPSHOT_PATH.as_posix(),
        "sha256": SNAPSHOT_SHA256,
    }:
        raise DiagnosticError("V26 result descriptors changed")
    if (
        candidate.get("status") != "failed_dev"
        or candidate.get("candidate_config_sha256") != CONFIG_SHA256
        or candidate.get("checkpoint_sha256") != CHECKPOINT_SHA256
        or candidate.get("selection_evidence") != result.get("selection_evidence")
        or candidate.get("optimizer_steps") != 12_636
        or candidate.get("private_data") is not False
        or candidate.get("sealed_data") is not False
        or candidate.get("sealed_runs") != 0
        or candidate.get("production_approval") is not False
    ):
        raise DiagnosticError("V26 candidate report identity or scope changed")
    if _sha256(root / CHECKPOINT_PATH) != CHECKPOINT_SHA256:
        raise DiagnosticError("V26 checkpoint bytes changed")
    descriptors = (
        ("base_v25_config", BASE_V25_CONFIG_PATH, BASE_V25_CONFIG_SHA256),
        ("base_v25_result", BASE_V25_RESULT_PATH, BASE_V25_RESULT_SHA256),
        ("negative_coverage_report", COVERAGE_REPORT_PATH, COVERAGE_REPORT_SHA256),
        ("negative_coverage_cache", COVERAGE_CACHE_PATH, COVERAGE_CACHE_SHA256),
        ("annulus_preflight", ANNULUS_REPORT_PATH, ANNULUS_REPORT_SHA256),
        ("annulus_selection_cache", ANNULUS_CACHE_PATH, ANNULUS_CACHE_SHA256),
    )
    for key, path, digest in descriptors:
        if config.get(key) != {"path": path.as_posix(), "sha256": digest}:
            raise DiagnosticError(f"V26 configuration descriptor changed: {key}")
        if _sha256(root / path) != digest:
            raise DiagnosticError(f"V26 input bytes changed: {key}")
    if (
        coverage_report.get("status") != "model_free_complete"
        or coverage_report.get("private_reads") != 0
        or coverage_report.get("sealed_runs") != 0
        or annulus_report.get("status") != "model_free_preflight_only"
        or annulus_report.get("dev_truth_used_for_selection") is not False
        or annulus_report.get("private_reads") != 0
        or annulus_report.get("sealed_runs") != 0
    ):
        raise DiagnosticError("V26 row-selection evidence scope changed")
    if (
        base_result.get("task") != "marker-center"
        or base_result.get("revision") != "marker-center-plot-domain-v25"
        or base_result.get("candidate_id") != "P1"
        or base_result.get("optimizer_steps") != 12_636
        or base_result.get("private_reads") != 0
        or base_result.get("sealed_runs") != 0
        or base_result.get("production_approval") is not False
    ):
        raise DiagnosticError("V25 result identity or scope changed")
    return {
        "result": result,
        "config": config,
        "candidate": candidate,
        "coverage_report": coverage_report,
        "annulus_report": annulus_report,
    }


def _load_npz(path: Path, expected_names: set[str]) -> dict[str, np.ndarray]:
    with np.load(path, allow_pickle=False) as loaded:
        if set(loaded.files) != expected_names:
            raise DiagnosticError(f"cache arrays changed: {path.as_posix()}")
        return {name: loaded[name] for name in loaded.files}


def _reconstruct_rows(
    root: Path,
    frozen: Mapping[str, Any],
    authenticated_sources: Sequence[Mapping[str, Any]],
) -> tuple[
    dict[str, tuple[torch.Tensor, ...]],
    dict[str, dict[str, np.ndarray]],
    dict[str, Any],
    dict[str, Any],
]:
    source_rows = {
        str(row.get("path")): row for row in authenticated_sources
    }
    gate_seal_source = source_rows.get("ml/markers/gate_seal.py")
    if (
        gate_seal_source is None
        or gate_seal_source.get("historical_sha256")
        != "b310789162c06ca11cb348cbf2fcece877a25ce6173ec0dee70bdc29d33ef6d7"
        or gate_seal_source.get("current_sha256")
        != ALLOWED_POST_RUN_SOURCE_DRIFT["ml/markers/gate_seal.py"]["current_sha256"]
        or gate_seal_source.get("post_run_drift_authenticated") is not True
    ):
        raise DiagnosticError("canonical JSON source drift was not authenticated")
    renderer_source = source_rows.get("ml/synthetic/renderer.py")
    if (
        renderer_source is None
        or renderer_source.get("current_sha256")
        != ALLOWED_POST_RUN_SOURCE_DRIFT["ml/synthetic/renderer.py"]["current_sha256"]
        or renderer_source.get("post_run_drift_authenticated") is not True
    ):
        raise DiagnosticError("executed renderer source drift was not authenticated")

    # The frozen V3 binding compares the historical gate_seal.py source hash
    # before it can reconstruct any panels. The canonical helper now lives in
    # the exact authenticated post-run gate_seal.py above. For this call, copy
    # the binding document and rebind only that field to the authenticated
    # current source before invoking the original validator. The original
    # document is untouched and all six other nested helper checks remain live.
    # Exact frozen panel-inventory, tensor-multiset, truth-mapping, and plot-domain
    # aggregate hashes checked by preparation below prove canonical serialization
    # compatibility for these executed data. V26 selection hashes are direct ASCII
    # identities and are checked separately, not used as canonical-JSON proof.
    binding_module = v25.runtime_domain_binding_v3
    helper_validator = getattr(binding_module, "_validate_helper_sources", None)
    if not callable(helper_validator):
        raise DiagnosticError("V3 helper-source validator is unavailable")
    profile_validator = getattr(binding_module, "_validate_profile_sources", None)
    if not callable(profile_validator):
        raise DiagnosticError("V3 profile-source validator is unavailable")

    def authenticated_helper_sources(document: Mapping[str, Any]) -> None:
        _validate_with_canonical_source_rebinding(
            document,
            expected_historical_sha256=str(gate_seal_source["historical_sha256"]),
            authenticated_current_sha256=str(gate_seal_source["current_sha256"]),
            helper_validator=helper_validator,
        )

    def authenticated_profile_sources(profile: Any, repository_root: Path) -> None:
        _validate_with_renderer_source_rebinding(
            profile,
            repository_root,
            renderer_relative_path=Path("ml/synthetic/renderer.py"),
            expected_historical_sha256=str(renderer_source["historical_sha256"]),
            authenticated_current_sha256=str(renderer_source["current_sha256"]),
            profile_validator=profile_validator,
        )

    binding_module._validate_helper_sources = authenticated_helper_sources
    binding_module._validate_profile_sources = authenticated_profile_sources
    try:
        base = v25._prepare(root, v25._default_dependencies())
    finally:
        binding_module._validate_helper_sources = helper_validator
        binding_module._validate_profile_sources = profile_validator
    _, base_config_sha256, _ = v25._validate_candidate_config(
        v25.CONFIG_PATH, root, base.report
    )
    if base_config_sha256 != v26.BASE_V25_CONFIG_SHA256:
        raise DiagnosticError("reconstructed V25 configuration changed")
    coverage_names = {
        f"{scope}_{name}"
        for scope in ("component", "family")
        for name in (
            "scene_index",
            "proposal_index",
            "coordinates",
            "nearest_truth_index",
            "nearest_truth_distance_px",
            "nearest_truth_radius_px",
            "selected",
            "stratum_id",
            "retention_role_id",
        )
    }
    annulus_names = {
        f"{scope}_{name}"
        for scope in ("component", "family")
        for name in ("proposed_selected", "reserved", "added", "displaced")
    }
    coverage = _load_npz(root / COVERAGE_CACHE_PATH, coverage_names)
    annulus = _load_npz(root / ANNULUS_CACHE_PATH, annulus_names)
    values: dict[str, tuple[torch.Tensor, ...]] = {}
    evidence: dict[str, Any] = {}
    for scope, scenes, sampling, domain_limited in (
        ("component", base.component_train, base.component_values[5], False),
        ("family", base.family_train, base.family_values[5], True),
    ):
        rebuilt, observed = v26._rebuild_scope(
            scope,
            scenes,
            sampling,
            frozen["coverage_report"],
            coverage,
            annulus,
            domain_limited=domain_limited,
        )
        v26._validate_selection_evidence(
            observed, frozen["annulus_report"].get(f"{scope}_train"), scope
        )
        values[scope] = rebuilt
        evidence[scope] = observed
    v26._validate_rebuilt_counts(values["component"], values["family"])

    metadata: dict[str, dict[str, np.ndarray]] = {}
    for scope in ("component", "family"):
        selected = annulus[f"{scope}_proposed_selected"]
        row_count = int(selected.sum())
        labels = values[scope][1].detach().cpu().numpy() > 0.5
        distances = coverage[f"{scope}_nearest_truth_distance_px"][selected]
        if (
            row_count != EXPECTED_ROWS[scope]
            or len(labels) != row_count
            or int(labels.sum()) != EXPECTED_POSITIVES[scope]
            or not np.array_equal(labels, distances <= 3.0)
        ):
            raise DiagnosticError(f"{scope} selected-row labels or counts changed")
        metadata[scope] = {
            "scene_index": coverage[f"{scope}_scene_index"][selected],
            "proposal_index": coverage[f"{scope}_proposal_index"][selected],
            "nearest_truth_index": coverage[f"{scope}_nearest_truth_index"][selected],
            "nearest_truth_distance_px": distances,
            "nearest_truth_radius_px": coverage[f"{scope}_nearest_truth_radius_px"][selected],
            "labels": labels,
        }
    compatibility = {
        "nested_check": (
            "ml.markers.center.plot_domain_v25.runtime_domain_binding_v3."
            "_validate_helper_sources"
        ),
        "replacement_scope": (
            "single V25 model-free preparation call; copied binding document; "
            "canonical_json_source_sha256 field only"
        ),
        "original_binding_document_mutated": False,
        "other_nested_helper_checks_retained": 6,
        "historical_canonical_json_source_sha256": gate_seal_source[
            "historical_sha256"
        ],
        "current_canonical_json_source_sha256": gate_seal_source[
            "current_sha256"
        ],
        "canonical_serialization_compatibility_checks": [
            "exact frozen panel-inventory aggregate SHA-256",
            "exact frozen tensor-multiset aggregate SHA-256",
            "exact frozen truth-mapping aggregate SHA-256",
            "exact frozen plot-domain aggregate SHA-256 values",
        ],
        "selection_identity_serialization": (
            "direct ASCII; checked separately and not canonical-JSON compatibility proof"
        ),
        "renderer_profile_rebinding": {
            "path": "ml/synthetic/renderer.py",
            "scope": (
                "copied V3 generator profile passed to original source validator; "
                "renderer SHA-256 field only; both load and truth-join checks"
            ),
            "original_profile_mutated": False,
            "renderer_execution_scope": renderer_source["execution_scope"],
            "historical_sha256": renderer_source["historical_sha256"],
            "current_sha256": renderer_source["current_sha256"],
            "other_profile_source_checks_retained": 11,
            "executed_output_identity_checks": [
                "V3 train and dev regenerated source PNG SHA-256 values",
                "V25 component and family tensor-set identities",
                "V25 model-free preflight exact equality",
                "V26 regenerated proposal and selected-row identities",
            ],
        },
        "remaining_nested_validation": {
            "runtime_inputs_validate_candidate": (
                "authenticates the frozen candidate descriptor and runtime/model identities; "
                "does not compare generator source files"
            ),
            "load_split_v3": (
                "authenticates frozen manifest, report, panel-plane, and implementation "
                "identities, then regenerates and compares every train/dev source PNG"
            ),
        },
        "downstream_identity_checks": [
            "frozen V3 binding bytes",
            "component and family tensor-set identities",
            "V25 model-free preflight exact equality",
            "V26 regenerated proposal identities",
            "V26 frozen and proposed selection SHA-256 values",
            "V26 selected-row counts and labels",
        ],
    }
    return values, metadata, evidence, compatibility


def _summary(values: Sequence[float] | np.ndarray) -> dict[str, Any]:
    array = np.asarray(values, dtype=np.float64)
    if not len(array):
        return {"count": 0}
    return {
        "count": int(len(array)),
        "minimum": float(array.min()),
        "p05": float(np.quantile(array, 0.05)),
        "median": float(np.quantile(array, 0.5)),
        "p95": float(np.quantile(array, 0.95)),
        "maximum": float(array.max()),
    }


def _nearest_pair_metrics(
    patches: torch.Tensor,
    query_indices: Sequence[int] | np.ndarray,
    candidate_indices: Sequence[int] | np.ndarray,
    *,
    exclude_identity: bool,
    block_size: int,
    include_raw: bool = False,
) -> dict[str, Any]:
    queries_np = np.asarray(query_indices, dtype=np.int64)
    candidates_np = np.asarray(candidate_indices, dtype=np.int64)
    if not len(queries_np) or not len(candidates_np):
        empty = {
            "query_count": int(len(queries_np)),
            "matched_query_count": 0,
            "unmatched_query_count": int(len(queries_np)),
            "normalized_rmse": {"count": 0},
            "channel_max_absolute_difference": {
                name: {"count": 0} for name in CHANNEL_NAMES
            },
        }
        if include_raw:
            empty["_normalized_rmse_values"] = np.empty(0, dtype=np.float32)
            empty["_channel_max_values"] = np.empty((0, len(CHANNEL_NAMES)), dtype=np.float32)
        return empty
    flat = patches.reshape(len(patches), -1)
    element_count = int(flat.shape[1])
    best_squared = torch.full((len(queries_np),), float("inf"), dtype=torch.float32)
    best_candidate = torch.full((len(queries_np),), -1, dtype=torch.int64)
    with torch.inference_mode():
        for query_start in range(0, len(queries_np), block_size):
            query_end = min(query_start + block_size, len(queries_np))
            query_ids = torch.from_numpy(queries_np[query_start:query_end])
            query = flat.index_select(0, query_ids)
            local_best = torch.full((len(query),), float("inf"), dtype=torch.float32)
            local_index = torch.full((len(query),), -1, dtype=torch.int64)
            for candidate_start in range(0, len(candidates_np), block_size):
                candidate_end = min(candidate_start + block_size, len(candidates_np))
                candidate_ids = torch.from_numpy(candidates_np[candidate_start:candidate_end])
                candidate = flat.index_select(0, candidate_ids)
                squared = torch.cdist(
                    query,
                    candidate,
                    p=2.0,
                    compute_mode="donot_use_mm_for_euclid_dist",
                ).square_()
                if exclude_identity:
                    squared.masked_fill_(query_ids[:, None] == candidate_ids[None, :], float("inf"))
                candidate_best, candidate_position = squared.min(dim=1)
                improve = candidate_best < local_best
                local_best[improve] = candidate_best[improve]
                local_index[improve] = candidate_ids[candidate_position[improve]]
            best_squared[query_start:query_end] = local_best
            best_candidate[query_start:query_end] = local_index
    matched = best_candidate >= 0
    matched_queries = torch.from_numpy(queries_np)[matched]
    matched_candidates = best_candidate[matched]
    if bool(matched.any()):
        difference = torch.abs(
            patches.index_select(0, matched_queries)
            - patches.index_select(0, matched_candidates)
        )
        channel_max = difference.amax(dim=(2, 3)).detach().cpu().numpy()
        rmse = torch.sqrt(best_squared[matched] / element_count).detach().cpu().numpy()
    else:
        channel_max = np.empty((0, len(CHANNEL_NAMES)), dtype=np.float32)
        rmse = np.empty(0, dtype=np.float32)
    result = {
        "query_count": int(len(queries_np)),
        "matched_query_count": int(matched.sum()),
        "unmatched_query_count": int((~matched).sum()),
        "normalized_rmse": _summary(rmse),
        "channel_max_absolute_difference": {
            name: _summary(channel_max[:, channel])
            for channel, name in enumerate(CHANNEL_NAMES)
        },
    }
    if include_raw:
        result["_normalized_rmse_values"] = rmse
        result["_channel_max_values"] = channel_max
    return result


def _boundary_audit(
    patches: torch.Tensor,
    metadata: Mapping[str, np.ndarray],
    *,
    block_size: int,
) -> dict[str, Any]:
    labels = metadata["labels"]
    distances = metadata["nearest_truth_distance_px"]
    groups: dict[tuple[int, int], list[int]] = defaultdict(list)
    for row, (scene, truth) in enumerate(
        zip(metadata["scene_index"], metadata["nearest_truth_index"], strict=True)
    ):
        groups[(int(scene), int(truth))].append(row)
    output: dict[str, Any] = {}
    for name, lower, upper in ANNULI:
        aggregates: dict[str, list[dict[str, Any]]] = {
            "positive_nearest_opposite": [],
            "positive_nearest_same": [],
            "negative_nearest_opposite": [],
            "negative_nearest_same": [],
        }
        for group_rows in groups.values():
            rows = np.asarray(group_rows, dtype=np.int64)
            positive = rows[labels[rows]]
            negative = rows[(~labels[rows]) & (distances[rows] > lower) & (distances[rows] <= upper)]
            if not len(positive) and not len(negative):
                continue
            aggregates["positive_nearest_opposite"].append(
                _nearest_pair_metrics(
                    patches, positive, negative, exclude_identity=False,
                    block_size=block_size, include_raw=True,
                )
            )
            aggregates["positive_nearest_same"].append(
                _nearest_pair_metrics(
                    patches, positive, positive, exclude_identity=True,
                    block_size=block_size, include_raw=True,
                )
            )
            aggregates["negative_nearest_opposite"].append(
                _nearest_pair_metrics(
                    patches, negative, positive, exclude_identity=False,
                    block_size=block_size, include_raw=True,
                )
            )
            aggregates["negative_nearest_same"].append(
                _nearest_pair_metrics(
                    patches, negative, negative, exclude_identity=True,
                    block_size=block_size, include_raw=True,
                )
            )
        output[name] = {
            key: _merge_pair_metrics(parts) for key, parts in aggregates.items()
        }
    return output


def _merge_pair_metrics(parts: Iterable[Mapping[str, Any]]) -> dict[str, Any]:
    query_count = matched = unmatched = 0
    rmse_parts: list[np.ndarray] = []
    channel_parts: list[np.ndarray] = []
    for part in parts:
        query_count += int(part["query_count"])
        matched += int(part["matched_query_count"])
        unmatched += int(part["unmatched_query_count"])
        raw_rmse = np.asarray(part["_normalized_rmse_values"], dtype=np.float32)
        raw_channels = np.asarray(part["_channel_max_values"], dtype=np.float32)
        if len(raw_rmse):
            rmse_parts.append(raw_rmse)
            channel_parts.append(raw_channels)
    rmse = np.concatenate(rmse_parts) if rmse_parts else np.empty(0, dtype=np.float32)
    channels = (
        np.concatenate(channel_parts, axis=0)
        if channel_parts else np.empty((0, len(CHANNEL_NAMES)), dtype=np.float32)
    )
    return {
        "query_count": query_count,
        "matched_query_count": matched,
        "unmatched_query_count": unmatched,
        "normalized_rmse": _summary(rmse),
        "channel_max_absolute_difference": {
            name: _summary(channels[:, channel])
            for channel, name in enumerate(CHANNEL_NAMES)
        },
    }


def _score_partition(scores: np.ndarray, mask: np.ndarray, threshold: float) -> dict[str, Any]:
    selected = scores[mask]
    return {
        "count": int(len(selected)),
        "above_or_equal_threshold": int((selected >= threshold).sum()),
        "below_threshold": int((selected < threshold).sum()),
        "scores": _summary(selected),
    }


def _score_aggregates(
    scores: np.ndarray,
    labels: np.ndarray,
    distances: np.ndarray,
    threshold: float,
) -> dict[str, Any]:
    output = {
        "positive_le3": _score_partition(scores, labels, threshold),
        "negative_all": _score_partition(scores, ~labels, threshold),
        "negative_outside_annuli": _score_partition(
            scores, (~labels) & (distances > 12), threshold
        ),
    }
    for name, lower, upper in ANNULI:
        output[name] = _score_partition(
            scores, (~labels) & (distances > lower) & (distances <= upper), threshold
        )
    return output


def _checkpoint_forward(
    root: Path,
    values: Mapping[str, tuple[torch.Tensor, ...]],
    metadata: Mapping[str, Mapping[str, np.ndarray]],
    *,
    batch_size: int,
) -> tuple[dict[str, Any], dict[str, np.ndarray], int]:
    payload = torch.load(root / CHECKPOINT_PATH, map_location="cpu", weights_only=False)
    if not isinstance(payload, dict) or set(payload) != {"state_dict", "config"}:
        raise DiagnosticError("V26 checkpoint payload contract changed")
    model = v25.ScaleClassifierNet(v25.ModelConfig(seed=20260902))
    if payload["config"] != model.export_contract():
        raise DiagnosticError("V26 checkpoint model contract changed")
    model.load_state_dict(payload["state_dict"], strict=True)
    model.eval()
    scores: dict[str, np.ndarray] = {}
    batches = 0
    with torch.inference_mode():
        for scope in ("component", "family"):
            parts = []
            patches = values[scope][0]
            for start in range(0, len(patches), batch_size):
                output = model(patches[start:start + batch_size])
                parts.append(output[:, 0].detach().cpu())
                batches += 1
            scores[scope] = torch.cat(parts).numpy()
    threshold = float(v26.RECIPE["confidence_threshold"])
    report = {
        scope: _score_aggregates(
            scores[scope],
            metadata[scope]["labels"],
            metadata[scope]["nearest_truth_distance_px"],
            threshold,
        )
        for scope in ("component", "family")
    }
    combined_scores = np.concatenate((scores["component"], scores["family"]))
    combined_labels = np.concatenate(
        (metadata["component"]["labels"], metadata["family"]["labels"])
    )
    combined_distances = np.concatenate(
        (
            metadata["component"]["nearest_truth_distance_px"],
            metadata["family"]["nearest_truth_distance_px"],
        )
    )
    report["combined"] = _score_aggregates(
        combined_scores, combined_labels, combined_distances, threshold
    )
    report["combined"]["confusion"] = {
        "true_positive": int((combined_labels & (combined_scores >= threshold)).sum()),
        "false_negative": int((combined_labels & (combined_scores < threshold)).sum()),
        "false_positive": int(((~combined_labels) & (combined_scores >= threshold)).sum()),
        "true_negative": int(((~combined_labels) & (combined_scores < threshold)).sum()),
    }
    return report, scores, batches


def run(
    output: Path,
    source_sha256: str,
    *,
    root: Path = REPO_ROOT,
    elapsed_time_bound_seconds: float = 900.0,
    torch_threads: int = 1,
    batch_size: int = 512,
    pair_block_size: int = 128,
) -> dict[str, Any]:
    started = time.perf_counter()
    root = root.resolve()
    output = (output if output.is_absolute() else root / output).resolve()
    if root / "artifacts" not in output.parents or output.exists():
        raise DiagnosticError("output must be a new file below artifacts")
    if (
        elapsed_time_bound_seconds <= 0
        or torch_threads <= 0
        or batch_size <= 0
        or pair_block_size <= 0
    ):
        raise DiagnosticError("runtime bounds and batch sizes must be positive")
    if _sha256(Path(__file__)) != source_sha256:
        raise DiagnosticError("diagnostic source hash changed")
    torch.set_num_threads(torch_threads)
    authenticated_sources, _ = _authenticate_sources(root)
    frozen = _authenticate_frozen_inputs(root)
    _check_deadline(started, elapsed_time_bound_seconds, "authentication")
    values, metadata, selection_evidence, source_compatibility = _reconstruct_rows(
        root, frozen, authenticated_sources
    )
    _check_deadline(started, elapsed_time_bound_seconds, "selected-row reconstruction")
    boundary = {
        scope: _boundary_audit(
            values[scope][0], metadata[scope], block_size=pair_block_size
        )
        for scope in ("component", "family")
    }
    _check_deadline(started, elapsed_time_bound_seconds, "patch boundary comparison")
    score_report, _, forward_batches = _checkpoint_forward(
        root, values, metadata, batch_size=batch_size
    )
    _check_deadline(started, elapsed_time_bound_seconds, "checkpoint forward pass")
    row_inventory = {
        scope: {
            "selected_rows": len(values[scope][1]),
            "positive_rows": int(metadata[scope]["labels"].sum()),
            "negative_rows": int((~metadata[scope]["labels"]).sum()),
            "selection_evidence": selection_evidence[scope],
        }
        for scope in ("component", "family")
    }
    row_inventory["combined"] = {
        "selected_rows": sum(
            len(values[scope][1]) for scope in ("component", "family")
        ),
        "positive_rows": sum(
            int(metadata[scope]["labels"].sum())
            for scope in ("component", "family")
        ),
        "negative_rows": sum(
            int((~metadata[scope]["labels"]).sum())
            for scope in ("component", "family")
        ),
    }
    report = {
        "schema": "graphreader.marker-v26-confidence-boundary-diagnostic.v1",
        "scope": "frozen-v26-selected-synthetic-train-confidence-boundary",
        "diagnostic_source_sha256": source_sha256,
        "authenticated_sources": authenticated_sources,
        "historical_source_compatibility": source_compatibility,
        "inputs": [
            {"path": path.as_posix(), "sha256": digest}
            for path, digest in (
                (RESULT_PATH, RESULT_SHA256),
                (CONFIG_PATH, CONFIG_SHA256),
                (CANDIDATE_REPORT_PATH, CANDIDATE_REPORT_SHA256),
                (CHECKPOINT_PATH, CHECKPOINT_SHA256),
                (SNAPSHOT_PATH, SNAPSHOT_SHA256),
                (BASE_V25_CONFIG_PATH, BASE_V25_CONFIG_SHA256),
                (BASE_V25_RESULT_PATH, BASE_V25_RESULT_SHA256),
                (v25.protocol.COMPONENT_CONFIG_PATH, v25.protocol.COMPONENT_CONFIG_SHA256),
                (v25.protocol.FAMILY_BINDING_PATH, v25.protocol.FAMILY_BINDING_SHA256),
                (v25.protocol.DEV_PROTOCOL_PATH, v25.protocol.DEV_PROTOCOL_SHA256),
                (COVERAGE_REPORT_PATH, COVERAGE_REPORT_SHA256),
                (COVERAGE_CACHE_PATH, COVERAGE_CACHE_SHA256),
                (ANNULUS_REPORT_PATH, ANNULUS_REPORT_SHA256),
                (ANNULUS_CACHE_PATH, ANNULUS_CACHE_SHA256),
            )
        ],
        "row_inventory": row_inventory,
        "patch_distance_definition": {
            "nearest_by": "root_mean_square_difference_over_float32_3x33x33_patch",
            "same_truth_only": True,
            "same_scene_only": True,
            "same_label_excludes_self": True,
            "channel_max_absolute_difference": list(CHANNEL_NAMES),
            "annuli": [
                {"name": name, "lower_exclusive_px": lower, "upper_inclusive_px": upper}
                for name, lower, upper in ANNULI
            ],
            "pairwise_workspace": "blocked; no global all-pairs matrix",
            "pair_block_size": pair_block_size,
        },
        "patch_boundary": boundary,
        "train_confidence": score_report,
        "confidence_threshold": float(v26.RECIPE["confidence_threshold"]),
        "checkpoint_forward_passes": 1,
        "checkpoint_forward_batches": forward_batches,
        "optimizer_steps": 0,
        "thresholds_selected": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approved": False,
        "elapsed_time_bound_seconds": elapsed_time_bound_seconds,
        "elapsed_ms": round((time.perf_counter() - started) * 1000, 3),
        "limitations": [
            "Synthetic train only; this is not dev, sealed, private, or production acceptance.",
            (
                "Historical preparation reconstructs and authenticates synthetic train and dev "
                "runtime objects; reported patch metrics and checkpoint inference use train only."
            ),
            (
                "The current renderer executes during family source regeneration; unchanged "
                "frozen PNG, tensor, proposal, and selection identities establish compatibility "
                "for these reconstructed inputs only."
            ),
            "Patch distances and train confusion diagnose compatibility and fit but do not establish causation.",
            "No distance cutoff, confidence threshold, sampler, objective, or candidate is selected.",
            "Patch-distance distributions emit no case identities or patches.",
        ],
    }
    if _sha256(Path(__file__)) != source_sha256:
        raise DiagnosticError("diagnostic source changed during execution")
    output.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(report, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"
    with output.open("xb") as stream:
        stream.write(payload)
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--max-elapsed-seconds", type=float, default=900.0)
    parser.add_argument("--torch-threads", type=int, default=1)
    parser.add_argument("--batch-size", type=int, default=512)
    parser.add_argument("--pair-block-size", type=int, default=128)
    arguments = parser.parse_args()
    report = run(
        arguments.output,
        arguments.source_sha,
        elapsed_time_bound_seconds=arguments.max_elapsed_seconds,
        torch_threads=arguments.torch_threads,
        batch_size=arguments.batch_size,
        pair_block_size=arguments.pair_block_size,
    )
    print(json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    main()
