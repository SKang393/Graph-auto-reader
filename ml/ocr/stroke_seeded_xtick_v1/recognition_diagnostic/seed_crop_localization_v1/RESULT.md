# Seed-crop text localization diagnostic

## Scope and decision

This is an isolated recognition/localization research result. It changes no
production behavior and trains no model. The localization rule was derived
only from pinned synthetic `train` rows, checked on three hash-disjoint
train-source groups, then applied unchanged to the already-consumed synthetic
development probe. No sealed, private, or user data was read.

The rule is a single axis-aligned ROI envelope in the 256x96 seed crop. For
each unique positive fit image, the exact glyph box is translated by the
affine-mapped, truth-blind proposal center. The fit envelope is expanded by a
fixed 2 crop pixels. The resulting seed-relative ROI is
`[-27.8132485, 2.3107218, 31.0058941, 50.2318498]`. The ROI is not tuned to
recognizer predictions or development labels.

## Train-only internal holdout

The curation manifest SHA-256 is
`aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672`; its
V6 input manifest SHA-256 is
`12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a`.
Only `trainable_single_box` positive/background annotations were opened.
Excluded annotations/images were not opened. The 18 fit source groups had 70
unique positive images and 131 unique trainable image hashes. The fixed
3-group holdout had 25 positive rows (22 unique images) and 14 background rows
(11 unique images), with zero hash overlap. All 25 positive boxes fit inside
the ROI; none of the 39 holdout ROIs clipped.

Both readers used the production `OcrCropBatcher`. V5 used its frozen `.65`
confidence and numeric grammar; official PP-OCR used the documented `0.0`
minimum and the same numeric grammar.

| Reader and crop | Positive exact | Positive wrong | Positive empty | Background raw nonempty | Background numeric grammar |
|---|---:|---:|---:|---:|---:|
| Numeric V5, localized ROI | 3/25 | 1/25 | 21/25 | 0/14 | 0/14 |
| Numeric V5, whole seed crop | 0/25 | 0/25 | 25/25 | 2/14 | 2/14 |
| Official, localized ROI | 23/25 | 2/25 | 0/25 | 5/14 | 0/14 |
| Official, whole seed crop | 22/25 | 3/25 | 0/25 | 14/14 | 0/14 |

These rows represent 33 unique images, with all 39 occurrences retained.
Repeated image hashes can have subpixel differences in transformed proposal
centers and therefore slightly different ROIs. Results are occurrence-level.
By source, official localized exact recognition was 15/15 and 8/10 on the two
positive-bearing groups; the remaining group had only backgrounds.

## Frozen consumed-probe comparison

The input used the prior frozen P3/P5 truth-blind score rows at threshold 0.5.
All 116 union-selected proposals were retained (99 P3, 105 P5, 100 unique crop
hashes). A fresh crop-hash check found zero overlap against the complete
localizer train manifest and its internal-holdout metadata. The prior source,
source-identity, full-panel-hash and classifier-training overlap audit also
remains zero for the admitted rows. Nine earlier classifier-overlap rows had
already been quarantined upstream and are absent from the frozen 433-row score
input. The 10 no-visible panels and all 181 visible truths were included.

Classifier geometry parity reproduced the frozen baseline exactly:

| Detector proposals | Proposals | Matched | Unmatched | Precision | Recall / 181 | False proposals on no-visible panels |
|---|---:|---:|---:|---:|---:|---:|
| P3 at 0.5 | 99 | 78 | 21 | 0.7879 | 0.4309 | 2 |
| P5 at 0.5 | 105 | 82 | 23 | 0.7810 | 0.4530 | 8 |

The following reader-confirmed metrics filter the same selected proposals by
the unchanged reader rules: V5 confidence at least 0.65 plus grammar, or
official grammar with the documented zero minimum.

| Classifier / official crop | Confirmed proposals | Matched / false | Precision | Recall / 181 | Exact / wrong among matches | Exact / 181 |
|---|---:|---:|---:|---:|---:|---:|
| P3, localized ROI | 89 | 75 / 14 | 0.8427 | 0.4144 | 73 / 2 | 0.4033 |
| P3, whole seed | 38 | 30 / 8 | 0.7895 | 0.1657 | 27 / 3 | 0.1492 |
| P5, localized ROI | 90 | 79 / 11 | 0.8778 | 0.4365 | 77 / 2 | 0.4254 |
| P5, whole seed | 40 | 32 / 8 | 0.8000 | 0.1768 | 29 / 3 | 0.1602 |

Neither localized official comparator emitted a grammar-confirmed proposal on a
no-visible panel. It still emitted grammar-valid text for 14 P3 and 11 P5
unmatched proposals across all panels, so localization does not remove false
text on visible panels. Numeric V5 produced zero grammar-and-confidence
confirmed proposals in either crop mode. This is a reader compatibility result
on preselected detector crops, not an end-to-end acceptance result.

## Execution and verification

- No optimizer or training ran. Both OCR inference passes used
  `CPUExecutionProvider`.
- The localized ROI was applied through the production cropper. The C# self-test
  passed for the 128x32 and 320x48 crops and verified original polygon mapping.
- Sixteen focused Python tests passed; `py_compile` and project-local `pyflakes`
  passed.
- Development truth finalization required three parser/schema corrections
  after inference. The receipts are preserved in the ignored run artifacts.
  None caused inference reruns or read truth before both reader passes had
  completed. The finalizer now binds the exact completed recognition output and
  input hashes before reading the consumed probe labels.
- The dev recognition output was made before six metadata-only additions to the
  dev C# source. Its execution receipt binds the reconstructed executed source
  snapshot and actual runner assembly. The recognizer model, crop geometry,
  inference path, and predictions were unchanged by those later output metadata
  fields.
- The train recognition aggregation first keyed duplicate crops by hash, then
  rejected tiny ROI differences for identical raster hashes. Both failed
  attempts are recorded; the final runner keeps all occurrences and keys
  results by unique case ID.
- Three dev finalization attempts are also recorded. First, the C# property was
  `CaseId`, not `ProposalId`; second, the pinned manifest split literal was
  `exploratory-synthetic-diagnostic`; third, the finalizer needed to add a
  `ProposalId` alias after joining geometry from the truth-blind input. The
  first two stopped before annotation access; the third read dev truth only
  after both readers had completed. None required another inference pass.

Commands (PowerShell from project root):

```powershell
& .\tools\gpu-training\venv\Scripts\python.exe -m unittest ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.seed_crop_localization_v1.test_prepare_localization ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.seed_crop_localization_v1.test_finalize_train_holdout ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.seed_crop_localization_v1.dev_comparison_v1.test_prepare_dev ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.seed_crop_localization_v1.dev_comparison_v1.test_finalize_dev
$env:PYTHONPATH='E:\Graph-auto-reader\tools\qa-lint'
& .\tools\gpu-training\venv\Scripts\python.exe -m pyflakes ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/prepare_localization.py ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/finalize_train_holdout.py ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/dev_comparison_v1/prepare_dev.py ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/dev_comparison_v1/finalize_dev.py
$env:DOTNET_CLI_HOME='E:\Graph-auto-reader\tools\dotnet-home'; $env:NUGET_PACKAGES='E:\Graph-auto-reader\tools\nuget-packages'; $env:TEMP='E:\Graph-auto-reader\tools\tmp'; $env:TMP='E:\Graph-auto-reader\tools\tmp'
dotnet run --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/Localization.csproj -- --self-test
dotnet run --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/dev_comparison_v1/LocalizationDev.csproj -- --self-test
& .\tools\gpu-training\venv\Scripts\python.exe ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/prepare_localization.py
dotnet run --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/Localization.csproj --no-restore -- E:\Graph-auto-reader E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-seed-crop-localization-train-v1\input.json E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-seed-crop-localization-train-v1\recognition_v3.json
& .\tools\gpu-training\venv\Scripts\python.exe ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/finalize_train_holdout.py
& .\tools\gpu-training\venv\Scripts\python.exe ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/dev_comparison_v1/prepare_dev.py
dotnet run --project ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/dev_comparison_v1/LocalizationDev.csproj --no-restore -- E:\Graph-auto-reader E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-seed-crop-localization-dev-v1\input.json E:\Graph-auto-reader\artifacts\stroke-seeded-xtick-v1-seed-crop-localization-dev-v1\recognition_v1.json
& .\tools\gpu-training\venv\Scripts\python.exe ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/seed_crop_localization_v1/dev_comparison_v1/finalize_dev.py
```

The frozen inference outputs and aggregate reports are generated, ignored run
artifacts under `artifacts/stroke-seeded-xtick-v1-seed-crop-localization-{train,dev}-v1/`.
They are retained locally for parent review; no generated images/models are
part of this source batch.

## Provenance hashes

Pinned inputs and models:

| Artifact | SHA-256 |
|---|---|
| Train curation manifest | `aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672` |
| V6 train manifest | `12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a` |
| V6 materializer | `98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d` |
| Curation source | `7e8a32dda6b5cf74f013db0992bd66ed894301dca21cf94ea32f64f1275c32df` |
| V5 numeric model | `9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84` |
| Official PP-OCR model | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| Official alphabet YAML | `27e91d0582f40168aa218303c76e184bc78fa7a5d105aad0cfbad8458b441067` |
| Ambiguity model | `b8e2773ca3966469081875fc36b3981ef4eb458356d8dfdae2be2722602f0096` |
| V5 threshold-selection evidence | `e5095b63221b7a745e48ead61011a7794e5da54e0fc24fd641d785bc09257260` |

Train-only run artifacts:

| Artifact | SHA-256 |
|---|---|
| Train protocol | `42d83e1ee179ac9b4427acf783b4af554fccbec149fe22ed1953e88d2aa8da8c` |
| Truth-blind inference input | `4cb104be1016d0c541e559fb7701aaba26f52fc1fc7abfbe89625a411c1c9520` |
| Separate truth rows | `6962bf7db78609f5a234edc67130fdf1b1deabc4af06ab98b5306bdc8b9b26d3` |
| Reader output | `f74d72212f2afefa5b0d2ec3563dcc86329696c3a7f8c657988cbf491579207c` |
| Final train report v2 | `bad3067d2e40838194bf508aeb0f577c55d5e0c68668c56a697e0acecbb88a81` |

Consumed-probe run artifacts:

| Artifact | SHA-256 |
|---|---|
| Frozen score rows | `30be7b889ac536cabdaba0b7155b590c08c6d5d2f0505cf1c34a7496725f21fe` |
| Candidate manifest | `efb3da7ee27baaea3ffa10b2202c0419d174ffea3b375667264de378307ad1cd` |
| Previous P3/P5 overlap audit | `3b2b14340cb0dc4c40b5e0d8417cc83f226c14d32b1c6efbdec87f23d7d01056` |
| Previous paired P3/P5 report | `5612b128c796c224084493b3e4890cc6f5c0a22eb5d36fa1472ddd62c952d172` |
| Fresh truth-blind input | `8940fe0579ffa209985fe1aa04823c77735d92971932b1527b967e62d9e9fb49` |
| Fresh overlap preflight | `3d886ff888207edf076331a6a5b1351a89e13a3d17399efd8f1e2d3b45ad0eee` |
| Dev protocol | `5366fb1b305bfe166535d161b0c0e06a4c069c87b7a2e3ecbdd10a018a5e405f` |
| Reader output | `2e3666a0d32b3156efc7cc443def57049ddd60c299201c30bb6a18c2b0105a7b` |
| Final dev report | `7849228836a9a2d939195af137a4ac1c2503e8a7b7413fd453ef16be3dcacbc5` |
| P3 checkpoint | `8b1f8ce7d9897e3db19e71a4f21576b92e51ae56a90aa1fec9fb1df364820c92` |
| P5 checkpoint | `d34c1b9ea9674f4b7d7a2dd41ca40d872bdc338b5d30092f7cc997c41d3e7445` |
| Consumed synthetic probe manifest | `127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba` |
| Executed dev runner source snapshot | `8c364f49ad11bb68465f1cdb41c7e4e5ab1c0d3029b9b34d1160514bee1d8193` |
| Executed dev runner assembly | `69dc9b9924701b36c40e54173a58e7233009cd466be5c6cbdf70f4b5bc148415` |
| Runner execution receipt | `edf7fe42de2efaba30b5b139fd144d7d7bdaedfe4d9ace49ee8562eef6a0e57a` |

Source and test identities (the dev C# source hash below is the current file;
its executed pre-metadata version is separately bound above):

| Source | SHA-256 |
|---|---|
| `prepare_localization.py` | `528f7a248786e4b675dd498ceda0444161e624076d72fc199131725ce6fd6092` |
| `test_prepare_localization.py` | `3480462d7005ec3c32eb42b03616efa21155ad85f1bb9be7defb8c66bf8c172c` |
| `finalize_train_holdout.py` | `2ec9620535dc1c7dd1034ae24dcfc97714e3d9be915adf518dc199d89ef3a88e` |
| `test_finalize_train_holdout.py` | `e3fc95b4acd15e4cf7a403d9f822d4e8d6b4ba5c231f37ee37b082e798035441` |
| Train `Localization.csproj` | `d69f515b72e669328a9f7b0de4593e9ae3d423f7e008de4863dd0a74525a7adb` |
| Train `Program.cs` | `68a3514b7d9b12869f060101e5c7b8efd86365a68a473a47faf4c7333e35ab79` |
| `protocol_train.json` | `42d83e1ee179ac9b4427acf783b4af554fccbec149fe22ed1953e88d2aa8da8c` |
| Dev `prepare_dev.py` | `8510089d77b6dfb243a811e9f2cf5b11741e94327c84ed12efe2cab7753911ab` |
| Dev `test_prepare_dev.py` | `340a43c8926305ec2b64c283a9086be0baf20194038e6da3794f87876aadf132` |
| Dev `finalize_dev.py` | `44a19d0f24cd2b393757aa4167cf2cde0e0f8d458adfadc3b8cf6d4f2b63eef4` |
| Dev `test_finalize_dev.py` | `e65995226d059c599ff39cfd7f77aaf12392f481a618fe7a6064228b814cc7d8` |
| Dev `LocalizationDev.csproj` | `7c71fe070719cd11b84464974cb06345c619406862e992b0304d7482603d8e70` |
| Dev `Program.cs` current | `d4cf8990a80c639854a553a5ec21fc26e674c1ee9b440327d5733389a6bd40c1` |

Preserved failure receipts:

| Receipt | SHA-256 | Correction |
|---|---|---|
| Train `recognition_failure_v1.json` | `b4cbef51b83bad2fadcfedb7eaa2585d6266cef834bfadc3d56852320ce67816` | Key outcomes by proposal case ID, not duplicate image hash |
| Train `recognition_failure_v2.json` | `39830eabb3033cfa47c8f51f3c2de847b666de05e09494ea3b0073f2763b8f46` | Retain duplicate rows with their own affine ROI |
| Dev `finalization_failure_v1.json` | `2b0bb3edecdce31a08e1b1f9902563fc746d03441030a2f4574f4b414d01b316` | Use C# `CaseId` when joining completed recognition rows |
| Dev `finalization_failure_v2.json` | `d9c1ada2f75f3dd133fca116b56e26ee098ad978d0cb9203d2f11cbb30d8c4b9` | Enforce the pinned exploratory split name |
| Dev `finalization_failure_v3.json` | `527845cfe68146e681844ae3b528e9b64d10876405912bb98121f4ad4f278548` | Alias verified `CaseId` to `ProposalId` after truth-blind join |

The post-run dev source currently includes only later proposal-metadata output
fields; executed inference bytes are preserved by the source snapshot and
assembly hashes above. The accepted feature remains isolated and is not a
production recognizer integration.

## Source batch

The intended source files are the 13 Python/C# project, test and protocol files
under this directory plus this `RESULT.md`. The two files under
`reproducibility/` are copied non-compiled evidence: the frozen dev protocol
contains hashes and run flags only, while the C# snapshot contains runner code,
not image or annotation payloads. The snapshot is a reconstruction from the
post-run source by reversing only six metadata-output additions; it is not
the original executed source bytes. The execution receipt binds the actual
runner assembly separately, so the source snapshot must not be described as
byte-for-byte execution provenance. Their hashes are:

| Evidence file | SHA-256 |
|---|---|
| `reproducibility/executed_Program_v1.cs.snapshot` | `8c364f49ad11bb68465f1cdb41c7e4e5ab1c0d3029b9b34d1160514bee1d8193` |
| `reproducibility/dev_protocol.json` | `5366fb1b305bfe166535d161b0c0e06a4c069c87b7a2e3ecbdd10a018a5e405f` |
| `reproducibility/Localization.csproj.pre-exclusion.snapshot` | `d69f515b72e669328a9f7b0de4593e9ae3d423f7e008de4863dd0a74525a7adb` |

The train project used the SDK's recursive default C# glob. After the
`dev_comparison_v1/Program.cs` subdirectory was added, a later rebuild would
have included that second entry point unless excluded. There is no evidence
that the dev runner existed or was compiled in the original train inference,
so this is recorded as a post-layout build hazard, not an original-run defect.
Added an explicit
`Compile Remove="dev_comparison_v1/**/*.cs"` so the parent train project
contains only its own runner. The original pre-exclusion project bytes are
retained by the snapshot hash above. The corrected project and the separate
dev project both build with zero warnings and zero errors; these builds do not
change or rerun the already-recorded inference.

Commit the 13 Python/C# project, test and protocol files, this `RESULT.md`, and
the three noncompiled evidence files above (17 files total). Do not include
`bin/`, `obj/`, Python caches, or
ignored run artifacts. No existing results outside this research directory
were changed.
