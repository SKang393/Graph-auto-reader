# Versioning and GitHub release policy

Graph Auto Reader uses a custom numeric `x.y.z` sequence. Each component ranges
from 0 through 99. This is not Semantic Versioning.

## Build checkpoints

Every packaged application build, including a rebuild, advances the central
version in `Directory.Build.props` exactly once. Routine local-test and CI
compiler outputs do not consume a product build number. Matching installer and
portable packages from one common publish output count as one build and share
one version. This unit was explicitly approved on 2026-09-08.

Build number equals version ordinal. Prepare the next value before
committing the clean source checkpoint that will be built:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File packaging/Prepare-CheckpointVersion.ps1
```

The ordinary build sequence is continuous across component boundaries:

```text
0.0.98 -> 0.0.99 -> 0.1.0 -> 0.1.1
0.99.99 -> 1.0.0 -> 1.0.1
```

Therefore, rollover resets the lower component to `0`, not `1`. A retry before
the prepared version has a build-ledger entry remains idempotent. Once the
ledger records that version, rebuilding otherwise unchanged source requires the
next version.

## Model-integrated product promotion

The finished model-integrated product is exactly `2.0.0`, following the existing
basic/manual product generation. On 2026-09-08 the maintainer authorized this
target in place of the previously proposed 1.0.0 finish. All mandatory readiness,
accuracy, privacy, licensing, and distribution gates remain required. Once they
pass, prepare the authorized promotion with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File packaging/Prepare-CheckpointVersion.ps1 -PromoteStable
```

This command may move the central version directly from a pre-2.0 checkpoint,
for example `0.23.58` or `1.2.3`, to `2.0.0`. It is the only permitted nonsequential
checkpoint transition. It does not create intermediate version numbers, rewrite
Git history, tag a release, or publish artifacts. The maintainer's authorization
is conditional on passing the readiness gates; it does not assert that those
gates already pass. Ordinary internal checkpoint increments remain unchanged.

Build number continues to equal version ordinal: the 2.0.0 record is build
20000 and its successor is build 20001. Promotion adds only one actual record;
the skipped numbers do not represent produced builds. Ledger record count and
highest assigned build number therefore differ after the exceptional jump.

Without `-PromoteStable`, `0.23.58` advances normally to `0.23.59`. After the
stable checkpoint, normal progression resumes with `2.0.1`.

All completed checkpoint commits belong in `main` and `origin/main`. Git commit
history is the track record for internal checkpoints. A GitHub Release is not
created for every commit.

## Development portable previews

`packaging/Build-DevPortable.ps1` and `packaging/Watch-DevPortable.ps1` produce
builds, so every successful portable build consumes a build number. UTC time and
commit identity remain part of the local folder name, but they do not replace
the central version. The build script refuses a dirty tree, the retired
`-AllowDirty` mode, and a central version that is not the ledger successor.

Portable previews, training snapshots, generated weights, caches, `bin`, and
`obj` are not committed and are not GitHub Releases. Every portable build is
recorded in `docs/BUILD_LEDGER.json`; the ledger commit is pushed before the
previous build directory is deleted. Exactly one portable build directory is
kept locally. A failed commit or push retains both the previous and new build.

## Arithmetic cadence and publication hold

The historical arithmetic cadence starts at `0.0.1`, followed by every twentieth
checkpoint. With components limited to 0 through 99, cadence-matching `z` values
are:

```text
1, 21, 41, 61, 81
```

Equivalently, with `ordinal = x * 10000 + y * 100 + z`, an ordinary version is
cadence-matching when `ordinal % 20 == 1`. The current publication hold excludes
all pre-2.0 checkpoints from executable release eligibility. The one-time
`2.0.0` product promotion is release eligible even though it is outside the
arithmetic cadence. Later versions use the unchanged cadence.

Examples:

```text
0.0.81  historical cadence match, publication held
0.0.99  internal
0.1.0   internal
0.1.1   historical cadence match, publication held
2.0.0   eligible product promotion
```

Release eligibility covers the cadence and current publication hold. It does not override failed
tests, dependency or model provenance, clean-machine validation, a dirty tree,
or missing release artifacts.

## GitHub publication

The 2026-09-08 maintainer decision authorizes the complete 2.0.0 installer and
portable ZIP release only after all required gates pass. It explicitly withholds
intermediate public releases, even at cadence-eligible checkpoints. Phase commits
still go to `origin/main`. Keep existing local portable build history intact.
The current repository's GitHub release list was empty on that date; no earlier
1.x release identity has been verified here, and none may be fabricated.

An eligible checkpoint becomes a public GitHub Release only when all of these
conditions are true:

1. The checkpoint is committed and pushed to `origin/main`.
2. Every required build, test, license, provenance, and clean-machine gate
   passes from a clean checkout.
3. The maintainer has authorized that release checkpoint.
4. An annotated tag named exactly `v<version>` points to the checkpoint commit.
5. The installer and portable ZIP are built from one common publish output and
   pass the release artifact validator.

The release operator then pushes the annotated tag:

```powershell
git tag -a vX.Y.Z -m "Release X.Y.Z"
git push origin vX.Y.Z
```

The tag workflow validates the tag, rebuilds and revalidates the artifact pair,
and publishes the installer, portable ZIP, checksums, SBOM, release metadata,
release notes, and known limitations. A normal push to `main` never creates a
tag or public release.

## 2026-08-19 build-numbering correction

The central version remained at `0.0.21` while 430 portable builds were
produced, followed by two builds stamped `0.0.22`. The previous policy that let
preview rebuilds reuse a version is withdrawn.

The 432 historical build directories are assigned build numbers 1 through 432
in their existing chronological lexical order. Their binaries and folder names
are not changed. `docs/BUILD_LEDGER.json` preserves each stamped version,
commit, build time, executable checksum, and retention state.

Build 432 maps to the corrected ledger version `0.4.32`. The first corrected
successor is `0.4.33`; later builds continue one ordinal at a time. The 22 historical release-eligible builds are marked
`missed-historical`; no retroactive tags, artifacts, or releases are fabricated.
The numeric cadence next reaches build 441, version `0.4.41`; publication is
currently withheld until the authorized 2.0.0 milestone.

The August correction changed the then-proposed first functional release from
`1.0.1` to `1.0.0`. The September 8 maintainer decision supersedes that future
target with `2.0.0`; it does not rewrite the August build records.
