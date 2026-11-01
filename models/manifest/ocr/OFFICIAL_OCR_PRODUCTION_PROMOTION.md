<!--
SPDX-License-Identifier: Apache-2.0
Copyright 2026 Sungwoo Kang
-->

# Official PP-OCRv5 production promotion contract

## Current decision

The official PP-OCRv5 pair completed the one authorized legacy dense-detector
public synthetic evaluation and failed the fixed production gates. The distinct
source-bound structure-consensus composition also consumed its one allowed
execution and failed closed before report creation. There is no tracked OCR
model manifest, no approved
benchmark report, and no OCR payload in the production model store or a release
package. The reviewed Apache-2.0 license and ONNX change-notice bundle exists,
but it has not been bound through a model-store index or package. This is an
intentional fail-closed state.

The preregistered structure-consensus workflow matches the production
vertical-glyph grouping, enforces the C# gate's 8 MiB limit for every embedded
resource, and does not accept an external precomputed marker-result file. The
marker-center P1 through P3 budget is exhausted and P3 remains rejected after
one false positive in its once-only public gate. The official OCR execution
failed earlier at the detector probability-tensor contract, so no OCR metrics
or marker-stage result was emitted and approval remains impossible.

The separate project-owned structural-veto V17 detector budget is also
exhausted. P1, P2, and P3 each failed selection before public evaluation. Final
P3 retained all 1,728 validation truths and passed CPU parity but left one
prohibited false region, so the truth-hidden 288-scene public archive remains
unopened with zero evaluations. It cannot replace or approve the official
detector, and no rerun is authorized.

The newer project-owned relational-scene V21 budget is now exhausted as well.
Final P3 reused the exact P2 checkpoint with zero optimizer steps and passed
CPU parity, but its visible selection retained only 1,014/1,024 truths and
admitted two prohibited false regions. All three candidates are consumed, its
truth-hidden public archive remains unopened with zero evaluations, and it has
no manifest, store entry, package, production approval, or release eligibility.

| Task | Model | Converted ONNX SHA-256 | Current status |
| --- | --- | --- | --- |
| OCR detection | `PP-OCRv5_mobile_det` | `d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb` | Conversion parity passed; public evaluation failed |
| OCR recognition | `en_PP-OCRv5_mobile_rec` | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` | Conversion parity passed; public evaluation failed |

Both models must bind the same checksum-exact production gate report. Neither
model may be promoted by itself.

## Fixed evaluation result

The split was frozen before inference with 100 validation text cases, 100
sealed-test text cases, and 20 exclusions. No private image or Chandler data was
used. The split SHA-256 is
`1fc3b2e72f89cbfb0d8854ec8701368e7ae764cbd5c6fef17b7e497d06ec9f09`.
Its 532,313-byte fixture archive SHA-256 is
`69eeeff73f4cfd2dd6580ad9538f1a89527f8e5b320ce6a9cd7155d2bd22ea99`.
The post-run freeze verification passed.

The ignored report at
`ml/ocr/official_bakeoff/runs/production-gate/evaluation/report.json` has
SHA-256 `3fceb688d44a2f4c322e40bdfa3dd09ca74ec6d5742ab257ae102ccf76e1c44b`
and records `status=fail`, `production_approval=false`, and
`release_eligible=false`. Validation exact match was `0.0`, CER `1.0`, and role
accuracy `0.03`. Sealed-test exact match was `0.0`, CER `1.0`, and role accuracy
`0.02`. Detection exact-count rate was `18/220`, or
`0.08181818181818182`. The detector matched only 3 of 100 validation text cases
and 2 of 100 sealed text cases, produced 195 false regions across text cases,
and produced false regions on 7 of 20 exclusion fixtures. Independent composed
marker-stage evidence was not available, so `marker_creation_evaluated=false`.
ONNX parity remained passing at maximum absolute error
`2.205371856689453e-06`.

## Structure-consensus execution result

The new public validation and inference-locked sealed split was frozen once
before model execution with 200 text and 50 exclusion cases in each partition.
No private image or Chandler data was used. The 1,289,983-byte split SHA-256 is
`8685a3dfcb8212f612115c20d0f70437e0738fa1c4d86743cfd0e50bc5a41a8d`.
Its 1,732,541-byte fixture archive SHA-256 is
`6bd83d6b05918a6829a99e4cfecbb5af0e0629c458242398429856baae5e02b2`.
Post-freeze verification passed.

The separate evaluation environment retained the exact conversion lock and
added only evaluation-time `opencv-python-headless` `4.10.0.84` from wheel
SHA-256 `afcf28bd1209dd58810d33defb622b325d3cbe49dcd7a43a902982c33e5fad05`.
The exact detector and recognizer ONNX hashes remained unchanged. The single
authorized official composition run began CPU inference, then failed with
`BLOCKED: Detector output is not a probability tensor.` before it created an
output root or report. The static ONNX graph still identifies a final `Sigmoid`
producer for its sole output, but the executed tensor violated the exact frozen
`[0,1]` range check. No minimum or maximum value was captured, so none is
asserted. No metrics, predictions, parity pairs, marker evidence, manifest,
store entry, or package evidence was emitted.

The ignored failure record at
`ml/ocr/official_bakeoff/runs/structure-consensus/official-evaluation-failure.json`
has SHA-256 `b14dd36632224254933fad9c826ab80298e25371a0e32040bcab66a4684fd4e0`.
The one-run budget is consumed. Do not rerun, repair, or tune this composition
against the exposed 500-case split.

## Prospective bounded probability activation

The runtime exposes a new manifest-selected
`probability_with_1e-5_clamp` activation for a future preregistered
composition. It accepts only finite detector outputs within `1e-5` of the
closed interval `[0,1]`, clamps that bounded numerical drift to the interval,
and rejects larger or non-finite values. Cache identity and runtime request
provenance bind both the activation and its fixed tolerance. The existing
`probability` activation remains strict and unchanged.

Neutral generated-tensor probes and focused runtime tests establish only this
boundary behavior. They are not detector-accuracy, public-gate, model-store,
packaging, clean-machine, or release evidence. The consumed 500-case attempt
remains failed and cannot rerun. Any use of the new activation requires a new
frozen protocol, disjoint fixture bytes, an exact source binding, and a new
one-run authorization before inference.

The distinct V2 bounded-probability composition is now preregistered at
protocol SHA-256
`0ee2ec0ef4a9f2f7f7f373da7389b84513f254c8642e4ddd5fd5427518d5e133`
and workflow SHA-256
`b2a52e925f9d06714d7a346ac929c60c8a81cf101893147657011e70325a18d7`.
It binds both exposed predecessor split and source inventories and permits one
freeze and one official CPU evaluation. The single verified 500-case freeze is
bound at split SHA-256 `a7f407aa...f998`, archive SHA-256 `a1f978cf...961d`,
and source-inventory SHA-256 `3cd3033a...d34`; both prior source and case-ID
overlap counts are zero. The post-freeze authorization config binds those bytes,
the exact model pair, evaluator bundle, runtime gate configuration, and
canonical one-run seal inputs. That one execution is consumed and the terminal
result is fail. Exact-match, CER, role, detection, exclusion, and independent
marker-stage gates remain blocked. The probability-boundary repair itself is
confirmed: one of the 500 detector calls required a `1.1920928955078125e-7`
clamp and no output exceeded the fixed tolerance. No manifest, store entry, or
package evidence exists. Promotion remains unavailable and the exposed V2 split
must not be reused for tuning or another model call.

## Evidence that must exist before approval

The following evidence is conjunctive. A passing item cannot waive or replace
another item.

1. The public validation and inference-locked sealed splits are frozen before
   model execution. Every fixture byte is checksum-bound to its split record,
   and no private image or Chandler data is present.
2. The exact converted ONNX pair runs on the CPU provider over the frozen
   fixture bytes. Predictions identify the detector and recognizer SHA-256
   values, split SHA-256, evaluator SHA-256, provider, and per-case source
   SHA-256.
3. Validation and sealed-test exact match are each at least `0.90`; CER is at
   most `0.05`; role accuracy is at least `0.90`; detection exact-count rate is
   `1.0`; duplicate regions and exclusion false regions are zero; and marker
   creation from OCR text is zero.
4. At least 16 direct parity pairs per model establish ONNX maximum absolute
   error at most `1e-4`. The runtime report identifies CPU execution for both
   payloads.
5. The report embeds and hashes the reviewed evaluator, execution workflow,
   fixture ZIP, sealed split, direct core predictions, final predictions,
   runtime results, and independently composed marker-stage results required by
   `graphreader-ocr-structure-consensus-public-gate-v1`. The gate must also
   rederive decoded source BGR, axis/tick/divider-masked detector BGR, DB model
   to structure-candidate matches, final regions, truth matches, duplicates,
   and exclusion hits. Reported aggregate metrics must equal metrics derived
   from those resources.
6. The reviewed Apache-2.0 license and ONNX change-notice files exist at
   `LICENSES/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt` and
   `LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt`, with SHA-256 values
   `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4`
   and `8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729`.
   The notice paths must still be checksum-bound by the model-store index and
   verified in both packages. Repository-level or archive-name-only licensing
   is not enough.
7. Both schema-version 1 manifests declare the exact payload SHA-256, exact
   tensor and preprocessing contracts, commercial use, redistribution, CPU
   provider support, and one shared production approval report.
8. The ignored production model store contains only canonical indexed
   resources. Each manifest, ONNX payload, notice, and benchmark report passes
   checksum validation, and the resolver successfully discovers both tasks.
9. The Windows packaging audit discovers both OCR tasks, packages the same
   payload bytes and report, verifies notices and checksums, and still passes
   the forbidden-file scan. Installer and portable staging must resolve the
   same model identities.

The runtime gate now rehashes every embedded resource, opens the fixture ZIP,
matches every fixture byte to its split record, binds direct detector and
recognizer tensor identities, and requires runtime provenance for the frozen
execution workflow and CPU provider. It also requires independently composed
marker-stage results for every fixture. The current official report predates
that composed marker run and failed the accuracy and exclusion thresholds, so
it cannot satisfy the strengthened gate and must not be rewritten as passing.

## Fail-closed manifest transition

Do not add a tracked OCR manifest while the public gate inputs are missing.
After those inputs exist, an approval-false manifest may be used for local
evaluation only. It must bind the reviewed license and notice checksums above,
and it must contain no benchmark where all of these values are simultaneously
true:

```text
status = pass
release_eligible = true
production_approval = true
```

Promotion is one atomic evidence change for both tasks. Each final manifest
must contain exactly one such approval benchmark with:

```text
profile = graphreader-ocr-structure-consensus-public-gate-v1
evidence_path
evidence_sha256
evaluator_source_sha256
sealed_split_sha256
predictions_sha256
runtime_results_sha256
sealed_test_exact_match
sealed_test_cer
onnx_max_abs_error
```

The two `evidence_sha256` values must be identical. Any model identity,
checksum, resource, provider, notice, threshold, store-resolution, or packaging
failure returns both tasks to unavailable. It must not emit release artifacts.

## Production store layout

The model-store workspace is rooted at `artifacts/production-model-store`.
The runtime model root must be a clean indexed package directory, whose index
resolves each resource beneath the canonical task identity and version paths
enforced by `ProductionModelStore`:

```text
manifest/<model-id>/<version>/manifest.json
runtime/<model-id>/<version>/<declared-onnx-file>
notices/<model-id>/<version>/<notice-file>
evidence/<model-id>/<version>/<shared-report-file>
```

On 2026-10-31 the parent workspace also contained a nested `package` directory,
so passing the parent as `GRAPHREADER_PRODUCTION_MODEL_STORE` correctly failed
closed on an unlisted file. Passing the clean
`artifacts/production-model-store/package` directory instead allowed the
opt-in `ExactLocalClassifierManifestComposesLazyProductionAdapter` integration
test to pass: the approved classifier was resolved by its exact payload hash
and the inference host remained lazy. The package-index SHA-256 was
`2cbd8adb51f80307b1ca4f011d54f108a78c3299c901e680ed92696ec46750a8`.
This component check did not load the model, approve the missing detector or
recognizer, or establish full automatic-workflow or release readiness.

The source-built conversion outputs remain ignored research artifacts until
the full transition above passes. A conversion report, payload hash, or CPU
smoke by itself is not production approval.
