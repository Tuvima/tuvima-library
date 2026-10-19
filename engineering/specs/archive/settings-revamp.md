# Spec: Settings revamp (one look, one Devices page, one attention banner)

> **Status:** Built · **Version:** 1.1 · **Last updated:** 2026-10-19 (built 2026-10-19; locked by Shy; drafted as versions 2 to 31 in the planning thread; v1.1 change note below) · **Related docs:** [Product overview](../../product/overview.md) · [Design system](../../design/design-system.md) · [Ingestion and readiness](../ingestion-and-readiness.md) · [First run and prerequisites](first-run-prerequisites.md) · [Profiles setup redesign](../profiles-setup-redesign.md)

> Group 5 of the 2026-10-18 bug list (bugs 24, 7, 10, 9, 29 and planning item D, with bug 14 folded in as an optional last packet). Checked against the code on main. Where something is inferred, it says so. Nothing is built. The mockup that shows the new look is kept in the planning project's shared files (plans/settings-revamp/mockup.html). Bug 9 (Shy: wasted space, misaligned buttons, off-brand) is answered by the mockup.

> **Change note (v1.1, 2026-10-19, approved by Shy with Developer tools spec v3):** Local AI becomes the engine room and every AI feature becomes a plugin ([Developer tools](../developer-tools.md) D-Q, D-R). Local AI's sub-pages become **Status, Model and hardware, Budget and Usage**; the Features, Vocabulary and Automation sub-pages go (AI features move to the Plugins page, the vibe vocabulary into the vibe tags plugin, schedules into each plugin and Scheduled tasks). Until Developer tools WP30 is built, this spec's packets keep today's Local AI content as it is on the new frame; they do not rebuild Features, Vocabulary or Automation.

## Product walkthrough

**What goes wrong today.** Settings is built from several different page shapes. Most pages open straight into content, some have a row of tabs built one way (Network, Metadata Providers), one has tabs built another way (Users and access), Security is a list of rows, and the two big admin pages are single long components. Each sets its own padding and heading style, so the breadcrumb, the title and the content do not line up from page to page. The tab row that Network and Metadata Providers share is set to scroll sideways, so it grows its own scroll bar even when everything fits (bug 7). Devices sits under Server, was moved there on purpose on 12 Oct, and now looks like a different product (bug 10). Needs Review and missing tools have no common place to announce themselves (bug 29).

**What it feels like after.**
1. Every Settings page has the same frame: the breadcrumb, then the page title with a one-line description, then (only if the page has sections) one row of tabs, then the content in standard cards, all on the same left edge.
2. Pages with sub-pages open them as a tree in the left menu, the same way Library and Ingestion does, and every item keeps its own icon. The sideways tab row (and its stray scroll bar) goes away; tabs remain only for sections inside a single page.
3. Server administrators see an attention banner above the breadcrumb on every Settings page whenever something needs them: items in Needs Review, a missing tool. Each is its own line with one button and an X. Dismissing a line is remembered, so it does not nag; it returns only when something new needs attention (more review items, a different tool missing). The Settings menu also shades entries that need attention: Needs Review in amber, the Library's "waiting" colour, and red only for something that cannot work at all (today that is Local AI while its tools or libraries are missing). Tools and requirements is not shaded, because the tools are optional for most libraries; a missing tool still appears as a notice bubble. The shading stays after the banner is dismissed, so the information is always there but not shouting. Opening Local AI shows a notice at the top that cannot be dismissed while the problem lasts: "Local AI is switched off until the tools and libraries it needs are downloaded", with a Download button. The bubbles do not appear on phones, which are for watching, reading and listening. Nobody but server administrators sees any of it.
4. There is one Devices page, under Personal > Security. Everyone sees their own paired phones, TVs and signed-in browsers and can sign one out. A household administrator also sees their own household's, and a server administrator everyone's. Administrators can filter the list by household and by profile. The "Address to type in the app" card moves to the Profiles overview (item 7). Server no longer has a Devices entry.
5. **Households fold into Profiles.** Profiles becomes an editing page for administrators only: a household administrator edits their own household; a server administrator also gets Add household and a Households tab. Members do not see it; they use Edit profile on their Overview, and everyone switches profile from the top-bar profile menu. the old Administration > Users and access > Households goes away; Applications and Authentication stay in Server. The settings group "Account" is renamed "Personal". See mockup view 9.
7. **A new Overview page and an Activity page, with Profiles kept separate** (Shy). Settings > Personal gets **Overview** as its first entry: the profile header (photo, name, joined date, Edit profile) and your counts, then **Recent activity as a full-width row first**, then a short **Your taste** summary linking to its own page, and a simple **phone icon** that opens an **Apps** sub-page (store links once published, the server address which every person sees, and a QR code a phone can scan to connect). There is no separate My list row: adding to My list shows in Activity as "Added to My list". **Taste is its own page too**, and so is **Activity**, both right below Overview, with the same scope rules: a member sees their own; a server administrator sees more (Mine, a household, one profile, or Everyone, Tautulli style). See mockup views 5, 7 and 8.
6. The "Secure account" reminder is a light red card at the top of the profile menu, above the name, with **Secure account** and **Not now**. It says a password is needed to use the Library away from home; phones and TVs at home keep working without it. After Not now it is gone from the menu and a quiet note appears in Settings > Security.

**What stays the same.** What each page lets you change. The Settings menu groups. The Needs Review page, the missing-tool alert, the Tools and requirements page and the Activity log keep the content their own specs gave them. Settings stays configuration and operational state, never a place to fix an individual item.

**Acceptance criteria**
- **A1.** On every Settings page at desktop width, the breadcrumb, title and first content card share one left edge, measured on a screenshot of at least six pages. (WP0, WP1, WP3)
- **A2.** No Settings page shows a horizontal scroll bar at desktop width, because sub-pages are menu items and not a tab row. (WP2)
- **A3.** Every parent page that has sub-pages (Overview, Profiles, Preferences, Security, System, Library and Ingestion, Network, Streaming, Users and Sign-in, Local AI, Developer Tools) expands as a tree with an icon on every item, opens on its own main page with no duplicate child of the same name (D45), and the same tree is the page selector on a phone. (WP2)
- **A4.** A server administrator with a missing tool and 3 items in Needs Review sees a banner with both lines on every Settings page; a dismissed line stays dismissed after a reload and returns only when something new needs attention. A household administrator or member sees neither. (WP4)
- **A4b.** Needs Review shows amber with a count and Tools and requirements is not shaded; Local AI shows red while a prerequisite is missing and its page shows a notice that cannot be dismissed; the shading stays after the banner is dismissed. (WP3, WP4)
- **A4c.** Notices are separate full-width bubbles with no surrounding bar, never more than three, and are not drawn on a phone. (WP1, WP4)
- **A5.** Server has no Devices entry. Security has a Devices page that shows a member only their own devices, a household administrator their household's, a server administrator all, with household and profile filters for administrators. (WP6)
- **A6.** The the old Administration > Devices entry and route no longer exist and no code for them is left behind. (WP6)
- **A7.** A member does not see Profiles; a household administrator and a server administrator each see exactly the Profiles actions in D29, and the Engine refuses the rest; the Households screen under Server no longer exists. (WP13, WP14)

## 1. Goal

Settings looks and behaves like one place. A person always knows where they are, administrators are told in one spot when something needs them, and devices are found in one page whose contents match the person's rights.

## 2. Scope

**In scope**
- A shared page frame for every Settings page: breadcrumb, title, description, optional tabs, content width and spacing.
- One tab component; removing the stray scroll bar.
- The administrator attention banner: where it sits and how it looks, fed by the Needs Review count and the missing-tool state.
- Moving Devices to Security, scoped by rights; removing the Server entry.
- Folding households into Profiles with role-based views, and updating the underlying permissions first.
- Secure-account reminder in the profile menu.
- Personal Overview (Recent activity strip, taste, Apps sub-page with address and QR code), a separate Activity page and a history record that survives deleting an item.
- A Logs page under System: events, technical log, retention for system logs, scheduled tasks list, log export.

**Out of scope**
- The content of the Needs Review page (ingestion spec WP6), the missing-tool detection and Tools and requirements page (first-run spec), the Activity log and live ingestion page.
- What Network and Devices can do; those change presentation and placement only. Profiles is the exception: D29 changes who may do what, and the Engine rights are updated first.
- Mobile availability rules for each page (which pages are desktop-only) stay as they are.
- Reworded "Tuvima" copy (Group 6), except new text here says "Library".

## 3. Decisions made

- **D1.** One page frame wraps every Settings page, so alignment is fixed once rather than page by page. Reason: Maintenance; today each page sets its own padding.
- **D2.** **Sub-pages are tree items in the Settings menu, not tabs** (Shy). Every page that has sub-pages expands in the left menu like Library and Ingestion does, and each sub-item has its own icon. One tree component serves the whole menu. Tabs are used only for sections inside one page. Reason: Maintenance and consistency (one pattern everywhere); it also removes the tab row that caused bug 7.
- **D3.** The attention notices sit above the breadcrumb (Shy, Q2) as separate rounded bubbles, each the full width of the page content, with no bar or frame around them, at most three at once, most urgent first, in tinted theme colours. **Each line is dismissable and the dismissal is remembered** (Shy); it returns only when something new needs attention. The Settings menu shades entries that need attention: amber (the "waiting" colour) for Needs Review only, red only for something that cannot work, which today is Local AI (Shy). Banner and shading read one shared attention state, so they cannot disagree. The top-bar processing indicator stays blue and green with no amber (ingestion spec D6); amber here only marks Settings entries waiting on an administrator. Only server administrators see it (ingestion spec D5), because only they resolve Needs Review.
- **D4.** This plan builds the banner's frame and look. Its content comes from the Needs Review count and missing-tool state the other two specs provide; it shows nothing for a source that is not built yet. Reason: avoids two threads each building a banner.
- **D5.** **Reverses 12 Oct decision D9 of Settings and Profiles** (Devices under the old Administration > Devices). Devices now lives in Personal > Security (Shy, 2026-10-19, planning item D). The reason D9 gave, that devices are an admin task, no longer holds: people should see and sign out their own devices.
- **D6.** Devices scoping (Shy, 2026-10-19): a member sees their own; a household administrator sees their own household's; a server administrator sees everything. A member cannot sign out another person's device; only a server administrator, or a household administrator for their own household's people, can. The Engine's permission check decides, not the Dashboard (the Dashboard only chooses what to show).
- **D7.** No redirect and no leftover code (Shy): the old Administration > Devices entry and route are removed and the page lives in Security. Reason: Maintenance.
- **D9.** **Three kinds of alert, one frame** (Shy): (a) dismissable, remembered banner lines for server administrators, global; (b) permanent notices at the top of one page that cannot be dismissed while the problem lasts (Local AI is the first; Network and Remote Access shows one while remote access is not set up); (c) menu shading. The frame provides slots for (a) and (b) so any page can use them later, globally or per page.
- **D10.** **Secure account is only about using the Library away from home** (Shy). A phone or TV at home works without a password. The reminder and Settings wording say so. Today's Security text says the password is for "your phone, TV and other devices at home", the opposite; WP7 changes it and checks whether anything actually blocks pairing at home.
- **D11.** **A new Personal > Overview page shows the signed-in profile, and Profiles stays a separate page** (Shy, inspired by Plex's profile page, not copied). Layout: header and counts; Recent activity as the first full-width row; Your taste (editable; the editing layout is a placeholder to be designed later); an Apps icon opening the Apps sub-page (D24). Profiles keeps the household list with add and edit.
- **D12.** **Banner dismissals are remembered with the profile** (Shy, Q8), so they follow the person to any browser. After "Not now" on Secure account there is no avatar dot (Shy, Q9).
- **D13.** **Recent activity, not "recently watched"** (Shy): the strip covers watching, listening and reading. Every tile in the strip has the same height, and each keeps its natural shape (Shy): books and comics portrait, episodes and movies landscape, music and audiobooks square, so portrait tiles are narrower and landscape ones wider. Music is shown as a **music session** (D14), not one tile per song. There is no separate My list row: adding a title to My list appears in the feed as "Added to My list". No star ratings or likes appear anywhere on this page.
- **D14.** **Music appears as one "Music" tile per sitting** (Shy). One tile per session: a square 3 by 3 collage of 9 album covers picked at random from that session, a different 9 on each page load, captioned "1 h 05 m · 50 songs" (see D17). Shuffled listening to 50 albums is therefore one tile, not fifty. The full page shows the same session with a **Show songs** button. What the Engine records today (read from the data structure, to be confirmed by Claude Code): per-person playback sessions with start, end, kind (read, watch, listen) and time played, and per-song play counts with last played time. A music session ends after 30 minutes of silence, or as soon as the person starts a different activity such as watching a movie (Shy, Q14). If songs cannot be tied to a session from what is recorded, the build adds that record; it does not guess.
- **D15.** **Activity is its own Personal page, and administrators get a wider scope** (Shy, 2026-10-19). A member opens Activity and sees their own. A server administrator sees the same page with a **Scope** filter: Me, a household, one profile, or Everyone, plus a Person column, and no switching into anyone's profile. Opening a scope that includes other people is logged (D22), one entry per viewing session per scope, not one per page scroll. A household administrator sees the same page with the scope limited to their own household (Shy, Q22, for the safety reason in D32): Me, a profile in their household, or the household. The Engine refuses anything wider.
- **D16.** **Activity is a page of its own, not a tab** (Shy). It sits in Settings > Personal under Overview. The Overview strip links to it with See all. Because Settings is hidden from Kids profiles, the same page also opens from the profile menu ("My profile" for the Overview, "My activity" for Activity), so Kids and phone users can reach their own. It replaces My Library's History, so there is one list.
- **D17.** **Recent activity tiles show artwork, a progress bar where progress is tracked, and one line saying what you did** (Shy). The caption is a single non-wrapping line that starts with a verb so it cannot be read as the length of the item: "Watched 52 m", "Read 1 h 20 m", "Listened 3 h 12 m" (audiobooks), "Played 1 h 05 m" (music; the song count lives in the hover). Movies, episodes, books and audiobooks show a thin progress bar along the bottom of the artwork; a finished item shows a check instead of a bar. Music has no bar. Titles are not printed; they appear in a richer hover card (D18). The full Recent activity page in My Library gives the real details (title, episode, date, time). Reason: long titles, especially audiobooks, wrap badly and make tiles uneven.
- **D18.** **A richer hover card, built on the app's existing popover** (Shy). On mouseover or keyboard focus a tile shows a small card: a larger image, the title, episode or chapter, when, how long, and for music how many songs from how many albums. It uses the app's existing popover control (`AppPopover`, to be confirmed by Claude Code) so every page that needs a hover card uses the same one, instead of a bare tooltip.
- **D19.** **Personal pages work on a phone; administrator pages stay summary-only** (Shy). Overview, Activity, Taste, Security, Playback, Listening and Reading are consumption-side pages and must work on a phone: counts in two columns, the activity strip scrolls sideways, buttons full width, and the same data and wording as desktop. A phone has no hover, so a tap opens the item (matching the locked rule that phones have no hover previews); the song count and other detail are on the Recent activity page. Notices, menu shading, Users and access, Network and the rest of the administrator pages keep today's phone rules (summary or desktop-only). Kids profiles do not reach Settings today, so they do not see Overview; they keep My Library and Recent activity.
- **D20.** **The Overview, the Activity page and the music sessions are built in this spec** as the last wave (Shy, Q11).
- **D21.** **Looking at photos is not tracked as activity** (Shy). The View (photos) area is removed from Recent activity and its counts; it is too new and narrow to include. Reading, watching and listening are the tracked activities.
- **D22.** **Logs and history have their own retention, set on a new Logs page under Server** (Shy, Q17). Three kinds:
  - **Personal activity** (watched, listened, read, added to My list) and the **administrator-view log**: kept forever, never pruned automatically (D25). History is removed only by deliberate manual action by a server administrator (D32).
  - **System activity log** (ingestion and system events): 90 days by default (today 60, in `config/maintenance.json`), plus a size limit.
  - **Engine log files** (technical files for bug reports): 90 days by default (Q18), plus a total size limit (today 14 daily files of up to 50 MB each).
  Pruning of system logs is silent and perpetual, so there is no notification for it. Instead the **Tools and requirements** page gets a **Scheduled tasks** section listing every recurring task with when it last ran and when it runs next.
  - **Personal activity** (what people watched, listened to and read) and the **administrator-view log**: kept forever by default. A setting offers "keep 12 months" and a size limit; when a limit is on, a scheduled task removes the oldest entries once it is exceeded.
  - **System activity log** (the readable events shown on the Logs page): 90 days by default (today 60, in `config/maintenance.json`), plus a size limit.
  - **Engine log files** (the technical files used for bug reports): 90 days by default, plus a total size limit (today 14 daily files of up to 50 MB each).
  Pruning is silent and perpetual, so there is no notification for it: whatever is older than the limit simply falls away. Instead the **Tools and requirements** page gets a **Scheduled tasks** section listing every recurring task with when it last ran and when it runs next (pruning, backups, refreshes), so nothing comes due unseen.
- **D23.** **Exporting logs for a bug report** (Shy). A person can export exactly the log details they want: choose a time range, a level (errors only, warnings and above, everything), an area (ingestion, playback, network and so on) or one item or run, see how many lines and how large the file will be, and download one file. By default the export hides private details: keys, tokens, sign-in details, and file and folder names (a switch turns the file-name hiding off). Server administrators only, since logs can mention other people. Reason: Privacy (a bug report should not leak anything the person did not mean to share).
- **D24.** **Apps sub-page** (Shy): a simple icon on the Overview opens a sub-page with the app store links (shown once published), the server address, and a QR code. **Every person sees the server address, not only administrators** (changes today's administrator-only card, because the address is not secret). The QR code contains only the server address, never a key or sign-in, so scanning it fills in the app's server field and the person still signs in as normal. Once remote access is settled, the same page also hands the apps their outside address so they connect at home and away. The QR code needs a small tool or library; its licence must be AGPL-compatible before use.
- **D25.** **Activity keeps its own record of the item, so history survives deleting it** (Shy). Each activity entry stores the title, kind, year, episode label and a small picture as they were at the time. If a movie you watched two years ago is later deleted from the server, Recent activity still shows what it was, when and for how long. Today, history rows are tied to the media file and are deleted with it (found in the data structure, to be confirmed by Claude Code); that must change. Pre-release data may be reset.
- **D26.** **No Manage profiles button on the Overview, and avatars in the profile menu** (Shy). Profiles is already one click away in the left menu and the top menu. The top-bar profile menu shows each person's avatar beside their name so switching is friendlier. "My profile" opens the Overview from that menu (Q19, yes).
- **D27.** **Recent activity groups by title, is searchable, and shows the attempts** (Shy, inspired by Tautulli). A search box and filters (type, date range) sit above the list. Typing "Tremors" shows one entry for Tremors that expands to its sessions: "Started Sep 6, 30 m, stopped at 31% · Sep 9, 12 m, stopped at 43% · Sep 13, finished, 1 h 36 m in total". Each session shows date and time, how long, how far (percent reached), outcome (finished, stopped), and the device it was on. A switch shows every session as a flat list instead. What the Engine records today supports this (session start, end, time played, progress, device); anything it does not record is left out. No IP addresses or other sign-in details appear.
- **D28.** **Devices show a type icon** (Shy): Android, Apple, TV (Roku and others), browser, and a generic one. The type comes from what the device reported when it paired; an unknown device gets the generic icon.
- **D29.** **Households fold into Profiles; one build, permissions first** (Shy, Q20). The page keeps the name **Profiles** and is **an editing page for administrators only** (Shy, 2026-10-19): members do not see it at all. Switching profile happens only in the top-bar profile menu (D26), never on this page. A household administrator sees and can edit the people in their own household and add profiles to it. A server administrator also gets **Add household**, which opens a **Households** tab beside the profile list. A member edits only their own name, picture and details from **Edit profile** on their Overview. To manage another person, an administrator opens their page here; to see what that person sees, they switch to that profile from the profile menu. The old the old Administration > Users and access > Households screen is retired and its content lives here. Server keeps only what runs the server: Applications, the server's sign-in rules (Authentication), Network and the rest. The Engine, not the screen, decides who may do what, so rights are updated first (WP13).
- **D30.** **The settings group "Account" is renamed "Personal"** (Shy). Server is for running the server; Personal is for people and what they do and own. The full menu is in D35.
- **D31.** **Recent activity reuses the library pages' timeline pieces** (Shy). The watch and listen pages already have a date timeline, grouping control, filter bar and active-filter chips (`AppTimelineList`, `AppTimelineNavigator`, `AppTimelineGroupingControl`, `AppFilterBar`, `AppActiveFilterSummary`, `PlaybackTimelineMetaRow`, to be confirmed by Claude Code); the activity page uses those and extends them where needed rather than building new ones. Filters: text search (title, person in the administrator view, device), type, date range, outcome (finished or not), device, and grouping (by day, week, month; by title). The result can be exported as a text file (CSV) of exactly what the filters show (D27). Exporting someone else's activity is a server-administrator action and is logged.
- **D32.** **Only a server administrator can delete activity history** (Shy, Q21), because it is a safety control as well as data: if a child watched something they should not have, the administrator needs to be able to see it, and nobody should be able to wipe it. It sits under a "..." menu as **Delete history...**, shows how many entries will go, asks for the word DELETE, and is logged. Members and household administrators have no delete control.
- **D33.** **Activity has the same timeline scroller as the View (photos) pages** (Shy). A slim date rail beside the list (years, expanding to months for the year you are in, with counts) lets you jump through years of activity quickly, and on a phone it becomes the same "Jump to period" picker. It is the existing component (`AppTimelineNavigator`, used by the View timeline, to be confirmed by Claude Code), fed with activity counts instead of photo counts, so the Activity page is nearly the same page showing activity in place of pictures and videos. It follows the active filters and scope: the counts on the rail change with them.
- **D34.** **Taste is its own Personal page, with the same scope rules as Activity** (Shy). A member sees and edits their own taste. A household administrator sees their own household's (a scope of Me, a profile in the household, or the household). A server administrator sees everyone's, with the same Scope filter. Viewing taste for other people is logged like Activity (D15). Other people's taste is read-only (Q23). The Overview keeps only a short summary row linking here. The page's layout is not designed yet; this spec delivers a working placeholder (what the Library has learned, in plain groups such as genres and moods, each removable, plus an add control) and the scope rules, and a later design replaces the layout without changing who sees what. The existing taste and vibe data is reused (to be confirmed by Claude Code).
- **D35.** **The Settings menu hierarchy** (Shy; revised 2026-10-19). Every page with sub-pages is an expandable tree item and every item has its own icon. "Administration" is renamed **Server** (Shy, Q26) and Library and Ingestion is the first page of Server. Groups: Personal, Server (how the library runs), Manage (people and upkeep, Q33), Developer (D48). (server) means server administrators only, (admin) server and household administrators.
  - **Personal**
    - Overview (person), which is the summary and the page you land on; sub-pages Activity (history) and Taste (favourite)
    - Profiles (group) (admin), opens on the people list (viewing profiles: limits, preferences, devices); sub-page Households (house) (server). Accounts and sign-in live in Server (D46).
    - Preferences (tune), which is itself the summary page; sub-pages Playback (play), Listening (headphones), Reading (book). No separate Summary page (Shy).
    - Security (lock), opens on Sign-in (password, email, extra protection); sub-page Devices (devices)
  - **Server** (server)
    - Library and Ingestion (video library), opens on one summary page that shows library counts and live ingestion (D37); sub-pages Needs Review (review, amber count), Libraries (folders), Metadata Providers (hub)
    - Network and Remote Access (wifi), opens on connection status; sub-pages Local Network, Remote Access, Advanced (Streaming moved, D41)
    - Streaming (stream), renamed from Playback and Delivery (Q29), opens on Limits and Quality; sub-pages Hardware Acceleration, Optimized Copies, Diagnostics (D41)
  - **Manage** (server)
    - Users and Sign-in (person shield), opens on Users (accounts, roles, invitations); sub-page Sign-in Methods (password rules, extra protection, later OpenID Connect / SSO)
    - System (computer), opens on a resources summary (D39); sub-pages Tools and requirements (build), Logs (list) (D38), Backup and recovery (backup)
  - **Developer** (server, renamed from Advanced; Shy)
    - Local AI (memory), opens on Status; sub-pages Model and hardware, Budget, Usage (D43, as changed in v1.1)
    - Integrations (link icon), credentials and webhooks that let outside software use the Library, one page
    - Plugins (extension icon), one page (D44)
    - Developer Tools (construction), opens on Provider Tester; sub-page Enrichment Tester
  Removed: System Overview (its parts move, D37 and D39), Recently Added and the Activity log as menu entries (D37, D38), Logs (merged into Logs), the empty "Privacy and Data" placeholder, the old Administration > Devices, Users and access > Households, and Settings > Review. A parent shows the shading of its most urgent child (D3).
- **D45.** **One rule for every menu item: the top item is the page you land on** (Shy). Clicking a parent opens its main page (the overview, or the first page it already had, such as Sign-in for Security). Sub-pages never repeat it: there is no "Overview", "Live ingestion" or "Users" child that duplicates the parent. The tree shows the parent as selected with its sub-pages below. Reason: Uniformity; one predictable behaviour everywhere.
- **D46.** **Accounts are a server matter; profiles are personal** (Shy, Q31, Q32). "Access" is replaced by **Users and Sign-in** (accounts, roles, invitations, sign-in methods, the place where OpenID Connect / SSO will be added) and **Integrations** (the old Applications: credentials and webhooks for other software, not the phone and TV apps). Personal > Profiles keeps only viewing profiles. Reason: Extensibility; SSO and identity providers attach to accounts.
- **D47.** **A Manage group holds people and upkeep** (Shy, Q33; Plex's Manage as a reference, not a copy). Server = Library and Ingestion, Network and Remote Access, Streaming. Manage = Users and Sign-in, System (with Tools and requirements, Logs, Backup). They stay separate pages; only the menu grouping is shared. There is no Status group: the summary pages and attention bubbles already do that job.
- **D48.** **Developer group** (Shy): Advanced is renamed **Developer** and holds Local AI, Integrations, Plugins and Developer Tools, because they all extend or connect the Library. Plugins add abilities inside the server; Integrations bring outside software in. Each has its own icon. Whether outside software should eventually connect through plugins is a question for the plugin review (planning item J); until then Integrations keeps today's credentials and webhooks unchanged.
- **D36.** **Playback, Listening and Reading become one Preferences page with a summary and three sub-pages** (Shy). A short Summary page lists your current choices at a glance with a link into each. It functions the same way as today for each preference, and administrators still edit other people's preferences from the person's page (D3 of Settings and Profiles). The page is its own summary, with no extra Summary sub-page (Shy), and the name is **Preferences** because "Settings" is too vague and repeats the area name (Shy, Q24).
- **D37.** **Library and Ingestion opens on one summary page that replaces the System Overview's library parts** (Shy). There is no separate "Live ingestion" sub-page: the summary page is where live ingestion shows. The Local AI setup banner on this page is removed, because the red menu item and the attention bubbles already say it. One page shows the library summary (counts by type, health), the live ingestion (the rotating Now panel and the Added summary from the ingestion spec), and recent activity and recently added, with live updates. The full Recently Added list opens from that page's "View all" and is not a menu item. This changes the **locked ingestion spec**, which defines the live page, the Activity log and the Overview's activity table separately (see section 8).
- **D38.** **Activity log and Logs become one page called Logs, under System** (Shy's instinct that they are the same thing; recommendation). One page with three parts: **Events** (the readable system and ingestion events, searchable and filterable; this is the Activity log), **Technical log** (the engine's log files, for bug reports), and **Retention and export** (D22, D23). The live page's recent activity links to Logs already filtered to ingestion. Naming it Logs also stops it being confused with the personal **Activity** page.
- **D39.** **System opens on a resources summary** (Shy): processor load, memory in use, disk space for the library and the data store, Engine version and how long it has been running, all local. Whether the Engine reports these today is to be confirmed by Claude Code; if not, a small reading is added (WP17). Tools and requirements, Logs and Backup and recovery are its sub-pages.
- **D40.** **Menu icons line up in fixed columns** (Shy). Every row, parent or not, has the same slots: chevron (blank for a page with no children), icon, label, badge. A child row indents by one fixed step, so all icons at one level share a left edge and all labels at one level share a left edge, top to bottom of the menu. One tree component draws every item, so no page can drift.
- **D41.** **The Server pages are regrouped by the question the administrator is asking** (Shy: this is a settings overhaul). Nothing a page can do is removed; things move to where they make sense:
  - *How do people reach the server?* **Network and Remote Access**: opens on connection status, then Local Network, Remote Access (HTTPS proxy, Tailscale), Advanced (reset). Streaming limits leave Network.
  - *How is video and audio sent and prepared?* **Streaming** (renamed from Playback and Delivery, Q29): Limits and Quality (concurrent remote streams, remote quality, upload capacity, moved from Network > Streaming), Hardware Acceleration (moved from Network > Streaming), Optimized Copies (scheduling, storage and active jobs, which the code calls variants), Diagnostics.
  - *Is the server healthy?* **System**: resources summary (the "Server information" from Network's Overview moves here, D39), Tools and requirements (the "Playback tools and services" status from Delivery moves here, as the first-run spec decided), Logs, Backup and recovery.
- **D42.** **Scheduled work stays on the page it belongs to, and one list shows all of it.** Streaming's scheduling, enrichment refresh, Local AI automation, backup and the library sweeps keep their settings where they are, and the Scheduled tasks section on Tools and requirements (D22) lists every recurring task with last and next run. Reason: Maintenance; no single "schedules" dumping page.
- **D43.** **Local AI keeps AI settings only.** Its Models and Runtime sub-page content (files and download state) moves to Tools and requirements, as the first-run spec decided; Local AI keeps Features, Vocabulary and Automation. *(v1.1: superseded by Developer tools D-Q; Local AI becomes Status, Model and hardware, Budget and Usage, and AI features move to Plugins.)*
- **D44.** **Plugins is not redesigned here** (planning item J, the plugin review, comes later). Its sub-pages fold into one page (Q30) so the menu is not cluttered; its content is untouched.
- **D8.** Settings stays configuration and operational state (repo rule). Nothing in this plan adds a correction workspace.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | Breadcrumb, title, description, optional tabs, cards, all on one left edge |
| Server administrator, something needs attention | Banner above the breadcrumb: "14 items need review" and "FFmpeg is missing", each with a button and an X. X hides that line and remembers it; the menu entry stays shaded |
| Phone | No banner and no menu shading; Settings on a phone stays consumption-only as today |
| Network, remote access not set up | Permanent notice at the top: remote access is off, with a Set up button; clears once it is set up |
| Local AI, prerequisite missing | Permanent notice at the top of the page, not dismissable: switched off until the tools and libraries are downloaded, with a Download button |
| Dismissed line, something new arrives | The line returns with the new count |
| Settings menu, item needs attention | Entry is shaded: Needs Review amber with a count, Local AI red with a short label |
| Nothing needs attention | No banner, no empty space where it was |
| Household administrator or member | No banner, no hint that review exists |
| Loading | Frame draws at once; banner appears when counts arrive without moving the page down more than once |
| Engine unreachable | Existing "state could not be loaded" notice inside the frame; no banner |
| Phone, tabs | Tabs scroll sideways with no bar |
| Devices, member | Their own paired devices and signed-in browsers; Sign out on each |
| Devices, household administrator | Their own plus their household's, with a profile filter |
| Devices, server administrator, filters | Household and profile filters above the lists, both default to All |
| Devices, server administrator | Everyone's, plus nothing about connecting an app (that lives on the Profiles overview) |
| Devices, nothing paired | Existing empty message |

## 5. Data / model changes

Banner dismissals are remembered with the profile (D12). Otherwise none to existing data. The banner reads the review count Settings already tracks and the tool state from the first-run spec. The Devices page reuses the existing paired-device and session lists; whether the household-wide and server-wide scopes need a new Engine query is to be confirmed by Claude Code in WP6.

## 6. Work packets

### WP0 — Look first
- **What it does:** Before changing anything, take a screenshot of each Settings page at desktop width, record where the breadcrumb, title and content edges sit, and list the exact cause of each misalignment and of the stray scroll bar (the tab row's sideways scroll is the suspect for bug 7). Report only; no code change.
- **Files likely touched:** none (report under `.tmp/`)
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** a one-page report names, per page, the cause of any misalignment; Shy can compare it to this plan before WP1 starts.

### WP1 — One page frame
- **What it does:** Builds a shared frame component (breadcrumb, title and description, tab slot, banner slot, content width and spacing) and puts every Settings page inside it. Pages stop setting their own outer padding.
- **Files likely touched:** `Components/Pages/Settings.razor` and its CSS, a new frame component, each page's outer wrapper in `Components/Settings/` (to be confirmed by Claude Code)
- **Depends on:** WP0
- **Size:** Complex
- **Done when:** a screenshot comparison of at least six pages shows the same left edge for breadcrumb, title and first card (A1); existing Settings tests pass.

### WP2 — One menu tree
- **What it does:** One tree component for the whole Settings menu (D35): expandable parents, an icon on every item, the active item and its parent highlighted, the attention shading from D3, and the same tree as the page selector on a phone. Replaces the sideways tab row and the second tab component, so the stray scroll bar (bug 7) goes. Moves Network, Metadata Providers, Local AI, Plugins, Playback and Delivery and Access sub-pages into the tree.
- **Files likely touched:** `Models/ViewDTOs/SettingsNav.cs`, `Components/Layout` menu components, `Components/Shared/SettingsSubsectionNav.razor` (retired), `Components/Pages/Settings.razor`, `UsersAccessSettingsTab.razor` and CSS
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** every parent in D35 expands with icons on all items; no tab row remains for sub-pages; no scroll bar at desktop width; the phone page selector lists the same tree; existing routes for pages that remain keep working.

### WP3 — Page-by-page tidy
- **What it does:** Brings the remaining pages onto the frame's cards and headings: Overview, Libraries, Providers, Delivery, Local AI, Plugins, Backup and recovery, Profiles, Security, Privacy. Adds the menu shading for entries that need attention.
- **Files likely touched:** the page components in `Components/Settings/` (to be confirmed by Claude Code)
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** the six-page screenshot check from WP1 is repeated for all pages and passes.

### WP4 — Administrator banner
- **What it does:** Builds the full-width banner in the frame's slot (not drawn on phones) and the permanent page-notice slot with the Local AI notice as its first user; one dismissable line each for Needs Review items and a missing tool; dismissals remembered (Q8) and cleared when something new needs attention; hidden for everyone but server administrators and when nothing is pending. Feeds the same attention state to the menu shading.
- **Files likely touched:** a new banner component, `Components/Pages/Settings.razor` (already tracks the review count), the tool-state source from the first-run spec
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** the three-person check in A4 passes: server administrator sees both lines, household administrator and member see nothing; a test covers each.

### WP5 — Applications and Authentication on the frame
- **What it does:** With the Households screen retired (D29), the two remaining Server pages that came from Users and access, Applications and Authentication, move onto the shared frame and appear as Access sub-pages in the menu tree (D35).
- **Files likely touched:** `ManagedAccessApplications.razor`, `SecurityTab.razor`, `SettingsNav.cs`
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** both pages use the frame; no Households entry remains under Server.

### WP6 — Devices in Security
- **What it does:** Moves paired devices and signed-in sessions to a Devices page under Security, scoped by rights (D6). Removes the Server entry and route with no redirect (D7). Adds household and profile filters for administrators, and a device-type icon on every row (D28). The "Connect an app" card is not part of this page; it moves to the Profiles overview (WP8).
- **Files likely touched:** `Components/Settings/DevicesTab.razor`, `PairedDevicesSection.razor`, `SignedInSessionsSection.razor`, `AppsAndDevicesPanel.razor`, `SecuritySettingsPage.razor`, `Models/ViewDTOs/SettingsNav.cs`; Engine query for household and server scope if missing
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** the member / household administrator / server administrator check in A5 passes; the old route and menu entry are gone (A6); Settings and Profiles docs no longer say the old Administration > Devices.

### WP7 — Secure account reminder
- **What it does:** Fixes the wording so Secure account is about away-from-home use only (D10), and checks that nothing blocks pairing a phone or TV at home (if something does, it reports to Shy before changing it). Replaces today's menu item and avatar dot with a light red card at the top of the profile menu, above the name (Secure account, Not now). Not now is remembered per account; afterwards the menu is clean and Settings > Security shows a quiet note.
- **Files likely touched:** `Components/Navigation/TopNavAccountMenu.razor`, `Services/Navigation/SecureAccountPrompt.cs`, `SecuritySettingsPage.razor`
- **Depends on:** none
- **Size:** Standard
- **Done when:** the prompt shows above the name, dismisses, stays dismissed, and the Security note appears; this refines Profiles setup redesign D5 (today: menu item, avatar dot, Security card). The dot goes (Q9).

### WP8 — Account overview page
- **What it does:** Adds Personal > Overview as the first Personal entry. It has: header and counts, a full-width Recent activity strip, Your taste with an Edit button (the edit screen is a placeholder for now), and an Apps icon. Also the Apps sub-page (D24): store-link slots, the server address for everyone, and the QR code. Opens also from the profile menu as "My profile" (Q19). The Profiles page and its household list stay as they are apart from the frame. Counts and taste come from data the Engine already holds; anything it cannot supply is left out, not faked.
- **Files likely touched:** a new overview component and Apps sub-page, `Models/ViewDTOs/SettingsNav.cs`, `AppsAndDevicesPanel.razor` (its content moves), `Components/Navigation/TopNavAccountMenu.razor` (avatars beside each profile, D26), a QR code library (licence check first) (to be confirmed by Claude Code)
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** the Overview tab matches the mockup for a member and for a server administrator; the hover card shows the details in D18 and a tap on a phone opens the item; usable at phone width (D19); scanning the QR code on a phone fills the server address into the app; every signed-in person can see the address.

### WP9 — Activity page and history record
- **What it does:** The Activity page (Settings > Personal, below Overview): a date-grouped timeline with the View-style date rail (D33) of everything watched, listened to, read and added to My list, filterable by type, with search, filters and per-title grouping (D27), the music session rule (D14) manual deletion (D22) and text export (D31). Activity entries keep their own snapshot of the item so deleted items still show (D25). Photos and the View area are not tracked (D21). Replaces My Library's History; "My activity" opens it from the profile menu. For a server administrator it adds the Scope filter (Me, household, profile, Everyone) and a Person column (D15), and logs opening a scope that includes other people.
- **Files likely touched:** `Components/Pages/MyLibrary.razor` history shelf, the activity storage and query, the playback and My list write paths, the person's profile detail page (to be confirmed by Claude Code)
- **Depends on:** WP8
- **Size:** Complex
- **Done when:** a member sees only their own entries; a server administrator can switch scope to a household, a profile or Everyone and each view shows the person on every row; searching "Tremors" shows one entry that expands to its separate sessions with date, time, percent reached and device; deleting a watched movie from the library leaves its entry readable in Recent activity; adding a title to My list adds an entry; an hour of shuffled music shows as one tile with a 9-cover collage that changes on reload; filters work; an administrator deleting history before a date removes only those entries, and members have no delete control.

### WP15 — Taste page
- **What it does:** A Personal > Taste page below Activity: your taste in plain groups with remove and add, a placeholder layout (D34), and for administrators the same Scope filter as Activity (household administrators limited to their household), read-only for other people, with viewing logged. The Overview row links to it.
- **Files likely touched:** a new taste page, `SettingsNav.cs`, the Overview component, the existing taste and vibe data and its Engine reads (to be confirmed by Claude Code)
- **Depends on:** WP8, WP13
- **Size:** Standard
- **Done when:** a member sees and edits only their own taste; a household administrator can switch scope within their household and cannot edit others; a server administrator can view anyone's; the Engine refuses a wider scope for a household administrator; viewing another person's taste writes a log entry.

### WP10 — Administrator-view log
- **What it does:** Writes an entry each time a server administrator views another person's recent activity (who, whose, when), kept forever (D22).
- **Files likely touched:** the existing system activity or audit storage (to be confirmed by Claude Code), the administrator activity view from WP9
- **Depends on:** WP9
- **Size:** Standard
- **Done when:** viewing someone's activity creates one entry naming both people; a test covers it.

### WP11 — Logs page, retention and scheduled tasks
- **What it does:** The Logs page under System (D38) with its three parts (Events, Technical log, Retention and export) and the retention rules in D22 (system activity 90 days and a size limit; Engine log files 90 days and a size limit; personal activity kept forever with manual clearing), the scheduled pruning task for each system log, and a **Scheduled tasks** section on Tools and requirements listing every recurring task with last and next run.
- **Files likely touched:** `config/maintenance.json`, the Engine log setup in `src/MediaEngine.Api/Program.cs`, the maintenance background services, a new settings section, the Tools and requirements page from the first-run spec (to be confirmed by Claude Code)
- **Depends on:** WP1 and the Tools and requirements page from the first-run spec
- **Size:** Complex
- **Done when:** changing a limit changes what the next prune removes; with a size limit on, the oldest entries go first until under the limit; the Scheduled tasks list shows next-run times; no notification is raised for pruning.

### WP12 — Log export for bug reports
- **What it does:** The export in D23, as part of the Logs page: filters, a preview of line count and size, one downloaded file, private details hidden by default.
- **Files likely touched:** a new Engine export action and a Settings page or section (to be confirmed by Claude Code)
- **Depends on:** WP11
- **Size:** Standard
- **Done when:** exporting "errors from the last 24 hours, ingestion only" produces one file with only those lines; an API key placed in a test log line does not appear in the export; non-administrators cannot export.

### WP13 — Permissions groundwork (first)
- **What it does:** Brings the Engine's rights in line with D29 and D15 before any screen is built: members switch only within their household; household administrators edit people in their own household only; server administrators create, rename and remove households and read other people's activity (logged); the existing rule that at least one usable administrator always remains is kept. Tests cover each role against each action, including attempts that must be refused.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/` and `Services/Security/`, `src/MediaEngine.Contracts/Authentication/`, matching tests (to be confirmed by Claude Code)
- **Depends on:** none
- **Size:** Complex (independent review required; this is access and security work)
- **Done when:** a test per role and action passes, including refusals; no existing access test is weakened.

### WP14 — Profiles page for every role, with Households tab
- **What it does:** The Profiles page of D29: profile list with Edit (household administrator for their household, server administrator for all), Add profile, and for a server administrator Add household and a Households tab. Members do not see the page (they use Edit profile on their Overview). No Switch here. The person page keeps preferences, access, sign-in and devices, plus a View activity link that opens the Activity page already filtered to that person (D15). Content from the retired Households screen is moved here, and that screen is removed.
- **Files likely touched:** `ProfilesTab.razor`, `ProfileDetailPage.razor`, `ManagedAccessUsers.razor` (retired), `SettingsNav.cs`
- **Depends on:** WP1, WP13
- **Size:** Complex
- **Done when:** a member does not see Profiles in the menu and the Engine refuses its actions; a household administrator sees and edits their own household only; a server administrator also sees Add household and the Households tab; the old Households route and screen are gone with no redirect.

### WP16 — Library and Ingestion landing page
- **What it does:** Makes Library and Ingestion open on one live page that combines the library summary taken from the System Overview, the live ingestion panels from the ingestion spec, and recent activity and recently added, with live updates (D37). The System Overview page is removed.
- **Files likely touched:** `Components/Settings/OverviewTab.razor` (retired), `IngestionLiveDashboard.razor` and its state, `RecentlyAddedPageContent.razor`, `SettingsNav.cs`
- **Depends on:** WP2 and Shy's decision on Q27 (the ingestion spec bump)
- **Size:** Complex
- **Done when:** opening Library and Ingestion shows library summary, live ingestion and recent activity on one page that updates without reload, with no Local AI banner and no Live ingestion sub-page; the System Overview route and menu entry are gone; Recently Added opens from View all.

### WP17 — System resources summary
- **What it does:** System opens on a summary of processor load, memory, disk space and Engine version and uptime (D39); adds a small Engine reading if one does not exist.
- **Files likely touched:** a new System summary component, an Engine endpoint for resource readings (to be confirmed by Claude Code)
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** the summary shows current values and refreshes; nothing leaves the machine.

### WP18 — Regroup the Server pages
- **What it does:** Moves existing content to where D41 to D44 put it: Streaming limits and Hardware Acceleration out of Network into Streaming (renamed from Playback and Delivery); Server information from Network's Overview into the System summary; Playback tools and services status into Tools and requirements; Local AI's Models and Runtime content to Tools and requirements; Plugins sub-pages into one page. Plain-language labels replace jargon ("variants" become "optimized copies", to be checked against the presentation rules). No setting is removed; saved values keep working.
- **Files likely touched:** `SettingsNav.cs`, `NetworkStreamingSettingsPanel.razor`, `PlaybackDeliverySettingsTab.razor`, `NetworkOverviewPanel.razor`, `LocalAiSettingsTab.razor`, `ModelsTab.razor`, `PluginSettingsTab.razor` (to be confirmed by Claude Code)
- **Depends on:** WP2, the Tools and requirements page from the first-run spec
- **Size:** Complex
- **Done when:** every setting that existed before is reachable at its new place; Network has no Streaming page; Streaming holds limits, hardware and optimized copies; Local AI shows only AI settings; a test or screen check lists the old and new location of each moved panel.

### Follow-up (not in this spec)
- **Developer tools as a whole** (Shy, 2026-10-19): Local AI, Plugins, Integrations and Developer Tools get one planning review so they work cohesively, including whether outside software should connect through plugins (planning items J and the Local AI review). This spec only gives them one Developer menu group, and moves no behaviour.

## 7. Risks and open questions

- **Risks:** logs and retention (WP11, WP12) are a sizeable addition for the sake of one decision about history; they can be split into their own spec later if you prefer. The overview needs a few summary counts that may need a new Engine query, and it is the largest packet, so it is the last wave and can be dropped or split into its own spec (Q11). A shared frame touches every Settings page, so WP1 and WP3 need the screenshot check on all of them (Performance is unaffected; Reliability depends on tests staying green). Moving Devices changes where administrators look; the old address redirect covers saved links. The household-wide Devices scope may need a new Engine query.
- **Answered by Shy (2026-10-19):** all open questions are decided and recorded in section 3: Q1 to Q9 (D3, D6, D7, D10, D12, D24), Q10 to Q15 (D15, D16, D20, D22), Q16 to Q22 (D22, D26, D29, D32). No question is open.
- **Q23 (answered: view only).**
- **Q24 (answered: "Settings" does not work as the name, so Preferences).**
- **Q25 (answered: Users).**
- **Q33 (answered):** Manage group adopted (D47).
- **Q29 to Q32 (answered):** Streaming rename (yes), Plugins folded (yes), Users moved to Server > Users and Sign-in (yes), Applications renamed Integrations (yes).
- **Q26 to Q28 (answered):** the group is **Server**; the locked ingestion spec is amended to v4 when this spec locks (section 8); Logs lives under System.

## 8. Gaps and drift

- **Locked ingestion spec (engineering/specs/ingestion-and-readiness.md, Locked v3).** Shy's menu decisions D37 and D38 change what that spec defines: it keeps the System Overview with its own Recent Activity table, a live ingestion page, and a separate Activity log page (its WP7 "One activity list on every screen"). Here they become one live page under Library and Ingestion and a Logs page under System. The meaning of a Locked spec cannot be changed from here, so the proposal is: **when this spec is locked, bump the ingestion spec to v4 with a short change note** (the live page also shows the library summary and is the landing page; the Activity log becomes the Events part of Logs; the banner and Needs Review page stay as written) and a note that this spec's WP15 to WP17 carry the new work. Nothing in the ingestion spec's goals, decisions D1 to D15 or acceptance criteria otherwise changes. This needs Shy's explicit decision (Q27).
- **Locked first-run spec.** The Tools and requirements page keeps its content; only its place in the menu changes (under System). The Scheduled tasks section (D22) is an addition to that page. No conflict.
- **Settings and Profiles (2026-10-12).** Decision D9 (devices under Server) is reversed here (D5), and D7 (retire the My household tab) stands. Recorded.

## Plain English summary

Settings gets one common frame, so every page lines up, and one menu tree where every page with sub-pages expands with icons in fixed columns. Server becomes Server; Server pages are regrouped by the question being asked (reach the server, send and prepare video, is it healthy), with Playback and Delivery renamed Streaming. Library and Ingestion opens on one live page that replaces the System Overview, and a single Logs page under System replaces the Activity log and Logs and data. Administrators see up to three dismissable notice bubbles at the top, the menu shades what is waiting (Needs Review in amber) or cannot work (Local AI in red), and Local AI shows a permanent notice on its own page. The "Account" group becomes "Personal": an Overview of you (counts, recent activity, taste, and an Apps page with the server address and a QR code), an Activity page with search, filters, per-title attempts and a date rail like the photo pages, a Taste page with the same who-sees-what rules, and Profiles, which becomes an editing page for administrators that also holds households, with the Engine's rights fixed first. Server administrators can see anyone's activity and household administrators their own household's, always logged; only server administrators can delete history, and history survives deleting an item. Devices move under Security with device icons and household and profile filters. Secure account is only about using the Library away from home. A single Logs page under System handles events, retention and bug-report exports.
