<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Validation surfaces

The public validation harness is the quality scoreboard for synthetic
metric-contract fixtures and release-license plumbing. Goal 22 model acceptance
uses `ml/policy/evidence-policy.json` and `ml/policy/acceptance-bars.json` as its
sole policy and threshold sources. Real acceptance uses the dedicated guarded
workflow evaluator described below.

The checked-in public suite is a synthetic metric-contract smoke. It exercises
calculators, gates, report generation, split metadata, and manifest-policy
plumbing. Its scores and timing are not detector accuracy, inference runtime,
or end-to-end application performance evidence.

## Run the public scoreboard

From the repository root, run:

```powershell
dotnet run -c Release --project tools/GraphReader.Benchmarks -- --suite public
```

The command writes reports beneath:

```text
artifacts/validation/public-scoreboard/
  public-scoreboard.json
  public-scoreboard.md
  public-scoreboard.html
```

Use `--output <directory>` to choose a different report directory. The process
returns `0` only when every regression gate passes, `1` when a quality gate
fails, and `2` for invalid arguments, cancellation, or an output failure.
Private evaluation cannot be selected through this executable.

The JSON report is the automation interface. Every format carries the evidence
scope warning. The Markdown and HTML reports are
human-readable views of the same deterministic scoreboard. Each format records
the case and module identity, measured value, required threshold, gate result,
timing, and peak managed memory. A failed run also writes each failure to
standard error as `FAIL [module/case] gate: detail`.

## Synthetic smoke thresholds

The checked-in benchmark catalog governs this synthetic calculator smoke only.
It does not override the shared 95 percent Tier 1 model bars or the structural
export-safety rules. Threshold comparisons use the direction shown below. A
value equal to its threshold passes.

| Metric ID | Pass condition |
| --- | ---: |
| `marker_center_precision_3px` | at least `0.80` |
| `marker_center_recall_3px` | at least `0.80` |
| `marker_center_f1_3px` | at least `0.80` |
| `marker_center_precision_5px` | at least `0.90` |
| `marker_center_recall_5px` | at least `0.90` |
| `marker_center_f1_5px` | at least `0.90` |
| `duplicate_detection_rate` | at most `0.05` |
| `false_positives_per_megapixel` | at most `0.50` |
| `shape_macro_f1` | at least `0.80` |
| `fill_state_macro_f1` | at least `0.80` |
| `axis_anchor_error_px` | at most `3.0` px |
| `calibration_rmse_graph_units` | at most `1.0` graph unit |
| `x_tick_numeric_exact_match` | at least `0.90` |
| `y_tick_numeric_exact_match` | at least `0.90` |
| `ocr_character_error_rate` | at most `0.10` |
| `series_association_accuracy` | at least `0.85` |
| `legend_mapping_accuracy` | at least `0.85` |
| `phase_divider_error_px` | at most `3.0` px |
| `phase_divider_error_sessions` | at most `1.0` session |
| `phase_assignment_f1` | at least `0.90` |
| `csv_point_precision` | at least `0.95` |
| `csv_point_recall` | at least `0.95` |
| `y_mae_axis_range_percent` | at most `2.0%` |
| `exact_phase_code_accuracy` | at least `0.95` |
| `cold_runtime_ms` | at most `6000.0` ms |
| `warm_runtime_ms` | at most `2000.0` ms |
| `peak_memory_bytes` | at most `1,073,741,824` bytes |
| `confidence_expected_calibration_error` | at most `0.10` |
| `confidence_brier_score` | at most `0.10` |

These thresholds are smoke regressions, not detector tuning knobs or production
model approval. Never lower a threshold to make one fixture pass.

## Dataset and split safety

Public scoring uses synthetic or openly licensed fixtures only. Synthetic
families are held out by renderer, font, degradation recipe, chart template,
and marker-style family where those fields apply.

Real-case metadata must include a stable article identifier. The split
validator groups at article level and rejects a dataset when panels from one
article appear in more than one of train, validation, or test. Panel-level or
image-level random splitting is not an acceptable substitute. Private study
data is evaluation-only unless separate, explicit permission authorizes
training.

## Real and private evaluation safety

The public scoreboard cannot select private evaluation. Goal 22's explicitly
authorized real corpus is the Git-ignored `data/manual data/` tree documented in
`AGENTS.md` Section 8.1. Only the dedicated local acceptance harness may read it,
and it must keep images, truth rows, study names, participant names, and per-case
predictions out of Git and release artifacts. Real development diagnosis may be
case-level; real sealed evaluation remains aggregate-only.

The general-purpose private adapter for any other dataset remains unavailable by
default and returns availability only when all of these conditions hold:

1. The caller explicitly opts in for the current invocation.
2. No supported continuous-integration environment variable is enabled.
3. The configured directory exists.
4. Both the configured path and its resolved link target are outside the
   repository. This rule does not relocate or supersede the explicitly
   authorized Goal 22 corpus.

Unavailable runs return structured reason codes:

- `ExplicitOptInRequired`
- `ContinuousIntegrationDetected`
- `ExternalDirectoryRequired`
- `ExternalDirectoryNotFound`
- `ExternalDirectoryMustBeOutsideRepository`
- `ExternalDirectoryCouldNotBeResolved`

The general adapter does not enumerate, read, copy, or write private case data.
Keep other private datasets in their approved external directories. Neither
path may be used in CI, training, fine-tuning, candidate selection, or public
reports.

## License release gate

The public scoreboard runs a license-policy check over every JSON model
manifest. It requires a model ID, a 64-character hexadecimal checksum,
`commercial_use: true`, a nonempty SPDX expression, `license.reviewed: true`,
and a notice path that exists inside the repository. It rejects SPDX values
containing GPL, AGPL, SSPL, BUSL, or non-commercial markers. A bundled model
also fails when its manifest declares `redistribution: false`.

This check does not prove that an unbundled declared weight exists or that its
bytes match the manifest checksum. Artifact checksum verification and full
frozen-schema validation remain separate release checks. A model with missing
or non-redistributable weights remains blocked even if this license-policy
check passes. Both Windows distributions must use the same reviewed manifests,
model checksums, and notice set.

## Interpret the reports

Read gate status before aggregate averages. A high overall score does not hide
a module/case failure. Marker center precision, recall, and F1 are reported at
both 3-pixel and 5-pixel tolerances using one-to-one matching. Duplicate rate
and false positives per megapixel remain separate so repeated detections and
background artifacts are visible.

Shape and fill macro-F1 weight classes equally. Axis, calibration, OCR,
grouping, legend, phase, CSV, and confidence-calibration metrics diagnose their
own modules. Timing and peak managed memory are evidence, not accuracy scores.
Compare cold and warm timing only with equivalent hardware, providers, fixture
sets, and build configuration.

A public `PASS` means the synthetic metric-contract smoke and manifest-policy
gate met the catalog thresholds in that run. It is not detector accuracy or
end-to-end performance evidence. It is also not evidence that private article
cases passed, that all research graphs are supported, or that a model may be
redistributed without the separate provenance review.
