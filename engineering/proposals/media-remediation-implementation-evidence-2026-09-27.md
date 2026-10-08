# Implementation and verification evidence — 27 September 2026

## Product-owner walkthrough

1. **Setup and startup (WP1):** setup now offers display language, metadata language and country, with system culture defaults for unconfigured installations. Optional providers, including ComicVine, are offered before media exists. Protected real-media startup checks the inventory first and performs byte verification in the background. Saving language uses a separate configuration lock so it does not wait for the media hash audit.
2. **Safer identification (WP7):** album reconciliation remains enabled. Explicit artist and MusicBrainz conflicts reject a candidate; title similarity alone is insufficient. Wikidata-derived IDs cannot corroborate a later Wikidata match. Movie searches strip supported technical release suffixes without renaming files or rewriting embedded titles. Existing wrong catalogue records are retained for later reingestion.
3. **Playback and images (WP3/WP8):** compatible video conversion explicitly produces an encoder-compatible pixel format, failed packages retain their failure, and playback preparation preserves start intent. Browser telemetry records only registered device IDs. HEIC grid images decode as complete images before resizing; derivative concurrency is bounded. Gallery media requests autoplay.
4. **Browsing and feedback (WP4/WP5/WP9/WP10/WP11):** processing rows/cards use a reduced-motion-aware yellow outline. Album/artist browse defaults to cards; Songs remains a list. Browse errors are request-local. Personal actions load missing state before saving. Background notifications coalesce ingestion activities, avoid titles and defer during playback/reading. Known unmatched items no longer qualify as completed additions merely because file intake succeeded.
5. **Operations (WP12):** file registration, identity work and contributor work are different units. Multipart audiobooks share work identity. Optional contributor requests have a durable separate queue with timeouts and retry limits. The dashboard says **Finishing enrichment** with active/queued counts instead of a fabricated 99%.
6. **Performance (WP0/WP6):** work/person details load a scoped projection. Contributor data is materialized once and reused, including audiobook/book date relationships. Shared compact artwork requests small/medium renditions. Detail timing logs and a read-only performance probe provide repeatable evidence.

## Confirmed causes

- **Radioactivity:** local/MusicBrainz evidence identifies Age of Days, 2013. The accepted Wikidata item was Kraftwerk's Radio-Activity, 1975. Implicit text fallback, incomplete strategy coverage and soft artist/date checks allowed that mismatch. This was not fixed by editing the real media or overriding this title.
- **17–18%:** 479 registered files were compared with approximately 85 settled identity jobs; hundreds of audiobook segments intentionally share work identity. Six TV retries also waited on optional contributor work inside an episode deadline.
- **99%:** once identity work settled, pending contributor work prevented whole-run completion and a generic cap displayed 99%. New contributor operations also initially used `pending`, which the lease query did not consume. They now enqueue explicitly, recover old pending entries and recover expired/interrupted work. The UI presents this phase without a percentage.
- **Video:** ten-bit source pixels were incompatible with the selected H.264 main output; failed conversion packages could return to preparing. Browser heartbeat device IDs also violated the registered-device foreign key.
- **Slow audiobook details:** repeated canonical-contributor view evaluation, including hidden repetition inside date selection, dominated read projection time.

## Measurements and tests

- Real database read-only samples after contributor/date-query reuse: audiobook first 337.6 ms, warm 247.1–300.5 ms; credited person first 264.2 ms, warm 244.6–267.5 ms; catalogue first 462 ms, warm 474.8–505.2 ms. Previous immediately preceding samples were approximately 0.75 s, 0.75 s and 1.5 s respectively. These six-sample measurements are not a p95 page-load certification.
- Live audiobook detail API: 855–1097 ms versus an earlier 3461 ms observation. Still above the plan's warm target.
- Audiobook started from one Listen action; browser audio reached ready state 4 and advanced. Real TV frames rendered with horizontal controls. Full cold/warm playback latency distributions remain unmeasured.
- Heartbeat requests returned HTTP 200 after the device fix; no foreign-key error appeared in that verification run.
- Latest build: zero warnings and errors. Latest focused checks: 33 ingestion-presentation tests, 35 ingestion UI guardrails, 63 matching/worker tests passed. Additional prior focused API/UI/provider/ingestion checks passed. Earlier full-suite runs contained failures; a clean full-suite result is not claimed.
- Live status after the final restart: 479/479 files registered; contributor work had one succeeded, one leased and three queued. The page showed **Finishing enrichment**, not 99%. Unmatched release-filename movies were absent from completed additions.

## Still open / not certified

- Entire multi-format browser matrix, reader position/progress verification, HEIC service-level visual proof, personal-action persistence/Undo matrix and notification flood/reconnect testing.
- Early playable registration before a full content hash and a shared managed-file playback/write lease mechanism. Protected originals remain read-only; no deferred source rewrite was introduced.
- Sustained cold/warm/ingestion-loaded performance budgets, responsive/DPR image audit and further detail-query optimization.
- Persistent notification history and additional provider/backup/protection activity categories beyond the implemented ingestion coordinator.
- Fresh reingestion and validation of existing wrong/unmatched catalogue entries. No title-specific database correction or source-media repair was performed.
- Final full-byte source audit for the latest runtime was still pending at the last check. Inventory protection was active with no recorded events; do not describe that as a completed hash audit.

## Plain-English completion summary

The implemented fixes make identification safer, improve playback compatibility, reduce repeated page-loading work and explain remaining ingestion activity more honestly. The app is running with these changes, but the full remediation programme and performance acceptance matrix are not yet complete. Original files have not been repaired, renamed or moved by this implementation.


## Retail editor search and edition safeguards

The editor now requests selectable retail candidates instead of running the automatic ingestion accept/reject path. It searches a bounded pool of at least 25 candidates before ranking and returning the requested number. A one-result ISBN strategy no longer limits later title searches. Provider request failures are carried through the API and shown separately from empty search results, including partial failures when another provider succeeds. Caller cancellation remains cancellation.

EPUB ISBN extraction now validates checksums for both explicitly tagged and bare identifiers, so an invalid first identifier cannot mask a later valid ISBN. Original EPUBs are only read.

Automatic written-media matching rejects explicit edition-language conflicts. Apple ebook search does not supply reliable edition language, so an edition without explicit matching language or exact valid ISBN evidence is not automatically accepted; it remains available through manual search. This deliberately favors review over accepting a different-language edition. Existing catalogue matches are not retroactively rewritten.

Validation: 64 focused provider/search tests, six ISBN checksum cases, and two EPUB extraction cases passed. Regression coverage includes French/English/unknown edition evidence, provider outages, manual editor routing, and ISBN-to-title fallback candidate limits. The EPUB tests construct temporary fixtures, never modify real media. Live browser editor verification has not been performed for this patch.

Product summary: editor searches show choices instead of hiding everything except an automatic match; service failures are visible, and an unverified Apple edition cannot silently replace an English book's metadata.
