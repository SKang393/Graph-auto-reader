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

Clean-machine install, launch, repair, upgrade, uninstall, and user-data
preservation remain mandatory evidence before publication. The tracked
definition remains `implementation-complete-runtime-unverified` until that
evidence exists.

## Installer transaction component check

At source commit `f13382ce`, the existing
`packaging/tests/installer-transaction` program was published as a
self-contained Windows executable and run over SSH in a new, task-owned
directory on the separate laptop. Its executable SHA-256 was
`313166c12afdd89f688703d975295d42d5233227a35464557da30de10ae3f0df`;
the copied host report SHA-256 was
`464f0622f381b12a5f3790b8a8c2ad3e3963455222f48c37b6c52d65076d2eb0`.
The executable exited 0 and reported passing checks for file and metadata
rollback, upgrade and uninstall preservation, rejection of a reparse point,
and invalidation of a deferred operation after replacement. The fixture used
real local filesystem operations and an in-memory registry substitute.

This is a component test on the laptop's existing Windows installation. It
did not run the actual setup executable, make Windows registration changes,
test application launch, or establish clean-machine release evidence.
