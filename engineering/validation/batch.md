# Local validation batch

Items built and merged in cloud sessions that still need a check on SHY-DESKTOP. Cloud sessions append rows; a local validation session works through them once per batch (`.claude/rules/cloud-first.md`). Status: Awaiting, Validated, Failed (with a fix brief).

| Item | PR | What to check | Status |
|---|---|---|---|
| Scheduled tasks list: five more background jobs | this PR | Open Settings, System, Logs, Scheduled tasks. Provider health checks, Playback session cleanup, Library folder health check, Streaming file cleanup and Scheduled metadata refresh should be listed as running all the time. The two hourly ones (Streaming file cleanup, Scheduled metadata refresh) show a last run within an hour; the others say "Runs quietly in the background". Failure: any of the five missing, or a frequent job filling the run history. | Awaiting |
| Same answer every run: comic series, shared people | this PR | Re-run the ingestion record-and-replay comparison used for #327 on `C:\Temp\real_import` (one ingestion run, Engine slot). Batman, The Sandman and Saga should reach their series ID in both runs; Frank Herbert should keep his author link on the Dune book and audiobook; compare the Dune, Matrix and Expanse cast IDs. Failure: any of the three still differs between runs (send the difference list back as a fix brief). | Awaiting |
