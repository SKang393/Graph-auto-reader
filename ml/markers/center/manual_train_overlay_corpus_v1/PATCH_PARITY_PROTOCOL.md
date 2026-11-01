<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Controlled-overlay native patch parity

After the 200-image proposal-domain capture is frozen, select every actual
emitted lattice position within three pixels of each controlled overlay center,
provided its 33-pixel input lies entirely within that overlay's opaque patch.
The selection uses controlled coordinates but emits no class labels. Record
all unavailable images and images with no selected proposal in the denominator.

Hash each selected Python three-plane float32 patch. Reconstruct each captured
panel and plot polygon in the native candidate sampler, using the same
checksum-bound application assembly and candidate configuration as the
capture. A diagnostic runner may return fixed zero outputs while hashing the
actual sampler input tensors; it must not load truth, train a model, or alter
proposal enumeration. Verify the complete emitted-coordinate sequence against
the frozen stage record before comparing selected tensor hashes. Account for
every selected patch and report any mismatch rather than dropping it.

All per-panel identities, image data, target positions, and tensor hashes stay
in ignored project-local artifacts. This checks sampler parity on controlled
positions, not natural-marker accuracy, full-image precision, model approval,
or release acceptance. No optimizer step, development payload, or sealed
payload is permitted in this comparison.
