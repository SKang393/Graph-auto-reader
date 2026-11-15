<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve wide endpoint labels before literal OCR

## Source defect and bounded repair

The existing development traces document unpaired numeric labels and incomplete
calibration. Source inspection found a role/association mismatch; this report
does not attribute the private failures to that mechanism.

For a plot spanning X=100..300, the role classifier's five-percent center margin
starts at X=90. An owned numeric box spanning X=68..108 has center 88 and visibly
overlaps the first tick at X=100. The classifier discarded it before recognition,
although its twelve-pixel offset is within the existing twenty-pixel glyph-size
association bound. The mirrored last endpoint has the same mismatch. This
prevents fitting six otherwise sufficient, literal measured pairs.

The numeric X lane now also accepts positive horizontal box overlap when the
entire box is below the baseline. Existing center-margin eligibility, semantic
context precedence, Y-corner handling, nearest-tick/tie rejection, glyph-size
bounds, literal parsing, sequence checks, all-pair fitting, observed zero and
explicit review/Apply remain unchanged. No tolerance, model, threshold or
scientific value is changed or inferred. Classifier identity advances from
v12 to `graph-text-role-classifier-v13-endpoint-box-tick-lanes`; existing OCR
cache and automatic-detection fingerprints already include this identity.

The acceptance property is retention of the six supplied literal rows and the
same measured calibration anchors for either endpoint, while keeping prior
straddling/outside/nonnumeric behavior. These are owned source invariants, not
a new model accuracy bar. Independent evaluation remains excluded and the
sealed-run budget is zero.

## Owned evidence and review correction

The control supplies plot `(100,80)..(300,300)`, measured X ticks 100/200/300
with literals 1000/1001/1002, and Y ticks 80/190/300 with literals 10/5/0.
Endpoint boxes are forty by sixteen pixels at Y=310, with one center displaced
twelve pixels beyond the first or last tick. Four classifier cases cover both
endpoints before recognition (`NumericExpected`) and after literal recognition.
Two proposal cases call the production classifier and proposal functions,
mirroring the session's X/Y admission filter. Their explicit-role reference
checks pairing equivalence; it is not independent OCR evidence or an invocation
of the actual image-decoding/recognition session.

| Verification stage | Actual result | Test / wall time |
| --- | --- | --- |
| Original classifier control and guards | Four expected failures, twelve guard passes, zero skips | 93 ms / 4.3140342 s |
| Original six-pair proposal control | Two expected failures: five of six rows retained, `INSUFFICIENT_PAIRS` | 177 ms / 2.1963067 s |
| Initial broad overlap implementation | 163 OCR/cache and 90 proposal tests passed | 842 ms / 2.8063748 s; 268 ms / 2.9482694 s |
| Newly reviewed straddling controls against broad overlap | Four expected failures, zero passes/skips | 75 ms / 2.0536914 s |
| Final narrow overlap implementation | 167 OCR/cache and 90 proposal tests passed, zero failures/skips | 848 ms / 2.7686017 s; 294 ms / 2.0106249 s |

The initial broad overlap version was not integrated. Review found that a box
at `(68,294)` of size 40 by 16 straddles the baseline: it previously followed
the Y lane but the new broad branch captured it as X. The mirrored right box
also gained an incorrect X role. Requiring `bounds.Top >= plotBounds.Bottom`
only for the new overlap path preserves the existing behavior. One raw-role
test expectation needed correction: provisional right-hand empty text falls
through to existing `LegendText`, while recognized numeric text is `Other`.
Both map to no axis. Production fallback logic was not changed to satisfy that
test. The earlier 253-test pass is intermediate evidence with incomplete
boundary coverage, not the final acceptance result.

All builds had zero warnings/errors. Original red OCR/App test builds took
17.2503164 / 10.9064409 s. Initial broad green builds took 12.886676 /
55.7164829 s. Straddling red test build took 7.7464782 s. Final production
OCR/App builds took 11.7182794 / 31.8457166 s, followed by the corrected OCR
test-only compile in 8.1352815 s. These are build/check timings, not app latency.

Normal-host commands used one compiler, the existing dependency cache and
reviewed OpenCV restored to App/test runtime roots:

```powershell
dotnet build tests/GraphReader.Ocr.Tests/GraphReader.Ocr.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false
dotnet vstest tests/GraphReader.Ocr.Tests/bin/Debug/net10.0/GraphReader.Ocr.Tests.dll '/TestCaseFilter:FullyQualifiedName~GraphTextRoleClassifierTests|FullyQualifiedName~OcrCacheRegressionTests' '/Logger:trx;LogFileName=final-ocr.trx' '/ResultsDirectory:artifacts/endpoint-label-lane-check'
dotnet vstest tests/GraphReader.App.Tests/bin/Debug/net10.0-windows/win-x64/GraphReader.App.Tests.dll '/TestCaseFilter:FullyQualifiedName~CalibrationTickProposalTests' '/Logger:trx;LogFileName=final-app.trx' '/ResultsDirectory:artifacts/endpoint-label-lane-check'
```

Test-only builds added `-p:BuildProjectReferences=false`. Red filters selected
the newly added endpoint or straddling methods, with separate TRX/log names.

## One changed-source full development confirmation

`--real-dev-endpoint-trace` reuses the full spatial trace's frozen selection,
panel preparation, automatic Rust/native geometry, literal CPU OCR, measured
association and printed-X accounting. Its separate twenty-library runtime map
is checked before selector access; historical profile pins are unchanged.
The probe-only build with `BuildProjectReferences=false` passed without warnings
or errors in 12.95 s. Its owned self-test passed in 0.980 s, covering missing-pin
and incompatible-mode refusal and the inherited terminal/printed-X accounting.
No private input was read by that self-test.

One actual invocation ran from `2026-11-15T15:04:50.6937462Z` to
`2026-11-15T15:05:33.3019725Z`, exit 0, empty stderr, 42.5853229 s wall.
It completed all twelve fixed development projects/panels: eleven prepared
panels and one full-image fallback. Seven returned OCR and five returned no
geometry. Zero panels were unsupported, failed OCR/other execution, timed out,
unfinished or unattempted. All 51 independent cases remained excluded. No
supplied geometry/values, truth scoring, image writing, selection, Apply or
export was used. Sealed payload reads were zero.

| Aggregate | Prior documented full spatial trace | Current full trace |
| --- | ---: | ---: |
| OCR panels / geometry failures | 7 / 5 | 7 / 5 |
| OCR regions / literal numeric rows | 117 / 72 | 117 / 72 |
| Associated labels | 63 | 63 |
| Complete session drafts | 0 | 0 |
| Printed-X review drafts | 2 | 2 |
| Partial X / partial Y drafts | 0 / 2 | 0 / 2 |
| Panels using estimated positions elsewhere | 1 | 1 |

Real development coverage did not improve. The earlier full trace is documented
in [spatial label grouping](SPATIAL-AXIS-LABEL-ROWS-20261115.md); its raw outputs
were already retired, so no byte-for-byte per-case equality is asserted. This
is current integrated-source confirmation with intervening geometry repairs,
not an isolated classifier performance comparison. The separate review-choice
operation's 6/12 result must not be substituted for automatic geometry here.

All five native attempts returned `OPENCV_AXIS_AMBIGUOUS`: three exact-pair
budget failures and two completed searches with multiple ranked candidates.
Nine literal numeric rows remained unassociated. Across the seven OCR panels,
proposal-reason counts were: duplicate association 1, inconsistent spacing 2,
insufficient pairs 4, invalid numeric label 1, invalid range 2, nonmonotonic 2,
observed zero required 2 and unpaired label 3. Counts overlap by panel. Saved
trace diagnosis, rather than unchanged inference, is the next consumer.

Stage totals were preparation 2,537.9439 ms, axes 14,760.1967 ms, OCR
23,732.4235 ms and association 58.3353 ms. Median OCR panel time was
2,800.2315 ms; maximum 6,739.5678 ms. The existing bounded CPU policy was used
for comparability, not the app's automatic GPU selection. These single-run
timings do not establish an improvement or a regression in default-device speed.

Assignment `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`,
inventory `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`,
and selected-source digest
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`
match the prior fixed sample. No unchanged451 or Mirage rerun occurred.

## Runtime and result identities

This is unpackaged source work over `c0f7a2f97f3fd74f82f0c6b92ed4cfa165f40b1f`.
The retained 0.4.53 preview is unchanged and does not contain this classifier
repair. No new dependency, model, license or public contract was introduced;
project source remains Apache-2.0.

| Evidence | SHA-256 |
| --- | --- |
| Final classifier source | `ab51737d5bd4388caedd26c8094ff956cc42c8270d5630319c69eec089b6cf89` |
| Classifier tests | `1cfb52abb7b4b173976c2c95f05476822e81cee1eacc84aecac0aa588222ad77` |
| Proposal tests | `c130ea8c8ddddf362b03050c89e1819f12ff3ac3560e8c816807fcf126c4d7a7` |
| Final App EXE | `47b68cee094370d6d8a5c291ec73bf5cd2bc6234c69ab5f330360e42dc4a2199` |
| Final App DLL | `b585892bd357067cc8086d610364a559c85e069475ed0a806893db6fd35125d8` |
| Final OCR DLL | `81a01e9bf0e51a8523f569d745a69ded76860db1aee32fb4bb5ae998a29c076d` |
| Superseded broad-overlap OCR DLL | `51ae456b9e5b574b28628f3ad2c52022899c13de06671841ea83f90e1fcd1e7c` |
| Original classifier red TRX | `193d734b04ab28cc4085205dae8ed4690af657140129932450ad704c1d5588b2` |
| Original proposal red TRX | `d1b379fbaff67ae112ccfb89ef85d3bff96da6b88b69c0846e8f47b7c93c94e0` |
| Broad classifier green TRX | `1843efd98463416f9f9c0b2c5f81feb14432bae7050bec1e8d3e0895a12ee7db` |
| Broad proposal green TRX | `36caa150013decccb62c9f550770b64c9c9deda468e9533cbc6845e77a81ec74` |
| Straddling red TRX | `96b29ffc7184d886cda297534a314152308e5714ff62fc78a4124573f85e91f1` |
| Final classifier green TRX | `d64e5e0bf8fe67aa10d15822f4a45d24e824e6b15756dddaabcdc45ec56da176` |
| Final proposal green TRX | `dbd67edcebfb349ffaad08f4ecbe515a7874cf8e749662f520f1753278d090e1` |
| Endpoint profile source | `09258a41fbcb9cce0a3c3e8418ab285acb79ebd4e687af54c3c8f005307797d6` |
| Final probe DLL | `54330550e515657cc176ce4c07d30bb1d2e600d73c5ae832d3478c8e8d9078c3` |
| Final probe EXE | `71ab8b42a56b01bb188d7116b6f71561dc58331f2e60e36846485392419ebadb` |
| Full development report, 55,176 bytes | `e606c8d7711b8da6b513d8d23e7af0c020e7e859ed9792a9c334a50814d5a515` |

## Integration and retention

The final owned source checks passed. The changed-source full confirmation
completed, but real automatic-calibration coverage remains unmet; exact-source
Windows CI is pending integration. No real accuracy improvement is claimed from
the owned supplied-geometry/string controls. A separate pixel control was considered
but not generated: nominal rendered text bounds would not establish the actual
detected component condition, and existing frozen worker probes require different
runtime/geometry contracts. No repeated rendering variants were tried.

After the source/outcome is pushed and its consumers finish, retire the 22
completed files totaling 794,979 bytes in `artifacts/endpoint-label-lane-check`.
The four files totaling 58,459 bytes in `artifacts/endpoint-full-development-check`
remain active only for the next saved-trace association diagnosis; retire them
after its aggregate outcome is recorded and pushed. Do not rerun unchanged OCR.
Preserve the source tests, current runtime/model assets, originals, independent
evaluation and user Data. Do not package another build for this isolated fix.
