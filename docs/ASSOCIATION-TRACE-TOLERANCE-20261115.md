<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Report the actual directional association bound

Saved [endpoint trace diagnosis](ENDPOINT-LABEL-LANE-20261115.md) exposed an
evidence-only discrepancy: `TraceRow` still reported half the smaller neighboring
gap, capped by glyph size. Production association uses the neighbor toward the
glyph's offset. The old field can therefore show distance greater than tolerance
while the production paired flag is correctly true. No runtime association
behavior is changed by this correction.

Historical profile outputs, pins and the legacy `association_tolerance` field
are preserved. Only the current endpoint diagnostic advances to
`graphreader.real-dev-endpoint-review-trace.v2`, identifies policy
`legacy_symmetric_plus_directional_toward_label_offset`, and adds
`directional_association_tolerance`. Success and sanitized failure schemas agree.
The completed private v1 invocation remains historical evidence; it was not rerun
to replace its output. This diagnostic schema is not a public project/model/
vision contract.

The owned self-test invokes the actual `CalibrationTickProposal.CreateReviewDraft`
pairing path with supplied geometry and strings. For ticks `[10,20,60,100]` and
a twelve-by-ten-pixel label:

| Label center | Legacy bound | Directional bound | Actual paired |
| ---: | ---: | ---: | --- |
| 27 | 5 | 10 | Yes |
| 40, exact tie | null | null | No |
| 110, no outward neighbor | 10 | 10 | Yes |

It also verifies that historical rows do not gain the new field. Existing
diagnostic tests previously exercised only a tie, bypassing the unique-nearest
tolerance branch. These are owned supplied-geometry controls, not image/model
accuracy evidence. Sealed use and private reads are zero.

The probe-only build passed with zero warnings/errors in 11.35 seconds:

```powershell
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:BuildProjectReferences=false
tools/GraphReader.CalibrationOcrProbe/bin/Debug/net10.0-windows/win-x64/GraphReader.CalibrationOcrProbe.exe --real-dev-endpoint-trace-self-test
```

The self-test passed, exit 0, in 0.892 seconds. All twenty endpoint runtime DLL
pins were restaged and verified without rebuilding or changing the application.

| Identity | SHA-256 |
| --- | --- |
| Probe DLL | `392b5bcdb2f0b67c18a5d418fc6e9d5cdd1a03833405a40a9f517f9a6e76fdaa` |
| Probe EXE | `cd3af08b05c4c9ac57fce8c0fef1775dc0c5c88ba66e7191ef3bfd8e0b2d63b9` |
| Endpoint v2 profile source | `304b709f268a60079c3e2fcce615f09b8e41ecf07b3f98f21f4473735ae13d33` |

No dependency, model, license, package or production approval changed. The code
remains Apache-2.0. No new private output was generated. This diagnosis and
correction reached origin/main in `a34115441066bcb7613e13a0c11b19ac7a0267cf`.
The four completed files totaling 58,459 bytes in
`artifacts/endpoint-full-development-check` were retired at
`2026-11-15T15:38:50.9443062Z` after their sole consumer finished. The first
sandbox consumer query was denied before deletion; the normal-host retry passed
path, inventory, reparse and live-process guards. Originals, independent data
and current runtime assets were preserved.
