# Dual-view metadata admission foundation

This module checks a caller-bound train/dev source-identity manifest and a
separate original-pixel proposal payload. The caller must capture proposals
without truth before supplying the separate truth payload. `freeze_capture`
does not prove that chronology or truth isolation: caller-supplied digests are
consistency checks, not independent pre-truth capture receipts. The payload
must include every train source, including failures; dev inputs are identities
only. Local and context tensor hashes are unverified metadata, not authenticated
tensor contents. The
current contract handles one already-bound original-coordinate frame per source;
production panelization and transforms require a future verified adapter.

Association uses the tolerance bound in the pre-capture manifest, at most five pixels. Only unique
connected-marker matches are marked pending; isolated markers, ambiguities,
duplicates, unmatched proposals, review-only overlays and paired tensor hashes
seen in any review-only context remain review-only. All unmatched truth and
failed sources remain in the report. Pending-positive weights are balanced by
source and are descriptive only. No negative rows are admitted and every report
sets `training_approved=false`.

Expected digests must come from an independently trusted producer. This module
does not establish capture time, authenticate the caller's claims, verify tensor
contents, establish real runtime sampler parity, or prove that historical source
families are independent. The selected tolerance and producer protocols need separate
preregistration and review before training. No rendering, model, optimizer,
private data or reserved data is accessed by this code.
