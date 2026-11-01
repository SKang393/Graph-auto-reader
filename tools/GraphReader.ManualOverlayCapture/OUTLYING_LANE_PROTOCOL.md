<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Outlying Y-tick lane safeguard check

The application requests review for an unreviewed numeric Y-tick reading whose
distance to the selected axis exceeds the within-panel median by more than 1.25
of its box heights, when at least three valid numeric Y readings are available.
The OCR region is retained and user-confirmed readings bypass this warning.
This review signal was developed after inspecting the same training and
development images; it does not establish automatic accuracy or acceptance.

Run the same 20 pinned original training images with the unapproved candidate
workflow and compare all 21 panels with the previous detailed capture. Check
the three frozen visually reviewed text fragments for the new warning, preserve
any other warning and failure, and compare marker-stage observations and scored
recorded points. A distant genuine label can also trigger review. No optimizer,
sealed-payload read, model approval, or release gate change is authorized.
