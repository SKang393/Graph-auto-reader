<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Marker connection context controls

## Observed defect

Runtime source `4e371cb0cf092a12fe0f490b3f136d93651c520a` used the existing
single-scale normalized correlation and fixed 0.9 similarity threshold. On two
owned 640 x 400 controls, five identical filled circles of radius three were
joined by one-pixel lines. The first circle supplied a 13 x 13 example; four
remaining centers were known. Both cases used exactly the same worker/settings.

| Control | Matched within 1 pixel | Missed | False proposals | Request wall | Native matching |
| --- | --- | --- | --- | --- | --- |
| Horizontal connections | 3/4 | 1 | 0 | 736.690 ms | 166.189 ms |
| Changing slopes | 0/4 | 4 | 0 | 541.383 ms | 56.239 ms |

The horizontal case missed the opposite endpoint, whose one-sided line differs
from the selected endpoint. The changing-slope case produced no proposals.
This identifies connection context as a practical limitation of the current
matcher. The earlier identical-context controls established execution and
coordinate behavior only. They did not establish representative usefulness.
These two controls likewise do not establish an accuracy rate.

Both calls completed without worker failure using one OpenCV thread,
BelowNormal priority and the verified 416/10000 job budget on 12 logical
processors (0.4992 core). Whole probe wall time was 1.661 s. No proposals were
applied. Inputs were generated only in memory; no model, image dataset, private
corpus, training, sealed run or threshold selection was involved.

## Reproduction and identities

Build `GraphReader.CalibrationOcrProbe.csproj` in Release with existing local
caches, `--no-restore -m:1 -nr:false -p:NuGetAudit=false
-p:UseSharedCompilation=false -p:BuildProjectReferences=false` and the existing
isolated `artifacts/hardware-final-build` output. Build passed in 5.634 s with
zero warnings/errors. Restage the reviewed OpenCV DLL afterward, verify the
unchanged App hash, then run:

```text
GraphReader.CalibrationOcrProbe.exe --marker-shape-control-check <absolute-App-exe>
```

The baseline parent had a 43-second cancellation deadline and a 45-second hard
watchdog. The final four-control diagnostic uses 65/70 seconds; existing child
limits remain unchanged. It writes only a bounded JSON result to stdout.

| Item | SHA-256 |
| --- | --- |
| App DLL | `144f81553539ca592bd58eb3e33f1f330f9aae5efde9fcb6194c6f7869ce62cf` |
| Probe DLL | `f17feaa33e9f9dc9802f3215ccc368a7fd0a3e895bf00e3c80dceaa9a3a209e0` |
| Horizontal input | `06ac293bd70bf308acf0a1d88626a9b15df27bf32ab9ff4073a528f317cade65` |
| Changing-slope input | `a87260d8ccc76c313e9aa0099498f45578040d7b46731132c87bfd833011edbb` |
| Result JSON | `f559ba1bd94c00d8c8d8117cd93bb00dabf69f9b91b25c689e2cc6840144aa24` |

The reviewed native runtime remains
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
New diagnostic source is Apache-2.0, with no added dependencies or assets.

## Candidate and decision

The existing production template helper also compares full rectangles, while
enclosure recovery only covers hollow glyphs. Neither removes connection
context for both filled and open markers. The selected correction uses the
existing native masked correlation with a positive float Gaussian mask:
`M(x,y)=exp(-((x-dx)^2+(y-dy)^2)/(2*(width/6)^2))`, centered on the exact selected
point offset. OpenCV uses M for its mean and M squared for product/norm terms.
This preserves support on background and hollow interiors. Algorithm identity
is `original-panel-user-exemplar-gaussian-single-scale-v2`; similarity remains
0.9. Nonfinite scores and neighbors are skipped. Values outside [-1,1] are
rejected except for a declared float32 roundoff allowance of 0.00001.

This was one fixed hypothesis, recorded before implementation and run without
a parameter sweep. Independent read-only review verified mask forwarding in
the pinned OpenCvSharp/OpenCV sources and found no material coordinate,
numerical, source-binding or resource-limit defect in the final change. UI
guidance explains how to fit a whole-symbol example and offers another example
or manual insertion when positions are missed.

## Candidate results

Build passed with zero warnings/errors in 64.101 s; App test compilation took
9.679 s. The focused marker/localization run passed **37/37**, no skips, in
4.607 s command wall time. New regressions cover positive fractional-offset
weights, invalid scores/neighbors, roundoff bounds, actual hollow/filled/cross
separation, unchanged input bytes and constant-example rejection.

| Control | Matched within 1 pixel | Missed | False proposals | Request wall | Native matching |
| --- | --- | --- | --- | --- | --- |
| Horizontal connections | 4/4 | 0 | 0 | 628.303 ms | 77.445 ms |
| Changing slopes | 4/4 | 0 | 0 | 595.501 ms | 87.863 ms |
| Hollow circles, changing slopes, filled decoy | 4/4 | 0 | 0 | 576.208 ms | 90.303 ms |
| Seed plus marker-free crossing/T/line/background regions | 0/0 | 0 | 0 | 601.347 ms | 85.250 ms |

The first two input hashes exactly match the baseline. Hollow control clearing
is confined to each circular interior; original line pixels outside the circle
remain. All calls reported the existing one-thread, BelowNormal, verified
0.4992-core job budget. A 50 ms sampler observed three of the four short-lived
workers, with maximum reported peak working set 69,959,680 bytes (66.72 MiB).
This sample can miss startup/exit intervals and is not a universal memory bound.
Whole probe/sampling wall time was 3.663 s, exit 0, no owned process left.

A separate near-limit control used 2,560 x 1,600 pixels (4,096,000), below the
unchanged 4,194,304-pixel cap, and a 2,400 x 1,440 search plot. The two expected
centers were returned with zero pixel error in **976.046 ms**; native matching
took 472.708 ms. The observed child cancelled and exited in **96.828 ms**.
Restart took 1,051.950 ms and returned the same two centers. Sampled maximum
reported peak working set was 210,821,120 bytes (201.05 MiB), with no surviving
owned process. These are local measurements, not maximum-resource guarantees.
The fixed five-second matching watchdog and parent/child limits remain unchanged.

Reproduce the near-limit lifecycle check with
`--marker-exemplar-check <absolute-App-exe> near-pixel-limit`. This optional
diagnostic mode preserves the earlier default 1,280 x 800 fixture. Its probe-only
build took 6.247 s with zero warnings/errors; App tests were not repeated for
that diagnostic-only change. The reviewed native DLL was restaged after each
compilation. No original images or files are modified.

Retain this correction for ManualPreview review assistance and prepare one
integrated **0.4.41** preview with the prior batch-add and boundary changes.
The four controls are engineering regressions, not a production-accuracy gate.
No private/sealed data, training or model selection was used; sealed budget is
zero. Native interaction, representative accuracy and final production readiness
remain unverified. No public release or model activation is authorized here.

Candidate SHA-256 identities, before version-only preparation:

| Item | SHA-256 |
| --- | --- |
| App DLL | `8b828bdd9a45de2617be3497d8d3f2fcdfebba35a1b8c6d3fc442af6ad8c01e7` |
| Final diagnostic DLL, including near-limit mode | `3ba271942574d79b0c697c464e8e25b13480ca9df8b9de09c1ce25105267ef3a` |
| Hollow control | `5283a92de1a9bcf2a6ab5870131d504dfd11157f9ef96860f97c5146ec894609` |
| Marker-free control | `b4c0d1c98801e81476932f85bde7edbd86669114f01799b4497bb8aaeea16d7d` |
| Near-limit control | `4a4a0b37865a3128dd3998a124c0687facab0f5d228faca26d7819bd68b9da41` |
| Test TRX | `fc01ec57237dac29c537fc3c79ba543bc6a7c3f28536e4bc51cd9be132da0966` |
| Four-control JSON | `339c740178db82007bd74a53d661553777d4bab8877ebeb482c358ecd3447884` |
| Near-limit JSON | `232fcff19d7bdd20d308e56f443c318789b17770e0f329cf1db725c00ec0a347` |
| Small/large process measurements | `f5f6cd741bdac9009eaa691d1825efd82882b2b28a811310655b5d6f46ca74de` / `f3925fc3507c97586106270288b7bd98892e6e25059604b76155ead41f78a62a` |

After combined source/outcome `a7f7394cd72c6777ae71c27a12d185f401548dca` reached
main, all 19 files (66,268 bytes) in `artifacts/marker-shape-check` were retired
after containment and active-worker checks. Pixels existed only in memory.
The [0.4.41 package](HOSTED-INTERNAL-PREVIEW-20261113.md#hosted-delivery-0441)
is now uploaded, downloaded and verified with the same controlled outcomes.
The isolated diagnostic compiler output is reused for the active Prepare/crop
batch after package verification; no second compiler tree is created.
