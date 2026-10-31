<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# PDFium host diagnostic

A non-release Production-mode diagnostic application was published from
commit `46bfb59a964fefdf6aece5fe209d8586541e78d1` and version `0.4.35`.
The application assembly SHA-256 was
`ff665d753cf13c0c0e4371fa1596ce79661070e6f5ffe720dc3764eaf85b03bc`.
The staged renderer SHA-256 was
`e61fa4e46ada9ae3e9040865674c35c3abdb5722031d5c9447a54155e483a468`.
The candidate archive SHA-256 was
`5aabf7f2c9d04dc2a130dc5d99e8b706e5de23dde175f769827bd762c643eb78`.

The packaged-PDFium production-runtime smoke exited 0 on the development
computer and, after archive hash verification, on Flex5Laptop's existing
Windows 11 installation over SSH. The laptop's native renderer also passed
the synthetic 72 by 72 pixel smoke. The resulting report SHA-256 is
`22bdcc25ecca543b5c83f35053ebf52d04aedebefdd80877ea247852e73c1314`.
These are host diagnostics, not clean-machine evidence, production model
acceptance, an installer test, or release approval. The candidate is not a
release package and does not change any release gate.
