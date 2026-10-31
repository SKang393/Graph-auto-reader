<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Internal preview 0.4.35

The clean-source internal checkpoint was built from commit
`46bfb59a964fefdf6aece5fe209d8586541e78d1`. Its executable SHA-256 is
`fa536d303c7526086923e40e25acef2750cc7ed3e72a01724b9e589db88464bc`;
the reviewed OpenCV binary SHA-256 is
`c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31`.
The local preview ZIP SHA-256 is
`6cf370fbc931667c51a861fe72e1920284262cfe051f546338f8e775f203f31e`.
The ZIP's executable was checked against the build identity and no private
attestation was present in its entries.

The build's fast checks passed 718 application tests, 23 domain tests and 129
integration tests; 7 integration tests were skipped. The reviewed OpenCV
source and policy checks passed. The build runs in `ManualPreview` mode, has
no approved models included, and marks automatic stages unavailable. It is
for local maintainer testing only, not a production installer or release
portable. Clean-Windows acceptance and automatic-recognition gates remain open.

On the separate laptop's existing Windows 11 installation, the verified ZIP
was extracted and a model-free diagnostic loaded the OpenCV library and ran
the application's smoke process successfully over SSH. The result SHA-256 is
`9ac701f0e146936e5e1557ea0dc0b61af7caf02c076d13da0fa60bf23103db88`.
The report sets `releaseEvidence=false`; this host is not a clean Windows VM,
and the smoke result is not a visual or recognition-accuracy test.
