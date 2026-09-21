<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Correct confidence in alternative OCR readings

The decoder could give an almost unsupported character high confidence by
averaging it with correctly recognized surrounding characters. A replacement
with probability 0.000001 received confidence 0.875 in an eight-character
reading and 0.969 in a 32-character reading. Even a zero-probability deletion
could receive confidence 0.99.

Alternative confidence now also reflects the second/best probability ratio at
the changed timestep. Unsupported replacements, insertions and deletions stay
near 0.000001 in the executable regression probe. Zero-probability alternatives
are omitted. Equal-support alternatives retain the existing 0.99 cap below the
primary reading. This is support for the single changed CTC path, not a summed
sequence probability. The result-cache identity includes the decoder revision
and alternative budget, so old recognized results cannot hide the repair.

Eight added regressions cover short and long strings, insertions, deletions,
impossible classes, equal-support ordering and cache identity. Final checks
pass 447 OCR tests and 478 App tests; 16 optional OCR tests are skipped. The
native runner builds with zero warnings and errors. Tests and build take
106.78 seconds. An earlier checked version is archived separately because
review caught the equal-support ordering edge case before final verification.

The actual application runs both unchanged open development fixtures with
unchanged weights and thresholds. Every observed model uses CPU.

| Check | Before and after |
| --- | ---: |
| Broader native marker matches | 569 / 838 |
| Extra / missing native markers | 17 / 269 |
| Exact native text readings | 337 / 453 |
| Axis corners within two pixels | 84 / 93 |
| Full-workflow successful / blocked sources | 12 / 11 |
| Correct unique exported values | 403 / 706 |
| Correct relational export rows | 399 / 724 |
| Wrong-scale / failed-source residual rows | 0 / 0 |

All prior native marker matches remain. Primary text, roles and geometry remain
unchanged; alternatives change in 487/499 native and 825/842 workflow regions.
Downstream region confidence changes in 19 native and six workflow regions.
Six native and two workflow calibration records also change beyond timing,
including confidence-weighted fits and one session-assignment result. All
eight affected panels remain blocked for review. One already failed source
has fewer calibration error reasons but remains failed. Every exported numeric
value and source completion state is unchanged. Do not describe all internal
observations as identical.

Native verification takes 130.15 seconds; the full CSV check takes 148.69
seconds. Both return the expected nonzero accuracy-gate result. The repair
passes its regression checks, but product accuracy remains below the shared
bars. Numeric corrections still require independent unchanged anchors and
explicit review before export.

The [evidence index](GOAL-22-CTC-CONFIDENCE-REPAIR.json) binds final sources,
compiled runtime, probes, tests and workflow reports. Predecessor af5c66f
passes CI 35621502596. No new license, dependency, model, training, private or
sealed read, promotion, package or release is involved. Build 433 remains
0.4.33. Continue the remaining reading and marker accuracy work. All four
Goal 22 outcomes remain incomplete.
