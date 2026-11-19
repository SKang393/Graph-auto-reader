# Point and phase review evidence

The project and extended CSV and JSON audits preserve the scope of reviewer actions. Confirming calibration authorizes the coordinate mapping. Newly detected points start as `unreviewed`. A profile-derived phase label remains unconfirmed until the reviewer explicitly adds or labels that region. A divider move changes its geometry and preserves its label evidence.

The three-column scientific CSV retains its existing format. Read the extended audit alongside it to inspect review coverage.

| Audit field | Interpretation |
| --- | --- |
| `review_status` | Stored point-level status. A corrected point can have a single edited attribute. |
| `point_review_actions` | Operations attributable to this point, separated by a vertical bar. A pixel move identifies the edited image axis when the operation recorded it. Reassignment identifies a series edit. Explicit point acceptance is recorded separately. |
| `phase_source` | Source of the assigned phase label, such as `profile_prior`, `ocr` or `manual`. |
| `phase_label_review` | `explicit_label` requires a matching label operation and saved confirmation. `unconfirmed` preserves an unset confirmation. `recorded_confirmation_only` preserves a historical confirmation whose current label cannot be traced to that operation. |

`none_recorded` describes an unreviewed point with no attributable action. `recorded_status_only` preserves a stored point status without matching action evidence. Older moves can report `point_moved_axes_unspecified`. An export without phase evidence reports `unknown`. The saved project retains the individual history and audit events.

## Native verification

A Windows WPF import, detection, manual calibration, save, reopen and export reproduced the original defect on one nonsealed development graph. Before a point edit, the previous build wrote 15 corrected points and two manually confirmed phases. With the fix, the same operation saved 15 unreviewed points and two unconfirmed profile-derived phases.

After one horizontal move, six series reassignments and one explicit point acceptance, the project held seven corrected, seven unreviewed and one accepted point. The selected export contained 12 rows. The CSV and JSON audits agreed on one horizontal move, six series reassignments, one acceptance and four untouched points. All 12 phase labels were unconfirmed. In a separate copy, labeling the second phase affected its two exported rows. Undo and redo restored the matching phase provenance. Opening and re-saving a project from the previous build preserved every point and phase record, including ambiguous historical flags.

Image-only detection produced zero numeric rows. After assistance, 12 of 16 reference observations matched sessions, eight were within one source pixel of Y, four exceeded that tolerance and four were missing. There were zero extra exported observations. The independent numeric reference does not establish phase or series correctness. No model, numeric threshold or export acceptance rule changed.

Local Release validation passed 1,444 App tests, 194 integration tests and all 37 export tests. The export controls check spreadsheet-text escaping on all three appended CSV fields and the original values in JSON. Two native-worker App cases and seven opt-in integration cases skipped under the default environment. The exact reviewed PDFium case also passed separately with the approved renderer. The integration suite exercised saved/manual projects, interrupted batches, PDF panel source binding, export refusal and the direct workflow audit.
