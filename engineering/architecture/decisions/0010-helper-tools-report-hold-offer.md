# 0010 — Missing helper tools: report them, hold the files, offer a checked download

**Date:** 2026-10-18 (Shy; spec `engineering/specs/archive/first-run-prerequisites.md`) · **Status:** Accepted

## What
The Library keeps one catalogue of the helper tools it relies on (FFmpeg and FFprobe, smooth-streaming support, Tesseract, the image engine, the local AI engine and each AI model). That one catalogue drives first-run setup, Settings → Tools and requirements, the start-up log line and the health checks, so they can never disagree (D3). A missing tool is a state the Library reports, never a reason to fail someone's file (D1): a video or audio file that needs FFmpeg is registered and identified as normal, held with a durable "waiting for media tools" marker, and finishes by itself when the tool appears. Setup warns and never blocks (D5). FFmpeg is always Required (Shy's amendment to D4). When a tool is missing or broken the Library offers **Install for me** / **Download again**, which fetches an exact pinned build from a fixed GitHub address, checks its SHA-256 fingerprints before anything is used or run, and logs every attempt in the activity log; nothing downloads by itself (D2, D7, Q5 "Offer"). Windows installer and Docker images keep bundling the tools; containers never download into themselves.

## Why
Shy's first test run on a fresh install failed silently: FFmpeg was not on the computer and every video and audio file failed as if it were corrupt. **Reliability** (no pile of failed files, recovery without a restart or re-scan), **Maintenance** (one catalogue instead of four separate checks) and **Privacy** as Shy defined it on 2026-10-18: privacy first means the Library never shares your data, not that it cannot fetch what it needs. A neutral request to the download host reveals only that someone fetched the file.

## Trade-offs accepted
- **Supply chain.** Fetching and then running a program is only safe because every address and fingerprint is fixed in `config/tool_downloads.json` and checked before use; changing that file is an administrator act, like changing an explicit tool path already was.
- **Tesseract on Windows needs its own installer** and administrator approval, and is offered only when the Engine runs as an ordinary program (not as a Windows service).
- **Only Windows x64 has a pinned build.** Linux and macOS keep the "install it yourself" instructions until a build is chosen and licence-checked.
- **"Waiting" can hide a problem** if nobody looks; the setup list, the waiting label, the Settings page and an administrator banner all say the same thing.

## Revisit if
A pinned Linux or macOS build is chosen, the Library starts shipping its own updater, or a tool stops publishing fingerprinted releases.
