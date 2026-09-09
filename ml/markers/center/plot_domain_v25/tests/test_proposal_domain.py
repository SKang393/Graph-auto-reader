# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.center.line_aware_v1.pipeline import ProposalBatch
from ml.markers.center.mask_preserving_v24 import mask_preserving as v24
from ml.markers.center.plot_domain_v25.proposal_domain import (
    PlotDomain,
    extract_proposals_in_domain,
    postprocess_in_domain,
)


def test_runtime_predicate_keeps_polygon_and_sixteen_pixel_boundary_only() -> None:
    domain = PlotDomain.runtime_plot(
        100,
        80,
        ((30, 60), (70, 60), (70, 20), (30, 20)),
        identity="axis-evidence",
    )

    assert domain.supports_proposal(50, 40)
    assert domain.supports_proposal(14, 40)
    assert not domain.supports_proposal(13.99, 40)
    assert not domain.supports_proposal(5, 5)
    assert domain.contains(50, 40)
    assert domain.contains(30, 40)
    assert not domain.contains(70, 40)  # C# ray crossing includes one edge and excludes the other.


def test_runtime_extraction_filters_before_patch_allocation_and_matches_v24_values(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    tensor = torch.zeros((3, 72, 112), dtype=torch.float32)
    tensor[0] = 0.2
    tensor[1, 5:20, 7:18] = 0.75
    tensor[2, 31:55, 70:100] = 0.5
    reference = v24.extract_proposals(tensor)
    domain = PlotDomain.runtime_plot(
        112,
        72,
        ((40, 56), (76, 56), (76, 20), (40, 20)),
        identity="axis-evidence",
    )
    expected_indices = tuple(
        index
        for index, (x, y) in enumerate(reference.coordinates.tolist())
        if domain.supports_proposal(x, y)
    )
    expected = torch.tensor(expected_indices, dtype=torch.long)

    monkeypatch.setattr(
        v24,
        "extract_proposals",
        lambda _: pytest.fail("runtime extraction must filter before V24 unfolds all patches"),
    )
    actual = extract_proposals_in_domain(tensor, domain)

    assert actual.retained_ink_indices == expected_indices
    assert actual.ink_supported_count == len(reference.patches)
    assert actual.omitted_by_domain_count == len(reference.patches) - len(expected_indices)
    assert torch.equal(actual.proposals.coordinates, reference.coordinates.index_select(0, expected))
    assert torch.equal(actual.proposals.patches, reference.patches.index_select(0, expected))


def test_component_fixture_preserves_exact_v24_proposals_and_postprocess() -> None:
    tensor = torch.zeros((3, 36, 44), dtype=torch.float32)
    tensor[0, 8:29, 7:37] = 0.6
    scene = SimpleNamespace(tensor=tensor)
    domain = PlotDomain.component_full_canvas(44, 36, identity="component-1")
    expected_proposals = v24.extract_proposals(tensor)
    actual_proposals = extract_proposals_in_domain(tensor, domain)
    output = np.zeros((len(expected_proposals.patches), 4), dtype=np.float32)
    output[:, 0] = np.linspace(0.1, 0.9, len(output), dtype=np.float32)
    output[:, 3] = 4.0

    expected_predictions = v24.postprocess(scene, expected_proposals, output)
    actual = postprocess_in_domain(scene, actual_proposals.proposals, output, domain)

    assert torch.equal(actual_proposals.proposals.patches, expected_proposals.patches)
    assert torch.equal(actual_proposals.proposals.coordinates, expected_proposals.coordinates)
    assert actual.predictions == expected_predictions
    assert actual.consensus_rejected is None
    assert actual.nms_suppressed is None


def test_runtime_postprocess_rejects_decoded_center_before_consensus_and_nms() -> None:
    tensor = torch.ones((3, 80, 100), dtype=torch.float32)
    scene = SimpleNamespace(tensor=tensor)
    domain = PlotDomain.runtime_plot(
        100,
        80,
        ((30, 60), (70, 60), (70, 20), (30, 20)),
        identity="axis-evidence",
    )
    proposals = ProposalBatch(
        torch.zeros((2, 3, 33, 33), dtype=torch.float32),
        torch.tensor(((40, 40), (76, 40)), dtype=torch.float32),
    )
    output = np.asarray(((0.9, 0, 0, 4), (0.8, 0, 0, 4)), dtype=np.float32)

    result = postprocess_in_domain(scene, proposals, output, domain)

    assert [(item.x, item.y) for item in result.predictions] == [(40.0, 40.0)]
    assert result.outputs_above_threshold == 2
    assert result.decoded_outside_plot == 1
    assert result.consensus_rejected == 0
    assert result.nms_suppressed == 0


def test_runtime_postprocess_rejects_a_proposal_from_outside_bound_domain() -> None:
    tensor = torch.ones((3, 80, 100), dtype=torch.float32)
    domain = PlotDomain.runtime_plot(
        100, 80, ((30, 60), (70, 60), (70, 20), (30, 20)), identity="axis-evidence"
    )
    proposals = ProposalBatch(
        torch.zeros((1, 3, 33, 33), dtype=torch.float32),
        torch.tensor(((4, 4),), dtype=torch.float32),
    )
    with pytest.raises(ValueError, match="outside its bound domain"):
        postprocess_in_domain(
            SimpleNamespace(tensor=tensor),
            proposals,
            np.asarray(((0.9, 0, 0, 4),), dtype=np.float32),
            domain,
        )


@pytest.mark.parametrize(
    "polygon",
    [
        ((0, 0), (1, 1), (0, 1), (1, 0)),
        ((-1, 0), (1, 0), (1, 1), (0, 1)),
        ((0, 0), (float("nan"), 0), (1, 1), (0, 1)),
    ],
)
def test_invalid_runtime_polygon_fails_closed(polygon) -> None:
    with pytest.raises(ValueError):
        PlotDomain.runtime_plot(10, 10, polygon, identity="invalid")
