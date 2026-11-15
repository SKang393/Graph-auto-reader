# Interval label cluster containment

Base: `1fe50b58b07ef9be7d23f8252016ad86ce7ee32`.

## Change

The prior splitter abandoned an entire merged X-label row when one neighboring
gap was ambiguous, even when at least three other interval groups were
independently separable. The detector now groups measured intervals into
clusters using the existing strict gap rule. A boundary is split only when its
gap is greater than the larger adjacent component width; component containment,
vertical overlap, crossing, and tick-position checks remain unchanged.

Safe singleton clusters are emitted with `x_label_between_measured_ticks`
provenance. Adjacent ambiguous intervals remain merged and are marked
`x_label_spans_measured_tick_intervals`. If fewer than three safe singleton
clusters remain, the original full row is preserved and marked ambiguous. No
ambiguous text is promoted into numeric calibration evidence.

The proposal layer treats this provenance as review-only: ambiguous rows add an
association warning and are excluded from fitting. Interval-label positions
remain estimates derived from the label polygon; they are not snapped to an
edge tick. The UI keeps the estimated-review warning and explicit review/apply
flow.

## Verification

The owned OCR test sequence exposed two issues before the final run:

- The initial regression test did not compile because of CA1861 on a constant
  array argument. The fixture was changed to use a static readonly array.
- The first full OCR run after splitting passed 928 tests, skipped 16, and had
  one failure: whole-row fallback lacked the ambiguity flag. That fallback was
  corrected, and a case with an ambiguous pair plus only two safe singleton
  groups was added.

Final OCR suite: 930 passed, 16 asset skips, 0 failed; test duration 7 seconds,
wall time 22.3020657 seconds. TRX SHA-256:
`b398f8525b7010b43c41f813a586e8b650f84d84af46eca3a40b55d4f16066b3`.

The focused cases cover mixed safe and ambiguous clusters, fewer than three
safe groups, an all-ambiguous run, crossing and adjacency guards, invalid
transform, ordinary tick-centered labels, and unchanged source pixels/component
accounting. The tracked implementation is in
`src/GraphReader.Ocr/ConnectedComponentTextRegionDetector.cs`; proposal
validation is in `src/GraphReader.App/Services/CalibrationTickProposal.cs`.

## Status and limits

The first integrated App build passed with zero warnings/errors in 61.328713
seconds wall. Its suite reported 1,217 passed / one failed / zero skipped in
49.3700143 seconds. The new non-axis-row fixture used an empty `SuggestedAxis`,
which the existing worker response contract rejects before rows appear. The
actual worker filters candidates to X/Y geometry before recognition. The fixture
was corrected to a valid below-axis X polygon with `Role=Other` and
`SuggestedAxis=X`; no worker validation was relaxed. Failed TRX SHA-256:
`9b2e4a0a1f070ed34d4854974591fd8cc7dcd402c5a9f22b7d8e59eb22103211`.
The warning key was also registered for localization audit. Retry build passed
with zero warnings/errors in 34.9951665 seconds. The full App suite then passed
**1,218 tests**, zero skips/failures, 46 seconds test time / **48.9795396 seconds**
wall. TRX SHA-256:
`d7637cc1c82c2e6b47c10af6dd4a57c3bc067cf786bcfc2ecdf5d344623fb154`.
The tests cover valid-looking numeric ambiguity, duplicate identities in either
row order, correction preservation, explicit exclusion/refit, visible guidance,
disabled value-copy commands and fresh review before Apply. The product remains
English-only; a requested Korean culture uses its existing English fallback.

A read-only review of the saved prior trace's other seven OCR panels found no
additional coordinate convention defect. Among 117 rows / 72 numeric readings /
63 associations, nine literal rows exceeded the existing font-distance bound;
eight were tiny fragments. One duplicate group contained three separate rows
competing for one measured tick. There were no nearest-tick ties, nonzero crop
offsets or wrong-axis mappings in that inspected subset. Cached geometry does
not establish which fragment is intended or justify moving, merging or dropping
it. Correction, explicit exclusion and refit remain the supported review path.
No inference or private source read was repeated for this inspection.

The separate probe-only build passed with zero warnings/errors in 8.4256455
seconds. All 20 runtime libraries matched the frozen App. Its owned profile
self-test passed in 0.5275101 seconds; missing arguments produced the expected
sanitized refusal, exit 1, in 0.2255122 seconds.

## Fixed development confirmation

One `--real-dev-interval-cluster-trace` run completed from
`2026-11-15T19:53:13.9133285Z` to `2026-11-15T19:53:55.0742307Z`, exit 0,
empty stderr, **41.1520687 seconds** wall. All 12 sources/panels finished:
11 prepared panels and one genuine no-panel fallback. Eight reached OCR and
four lacked geometry, unchanged from the saved interval-label baseline. The
three pair-budget and one rotated-geometry refusals remain visible. There were
zero timeouts, exceptions, unsupported, unfinished or unattempted panels.

Exactly one OCR panel changed; the other seven OCR outputs and all geometry
matched the saved baseline. A compound row became **15 numeric label proposals
at estimated positions plus two explicitly ambiguous clusters**. Every new
numeric proposal was associated as estimated; neither ambiguous cluster was
numeric or associated in this run. The owned valid-looking-number test separately
proves ambiguity cannot bypass the guard. These counts are proposal coverage,
not recognition accuracy or verified scientific values.

| Stage | Saved interval baseline | Cluster containment |
| --- | ---: | ---: |
| OCR regions | 128 | 144 |
| Literal numeric proposals | 79 | 94 |
| Associated proposals | 70 | 85 |
| Estimated associations | 5 | 20 |
| Eligible interval proposals | 0 | 15 |
| Explicit ambiguous clusters | 0 | 2 |
| Complete session drafts | 0 | 0 |
| Printed-X drafts | 2 | 2 |
| Partial X / Y drafts | 0 / 3 | 0 / 3 |

The remaining ambiguity blocks automatic X fitting until reviewed and explicitly
excluded or replaced through manual calibration. No row was silently dropped,
and no calibration was applied or exported. Exact-box measurement was attempted
once, bound 20 X / 7 Y ticks in 70.5563 ms, and retained existing review warnings.

| Stage time, milliseconds | Saved baseline | Current |
| --- | ---: | ---: |
| Preparation total | 2,644.010 | 1,906.3184 |
| Axis total | 15,913.8041 | 11,787.1151 |
| OCR total | 27,368.2276 | 26,189.6175 |
| Association total | 89.6767 | 102.4566 |

Current OCR median was 3,090.32245 ms, maximum 7,277.3662 ms. Single runs contain
scheduling and initialization differences; this is not a general latency claim.
No separate private rerun was performed for the grid optimization.

| Identity | SHA-256 |
| --- | --- |
| App EXE | `e21d8dd49fb85c8d5563c9199c00286d28e1269a13a2db3b4533494c9a478b4e` |
| App DLL | `7c8df79baaaad3a4b3992e9b97d4fb8843cd9eca494086a8ee9ac39a8d1f056b` |
| Probe DLL | `3c7fbcb3117fdc97d505303da79f2bbf3a6607880ba18d8a50e253e7867030de` |
| Runtime pin manifest | `60007b67f65b98a51ee1710234cc71a1b421c519cd553f237e21ab3d31131f04` |
| Current report, 80,485 bytes | `fd4ffedb8a462825995556b8b3ee3059f9facd8f829d53fad578107df1900ba5` |
| Saved baseline | `ca6261ffd33061661a0c8e150861cc7ba76c324d37c5b1a39ee794869ce18755` |

The separate tracked profile pins Rust, the 20 runtime libraries, and the
unchanged reviewed OCR model/manifest. Assignment
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`,
selected inventory
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`, and
source digest
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`
matched the baseline. The 120-development / 51-reserved assignment was preserved;
earlier inventory exposure is not relabeled as an audited pristine holdout.
Sealed reads, truth scoring, training, image writes and paid calls were zero.
The full trace uses the existing materializer, not the image-only property reader.

No model, threshold, dependency, package, or frozen contract was changed. The
owned test artifacts under
`artifacts/interval-cluster-containment-check` remain active until integrated
validation and its outcome record are complete. Full Windows
[CI 43755505445](https://github.com/SKang393/Graph-auto-reader/actions/runs/43755505445)
for integrated head `991b3f3031ac315e8b93692a15de30fcebaa0b29` passed all
13 .NET suites: 3,081 passed, 30 skipped, zero failed, including all 1,218 App
tests and 930 OCR tests. Rust passed 72 tests; all applicable policy and
packaging checks passed. Public release jobs were skipped. Native UI acceptance
is pending. The current delivered build remains 0.4.54. No production accuracy
or automatic-calibration completion is claimed.

## Completed-output retirement

After this outcome and the grid subset outcome reached `origin/main`, the following
completed generated outputs were removed. Their named comparison and
component-diagnosis consumers have finished. Historical results remain in their
pushed outcome documents; originals, active runtime assets and the current build
are outside this scope.

| Local artifact directory | Files | Bytes |
| --- | ---: | ---: |
| `interval-label-grouping-check` | 5 | 3,116,732 |
| `regular-grid-finalization-check` | 5 | 666,319 |
| `native-search-capacity-check` | 3 | 12,680 |
| `grid-subset-completion-check` | 3 | 344,052 |
| `cached-x-row-properties` | 8 | 7,478 |
| `interval-label-dev-trace` | 7 | 70,349 |
| `native-raster-endpoint-dev-trace` | 3 | 65,785 |
| `native-endpoint-owned-check` | 9 | 20,676 |

Total: **43 files / 4,304,071 bytes**, all under this worktree's `artifacts/`.
Guarded retirement completed at `2026-11-15T20:01:19.4766273Z`, after outcome
`29076d7b` reached remote head `991b3f3031ac315e8b93692a15de30fcebaa0b29`.
Result hashes, exact inventories, resolved contained paths, zero reparse points
and no active task runtime consumers were rechecked. All eight paths were absent
after removal.
The new cluster report/test directory remains active through this batch's final
integration/CI review, then follows the same pushed-record retirement rule.
