# Spec: Profile Overview, Taste and Activity redesign

> **Status:** Built · **Version:** 1 · **Last updated:** 2026-10-29 · **Related docs:** [Product overview](../../product/overview.md) · [Users, profiles and households](../users-profiles-households.md) · [Settings flat menu](../settings-flat-menu.md) · [Presentation rules](../../../docs/product/presentation-rules.md)

## 1. Goal

The three pages under Profile (Overview, Taste, Activity) look like a person's own corner of Library: a banner, a few friendly numbers, what they are in the middle of, what they did lately and what they like. Everything is worked out on the person's machine from their own history, with no AI needed; plugins can add more power later.

## 2. Scope

**In scope**
- Overview: banner, stat strip, Continue enjoying, Recent activity, Your taste card, a rolling 30-day section with a calendar heatmap.
- Taste: genre donut, "You love ..." sentence, key tastes as removable pills, format mix, "How your taste shifts" over months, "Based on N finished titles".
- Activity: restyle to match the Overview timeline, keeping every feature it has today.
- Kids reach the same three pages for their own profile (Taste read-only).

**Out of scope**
- The Settings menu itself (flat-menu spec), sign-in details (Security & Devices).
- Star ratings, streaks, Discovery dial, "Your mood lately", More/Less like this (these wait for a plugin or a later decision; see D6 to D8).
- Managing other people's profiles (the Profile page already has its own flow).

## 3. Decisions made

- **D1.** The page is called Profile; Overview, Activity and Taste are its tabs at their current addresses (/settings/overview, /settings/overview/activity, /settings/overview/taste). Shy, 2026-10-29.
- **D2.** Nothing shown is invented. A tile or section with no data is left out or shows a short friendly empty line; sample copy from the mock (streak, mood) is not built.
- **D3.** The stat strip is: In progress, Finished (last 30 days), Hours enjoyed, Days active (last 30 days). "Last 30 days" is a rolling window ending today, so it is always a moving figure. The daily history behind it is kept so year-long graphs can come later. Shy, 2026-10-29.
- **D4.** No streak. Shy, 2026-10-29.
- **D5.** Taste ships the no-AI parts now: genre donut, sentence from the top three genres, key-taste pills (remove and add as today), format mix, and "How your taste shifts" (finished titles by month). Shy, 2026-10-29.
- **D6.** No star ratings in this work.
- **D7.** No Discovery dial until something can use it.
- **D8.** More like this, Less like this and Your mood lately appear only when an AI taste plugin is on.
- **D9.** Activity keeps search, filters, grouping, date rail, CSV export and scopes; only its look changes.
- **D10.** Kids see their own Overview and Taste (Taste read-only) and their own Activity only.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | Overview: banner (avatar, name, tagline, joined date, Edit profile, Apps button), stat strip, Continue enjoying cards for Reading, Watching, Listening, Recent activity timeline (Today / Yesterday headings, cover art, "..." menu), Your taste card, last-30-days tiles and calendar heatmap. Taste: donut, sentence, pills, format mix, monthly chart. Activity: same timeline style with the existing toolbar. |
| Empty | New profile: stat tiles show 0 and a one-line invitation; Taste says it appears after a few finished titles; heatmap is hidden. |
| Loading | Skeleton blocks in the same layout. |
| Error | "Couldn't be loaded" with Retry, as today. |
| Partly ready | Taste learned values still being worked out: counts and format mix show, the sentence and monthly chart wait. |
| Phone | Single column; stat strip scrolls sideways or wraps two by two; Edit profile is full width. |
| Kids | Same pages, no Edit controls on Taste. |
| Editing | Editing remains desktop-only. |

## 5. Data / model changes

- Overview needs a daily activity history and a rolling 30-day Finished and Days active count; the Engine already counts activity per day, so this extends it. To be confirmed in WP1.
- Taste needs finished titles grouped by month and genre. Existing taste data (genres, moods, eras, media mix, summary) is unchanged.
- Pre-release: a reset is acceptable if a data step is needed.

## 6. Work packets

### WP1 — Engine figures
- **What it does:** adds rolling-30-day Finished and Days active, the daily history for the heatmap, and finished-by-month-and-genre to the Engine's profile and taste answers, with Contracts tests and fixtures.
- **Files likely touched:** `src/MediaEngine.Contracts/`, `src/MediaEngine.Api/Services/ReadServices/ProfileOverviewReadService.cs`, `src/MediaEngine.Api/Services/Taste/`, tests (to be confirmed).
- **Depends on:** none · **Size:** Standard
- **Done when:** tests show the counts for a seeded history, including an empty one.

### WP2 — Overview page
- **What it does:** new Overview layout per section 4.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/Overview/`.
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** bUnit tests for normal, empty and error states; phone-width check.

### WP3 — Taste page
- **What it does:** new Taste layout per D5 and D8, keeping remove/add and the admin Scope filter.
- **Files likely touched:** `Settings/Overview/OverviewTastePage.razor`.
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** tests for pills, read-only for Kids, and plugin-off hides the AI controls.

### WP4 — Activity restyle
- **What it does:** timeline look from WP2, all current features kept.
- **Files likely touched:** `Settings/Activity/ActivityPage.razor`, `ActivityRow.razor`.
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** the existing Activity tests still pass and rows show cover art and a "..." menu.

### WP5 — Kids access and docs
- **What it does:** Kids reach their own pages; docs and presentation rules updated.
- **Depends on:** WP2, WP3 · **Size:** Standard
- **Done when:** a Kids profile opens Overview and Taste, with no editing.

## 7. Local checks

- **L1.** On a profile with real history, Overview shows believable numbers and a heatmap that matches what was played.
- **L2.** Taste's sentence and monthly chart look right on the real library.
- **L3.** Look and feel against Shy's target screens at desktop and phone width; light and dark.
- **L4.** A Kids profile can open Profile and cannot edit Taste.

## 8. Risks and open questions

- **Risks:** large histories could make the monthly chart slow (Performance); keep it a summarised read.
- **Open questions:** none. The Settings menu naming is owned by the flat-menu spec.

**Plain English summary:** Profile gets three nicer pages built from what a person actually watched, read and listened to, with nothing made up. Streaks, stars and the Discovery dial are left out. Mood and "more or less like this" wait for an AI plugin.
