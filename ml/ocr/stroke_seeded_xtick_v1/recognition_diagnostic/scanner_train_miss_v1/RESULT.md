# Scanner train-only width and miss study

## Scope

Exploratory synthetic train-source diagnostics only. Scanner width 8 remains the frozen selected rule. No production defaults changed, no optimizer or GPU was used, and no sealed, private, consumed-development, or prior validation labels were read for the fit-only attribution. The separate center-rule diagnostic used a newly frozen source-disjoint internal split, excluding the prior validation groups.

## Width study and frozen split

The v2 split grouped sources by connected components formed from duplicate crop and full-panel raster hashes. It had 13 fit components covering 19 source IDs and 5 validation sources/components. One known scanner failure, source `stroke-seeded-xtick-v1-train-27103111`, remained in fit component metadata but was not scored. The frozen scanner report contains 18 successfully scored fit sources. The 86 identical crop rows labeled `background_only`/`excluded` were quarantined from split class counts, not treated as independent evidence.

Fit denominator: 179 positive visible text targets and 192 visible stroke targets. Width 8 yielded 82/179 text-target matches (recall 0.4581), 88/192 stroke matches (recall 0.4583), 793 proposals, 701 unmatched, 4 ambiguous, and proposal precision 0.11097. Width 12 yielded 84/179 text matches, 90/192 stroke matches, 841 proposals, 747 unmatched, and precision 0.10702. Width 16 yielded 87/179 text matches, 93/192 stroke matches, 863 proposals, 766 unmatched, and precision 0.10776. Selection was predeclared as highest fit F1 among widths whose precision was within 0.10 absolute of baseline, tie-breaking on precision then smaller width. Width 8 remained selected.

The frozen width-8 validation result was 26/62 visible stroke matches and 23/56 visible text-target matches, from 103 proposals: 77 unmatched, 0 ambiguous, precision 0.25243, stroke recall 0.41935, and text-target recall 0.41071. This exactly matched the width-8 baseline. It is a small synthetic source-held-out result, not product acceptance.

The first v1 run failed after truth-blind fit/validation scanning but before opening validation labels. Its post-scan validation count check incorrectly reused a fit-only curation row count. The preserved receipt is `artifacts/stroke-seeded-xtick-v1-scan-train-miss-v1/failure.json`. The corrected v2 run passed with no scanner or data changes.

## Fit-only miss attribution

The corrected pixel scanner reproduced the frozen width-8 proposal geometry exactly on every fit panel. Among 97 missed fit text targets, 44 were rejected by the fixed maximum cluster width, 49 had an accepted pixel cluster but no one-to-one geometric proposal match, and 4 were removed by the fixed 4-pixel endpoint guard. The 49 unmatched accepted-cluster proposals were a median 5.684 px and p90 6.747 px from their target centers; only 2 were within the target match radius. Axis-uncertain panels contained 95/97 misses (95/167 uncertain targets missed); clear panels contained 2/12 misses. This associates misses with uncertain axes but does not establish causality.

## Source-derived center correction check

With width 8 and every other scanner setting frozen, a global translation was fit from 111 mutual-nearest proposal/label pairs on 14 source IDs. The frozen runtime-only offset was `dx=+0.454775 px`, `dy=-0.424279 px`; labels are not runtime inputs. The new validation split contained four complete source components and 44 positive targets, with no overlap with the prior validation IDs. Baseline scored 19 TP, 64 FP, 2 ambiguous from 85 proposals (precision 0.22892, recall 0.43182); corrected scored 20 TP, 65 FP, 0 ambiguous from the same 85 proposals (precision 0.23529, recall 0.45455). Both produced 3 proposals on 3 no-visible-target panels. Only source `27103121` changed, from 4 TP/20 FP/2 ambiguous to 5 TP/21 FP/0 ambiguous; source `27103113` remained at 0/17 target matches. The one-match gain with one added FP does not justify integration. Local cluster splitting is a separate untested follow-up.

## Reproduction inputs and hashes

| Artifact | SHA-256 |
|---|---|
| V6 train manifest | `12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a` |
| V2 frozen width report | `a08da3e040112e4476777c7c7d4e21b4882e1f32e1f2a37209bebd5b84984e83` |
| V2 fit truth-blind proposals | `e3d1476b8264e61e35c1187e5d7851cd5abf2f5ff30e889043a8aa0d9eee99ca` |
| V2 frozen width rule | `07aeab1d6c093ffa1df7ddc524b286480a0aae8a717dcb223610e356c4b26930` |
| Fit attribution report | `6c93f77f84a568c455c60a48e588f53066eee9e68c4a5f1c54b81015c52e4ccb` |
| Center correction frozen rule | `725b1283dcac361b66e2e22f99e271c179c28aafc5323e36ec49bcdb30cce8dc` |
| Center correction report | `4957523a6d955f3cb6194daa9d294b9b45af99a19a9d8780f27875dfca2a56d7` |

The code entrypoints pin input manifests/reports and record their dependency hashes:

| Source | SHA-256 |
|---|---|
| `scan_train_miss.py` | `30c8de43650a3730de581b87c0f9ac81a62d71514171356f3c29a39aa2b732dc` |
| `test_scan_train_miss.py` | `d0e5030966fae313da266fe4d0c0934ad0c88a5f28b50f431b9a1302d2fc970e` |
| `attribute_fit_misses.py` | `e8153b35c6349c3e17e2a0cdee99e39ac761a72de113fc95f129947ecddea247` |
| `test_attribute_fit_misses.py` | `af25ce62f2c157e3455093a47a36a2c14519c14ba0fbde82481aee0cb263b3c5` |
| `center_position_experiment.py` | `550bd812b9745ff379b4099c733a8c5a7895c3a996ce3e0fe73a82a62277b78a` |
| `test_center_position_experiment.py` | `772d3385b302399769e91d4f18f0af40d8e60a76171687da1e7f52a4d422e4a9` |

No compiled-source snapshot is needed: these Python sources are the executed entrypoints. Generated reports/proposals are under ignored project artifacts and are bound above.

## Verification

Focused tests passed: `test_scan_train_miss`, `test_attribute_fit_misses`, and `test_center_position_experiment` (9 tests total). Pyflakes passed on these source and test files. The width scanner and center rule were run as modules from the repository root with the project venv Python.
