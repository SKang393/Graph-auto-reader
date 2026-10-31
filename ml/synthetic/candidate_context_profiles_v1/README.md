# Candidate context profiles v1

This procedural, fixed-recipe generator is review-only. It does not train a
model, approve data or measure product accuracy. The evaluation split is an
exploratory development split and is not an acceptance or release gate.
Recipe split `evaluation` maps explicitly to the existing scene split
`validation` and contextual-negative split `dev`; training maps to `train` in
both. The mapping appears in the manifest and each case recipe.

The recipe has eight training cases in the existing `vector_clean` and
`print_monochrome` profiles and four evaluation cases in `scan_rough`. The
assigned renderer, font, template, marker and degradation family keys are
disjoint between splits. These are labels on shared existing code, not evidence
of independent renderer implementations. Project-local Noto Sans, Serif and
Mono fonts and the OFL notice are authenticated by `public_fonts_v1`; distinct
font bytes do not establish historical or visual independence. Neither source
identities nor near-duplicates against previous data have been checked.
For split isolation, the generator checks configured family keys and exact
whole-scene image hashes. It does not check whether individual candidate or
negative crop bytes overlap across splits.
The historical scene declaration retains its legacy `system` font-family
label for schema compatibility. The renderer annotation and manifest record
the actual verified Noto font and SHA-256 used for that case. Rendering patches
the shared resolver in-process, so run this generator in its own process.

Contextual negative annotations come from the existing train/dev profile and
remain review-only, including its overlap exclusions and omission audit. Their
presence does not authorize negative-label admission or optimization. The
manifest records source hashes and records these limits. A failed run may leave
a partial output folder without a manifest; preserve and review it.

Run from the project root, using a new absolute path under `artifacts` whose
parent already exists:

```
python -m ml.synthetic.candidate_context_profiles_v1.generator --output E:\Graph-auto-reader\artifacts\NEW-REVIEW-DIRECTORY
```

The output contains PNGs, masks and annotations. Never treat it as held-out
independent evidence or as a production-approved model/data package.
