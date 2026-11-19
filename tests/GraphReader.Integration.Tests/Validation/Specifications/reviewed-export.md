# Reviewed synthetic export

The 320 × 196 PNG and expected CSV describe one response series across three phases.
The CSV was specified independently of the exporter. The test imports the PNG and
seeds an uncalibrated project with four unreviewed centers in original pixels.
No detector or model supplies these candidates. All later edits, project save and
reopen, and file writes use the manual workspace.

Run the case from the repository root on the native Windows development setup.

```powershell
dotnet test tests/GraphReader.Integration.Tests -c Release --filter FullyQualifiedName~ReviewedSyntheticExportTests --logger "console;verbosity=detailed"
```

## Reproduce the review

1. Use the labeled ticks at (40, 152), (40, 24), and (296, 152) for
   (session 10, Y 0), (10, 64), and (18, 0), respectively.
2. Add the Training divider at X 120 and initially place Follow-up at X 240.
   The draft has the same number of rows as the reference. It includes session
   14, misses session 15, and puts session 16 in Training.
3. Delete (168, 72), add (200, 56), and move the second divider to X 232.
   The point centered on that divider belongs to Follow-up. Explicitly accept
   the existing points at (72, 120) and (136, 88). Leave (232, 104) unreviewed.
4. Save, reopen, and export. Compare the scientific CSV with
   `reviewed-export.expected.csv`. Check both divider positions, all three saved
   anchors, and the per-point provenance in the written audit JSON. Every X
   value is an estimate under the confirmed session-10 origin.

The fixture's strict count requires a confirmed calibration, a separate point
acceptance event for the current point, and an explicit label for its phase.
The normal CSV contains all four rows. The test reports these dispositions.

| Session | Y | Phase | Strict review disposition |
| --- | --- | --- | --- |
| 11 | 16 | a | Omit. The point was accepted, and the default Baseline label remains unconfirmed. |
| 13 | 32 | b | Include. The point was accepted and Training was explicitly labeled. |
| 15 | 48 | b | Omit. Adding the missing point records manual placement only. |
| 16 | 24 | m | Omit. The point remains unreviewed. |

## Later operator-time comparison

Use the same native build, export task, and review criteria for manual entry and
assistance. Give operators practice on other graphs. Balance graph difficulty
and counterbalance workflow order across operators so each graph is timed under
both workflows across operators and each operator sees a given graph once.
Keep the reference rows hidden during the task.

Start the timer when the operator begins import. Stop after the final export
and its review-or-omission record are saved. Record elapsed and hands-on time
separately, including calibration, proposal inspection, corrections, and export
retries. Retain incomplete runs with their elapsed time and reason. Have an
independent reviewer compare each export to the reference for missing, extra,
misvalued, and misphased rows and check the omissions. Report completion and
strict coverage alongside the paired time differences by graph. Automated test
duration measures software execution only.
