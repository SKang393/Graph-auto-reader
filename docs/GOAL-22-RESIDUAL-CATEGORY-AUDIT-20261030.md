<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Train-only residual artifact category audit

The aggregate audit replayed the registered candidate proposal path on all 102
authorized training image groups. It retained no source images, per-case output,
or inference cache. It performed no optimizer steps or sealed reads. The
development groups were not evaluated. This is diagnostic evidence, not a
production approval.

Of 1,743 labeled centers, 1,262 had a nearby proposal. Among 481 unmatched
centers, 415 were attributed only to artifact masks, 57 only to text masks,
and 9 to mixed causes. Within the 415 artifact-only centers, 152 encountered
geometry-seed masks only, 252 residual masks only, and 11 both. Of the 252
residual-only centers, 188 encountered intersection masks and 64 legend
masks; none encountered only arrow, bracket, or multiple residual categories.

These counts identify where to investigate. They do not establish that a mask
is wrong, authorize relaxing thresholds, or measure development accuracy.

- Aggregate report: `artifacts/engauge-residual-categories-20261030/report.json`
- Report SHA-256: `51dddf464f5a97076ab8bd6306beead308501c93b6f25d27d0e4470d2871b1a9`
- Candidate binding SHA-256: `c5b5a60192f88da211325b0bbe750a0f0861ddcdb4612e288ec89e4324616229`
