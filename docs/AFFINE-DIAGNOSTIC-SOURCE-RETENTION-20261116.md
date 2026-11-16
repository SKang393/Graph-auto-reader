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
added.

Source/outcome commit `9eb9ba95f23fc75a7e01d89cb4e847fafd65de28` is integrated
and pushed to `origin/main`. Exact-head
[Windows CI 43846734054](https://github.com/SKang393/Graph-auto-reader/actions/runs/43846734054),
job `133228661798`, passed with `full_product_validation=true` and
`scope_reason=full_path_status`. It ran **3,258 passing .NET tests**, **30
explicit skips**, **85 passing Rust tests** and **68 passing packaging checks**,
with zero failures. Version policy passed 62, portable retention 12, ledger
generation six and localization self-tests nine. Both release jobs were skipped.
The job log spans `2026-11-16T10:12:30.5771482Z` to `10:24:54.9135485Z`.
No application package or workflow retry was created.

After the source/outcome push and full CI, the completed
`artifacts/affine-diagnostic-source-retention-check` and probe `bin`/`obj` were
retired at **2026-11-16T10:27:28.9595830Z**: **88 files / 97,004,841 bytes**.
Exact canonical scope, file hashes/counts, no reparse/tracked files and inactive
consumers were verified before deletion. All targets were absent afterward;
all **27 protected file hashes** and the sole **0.4.58 ZIP** were unchanged.
Tiny retirement receipts remain only until this closeout record is pushed.
Original data, current runtime assets and the protected correction checkpoint
remain intact.
The separate [REV-013](REVIEW_QUEUE.md#rev-013---name-the-next-original-development-graph)
waits for an exact original input identity for a new changed-stage confirmation;
no retired private report or twelve-case search was reconstructed.
