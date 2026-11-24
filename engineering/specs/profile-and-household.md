# Spec: Profile and Household

> **Status:** In progress (Locked by Shy, 2026-11-24; build started 2026-11-24) · **Version:** 1 · **Last updated:** 2026-11-24 · **Related docs:** [Product overview](../product/overview.md) · [Users, profiles and households](users-profiles-households.md) · [Library access](library-access.md) · [Settings and top navigation fixes](settings-and-top-navigation.md) · [Look v3.2 phone and tablet standards](look-pass-v3-2-phone-and-tablet-standards.md) · [Mockups](../design/mockups/profile-and-household.html) (screens 1 to 6)

## 1. Goal

Your own name, picture, PIN and preferences are one press from your picture in the top bar, on a page called **My profile**, instead of five presses deep inside an administrator page. The page about the people you live with is called **Household** and holds everything that is about the household in one place.

## 2. Scope

**In scope**
- The profile menu in the top bar and its phone sheet.
- A new **My profile** page that replaces today's "Profile" page and absorbs your own Preferences.
- The **Household** page that replaces "Manage Profiles", and the page for one person inside it.
- The names of these pages everywhere they appear (menus, page titles, links, help pages).
- Desktop, tablet and phone layouts of all of the above.

**Out of scope**
- What each preference does. Every option that exists today is kept and behaves the same.
- Who is allowed to do what. No permission changes: anyone who can change something today can change it after this, and nobody gains anything new.
- Security & Devices, which stays as it is.
- Creating and removing households, sign-ins and the Advanced tab under Server > Users & Households, which stay where they are.
- The full-screen "Who's watching?" screen, including on a television.
- The redesign of Activity and Taste (still in the unplanned queue).
- The "honest waiting" message for Watch, Read and Listen (screen 7 in the mockups). It is a separate piece of work.
- The native phone and TV apps.

## 3. Decisions made

- **D1. Your name at the top of the profile menu is a button that opens My profile** (question 220, answer A). Its second line reads "Profile and preferences". The separate "Profile" row is removed, because the top row now does that job. Reason: the most obvious thing to press should lead to your own options.
- **D2. "Profile" is renamed "My profile" and "Manage Profiles" is renamed "Household"** (221A). Reason: "Manage Profiles" reads as an administrator page, so people did not look there for their own options; "Household" says what the page is about.
- **D3. Preferences is a tab of My profile** (221A). My profile's tabs are Profile, Preferences, Activity, Taste and Linked accounts. Administrators also see Access & safety for themselves, as they do today. Import still opens from its button on the Profile tab.
- **D4. Preferences keeps its four sections** (Watching, Listening, Reading, Home), one shown at a time, with the same options as today. One line at the top says these choices are yours and follow you to every device. Options that belong to one device only ("On this device": maximum resolution, hardware decoding, surround sound) sit together at the bottom of Watching under their own heading.
- **D5. One Edit window.** "Edit name and picture" opens a single small window, the same one from My profile and from a person's page. The PIN moves out of that window to its own row under "Sign-in and safety", so it is visible without opening anything. Reason: today there are two different Edit windows and only one shows the PIN.
- **D6. The Household page is titled with the household's name.** A person who may rename the household today sees a pencil beside the name. Everyone else sees the name only.
- **D7. One row per person, one line of plain words.** The whole row opens that person's page; there is no Edit button and no row of badges. The line under the name says what matters ("Administrator · no sign-in", "Kids · PIN"). "Household administrator" and "Server administrator" both read "Administrator" in this list; the person's page gives the detail. Your own row is marked "You" and opens My profile. The last row is "Add profile", as today.
- **D8. Household-wide choices sit together on the Household page** under "For everyone in this household": "What Kids can see" and "Libraries this household can use". The second moves here from Server > Users & Households > Households (222A). Reason: it is about this household, and belongs where the household's people are.
- **D9. "Who's watching?" is removed from the profile menu** (223A). The menu's "Switch to" list already does the same thing. The full-screen screen still appears when Library opens and on a television.
- **D10. No new "Ask who's watching when Library opens" switch** (224B). The screen keeps working as it does today. Reason: fewer controls.
- **D11. Kept from 30 October:** the signed-in person stays at the top of the profile menu, and creating or removing a whole household stays under Server > Users & Households. The Household page shows server administrators one quiet line linking there.
- **D12. A person's page has the tabs Profile, Preferences, Access & safety and Activity**, one link back to Household, and the person's name as the title. Delete stays at the bottom of Access & safety, asks once and then happens.
- **D13. Each person's circle has its own color**, taken from the existing avatar colors in the order people appear in the household, so two people with the same initial do not look identical. A picture, when there is one, replaces the circle as today. The color is worked out when shown; nothing new is stored.
- **D14. Sign out lines up with the other menu rows** and appears only when this browser is signed in with a password. On a home network with no password there is nothing to sign out of.
- **D15. A Kids profile sees less.** Its top row opens its own simple profile page, as "Profile" does today. It has no Household or Settings rows.
- **D16. Phones get everything** (phone parity). The profile sheet uses the full width with the same rows as the desktop menu. My profile and Household are full pages with a title bar and one back arrow. Tab rows scroll sideways. Each option stacks its control under its label. Reordering people is "Reorder" in the page's "..." menu instead of a drag handle.
- **D17. Addresses.** My profile keeps today's Profile address, with Preferences as a tab under it. Household and a person's page move to addresses that say "household". The old Manage Profiles addresses stop working and do not redirect (standing rule). The existing short addresses for preferences keep opening the Preferences tab.

## 4. UI behaviour

### Profile menu (mockup screen 1; phone sheet on screen 6)

| Situation | What the person sees |
|---|---|
| Normal, administrator | Own row (picture, name, "Profile and preferences", arrow). "Switch to" with the other people; a Kids chip or a small lock for a PIN at the end of a row. Then Household, Settings (with the Needs Review count), Recycle bin, Help, and Sign out when signed in with a password. |
| Normal, member | The same without Household. Settings and Recycle bin follow today's rules for members. |
| Normal, Kids profile | Own row and "Switch to" only, plus Help. |
| Only one profile | No "Switch to" heading or list. |
| Loading | The menu opens at once with the signed-in person; the other people appear when ready, with no layout jump. |
| Error (Engine unreachable) | The menu still opens with the own row and Help; "Switch to" is left out. |
| Phone | A bottom sheet across the full width with the same rows, 48 px tall. |
| TV | Unchanged. |

### My profile (screens 2 and 3)

| Situation | What the person sees |
|---|---|
| Normal, Profile tab | Card with picture, name, role and join month, and "Edit name and picture". "Sign-in and safety": Profile PIN (Set a PIN or Change), Password and email, Devices; the last two open Security & Devices. "Last 30 days" figures as today. The Import button as today. |
| Normal, Preferences tab | One line saying these are yours and follow you. Watching, Listening, Reading, Home switch. Options save the moment they are changed, as today. |
| Activity, Taste, Linked accounts | The same content as today's tabs. |
| Administrator | One more tab, Access & safety, with the same content it has today for their own profile. |
| Kids profile | Its own simple page, unchanged. |
| Empty (new profile, nothing played) | The "Last 30 days" figures show 0 and one line: "Nothing yet. What you watch, read and listen to will show up here." |
| Loading | The page frame, title and tabs appear at once; the content area shows the app's normal loading state. |
| Error (Engine unreachable) | The app's normal "Can't reach Library" state with Try again; nothing looks editable. |
| Error (a save fails) | An inline message beside the option, and the option returns to its old value. |
| Phone | Title bar "My profile" with one back arrow; tabs scroll sideways; options stack. |

### Household (screen 4; phone on screen 6)

| Situation | What the person sees |
|---|---|
| Normal, household administrator | Title is the household's name. One row per person, then "Add profile". "For everyone in this household": What Kids can see, and Libraries this household can use (for those who may change it today). |
| Server administrator with several households | Their own household first, then the others, each under its name with the same rows. One quiet line links to Server > Users & Households for creating and removing households. |
| Member | No Household row in any menu. Opening the address shows the app's normal "not available" state. |
| Empty (only you) | Your own row and "Add profile". |
| Eight people | "Add profile" is replaced by the Engine's sentence saying the household is full, as today. |
| Loading | Title and frame at once; rows in the normal loading state. |
| Error | The normal unreachable state with Try again; a failed rename or reorder shows an inline message and puts things back. |
| Phone | The same list without drag handles; "Reorder" in the "..." menu. |

### A person (screen 5)

| Situation | What the person sees |
|---|---|
| Normal | One link back to Household, the person's name as the title, tabs Profile, Preferences, Access & safety, Activity. The same options as today. |
| Your own row | Opens My profile instead, so there is one version of your page. |
| Not allowed, or the person no longer exists | The app's normal "not available" or "no longer exists" state with a link back to Household. |
| Phone | Title bar with the person's name and one back arrow. |

## 5. Data / model changes

None. Nothing new is stored and no existing library needs a reset. The addresses of the Manage Profiles pages change without redirects (D17).

## 6. Work packets

### WP1 — Names, Settings menu and addresses
- **What it does:** Renames Profile to My profile and Manage Profiles to Household in the Settings menu, page titles and browser titles; adds Preferences as a tab of My profile; moves Household and the person page to household addresses; retires the old Manage Profiles addresses without redirects; keeps the short preference addresses opening My profile > Preferences (D2, D3, D17).
- **Files likely touched:** `src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs`, `src/MediaEngine.Web/Services/Navigation/SettingsMenuBuilder.cs`, `src/MediaEngine.Web/Components/Pages/Settings.razor`, `src/MediaEngine.Web/Resources/SharedStrings.resx`, `tests/MediaEngine.Web.Tests/SettingsNavTests.cs`, `SettingsMenuTreeTests.cs`
- **Depends on:** none
- **Size:** Standard
- **Done when:** `SettingsNavTests` and `SettingsMenuTreeTests` pass with the new names, tabs and addresses, including a test that the old Manage Profiles address is not found.

### WP2 — My profile page
- **What it does:** Builds My profile with its tabs: the Profile tab (card, single Edit window, Sign-in and safety rows with the PIN row, figures, Import button), the Preferences tab (the existing preference sections, "On this device" grouped at the bottom of Watching, the "these are yours" line), and Access & safety for administrators (D3, D4, D5).
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/Settings.razor`, `Components/Settings/ProfileDetailPage.razor`, `ProfileTabs.razor`, `PlaybackSettingsPage.razor`, `ListeningSettingsPage.razor`, `ReadingSettingsPage.razor`, `ThisBrowserVideoSection.razor`, `Components/Profiles/ProfileEditorSheet.razor`, the current Profile overview component, `tests/MediaEngine.Web.Tests/ProfileDetailPageTests.cs`, `ProfileEditorSheetTests.cs`, `ProfileOverviewPresentationTests.cs`
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** component tests show a member reaching every preference from My profile > Preferences, the PIN row on the Profile tab, one Edit window with no PIN field, and Access & safety only for administrators.

### WP3 — Profile menu and phone sheet
- **What it does:** Makes the own row a button to My profile, removes the Profile and "Who's watching?" rows, renames Manage profiles to Household, adds the "Switch to" heading, per-person colors, and the Sign out rule; the phone sheet uses the full width (D1, D9, D13, D14, D15, D16).
- **Files likely touched:** `src/MediaEngine.Web/Components/Navigation/TopNavAccountMenu.razor` and its `.css`, `TopNavProfileSwitcher.razor`, `PhoneNavigationMenu.razor`, the shared avatar component, `tests/MediaEngine.Web.Tests/UiShellRenderTests.ProfileMenu.cs`, `ProfileQuickSwitchTests.cs`
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** menu tests pass for administrator, member, Kids and single-profile cases, and the phone sheet's rows are as wide as the sheet at 390 px.

### WP4 — Household page
- **What it does:** Rebuilds the people list as one plain row per person that opens the person's page (own row opens My profile), titles the page with the household's name with rename for those allowed, and adds "For everyone in this household" with What Kids can see and Libraries this household can use, removing the latter from the Server page (D6, D7, D8, D11). No Who's watching switch (D10).
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/ProfilesTab.razor`, `HouseholdsTab.razor`, `UsersAccessSettingsTab.razor`, `Components/Profiles/ProfileManageRules.cs`, `tests/MediaEngine.Web.Tests/ProfilesTabTests.cs`, `HouseholdsTabTests.cs`, `ManagedAccessHouseholdTests.cs`, `ProfileManageRulesTests.cs`
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** tests show the rows and their one-line descriptions, the household libraries row on this page and gone from the Server page, and that a member cannot open the page.

### WP5 — A person's page
- **What it does:** Titles the page with the person's name, one back link to Household, Activity as a tab, and reuse of the same Profile and Preferences parts as My profile (D12).
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/ProfileDetailPage.razor`, `ProfileTabs.razor`, `tests/MediaEngine.Web.Tests/ProfileDetailPageTests.cs`
- **Depends on:** WP2, WP4
- **Size:** Standard
- **Done when:** tests show the four tabs, the single back link, and Delete asking once.

### WP6 — Phone and tablet layouts
- **What it does:** Title bar with one back arrow on My profile, Household and a person's page; sideways-scrolling tabs; stacked option rows; "Reorder" in the "..." menu; checks at 1024, 1280, 1440 and phone width against the mockups (D16).
- **Files likely touched:** the component-scoped `.css` files of the pages in WP2 to WP5, `PhoneNavigationMenu.razor`, `tests/MediaEngine.Web.Tests/AlignmentGuardrailTests.cs` if a rule needs a new entry
- **Depends on:** WP2, WP3, WP4, WP5
- **Size:** Standard
- **Done when:** no sideways page scroll and no clipped control at any of the four widths on a small test library.

### WP7 — Links, wording and help pages
- **What it does:** Points every remaining link to the old pages (My List, the Who's watching screen's manage button, Vault and PIN prompts, setup) at the new ones; updates the Dashboard code map and the public help pages.
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/ProfilePickerPage.razor`, `MyListPage.razor`, `Services/Vault/VaultPinPrompt.cs`, `NsfwModeSwitching.cs`, `src/MediaEngine.Web/CLAUDE.md`; in `tuvima-docs`: `product/presentation-rules.md`, `guides/households.md`, `guides/library-settings.md`, `guides/account-security.md`, `reference/glossary.md`, `architecture/settings.md`
- **Depends on:** WP1 to WP6
- **Size:** Mechanical
- **Done when:** a search of the Dashboard and the help pages finds no "Manage Profiles" or "Manage profiles", and the docs build passes.

## 7. Local checks

- **L1.** Press your picture, then your name. My profile opens. Press Preferences. Failure: more than two presses to reach Preferences, or any option from today missing.
- **L2.** Change a Watching preference on the desktop, then open My profile on a phone. The change is there. Failure: the phone shows the old value.
- **L3.** As an administrator open Household. Each person is one row with one line of words and no badges or Edit button; pressing a row opens that person; your own row opens My profile. Failure: bordered buttons, badges, or two different pages for yourself.
- **L4.** On Household, change "Libraries this household can use", then switch to a member of that household. The member sees the change. Check the row is gone from Server > Users & Households. Failure: the row is in both places or the change does not apply.
- **L5.** Switch to a member profile. The menu has no Household row; My profile and Preferences work. Switch to a Kids profile. Only its own page and "Switch to" are offered.
- **L6.** On a real phone open the profile sheet, My profile > Preferences and Household. Rows use the full width, there is one back arrow, nothing is cut off. Failure: sideways scrolling of the page or a control squeezed beside its label.
- **L7.** Compare each screen with the mockups at 1024, 1280, 1440 and phone width.
- **L8.** Open Library on the television. "Who's watching?" works as before.

## 8. Risks and open questions

- **Risks:** People used to "Manage Profiles" will look for it once; the Household name and the menu row make the new place easy to find, and the help pages are updated. Old bookmarks to the Manage Profiles addresses stop working (accepted, D17). If the Engine lets only a server administrator change a household's libraries, a household administrator will not see that row (D8 changes where it is shown, not who may use it); the build reports which it is and changes nothing. Six tabs for an administrator on My profile is a full row at 1024 px; the mockup shows it fits.
- **Open questions:** none.
