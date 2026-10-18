# Spec: First run and prerequisites

> **Status:** In progress · **Version:** 1 · **Last updated:** 2026-10-18 · **Related docs:** [Product overview](../product/overview.md) · [Windows install (public)](../../docs/install/windows.md) · [Install from source (public)](../../docs/install/from-source.md) · [Configuration reference (public)](../../docs/reference/configuration.md)

> Written 2026-10-18 after Shy's first test run on a fresh data store, Group 1 of the bug list (bugs 25, 26, 3, 1, 2). Everything below was checked against the code on main (merge of PR 259). Where something is inferred rather than seen, it says so. Shy answered every question and Locked the spec on 2026-10-18. Nothing is built. Shy said not to download FFmpeg by hand during the test, then asked whether the Library should fetch missing tools itself at start-up and clarified that privacy first means not sharing data, not refusing to download what the app needs. Options are in section 7 (Q5); the recommended one is WP7.

## Product walkthrough

**What goes wrong today.** On a run from source, FFmpeg (the program that reads video and audio files) is not on the computer. The Engine notices at start-up and writes one line in its log, nothing else. Setup says nothing. Then every video and audio file fails with "Media inspection did not return technical facts", and nothing gets identified. Nobody is told why or what to do.

**What it feels like after.**
1. First run: during "Getting things ready" the Library checks the tools it relies on. If one is missing, Shy sees a plain note, what it will affect, and the exact way to fix it for how the Library was installed. Setup can still be finished (D5).
2. Adding media: a video or audio file never fails just because a tool is missing. It waits, shows "Waiting for media tools", and finishes by itself once the tool is found (D5).
3. Later, one place in Settings lists every tool: Ready / Missing / Optional, with a **Check again** button, so installing FFmpeg does not mean restarting anything.
4. "Enhance your library": one screen per service. A link opens the key page, a box takes the pasted key, **Test** checks it straight away.
5. Folder picker: the path runs across the top, easy to copy, and the picker's icon matches what is being chosen (movies, TV, music and so on).

**What stays the same.** Windows installer and Docker users already get FFmpeg and Tesseract with the app, so they will normally see "all Ready" and nothing else. Books and comics work with no extra tools.

**Acceptance criteria (each is mapped to the work in section 6)**
- **A1.** With FFmpeg missing, first-run setup shows it as missing, says what it affects, and says how to fix it. (WP1, WP2)
- **A2.** With FFmpeg missing, adding a movie, show, song or audiobook never produces a failed file. It shows "Waiting for media tools". (WP3)
- **A3.** After FFmpeg is installed and **Check again** is pressed, waiting files finish without a restart or a re-scan. (WP1, WP3)
- **A4.** One corrupt video with FFmpeg present fails only that file, which goes to Needs Review with a plain reason; every other file keeps moving. (WP0, WP3)
- **A5.** Tesseract missing shows as Optional with the effect "scanned pages and photos of paper won't be searchable". (WP1, WP2)
- **A6.** Settings has one **Tools and requirements** page listing every tool and the Local AI models with their state, matching what setup showed, and for anything missing it says what is blocked and how many files are held. The Local AI page keeps only AI settings. (WP2, WP7)
- **A7.** Each key-needing service connects from a single screen: link, paste, Test. (WP4)
- **A8.** The folder picker's path bar spans the full width above the lists, and the icon follows the media type. (WP5)
- **A9.** The from-source install page lists every tool and how to get it. (WP6)

## 1. Goal

Whoever installs the Library, however they installed it, is told up front which helper tools it needs and which are missing, and a missing tool never turns into a pile of failed files. The setup screens for keys and folders also stop being fiddly.

## 2. Scope

**In scope**
- One list of the tools the Library relies on, each marked Required or Optional with a plain "what breaks without it" line, checked at first run, at every Engine start-up, and on demand from Settings.
- Making video and audio files wait, not fail, when FFmpeg is missing, and recover by themselves.
- Finding out, and fixing, why failures in the file pipeline looked like a stall (bug 21's cause; its display belongs to Group 2).
- The "Enhance your library" key screens, and the folder picker's path bar and icon.
- Install documentation that lists the tools.

**Out of scope**
- Fetching tools from unpinned or unchecked sources. Any download is a fixed address with fingerprints checked (see WP7).
- The ingestion page's look, counts, progress and icons (Group 2).
- Local AI behaviour and settings themselves (planning item I, Local AI review). This spec only moves the model files and their download state onto the new page and uses the same download mechanic; it does not change what the AI does.
- Reworded "Tuvima" copy elsewhere (Group 6). New text here says "Library".
- Folder behaviour (Managed / As-is) and the folder list table.

## 3. Decisions made

- **D1.** A missing tool is a state the Library reports, never a reason to fail someone's file. Reason: Maintenance and Reliability; today a tool outage looks like corrupt media and is easy to mistake for a bug in the library itself.
- **D2.** Privacy first means the Library never shares your data; it does not mean it cannot fetch the tools it needs (Shy, 2026-10-18). Fetching is offered, not automatic (Q5). Reason: Reliability and Privacy.
- **D3.** One catalogue of tools drives setup, Settings, the start-up log and the existing health check, so they can never disagree. Reason: Maintenance.
- **D4.** Whether a tool is Required depends on the media types chosen: FFmpeg is Required when Movies, TV, Music or Audiobooks are in use, and Optional for a books-and-comics-only library. Reason: do not nag people about tools they will never need.
- **D5.** Setup warns about a missing tool and never blocks (Q1, Shy 2026-10-18). FFmpeg is needed for the app's video and audio work; a library with only books or comics does not care. Files that need a missing tool are held back, not failed, until it is fixed (D5). Fixing is offered, not automatic: an alert plus an install or download-again button (Q5), and the page shows exactly what is blocked while a tool is missing.
- **D6.** One **Tools and requirements** page in Settings (Q3, Shy's decision) lists every tool and every on-demand download, including the Local AI models, using one shared download mechanic (same pinned, checked, logged approach). The Local AI page keeps only the AI settings; the model files and their download state move to the new page. Reason: Maintenance, one mechanic and one place to look.
- **D7.** Every download or repair writes entries to the system activity log (started, finished, failed, checksum refused, cancelled).
- **D8.** Detection is automatic first (the Library's own folder, the system path, and each tool's standard install folder). If a tool still isn't found, the Tools and requirements page offers **Locate it** with the file or folder picker, and saves the chosen location as that tool's explicit path in its existing settings (Shy, 2026-10-18). Reason: Ease of use.
- **D9.** The one-screen key dialog is used everywhere a key is added or changed, at setup and later in Settings (Q4).
- **D10.** New wording is plain language: what it affects, then the fix. No file paths or error text in what the person sees; detail stays in the Engine log.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| First run, all tools Ready | Nothing extra: "Getting things ready" finishes as today |
| First run, a Required tool missing | A short list under the heading "A few things to install". Each row: tool name, what it affects, how to fix it for this install (Windows reinstall, Docker image, or from-source steps). A **Check again** button. Continue is allowed (D5) |
| First run, only an Optional tool missing (Tesseract) | One quiet line: "Scanned documents won't be readable until Tesseract is installed." No warning colour |
| Settings, Tools and requirements page | Each tool and each AI model: Ready, Missing or Optional, a one-line effect, **Check again**, and for anything missing the list of what is blocked ("12 videos are waiting for FFmpeg") with **Install for me** or **Download again** and progress while it runs. Same wording as setup. The Local AI page shows only AI settings |
| Adding media while a tool is missing | The file shows "Waiting for media tools" on the ingestion list and in the Recently added page; a notice links to the tools list |
| Tool found after Check again | Waiting files continue on their own; the notice clears |
| Corrupt file, tools Ready | That file only: a plain reason and a place in Needs Review |
| Engine unreachable | Existing "could not check this server" message, unchanged |
| Keys screen | Service logo and name, a **Get your key** link that opens the service's page, one box per credential, **Test** that shows success or the service's plain reason; success saves the key. Phone and desktop the same; editing is desktop-only as today |
| Folder picker | Title bar with an icon for the media type, a full-width path bar, then the drive list and folder list below. Import and other untyped uses keep the generic folder icon |

## 5. Data / model changes

- **Tools list (new, read-only):** produced at run time, not stored. Carried over the existing setup check result plus a new Settings view. The wire shape lives in Contracts and gets fixtures and tests.
- **Waiting files:** a file whose inspection is waiting on a tool needs a durable "inspection pending" marker so it survives a restart. Whether that is a new status on the existing file/job rows or a small new column is to be confirmed by Claude Code; the data-store rules require a numbered upgrade step either way. Pre-release data may be reset.
- Existing libraries: files that failed with the old error before this change are re-queued by the first "Check again" or the next scan (to be confirmed).

## 6. Work packets

### WP0 — Reproduce the failure and confirm the stall
- **What it does:** With FFmpeg absent, add one movie, one song and one book; with FFmpeg present, add one deliberately corrupt video. Record exactly what is left behind for each (file row, identity job, log entry, counters) and whether other files are blocked. Write the findings into the spec's section 7 and add a failing test for each wrong outcome. No product change yet.
- **Files likely touched:** new tests in `tests/MediaEngine.Ingestion.Tests`; findings note. To be confirmed by Claude Code.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a short written finding exists stating, for each case, what ends up stored and whether the queue kept moving, with a test that fails today for each case that should not fail.

### WP1 — Tools catalogue and on-demand check
- **What it does:** One Engine service lists every tool and on-demand download, including the Local AI runtime and models (see section 7 table), each with Required/Optional (by chosen media types), state, plain effect, fix text for the three install kinds, and a **re-check** that re-detects, and that also searches each tool's standard install folder (for Tesseract on Windows, its own program folder). A saved explicit location from **Locate it** takes priority, as the existing settings already define; a saved location that no longer exists reports Missing and says so, rather than silently falling back. FFmpeg is only detected once at start-up today, so re-detect is new work; Tesseract already re-detects every 30 seconds. Logs one summary line at start-up. The existing media-runtime health check reads from this service.
- **Files likely touched:** `src/MediaEngine.Processors/FFmpegService.cs`, `src/MediaEngine.Domain/Contracts/IFFmpegService.cs`, `src/MediaEngine.Api/Services/LocalAssets/Ocr/OcrService.cs`, new service under `src/MediaEngine.Api/Services/`, `src/MediaEngine.Api/Services/SetupPreflightService.cs`, `src/MediaEngine.Api/Services/HealthChecks/LaunchDependencyHealthChecks.cs`, `src/MediaEngine.Contracts/Setup/SetupContracts.cs`, Contracts tests and fixtures (`tools/Update-ContractFixtures.ps1`).
- **Depends on:** none
- **Size:** Standard
- **Done when:** unit tests show each tool reporting Ready, Missing and Problem states; a re-check after a binary appears flips Missing to Ready without restarting; the contract snapshot test passes.

### WP2 — Show it at first run and in Settings
- **What it does:** Adds the "A few things to install" list and Check again to the setup "Getting things ready" step, and a new Tools and requirements page to Settings, including **Locate it**: for a tool that automatic detection cannot find, it opens the folder picker so the person can point at where it lives, then runs Check again. The picker today chooses folders only, so the person picks the folder holding the tool; a file-picking mode is a later option. Fixes the existing gap where the setup result already carries an FFmpeg status but the screen ignores it and the overall pass/fail ignores it too.
- **Files likely touched:** `src/MediaEngine.Web/Components/Setup/SetupPreflightStage.razor`, `SetupReadinessStage.razor`, `SetupPage.razor`, a new Tools and requirements page under `src/MediaEngine.Web/Components/Settings/` (the Local AI page `LocalAiSettingsTab.razor` loses the model files; `ModelsTab.razor` is the likely source of the model list, to be confirmed), `IEngineApiClient.Setup.cs` and its implementation, component CSS. Follow the Dashboard visual checks in `src/MediaEngine.Web/CLAUDE.md`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** with FFmpeg hidden from the Engine, the setup step lists it with its fix and lets setup finish; the Tools and requirements page shows the same row plus what is blocked; Check again clears it after FFmpeg is made available; phone and desktop screenshots reviewed.

### WP3 — Missing tool means waiting, not failing
- **What it does:** `PlaybackInspectionWriter` stops throwing when FFmpeg is unavailable: the file is registered and identified as normal, inspection is marked pending, and a retry runs when the tool becomes available (WP1's re-check, and at start-up). A real probe failure with tools present records the reason on that file and sends it to Needs Review. It also lets the tools page ask "how many files are held waiting for this tool?" so the page can show what is blocked. Also ensures no registered file is left without an identity job after any inspection outcome. Today inspection runs after the file row is inserted and its failure aborts the rest of that file's steps (inferred from `IngestionEngine.Pipeline.cs`; WP0 confirms). The video metadata extractor already degrades quietly when FFmpeg is missing, so the two now behave the same way.
- **Files likely touched:** `src/MediaEngine.Ingestion/Services/PlaybackInspectionWriter.cs`, `src/MediaEngine.Ingestion/IngestionEngine.Pipeline.cs`, `IngestionEngine.ProcessingStage.cs`, `IngestionEngine.Operations.cs`, repository and a numbered upgrade step under `src/MediaEngine.Storage/Schema/Upgrades/` if the marker needs storage, `docs/reference/database-schema.md`.
- **Depends on:** WP0, WP1
- **Size:** Complex
- **Done when:** the tests from WP0 pass; files added with FFmpeg missing show "Waiting for media tools", then complete after the tool appears and Check again is pressed; a corrupt file fails alone.

### WP4 — One-screen key connection
- **What it does:** The connection dialog shows a single screen per service in place of the numbered steps: link, credential boxes, **Test**; success saves. The numbered steps are defined per service in `config/providers/` (comicvine, tmdb, tvdb and subdl have three each today), so the dialog flattens them and no per-service code is needed. The link text and address are taken from those same steps.
- **Files likely touched:** `src/MediaEngine.Web/Components/Shared/Providers/ProviderOnboardingDialog.razor` (+ CSS), `src/MediaEngine.Web/Components/Setup/SetupProvidersStage.razor` if wording changes, `config/providers/{comicvine,tmdb,tvdb,subdl}.json` only if a link is missing, existing Dashboard tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** each of the four services opens one screen, the link opens its key page, a wrong key shows a plain failure, a right key shows success and saves; same behaviour from Settings (Q4).

### WP5 — Folder picker: path across the top, icon by type
- **What it does:** Moves the path bar out of the right-hand panel to a full-width row above the drive list and folder list. Adds an optional media-type input; the picker's icon follows it. The add-folder action already knows the type (it names it in its help text); the Import folder and other untyped uses keep the generic icon.
- **Files likely touched:** `src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor` (+ CSS), callers in `src/MediaEngine.Web/Components/Settings/LibraryFoldersPanel.razor` and `ViewLibrarySettings.razor`, the app's existing per-type icon mapping.
- **Depends on:** none
- **Size:** Standard
- **Done when:** at desktop and narrow widths the path bar spans the dialog and can be selected and copied in one go; opening the picker for Movies, TV, Music, Books, Comics and Audiobooks shows six different icons.

### WP6 — Install pages list every tool
- **What it does:** The from-source page currently covers Tesseract but not FFmpeg. Add both, with what each affects, where to get them, and how to check. Align the Windows and Docker pages and the configuration reference with the new tools list. Update the status and feature inventory pages per the docs rules.
- **Files likely touched:** `docs/install/from-source.md`, `docs/install/windows.md`, `docs/install/docker.md`, `docs/reference/configuration.md`, `docs/product/status.md`, `docs/guides/library-settings.md`.
- **Depends on:** WP1, WP2
- **Size:** Mechanical
- **Done when:** a reader following only the from-source page ends with FFmpeg and Tesseract detected; the docs build passes.

### WP7 — Fetch missing tools for from-source runs and repairs (shape depends on Q5; written for the recommended "Offer")
- **Where it applies (decided with Shy 2026-10-18):** Windows installer and Docker image already bundle FFmpeg and Tesseract with their licences, and stay that way. Containers never download into themselves at run time. So fetching is only for runs from source, for a bundled copy that has gone missing or broken (repair), and, later, for Linux or macOS runs once a pinned build is chosen there.
- **What it does:** For FFmpeg and Tesseract (with its English and page-orientation data), adds an **Install for me** action to the Tools and requirements page. The Local AI models already download on demand; they move onto this page and are brought under the same logging and status wording, reusing their existing download code rather than rebuilding it. It downloads the exact pinned builds that `tools/Download-FFmpeg.ps1` and `tools/Download-Tesseract.ps1` already use, checks each download and each program against the recorded SHA-256 values, and places them in the Library's own data folder (not the app or repository folder). It then re-checks, so waiting files continue (WP3). Under "Offer" (recommended, Shy 2026-10-18) nothing downloads by itself: when a tool is missing or broken the person gets an alert on the tools list and a banner, so they know it broke, and chooses **Install for me** (first time) or **Download again** (repair). The download runs in the background with progress and a Cancel; files wait (WP3) until it finishes. If it fails (offline, blocked), the plain fix instructions remain. Under "Auto" the same download would also start by itself at first run or start-up. Windows first; Linux and macOS have no pinned build today, so they keep the "how to install it yourself" text until one is chosen and licence-checked. The Engine remembers a decline and does not ask again. Every download writes entries to the system activity log (the log already used for events such as media failures; to be confirmed by Claude Code): started (tool, version, source), finished, failed (with the plain reason), checksum refused, and cancelled. Note for Claude Code: FFmpeg's Windows download is a plain .zip, which the app's own platform can unpack without any extra tool. Tesseract's pinned Windows download is a setup .exe, not a zip. Decided with Shy (2026-10-18): the Library downloads that setup program (checksum verified, logged), then asks the person to run it, with a clear prompt such as "Tesseract's installer is ready. Run it to finish." and a button that launches it. It does not unpack it and no 7-Zip is needed. Consequences for this packet: Windows will ask for administrator approval; the installer puts Tesseract in its own standard program folder and may not add it to the system path, so detection (WP1) must also look in that standard folder, and **Check again** (or the installer finishing) must pick it up; the English and page-orientation language data must be selected or copied the way the existing seeding step expects (to be confirmed by Claude Code). FFmpeg stays a silent zip download with no prompt.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/AiEndpoints.cs` and the model inventory code (to be reused, not rewritten), `LocalAiSettingsTab.razor`/`ModelsTab.razor`; new installer service under `src/MediaEngine.Api/Services/`, `src/MediaEngine.Processors/FFmpegService.cs` and `src/MediaEngine.Api/Services/LocalAssets/Ocr/OcrService.cs` (add the data-folder location to its search order, after the explicit path and before PATH), config for the pinned download address and hashes under `config/`, the tools list UI from WP2, tests with a fake download source, `docs/reference/configuration.md`, `docs/explanation/privacy-local-first.md`, `THIRD-PARTY-NOTICES.md` if needed.
- **Depends on:** WP1, WP2
- **Size:** Complex
- **Done when:** with a fake download source, a correct archive installs and flips the tool to Ready; a wrong checksum installs nothing and says so plainly; a refused or failed download leaves files waiting, not failed; each download, success or failure, appears as an entry in the system activity log; the privacy page lists the downloads and states that no library data is sent.

**Waves (at most two at once):** Wave 1: WP0 and WP1. Wave 2: WP2 and WP3. Wave 3: WP4 and WP5. Wave 4: WP6 (and WP7 if chosen), then review against this spec.

## 7. Risks and open questions

**Findings (confirmed in the code unless marked inferred)**

| Tool | Required? | What breaks without it | Where it comes from | Checked today? |
|---|---|---|---|---|
| FFmpeg and FFprobe (one set; the app needs both) | Required for Movies, TV, Music, Audiobooks | Every video and audio file (.mkv, .mp4, .mp3, .m4b, .flac and more) fails inspection, so nothing is identified or playable. Also no thumbnails, no adaptive streaming, no casting, no subtitle and audio syncing | Windows installer bundles it; Docker image installs it; a run from source has nothing unless it was added by hand (the developer script `tools/Download-FFmpeg.ps1` exists) | Engine logs one line at start-up; the setup check carries a status the screen ignores; a health check reports Degraded (Unhealthy only in Docker) |
| FFmpeg features: HLS, H.264, AAC | Required for adaptive streaming | Adaptive playback unavailable | Part of the FFmpeg build above | Health check only |
| Tesseract with English data | Optional (fetched with the other tools under Q5) | Scanned PDFs and photos of paper cannot be text-searched. Documents that already contain text keep working | Installer and Docker bundle it; source needs the developer script (needs 7-Zip) or a package manager install | Settings > Documents says "not installed"; not in setup |
| SkiaSharp image engine | Required | Cover and thumbnail creation fails | Ships inside the app on every install | Health check |
| Local AI runtime | Optional | Local AI features off | Native library shipped with the app | Health check (Degraded) |
| AI models | Optional | Dependent AI features wait | Downloaded on request, never bundled by rule | Health check; belongs to the Local AI review |
| .NET 10 | Required | App does not run | Bundled in Windows and Docker installs; source needs SDK 10 | Not checkable from inside the app |
| Writable data folders, data store, free space | Required | Setup blocked | Created by the app | Yes, setup check |
| Provider keys (TMDB, TVDB, ComicVine; SubDL optional) | Optional | Less artwork and metadata, no external subtitles | You enter them | Settings and setup; not part of this list |
| Internet access to metadata services | Needed for identification | Items wait and retry | Your network | Provider health monitor; not part of setup |

- **Why one file looked like a stall (bug 21).** Each file is handled on its own, so one failure does not block the others, which is how the pipeline is built. But a failed file leaves its file row registered with no further steps and increments the "failed" and "done" counts. When every file is a video, everything fails in turn and the page ends up at "0 items being processed" with nothing identified. This is inferred from the code; WP0 confirms it.
- **Risk:** hiding a missing tool as "waiting" could leave a user wondering why titles are not appearing. Mitigation: the setup note, the "Waiting for media tools" label and the Settings list all say the same thing.
- **Note:** the Windows installer and the Docker image already bundle both tools, with their licences shipped (Apache 2.0 for Tesseract, GPLv3 for FFmpeg, both recorded in the project's attributions). Q5 therefore mostly affects people running from source.
- **Risk:** Tesseract on Windows is installed by running its setup program, which needs administrator approval and puts the tool outside the Library's folders; detection must look there, and the person must finish the installer for the tool to become Ready. Until then the page shows it as downloaded and waiting for install.
- **Risk:** re-detecting FFmpeg while the Engine is running must not disturb jobs already using it. WP1 tests this.

**Open questions:** none. All five were answered by Shy on 2026-10-18 and are recorded as decisions D5 to D9.
- **Q1.** *Answered by Shy (2026-10-18): warn, never block; hold back files that need a missing tool. See D5.*
- **Q2.** *Answered by Shy: hold the files back until the tool is installed (D5).*
- **Q3.** *Answered by Shy: a new Tools and requirements page that also covers the Local AI models; the Local AI page keeps only AI settings (D6).*
- **Q5.** *Answered by Shy: Offer, and show what is blocked while a tool is not downloaded (D5).*
- **Q4.** *Answered by Shy (2026-10-18): the same one-screen dialog is used when adding or changing a key later in Settings (D9).*

**On-demand download of missing tools: is it a good idea? (Shy's question, 2026-10-18; suggestions, not a decision)**

Yes, it is a good idea, and Shy has clarified that privacy first means the Library never shares your data, not that it cannot download what it needs. The Library already fetches AI models and extra OCR languages on request, so there is precedent. Fetching FFmpeg removes the most common first-run failure for people running from source or on a NAS. What still matters:

| Concern | What it means |
|---|---|
| Trust and safety | A program fetched and then run needs a fixed address and checked fingerprints so a tampered file is refused. The existing script already pins both. Without that it is a supply-chain risk |
| Reliability | Offline machines, firewalls and NAS will fail the download. Containers should not download into themselves at run time (their image already includes the tools). So the check-and-tell instructions must always remain as the fallback |
| Size and time | FFmpeg is a large file. It downloads in the background with progress and a Cancel, never blocking the app |
| Permissions | The service account may not be allowed to write to the app folder, so the tools go in the Library's own data folder |
| Transparency | The privacy page lists the download and says nothing about your library is sent. One neutral request to the download host reveals only that someone fetched the file |
| Licence | FFmpeg's pinned build is GPLv3 and already recorded in the project's attributions, compatible with AGPLv3. A new Linux or macOS build needs the same licence check (project rule) before use |
| Platforms | Only Windows has a pinned build today; Linux and macOS keep the how-to text until one is chosen |

Options: **None** (check and tell only), **Offer** (an Install for me button, nothing automatic), **Auto** (download in the background at first run or start-up when a Required tool is missing). Recommendation: **Offer**. Since the installer and Docker image already bundle the tools, a missing tool usually means something is broken; the person should hear about it, then choose to install or download again. The how-to text stays as the fallback.

## 8. Gaps and drift

Not applicable (new spec).

---

**Plain English summary.** The test run failed because the Library quietly needed a video tool (FFmpeg) that was not on the computer, and it reacted by marking every video and audio file as broken instead of telling anyone. This plan makes the Library check its helper tools on first run and every start, explain in everyday words what is missing and how to fix it, and keep your files waiting rather than failing until the tool shows up. If a tool is missing or broken, the Library alerts you and offers a one-click install or re-download from fixed, checked sources, so nothing happens behind your back. It also turns the numbered key-entry steps into one simple screen per service, and tidies the folder picker so the path is easy to copy and the icon matches the media type. Windows and Docker installs already include the tools, so most people will see no change at all.
