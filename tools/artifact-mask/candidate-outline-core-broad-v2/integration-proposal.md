# Outline-core candidate integration proposal

Status: exploratory. No production code or gate changes are included.

## Evidence

The frozen broad v2 synthetic component comparison reduced suppressed marker centers from 3/8 to 0/8. Marker truth pixels masked remained 0/562. All 16 ordinary cross/grid centers stayed masked in baseline and candidate. Arrow, bracket, and legend category counts remained 1 each, with all 83/83, 47/47, and 49/49 annotated structure pixels masked. Connecting-line intersection regions fell from 19 to 16, with no new region category.

The implementation is still a test-only wrapper around the current structural probability provider. It checks a long crossing and at least 15/16 occupied annular sectors for some radius 4 through 9, then raises marker-like probability in the existing 5 by 5 conflict core. The measured raster was 200 by 150. Timings were one warmed pass only and are not a performance benchmark.

## Smallest possible production change

Add a production structural-probability decorator that wraps `RasterPreOcrStructuralProbabilityProvider` and applies the tested core adjustment. Wire it only at the existing residual-mask composition boundary in `RasterResidualArtifactMaskAdapter`, preserving detector thresholds and region logic. Advance the residual composition version/fingerprint so prior cached outputs cannot be mistaken for results from the new rule.

## Required focused validation before integration

- Convert the broad v2 reflection comparison into direct construction of the production candidate decorator while retaining all 13 synthetic cases and criteria.
- Keep clear plus/X/grid centers masked, all marker centers and truth pixels reviewable, and arrow/bracket/legend coverage and region counts unchanged.
- Add sub-4 and over-9 pixel-scale outlined markers as explicit boundary cases before any follow-on tuning.
- Measure repeated candidate and baseline timings at representative full raster dimensions; single-pass synthetic timings are too noisy to establish cost.
- Run the existing approved non-sealed development checks after the candidate is wired. Do not promote based on these synthetic component checks alone.

## Limitations

No real or sealed data, OCR text truth, training, or production workflow acceptance was used. The radial support rule has explicit size limits and has not been validated against varied real marker shapes, anti-aliasing, or noise. Parent review is required before any shared production-source edit.
