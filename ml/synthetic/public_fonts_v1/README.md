# Public synthetic font package v1

This package contains unmodified Noto Sans Regular, Noto Serif Regular, and
Noto Sans Mono Regular. All four upstream files, including the original license
notice, come from the official `notofonts/noto-fonts` repository at commit
`ffebf8c1ee449e544955a7e813c54f9b73848eac`. The exact source URLs, Git blob
identities, and SHA-256 hashes are in `manifest.json`.

The intended purpose is project-local synthetic graph and OCR rendering. The
font and license files are public upstream assets and contain no user or
private data. They are eligible for version control in this repository together
with the original OFL copyright and license notice.

The upstream notice in `assets/LICENSE` identifies SIL Open Font License 1.1.
Retain that copyright notice and license when distributing these font files.
This package records provenance; it is not a legal approval or approval of any
dataset, model, distribution, or training experiment.

Run `python -m ml.synthetic.public_fonts_v1.fonts` from the project root to
check the manifest, asset inventory, original hashes, font family and style,
numeric glyphs, and monospaced advances. The verifier does not search host
fonts. A future synthetic corpus must explicitly adopt these files, bind its
own source and generated-data hashes, and verify split disjointness. Different
family names and bytes do not prove independence from a historical benchmark
or eliminate visual similarities among fonts from the same upstream project.
