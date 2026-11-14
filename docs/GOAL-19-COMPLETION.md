<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Goal 19 completion record

## Problem

The existing internal build had no maintainable live portable preview, ordinary
application composition depended on recorded data or an all-or-nothing
production failure, and production release inputs lacked approved models,
complete OpenCvSharp native provenance, private supplied-example evidence, and
clean-machine distribution evidence.

## Implementation

- Added a real-data WPF manual workflow for image import, three-anchor
  calibration, series creation, editable points and phase dividers, explicit
  shared-baseline/probe relations, save, autosave, recovery, reopen, and CSV
  export.
- Kept printed X, estimated X, observation order, and modification history
  separate and auditable.
- Added deterministic marker symbols, unknown-fill rendering, localization,
  zoom/pan controls, empty-state magnifier behavior, and failure-safe commands.
- Replaced all-or-nothing production composition with a manual-capable,
  stage-aware registry. Automatic stages remain disabled unless their complete
  production evidence is approved.
- Added a fail-closed artifact-mask adapter boundary. Production Auto Detect
  now requires normalized, checksum-bound, original-pixel evidence for arrows,
  brackets, legends, and connecting-line intersections before the mask composer
  or complete detector can report approved.
- Added a concrete lazy bridge for the existing frozen marker-center artifact
  head. It does not add a model task or change a schema. It composes only when
  the packaged production-approval evidence is a parsed and rehashed direct
  full-frame artifact-mask report whose embedded dataset manifest, evaluator
  source, and split seal pass their own hashes. The evaluator source is frozen
  at SHA-256 `58c3cd9ec6bf2255c4b5d2cc9d865e2d37f58b0688a8bceda78ff523398a9f5f`;
  the dataset schema, public seed, fixture hashes, inference-locked seal, and
  exact fixture membership are validated. Manifest and report identities and
  metrics must match, each fixture must have exact counts, and false
  positives, false negatives, duplicates, arrow-shaft hits, arrowhead hits, and
  every other prohibited-structure hit must be zero. A 2048 by 2048 regression
  also proves repeated marker frames reuse private immutable planes instead of
  cloning each full-resolution mask. The current rejected marker-center
  manifest does not satisfy that gate.
- Added the missing local ONNX OCR detector and production factory without
  changing a frozen schema. Detection consumes a checksum-bound dense
  probability map, maps deterministic regions to original pixels, and rejects
  invalid output, provider drift, and cancellation. Recognition now supports
  exact per-channel preprocessing. The shared inference request can bind a
  CPU-only policy with a provider-specific cache identity. Production
  composition rehashes and strictly parses both approved OCR manifests and the
  same embedded evaluator, sealed split, predictions, and runtime results.
  It independently derives all fixed metrics and executes both exact payloads
  through a two-item CPU preflight before exposing the adapter. Tampered
  resources and non-ONNX payloads fail closed. The current store contains
  neither approved OCR task, so normal production remains unavailable.
- Added atomic development-portable build, launch, and watcher scripts.
- Added an opt-in, checksum-bound development-portable replacement for the
  exact reviewed source-built OpenCV runtime. It validates provenance and the
  ignored maintainer attestation, rejects tampering and ambiguous destinations,
  and records that clean-machine and release approval remain false.
- Added independent packaging and verification for multi-file model payloads.
- Added a checksum-bound PDFium dependency review that maps every retained
  target label and system import to an explicit source, license, notice, or
  build-only disposition without promoting the runner.
- Added internal-only PDFium portable staging that binds the exact runner,
  deterministic dependency-mapped notice, source evidence, and approval-false
  metadata while rejecting tampering, ambiguity, traversal, and reparse input.
- Bound the exact Real-ESRGAN NCNN runtime to an unmodified Visual Studio VC
  Redist `vcomp140.dll`, private authority attestation, Authenticode identity,
  reduced inventory, notice hashes, and direct public synthetic smoke. This is
  redistribution provenance only; every production approval remains false.
- Added a bounded WPF composition regression that exercises the real manual
  ViewModel commands, canvas edits, save, recovery, reopen, and export without
  selecting recorded fake graph data.
- Changed async enhancement composition to resolve the exact manifest, notice,
  runtime dependency, and model checksums before advertising a local developer
  stage. A rejected configured model remains unavailable with its exact
  diagnostic while the immutable-original manual workflow continues.
- Added a fail-closed local portable validation harness for Unicode and space
  paths, shared and normal portable data roots, endpoint observation, registry
  observation, read-only diagnostics, and file-system tracing.

## Portable preview

The authoritative current preview is the immutable build selected by ignored
`artifacts/dev-portable/latest.json`. That record supplies the exact clean
commit, build time, executable path, executable SHA-256, and reviewed OpenCV
runtime SHA-256 after every stable checkpoint. The current verified behavior is:

- version: `0.0.21`
- dirty development build: no
- running process: responsive visible window titled `Graph Auto Reader`, exact
  Chandler path supplied by `--open-image`
- current full Release rerun: PASS, 814 passed and 9 expected hardware or opt-in skips; all 87 App tests passed on the current interactive desktop
- production-model IDs reported available: `0`
- automatic stages reported unavailable: `6`
- mutable root: `artifacts/dev-portable/Data`
- mutable entries after empty launch: `Autosave`, `Cache`, `Logs`, `Recovery`,
  and `Settings` directories; no graph/project/export file
- the regenerated empty-state PNG is nontransparent with SHA-256
  `055a5d305f57a3c997a801f133baa562c898aecb696831843f29555906d21818`;
  the harness now rejects transparent pixel surfaces as inconclusive instead
  of emitting false evidence
- the real Chandler window changed after invoking the actual Zoom In control
  and again after invoking Fit Graph; the zoom and final fitted capture SHA-256
  values are `262110e2f6f68a84e275f62b62a21cb8a983b6bd22df796a790752d968cfeaf0`
  and `b3f85b6ef8e02fd7caef701fa6fe72304fef685c81582f9573c42a7fc46ccf0c`

Start the watcher with
`powershell.exe -File packaging/Watch-DevPortable.ps1 -BuildOnStart -FastTestsOnly -AllowDirty -LaunchAfterBuild`.
Launch the latest stable build with
`packaging/Run-Latest-DevPortable.cmd`. Import, tab management, manual
calibration, series and point editing, phase editing, project save, autosave,
recovery, reopen, and CSV export are usable. All six automatic stages remain
unavailable.

The Goal 20 recovery promoted this internal checkpoint to `0.0.20` only after
the user supplied the private Chandler graph. The real `ManualPreview` WPF
composition then passed import, calibration, manual edits, phase editing,
save/reopen, export, autosave/recovery, zoom, pan, fixed magnifier, and rendered
window evidence without fake graph data. Purpose-aware portable isolation also
passed 7/7 local gates while retaining attributed Windows, WPF, .NET, and GPU
cache activity as evidence-bearing warnings. See `docs/GOAL-20-COMPLETION.md`.

This remains local isolated-profile evidence, not clean-profile or clean-VM
evidence.

## Production adapters

Import, manual calibration, review, save, recovery, and export remain available
in Production composition. Automatic stages currently report:

| Stage | State | Exact blocker |
| --- | --- | --- |
| Enhancement | Unavailable | Exact runtime redistribution provenance is reviewed, but local-adapter, scientific, CPU fallback, clean-machine, production, and release approvals remain false. |
| Axis | Unavailable | Deterministic adapter exists; OpenCvSharp native redistribution audit is blocked. |
| OCR | Unavailable | The executable detector, recognizer, strict two-manifest factory, CPU provider binding, checksum-bound axis-mask derivative, exact BGR24 detector and recognizer path, and strengthened fail-closed composition boundary are implemented. Color mode is part of stage identity; detector masking whitens Gray8 and all BGR channels; recognition crops preserve original BGR samples; missing or non-BGR production metadata fails before provider execution. The explicitly selected DB postprocessor now performs OpenCV contour extraction, default fast mini-box scoring, rotated minimum-area boxes, geometric unclip, original-coordinate quadrilateral mapping, deterministic raw-contour caps and IDs, and cooperative cancellation. The `resize_long` path includes the pinned small-image padding, truncation, and 128-stride ceiling alignment semantics. Production metadata must declare `db_postprocess_v1`, `score_mode=fast`, and the reviewed PP-OCR values. No approved OCR payload pair exists. Project numeric Candidates 1, 2, and 3 failed the frozen validation gate and exhausted their budget. The exact official PP-OCRv5 pair passed reproducible conversion and parity, then failed its once-only frozen 220-case public and sealed evaluation: exact match `0.0`, CER `1.0`, role accuracy `0.03` and `0.02`, detection exact count `18/220`, 195 false text regions, and false regions on 7/20 exclusions. Independent composed marker-stage evidence was absent. The reviewed model license and change notice are tracked, but no OCR manifest, model-store entry, or package payload exists. Do not tune or rerun against the exposed split. |
| Markers | Unavailable | Marker-center exhausted its budget after one public false positive. The unchanged-weight probability-runtime classifier passed selection, public-v3, disjoint confirmation-v3, exact package discovery, and CPU/DirectML runtime validation. The concrete artifact-mask runtime bridge exists under the frozen marker-center task, but the current marker-center manifest lacks the separately required direct full-frame artifact-mask approval profile. The stage still requires an approved center model and checksum-bound artifact-mask evidence. |
| Legends | Unavailable | Deterministic reasoner requires approved OCR and marker evidence. |
| Phases | Unavailable | Deterministic reasoner requires approved axis, OCR, and marker evidence. |

No automatic stage executes in Production with candidate assets.

Production batch orchestration now checkpoints only fully reviewed panels. A
direct two-image composition test completed panel one, cancelled while panel
two entered detection, retained panel one's projected automatic review and
both immutable source hashes, then reran to a complete ordered result. This
closes the executable cancellation and resume behavior in the adapter boundary.
The mandatory release gate remains blocked until the same scenario runs with
the approved packaged OCR and marker-center payload set.

## Models and provenance

`models/manifest/PRODUCTION_MODEL_MATRIX.md` records exact current candidates.
There are seven manifests and one installed, checksum-verified,
release-eligible model file in the ignored production model store.

Bounded ignored development evidence resolved the model decisions. Only the
marker classifier reached approval:

- A release-minimal Real-ESRGAN anime x2 runtime excludes the nonredistributable
  debug OpenMP DLL, demos, and unused models. The authorized-vcomp profile uses
  unmodified Microsoft version `14.44.35211.0`, SHA-256 `55aba23c...164`, and
  passed a direct exact-2x public synthetic smoke in 2193.913 ms. Redistribution
  provenance is reviewed only for this profile; local-adapter, scientific,
  memory, offline CPU, clean-machine, production, and release gates remain false.
- The official `RealESRGAN_x2plus` hash was reverified, but its PyTorch
  checkpoint is incompatible with the current NCNN adapter and no conversion
  is authorized.
- Repair-v2 marker-center `P1`, `P2`, and `P3` are consumed. P3 passed all
  selection and CPU ONNX parity gates, then failed its once-only public gate.
  `public-zigzag` and `public-probes` were exact; `public-stair` produced one
  false-positive center. There were zero false negatives, duplicates, and
  prohibited text, axis, tick, divider, bracket, arrow, legend, or intersection
  hits. The three-candidate budget is exhausted, confirmation was not
  authorized, and no marker-center model is approved.
- The sealed current packed marker classifier reached shape, fill, artifact,
  and minority-shape F1 `1.0`, but its direct packed ONNX maximum absolute
  error was `2.288818359375e-05`, above the `1e-5` gate. A new
  `marker-classifier-production-runtime-repair-v2` P1 retained the exact
  checkpoint with zero optimizer steps and passed the full fixed 140-case
  selection gate: shape F1 `1.0`, fill F1 `0.9907389542735867`, artifact and
  minority F1 `1.0`, and CPU ONNX parity `2.0265579223632812e-06`. One
  once-only public-v3 gate then passed shape F1 `0.9907246376811594`, fill F1
  `0.9440313111545988`, artifact and minority F1 `1.0`, and CPU ONNX parity
  `1.7285346984863281e-06`. The disjoint confirmation-v3 gate also passed, the
  C# packed-runtime decoder passed on CPU and DirectML with maximum provider
  difference `3.5762786865234375e-07`, and the exact payload
  `26f9304f1689053a0b94aa896a1e239f6ade1e5c1920736a3535c1b32f803b8a`
  is approved for checksum-bound production discovery and packaging.
- The project-trained graph-numeric CTC experiment used only procedural vector
  glyphs. Candidate and single repair held-out exact match were both
  `0.015625`; the repair CER worsened to `0.932710`. Its exact ONNX passed an
  execution-only CPU/DirectML probe, but no OCR manifest or approval was made.
- A distinct dense spatial-sequence V2 experiment used disjoint renderer,
  glyph-family, and degradation families. Its one-factor contrast repair
  improved held-out exact match from `0.046875` to `0.10546875`, but held-out
  CER remained `1.201399`. The exact repair ONNX passed CPU and DirectML tensor
  execution, but no manifest or approval was created.
- Official PaddleOCR metadata is pinned to source revision
  `33cbdd9deb2e00f61e7966db70669b249c005a37`. The exact detection and English
  recognition archives were downloaded, hashed, and inventoried at
  `50446e5d01ac2a73d5319c89513281f6578414c888c602f9af13f93feefffc58`
  and `e595b4cf2ffad19fbb5a61ba345d63939577a3ab8717b6e5995642590c9101b4`.
  All six payloads match immutable official PaddlePaddle model repositories at
  `0d63e78e2b680928f6b1747d76a08db6e645efb7` and
  `267c36e24c331595590fe7bd72bde2436fd286f2`. Their exact model cards declare
  Apache-2.0. The locked CPython 3.11.9 toolchain installed entirely offline
  from 27 checksum-bound selected wheels. The signed PSF installer, exact venv
  and converter launchers, all 29 installed distributions, and 14,894 installed
  RECORD files verified, `pip check` passed, and two independent conversions per
  model produced byte-identical opset-11 ONNX outputs with no external tensors.
  Detector SHA-256 is
  `d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb`
  with maximum CPU raw-tensor difference `1.349323213162279e-07`; recognizer
  SHA-256 is
  `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`
  with maximum difference `2.205371856689453e-06`. The ignored report is
  checksum-bound to the final harness and remains `production_approved=false`
  and `release_ready=false`. The reviewed Apache-2.0 model license and ONNX
  change notice are tracked.
- The official pair then ran once against a split frozen before inference with
  100 validation text cases, 100 sealed text cases, and 20 exclusions. No
  Chandler or private data was used. Split SHA-256 is
  `1fc3b2e72f89cbfb0d8854ec8701368e7ae764cbd5c6fef17b7e497d06ec9f09`;
  fixture ZIP SHA-256 is
  `69eeeff73f4cfd2dd6580ad9538f1a89527f8e5b320ce6a9cd7155d2bd22ea99`.
  The report failed with validation and sealed exact match `0.0`, CER `1.0`,
  role accuracy `0.03` and `0.02`, detection exact count `18/220`, 195 false
  text regions, and false regions on 7/20 exclusions. It lacked independent
  composed marker-stage evidence. Report SHA-256 is
  `3fceb688d44a2f4c322e40bdfa3dd09ca74ec6d5742ab257ae102ccf76e1c44b`.
  No manifest, model-store discovery, package discovery, or approval exists.
- Project numeric OCR Candidate 1 completed its recovery with zero optimizer
  steps and unchanged checkpoint SHA-256 `6e941b2b...8235`. It failed with
  validation exact `0.3359375`, sealed exact `0.119140625`, sealed CER
  `0.4452018877818563`, and validation role accuracy `0.5828125`; no ONNX was
  exported. Candidate 1 is consumed and rejected. Candidates 2 and 3 are
  unregistered, and no model, private result, or approval exists.

The exact OpenCvSharp slim package remains blocked:

- native package SHA-256:
  `281551a6c032d1aab316db9c1817bcded5a85188b24b2efd12c02665e7233817`
- `OpenCvSharpExtern.dll` SHA-256:
  `1fa122bdb8e94175e7719fb8aaf2ab211268a756f5d0c7a13c710ed79ae30cd`
- native payload files: 1
- direct imported system DLLs: 9
- included native license/notice/SBOM/link-map files: 0

The binary is statically linked and direct PE imports cannot establish the
complete linked dependency set. No native inventory or third-party notice file
was fabricated. `redistribution: false` and `reviewStatus: blocked` remain.

A separate pinned source-build fallback compiled and passed 3/3 focused
axis-provider tests without altering that decision. Canonical path mapping made
two clean builds byte-identical: the 7,965,696-byte DLL SHA-256 is
`87c12460daba638b36e916ea2bb832d0759fbf094b8639919a7ce11b0cca5791`
and the 16,390,044-byte linker-map SHA-256 is
`e7f9f768b82172b9f2021b2a469de371962655bd0833c8f214bbefdad05a8a77`.
All 15 evidence entries now map to a candidate disposition and four complete
notice sections validate. The ignored scoped maintainer attestation validates
the five linked Microsoft static-runtime entries. Four committed procedural
axis families now produce byte-identical canonical results with the package and
source DLLs, with evidence SHA-256
`cafb1df2d8b3d959d3de6e7221115634585ed6c7dc9e34994075003f67f85ac7`.
The complete integration assembly passed 92/92 executed tests with the source
DLL, and isolated package/source WPF publishes both exited `--portable-smoke`
with code 0. The source build now replaces the NuGet DLL only in the
checksum-bound internal development portable. The production common publish is
unchanged because clean-machine load and workflow evidence remain mandatory.

The exact minimal PDFium runner is also reproducible: two builds from revision
`2870fa9244b0f0f69fb743fab1e08deefcb07b2b` produced byte-identical SHA-256
`efd13a38cf3cd8e04d8284a42fff42923267293170424153b1a2a96dbf6fe8ea`.
Independent review confirms all 240 retained target labels map exactly once to
15 component dispositions, 16 checksum-bound notice sources cover linked code,
NASM is build-only and unshipped, and the PE imports are exactly four Windows
system APIs. Runtime packaging, clean-machine execution, and release approval
remain mandatory and absent. An internal-only staging policy now copies exactly
the runner, dependency-mapped notice, and approval-false metadata. The staged
runner SHA-256 is `efd13a38...8ea`, and 55 PDF tests plus eight staging and
security scenarios pass without promoting the candidate.

## Files changed

- WPF application, controls, localization, composition, runtime paths, models,
  services, and ViewModels under `src/GraphReader.App/`
- application and integration regressions under `tests/`
- development portable scripts and tests under `packaging/`
- PDFium review policy, exact public inventories, deterministic notice, and
  fail-closed tests under `packaging/pdfium-source/review/`
- Windows build and artifact verifier multi-file model support
- pinned, fail-closed OpenCV source-build scaffold under
  `packaging/opencv-source/`
- OpenCV source review policy under `packaging/opencv-source/review/` and the
  public distribution notice under `LICENSES/`
- fail-closed portable runtime validation under
  `packaging/portable-validation/`
- Real-ESRGAN partial runtime evidence and marker candidate manifests
- failed V1 CTC and V2 spatial-sequence OCR experiment source, tests, and audit
- production model matrix and `docs/1.0-READINESS.md`

Private images, model weights, generated datasets, build output, and evidence
files are not tracked.

## Tests and commands

- `dotnet test GraphAutoReader.slnx -c Release --no-restore` in the current
  Release run: 814 passed and 9 expected hardware or opt-in skips; all 87 App
  tests passed on the current interactive desktop.
- focused manual workflow: 8/8 passed.
- bounded WPF manual composition: 1/1 passed.
- Goal 19 application/composition focus: 18/18 passed in reviewer verification.
- `packaging/tests/Test-DevPortable.Tests.ps1`: 8/8 passed, including
  fail-closed single-file and multi-file model discovery plus pre-launch
  executable checksum rejection through the durable Windows PowerShell 5.1
  launcher path.
- `packaging/tests/Test-ReleaseArtifact.Tests.ps1`: 60/60 passed, including
  canonical UTC metadata, mandatory direct-evidence gates, and exact-binary
  rejection.
- Super-resolution tests: 57/57 passed.
- Marker Python pipelines: 30/30 passed with four exporter deprecation warnings.
- Marker .NET assembly with the exact ignored candidate: 83/83 passed.
- OCR Python pipelines: the focused official archive, conversion, frozen-gate,
  stale-authority, reproducibility, ONNX checker, and parity suite passed
  142/142. The official evaluation itself ran once and failed its fixed model
  thresholds as recorded above.
- OCR .NET assembly: 110/110 passed with two expected opt-in provider skips.
- OpenCV source-audit behavior tests: 6/6 passed in Windows PowerShell and
  PowerShell 7. Retained lock/cache/triplet inputs, preflight metadata, and both
  CMake toolchain caches are bound fail closed to the tracked source lock.
- Source-built OpenCV focused axis-provider tests: 3/3 passed.
- OpenCV repeat-build comparison: passed once; DLL and linker-map hashes match.
- OpenCV candidate review policy and negative-path tests: passed; the ignored
  scoped maintainer attestation validates.
- OpenCV public-axis runtime parity: 4/4 fixed procedural families produced
  exact canonical output; 92/92 executed integration tests and both WPF smoke
  publishes passed with the source DLL.
- Reviewed OpenCV runtime packaging: 6/6 provenance-only, release-promotion,
  tamper, blocked-gate, and ambiguous-path cases passed. A self-contained
  development portable replaced the NuGet DLL with exact SHA-256
  `87c12460daba638b36e916ea2bb832d0759fbf094b8639919a7ce11b0cca5791`
  and passed `--portable-smoke`; metadata preserved
  `cleanMachineEvidence=false` and `releaseApproved=false`.
- PDFium dependency review policy: 4/4 positive and negative cases passed under
  Windows PowerShell and PowerShell 7. The exact policy validates 240 labels,
  16 notices, and 4 system imports while approval remains false.
- Portable validation self-tests: 7/7 in Windows PowerShell and PowerShell 7.
- Live local portable validation after Goal 20 classification repair: 7/7;
  55 allowed portable `Data` events, 12 attributed external warnings, zero
  application-owned or unattributed failures, and zero watcher errors.
- Production model resolution focus: 77 inference tests passed with 1 expected
  opt-in skip; 95 integration tests passed with 1 expected private-graph skip.
- Production runtime availability: 12/12 focused composition cases passed;
  exact OpenCV bytes plus provenance, notice, clean-machine, and release flags
  are required before the axis stage becomes approved.
- Public scoreboard: 37/37 synthetic metric-contract gates passed in 219.286 ms with peak managed memory 987216 bytes.
- `packaging/localization/Test-LocalizationAudit.ps1`: 9/9 passed.
- repository localization audit: 181 keys, 0 missing, 0 extra, 0 duplicate,
  and 0 unresolved references.
- `git diff --check`: passed.
- `Build-Windows.ps1 -AuditOnly`: release not ready, 16 substantive clean-tree
  blockers, seven manifests, one redistributable approved model file, thirteen
  blocked mandatory direct-evidence gates, and no emitted artifacts.

## Metrics and timing

- current full .NET Release suite: 814 tests passed with 9 expected hardware or opt-in skips; all 87 App tests passed on the current interactive desktop
- direct exact Real-ESRGAN Chandler invocation: 1054.545 ms, exit code 0,
  1726 by 790 output, output SHA-256
  `954090b7f3b0783123f51b5a523672fdc889ab5e13b1dc935b5818f05b4729d1`,
  and unchanged source SHA-256
  `65bd6e1021bd9a3f2d2564a640ff90060a34e784782f2d847b4b26f9c6c342e1`
- latest development portable build passed App 44/44, Domain 23/23, focused
  integration, publish, reviewed-OpenCV replacement, and process smoke
- watcher debounce: 2.53 to 2.63 seconds across independent verification
- queued watcher behavior: four expected builds
- manual WPF composition: 1 test in 1 second
- OpenCV clean source builds: 161.546 and 171.175 seconds
- OCR V2 candidate and repair training: 221.038 and 199.791 seconds
- PP-OCRv5 repeated conversions: detector 4.719 seconds combined and recognizer
  5.754 seconds combined in the final conversion-only report; CPU parity maxima
  were `1.349323213162279e-07` and `2.205371856689453e-06`

## License review

No dependency or model approval was inferred from repository-level licensing.
The OpenCvSharp managed package remains reviewed separately from its blocked
native runtime. No GPL, AGPL, SSPL, BUSL, or non-commercial dependency was
added. Only the exact user-authorized official Real-ESRGAN assets and pinned
OpenCV source revisions were imported into ignored local audit storage after
recording source, purpose, checksum, license, privacy, and Git eligibility. No
private graph was imported during Goal 19. Goal 20 later imported the explicitly
user-supplied Chandler image into ignored local evaluation storage only, with
no training, Git, packaging, or redistribution eligibility.

The public OpenCV notice bundle covers Apache-2.0, zlib, SoftFloat, and FDLIBM
obligations for the retained minimal source profile. The ignored scoped
maintainer attestation validates the five Microsoft static-runtime entries and
is not distributed. This approves the exact source-built component's public
provenance record, not clean-machine or application release readiness. The
minimal NCNN runtime excludes
`vcomp140d.dll`; the retained unmodified `vcomp140.dll` has an authorized
Visual Studio VC Redist source record and the ignored scoped maintainer
attestation. This closes provenance only. Scientific, offline CPU,
clean-machine, production, and release approval remain false.

## Readiness changes

- Manual-first development preview: implemented and running.
- Stage-aware production composition: implemented, automatic stages fail closed.
- Production OCR adapter boundary: implemented and directly tested with exact
  fixture archive bytes, per-case tensor identities, execution provenance,
  independently composed marker-stage evidence, and a checksum-bound detector
  derivative that masks verified axes while retaining original recognition
  crops. The factory remains null because both approved payloads are absent.
- Official PP-OCRv5 boundary: PASS for provenance, byte reproducibility, ONNX
  checking, CPU parity, and reviewed Apache-2.0 notice files; FAIL for the
  once-only fixed accuracy, role, detection, and exclusion gates. Manifests,
  model-store installation, package discovery, and production approval remain
  blocked.
- Production stage availability: asynchronously derived from checksum, manifest,
  notice, benchmark, license, redistribution, and CPU-provider validation for
  every indexed model; invalid or partial stores remain unavailable.
- Multi-file param/bin packaging limitation: resolved and verified.
- OpenCV source reproducibility, linked inventory, and public notice
  classification: resolved for the exact source-built binary.
- OpenCV local source-runtime parity: PASS.
- OpenCV internal development-portable replacement: PASS with exact
  checksum-bound provenance metadata.
- OpenCV production common-publish replacement: implemented fail closed. The
  audit requires the exact retained evidence root, the installer requires a
  checksum-bound passing clean-machine gate, and production stage availability
  rehashes the installed DLL. Clean-machine and workflow evidence remain blocked.
- PDFium dependency/license closure: resolved for the exact unbundled runner;
  runtime packaging, clean-machine execution, and release approval remain
  blocked.
- Local portable path, diagnostic, endpoint, registry-key, and write tracing:
  recorded with one fail-closed isolation gate.
- Current release audit: 16 substantive clean-tree blockers. Thirteen explicit
  workflow, Chandler, PDF, accessibility, native clean-machine, enhancement,
  Windows VM, and artifact-pair gates require direct checksum-bound evidence.
  The other three blockers are the missing approved OCR detection, OCR
  recognition, and marker-center defaults; marker classification is approved.
  Rejected research manifests remain audited without becoming mandatory release
  payloads.
- Version promotion: intentionally unchanged at `0.0.21`.
- Public release decision: unchanged, `FAIL`.

## Known limitations

- The user-authorized private Chandler graph now has complete manual-only Goal
  20 evidence. It does not provide automatic detector accuracy evidence.
- The production model set is incomplete. Only the marker classifier is
  approved and installed in ignored checksum-bound storage. Bounded
  Real-ESRGAN and marker-center evidence remains experimental and ignored. All
  bounded numeric OCR candidates failed and their authorized budget is
  exhausted.
- OpenCvSharp source inventory, public distribution notice, scoped ignored
  Microsoft attestation, reproducibility, exact public-axis parity, integration
  tests, and local WPF smoke pass. The public audit accepts only exact binary
  `87c12460daba638b36e916ea2bb832d0759fbf094b8639919a7ce11b0cca5791`;
  release promotion cannot occur until clean-machine and workflow checks pass.
- The PDFium binary and dependency notice closure pass reproducibility and
  independent review, but the runner is not bundled or release approved and
  has no clean-machine execution evidence.
- A purpose-aware local isolated-profile simulation passed, but it is not a
  clean profile or VM. Network remained enabled and polling can miss brief
  connections. FileSystemWatcher cannot directly identify the writer process.
- No installer/portable release parity, repair, upgrade, or uninstall evidence
  exists.

## Artifact paths

- `artifacts/dev-portable/latest.json`
- `artifacts/dev-portable/builds/0.0.21-20260804T193412554Z-ad1bb62b/`
- `artifacts/dev-portable/Data/`
- `artifacts/dev-portable/evidence/manual-preview-empty.png`, historical
  nontransparent output from the clean portable build. The disposable capture
  is retired after the 2026-11-13 hardware outcome; regenerate with the current
  screenshot smoke test instead of treating this path as retained evidence.
- `artifacts/evidence/goal19-track-a/manual-preview-track-a.trx`
- `artifacts/evidence/goal19-wpf-root/manual-preview-wpf-composition-root.trx`
- `.omo/evidence/goal-19-track-a-code-review.md`
- `artifacts/goal19/localization-report.json`
- `artifacts/goal19-realesrgan/adapter-benchmark-minimal/evidence.json`
- `models/manifest/markers/MARKER_CLASSIFIER_CANDIDATE_AUDIT.md`
- `models/manifest/ocr/GRAPH_NUMERIC_CTC_EXPERIMENT_AUDIT.md`
- `models/manifest/ocr/GRAPH_NUMERIC_SEQUENCE_V2_EXPERIMENT_AUDIT.md`
- `ml/ocr/official_bakeoff/runs/conversion/report.json`, ignored conversion-only
  evidence SHA-256 `8c6c8aa522663c0594894f0b526c9fcc0055139217d8d1b6bac968ed1c4d9d15`
- `ml/ocr/official_bakeoff/runs/production-gate/evaluation/report.json`, ignored
  failed fixed-gate evidence SHA-256
  `3fceb688d44a2f4c322e40bdfa3dd09ca74ec6d5742ab257ae102ccf76e1c44b`
- `artifacts/goal19-opencv-source/evidence-repro-pass2-final-a/`
- `artifacts/goal19-opencv-source/evidence-repro-pass2-final-b/`
- `artifacts/goal19-opencv-source/runtime-parity/run-20260804T195149029Z-487978feedc04c248ffab7b315191fc2/runtime-parity-summary.json`
- `artifacts/pdfium-source/evidence/`
- `artifacts/pdfium-source/evidence-second/`
- `artifacts/portable-validation/20260803T224522086Z-940006c1/portable-clean-profile-report.json`

## Integration notes

Goal 20 recovered this work and satisfied the mandatory manual real-graph gate.
Subsequent production checkpoints are integrated directly on `main` in the
single primary workspace. The user's unrelated dirty files remain untouched.
No tag, installer, portable release ZIP, GitHub release, or `1.0.0` promotion
was created.

## Acceptance status

- Portable preview: **PASS** for the internal manual development checkpoint.
  The user-authorized Chandler workflow and local purpose-aware isolation now
  have direct Goal 20 evidence.
- Production workflow: **FAIL**. Manual composition works, but all six automatic
  stages are unavailable.
- 1.0 readiness: **FAIL**. Mandatory model, provenance, private validation,
  packaging, and clean-machine gates remain open.
