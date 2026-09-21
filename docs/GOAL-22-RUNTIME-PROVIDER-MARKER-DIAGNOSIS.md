<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Enforced CPU execution and native marker loss diagnosis

Every observed model now actually uses CPU in the declared CPU-only workflow.
The repair preserves all current native marker and OCR scores and every
exported value and status in the full 23-source CSV check. It does not approve
any model or complete Goal 22.

## Processor-policy defect

The runtime host recorded CPU-only settings but did not pass them to its
inference runtime. The marker classifier omitted a per-request provider limit,
so discovery selected DirectML. Earlier paired development comparisons remain
descriptive; their CPU-only label did not accurately describe classification.

The runtime now snapshots the host's allowed providers and intersects every
request with that list before looking up cached results or model sessions.
A stage may request fewer providers but cannot broaden the host's policy.
GPU cache entries cannot satisfy a CPU-only request. Normal provider fallback
remains available when the host permits it.

## Verified behavior

- Inference tests: 92 pass, one optional model-store probe skipped because its
  store environment variable was absent. The skipped probe is not a pass.
- Application tests: 463 pass. Tests execute requests against a CPU-only host
  while DirectML is advertised, including a request that tries to broaden the
  policy, mutable caller lists, and existing GPU cache entries.
- Native build: zero warnings and zero errors. Build and checks take 95.05 seconds.
- All 31 development panels report CPU for axis, detector, recognizer, center
  detector and classifier. Geometry and OCR scores are unchanged. The native
  comparison takes 132.65 seconds.
- All 39 panels in the full CSV comparison also report CPU for every stage.
  Exported values, status, upstream observations and aggregate scores match
  the repaired baseline. The comparison takes 145.59 seconds.

The CSV workflow still exports 12 sources and requires calibration review for
11. Correct unique values remain 403/706 and correct relational rows 399/724.
Wrong-scale rows and residual exports from failed sources remain zero.

## Where marker detections are lost

The explicit synthetic observer now retains center counters, decoded locations,
pre-suppression centers, centers entering classification, classified plot
markers and text-exclusion identities. The observation does not change
predictions or export behavior. Approved production retains no candidate
diagnostic payload; private acceptance installs no per-case writer.

The current 21 sources contain 838 truth points. The workflow emits 821 center
candidates, rejects 159 classified candidates as artifacts, excludes another
19 as text, and retains 643 markers. Of these, 561 match truth and 82 are extra.
All 277 missing truths are retained in the following attribution:

| Observation within the unchanged five-pixel match radius | Missing truths |
| --- | ---: |
| No above-threshold decoded center nearby | 199 |
| Every nearby classified center rejected as an artifact | 52 |
| Nearby pre-suppression center not retained | 11 |
| Accepted nearby detection already assigned to another truth | 10 |
| Text exclusion after classification | 5 |

Of the 199 without a nearby above-threshold output, 95 are crosses and 51
are other glyphs. This is not proof of a training defect: missing proposals,
input masks, confidence and localization must be distinguished. Proximity
before refinement does not establish one proposal's lineage. The next bounded
diagnostic inspects actual native proposal inputs before deciding whether to
reuse a candidate or train again. Existing failed V28/V7 outcomes stay closed.

The [evidence index](GOAL-22-RUNTIME-PROVIDER-MARKER-DIAGNOSIS.json) binds
executed source, native runtimes, model/fixture inventories, complete scores,
test results and timing. No threshold, model, acceptance bar, dependency,
private/sealed access or production approval changed. Code and synthetic
fixtures remain project-owned Apache-2.0. Build 433 remains 0.4.33.
All four Goal 22 outcomes are not yet jointly verified.
