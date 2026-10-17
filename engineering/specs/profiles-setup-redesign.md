# Spec: Profiles and setup (as built)

> **Status:** Built · **Version:** 2 · **Last updated:** 2026-10-17 · **Related docs:** [First-run ingestion fixes](first-run-ingestion-fixes.md) · [Product overview](../product/overview.md) · [0007 — Accounts own access; profiles own experience](../architecture/decisions/0007-accounts-and-profiles-access.md)
> _Version 2 replaces the 2026-10-10 Draft (a plan written before the spec template existed). Nearly everything that Draft proposed was built by the Netflix-style profiles work (PRs 56 to 66) and the Settings and Profiles work (PRs 108 to 161). This version records what exists and the decisions Shy made on 2026-10-17. Nothing is left to build._

## 1. Goal

Choosing who is using Tuvima, adding or changing a person, and securing an account should feel like a streaming service on a phone, laptop or TV: big friendly faces, a few taps, no settings form. First-run setup stays short and never asks for an email the person does not need to give.

## 2. Scope

**In scope (all built)**
- The "Who's watching?" screen, PIN number pad, Manage profiles mode, add and edit profile sheet, avatar choices, quick switch from the top bar, Kids experience and per-profile content limit.
- Settings pages for profiles (Settings > Profiles and a detail page per person), and the Security page layout.
- Setup account step (name only on the "this computer" path) and the "Secure account" prompt.

**Out of scope**
- New sign-in methods, new account types, changes to household or admin permissions.
- A profile or avatar step inside first-run setup.

## 3. Decisions made

- **D1. "Who's watching?" appears after sign-in whenever the household has more than one profile.** With one profile it is skipped. A device can be set to "Always open as this person", which never skips a PIN. Showing it only on shared or TV devices was considered and dropped. (Shy, 2026-10-17)
- **D2. Avatars are 12 colour swatches, built-in icons, or an uploaded photo.** Typed colour codes are gone. (Shy, 2026-10-10; confirmed 2026-10-17)
- **D3. Kids profiles ship in v1** with a simpler shell and a "Kids" badge. The content limit is a separate per-profile setting chosen by a household admin: Everything, G, PG, PG-13 or R. A new Kids profile defaults to PG, every other profile to Everything. (Shy, 2026-10-10; confirmed 2026-10-17)
- **D4. The "this computer" setup path asks for a name only.** Email is asked for when the account is secured for use away from home, when an invitation is sent, or when an outside sign-in is linked. (Settings and Profiles D1, 2026-10-12; confirmed 2026-10-17)
- **D5. "Secure account" can be dismissed per account and then lives in two places:** a card on Settings > Security and a small attention dot on the account menu (with a "Secure account" item) until it is done. No permanent banner. (Shy, 2026-10-17)
- **D6. A household has at most 8 profiles.** At 8 the Add profile tile disappears. The old "N of 8 profiles granted" wording belonged to the retired Settings > Users drawer and is not used. (Shy, 2026-10-17)
- **D7. Setup stays at six steps** (Name your server, Your account, Libraries, Language, Enhance your library, All set). The earlier goal of five screens or fewer with a profile and avatar step is dropped. People choose an avatar from the profile editor. (Shy, 2026-10-17)
- **D8. Managing profiles lives in Settings > Profiles, with a detail page per person,** and in the picker's Manage mode. The Draft's "Settings > Profile" location is replaced. Only adults and admins see Manage. (Shy, 2026-10-17)
- **D9. Only admins create accounts.** Two admin levels; the "My household" tab and Settings > Users people drawer are retired. (Settings and Profiles D7, 2026-10-12)

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal, several profiles | After sign-in, a centred grid of avatars with names, a lock badge on PIN profiles, a "Kids" badge on Kids profiles, and an Add profile tile while under 8. Tapping a PIN profile opens a number pad. |
| One profile | Straight to Home, no picker. |
| Manage mode (adults only) | Same grid with a pencil on each avatar. The editor opens as a bottom sheet on phones and a centred dialog on desktop and TV: name, avatar (swatch, icon or photo), Kids switch, content limit, PIN, Delete. |
| Quick switch | Clicking the avatar in the top bar lists the other profiles (PIN asked as usual) and a link to the full screen. Phones keep a dropdown. |
| Kids profile | Simpler navigation (Home, Watch, Listen, Read, My List), larger tiles, no admin or settings links. Returning to an adult profile needs the adult's PIN. |
| Not yet secured account | Attention dot on the account menu and a "Secure account" item; a card on Settings > Security. Dismissing hides the prompt but the Security card stays. |
| Empty | A new household shows one profile and skips the picker. |
| Loading | A skeleton grid on the picker; a neutral skeleton on Settings. |
| Error (Engine unreachable, not allowed) | A plain one-line message with Retry; admin-only actions are simply not shown to others. |
| Partly ready | Not applicable. |

Device sizes: 2 columns on small phones, 3 to 4 on tablets, up to 4 on desktop and TV; touch targets at least 44 px (56 px on TV); focus always visible for remote control.

## 5. Data / model changes

None to build. Already stored: avatar colour or icon or photo, Kids flag, per-profile content limit, optional PIN, optional email on an account (the data store was rebuilt for this).

## 6. Work packets

None. Every packet of the Netflix-style profiles plan (P1 to P7) and the Settings and Profiles plan (S1 to S14) is merged.

## 7. Risks and open questions

- **Risks:** none new. The content limit touches every list, so speed on large libraries is worth watching.
- **Open questions:** none.

## 8. Gaps and drift

Each item was decided by Shy on 2026-10-17 and is recorded in section 3.

| Item | Draft said | Reality | Decision |
|---|---|---|---|
| Picker timing | Maybe only on shared or TV devices | Whenever more than one profile | Drop the Draft idea (D1) |
| Setup length | Five screens or fewer, with a profile and avatar step | Six steps, no profile step | Change the spec (D7) |
| Manage profiles location | Settings > Profile | Settings > Profiles plus detail page | Change the spec (D8) |
| Secure-account prompt | Open: Security card only, or card plus dot | Dismissable, Security card and account-menu dot | Keep as built (D5) |
| "N of 8" wording | Meaning unclear | Cap is 8; wording belonged to the retired drawer | Drop the wording (D6) |
