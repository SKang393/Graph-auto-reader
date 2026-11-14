<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Calibration with additional unlabelled ticks

The owned regression in `CalibrationTickProposalTests` previously rejected
three consistent labelled positions solely because another measured tick lay
outside them. Anchor creation already used labelled positions. The extra
restriction prevented a valid reviewed calibration without preventing any
actual extrapolation in that code path.

`CalibrationTickProposal.CheckAxis` now validates the observed pairs without
requiring labels at the outermost measured ticks. Both measured proposals and
reviewed drafts keep the same three-pair minimum, unique associations, numeric
literal checks, monotonicity, all-inlier fit, observed Y zero and source checks.
No value is assigned to an unlabelled tick. Explicit review and Apply remain
mandatory. The [feature policy amendment](CALIBRATION-TICK-PROPOSALS-20261113.md)
records this change; no frozen contract, model, dependency or license changed.

## Validation

The focused proposal and view-model batch passed **52/52**, zero skipped, in
837 ms test time. The first run had 52 passes and one failure from an obsolete
partial-axis test row that still expected the removed endpoint restriction.
That row was removed after the new positive regression covered the same case.
All remaining invalid-row cases passed. Independent source review found no
material issue.

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false '-p:OutDir=E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/hardware-final-build/' --filter 'FullyQualifiedName~CalibrationTickProposalTests|FullyQualifiedName~CalibrationTickViewModelTests' --logger 'trx;LogFileName=unlabelled-ticks-final.trx' --results-directory artifacts/unlabelled-tick-check --verbosity minimal
```

The actual owned workflow used the new source App, retained 0.4.48 OCR assets
and the [repaired Rust worker](CONNECTED-INK-AXIS-REPAIR-20261114.md). Its graph
contained 11 measured X ticks at 110/140/190/240/290/340/390/440/490/540/570 and
three Y ticks. OCR supplied only the six printed labels: X 1/2/3 at
140/340/540, Y 10/5/0 at 80/230/380. The additional ticks received no values.
Simulated review/Apply, two marker acceptances, three-row CSV export and
save/reopen all completed. The saved calibration is user-confirmed manual
three-anchor data with zero tick evidence IDs.

Process wall time was 5,262.527 ms; pipeline 4,648.4304 ms, OCR 2,368.3821 ms
and marker processing 250.7564 ms. Exit was zero and stderr empty. The compiled
test output initially contained the NuGet OpenCV binary; the reviewed 0.4.48
OpenCV runtime was restored before inference. The App stayed the new source
build. This is not a packaged-build, native visual or private accuracy result.

| Evidence | SHA-256 |
| --- | --- |
| Owned source image | `a37da5cd22d8ad3eb3b58b22e324213398a80ee6894b6a67b12b36a204f70f27` |
| Source App DLL | `939a76b3f0ce08ea76b63006c97dc2bfe073570adc6772c1cecbfd5385f3fffd` |
| Local Rust worker | `46e6dcf885760e4ce61c956ef93802b53e2dd51ddb0118ac0c84691e3dda94a0` |
| Final test TRX | `90662c796601a0f98118989b549870c924420f9dd812d1711a00c0cb45c3053f` |
| Workflow report | `d89ec3a078b6e331bc0daa2a6d66ae4a9e7d99a3efdfe89992a4adf1a88d953b` |
| Exported CSV | `a5cab504b91b88ae3c3f6ff976e298cc3d75e47139102400dc64c0e335635302` |

Regenerate the owned input from the connected-ink document's grid/outline
recipe, adding outward X ticks at 110/190/240/290/390/440/490/570 from y=380 to
385. Refresh its owned request hash and run the existing
`--marker-workflow-check` against the source build. Generated frames, requests,
TRX and workflow output in `artifacts/unlabelled-tick-check` are disposable
after this source/outcome record is pushed and active consumers finish.
Retained preview 0.4.48 is unchanged; production approval remains incomplete.
