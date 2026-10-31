# Clean Windows host preparation, 2026-10-30

The separately supplied Windows test laptop is reachable through authenticated SSH. The maintainer clarified that the E: storage restriction applies to the development computer; the laptop's C: drive may be used for tests. QEMU was extracted into a dedicated test folder on the laptop from the Windows installer linked by [QEMU](https://www.qemu.org/download/). The installer matched its publisher's SHA-512. The tested executable reports `QEMU emulator version 11.1.0 (v11.1.0-12130-ge470268ff4)`.

The laptop holds these official evaluation images, each verified against Microsoft's published SHA-256:

- Windows 11 Enterprise 26H2, English x64: `bc3f24086ebadc94489066b5ad78089e2cf5c3491e90e790bb81a2b199c10e38`. [Microsoft verification](https://support.microsoft.com/en-us/servicing/os/windows/docs/2026/09/verify-the-authenticity-of-a-windows-11-enterprise-evaluation-iso-file).
- Windows 10 Enterprise 22H2, English x64: `ef7312733a9f5d7d51cfa04ac497671995674ca5e1058d5164d6028f0938d668`. [Microsoft verification](https://download.microsoft.com/download/c/1/1/c11d2ca5-967c-45c0-bc7d-2d9ca3f1fe07/Windows10Enterprise22H2HashValues.pdf).

The project host-readiness script, SHA-256 `d7820564be80f21dd6fe871ff8333c1b87fd49806c178069e2435348b4c5b003`, returned `inputs_checked_manual_verification_needed`, matching ISO checksums, and no listed input blockers for each image. QEMU executable SHA-256 is `47d57a6072e0bb3bd98f87926eb129eb1736dfe818c67b3b81ef7ce4edd0b3cd`; installer SHA-256 is `f98a8aeb5f7faea9765b6dee28316c266cd179d80354a2fed8e50176f9a2e59f`.

Windows Hypervisor Platform was enabled without restarting the laptop. Windows reported that a restart is required; a short QEMU WHPX startup probe remained running, which is not a complete virtualization test. This QEMU build reports `-tpmdev` as unavailable, so the Windows 11 media has not been established as bootable under the required virtual hardware. Windows 10 media is available for a separate compatible clean-guest attempt.

No clean guest, installer run, portable test, or release evidence has been created. The harness requires a newly installed guest less than 24 hours old, so guest installation is deferred until a candidate is ready to validate.

## Follow-up, 2026-10-31

The maintainer subsequently installed a Windows 10 guest, which reaches its
desktop under QEMU software emulation. The guest diagnostic disc is attached,
but a completed guest diagnostic or installer test has not been recorded. The
running QEMU command does not expose an SSH-accessible monitor, and its guest
has no network adapter. Avoid relying on remote-desktop keyboard control from
the development computer during the maintainer's work.

The separate laptop's existing Windows 11 host was tested over authenticated
SSH with the model-free preview diagnostic, not the guest release harness. The
portable archive SHA-256 was
`9c885fba3041714a22dfdf96fcbda2417e2f0d2d4f57e2bf5b3b644b4fdabf5d`;
the script SHA-256 was
`e87eb32d924fd96223cb74bb97cbdf8279a53e35eac9023a97266173cb26fa5e`.
The native OpenCV library loaded, and the application smoke process returned
exit code 0 without timing out. The transferred report SHA-256 is
`1439cd3d07849ceaaf1ae0e006fc72ba5176beb44bbda2a6d98ce776def5d9b8`.
This test used the laptop's existing Windows installation and a model-free
preview; the report explicitly marks `releaseEvidence=false`. It does not
establish clean-Windows installer acceptance, portable release acceptance,
visual behavior, or automatic-recognition accuracy.
