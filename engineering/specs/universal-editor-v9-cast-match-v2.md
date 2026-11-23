# Spec: Editor v9 version 2, People inside Match (amends v9 version 1)

> **Status:** Locked · **Version:** 2 · **Last updated:** 2026-11-22 (locked by Shy) · Related: Editor v9 version 1 (Locked), issue #1244 · mockups `../design/mockups/editor-v9/people-in-match.html`

## What changes from version 1 (Shy, 2026-11-22; Locked text is not edited)
- **D2 changed.** People is a **switch inside Match** (Media providers, Wikidata, People), not its own tab. Q120 answer changes from A (new tab) to B (inside Match).
- **D2 changed.** No "Change person" on Cast and crew tiles or their "..." menu. Cast and crew stays as today; the only way in is Edit, then Match, then People.
- **New.** Works for titles with no Wikidata page of their own (a video folder such as "MasterClass: Stephen Curry"). People stays available; Wikidata and Media providers stay in the switch. Results are judged by name, role and description; the "lists this title" row says "No Wikidata page", and the verdict reads "Could not confirm".
- **New.** Long lists: a "Find a person or role" filter and "Show all N".
- **New.** Each row and the result card link to the person's Wikidata page and, when one exists, their Wikipedia page.
- **Roles.** Roles stay as pulled from the retail listing, a person may carry several (Guest · Writer). Roles are not edited here.
- **Person page.** No "More" button and no edit entry on a person's page (people are not edited; fixes happen on the title). Q122 stays: Match is read-only on the person.
- **Search** finds people only (Wikidata humans; musical groups for music roles), ranked by the credit's role.
- Everything else in version 1 (staging, Review changes, lock against refresh, phone sheet, permissions) is unchanged.

## Work packets
Same as version 1 except WP4 puts the list in Match as the People switch (not a new tab), WP5 becomes "remove the person page's More button" (no tile menu entry), and WP2 handles a title with no Wikidata page. 

## Local checks
Open a video folder with a wrongly matched host: Edit, Match, People, change the person, Save. Phone width too.

## Plain English summary
To fix a wrong person on a title, you open the title's editor and go to Match, where People now sits next to Media providers and Wikidata. It works even when the title has no Wikidata page. Each person has Wikidata and Wikipedia links, search finds people only, and roles stay as the retail listing gave them. Nothing is edited from the cast tiles or the person page.
