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

- You need to be a server administrator or a household administrator. Both look after profiles from **Settings → Profiles**, which only administrators see: choose **Edit** on a person's row to open their page. Server administrators also get **Add household** there, and **Settings → Profiles → Households**, which lists every household and lets you rename or remove one.
- Users (sign-ins) are looked after only by the server administrator, in **Settings → Users and Sign-in**.

## Add a person to the household

1. Open **Settings → Profiles** and choose **Add profile** in the household's list (a server administrator picks the household by using the row in its group).
2. Enter a name. Switch on **Kids profile** for a restricted profile. A kids profile never has administrator access.
3. Optionally set a **PIN**. Anyone who switches into that profile is asked for it. Use 4 to 12 digits. You can change it later from the person's **Profile details**.
4. Save. The person appears in the list. They can use the profile straight away by switching to it, and they do not need a sign-in of their own. A household holds up to eight people; at eight, Tuvima shows the Engine's sentence instead of adding a ninth.

Adult profiles should have a PIN if others can reach the household's sign-in. Tuvima reminds you about this but does not force it.

## Someone's sign-in

A sign-in (an email with a password, a passkey or two-step codes) is only a way in to a person's profile; the person, their access and their roles live in the profile. A profile can have one sign-in, or none: a person without one is opened by signing in as someone else in the household and switching to them.

Only the server administrator adds, changes and removes sign-ins, in **Settings → Users and Sign-in**. A person's **Sign-in** row on their profile page shows their email (or *No sign-in*) and, for a server administrator, links there. Changing a sign-in's email keeps the profile and everything in it. Deleting a sign-in keeps the profile too; a new sign-in can be attached to it later.

Signing in opens the person's own profile, and from there they can switch to any other profile of their household (a profile with a PIN asks for it), never into another household.

## Change what someone can open

A person's access belongs to their profile. On their page, **Access → Libraries and lanes** opens a sheet where you choose which of Read, Watch, Listen and View they get and which Libraries they can open. A household administrator can give only what their own profile has, and only to people in their own household. A server administrator's profile can open everything.

## Roles on a profile

The **Roles** group on a profile page has a **Household administrator** switch. A household administrator looks after the people of their household and what those people can open. The switch is greyed out, with the reason beside it, when the person has no sign-in (a household administrator always has one), is a Kids profile, or is the household's last household administrator (a household always keeps one). A server administrator also sees a **Server administrator** switch for people in the Library's own household. A household administrator cannot change a server administrator's profile.

## The administrator PIN

If a profile that holds an administrator role has a PIN, turn on **Ask for a PIN to open administrator settings** in the **Administrator PIN** group on its page. The setting is on the profile, so it follows the person wherever they sign in. You can change it on your own profile; a server administrator can change it on anyone's.

## Someone from outside your household

A server administrator can add someone who is not part of an existing household. Choose **Add a sign-in** under **Users and Sign-in → Users**, then **A new person**, set their household to **A new household** and give it a name. They start a household of their own and look after it. Once they are in, give them only what they need on their profile under **Settings → Profiles**.

## The profile picker

Signing in opens your own profile. Open the picker from the account menu's **Who's watching?** to switch to another profile of your household, or use the menu's quick list. Choose a person to open their profile; any profile can switch to any other unless it has a PIN. A profile with a PIN opens a number pad: tap the digits (or type them on a keyboard or remote), then choose **Continue**. The dots shake if the PIN is wrong, and too many wrong tries pause further attempts for a minute.

On a device that only one person uses, tick **Always open as this person on this device** to open straight to that profile. **Stop always opening as** undoes it.

## Phone backup

A paired phone backs up its photos to one profile. Open **Settings → Security → Devices**, find the phone in **Paired devices**, and choose a person in its **Whose photos … backs up** list. Only people in the phone's household are offered, and **Not chosen** turns backup off for that phone. The choice does not need a PIN. Photos go to that person's private photo space, and the phone keeps backing up to that profile even when someone else browses on it.

## Shared Library

Each household has one **Shared Library** for photos everyone in the house should see.

- Everyone in a household can open each other's photo space, read only, from the **Household** group in the View scope picker.
- To share a photo, submit it from your photo space to the Shared Library. Nothing is shared until you submit it.
- A household administrator reviews submissions. They can accept or decline each item, and accepted items appear in the household's Shared Library.
- Each household has its own Shared Library. People in one household cannot see another household's Shared Library.

Your private photo space stays private until you choose to submit something.

## Delete a profile

On a person's page choose **Delete profile**. Tuvima lists what goes with it (progress, bookmarks, lists and history), tells you that their sign-in is removed too, and asks what to do with their personal photos: **Move to Shared** (the default) or **Delete**. Library files are never touched. You cannot delete the profile you are using, a household's last household administrator, or (as a household administrator) a server administrator's profile.

## What a household administrator can and cannot do

A household administrator can add and remove people in their own household, set their PINs, choose what they can open and make another person with a sign-in a household administrator, all from each person's page under **Settings → Profiles**. They cannot add, change or delete users (sign-ins); the server administrator does that in **Users and Sign-in**. The Households list and the rest of **Settings → Users and Sign-in** stay with a server administrator.

They cannot change another household or a server administrator's profile, and only a server administrator makes someone a server administrator.

## Adding a second household

With one household the Library can be used without passwords. Before you add another household, set an email and password (or a passkey) for your own sign-in: **Settings → Profiles → Households → Add household** tells you so and has a **Set an email and password** button that opens Security.

## Next steps

- [Manage sign-in methods, passwords and recovery](account-security.md).
- [Set up secure remote access](remote-access.md) before you let people outside the home connect.
- [Configure an external sign-in provider](external-authentication.md).
