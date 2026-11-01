<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Connected-ink bridge with wider symbols

The original-pixel bridge search now scales to eight percent of the smaller
image dimension instead of six percent. It retains the 80-pixel cap, endpoint
tolerance, collinearity and observed connected-ink requirements, and bounded
detour. Hough settings are unchanged. A filled-square regression failed with
the previous limit and passed with the new limit; the equally sized empty gap
remained disconnected. The axis test suite passed 115 tests.

On 20 pinned, unmodified train images, two horizontal right endpoints moved
from approximately x=962 to x=1,598 and from x=254 to x=1,500 respectively.
The added lengths follow visible axis ink; both results still require review.
The other 18 images changed by less than three pixels at their axis endpoints.
On the 16-image development capture, 15 images changed by at most about four
pixels at an endpoint; one horizontal endpoint grew by about 63 pixels, and
the added extent followed visible axis ink on review. These checks are not a
complete plot or false-positive assessment.

On the same 20 unmodified images, the existing unapproved center candidate
matched 223/332 recorded points before the change and 242/332 after it. The
candidate count increased from 1,707 to 1,913, including 197 additional
candidates on the two images with enlarged extents. Recorded points are
incomplete and imperfect, so the unmatched candidates have unknown correctness.
The score does not exercise later classification or recovery. Some synthetic
overlay patches erased axis ink, so their failed plot geometry is not assumed
to represent the unmodified originals. One original graph still chooses an
incorrect slanted axis and remains flagged for review.

| Evidence | SHA-256 |
| --- | --- |
| Original axis report before | `7451192d37afe6a7144f26550c15fb9ab07b4eff7b682bfb75514634f332fc11` |
| Original axis report after | `63a228126a92b85563de5883f855a7631ee41a3ef46417a4682ee417e3ca569d` |
| Development axis report before | `0e67a8f67a24785d8c365b40d75cdaf163bee807ca495ddce95a5938206e023b` |
| Development axis report after | `425bafdba7411dd1436eb32de06a07a3ab329899667d0c4039333de413c9692e` |

The full solution check, `dotnet test GraphAutoReader.slnx -c Release
--no-restore -m:1 -p:UseSharedCompilation=false`, passed 2,375 tests, skipped
25, and failed none. The private scoring tools passed 18 Python tests and
pyflakes. CPU inference was used for diagnostics; no optimizer steps, sealed
payload reads, or model approvals occurred. Private images, predictions, and
reports remain project-local while the recognition investigation consumes them.
