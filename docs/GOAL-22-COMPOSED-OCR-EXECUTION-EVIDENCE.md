<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Composed OCR execution evidence, 2026-09-21

The OCR development tool and the real-workflow tool previously had no shared
identity for the OCR execution they were describing. Model hashes alone do
not establish that the same preprocessing, assembly or runtime was used.

An explicit development command now emits a version-2 aggregate report with
a shared execution fingerprint. It binds 30 common managed/native files,
detector and recognizer weights and manifests, OCR composition, axis stage,
CPU scheduling and optimization, framework and architecture. File locations
are excluded so copies of identical binaries compare consistently. The
existing version-1 command and sealed-evaluation formats are preserved.

The real-workflow tool can validate the report against a fully loaded frozen
candidate binding and explicit report, request and stage-candidate hashes.
It recomputes precision, recall, recognition, character error and role rates
from the counts, checks their cross-field consistency, and rejects duplicate
properties, extra output fields, split drift, negative/fractional/overflowing
counts, private or sealed access claims, truth supplied to inference, and
approval claims. Training metrics cannot stand in for the development split.

This is a compatibility check. It deliberately accepts a structurally honest
failing score and does not grant stage admission or model approval. The real
admission source-format adapters, sealed evidence and marker/classifier
prerequisites are still unfinished. Their existing restrictions remain in
place; no acceptance threshold or policy file changes.

Both tool builds have zero warnings and errors. The 34 evidence checks pass,
including mutation checks for every shared execution file. Existing admission,
worker and runner self-tests also pass. Replaying the same 21 owned sources
and 31 panels preserves every metric: 330/453 exact text, 337/453 correct roles,
378 matched regions, 147 false detections and 75 misses. The native report
successfully matches the separately bound workflow runtime. The full build,
self-tests, replay and compatibility validation took 117.45 seconds.

See [aggregate evidence, source hashes and commands](GOAL-22-COMPOSED-OCR-EXECUTION-EVIDENCE.json).
The separate [native crop diagnosis](GOAL-22-NATIVE-CROP-PADDING-DIAGNOSIS.md)
is closed without changing crop settings because its improvements are mixed.
No dependency, license, weight, application behavior, private/sealed access,
optimizer step, production activation or packaged build changed. Build 433
remains 0.4.33. All four Goal 22 outcomes remain incomplete.
