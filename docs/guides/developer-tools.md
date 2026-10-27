---
title: "Developer Tools"
description: "The four pages in Settings > Developer: Local AI, Plugins, Integrations and Developer Tools, and the rules they share."
audience: "administrator"
category: "guide"
product_area: "plugins"
tags:
  - "plugins"
  - "integrations"
  - "local-ai"
  - "admin"
status: current
---

# Developer Tools

Settings > Developer holds four pages that all extend or connect the Library: **Local AI**, **Plugins**, **Integrations** and **Developer Tools**. They share one set of answers, so you can learn it once. Only administrators see them, and phones and tablets do not list them in the Settings menu (the pages still open from a mobile browser).

## What every item tells you

Each plugin, application, webhook, backup destination and notification channel shows the same card:

- **Name and kind**, with an on/off switch.
- **Health**, in four words: **Working**, **Needs attention**, **Broken** and **Off**. A row in the Settings menu turns red with "Broken" only when something you switched on keeps failing, never for something that is off, and never after a single bad moment.
- **Allowed to**, in plain words, and **Sends data out**, which says **No** or **Yes, to** the exact places.
- **Last activity** and **last problem**.

## The approval rule

Nothing new switches on until an administrator has seen, in plain words, what it can do and whether anything leaves the machine, and confirmed. This covers plugins, the Local AI features (which are plugins), application keys and webhooks. If an item later asks for more, or points somewhere new, it stops and asks again. Turning something off or taking permissions away never needs approval.

## Plugins

A plugin is an optional add-on. There are four kinds, and the card says which one you are looking at:

| Kind | What it is | Where it runs |
|---|---|---|
| Official | Compiled code that ships with the Library, such as the Local AI features | Inside the Engine, with its trust |
| JSON-only | A description file the Library runs for the plugin: AI steps, channel and guide lists, notification channels and metadata sources | The Library does everything; the plugin has no code |
| Separate program | A program the Library starts and watches, talking to it through a local door | Its own process on this machine |
| Sandboxed | A WebAssembly module with no file, clock or network access except through the Library | A sealed sandbox with memory, time and work limits |

**Trust levels.** An entry in **Available plugins** is **Official** (built by Tuvima), **Verified** (checked by Tuvima) or **Community** (not checked, and it says "not verified"). A plugin you added by hand is **Private**. Verified and Official packages must carry a signature that matches a Tuvima release key built into the Library; with no key built in, they are refused rather than trusted. How a plugin was installed is recorded beside it, so a package can never vouch for itself, and any hand edit to its files makes it private again.

**Blocklist.** The directory can list plugins that must not run, with a reason and optionally the versions. A blocked plugin is switched off with "Blocked: reason" and cannot be switched on.

**Pages.** A plugin can add pages and menu entries (list, grid, guide timeline, form or detail) when you approve "Adds pages and menu entries to the Library". Each page is limited to the audience the plugin declared, and a page you may not see answers as if it did not exist.

See [Use Plugins](using-plugins.md) to manage them and [Build a Plugin](building-a-plugin.md) to make one.

## Local AI

Local AI is the engine room: it runs the models and keeps the machine fair. The AI features that ship with the Library (Smart labelling, Type helper, Series order, Vibe tags, Description insights and Taste summary) are Official plugins, each off until you approve it. The Local AI page has **Status**, **Model and hardware**, **Budget** (the default hourly allowance each plugin gets) and **Usage** (who used which ability in the last 7 days, with Needs attention for a plugin over its allowance). What the AI suggests is marked "Suggested by Local AI" on the item, ranks below every provider, and never replaces a value a person or provider set. See [How the Local AI works](../explanation/how-ai-works.md).

## Integrations

**Integrations** is for software outside the Library: applications with keys, and webhooks. The page matches Plugins: a summary strip (integrations, active, need attention), a search box with **Status** and **Access** filters, **All / Applications / Webhooks** tabs with counts, and a card for each integration. A card shows its kind, a status label (Enabled, Disabled, Needs attention), its access (Read only, Playback control, Library management, Custom access), how many keys are active, the next key expiry and when it was last used. A failing webhook shows "Delivery failed" and the time of the last attempt in red. **Manage** opens the integration; the "..." menu has **Disable** or **Enable**, **Revoke keys** and **Delete**.

**New integration** opens a three-step page: **Application** (Application or Webhook, a name, and for a webhook the address to send events to), **Access** (choose Read only, Playback control, Library management or Custom; permission groups expand to list what they hold; an Access summary says things like "No write access" and "Sensitive permissions excluded"; a webhook chooses **Events** here instead) and **Review and create** (the key's expiry, then Create). Nothing is saved until Create, and Cancel asks "Discard this integration?" if you entered anything. The key is shown once with a Copy button; it cannot be shown again.

- **One event list.** Webhooks, plugin subscriptions and notification routing all read the same list of events, so a new event appears everywhere at once. Notification events carry only a generic title unless "include details" is on.
- **Keys.** A new key suggests an expiry of one year (you can pick 90 days or never). A key nobody has used for 90 days, or one that expires within 14 days, shows **Needs attention**.
- **Webhooks.** A switched-on webhook shows **Needs attention** after 3 failed deliveries in a row and **Broken** after 5. A test send does not count, and the Integrations row in the Settings menu turns red only when one is broken.

## Notifications and backups

The Library has one notification service with a routing table, quiet hours and a limit of one message a minute per channel. Built-in channels are the Dashboard inbox and email; a JSON-only plugin can add more (see the sample in `samples/plugins/webhook-channel`).

Under **Backup and recovery** you choose where backups go: this computer, another folder, a network share, S3-compatible storage or WebDAV. Every copy is the same plain backup file, read back to check it; backups are never encrypted, so keeping them safe is up to you. Backups never include media files. See [Operations and recovery](operations-and-recovery.md#copy-backups-to-other-places).

## Where to go next

- [Use Plugins](using-plugins.md)
- [Build a Plugin](building-a-plugin.md)
- [Security architecture](../architecture/security.md)
