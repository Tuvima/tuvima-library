# Spec: Notifications and delivery (mail server form, push services, webhooks)

> **Status:** In progress · **Version:** 1 · **Last updated:** 2026-11-20 · **Related docs:** [Developer tools spec D-O, WP23, WP24, WP31](developer-tools.md) · [Plugin architecture](plugin-architecture.md) · mock-ups: `engineering/design/mockups/notifications-and-delivery.html` · Question range 180-189

## 0. Plain-English walkthrough

Today Library has a good notification brain and almost no way to reach you. It knows 10 kinds of event (backup failed, storage low, a tool missing, items waiting in Needs Review and so on), lets an administrator choose where each goes, holds messages at night, never sends more than one a minute per kind, and switches off a channel that keeps failing. But the only outside door is email, and the mail server can only be set up by editing a config file, so for a normal person email never works, and password reset by email never works either. A second door (a web address that gets the message, such as ntfy) exists only as a hand-installed plugin file.

This plan keeps the brain and adds the doors:

1. **A mail server form** under Settings > Notifications (pick Gmail, Outlook, iCloud, Fastmail or Other; type an address and an app password; Send test). Password reset uses the same mail server.
2. **An "Add a channel" button** with tiles: Email, Phone alerts (ntfy), Discord, Telegram, Pushover, Slack or Teams, Gotify, Web address (webhook) and Other services (Apprise). Each tile says what it costs and what it needs.
3. **Per-channel privacy**: each channel says in one line what leaves the computer and who can see it, and "Include details" moves from one switch to one per channel (off by default).
4. **A fix**: push channels currently never send unless some administrator has an email address. They will.
5. **More events** worth knowing about (security, drive offline, Library update, encode failed) and an optional **daily summary**.

What stays the same: administrators only, nothing leaves during quiet hours, generic titles by default, one message a minute per kind, five failures switch a channel off, the Dashboard inbox always works, no toasts.

## 1. Goal

A person who runs Library at home can get the few alerts that matter (a backup failed, a drive filled up, a new device signed in) on their phone, by email or in a chat channel they already use, set up in under two minutes from Settings, with a plain statement of what leaves their computer for each one.

## 2. Scope

**In scope**
- Mail server setup form and test (feeds notifications and password reset).
- Add, edit, remove and test channels from Settings; first-party channel types listed in section 4.
- Per-channel "include details" and a per-type "what leaves your computer" line.
- Push channels no longer depend on administrators having an email address.
- New events (section 4.4) and an optional daily summary.
- Reuse of the existing JSON channel runtime so the built-in types are data, not new code paths where possible.

**Out of scope (for this spec)**
- Notifications for non-administrators ("your request is ready", "new episode of a show you follow"). Needs a per-profile design (Q181); this spec leaves room for it.
- Two-way chat (a Discord bot that answers commands). Webhook alerts are one-way (Q186).
- Phone push from a Tuvima-run relay for the native apps, and browser Web Push. Both depend on other threads (remote access plan, Tuvima Social) and are listed as paths, not built (Q184, Q185).
- Remote-access and sign-in work; those have their own planning threads.

## 3. Decisions made (carried in from earlier)

- **D-O (Shy, 2026-10-19):** one notification service; administrator-only events; generic titles by default; channel logins in the secrets store.
- **WP23 decision 5:** password reset is not routed through the notification service (it carries a secret link). It shares only the mail sender. Unchanged.
- **Shy, 2026-11-20:** wants this to cover free push services (Pushover and others), webhooks and a Discord bot. Mail server form is wanted because reset by email does not work without it.
- **Shy, 2026-11-20 (Q180 to Q189, all recommended options, "lock it and build it"):** 180A first release = Email, ntfy, Discord, Slack/Teams, webhook (Telegram, Pushover, Gotify follow as plain additions of the same kind); 181A administrators only now; 182A include-details per channel, default off; 183A Apprise under Advanced; 184A no browser push yet; 185A native-app push relay planned in the Tuvima Social thread; 186A Discord webhook only, bot later as a plugin; 187A push channels send without an administrator email; 188A add all new events, inbox only by default; 189A daily summary.
- **How types are built (Shy asked):** Email is built in (password reset needs it). Every other type is a small JSON channel on the existing declarative plugin runtime, shipped as official, already switched on and approved, and shown as tiles in Add a channel so setup looks the same. Apprise is reached through the plain webhook. A Discord bot would be a separate-program plugin later. Anyone can add their own type as a plugin with the same privacy and switch-off rules.
- Standing rules: privacy first (nothing shared beyond what the person sets up), free sources first, no toasts, dark only, phone parity.

## 4. UI behaviour

Mock-ups A to E. Numbers below match the markers.

### 4.1 Channels group (mock A)
Channels list keeps its current look. Each row: name, one line of state (Working, Not set up, Needs attention) with last sent and how many people it reaches, then Edit and Send test. A failed channel shows the plain reason from the last error (1, 2). A new **Add a channel** button sits under the list (3).

### 4.2 Add a channel picker (mock B)
Tiles, each with cost and effort in words (5): **Email**, **Phone alerts (ntfy)**, **Discord**, **Telegram**, **Pushover** (one-time fee per phone, said plainly), **Slack or Teams**, **Gotify**, **Web address (webhook)**, **Other services (Apprise)** under Advanced (6). Phone: one column, first three then More.

### 4.3 Setup forms (mocks C, D)
- **Email:** provider pills fill server, port and security; username; password written once and never shown again (stored in the secrets store, not core.json); name and address to send as; **Send test to me**. Save needs one successful test (9). Gmail and iCloud show a small help sheet on app passwords.
- **ntfy:** server (ntfy.sh or my own), a suggested random private topic, Send test.
- **Discord, Slack, Teams, Telegram, Pushover, Gotify, webhook:** one or two fields each plus the help sentence for where to find the link or token.
- Every form ends with the same two things: **Include details** switch, off by default (11), and the "what leaves your computer" line (10).
- States: empty (no channels but inbox), saving, test succeeded (inline, green), test failed (inline, plain reason, never an address or a secret), Needs attention (red note and one Fix button), channel off after five failures (existing).
- Phone: forms are full-screen sheets; long provider fields collapse behind Change.

### 4.4 Events
Today's 10 stay. Proposed additions (all start inbox only, tickable like the others): **a new device signed in**, **several failed sign-ins in a row**, **a library drive went offline**, **a Library update is available**, **an encode failed repeatedly**, **remote access was turned on** (once the network plan lands). A **daily summary** (12) batches the quiet kinds into one message at a time the person picks.

### 4.5 What goes where
The matrix gains one column per channel (as now). On a phone it becomes one card per event with a switch per channel (mock E).

## 5. Data / model changes

- Channel instances stored in the data store (type, name, non-secret settings, per-channel include-details); secrets in the secrets store. Instance count is not limited (two ntfy topics, two Discord channels are fine).
- Mail server settings move from a config-file section into the same store; the password-reset sender reads them from there. No migration of old data (pre-release rule).
- Recipient rule split: email needs an administrator with an address; every other channel type sends to its one destination.
- New events and the digest setting added to the one event list and routing table.
- Wire types in Contracts with fixtures updated.

## 6. Work packets

### WP1: Mail server form and shared sender
- **What it does:** Settings form for the mail server with provider presets, write-only password, Send test, and one success required before Save; password reset reads the same settings.
- **Files likely touched:** `NotificationsPanel.razor`, `NotificationsEmailTest.razor`, `AuthSettings.cs`, `SmtpMailSender.cs`, `PasswordResetEmailSender.cs`, `EmailNotificationChannel.cs`, settings endpoints, Contracts and fixtures (to be confirmed)
- **Depends on:** none · **Size:** Standard
- **Done when:** on a fresh data folder a form entry plus Send test delivers an email, and "Forgot password" sends through the same server with no config-file edit.

### WP2: Channel instances and Add a channel picker
- **What it does:** Add, edit, remove and test channels from Settings; picker tiles; per-channel state line; phone sheets.
- **Files likely touched:** `NotificationService.cs`, `INotificationChannel.cs`, notification repository, settings endpoints, `NotificationsPanel.razor` and new setup components, Contracts (to be confirmed)
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** a second Discord channel can be added, tested and routed at 390 px and 1280 px wide.

### WP3: First-party channel types as data
- **What it does:** Ships ntfy, Discord, Slack/Teams, Telegram, Gotify, Pushover and webhook as built-in templates on the existing declarative channel runtime, each with its field list, help sentence and "what leaves" line.
- **Files likely touched:** `PluginNotificationChannels.cs`, declarative channel model, new built-in template files, `samples/plugins/webhook-channel` (to be confirmed)
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** each type's test message arrives in a real account (Local check) and a unit test per template checks the exact request body and that no login appears in an error.

### WP4: Per-channel details switch and recipient fix
- **What it does:** Moves include-details to each channel; lets non-email channels send when no administrator has an address.
- **Files likely touched:** `NotificationService.cs`, `NotificationSettingsAccessor.cs`, panel, tests
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** a test with zero administrator emails still delivers to an ntfy channel and a Discord channel with details off carries only the generic title.

### WP5: New events and daily summary
- **What it does:** Adds the events in 4.4 with sources, and the digest option.
- **Files likely touched:** `LibraryEvents.cs`, `NotificationEventCatalogue.cs`, event sources in the Engine, notification worker (to be confirmed)
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** each new event appears in the table and fires once in a test; the summary sends one message at the chosen time.

### WP6: Docs
- **What it does:** Public guide "Get alerts on your phone" and a privacy page row per channel type, in tuvima-docs.
- **Depends on:** WP3 · **Size:** Mechanical · **Done when:** pages listed in the manifest and the docs build passes.

## 7. Local checks

- Real Gmail app password: form, test, then password reset on SHY-DESKTOP.
- ntfy on a real iPhone and Android phone (public ntfy.sh and a self-hosted one).
- Discord, Telegram and Pushover test messages in real accounts.
- Look: compare Settings > Notifications and the setup forms with the mock-ups at 1024, 1280, 1440 and phone.

## 8. Risks and open questions

- **Cost and privacy of relays.** ntfy.sh, Discord, Telegram and Pushover all see the message text. The "what leaves" line and titles-only default are the answer; there is no way to make a third-party relay private.
- **Topic as a secret on public ntfy.sh.** Anyone who guesses the topic reads the alerts. The form suggests a long random topic and offers an access token or a self-hosted server.
- **Discord/Telegram terms.** One-way alerts through their webhook and bot interfaces are normal use; a two-way bot would need a terms and hosting review (kept out).
- **Pushover is not free.** Included because it is the most reliable; the tile says so. Q180 decides whether it ships first.
- **App passwords.** Gmail and iCloud need them; the help sheet matters more than the form.

### Questions for Shy
All answered; see Decisions made.

## 9. Plain English summary

Library already decides well who should hear about what, but it can barely tell anyone. This plan adds a real mail setup form so email and password reset work, an "Add a channel" button for free phone and chat options (ntfy, Discord, Telegram, Slack, Gotify, webhooks, and Pushover for people who pay once), a clear note on each about what leaves your computer, and a few more useful alerts. Everything stays opt-in, quiet at night, and generic by default.
