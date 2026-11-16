<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Retire completed report-bound affine diagnostics

The original-support integration exposed five obsolete diagnostic callers of a
changed working-frame return value. Its recorded compile errors were repaired,
but completed exact-report consumers should not continue to add maintenance
work after their input reports and active consumers have been retired.

From base `a32388ff6e3dbbd528209213ad5718618224bf22`, remove only the completed
stroke-mask, clipped-source-support and original-support-worker entrypoints,
their dispatch/deadline branches and obsolete imports/constants. The clipped
consumer's otherwise unreferenced source file is removed. This deletes 479
lines and adds eight documentation/dispatch lines across five probe files.
Their historical source remains at `d26c6e42`; measured outcomes remain in the
[stroke record](AFFINE-STROKE-SAMPLING-20261116.md) and
[original-support record](AFFINE-ORIGINAL-SUPPORT-20261116.md).

Reference searches and independent review found no surviving code consumers.
Shared stroke connectivity, span replay, original support, reusable owned
sampling/interior controls and every CI self-test remain. Removed CLI flags
reach the existing generic usage error before request-file reads or inference.
Application code, worker protocols, models, dependencies and packaged 0.4.58
are unchanged. No private or sealed input was read.

One probe-only build passed with **zero errors/warnings**, **10.4995 s**, after
a **3.4082 s** restore using the existing dependency cache. The three existing
owned self-tests each ran once with exit zero, no timeout and empty stderr:

| Mode suffix | Evidence | Wall time |
| --- | --- | ---: |
| `tick-refusal-self-test` | selection, strict wire and historical-mode guards | 0.5633 s |
| `span-properties-self-test` | seven owned controls, cancellation, immutability and metadata guard | 0.3879 s |
| `source-support-self-test` | five owned controls, cancellation, immutability and tamper guard | 0.2977 s |

All use the `--real-dev-affine-` prefix but reported **zero private/source
payload reads and zero worker calls**. No new tests or private comparisons were
added. Full source CI remains the integration gate.

Keep `artifacts/affine-diagnostic-source-retention-check` and probe `bin`/`obj`
only for this active integration/CI review; retire them after the source and
concise outcome are pushed and consumers finish. Preserve original data, current
runtime assets, the sole packaged build and the protected correction checkpoint.
The separate [REV-013](REVIEW_QUEUE.md#rev-013---name-the-next-original-development-graph)
waits for an exact original input identity for a new changed-stage confirmation;
no retired private report or twelve-case search was reconstructed.
