# 0015 — Profiles hold the person and their access; a user is a sign-in

**Date:** 2026-10-23 (spec: [Users, profiles and households](../../specs/users-profiles-households.md), Locked by Shy) · **Status:** Accepted · **Replaces:** [0007](0007-accounts-and-profiles-access.md) (the "accounts own access" part; "no guest keys" and the other removals in 0007 still stand)

## What
A **profile** is the person: their progress, lists and settings, and also their roles (server administrator, household administrator, member, Kids), their Read/Watch/Listen/View and library access, their content limits and their administrator PIN protection. A **user** is only a way to sign in (email with a password, passkey, two-step, outside sign-in), attached to exactly one profile. Signing in opens that profile; switching stays inside the user's household (with the profile PIN when one is set). Only the server administrator creates and deletes users; household administrators look after their household's profiles. Deleting a user removes the sign-in and keeps the profile (except the last household-administrator sign-in of a non-default household, which removes that household); deleting a profile removes its sign-in and everything personal in it. Backups, restores and imports place profiles under the user and household the administrator chooses, and never carry administrator roles.

## Why
- **Reliability:** changing an email, deleting a sign-in or restoring a backup never loses a person, because the person lives in the profile.
- **Privacy / security:** households look after themselves, a sign-in reaches only its own household, administrator rights apply only while acting as a profile that holds them, and a Library with several households requires passwords.
- **Maintenance:** one place holds access, so the sign-in and the profile can never disagree.

## Trade-offs accepted
Pre-release reset (new storage epoch); no conversion of earlier Libraries, backups or user files (user files were later removed by [library access v3](../../specs/library-access.md)). Native clients keep working through the same pairing and token flows, which now follow the profile's access.

## Revisit if
People need one sign-in that reaches several households, or self-registration under a profile (a later spec).
