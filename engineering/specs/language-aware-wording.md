# Language-aware app wording

- **Status:** Built, awaiting Local checks
- **Version:** 1
- **Last updated:** 2026-10-31
- **Related docs:** engineering/specs/plugin-architecture.md (C11 Languages), engineering/specs/users-profiles-households.md, engineering/specs/settings-and-top-navigation.md, docs/product/presentation-rules.md

## Goal
Words in Library follow the household's language and regional spelling: a US server shows "Favorites", a UK server "Favourites", a French server "Favoris". Each idea has one name, used the same on every screen. Each person can still choose the languages they want for subtitles, lyrics and audio.

## What exists today (checked 2026-10-31)
- Dashboard translation plumbing exists: a string table (`src/MediaEngine.Web/Resources/SharedStrings.resx`, about 499 entries) with French, German and Spanish copies, and a list of 31 supported languages in `Program.cs`.
- Only 14 Razor files use it (`IStringLocalizer<SharedStrings>`); most of the roughly 590 others type English directly. The word "Favorite" is typed in 107 files and is not in the string table.
- Display language, metadata language and country are server-wide settings (`ServerGeneralSettingsDto`: `display_language`, `metadata_language`, `country`).
- The culture sync in `MainLayout.razor` compares only the two-letter language, so "en-GB" would never apply.
- Providers already declare `language_strategy` (`localized` for TMDB, Apple, SubDL; `source` for LRCLib, ComicVine).

## Scope
In: moving all Dashboard wording into the string table; server-level display language plus regional spelling (US, UK, Canada, Australia English first); a shared glossary of product words; per-profile media language options; plugin language support; a guardrail test; translations added in batches.
Out: right-to-left layout (separate spec), translating Engine log text, the native clients (they reuse the string table later), per-profile app language (can be added later without redoing this).

## Decisions made
1. **Server-level language (Shy, 2026-10-31).** Display language, regional spelling and metadata language are household settings: people sharing a server share a language. Overrides the earlier per-profile idea.
2. **Profile options are about media only.** Each profile sets: preferred subtitle languages (ordered), preferred lyrics language, preferred audio language, and when to show subtitles (Off, Automatic, Always, Only when the audio is a foreign language). Modelled on Plex and Jellyfin. The "Not right? Find another" panel still lets anyone pick a different language for one item.
3. **Full translation is the goal.** Step one moves every English word into the string table (needed for any language). Translations then follow in batches; French, German and Spanish are already started.
4. **First-time default.** On first setup the server guesses from the browser's language and the administrator can change it; English with an unknown region becomes US English.
5. **Metadata language** stays one household setting (one stored copy of each title). Providers keep their `language_strategy`. Wikipedia descriptions must follow the server metadata language; if they do not today, that is a fix inside packet 4.
6. **Plugins** ship their own string files for their screens and messages, declare supported languages (plugin-architecture C11), and follow the server display language; lyrics and subtitles follow the profile options above.
7. **"My List" versus "Favorites"** wording is decided by the music thread; once the glossary exists it is a one-line change.

## Standards followed
.NET's built-in localization (`IStringLocalizer` + `.resx`), already in use. English is the master copy; stable keys; whole sentences with placeholders (no joined fragments); plural forms as separate keys; dates, numbers and sorting by culture; fallback chain `en-GB` to `en`, `fr-CA` to `fr` to `en`; text may grow 30 to 40 percent so layouts must flex; machine-translated first draft with human review per language; a test flags missing and unused keys. No new tool or library is needed (a plural helper, if wanted later, needs a licence check first).

## UI behaviour
- Settings > General: "Language" and "Region" (region shown only when the language has variants). Changes apply immediately; no toast and no reload notice.
- Profile > Preferences: a "Languages" group with subtitle languages, lyrics language, audio language and the subtitle rule.
- Missing translation: show the English word, never a key or blank.
- Phone, tablet, desktop, TV: same words; long languages (German) must not break pills or buttons.
- Sign-in and first-run screens use the server language.
- Empty, loading and error states use the same string table.

## Data/model changes
- Server settings: `display_language` becomes a full tag (`en-US`, `en-GB`, `fr-CA`), default from the browser at first setup, else `en-US`.
- Profile preferences: `subtitle_languages` (ordered list), `lyrics_language`, `audio_language`, `subtitle_mode`. Contracts DTOs, fixtures and tests updated together.
- No data-store migration (pre-release data may be reset).

## Work packets
1. **Full-tag culture and region (Standard).** Files: `Program.cs`, `MainLayout.razor`, `ServerGeneralTab`, Contracts settings DTO. Depends on: none. Done when a UK server shows a test word as "Favourites" and a US server as "Favorites".
2. **Glossary and en-GB overrides (Standard).** `SharedStrings.resx`, new `SharedStrings.en-GB.resx`. Depends on 1. Done when Favorites/Favourites switches on Listen.
3. **Move navigation, pills and shared components to the string table (Standard).** `Components/Navigation`, `Components/Shared`. Depends on 2. Done when none of these files contain typed-in user-facing words.
4. **Move Listen, View, Watch, Read, Home (Complex; two builders).** Depends on 3. Also checks the Wikipedia description language path. Done when guardrail count for these areas is zero.
5. **Move Settings, Users, Plugins, setup screens (Standard).** Depends on 3.
6. **Profile media language options (Standard).** Profile Preferences tab, Contracts, Engine lookups for lyrics and subtitles use the options. Depends on 1.
7. **Plugin string support (Standard).** `MediaEngine.Plugins`, first-party plugins. Depends on 2.
8. **Guardrail test and docs (Standard).** A `*GuardrailTests.cs` suite that counts typed-in words and may only go down; missing/unused key check; `presentation-rules.md` "Wording and language" section; docs repo profile and settings guides. Depends on 4 and 5.
9. **Translations in batches (Mechanical per language).** French, German, Spanish first; human review noted per language. Depends on 4 and 5.
Packets 4 and 5 may run side by side (different files).

## Local checks
- Run a US and a UK server and compare Listen and Home wording.
- Run a German server and look for clipped pills and buttons on phone and desktop.
- Set a profile to Japanese audio with English subtitles and check an anime title.
- Confirm sign-in and first-run follow the server language.

## Risks and open questions
- Maintenance: every future screen must use keys; the guardrail enforces it.
- Performance: negligible; strings are held in memory.
- Size: most screens are touched once; waves keep reviews small.
- Open: none.

## Plain English summary
The household picks one language and spelling for the whole app, so "Favorites" becomes "Favourites" for UK households and the app can be fully translated over time. Each person only chooses the languages they want for subtitles, lyrics and audio. All wording moves into one shared list so each idea has one name everywhere, and a safety test stops new screens from skipping it.
