# Spec: Users, profiles and households

> **Status:** In progress (Locked by Shy 2026-10-23) · **Version:** 3 · **Last updated:** 2026-10-23 · **Related docs:** [Product overview](../product/overview.md) · [Security model](../../docs/architecture/security.md) · [ADR 0007 Accounts own access; profiles own experience](../architecture/decisions/0007-accounts-and-profiles-access.md) (replaced by this spec) · [Backup and restore](backup-and-restore.md) D19, D20 · [Open from the start](archive/open-from-the-start.md) · [Profiles and setup redesign](archive/profiles-setup-redesign.md)

## 1. Goal

A **profile** is the person: their progress, lists, settings and also what they may see and do (administrator roles, libraries, Read/Watch/Listen/View, content limits). A **user** is only a way to sign in (an email with a password, passkey or two-step) attached to one profile. Changing an email, deleting a sign-in or restoring a backup never loses a person, because the person lives in the profile, not in the sign-in. Households are groups of profiles that look after themselves; only the server administrator manages users.

## 2. Walkthrough (Shy's example)

Two households on one Library.

**Household 1 (the default household)**
- **Shy**: server administrator and household administrator. Signs in as shy@tuvima.com. Can never be deleted.
- **Mom**: household administrator. Signs in as mom@home.com and lands in her own profile. To change server settings she switches to Shy's profile (Shy's PIN if he set one).
- **Kid 1**: a profile with its own sign-in, kid@home.com, for the convenience of their own phone app. Signs straight into Kid 1.
- **Kid 2**: a profile with no sign-in. Opened by signing in as Shy (or Mom) and switching to Kid 2.

**Household 2** is set up the same way, but nobody in it is a server administrator.

What happens:
1. **Someone signs in** with their email and goes straight into *their* profile; from there they can switch to other profiles in their own household (PIN-protected profiles ask for the PIN), never into another household.
2. **Mom changes her email** to mom@new.com: her profile, progress and roles stay exactly as they were; only the sign-in changed.
3. **The server administrator deletes Kid 1's user**: the sign-in is gone; the Kid 1 profile and everything in it stay in Household 1. Kid 1 opens it by switching from Shy or Mom, and a new sign-in can be attached to it at any time.
4. **Mom (household administrator) deletes the Kid 2 profile**: Kid 2 and everything personal in it are gone (history, progress, bookmarks, lists, waiting restore items, activity, picture); no other profile changes.
5. **Household administrators can delete each other's profiles**, but a household always keeps at least one household administrator profile, and nobody can delete Shy's profile.
6. **The server administrator deletes the last household administrator's user in Household 2**: nobody could sign in to look after Household 2 any more, so the whole household goes with all its profiles, after a confirmation that lists them. Household 1 can never be removed.
7. **Shy restores a backup made before he changed his email**: he picks which user and household it belongs to, and the profiles are rebuilt under that user. An email that changed is never a reason a restore fails.
8. **Shy adds Household 2** while his own account still has no password: the Library asks him to set an email and password first, because a Library with several households must not be open to anyone who reaches it. With only one household, people may share the Library without passwords.

## 3. Scope

**In scope**
- Roles and access move from the sign-in to the profile (server administrator, household administrator, member, Kids; libraries; Read/Watch/Listen/View; content limits).
- A user is a sign-in attached to exactly one profile; it opens that profile, and can switch within its household.
- Users are created and deleted only by the server administrator (and the existing invitation and QR-code first sign-in).
- Deleting a user keeps the profile; the last household-administrator sign-in of a non-default household removes the household.
- Deleting a profile removes everything personal in it; household administrators delete profiles in their household, never the last household administrator profile, never the server administrator's profile.
- Changing a user's email keeps everything.
- Backup, restore and "Import a user" let the administrator choose which user and household the profiles go under (replaces the "email already used, refused" rule of D19).
- A password (email plus password or passkey) is required for the server administrator before a second household can be added or invited.
- The delete-user safety work already built on the working branch (the profile clean-up, photos moved to Shared by default, the confirmed list, the PIN and phone rules) is folded into this spec.

**Out of scope**
- People registering themselves under a profile (Shy's later idea: they would inherit that household's permissions and the household administrator would lock them down). Noted as a future spec.
- Changes to paired-app pairing, consent and Application permissions beyond following the profile's access.
- Remote access, outside sign-in providers, passkey enrolment screens (unchanged except where they attach to a user).

## 4. Decisions made

- **D1. The profile holds the person (Shy, 2026-10-22).** Roles (server administrator, household administrator, member, Kids), library choices, Read/Watch/Listen/View access, content limits, PIN, preferences and all personal state live on the profile. Replaces ADR 0007's "accounts own access".
- **D2. A user is a sign-in only.** Email, password, passkeys, two-step, outside sign-ins, recovery codes, sessions and paired devices belong to the user. A user is attached to exactly one profile (its own). Changing the email changes nothing else.
- **D3. Signing in opens your own profile; switching stays inside your household.** A user can switch to any profile of their own household (profile PIN when set); administrator rights apply only while on a profile that holds them (Mom switches to Shy's profile to act as server administrator).
- **D4. Only the server administrator creates and deletes users.** Household administrators manage the profiles of their household, not users.
- **D5. Deleting a user removes only the sign-in.** The profile and its data stay; a new user can be attached to it later. Exception (D6).
- **D6. A household cannot be left without an administrator who can sign in.** Deleting the last user attached to a household administrator profile in a non-default household removes that household with all its profiles, after a confirmation that lists them. The default household (the server administrator's) and the server administrator's profile and user can never be deleted.
- **D7. Household administrators delete profiles in their own household**, including another household administrator's profile, but a household always keeps at least one household administrator profile. Deleting one profile never affects another. Deleting a profile removes its user too (a sign-in without a profile cannot exist).
- **D8. Deleting a profile leaves nothing personal behind**: history, progress, bookmarks, lists, waiting restore items, personal activity, reader state, prepared offline copies and its picture file; photos move to Shared unless the person deleting chooses Delete. Security and audit history and the photo-folder name reservation are kept.
- **D9. Restores and imports target a user you choose.** A profile (or the profiles of a household) from a backup or a user export can be restored under any user and household the administrator picks; a changed email is never a reason to refuse.
- **D10. Passwords before a second household.** With one household the Library may be used without passwords (today's "Start without a password"). Adding or inviting a second household requires every server administrator's user to have an email and a password (or passkey) first. The very first server administrator may still start without a password at setup, as long as there is one household (Shy, 2026-10-23).
- **D11. Start fresh (Shy, 2026-10-23, Q1).** Nothing existing is carried over: a new data store epoch, set the Library up again. Backups and user exports made before this change are refused in one plain sentence.
- **D12. One household per user (Q2).** A sign-in reaches only its own household's profiles.
- **D13. Switching is free unless a profile has a PIN (Q3).** Like Netflix: any profile of the household can switch to any other, Kids included; a profile with a PIN asks for it. Most people will not set one.
- **D14. At most eight profiles per household (Q4).**
- **D15. A household administrator always has a user (Q5).** A profile can be made a household administrator only when a user is attached to it. Deleting the user of a household administrator who is not the last one leaves the profile as an ordinary member (an administrator without a sign-in cannot exist). Deleting the user of the last household administrator of a non-default household removes the household (D6). Household administrators can make another profile of their household an administrator when it has a user.
- **D16. Export my data (Q6).** Exports your own profile; a household administrator can also export the whole household; the server administrator can export anything.
- **D17. Several server administrators (Q7).** More than one profile can be a server administrator, but only profiles in the default household with a user that has an email and password (for example mom@home.com \ Mom). The Library always keeps at least one. The server administrator is really "shy@tuvima.com \ Shy": the sign-in and its profile together.

## 5. UI behaviour

| Situation | What the person sees |
|---|---|
| Sign in with email | Goes straight into their own profile; the profile switcher lists the other profiles of their household only. |
| Switch to a profile with administrator rights | Asks for that profile's PIN if set; administrator screens appear only while on that profile. |
| Server administrator: Users and Sign-in | One row per user: email, the profile it opens, the household. Actions: Invite (QR code and link, as today), Change email, Reset password and two-step, Attach to a profile, Delete user. No access settings here (they are on the profile). |
| Delete user (normal) | "Delete the sign-in mom@home.com? Mom's profile and everything in it stay in Household 1. You can attach a new sign-in to it later." |
| Delete user (last household administrator sign-in of Household 2) | "This is the last sign-in that can look after Household 2. Deleting it removes Household 2 and its profiles: Pat, Sam, Leo, with their progress, bookmarks and lists. Their photos: Move to Shared (selected) / Delete." |
| Household administrator: Profiles | Every profile of the household with role, access and Delete profile; the last household administrator profile has no Delete; never Shy's profile. |
| Delete profile | Names what goes (progress, bookmarks, lists, history; photos Move to Shared or Delete) and that its sign-in, if any, is removed too. |
| Change email | Saves; "Your profile and everything in it are unchanged." |
| Restore / Import a user | After the first look: "Put these profiles under: [existing user ▾] or [a new user: email]" and "In household: [▾]"; clashes with profiles already in the Library are shown before anything changes. |
| Add a household without a password | "Set an email and password for your own sign-in first. A Library with several households must not be open to anyone who reaches it." with a button to do it. |
| Phones | Management stays on computers and tablets as today; phones show the read-only lists. |
| Errors | Engine sentences shown as they are (not allowed, not found, refused because of the last administrator). |

## 6. Data / model changes

- Roles, library and feature access move from the user to the profile; the user keeps only sign-in material and the one profile it opens.
- Existing two-step, passkeys and outside sign-ins stay on the user.
- Pre-release: this is a reset (a new data store epoch, D11); existing Libraries are set up again. Backups and user exports made before the change are refused in plain words.

## 7. Work packets (draft; finalised when Locked)

### UPH1 — Profile-held access (Engine, data store, Contracts)
- **What it does:** roles, library and feature access on the profile; a user attached to one profile; authorisation (Engine checks, paired apps, Dashboard sessions) reads the active profile's access; data store epoch bump.
- **Files likely touched:** `src/MediaEngine.Storage/AccountRepository.cs`, `ProfileRepository`, `Schema/Upgrades/`, `StorageEpochGuard.cs`, `src/MediaEngine.Api/Security/*` (RequestAuthority resolver, AccountAccessMutationService, gates), `src/MediaEngine.Identity/*`, `src/MediaEngine.Contracts/Authentication/*`, tests.
- **Depends on:** none · **Size:** Complex

### UPH2 — Users: sign-in only (Engine + Dashboard)
- **What it does:** Users and Sign-in for the server administrator (invite, change email, reset, attach to a profile, delete user per D5/D6); sign-in opens the user's profile; switching within the household.
- **Depends on:** UPH1 · **Size:** Complex

### UPH3 — Profiles and households (Engine + Dashboard)
- **What it does:** household administrators manage profiles (roles, access, delete per D7/D8), the "at least one household administrator" rule, household removal per D6, profile clean-up and photo choice (from the working branch).
- **Depends on:** UPH1 · **Size:** Complex

### UPH4 — Passwords before a second household
- **What it does:** D10 checks on adding and inviting a household; the Dashboard prompt.
- **Depends on:** UPH1 · **Size:** Standard

### UPH5 — Backup, restore and Import a user target a chosen user
- **What it does:** D9: restore and import put profiles under a chosen user and household; backup contents follow the new model.
- **Depends on:** UPH1, UPH2, UPH3 · **Size:** Complex

### UPH6 — Docs and guardrails
- **What it does:** security model, presentation rules, API reference, ADR 0007 marked replaced (new ADR), guardrails that keep access on the profile.
- **Depends on:** UPH1 to UPH5 · **Size:** Standard

## 8. Local checks

- **L1.** Shy's example on a small test Library: the two households, sign-ins, switching, the PIN, the deletes in section 2 steps 3 to 6, with the outcomes described there.
- **L2.** Change an email, sign in with the new one: same profile, same progress and roles.
- **L3.** Restore a backup and import a user export under a different user than the one that made them: the profiles are rebuilt with their progress.
- **L4.** A paired phone app signed in as Kid 1 keeps working after Kid 1's access is changed on the profile, and stops when the Kid 1 user is deleted.

## 9. Risks and open questions

- **Risks:** this changes who is allowed to do what across the whole Library (security review required on every packet); a reset of existing test Libraries; paired apps must follow the profile's access without re-pairing.
- **Open questions:** none. Q1 to Q7 answered by Shy on 2026-10-23 (D11 to D17).
