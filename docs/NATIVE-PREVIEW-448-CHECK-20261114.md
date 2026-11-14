<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native preview 0.4.48 check: launch unresolved

Computer Use is available through `@oai/sky` in the Node runtime. The initial
tool-name discovery missed this entry point, so the earlier statement that
Computer Use was unavailable was incorrect. Initialization and window listing
succeeded; no Graph Auto Reader window was present.

The delegated check attempted to launch the already verified 0.4.48 executable
through `sky.launch_app`. The call did not return for approximately 479 seconds.
The lead interrupted the attempt to prevent an indefinite wait. The resulting
aborted-call status does not establish a new physical Escape press by the
maintainer. No permission prompt was clicked or approved by the agent.
The independent process check found no App process and no new `Data` directory
inside the package. The agent issued no further UI calls after interruption.

No saved CPU/GPU choice, calibration action or native save/reopen behavior was
verified by this attempt. Existing headless results remain separate. The user
was asked whether an access prompt is waiting; native verification remains
open under REV-011. No screenshot, project, setting or source change resulted
from this attempt. This is an unresolved launch-tool action, not evidence of
an application workflow failure.

The maintainer subsequently reported that no access prompt was visible. A
second bounded attempt first listed windows successfully in 4.5 seconds,
again finding no Graph Auto Reader or lock-screen window. The launch call
requested a 45-second tool timeout but still returned no launch or refresh
result. The lead interrupted it after 517.3 seconds. That interruption was
initiated by the lead, not evidence of a physical Escape press. No prompt was
clicked, setting changed or screenshot captured. The launch outcome is
unknown, and no further UI input was issued. Continue code and worker checks;
native settings and workflow acceptance remain unresolved.
