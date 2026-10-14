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

- You need to be a server administrator or the household administrator. Both look after people from **Settings → Profiles**: open a person to see their page. Server administrators also get **Settings → Users & Access → Households**, which lists every household.
- Adding someone's own sign-in needs your own sign-in to be secure (a password or passkey). A desktop install started without a password shows **Secure your account** first.

## Add a person to the household

1. Open **Settings → Profiles** and choose **Add profile**.
2. Enter a name. Switch on **Kids profile** for a restricted profile. A kids profile never has administrator access.
3. Optionally set a **PIN**. Anyone who switches into that profile is asked for it. Use 4 to 12 digits. You can change it later from the person's **Profile details**.
4. Save. The person appears in the list. They can use the profile straight away through the household's sign-in, and they do not need a sign-in of their own.

Adult profiles should have a PIN if others can reach the household's sign-in. Tuvima reminds you about this but does not force it.

## Give someone their own sign-in

Use this when a person wants to sign in with their own email, rather than picking their profile from the household sign-in.

1. Open **Settings → Profiles**, open the person, and choose **Sign-in**. A person without their own sign-in reads *Switches from Who's watching*.
2. Enter their email and a **Starting password**. Press **Generate** for a random one. An own sign-in always has a password.
3. Leave **Must change it at first sign-in** on (the default) if the person should choose their own password the first time they sign in. Turn it off to give them an ordinary password that does not expire.
4. Choose **Give own sign-in** and hand over the password Tuvima shows you through a private channel. It is shown once, and the sheet stays open until you choose **Done**.

If you would rather not choose a password, choose **Send an invitation link instead**. Tuvima shows a code, a link and a QR code. The person uses it to choose their own password. An invitation code works once and expires after 7 days. A temporary password also expires after 7 days.

Tuvima never sends email for this. You hand over the code or password yourself.

Their own sign-in opens straight to them, without the profile picker. It stays in the same household, and it is never an administrator. It follows the household's library access, so when you change what the household can open, their sign-in changes too.

Once a person has their own sign-in, their **Sign-in** row shows the email (and *Must change password at first sign-in* while that is still waiting). Open it to:

- **Change email** (administrators): the person signs in with the new address from then on. Their password is unchanged, and an email that already has a sign-in is refused.
- **Set a new password**: the same password field and **Must change it at first sign-in** switch as above. It signs the person out everywhere.
- **Remove sign-in**: the person stays in the household with everything they have saved, and the household's sign-in can still open them.

Changing your own email or password is not done here: your own **Sign-in** row opens **Settings → Security**.

## Change what someone can open

For a person with their own sign-in, the **Household access** group on their profile page lets an administrator manage their access: a household administrator for the people in their own household, and a server administrator for anyone. **Libraries and lanes** opens a sheet where you choose which of Read, Watch, Listen and View they get and which Libraries they can open; you can only give what your household has. **Two-step codes** turns their codes off if they lost their phone, after asking you to confirm. Members never see this group, and a person who signs in from Who's watching has no access of their own to manage.

## Someone from outside your household

A server administrator can invite someone who is not part of an existing household. Choose **Invite someone outside your household** under **Users & Access → Households**. They start a household of their own and become its household administrator, with only what you give them.

## The profile picker

When a household's sign-in is used, Tuvima shows the profile picker. Choose a person to open their profile. A profile with a PIN opens a number pad: tap the digits (or type them on a keyboard or remote), then choose **Continue**. The dots shake if the PIN is wrong, and too many wrong tries pause further attempts for a minute.

On a device that only one person uses, tick **Always open as this person on this device** to open straight to that profile. **Stop always opening as** undoes it.

## Phone backup

A paired phone backs up its photos to one profile. Open **Settings → Administration → Devices**, find the phone in **Paired devices**, and choose a person in its **Whose photos … backs up** list. Only people in the phone's household are offered, and **Not chosen** turns backup off for that phone. The choice does not need a PIN. Photos go to that person's private photo space, and the phone keeps backing up to that profile even when someone else browses on it.

## Shared Library

Each household has one **Shared Library** for photos everyone in the house should see.

- Everyone in a household can open each other's photo space, read only, from the **Household** group in the View scope picker.
- To share a photo, submit it from your photo space to the Shared Library. Nothing is shared until you submit it.
- A household administrator reviews submissions. They can accept or decline each item, and accepted items appear in the household's Shared Library.
- Each household has its own Shared Library. People in one household cannot see another household's Shared Library.

Your private photo space stays private until you choose to submit something.

## What a household administrator can and cannot do

A household administrator can add and remove people in their own household, give them their own sign-ins, change their sign-in email, and set their PINs and passwords, all from each person's page under **Settings → Profiles**. They cannot give an own sign-in to a person that belongs to an administrator. Libraries and lanes and two-step codes are changed under **Household access** on the person's page, which a household administrator can use for their own household. The Households list and the rest of **Settings → Users & Access** stay with a server administrator.

They cannot change another household, act on a server administrator or another household administrator, or turn on administrator access for anyone. Administrator access is always set by a server administrator.

## Next steps

- [Manage sign-in methods, passwords and recovery](account-security.md).
- [Set up secure remote access](remote-access.md) before you let people outside the home connect.
- [Configure an external sign-in provider](external-authentication.md).
