<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Cached native-axis pixel-property diagnosis

## Problem and scope

The [exact-box tick trace](EXACT-BOX-TICK-EVIDENCE-20261115.md) rejected its one
native proposal with `AXIS_BOX_NOT_OBSERVED`. This follow-up measures whether
the same proposed box meets observed image ink. It does not rerun geometry,
OCR, association, a model comparison, or any independent evaluation.

Source base: `9b2b915f4971bbfebd16c58cbeae146575d7c08c`. The runtime App and Rust
identities remain those in the exact-box report. The only executable change is
an explicitly selected diagnostic mode in `GraphReader.CalibrationOcrProbe`.

## Implementation and validation

`--real-dev-cached-axis-properties` validates the cached report hash, frozen
selection, runtime identities and source digest. It reopens the same first 12
development projects through locked streams. The streaming XML extractor keeps
only embedded image text and image metadata, then invokes the existing PNG
validator. It does not construct calibration or digitized-point objects.

Only cached native tick-scan failures are cropped, using the recorded crop and
the same white-background Gray8 conversion. Measurements are endpoint darkness
and dark-pixel counts/longest white runs at fixed offsets -2 through +2 from
each proposed baseline. There is no snapping, alternative candidate selection,
threshold search, text interpretation or scientific value output. Partial
properties are withheld unless every selected source verifies.

The probe-only build passed with zero warnings/errors in 7.69 seconds after
correcting a CA1859 analyzer error. The owned self-test passed in 0.660 seconds:
cached-report tamper refusal before selector access, image-only extraction,
single crop offset, fixed-offset measurements and partial-output withholding.
All 20 pinned runtime libraries were verified; App and Rust bytes were unchanged.
Independent read-only review found no material issue before the authorized run.

Commands, from the active session worktree:

```text
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:BuildProjectReferences=false
dotnet <probe.dll> --real-dev-cached-axis-properties-self-test
dotnet <probe.dll> --real-dev-cached-axis-properties <cached-report.json> <corpus-root> <App.exe> <reviewed-ocr-assets> <Rust-worker.exe> --explicit-opt-in
```

The actual inputs were the previous `artifacts/exact-box-tick-dev-trace/report.json`,
the frozen corpus at `E:\Graph-auto-reader\data\manual data`, the current source
App/Rust outputs, and the unchanged reviewed OCR asset folder in the sole
0.4.54 package. No asset or image was copied.

## Result and failure accounting

One run completed from `2026-11-15T17:37:31.1892096Z` to
`2026-11-15T17:37:32.9341650Z`, exit 0, wall time **1.7349152 seconds**, empty
stderr. Source work units: **12 attempted, 12 completed, zero failed or
unattempted**. Cached failures: **one source, one panel**. Crop and property
work units each completed **1/1**, with zero failures. The combined source
digest verified before properties were emitted.

All three proposed anchors failed the fixed Gray8 `<128` darkness predicate.
This does not establish that those pixels are white; gray ink also fails that
predicate. The horizontal span contained 507 pixels and the vertical span
222 pixels. In the table, a "white run" is the diagnostic field's name for
consecutive pixels that fail `<128`, not a claim that their values equal 255.
Fixed-offset image properties:

| Offset | Horizontal dark pixels | Horizontal longest white run | Vertical dark pixels | Vertical longest white run |
| --- | ---: | ---: | ---: | ---: |
| -2 | 1 | 266 | 0 | 222 |
| -1 | 2 | 266 | 0 | 222 |
| 0 | 2 | 266 | 0 | 222 |
| +1 | 0 | 507 | 0 | 222 |
| +2 | 0 | 507 | 0 | 222 |

The observed box does not align with pixels below Rust's fixed darkness
threshold, including nearby rows/columns. This explains the current predicate's
tick rejection, without identifying whether geometry or contrast caused it.
Read-only mapping review verified that the affected crop is the full original
image at (0,0), dimensions and inclusive spans match, and both paths normalize
onto white and convert to Gray8 without resizing. No grayscale-buffer hash was
saved, so this is source/identity/mapping verification rather than a fresh
bytewise comparison. Native candidate fitting may also follow gray edges that
Rust rejects; an owned cross-stage contrast control is the next check. No
automatic calibration gain, accuracy or timing improvement is claimed.

The distinct [raw-intensity follow-up](NATIVE-CONTRAST-TICKS-20261115.md)
subsequently resolved the gray-versus-white ambiguity without repeating
inference or selecting a new box. Its outcome supersedes this diagnosis's
contrast uncertainty while preserving the original v1 result above.

Axis calls, OCR calls, truth scoring, image writes and sealed payload reads were
all **zero**. The 51 independent cases remained excluded. The frozen 120-case
development assignment and fixed first-12 selection were unchanged. No model,
threshold, training, Apply/export, unchanged451 or Mirage run was involved.

## Identities and retention

| Item | SHA-256 |
| --- | --- |
| Report, 1,966 bytes | `c5f86ee2e51314ab245f3ec61a627b817345072de3e63ff22a4a711c0680dfdd` |
| Probe DLL | `0c03ce485d19e5da30fe1eae9c3d653aa985bbfa1f58cb74f289c76b4e997595` |
| Diagnostic source at execution | `f8e65f0b3b2eddd1b8724fc389a39e80a2ef313cb8dc43bfc76a07bec1c8707a` |
| Cached exact-box report | `cba00fe43ba990ff538dee9a6c7eac832dcef2eef6987ad102e03265bf8f51ff` |
| Assignment | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` |
| Selected inventory | `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |
| Selected-source digest | `4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1` |

The temporary `artifacts/cached-axis-pixel-properties` report and the previous
exact-box raw report remain only for the coordinate-mapping review. Retire them
after that consumer finishes and this source/outcome record reaches `origin/main`.
Keep originals, active runtime assets and independent evaluation inputs intact.

Project source remains Apache-2.0. No new dependency, model, frozen public
schema, package, version increment or production approval was added. Diagnostic
completion is PASS; automatic geometry/calibration acceptance remains incomplete.

Full Windows CI
[43742745413](https://github.com/SKang393/Graph-auto-reader/actions/runs/43742745413)
passed on `1b18cebad471db16e040a39cf5aab2ae75f89ad4`: 13 .NET suites with
3,021 passed, 30 skipped and zero failed; 70 Rust tests; and applicable policy
and packaging checks, including 68 packaging verification tests. Release jobs
were skipped. This verifies the original v1 diagnostic source integration;
the later contrast correction has its own outcome record.
