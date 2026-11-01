<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Windows installer definition

`installer.json` records the enforceable installer behavior. The Apache-2.0
`GraphReader.Installer` bootstrapper is per-user, does not require elevation,
and embeds an archive of the common publish stage without rebuilding the
application binaries.

The installer writes binaries beneath
`%LOCALAPPDATA%\Programs\GraphAutoReader`, creates a Start Menu shortcut and a
normal current-user uninstall entry, and leaves mutable data beneath
`%LOCALAPPDATA%\GraphAutoReader` intact during uninstall. Same-version install
repairs files, newer versions upgrade, and downgrade is blocked unless the
operator explicitly passes `--allow-downgrade`.

Repair and upgrade stage a complete replacement beside the installation before
switching directories. If the switch or setup configuration fails, setup tries
to restore the prior directory, Start Menu shortcut and uninstall registry
values. Files absent from the new payload are copied from the old installation
without overwriting new payload files; setup rejects reparse points. For an
installation with a valid `.graphreader-owned-files.json` inventory, unchanged
setup-owned files absent from the new payload are left in the retained backup
instead of being copied into the active installation. Modified setup-owned
files and files not listed in the inventory remain copied to the active
installation. An absent or invalid inventory uses legacy preservation behavior.
Prior installations are retained as sibling
`GraphAutoReader.backup-*` directories because they may contain files setup did
not create. Setup reports the retained path in its completion message and on
standard output when available, and attempts to append it to
`%LOCALAPPDATA%\GraphAutoReader\installer-retained.log`. An interrupted or failed operation may also leave a sibling
`GraphAutoReader.staging-*` or `GraphAutoReader.failed-*` directory for review.
Uninstall unregisters the app and preserves the entire installation as a
`GraphAutoReader.uninstalled-*` sibling rather than deleting potentially
unowned files. The move completes before registration is removed; a failed move
leaves the old installation and registration in place. Setup reports the
intended path; it does not claim disk-space cleanup. When uninstall runs from
the installation itself, a copied helper in the user-data directory waits for
the installer to exit and performs the move and unregistration. Setup operations
use a shared mutex and unique installation identity; the helper refuses to act
if a later setup replaced the scheduled installation. Deferred uninstall returns
exit code 10 for pending, including in quiet mode. The machine-readable
`%LOCALAPPDATA%\GraphAutoReader\uninstall-latest.json` points to a status record
whose `state` is `pending`, `succeeded`, or `failed`. Non-quiet failures also
display an error. Pending is not a completed uninstall. A copied helper may
remain in the user-data directory for review.
If uninstall completes but its status record cannot be updated, setup records
that distinction in `installer-uninstall.log` when possible; the status record
may remain pending and should not be interpreted as a failed uninstall.
If the deferred move fails, setup attempts to record the original and intended
paths in `%LOCALAPPDATA%\GraphAutoReader\installer-uninstall.log`. New installs
include `.graphreader-owned-files.json`, which records the staged payload paths
and SHA-256 hashes before retained files are copied in. Legacy installs lack
that ownership record, so no automatic deletion is based on it.

If Windows prevents removal of the setup-owned temporary extraction directory,
setup reports the cleanup error on standard error when available. This does
not change a successful install into a reported failure or replace the
original installation error.
If the setup failure log cannot be written, interactive setup shows the original
error and states that the log is unavailable. Quiet setup still exits with an
error code.

Clean-machine install, launch, repair, upgrade, uninstall, and user-data
preservation remain mandatory evidence before publication. The tracked
definition remains `implementation-complete-runtime-unverified` until that
evidence exists.

## Installer transaction component check

At source commit `e1c023d6`, the existing
`packaging/tests/installer-transaction` program was published as a
self-contained Windows executable and run over SSH in a new, task-owned
directory on the separate laptop. Its executable SHA-256 was
`64222957a6fc821546dd19746a82dca603476ee42dcfce10023d9a827af3e8a6`;
the copied host report SHA-256 was
`836f39998f760d6d1025d823cf17b7c434ec1a86172ec6fe1b06ac90198afed3`.
The executable exited 0 and reported passing checks for file and metadata
rollback, upgrade and uninstall preservation, rejection of a reparse point,
invalidation of a deferred operation after replacement, locked temporary
staging cleanup, and unavailable failure-log handling. The fixture used real
local filesystem operations and an in-memory registry substitute.

This is a component test on the laptop's existing Windows installation. It
did not run the actual setup executable, make Windows registration changes,
test application launch, or establish clean-machine release evidence.

## Existing-host manual-preview transaction check

A diagnostic setup was built from installer sources at commit `8a9e4621` and
the internal 0.4.35 ManualPreview application at commit `46bfb59a`. This was a
test fixture, not a release package: its payload was assembled from the preview
with the portable sentinel removed and diagnostic build metadata added. The
setup SHA-256 was `52ee036fab2657b8e839cea9be58d9f258fc8f2077e1ad03762b80c651dfa39f`;
its embedded payload digest verified as
`27207dd48eb538456cddef6bd5089e880e719d4b286dd4a0f56b5007e2d29dc8`.

On the laptop's existing Windows account, the initial install produced the
expected application and uninstaller hashes, per-user registration and Start
Menu shortcut. The first test harness did not capture the install process exit
code, so that exit code remains unknown. A subsequent host test ran the installed
preview's no-window startup smoke, a same-version repair, and deferred quiet
uninstall. Repair preserved test user data and an unowned installation file in
both the active installation and its backup. Uninstall returned pending exit
10, then reported success; the registration and shortcut were removed while
the data and unowned file remained in their retained locations. The result
SHA-256 is `d50784d3782b0f98bed0ac04d1b586d5eb03a29404bcc4bbad0a358e751dd4d4`;
the final host harness SHA-256 is
`f67b828184fa201e4dd647d84d225848a3fc4a2edf1dd850df019492e5aee7d8`.

This existing-host test does not establish a clean-Windows result, an upgrade
or downgrade result, a full interactive workflow, automatic recognition, or
release approval.
