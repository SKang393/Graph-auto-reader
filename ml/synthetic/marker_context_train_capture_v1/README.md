# Bounded train-raster preparation

This prototype builds two procedural train images from fixed recipe indices 8
and 4, one with a visible artificial annotation cue and one uncued. Its
raster-only manifest contains only image identities, dimensions and the fixed
generator binding digest. Scene annotations and review-only overlay metadata
are returned separately and written only under `post-capture-truth`, outside
the `raster-only` tree when explicitly materialized.

The caller must independently freeze the digest of `source_binding()` before
calling `build` or `write_new`; source hashes are compared before and after
rendering. This is not a signature or a concurrent-write lock. The builder uses
process-global font resolver patches within its own rendering process, so it
must run alone, not concurrently in a shared interpreter. Source and font
hashes do not attest Python or native runtime libraries.

No model inference or optimizer is included. The generated glyphs are
review-only; their labels are not admitted. This prototype does not enforce OS
access isolation between the raster and truth directories, authenticate a
production proposal capture, or establish production preprocessing parity.
