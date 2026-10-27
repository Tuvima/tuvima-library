# Spec: Settings and top navigation fixes

> **Status:** Locked · **Version:** 2 · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../product/overview.md) · [Users, profiles and households](users-profiles-households.md) · [Universe (companion, section 4A)](universe.md) · [Presentation rules](../../docs/product/presentation-rules.md)

Covers bug-log items F11, F27, F28, F36, F63 and F66 (group 5, "Settings and top navigation"). Draft for discussion; nothing here is approved or built.

## 0. Product walkthrough (read this first)

**What you see today.** Open Settings and try the top bar (Read, Watch, Listen, View, Collections) or the Library logo: the page flickers and you stay in Settings. The only way out is the browser's Back button. Households sits under Profiles in the Personal group, although it is an administrator's job. Every library section (Read, Watch, Listen) has its own "My List" row in its side menu, while there is also a My List heart at the top right. Listen shows both "My List" and "Favorites". The Companion page loads, but nothing on it says what it is for.

**What you will see after this change.**
1. Clicking any top-bar link or the logo while in Settings takes you there, every time. (F28, F11)
2. Households sits under Users and Sign-in, in the administrator's Manage group, and is not offered to anyone else. (F27)
3. The side menus of Read, Watch and Listen no longer carry a "My List" row. The My List heart at the top right is the one place for it. (F66, option A)
4. In Listen, "Favorites" stays under Music / Playlists and is the only personal list there. (F63)
5. The Companion page opens with one plain sentence saying what it is and how to use it, plus a small (i) tooltip. (F36)

**What stays the same.** The top-right My List heart and the My Library page, the My List heart/"Add to My List" actions on titles, how saved items are stored, the Settings menu content and who can see each page, and the Companion's behaviour (this only adds explanation).

**Representative journeys.**
- Shy is on Settings > Network, clicks Watch: Watch opens. Clicks the logo: Home opens.
- Shy opens Settings > Users and Sign-in and finds Households as a sub-page next to Sign-in Methods. A household administrator who is not the server administrator does not see it.
- A listener opens Listen: the left menu shows Favorites under Music / Playlists, with no My List row. The heart at the top right still opens My List.
- Someone opens the Companion with nothing playing and reads what it is before choosing an episode.

## 1. Goal

Make Settings a place you can always leave, put each Settings page where its audience expects it, make personal lists less confusing (one My List, one Favorites), and explain the Companion.

## 2. Scope

**In scope**
- Fix the Settings page bouncing you back (F28 and F11, one cause).
- Move Households under Users and Sign-in (F27).
- Remove the "My List" row from the Read, Watch and Listen side menus (F66 option A, F63).
- Keep Favorites under Listen > Music / Playlists as the only personal list in Listen (F63).
- An explanation on the Companion page (F36).
- Tests that would have caught F28.

**Out of scope**
- Changing what My List holds, how it is stored or the My Library page.
- Redesigning the Companion or making it a desktop side panel (universe spec Q14).
- Renaming "Favorites" to the British spelling across the app (see Q3).
- Any change to who may see Households (still server administrators only).

## 3. Decisions made

- **D1. F28 and F11 are one bug with one cause** (found by reproducing it, section 7). The fix is in the Settings page, not in the top bar.
- **D2. Households is an administration page** (Shy, bug log 2026-10-26). The users-profiles-households spec is silent on where it sits in the menu (it only requires the password-first rule and server-administrator-only management), so moving it does not contradict a Locked decision. Pre-release rule: no compatibility shims, so **no redirect is added** (Shy, 2026-10-27) and the old Households address stops working .
- **D3. One My List, at the top right (Shy, 2026-10-27: option A).** The section rows are not separate lists: they open the same list filtered to that area. Removing them loses a shortcut, not data.
- **D5. Listen playlist and album action keeps "Add to My List"** (Shy, 2026-10-27), since it feeds My List.
- **D6. Spelling stays "Favorites"** (Shy, 2026-10-27).
- **D7. Companion is phone and tablet only** (Shy, 2026-10-27): the Companion menu item, Home card and page are hidden on desktop; the intro text says "on your phone". A desktop side panel would be a separate spec.
- **D4. Favorites stays in Listen only** because it holds liked songs, a music idea. It does not become a general list.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| In Settings, click a top-bar link or the logo | The page opens. No flicker, no return to Settings. |
| Settings page still loading when a link is clicked | The click still wins; Settings stops loading and the new page opens. |
| Server administrator, Settings menu | Manage group: Users and Sign-in, with Sign-in Methods and Households as sub-pages. Profiles has no Households row. |
| Household administrator (not server administrator) | Profiles page as today; no Households row, as today. |
| Read / Watch / Listen side menu | No "My List" row. Watch and Listen keep Continue and Unwatched/Unread rows; Listen keeps Favorites under Music / Playlists. |
| Top-right My List heart | Opens My List as today. |
| Companion, something playing | One sentence at the top: what the Companion is, plus an (i) tooltip with how to use it. |
| Companion, nothing playing | The existing "Nothing is playing" text, reworded to start with what the Companion is. |
| Phone / TV | Settings fix applies everywhere. The menus' My List removal applies on every screen size. Households stays a computer/tablet administration page. |

Suggested Companion wording (to confirm, Q5): "Companion follows what you are watching or listening to and shows who is on screen, where you have seen them before and a short recap, hiding anything past your place. Start something on a TV or computer and it appears here, or open any title, choose Companion from its menu and pick where you are."

## 5. Data / model changes

None. No stored data changes. The Households address changes (D2), which is a menu route, not data.

## 6. Work packets

### WP1 — Stop Settings bouncing you back (F28, F11)
- **What it does:** In the Settings page's section check (`ResolveSectionAndNormalize`), stop it from redirecting once the address is no longer a Settings address. Today a helper reports "not a Settings address, nothing to wait for" and the check then goes on and navigates back to the old Settings page.
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/Settings.razor`, a new bUnit test in `tests/MediaEngine.Web.Tests`.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test navigates from `/settings/overview` to `/watch` and `/` and ends on those addresses; manually, every top-bar link and the logo leave Settings on a small test library. (A one-line version of the fix was tried in the cloud and each link worked; the experiment was discarded.)

### WP2 — Households under Users and Sign-in (F27)
- **What it does:** Move the Households row from Profiles to the Manage > Users and Sign-in group, with the matching address and visibility rule, breadcrumb and menu tests.
- **Files likely touched:** `src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs`, the Households tab and its route checks, navigation tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a server administrator sees Households under Users and Sign-in; household administrators and members do not; the Profiles menu has no Households row; the Settings menu guardrail tests pass.

### WP3 — Remove "My List" from section menus (F66 A, F63)
- **What it does:** Delete the My List row from the Read, Watch and Listen side menus. Keep Listen's Favorites. Check the labels "Playlist added to My List" in Listen and decide wording in Q2.
- **Files likely touched:** `Components/Pages/ReadPage.razor`, `WatchPage.razor`, `ListenBrowsePage.razor`, `ListenPage.razor.cs`, related tests.
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** no side menu shows "My List"; the top-right heart and My Library are unchanged; ForMe's per-area My List still works for old saved links.

### WP4 — Explain the Companion (F36)
- **What it does:** Add the intro sentence and (i) tooltip; reword the empty state; hide the Companion on desktop form factors (phone and tablet only, D7).
- **Files likely touched:** `Components/Pages/CompanionPage.razor`, the title menu item and Home card that open it, its tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** the Companion page in every state opens with the explanation (suggested sentence, reworded for phones); on a desktop-sized window the Companion menu item, Home card and page are not offered (a test covers the device rule).

## 7. Root causes found

- **F28 / F11 (reproduced).** On a clean test Engine and Dashboard, with Settings open, clicking Watch moved the address to `/watch` and then straight back to `/settings/overview`; the logo behaved the same. Cause: Settings re-checks its own address whenever something nudges it. Once the address is no longer a Settings address, a safety check meant to say "nothing to correct" returns "fine", and the code then carries on and sends you back to the Settings page it believes you should be on. The check was added on 2026-10-22 with the library kinds work. It only shows when the nudge arrives while the page is still on screen, which makes it look random. With a one-line change that stops the check from redirecting away from non-Settings addresses, the same clicks reached Watch and Home.
- **F27.** Households was placed under Profiles when Profiles was built; no spec dictated it.
- **F66.** The three section menus each hardcode a "My List" row pointing at the same My List list filtered by area. It looks like three lists but is one, hence the confusion.
- **F63.** Listen shows "My List" (same cause as F66) beside its own "Favorites" row.
- **F36.** The page works but was built as reading-only, with explanation only in its empty state.

## 8. Local checks

- **L1.** On the real library, in Settings, click each top-bar link and the logo (also from deep pages such as Libraries and Users). Each must leave Settings.
- **L2.** Sign in as a household administrator and a member: no Households row.
- **L3.** On a phone-width window: Read, Watch and Listen menus have no My List; the top-right heart still opens it.
- **L4.** Play something on another device and open the Companion; the explanation reads well.

## 9. Risks and open questions

- **Risks:** WP1 touches the Settings page that all admin work passes through; the test must cover deep links. Removing My List rows removes a shortcut some people use.
- **Open questions:** none. Q1 to Q6 answered by Shy on 2026-10-27 (all recommended options).

## 10. Gaps and drift

- The bug log says every section's My List "always opens the one global list". It actually opens that list filtered to the section. The decision (Q1) is unchanged but the loss is smaller than described.

## Plain English summary

Settings has a bug: when you click anywhere in the top bar, Settings quietly drags you back. I reproduced it and found why; a tiny fix works. Households moves to the administrator's Users and Sign-in area. The three "My List" shortcuts in Read, Watch and Listen go away, leaving the heart at the top right as the one My List, and Listen keeps Favorites. The Companion page gets a plain sentence explaining what it is.
