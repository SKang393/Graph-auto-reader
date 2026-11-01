<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Conservative outline-core synthetic diagnostic

This candidate is test-only and unapproved. The frozen validation used 14 synthetic cases with oracle geometry and empty text boxes. Marker-center suppressions changed from 5 to 1; both baseline and candidate missed 16 of 45 clear-cross/grid centers. See `validation-outcome.json` for pixel and prohibited-structure results. These are exploratory component results, not release evidence.

`development-test-source-executed.cs` and `validation-test-source-executed.cs` preserve the exact executed test files, with SHA-256 values `1e6d16d8d55595fd9eeea96e447906265507ac868112217fe19c1e85f12f078a` and `7e8375fb0fc407c843db8acd35ab7e85f2fe5d771305741dbcbcb773a0c90bc9`. At execution the latter was located at `tests/GraphReader.App.Tests/ArtifactMaskConservativeOutlineCoreV1CandidateTests.cs`; it is archived here so its exact assembly-bound research harness does not run as an ordinary test. The outcome commands describe the historical execution, not a fresh validation. The protocol families are consumed; their executed/frozen snapshots preserve the pre-run states.
