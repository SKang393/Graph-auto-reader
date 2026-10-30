<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Repository, run-output, and build retention

Effective maintainer instruction: **2026-10-29**.

**Keep the current product and its required inputs on disk. Keep history in
Git and concise documentation. Do not accumulate completed run outputs or
superseded builds. Cleanup is part of completing the work.**

This policy applies to `E:\Graph-auto-reader` and all its worktrees, scratch
directories, training runs, evaluations, previews, and release staging. It
supersedes older instructions to preserve every failed attempt, every cited
artifact, legacy folders, or separate builds for different purposes. It does
not waive accuracy, privacy, license, or release-approval requirements.

## 1. Finish and push coherent changes

- Complete a meaningful, reviewable batch; run the relevant checks; commit its
  source and concise outcome documentation; integrate and push to `origin/main`.
  Verify that the intended commit reached the remote before calling it done.
- Do not leave completed work only in a local branch, worktree, or folder. Do not
  include another task's dirty changes in the commit.
- Small source fixes may be committed and pushed without a packaged build.
  A push is not a reason to create another executable package.
- Remove obsolete tracked files from the current tree after searching references
  and updating imports, tests, packaging, scripts, and documentation. Commit and
  push the removals. A file being pushed does **not** make current source obsolete.
- Preserve historical source through its pushed Git commit. Do not keep `old`,
  `legacy`, `backup`, copied-repository, or dated source folders for reference.
  Retire inactive worktrees after their work is integrated and pushed.
- Ordinary deletion does not remove bytes from Git history. Do not rewrite
  history or force-push as routine cleanup.

## 2. Record results, then delete completed outputs

Training, testing, validation, benchmarking, and review use temporary outputs.
Before deleting them, update the relevant tracked outcome document or ledger
with a compact record of:

- source commit, command, configuration/seed, and input/model identities;
- the measured result, denominators, duration, and pass/fail/void decision;
- material errors, limitations, and the selected change or model, if any;
- required aggregate provenance, acceptance status, and sealed-use accounting;
- which generated outputs are being retired and how they can be regenerated.

Commit and push that record and any resulting source changes. Then remove the
completed run's raw logs, tensor/feature caches, generated datasets, prediction
dumps, screenshots, temporary reports, rejected models, intermediate checkpoints,
and duplicate weights. A completed failed or cancelled attempt follows the same
rule after recording its cause. Do not preserve a directory merely because it
once failed or a document mentions its path or hash.

**A run is not closed while an active downstream evaluation, investigation, or
build still needs its outputs.** Keep only those specific current inputs until
their consumer finishes, then retire them. Each temporary retention must name
the consumer and removal condition; "may need later" is not a condition.

Documentation records what happened; it does not replace a model or file that
the current product actually loads. Retain the selected runtime model and its
manifest, license, and checksums. Keep a resume checkpoint only for an actively
resumable training task, not as a collection of historical checkpoints.

Update consumers before removing required files. A closed run's historical
documentation must identify retired artifacts as retired, not promise that they
remain available locally. Current validation must not depend on deleted scratch
paths. Keep compact machine-readable records only where a current gate needs
them; consolidate them instead of retaining whole run directories.

## 3. What belongs in the current tree

| Keep while currently required | Remove after the push/consumer checks above |
| --- | --- |
| Active application source, tests, contracts, build scripts, configuration, and documentation | Superseded implementations, unused experiments, legacy folders, and copied source trees |
| Current runtime assets/models, manifests, licenses, and redistributable dependencies | Duplicate or rejected model payloads and obsolete checkpoints |
| Reusable fixtures and data required by the current build or validation | Generated copies and completed-run datasets, caches, masks, tensors, and predictions |
| Concise outcome and build ledgers with honest limitations | Raw completed-run result files, diagnostic screenshots, logs, and temporary reports |
| One current packaged build identity | Every superseded development, review, test, preview, and release build copy |

Required tests, licenses, build tools, and current training/validation inputs count
as current dependencies even when they are not shipped inside the application.
Original user data, private research material, user projects, and unrelated work
are not disposable generated outputs. This policy does not authorize deleting
them.

## 4. Exactly one current packaged build, across all purposes

- Retain **one latest verified packaged build identity in total**, not one per
  purpose, branch, worktree, or distribution label. Development, review, preview,
  release-candidate, and release builds share this limit.
- The installer and portable ZIP made from the same publish output are one
  build set with one version and source commit, not separate historical builds.
  Remove redundant extracted/staging copies once verification is complete.
- Keep the existing latest-build pointer and `docs/BUILD_LEDGER.json` consistent.
  Exactly one completed build entry is locally retained; older entries remain
  historical records with `retained: false`.
- Replacement may temporarily hold the current build and **one staging
  candidate**. Verify and upload the replacement, push its ledger, switch the
  latest pointer, then remove the previous build and staging leftovers. On
  failure, keep the current build, record the failure, and remove the failed
  staging output. Do not start a third build while replacement is unresolved.
- User `Data`, projects, and settings are not build output and must survive
  replacement. Keep them outside disposable packaging directories.

## 5. Build only at meaningful checkpoints

Package after an integrated feature, substantial fix/refinement batch, measured
model improvement, or necessary repair to the usable current build is ready to
review. State the change that justifies the package in its ledger entry.

Do not package after each file edit, tiny fix, documentation change, or diagnostic
step. Do not run a package-producing watcher for every save. Routine compilation
and relevant tests still run when needed; their `bin`, `obj`, test outputs, and
scratch directories are temporary and are cleaned when the batch closes.

Every actual packaged build still consumes one version under the existing
version policy. This rule reduces unnecessary builds; it does not reuse or
falsify build numbers.

## 6. Push actual packages and their tracking record

"Push the build" requires both:

1. The source checkpoint and build-ledger record on `origin/main`.
2. The actual verified package files in the repository's artifact storage, with
   their upload confirmed and linked from the ledger.

Record version, source commit, UTC time, purpose, package names/sizes/SHA-256,
verification status, and remote artifact identity/location. A source commit or
ledger alone is not a binary upload.

Use repository-associated CI artifact storage for internal builds and GitHub
Release assets for an authorized release. Do not add repeated executable/model
payloads to ordinary Git history: deleting their working-tree copies would not
stop repository history growing. Keep the current internal artifact available
until replacement, with bounded retention for superseded temporary CI artifacts.
Historical tracking lives in the ledger and source commits, not local binaries.

An internal artifact upload does not create a public release or permit a new
release tag. Existing release gates and publication holds still apply. If an
upload or push fails, preserve the one current usable build, report the specific
publication blocker, and do not generate repeated replacement packages.

## 7. Cleanup is an ordinary completion step

The maintainer authorizes routine removal of obsolete generated outputs and
superseded tracked files that meet this policy. Do not request approval for each
cache, screenshot, or completed run. Ask only when ownership or continued use is
unclear, or the proposed deletion concerns original user material.

Before removal, check references and active consumers, confirm the summary/source
commit is pushed, and resolve every deletion target under its intended project
directory. Do not follow junctions or delete another active task's files. Use
exact paths and one shell; do not issue broad drive-wide cleanup commands.

Use checksums when proving a retained duplicate or protecting a required input.
Do not create another permanent archive or large per-file evidence collection
just to delete regenerable caches. Keep one compact cleanup entry with scope,
file count/bytes reclaimed, preserved inputs, and the pushed record reference.

Before disk-heavy work, check free space and budget peak output, including
temporary copies and caches. Clean closed work first. If there is insufficient
space, reduce/bound the output or report the exact requirement before starting.
Do not fill the drive and then begin a new recovery run that creates more copies.

A batch is complete when its changes and outcome are pushed, obsolete tracked
files are removed and that removal is pushed, disposable outputs are cleaned,
and the latest-build pointer/ledger are consistent. Report any remaining active
input or publication blocker explicitly. A policy edit alone does not claim that
existing scripts enforce it or that historical cleanup has already happened.
