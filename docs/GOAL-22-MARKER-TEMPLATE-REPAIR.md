<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Original-pixel marker recovery, 2026-09-21

The application now recovers repeated glyphs from complete shapes measured in
the current image. On the unchanged 21-source development diagnostic, correct
points increase from **569 to 666 of 838**, with **17 false points unchanged**.
Every previous match is retained. The separate full-CSV fixture remains
unchanged at 12 exports, 11 review failures, and 403/706 correct unique values.
The repair is useful but does not complete the accuracy gates or Goal 22.

## Problem and implementation

Some detector centers sit off-center or between neighboring symbols. Using
their radius crops directly as templates repeats those errors. A classified
radius-crop diagnostic added 63 correct points but raised extras to 52.

The repair accepts a template only from a complete original-image ink
component containing exactly one accepted center, with bounded dimensions.
It also reuses the existing measured legend glyphs. It checks the same fixed
scale factors and primary correlation of 0.90 used in the diagnostic, excludes
recognized text, legend frames and areas outside the plot, then submits new
centers to the unchanged classifier. Existing centers are preserved. The
normal calibration, session, phase, review and export steps still run.

Correlation records image-match support, not a calibrated probability. Source
pixels remain immutable, coordinates remain in original pixels, cancellation
is checked during search, and the additional stages have distinct provenance.
The adapter identity includes the recovery version. No model is approved by
this source change.

## Evidence

| Check | Before | After |
| --- | ---: | ---: |
| Broader native matched points | 569/838 | 666/838 |
| False / missing points | 17 / 269 | 17 / 172 |
| Precision / recall | 97.10% / 67.90% | 97.51% / 79.47% |
| Exact OCR readings | 337/453 | 337/453 |
| Axis corners within two pixels | 84/93 | 84/93 |
| Full-workflow exports / failures | 12 / 11 | 12 / 11 |
| Correct unique exported values | 403/706 | 403/706 |
| Correct relational rows | 399/724 | 399/724 |

Full-workflow source states, errors, exported numeric values, OCR and axis
observations are unchanged. Three existing wrong-phase rows remain counted.
There are zero wrong-scale rows, duplicate rows or residual rows from failed
sources. All observed model executions use CPU. The native diagnostic's 97
additional correct points do not translate into additional values in the
separate full-CSV fixture.

All **486 App tests pass**, including eight new scenarios covering displaced
centers, gaps between symbols, ambiguous/connected components, actual native
matching, legend/text/plot exclusions, classifier rejection, duplicates,
immutable pixels and cancellation. The native tool builds with zero warnings
and errors. Tests/build took 86.46 seconds, native verification 149.44 seconds,
and full CSV verification 160.59 seconds. Earlier compile/diagnostic plumbing
failures are retained as void evidence; they did not consume a sealed run.

Validation used `dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj
-c Release`, the real-acceptance tool's `--run-frozen-candidate-synthetic`
operation, and its `--score-frozen-workflow-csv` operation, with frozen input,
runtime and model bindings. Exact options and checksums are in the
[evidence record](GOAL-22-MARKER-TEMPLATE-REPAIR.json).

## Decision and remaining work

Keep the strict whole-glyph repair. The simpler direct-component diagnostic
raised extras to 43 after classification and is not integrated. Lower
similarity levels remain descriptive evidence, not selected product settings.
All measured failed outcomes and all authored truth points remain counted.

Recall still fails the unchanged 95% bar. Continue diagnosis of the remaining
172 missed points and OCR coverage, then complete sealed prerequisites, real
workflow/Chandler verification, model promotion and both 2.0.0 distributions.
Source checkpoint b41c3df passed CI run 35624802936 before this repair.

The change uses Apache-2.0 project code and the existing reviewed runtime and
model. No new dependencies, weights, training, private/sealed reads, production
approval, package, tag or public release occurred. Build 433 remains 0.4.33.
All four Goal 22 outcomes remain incomplete.
