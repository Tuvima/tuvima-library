# Spec: Smoother first-run setup (sign-in hand-off, tools summary, quiet AI runtime)

> **Status:** Built, awaiting Local checks · **Version:** 2 · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../product/overview.md) · [First run and prerequisites (archived)](archive/first-run-prerequisites.md) · [Users, profiles and households](users-profiles-households.md) · [Simpler library setup](library-setup-groups.md)

> Written 2026-10-27 from Shy's local-review bugs F1, F2 and F5 (group 3, "First-run setup flow"). Every cause below was read in the code on main (commit 44281287); nothing was run on Shy's PC, so where a cause is inferred it says so. Nothing is built. F4 and F6 (the setup wizard's Libraries step) belong to the library-and-folder group and are not touched here.

## Product walkthrough

**What goes wrong today.**
1. *(F2)* Shy creates the first administrator without a password. Instead of the next setup step, the browser lands on "Who's using Library?" showing one person. After clicking that person, setup does carry on, but it feels like setup was abandoned.
2. *(F5)* On the last setup screen, Tesseract (the tool that reads scanned pages) shows a note saying scanned documents "won't be readable until Tesseract is installed", then a line saying "Downloaded. After setup, open Settings, Tools and requirements and choose Run installer." Both are true, but together they read as a contradiction. Separately, FFmpeg is never shown when it is fine, so there is no way to tell it was checked at all.
3. *(F1)* When the Engine starts on a machine without the optional local AI engine, its log gets a long error (a "file not found" with a stack trace). Nothing is broken and the Engine runs. But it looks like a fault to anyone reading the log, and the same missing AI engine appears in the setup wizard as an optional box with a "Download Local AI engine" button that cannot work (Library has no download for it; it is installed by a script).

**What it feels like after.**
1. Creating the administrator ends with the next setup step (media locations) opening straight away. No sign-in page flash, no "who's using Library" screen, in the normal case of one person with no PIN.
2. The last setup screen shows a short "Your media tools" list: FFmpeg, Tesseract, ExifTool and the image engine, each with a green tick for ready, a progress bar while downloading, or one plain sentence for what to do. Tesseract says exactly one thing at a time ("Downloading", "Downloaded, one more step: run its installer" with a button, or "Ready").
3. Local AI never appears in setup. In the Engine log, a machine without it gets one calm information line, not an error with a stack trace. Settings, Tools and requirements still tells the whole story for people who want it.

**What stays the same.** Passwords, PINs, recovery codes and the rule that signing in with an email lands on the profile choice (Users, profiles and households D3) do not change for anyone after setup. Optional tools still never block setup. Local AI models are still chosen in Settings after setup.

**Acceptance criteria**
- **A1.** After "Create administrator" on a home-network start without a password, the next screen is the setup step after Administrator. (WP1)
- **A2.** After creating an administrator with a password and PIN, and saving recovery codes, setup also continues without the profile choice when the household has one profile. (WP1)
- **A3.** A later sign-in, by anyone, still shows the profile choice exactly as before. (WP1, guardrail test)
- **A4.** The last setup screen lists every tool with its state, including ready ones. (WP2)
- **A5.** Tesseract never shows two conflicting messages; its text depends only on its state (missing, downloading, downloaded and waiting for its installer, ready, problem). (WP2)
- **A6.** "Local AI engine" is absent from the setup screens. (WP2)
- **A7.** Starting the Engine without the local AI runtime writes no error-level or stack-trace line; it writes one information line saying Local AI is off and why. (WP3)

## 1. Goal

First-run setup should flow from one step to the next without surprise screens, and the tools summary at the end should say plainly what is ready, what is downloading and what needs one more action. Start-up logs should not look like faults when an optional feature is simply not installed.

## 2. Scope

**In scope**
- F2: where setup sends the browser after creating the first administrator.
- F5: how the last setup screen reports FFmpeg, Tesseract, ExifTool and the image engine, and keeping Local AI out of it.
- F1: how the Engine reports a missing optional AI runtime at start-up.

**Out of scope**
- The setup wizard's Libraries step (F4, F6; library-and-folder group).
- Profile picker look (F3 is the "one look" group) and the profile choice rule after setup (Users, profiles and households D3).
- Making Library able to download the local AI engine (answered under D3: later, as its own spec).
- Settings, Tools and requirements page redesign.

## 3. Decisions made

Shy answered Q1 to Q3 with "agree on all" (2026-10-27 06:04Z), taking each recommendation. Shy locked the spec on 2026-10-27 ("lock it").

- **D1 (Shy, 2026-10-27).** Setup's own sign-in skips the profile choice, because setup just created the only profile. Only the session started by setup, and only while the household has exactly one profile. Reason: Reliability and ease of use; it is the least invasive way to honour D3 everywhere else. Q1 answered A.
- **D2 (Shy, 2026-10-27).** The setup tools summary shows every tool, ready or not, and its text comes from the tool's state alone. Reason: Maintenance (one wording source) and trust (Shy can see FFmpeg was checked).
- **D3 (Shy, 2026-10-27).** Local AI is shown only in Settings, and stays script-only for now (Q3 answered A; a separate spec can add an Install button later). Reason: setup already says AI models are chosen later; the engine row is the same story.
- **D5 (Shy, 2026-10-27).** Once Tesseract is downloaded, the last setup screen shows a Run installer button (Q2 answered A). If the cloud cannot prove it works from that screen, fall back to a clearer Settings step and record it in Local checks.
- **D4 (Shy, 2026-10-27).** A missing optional runtime is an information event in the Engine log, once, with no exception attached. A broken install (files present but cannot load) stays a warning with detail. Reason: Reliability; logs should only alarm when something is actually wrong.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal (no-password start, desktop) | Create administrator, then the Media locations step. No screen between. |
| Normal (password start) | Create administrator, recovery codes shown, "I saved the codes, continue", then Media locations. |
| Household has more than one profile at setup | Not possible at first run; if it ever happens the profile choice still shows. |
| Tools summary, all ready | A compact list of tools with green ticks and versions, under the existing checks. |
| Tools summary, FFmpeg downloading | Info bubble with a progress percentage; finishes in the background. |
| Tesseract downloaded, installer not yet run | One bubble: "Tesseract is downloaded. One more step: run its installer", with a Run installer button, or "after setup, in Settings" if the button cannot work here. |
| Tesseract missing | One bubble: what it affects, a Download button, "or later in Settings". |
| Engine unreachable or tools check failed | Existing "could not finish checking" message; unchanged. |
| Phone, tablet, TV | Setup is desktop-and-browser; the list stacks to one column like the existing rows. |

## 5. Data / model changes

None. No stored data changes; pre-release, no reset needed.

## 6. Work packets

### WP1: Setup continues after creating the administrator (F2)
- **Root cause (read in code):** After the administrator is created with no recovery codes, `SetupPage.razor` calls `SignIn()`, which navigates to `/auth/login?returnUrl=/setup`. The login handler (`DashboardAuthenticationEndpoints.cs`) signs a passwordless account straight in, then `FinishSignInAsync` redirects to the profile picker (`/who?returnUrl=/setup`) because every interactive sign-in is marked "choose a profile" (`FirstPartyIdentityService.IssueSignInSessionAsync`, by design per Users, profiles and households D3, even for a household of one). `/setup` is only reached after one more click. So the cause is the global D3 rule meeting setup, not a broken redirect.
- **What it does:** When the sign-in is the one setup itself starts (return address is the setup page, one profile in the household, no PIN needed to enter it), do not mark the session "choose a profile". Password path with recovery codes continues to the same place after "I saved the codes". Leave D3 unchanged for every other sign-in.
- **Files likely touched:** `src/MediaEngine.Identity/FirstPartyIdentityService.cs`, `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs`, `src/MediaEngine.Web/Components/Pages/SetupPage.razor`, `tests/MediaEngine.Identity.Tests`, `tests/MediaEngine.Web.Tests` (to be confirmed).
- **Depends on:** none (Q1 answered, D1).
- **Size:** Standard.
- **Done when:** a test creates the first administrator (both sign-in modes) and the resulting redirect is `/setup`; a second test proves a normal sign-in of the same account still goes to `/who`.

### WP2: One honest tools summary (F5)
- **Root cause (read in code):** `SetupReadinessStage.razor` lists only tools that are not ready, so a ready FFmpeg shows nothing and Shy cannot tell it was checked. Tesseract's box always uses one fixed note ("won't be readable until Tesseract is installed", `TesseractNote`) whatever the state, then adds a state-specific line. In the "installer ready" state that produces "won't be readable until installed" next to "Downloaded", which reads as a contradiction. The same filter (optional, not ready, kind "tool") also lets the Local AI engine row in, with a Download button the Engine cannot honour (`ToolRequirementsService` marks it not installable), so pressing it only shows a failure message.
- **What it does:** Replace the two lists with one "Your media tools" list built from every tool except Local AI and AI models. Each row: icon, name, version when ready, and one state line from a single place (a small state-to-sentence table in `SetupToolRules.cs`). Remove Local AI from setup.
- **Files likely touched:** `src/MediaEngine.Web/Components/Setup/SetupReadinessStage.razor` (+ `.razor.css`), `SetupToolRules.cs`, `tests/MediaEngine.Web.Tests` (bUnit) (to be confirmed).
- **Depends on:** none (Q2 answered, D5).
- **Size:** Standard.
- **Done when:** bUnit tests render each Tesseract state and assert exactly one message; FFmpeg ready renders a tick row; no "Local AI engine" text appears at any state.

### WP3: Calm start-up log for a missing optional AI runtime (F1)
- **Root cause (read in code):** At start-up `ToolRequirementsStartupService` runs the tools check, which asks `AiRuntimeStatus` whether the local AI engine loads. `SharedAiRuntime.VerifyManifest` throws `FileNotFoundException("Shared AI runtime is not installed…")` when its manifest is missing, and `AiRuntimeStatus.Probe` catches every failure and logs it at warning level with the full exception. Local builds use the shared runtime folder (empty setting means `%LocalAppData%\Tuvima\AI Runtimes`); that folder only exists after running `tools/Install-AiRuntime.ps1`. Release installers do not bundle it (`docs/install/windows.md`). So on Shy's machine the optional engine is simply not installed, and the probe reports the normal "not installed" case as a fault. The Engine itself is unaffected, as Shy observed.
- **What it does:** Separate "not installed" (manifest missing) from "installed but cannot load". Not installed: one information line, no exception. Cannot load: keep the warning with detail. The tools check result and the Settings row are unchanged.
- **Files likely touched:** `src/MediaEngine.AI/Infrastructure/AiRuntimeStatus.cs`, `SharedAiRuntime.cs`, `tests/MediaEngine.AI.Tests` (to be confirmed).
- **Depends on:** none.
- **Size:** Standard.
- **Done when:** a test with an empty runtime folder produces no warning-level log entry and `TextRuntimeLoads` is false; a test with a corrupt manifest still logs a warning.

## 7. Local checks

- **L1.** On SHY-DESKTOP, on a fresh data folder and a small test library (never the real one): start setup, choose "Start without a password", create the administrator. Expect the Media locations step next, with no profile choice. Failure: the profile choice or the sign-in page appears.
- **L2.** Repeat with "Set a password now" and a PIN. Expect recovery codes, then "continue" lands on Media locations.
- **L3.** After setup, sign out and back in. Expect the profile choice as today.
- **L4.** On the last setup screen with FFmpeg already installed and Tesseract not: FFmpeg shows a tick with its version; Tesseract shows one message and one action; no Local AI box.
- **L5.** Start the Engine without the AI runtime folder. Expect no error stack in the log, one information line about Local AI.

## 8. Risks and open questions

- **Risks:** WP1 touches sign-in code, so a mistake could skip the profile choice for real households (mitigated by the A3 test and by limiting it to setup's own session). WP2 relies on the Windows Tesseract installer flow, which the cloud cannot run (L4).
- **Open questions:** none. Q1 (A, setup's own sign-in only), Q2 (A, Run installer button) and Q3 (A, local AI stays script-only) are answered and recorded as D1, D5 and D3.

## Plain English summary

Three small things make Library's first start feel rough. After you create the first admin, it shows a "who's using Library" screen with only you on it; the plan is to skip that screen just for setup, and leave the normal sign-in alone. On the last setup screen, the scanned-document reader (Tesseract) gives two messages that look like they disagree, and the video tool (FFmpeg) is not shown at all when it is fine; the plan is one clear list of every tool with a tick, a progress bar or a single next step. Finally, the log shows a scary error when the optional local AI engine is simply not installed; the plan is a calm one-line note instead, and to keep AI out of setup entirely. Nothing in your data changes, and your three answers are recorded, so it is ready for you to lock.
