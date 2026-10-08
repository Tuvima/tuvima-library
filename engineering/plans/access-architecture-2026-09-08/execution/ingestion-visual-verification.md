# Ingestion visual verification

Verified in the running Dashboard at `http://localhost:5016` after the user unlocked administration on 2026-09-08. The existing batch contained 78 media groups. No credentials were added to files, and no scan or media mutation was needed.

## Observed results

| Effective CSS viewport | Surface | Evidence |
| --- | --- | --- |
| 1920 × 1080 | Batch cards | Seven first-row cards have equal 422.188 px height. Their artwork starts at the same 472.713 px position and their icon rows at 801.193 px. Square and portrait art retain natural proportions within the same approximately 200.44 px square slot. Missing bylines do not shift rows. |
| 1920 × 1080 | Batch list | Condensed Media, Type and files, Status, and Details columns; completed status is a green check without repeated visible Ready to browse text. |
| 1280 × 720 | Album drawer | Tracks and footer remain usable with the drawer body scrolling. Escape closes the drawer and returns focus to the exact selected album button. |
| 390 × 844 | Cards/list/drawer | Two-column cards retain equal 371.023 px height and approximately 149.27 px square artwork slots. The condensed list fits; document width is 381 px, with no horizontal overflow. Mobile drawer shows title, tracks, and footer actions. |

The live A Night At The Opera drawer shows five tracks in one list, including Bohemian Rhapsody at **5:54**. It has no child-page controls. Automated drawer tests separately cover loading beyond 250 children, stale responses, cancellation/disposal, malformed paging, and retry.

Batch paging correctly changes from items 1–50 to 51–78, with Next disabled on the last page. Previous, the Page 2 pill, and Next all measured **35.994 px high at the same vertical position**. The page was returned to the first page and the temporary viewport override cleared.

Screenshots were captured inline in this task for desktop cards, desktop list, desktop and mobile drawers, mobile list, and mobile cards. Browser scaling was accounted for by measuring `innerWidth`/`innerHeight` rather than assuming the override's requested physical dimensions equalled CSS dimensions. No screenshot of the protected page was claimed before unlock.

## Limits

The Engine was idle, so a newly running ingestion was not forced solely to create a screenshot. Active-work labels and facets use the same tested tile renderer; this run visually inspected real completed batch items. Historical before-change renderings were not available in this verification session.

Plain English: covers and icons line up, the list uses compact rows, tracks scroll together with readable times, and the page indicator matches the adjacent buttons.
