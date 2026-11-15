<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Antialiased grid control

An owned Gray8 fixture expanded the existing grid ink into two-pixel `127/127`
bands and retained three outward ticks on each axis. The unique-grid test
required axis endpoints within one original pixel of `(20,100,115,10)` and
exactly three tick positions within one pixel of X `[30,70,110]` and Y
`[20,50,80]`. A second control required two such grids to remain ambiguous.

```powershell
cargo test --offline --manifest-path native/graphreader-engine/Cargo.toml --target-dir artifacts/native-engine antialiased_ -- --nocapture
# 3 passed, 0 failed, 51 filtered; test duration 0.02 s, wall 3.946 s.
```

The filtered run included the two new grid controls and the existing
antialiased-axis control. This hypothesis did not reproduce a defect; no
production code, threshold, or private data was changed or used.
