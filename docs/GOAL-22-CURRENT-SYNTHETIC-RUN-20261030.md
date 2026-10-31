<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Current-source synthetic recognition run

The fixed 21-source synthetic run used source commit `7ec80465`, CPU inference,
and historical, unapproved V46 OCR, V27 marker-center, and classifier payloads.
It did not train or use private or sealed inputs. One workflow completed; 20
failed during automatic calibration. Their diagnostics lacked confirmed
session-1 origin and final printed session. This is a bounded development
result, not production or release acceptance.

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

- Runtime binding SHA-256: `2a2abb3313f5dfcbb724552cd7c16dbac03b4fb04a65bef2b196c524c9dabc10`
- Run report SHA-256: `a1f085a017569e93569bab9cf96c156e4aa47008005e7861bd4f12b2d58199c4`
- Offline OCR audit SHA-256: `79fa6ee12a34684b3644cea2b390f261aee4b2a0f90adf192d234a312052a5c8`
- Source-global diagnostic SHA-256: `174fa2857f5ddec3d0ed86ac1c03f4c5ed1da53c90e93024e0b33532f72fcbad`
- Evidence folder: `artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/current-main-7ec80465-synthetic-20261030`
