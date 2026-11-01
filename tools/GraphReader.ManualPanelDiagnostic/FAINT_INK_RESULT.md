<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Faint connected-ink axis repair

The line provider retains its original dark-ink path search. If that search
fails between already aligned native line segments, it also checks a narrow
straight corridor for connected faint ink. Pixels from gray 200 through 229
are admitted only when nearby perpendicular background is at least 245;
the maximum span and existing endpoint tolerance are unchanged. This does not
bridge a blank gap or treat a uniform gray background as a line in the added
regression cases. The connected faint-line case failed before the change;
all three new cases and the 118-test axis suite passed afterward.

On 20 unmodified train images, the previously incorrect slanted axis on one
image moved to the visible vertical axis, and its horizontal origin moved
approximately 318 pixels left. The other 20 panels changed by at most two
pixels at measured axis endpoints. The corrected image remains marked for
review because its axis pair is ambiguous.

Two of 16 development images changed by more than two pixels. Both lack a
visible vertical axis and remain marked for review with partial Y-axis
evidence. In one the selected horizontal origin moved toward the first data
point; in the other the horizontal right endpoint extended along the faint
baseline. These observations are not independently adjudicated full-plot
accuracy or false-positive results.

The unapproved center candidate matched 242/332 recorded points on the same
20 originals before the change and 250/332 afterward. All eight additional
matches came from the one repaired holdout image. Returned center candidates
rose from 1,913 to 1,933; their final precision is unknown because recorded
points are incomplete and the downstream classifier and recovery were not
run. See the [marker diagnostic](../GraphReader.ManualOverlayCapture/README.md).

| Evidence | SHA-256 |
| --- | --- |
| Original axis report | `71177f3d6c14de7525d1eec0d03596fb35109248ef9b879fc0a81a94ed4a651b` |
| Development axis report | `bc6eaa24389cf78eb637785bc7b3a79cab37542c3259da0ed58fdea880f85904` |
| Original marker capture | `27d85db95b09e2c79f021de5d9cb4a65ac69c3fc82763d7b4656bdcaca28bfa2` |
| Original point score | `40ae0a929c0a63e8bae1b0894c908c0f14c83fab210a60711ed356e25eb79e26` |

The full solution check, `dotnet test GraphAutoReader.slnx -c Release
--no-restore -m:1 -p:UseSharedCompilation=false`, passed 2,378 tests, skipped
25, and failed none. The marker diagnostic passed 18 Python tests and pyflakes.
CPU inference was used for diagnostics. No optimizer, sealed payload read,
model approval, or product acceptance occurred. Private per-image evidence
remains project-local while the recognition investigation consumes it.
