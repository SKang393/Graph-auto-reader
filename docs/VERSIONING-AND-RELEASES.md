# Version and build identifiers

Graph Auto Reader uses a numeric `x.y.z` sequence. Components range from 0 to 99;
this is not Semantic Versioning. The build ordinal is
`x * 10000 + y * 100 + z`.

`Directory.Build.props` contains the central version. A packaged application
build consumes one ordinal. Installer and portable packages produced from one
common publish output share that version. Ordinary local-test and CI compiler
outputs are not packaged application builds.

`packaging/Prepare-CheckpointVersion.ps1` prepares the next checkpoint version.
Rollover resets the lower component to zero: `0.0.99` becomes `0.1.0`.
`docs/BUILD_LEDGER.json` records package identity, source commit, checksums and
retention state. A recorded version identifies one packaged build, even when
another build uses otherwise unchanged source.

The development portable scripts use the central version and require a clean
source checkout. A version number or passing compilation alone is not evidence
of digitization accuracy, clean-machine compatibility or release readiness.

Source commits and executable releases are separate. A normal push does not
create a release. Release artifacts require their own build, test, licensing,
provenance and distribution checks. Both Windows distribution formats use the
same version and common publish output.
