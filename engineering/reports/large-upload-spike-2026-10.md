---
title: "Large upload spike: dropping 1, 10 and 50 GB files on a library page"
summary: "Measured browser uploads of 1, 10 and 50 GB through a Dashboard-style relay to an Engine-style store, plain versus resumable, with dropped connections and Cancel; recommends resumable 64 MB chunks and a 50 GB warning threshold for D44."
audience: "product-owner, developer"
category: "report"
product_area: "engine, dashboard"
---

# Large upload spike: dropping 1, 10 and 50 GB files on a library page

October 21, 2026. Work packet WP11 of [Simpler library setup](../specs/library-setup-groups.md) (decision D44). Investigation only: no product code changed. The throwaway harness is in [`large-upload-spike-2026-10/harness/`](large-upload-spike-2026-10/harness/), and the raw numbers are in [`measurements.jsonl`](large-upload-spike-2026-10/measurements.jsonl).

## Summary

- **Resumable upload is needed.** A plain upload (the whole file in one request) fails outright once it takes longer than 100 seconds, because the Dashboard's link to the Engine gives up at 100 seconds. That limit is reached at about **10 GB on a wired gigabit network** and **about 3 GB on Wi-Fi**. When the connection drops, a plain upload starts again from zero: a drop at 60% of a 10 GB file sent **18.9 GB** to deliver 10 GB.
- **Resumable upload in 64 MB pieces worked at every size tested, up to 50 GB.** A dropped connection cost one retry and a few seconds, with no data resent. Cancel left nothing behind. Engine and relay memory stayed at about **100 MB** whatever the file size. The browser stayed at about **380 MB**.
- **Speed is the network, not Library.** On a simulated gigabit network every size ran at the network's speed (about 108 MB/s): **1 GB in 9.5 s, 10 GB in 1 min 42 s, 50 GB in 7 min 52 s**. On simulated Wi-Fi (30 MB/s), 1 GB took 34 s and 10 GB took 5 min 44 s, including a drop.
- **The file is stored once, not twice.** The upload is written into a hidden working file next to its destination and renamed at the end. Peak extra disk use equals the file's size.
- **Proposed D44 setting:** the amber "Large uploads can fail" warning appears **above 50 GB** (the largest size proven), not at 4 GB. This holds only if the local check on the real home network agrees. If it does, the warning effectively never shows for normal media.
- **Two Engine settings must change for WP12.** Uploads must not count against the Engine's limit of 600 requests a minute. That limit is shared by everything the Dashboard sends. Uploads also need their own longer per-piece time limit (details below).

## What was measured, and how honestly

The Engine has no upload action yet, so the harness copies the path WP12 will build:

1. A real browser (headless Chromium) picks a file and sends it to a **Dashboard-style relay**. In Tuvima the browser never talks to the Engine directly: images, streams and the backup upload all go through the Dashboard, which adds the Engine credentials server-side. The setup backup upload is the closest existing example.
2. The relay streams each request on to an **Engine-style store** with a server-side HTTP client. Like the Dashboard's Engine clients, that client keeps the default 100-second time limit.
3. The store writes to a hidden working file, then renames it when complete. It can cut the connection once at a chosen byte, to simulate a drop, and can slow itself down to simulate a network speed.

| Limitation | Effect on the numbers |
|---|---|
| Everything ran on one Linux cloud machine (loopback network). The "gigabit" (110 MB/s) and "Wi-Fi" (30 MB/s) speeds are simulated by the store reading at that rate. | Times are what those speeds allow; real Wi-Fi also has jitter and pauses. The real-network check is in the batch list. |
| The 10 and 50 GB source files were sparse (they read as zeros without disk reads). | Browser-side disk reading was not measured; a real film is read from disk at a few hundred MB/s, far above the network. |
| The 50 GB runs discarded the data instead of writing it (the cloud disk has 29 GB). | Disk writing was measured separately: 10 GB was written at 294 MB/s, so the disk is not the bottleneck at network speeds. |
| The drop was made by the Engine side cutting the connection. | A real Wi-Fi drop can instead leave a request hanging silently. WP12 needs a "no progress for 30 seconds" rule that abandons the piece and resumes (see recommendations). |
| Not measured: Windows Defender scanning new files, reverse proxies (nginx, Traefik) in front of Library, Docker volumes, and the Blazor live connection (`InputFile`). | The live-connection route is ruled out by D44 itself. Proxies often cap request bodies (nginx's default is 1 MB); 64 MB pieces need that cap raised, and this goes in the WP12 docs. |

## Measurements

All runs used a real browser. "Relay" means through the Dashboard-style hop. Memory is peak working set.

### Plain versus resumable

| File | Network | Method | Result | Time | Data sent | Engine / relay / browser memory |
|---|---|---|---|---:|---:|---|
| 1 GB | machine limit | plain, direct | done | 4.0 s | 1 GB | 89 MB / – / 374 MB |
| 1 GB | machine limit | resumable, direct | done | 4.2 s | 1 GB | 95 MB / – / 374 MB |
| 1 GB | machine limit | plain, relay | done | 4.4 s | 1 GB | 92 / 88 / 376 MB |
| 1 GB | machine limit | resumable, relay | done | 5.3 s | 1 GB | 99 / 93 / 375 MB |
| 1 GB | gigabit | plain, relay | done | 9.3 s | 1 GB | 103 / 104 / 375 MB |
| 1 GB | gigabit | resumable, relay | done | 9.5 s | 1 GB | 107 / 105 / 373 MB |
| 1 GB | Wi-Fi | resumable, relay | done | 34.3 s | 1 GB | 106 / 106 / 377 MB |
| 10 GB | gigabit | plain, relay | done, **93 s of the 100 s limit** | 93.1 s | 10 GB | 98 / 99 / 375 MB |
| 10 GB | gigabit | resumable, relay | done | 101.9 s | 10 GB | 102 / 100 / 379 MB |
| 10 GB | Wi-Fi | plain, relay | **failed at 100 s**, working file removed | 106 s | 4.7 GB wasted | 98 / 101 / 378 MB |
| 10 GB | Wi-Fi | resumable, relay, drop at 60% | done, 1 retry | 343.7 s | 10 GB | 106 / 106 / 380 MB |
| 50 GB | gigabit | resumable, relay | done | 471.6 s | 50 GB | 108 / 104 / 393 MB |
| 50 GB | gigabit | resumable, relay, drop at 60% | done, 1 retry | 473.2 s | 50 GB | 103 / 109 / 393 MB |

### After a dropped connection (10 GB, gigabit, drop at 60%)

| Method | What happened | Time | Data sent for 10 GB |
|---|---|---:|---:|
| Plain | The working file was discarded; the browser started again from zero | 155.8 s | 18.9 GB |
| Resumable | Asked the Engine how much had arrived, carried on from there | 96.2 s | 10 GB |

### Cancel (10 GB resumable, cancelled at 30%)

The working file was deleted at once. The store held 3.0 GB at the moment of Cancel and **0 bytes** afterwards.

### Piece size (10 GB, machine limit, direct)

| Piece size | Requests | Time | Requests per minute at gigabit speed |
|---|---:|---:|---:|
| 8 MB | 1,280 | 40.6 s | about 825 (over the Engine's 600 limit) |
| 64 MB | 160 | 34.9 s | about 100 |
| 256 MB | 40 | 34.9 s | about 26 |

64 MB is as fast as 256 MB and loses at most 64 MB on a drop. It still finishes inside the 100-second limit on a link as slow as 0.65 MB/s.

## Recommendations for WP12

1. **Method: resumable uploads in 64 MB pieces over normal web requests**, browser → Dashboard → Engine, never over the live Dashboard connection. Flow: *start* (name, size, kind, page) returns an upload ID; each piece is sent with its byte offset; *status* says how much has arrived; *cancel* deletes the working file. The pattern follows the open tus protocol, but there is no need for a tus library: it is four small Engine actions. (Reason: **Reliability**. A drop costs at most one piece, and nothing new needs a licence check.)
2. **Write into a hidden working folder on the destination's own drive** (for example `<Import folder>/.data/uploads/`) and rename it into place when the last piece arrives. The file is then stored once, and filing only starts on a complete file. Before starting, check there is free space for the file plus a margin, and refuse with a plain message if not. (Reason: **Reliability** and **Performance**.)
3. **Rate limit.** The Engine counts requests per sender address, and every Dashboard request shares one address. Small pieces would use up the budget of every screen for every user. Exempt the upload actions from the general limit and give them their own limit: one upload at a time per person, a few in total. (Reason: **Reliability**.)
4. **Time limits.** Give the Dashboard's upload relay its own client with a per-piece limit of about 5 minutes, separate from the shared 100-second clients. Raise the Engine's request-size limit for the piece action only, to 64 MB plus headers. (Reason: **Reliability** on slow Wi-Fi.)
5. **Stalls.** In the browser, abandon any piece that makes no progress for 30 seconds, ask for the status, and resume. Retry with a growing pause for up to about 2 minutes, then show "Upload paused: connection lost" with Resume and Cancel. Working files with no activity for 24 hours are removed by a background task. (Reason: **Reliability**. A real Wi-Fi drop can hang a request instead of failing it.)
6. **Progress and Cancel** come straight from the piece loop: progress updates after each piece, and Cancel stops the loop and deletes the working file. Both behaved correctly in the spike.
7. **D44 size setting.** Default it to **50 GB**, the largest size proven, as a setting in `config/`. Below it, uploads run normally. Above it, the amber warning offers the Import folder's path with Copy path. If the real-network check fails, lower the setting to the largest size that passed, and record the result here.

## Run it yourself

On SHY-DESKTOP, from this folder (needs the .NET 10 SDK):

```powershell
pwsh -File engineering/reports/large-upload-spike-2026-10/harness/start.ps1
```

Then, from a laptop on Wi-Fi or a wired PC on the same network:

1. Open `http://SHY-DESKTOP:5200/`.
2. Type `/relay` in the base-path box.
3. Choose a 1 GB file, pick **chunked** (resumable), and press Upload.
4. When it finishes, the page shows time and MB/s. Repeat with a 10 GB file, then pull the cable or turn Wi-Fi off for 10 seconds halfway: it should carry on by itself.
5. Press Cancel during another upload: the `store` folder next to the script should then be empty.

Stop the harness by closing its two `dotnet` windows (or `Stop-Process` on those two process IDs). The harness is not part of the app and is not built by CI. It is never pointed at real media folders: it writes only to its own `store` folder.

## Plain English summary

Big files can be uploaded reliably, but only if they are sent in pieces that can resume after a dropped connection. Sending a file in one go fails for anything that takes longer than 100 seconds: that is about 10 GB on a wired network and about 3 GB on Wi-Fi. A drop also means starting again from zero. Sent in 64 MB pieces, a 50 GB file arrived in about 8 minutes on a simulated wired network, survived a dropped connection, and used very little memory. Cancel cleaned up properly. I recommend building it that way and only showing the "large uploads can fail" warning above 50 GB. A quick check on your real home network will confirm the numbers before WP12 relies on them.
