<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Review completed panels after interrupted detection

A batch can complete and retain one panel before cancellation or a later
panel's calibration failure. The workspace previously restored its earlier
surface without marking that newly completed panel ready for review.

The view model now compares retained panel checkpoints with the preceding
automatic run. Newly completed panels are marked reviewable only when their
project, panel, source, and image identities match an open tab. Their stale
edit histories are cleared; unchanged panels from the prior run keep their
existing state and history. If a failure selects another panel, the displayed
surface follows that panel's own review state. This does not convert a failed
panel into a successful detection or change any calibration or export gate.

Four STA application regressions cover cancellation and calibration failure,
with and without a retained panel from a prior run. Before the fix, both
interruption cases left the new checkpoint in the ready surface instead of
review. Afterward, `dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj
-c Release --no-restore -m:1 -p:UseSharedCompilation=false` passed 783 tests
with no skips. These are in-process tests, not packaged visual or clean-machine
acceptance.
