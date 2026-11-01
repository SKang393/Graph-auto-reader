<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Outlying Y-tick lane review safeguard

The application now requests review when an unreviewed numeric Y-tick region
is substantially farther from the selected Y axis than the median of at least
three valid numeric Y-tick regions on that panel. Distance is normalized by each
region's height. The region and its reading remain available; an explicitly
accepted reading bypasses this warning. A real label can also be distant, so
the signal does not discard text or establish correctness. With fewer than
three usable readings, it provides no lane comparison.

The three detector-emitted outliers previously reviewed as vertical-text
fragments were all flagged on the repeated 20-image candidate run. Two already
had region-specific review warnings; the third gained one. Both affected panels
already needed axis review. All 21 panels kept their prior status, other
failure reasons, marker-center observations, and accepted markers. All 20
images still failed calibration. The incomplete recorded-point comparison
remained 250 center matches and 207 accepted-marker matches out of 332.

Two workflow and geometry regressions failed before the change and passed
afterward. The final application suite passed 769 tests; the integration suite
passed 170 with seven skipped. Seven focused Python scorer tests and pyflakes
passed, and the diagnostic Release build had zero warnings. The check used CPU
inference with no optimizer steps or sealed-payload reads. The images and rule
were previously inspected, and the three visual classifications were not
independently adjudicated. This is a safety improvement, not automatic
recognition or model approval.

| Evidence | SHA-256 |
| --- | --- |
| Verification protocol | `1986c26a94ec4d445f87d6d7948a86248a8ee55226660fc55ee54da20e3ba5a6` |
| Pre-run registration | `5e940247c5affd3f6d61281a466f2a1f9aab374c367789580d3e00c34cc32d0e` |
| Runtime description before and after | `007e2f483bd3be5a3746e8ee7d03cda5d862c0fbdb8f8b8a39d335d52933452d` |
| Full candidate workflow report | `e2d1b086d55e81d8527a3429a824021d491a8352312eb1233b8c85f1bdb82cf2` |
| Review-warning comparison | `beda94ebf6efa6c536a5f446a1252dc7d705af2150b8b64b60fa1bd2c7383e2c` |
| Recorded-point comparison | `d161cce0a9286bda8b6b3cdf18d090fe089aa4e9199c9ce406c91a05a51974e2d` |

The accompanying [visual review](WORKFLOW_Y_REVIEW_RESULT.md) describes
additional incorrect and partial readings that this position signal does not
resolve. Private region data and review sheets remain inside project artifacts.
