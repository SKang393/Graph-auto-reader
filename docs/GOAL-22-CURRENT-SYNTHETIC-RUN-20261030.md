<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Current-source synthetic recognition run

The fixed 21-source synthetic run used source commit `7ec80465`, CPU inference,
and historical, unapproved V46 OCR, V27 marker-center, and classifier payloads.
It did not train or use private or sealed inputs. One workflow completed; 20
failed during automatic calibration. Their diagnostics lacked confirmed
session-1 origin and final printed session. This is a bounded development
result, not production or release acceptance.

A source-level annotation audit retained all 21 sources and 31 annotation
panels. It reports no numeric X-tick labels for the 20 failed sources and two
for the one completed source. Workflow status and annotation observability are
recorded independently; generator anchors remain truth-only metadata. The audit
does not exhaustively inspect raster pixels for undisclosed text or map runtime
panels to annotation panels, so it cannot prove that failed images contain no
usable X evidence.

The exploratory offline scorer counts whole-source annotations for every
scored panel. Multi-panel sources can therefore repeat truth annotations, and
panel coordinates were compared with source coordinates without applying the
recorded transform. Its counts must not be treated as OCR accuracy, precision,
or recall. The corrected scorer skipped all 31 panels because explicit
compatible single-panel geometry was unavailable. Workflow completion counts
equal the prior historical replay, although CPU thread count
and source revision differ; this alone does not establish the failure cause.

A separate existing source-global scorer mapped panel-local predictions using
the recorded crop translations and counted each source truth once. On 21
synthetic sources it scored 453 visible text truths, including all 20 sources
whose primary workflows failed after OCR. Raw detection precision/recall were
74.78%/92.27%; assembled detection precision/recall were 90.49%/98.68%.
Both raw detection bars and the assembled precision bar remain below 95%.
The inverse transforms were checked independently, but the scorer relies on
recorded panel-image hashes rather than recomputing crop bytes. It also requires
an OCR panel for every source, so it would stop instead of scoring a source
that failed before OCR. This run remains diagnostic-only.

An aggregate diagnostic classified the 47 unmatched assembled detections by
their final roles: 26 annotation, 15 other, five legend text, and one phase
heading. Forty-three retained the same source/panel/region identifier as a raw
detection; four did not. Identifier equality is not proof of processing
lineage, and these roles can also contain legitimate graph text, so this result
does not justify suppressing them.

The fixed-output geometry diagnostic compared those 47 unmatched detections
with 447 geometry-matched controls. Thirty-six unmatched detections had zero
IoU with the nearest annotated truth, and only one had at least 0.5 IoU with
another same-source detection. Controls are matched on geometry only, not text
or role. Confidence and shape distributions overlap the controls; this does
not establish a safe confidence, shape, or duplicate-suppression rule.

- Runtime binding SHA-256: `2a2abb3313f5dfcbb724552cd7c16dbac03b4fb04a65bef2b196c524c9dabc10`
- Run report SHA-256: `a1f085a017569e93569bab9cf96c156e4aa47008005e7861bd4f12b2d58199c4`
- Offline OCR audit SHA-256: `79fa6ee12a34684b3644cea2b390f261aee4b2a0f90adf192d234a312052a5c8`
- Source-global diagnostic SHA-256: `174fa2857f5ddec3d0ed86ac1c03f4c5ed1da53c90e93024e0b33532f72fcbad`
- False-positive attribution SHA-256: `d6a2999e4fbd31a20bde52c392d0696e499302e29301498d9447a2172c1c146f`
- Bound geometry diagnostic SHA-256: `ac97213828625081a99ab9ea9a6541fda3f5fe6a8bd1798c355430420ae38b79`
- Source-level annotation audit SHA-256: `a4e6c3cd6dcca5b8980de00eb562442cb1be60f64f6b69cd0c47609da545e38c`
- Evidence folder: `artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/current-main-7ec80465-synthetic-20261030`
