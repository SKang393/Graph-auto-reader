# Sparse printed sessions

The newly drawn synthetic figure has one series, six observations and three phases.
Its X ticks read 1, 4, 9 and 16. The Y ticks read 0, 32 and 64. Dividers at
original-pixel X 128 and 228 begin Training and Follow-up. The reference in
`SparseSessionExportTests.cs` lists each center, printed session, Y value,
phase and review disposition independently of the program output.

The test gives the OpenCV exemplar worker one 13 × 13 patch centered on the
first disk at (68, 140). It scores the other five against their centers with a
one-pixel Euclidean tolerance and counts extra proposals. It then stores the
seed and returned centers as an unreviewed synthetic draft. The test supplies
tick locations and numeric label polygons to the calibration fitter. The axis
detector, OCR inference and out-of-process worker are separate native checks.

The ordinary session-1 export uses observation order. Select **Use printed X
values** and review the fit again to retain the gaps. After reopening the
project, compare the scientific CSV and JSON audit with the reference. Under
the reviewed-export protocol, the strict rows are (7, 20, b), (9, 42, b) and
(13, 34, m). Sessions 2 and 4 lack a confirmed baseline label. Session 15
lacks point acceptance. The scientific CSV retains all six.

## Native Windows checks

Run both commands from the repository root with the project development prerequisites.

```powershell
dotnet test tests/GraphReader.Integration.Tests -c Release --filter "FullyQualifiedName~SparseSessionExportTests" --logger "console;verbosity=detailed"
dotnet test tests/GraphReader.App.Tests -c Release --filter "FullyQualifiedName~PositiveIntegerSessionReviewAlsoOffersLiteralCoordinates|FullyQualifiedName~PrintedXDraftRequiresExplicitChoiceAndReviewThenSupportsUndoRedo|FullyQualifiedName~PrintedXReview" --logger "console;verbosity=detailed"
```

In the native UI, import the PNG, inspect the measured ticks and raw OCR labels,
and check the three suggested anchors against (52, 164), (52, 36) and (292, 164).
Choose printed X and confirm that Apply requires a fresh review. Place the seed
and inspect the five proposed centers. Check save, reopen and export against
the reference. Record the audit actions produced by live marker placement,
which can differ from accepting an existing unreviewed draft point.
The operator-time comparison protocol is in `reviewed-export.md`.
