---
title: "Set up a household"
description: "Add people to a household, give them their own sign-in, use the profile picker, back up a phone's photos, and share photos to the Shared Library."
audience: administrator
category: guide
product_area: profiles
status: current
---

# Set up a household

A household is the group of people who live together and the sign-ins that open them. It holds up to eight people, and each person has a profile with their own progress, lists, favourites and private photo space. This guide shows how to set one up, and how to give someone their own sign-in.

## Before you start

- You need to be a server administrator or the household administrator. Household administrators see **Settings → My household**. Server administrators see the same cards under **Settings → Users & Access → My household**, plus a **Households** tab that lists every household.
- Adding someone's own sign-in needs your own sign-in to be secure (a password or passkey). A desktop install started without a password shows **Secure your account** first.

## Add a person to the household

1. Open **My household** and choose **Add person**.
2. Enter a name. Switch on **Child profile** for a restricted profile. A child profile never has administrator access.
3. Optionally set a **PIN**. Anyone who switches into that profile is asked for it. Use 4 to 12 digits. You can change it later with **Set PIN**.
4. Save. The person appears as a card. They can use the profile straight away through the household's sign-in, and they do not need a sign-in of their own.

Adult profiles should have a PIN if others can reach the household's sign-in. Tuvima reminds you about this but does not force it.

## Give someone their own sign-in

Use this when a person wants to sign in with their own email, rather than picking their profile from the household sign-in.

1. On their card, choose **Give (name) their own sign-in**.
2. Enter their email. Choose **Send an invitation** or **Set a temporary password**.
3. Share what Tuvima shows you through a private channel. An invitation code works once and expires after 7 days. A temporary password also expires after 7 days, and the person must choose their own password the first time they sign in.

Tuvima never sends email for this. You hand over the code or password yourself.

Their own sign-in opens straight to them, without the profile picker. It stays in the same household, and it is never an administrator. It follows the household's library access, so when you change what the household can open, their sign-in changes too.

To remove someone's own sign-in, choose **Remove sign-in**. The person stays in the household with everything they have saved, and the household's sign-in can still open them.

## Someone from outside your household

A server administrator can invite someone who is not part of an existing household. Choose **Invite someone outside your household** under **Users & Access → Households**. They start a household of their own and become its household administrator, with only what you give them.

## The profile picker

When a household's sign-in is used, Tuvima shows the profile picker. Choose a person to open their profile. A profile with a PIN asks for it first.

On a device that only one person uses, tick **Always open as this person on this device** to open straight to that profile. **Stop always opening as** undoes it.

## Phone backup

A paired phone backs up its photos to one profile. Open **Settings → Network → Apps & devices**, find the phone in **Paired devices**, and choose a person in its **Whose photos … backs up** list. Only people in the phone's household are offered, and **Not chosen** turns backup off for that phone. The choice does not need a PIN. Photos go to that person's private photo space, and the phone keeps backing up to that profile even when someone else browses on it.

## Shared Library

Each household has one **Shared Library** for photos everyone in the house should see.

- Everyone in a household can open each other's photo space, read only, from the **Household** group in the View scope picker.
- To share a photo, submit it from your photo space to the Shared Library. Nothing is shared until you submit it.
- A household administrator reviews submissions. They can accept or decline each item, and accepted items appear in the household's Shared Library.
- Each household has its own Shared Library. People in one household cannot see another household's Shared Library.

Your private photo space stays private until you choose to submit something.

## What a household administrator can and cannot do

A household administrator can add and remove people in their own household, give them their own sign-ins, set their PINs and temporary passwords, reset their two-step codes, and hand out libraries and features that the household already has.

They cannot change another household, act on a server administrator or another household administrator, or turn on administrator access for anyone. Administrator access is always set by a server administrator.

## Next steps

- [Manage sign-in methods, passwords and recovery](account-security.md).
- [Set up secure remote access](remote-access.md) before you let people outside the home connect.
- [Configure an external sign-in provider](external-authentication.md).
