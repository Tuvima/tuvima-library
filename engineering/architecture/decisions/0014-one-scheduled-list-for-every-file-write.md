# 0014 — Every write into a file goes through one scheduled list

**Date:** 2026-10-22 (Shy; spec `engineering/specs/batched-writeback.md` D1, D2, D10, D11) · **Status:** Accepted

## What
Library never writes into a media file at the moment something changes. Every change is saved in the data store straight away, and the file it affects gets one entry on a durable list (`media_file_write_intents`). A newer change to the same file replaces the waiting entry. A scheduled run (every 60 minutes by default, `run_interval_minutes` in `config/writeback.json`, or **Write now**) works through the whole list, building what it writes from Library's latest values at that moment. This covers tags in music, books, comics and videos, embedded artwork, and the profile name in View files (through ExifTool for photos, phone videos and PDFs, and Open XML for Office files).

Library moves are the one thing that happens straight away, because they are filing, not writing into a file: the move records its steps (planned, moved, checked) on the same list before and after it acts, so a crash part-way is finished or undone from what is on disk. Its tags then wait for the run like any other change. Everything else that touches a file (refresh, rescan, the folder watcher, retag, deletion and restore) respects a waiting or moving entry.

## Why
Shy wanted write-back "built one way". One path means one set of safety rules, one place to see what is waiting, and far fewer writes to disks and cloud-synced folders (five edits to a file become one write) (**Maintenance**, **Reliability**, **Performance**). The data store stays the true record, so nothing a person sees waits on the schedule.

## Trade-offs accepted
- A file can lag up to one interval behind Library; a copy taken out of Library before the run may not carry the latest tags.
- A large first run after adding a library can take a while; runs are batched, resumable and shown in Settings.
- Nothing may call the tag writers directly; a guardrail test (`FileWriteQueueGuardrailTests`) enforces it.

## Revisit if
People need a file's tags to change at once (for example for another app that reads them live), or the list grows large enough that a single run per interval is too slow.
