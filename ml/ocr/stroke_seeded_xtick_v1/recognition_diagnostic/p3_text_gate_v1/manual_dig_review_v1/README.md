<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Manual training calibration review

This local inspection selects eight source groups from the build partition of
the authenticated train-only marker overlay plan and reopens only those
catalogued projects. It extracts their embedded images and three calibration
anchors each to ignored project artifacts. It reads no development or sealed
payloads and performs no model training. Exact image bytes and source identities
are checked before use.

Visual review found that calibration anchors can lie on data points or
unlabeled positions; an anchor value is therefore not automatically a printed
text label. From these eight build-group images, an assistant visually checked
76 numeric axis-label boxes and transcriptions for a small private training
pilot. Those annotations are incomplete, are not independently adjudicated, and
are not acceptance evidence. Their source images, coordinates, text, and
case-level data are not tracked. The downstream reader comparison is recorded
in `tools/GraphReader.ManualAxisOcrDiagnostic/README.md`.

The train-only source catalog SHA-256 is
`a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57`;
the build-group plan is
`34431350ed5e94dce0b2debd481b855ed673fdb7839d669527db397a48b64428`.
The existing train-capture assembly was pinned at
`882cd78c3a4a94ef64bab3e6da4aa0ddf917a023f6f5453980428a9ba201ccca`.
The tool's Release build completed with zero warnings and errors, and its run
reported eight selected build groups and 24 anchors.
