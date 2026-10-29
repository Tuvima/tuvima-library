# Spec: Profile menu and Who's watching

Status: Locked · Version: 2 · Last updated: 2026-10-29 (locked by Shy) · Related: follow-ups F87, F88, F3, F86; specs settings-and-top-navigation, users-profiles-households; Settings flat-menu thread (F90)

## Goal
Two small, familiar surfaces for switching people: the top-bar profile menu lists the *other* profiles first (Netflix order), and the Who's watching screen becomes open round pictures with the name underneath, tap to pick, with an edit mode.

## What exists today (checked in the code)
- Menu (`TopNavAccountMenu.razor`): current profile header, My profile, My activity, other profiles, "Who's watching?", Vault quick check, Needs Review (admins), Manage profiles, plugin entries, Settings, Help, Sign out. No "Transfer Profile" exists.
- Who's watching (`ProfilePickerPage.razor`, route /who): boxed cards, a "Continue as <name>" button for the pre-selected profile, a Manage profiles button that switches the same screen to edit mode with a pencil and Done. In edit mode a tap opens a pop-up editor sheet. There is no stored profile order.

## Scope
In: menu order and contents; Who's watching look, wrapping, edit mode, order; pencil goes to the person's profile page. Out: the Account/My Profile page redesign (F86), Settings navigation (F90), PIN and sign-in rules (unchanged).

## Proposed behaviour
**Menu, top to bottom:** the other profiles (round picture + name, PIN pad inline as today; the signed-in profile is not listed); Manage profiles (admins only, not Kids); Profile (the page now called Overview; Kids see it too and it opens their own overview, taste and activity); Settings (admins, not Kids); Help; then the Vault quick check, plugin entries and Sign out as today. Needs Review leaves the menu. "My activity" leaves the menu for adults (it becomes a tab on the profile page, F86); a Kids profile keeps a direct Activity link because it cannot reach Settings. The "Secure your account" card stays at the top when it applies. With a one-person household the menu shows no profile list and no "Who's watching?" row.
**Who's watching:** title "Who's watching?", then a centred row of large round pictures with names. Many profiles wrap onto further centred rows (up to 8). Tap = open that profile (PIN pad if it has one). "Continue as" is removed; the previous profile still gets keyboard focus, nothing opens without a click. "Manage profiles" pill under the row (people who may manage, never inside Kids). Edit mode: pencil over each picture, "Done" pill; a pencil opens that person's settings page at /settings/profiles/{profileId} (as Netflix does), no pop-up editor; "Add profile" is a dashed-plus round tile at the end. Sign out stays as a quiet link. Library's own colours, no boxes. Fixes F3 (picture sized to the circle).
**Order:** admins drag in the Settings Manage profiles list (not on the picker, which stays simple); one order shared by the household, new profiles go last; the picker and menu follow it.

## Data/model changes
One new per-profile sort position (Engine + Contracts + fixtures); data-store step per the data-store rules; reorder action needs household-manage permission.

## Work packets
1. Menu reorder and trim (Standard): TopNavAccountMenu, TopNavProfileSwitcher, CSS; done-when: order as above, Needs Review gone, tests updated.
2. Who's watching restyle (Standard): ProfilePickerPage + CSS; done-when: open round pictures, wrapping centred rows, no Continue-as, pencil/Done edit mode.
3. Profile order (Standard, depends on 2): position column, Engine action, Contracts, drag support for the Settings thread's list; done-when: order survives reload and shows in menu and picker.
4. Pencil to profile page (Mechanical, depends on 2 and the Settings thread's profile-page route).

## Local checks
Menu and picker with 1, 3 and 8 profiles on desktop, phone and TV widths; PIN profile from both menu and picker; Kids profile menu; drag order persists.

## Risks and open questions
The menu item and page are named "Profile" (Shy, 2026-10-29); the Settings thread keeps the route /settings/overview and applies the label. Transfer Profile is dropped (Shy). Profile order is also draggable on the Settings Manage profiles list (Settings thread), using the same stored order.

## Plain English summary
Switching profiles will work like Netflix: the menu shows the other people first, and the Who's watching screen is a clean row of round pictures you tap. Admins can reorder them and use a Manage mode with pencils to reach each person's page.
