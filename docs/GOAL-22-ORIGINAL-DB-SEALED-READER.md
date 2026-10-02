<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Original-DB sealed evidence reader

The production gate previously rejected every synthetic-sealed source. It now
validates the existing `graphreader.original-db-ocr-sealed-evaluation.v1`
aggregate emitted by `ml/policy/ocr_sealed_evaluation.py`.

The gate requires three checksum-embedded JSON resources:

| Resource | Existing producer content |
| --- | --- |
| `synthetic_sealed_score` | `aggregate-outcome.json` |
| `synthetic_sealed_request` | Exact `request.json` bytes |
| `synthetic_sealed_preflight_binding` | The admission attempt binding serialized with `canonical_json_bytes` |

Preflight bytes must hash to the existing attempt identity. The request must
match its outcome hash, admission, set, candidate and complete source count.
Both bind the same development score, runtime, policy, coverage and metrics.
All paths are provenance metadata; the reader never opens an archive or a
development checkout.

The reader recomputes detection precision/recall, exact recognition, CER and
role accuracy against the shared 95% / 5% bars. It retains recognition failures,
checks all seven role totals, and rejects unknown fields, duplicate properties,
disclosures and unclassified stderr. It does not add a per-role threshold.

## Verification

All **670 App tests pass**, with zero failures or skips. Application and native
evaluation-tool builds have zero warnings and errors. Final elapsed time is
**85.4799 seconds** under the shared CPU guard. The first run
also passed 670 tests; a subsequent fixture-only edit made character-edit
attribution consistent with exact-match counts, followed by the final checks.

`CreateOriginalDbSealedEvidence.py` creates fabricated counts using the actual
producer's pure formatting functions and validates its worker envelope with
the existing Python transport validator. C# tests accept the exact acceptance
boundary and reject changed identities, failed bars, subset inventories,
incorrect arithmetic and hidden case output. The fixture opens no sealed set.

Exact commands, source hashes and both test runs are in the
[evidence report](GOAL-22-ORIGINAL-DB-SEALED-READER.json).

## Remaining work

The composed OCR schema still needs its own compatible development and sealed
approval path. No authentic passing production bundle or model activation was
performed. Current runtime bytes require current acceptance evidence; prior
diagnostic executables cannot approve this changed executable.

Goal22 remains **FAIL**. Accuracy, real acceptance, production activation and
final distributions remain outstanding. No private/sealed data, model-store
change, optimizer step, package or release was involved. No dependency or
external asset was added; new project code and fixtures are Apache-2.0.
