<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Clean Windows guest control check

The laptop's existing guest was started without networking and with a temporary
snapshot overlay. QEMU's loopback QMP endpoint responded, but the virtio guest
agent channel did not respond to `guest-ping`; QMP reported its frontend was
not open. This does not establish whether the guest agent or driver is absent,
stopped, or the guest had not finished booting. QMP `system_powerdown` shut the
guest down normally; the base disk's size and modification time matched their
pre-boot values. No guest UI, installer or offline disk modification was used.

The official virtio-win project's stable ISO was staged for review on the
laptop, not installed. Version 0.1.302 is 877,373,440 bytes, SHA-256
`303f7ae40dad495d6ae474fdc571df58958a4dbc5c37a522d80f9a203867949d`.
The ISO was mounted read-only for inspection and then dismounted. Its Windows
10 x64 virtio-serial catalog and driver reported valid Microsoft signatures;
its QEMU guest-agent MSI and guest-tools installers reported `NotSigned`.
The archive listing did not provide an ISO checksum or detached signature.
This is not a recommendation to install the unsigned components without
further review.

The current guest still lacks a verified command and result channel. Clean
Windows installer and portable tests therefore remain incomplete. Host-level
smoke results do not substitute for clean-guest evidence.
