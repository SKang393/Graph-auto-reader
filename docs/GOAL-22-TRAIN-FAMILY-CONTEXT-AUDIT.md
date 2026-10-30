<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Train-only marker context audit

The audit joins saved V37 predictions and attribution to the bound runtime OCR
and plot geometry for all 28 synthetic family training panels. It performed no
new inference or optimizer steps and read no development, private, or sealed
split. The prior truth matching supplies diagnostic labels only; runtime
context comes from the saved OCR and geometry outputs.

| Saved family predictions | Count | Inside a runtime OCR polygon | Inside the runtime plot |
| --- | ---: | ---: | ---: |
| Extra | 119 | 21 | 119 |
| Matched | 468 | 72 | 468 |

Of the 119 extras, 45 were selected negative proposals and 74 were unselected
negative proposals in the original identity-view selection. V37 also used
reflection views. These counts do not prove missing training coverage or a
causal defect. OCR containment overlaps correctly matched points and is not a
safe exclusion rule. Dedicated runtime legend-frame and glyph classifications
are unavailable for these reports; the 133 component-training extras also lack
matching runtime OCR evidence. No new candidate or acceptance claim follows.

Executed with the project-local `Run-GpuPython.ps1` launcher and
`artifacts/goal22-tools/marker-provenance-audit-20261030/audit.py` as the Python
argument. Script SHA-256:
`2c055753d15c41feeba78072c302080f9d20dbbbc1e310ef452bd3488ec666e1`.
Report SHA-256:
`5a00bf425a7ecfdc6fffd2f73e94992e6a7e682e4a9781ccdb1a9cadac50af9d`.
The audit checked five runtime report hashes, 56 family arrays, train panel
joins, the fixed prediction identities, and the V25 training binding. The
temporary script and report remain project-local and may be retired after
their active consumer finishes, subject to the current deletion controls.
