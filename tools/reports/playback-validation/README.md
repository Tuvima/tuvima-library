# Unified playback visual review

Captured against the local Engine and Dashboard on September 28–29, 2026. Use the existing library, start the Engine and Dashboard from the repository root, then compare the numbered PNG files here with the task's reference mockups. Desktop captures use a wide browser viewport; responsive captures use a phone-width viewport. The images are checkpoints for layout and capability review, not pixel-perfect targets.

| Reference | Capture | Review note |
| --- | --- | --- |
| 1. Music dock | [01-desktop-music-dock.png](01-desktop-music-dock.png) | Transport centered; tools remain to the right. |
| 2. Music context | [02-desktop-music-lyrics-queue.png](02-desktop-music-lyrics-queue.png) | Lyrics and queue can coexist in the reflow sidebar. |
| 3. Audiobook context | [03-desktop-audiobook-chapters-bookmarks.png](03-desktop-audiobook-chapters-bookmarks.png) | Chapters and bookmarks share the sidebar. |
| 4. Music Now Playing | [04-desktop-now-playing-music.png](04-desktop-now-playing-music.png) | Desktop dock remains the sole transport. |
| 5. Audiobook Now Playing | [05-desktop-now-playing-audiobook.png](05-desktop-now-playing-audiobook.png) | Book and chapter progress remain separate. |
| 6. Music popout | Pending | The in-app browser did not expose a synchronized music popup during this run. Recheck in a regular browser. |
| 7. Audiobook popout | [07-audiobook-popout-history.png](07-audiobook-popout-history.png) | History appears in an attached side panel at wide popup sizes. |
| 8. Movie controls | [08-playable-movie-controls.png](08-playable-movie-controls.png) | A Time to Kill played with centered, configured 10/30-second transport. The earlier [08-movie-video-controls.png](08-movie-video-controls.png) used a source blocked by incomplete technical inspection. |
| 9. Movie options | [09-playable-movie-captions.png](09-playable-movie-captions.png) | Right-side panel overlays a playing movie, which continues advancing. This source has no selectable caption track yet; [09-tv-audio-panel.png](09-tv-audio-panel.png) shows populated audio choices on TV content. |
| 10. TV episode | [10-tv-episode-controls.png](10-tv-episode-controls.png) | Real episode playback and Next Up capability present. |
| 11. Personal video | [11-view-personal-video.png](11-view-personal-video.png) | Shared transport controls are centered in the View viewer. |
| 12. Ingestion sidebar | [12-ingestion-context-sidebar.png](12-ingestion-context-sidebar.png) | Detail sidebar reflows the media list without horizontal overflow. |
| 13. Mobile music | [13-mobile-music-full.png](13-mobile-music-full.png) | Full music player at phone width. |
| 14. Mobile audiobook | [14-mobile-audiobook-full.png](14-mobile-audiobook-full.png) | Equal-width book and chapter progress. |
| 15. Mobile audiobook sheets | [15-mobile-audiobook-bookmarks.png](15-mobile-audiobook-bookmarks.png), [15-mobile-audiobook-history.png](15-mobile-audiobook-history.png) | Bookmark and history bottom sheets. |
| 16. Mobile mini player | [16-mobile-mini-player.png](16-mobile-mini-player.png) | Compact continuation bar. |
| 17. Narrow video | [17-narrow-video-controls.png](17-narrow-video-controls.png), [17-narrow-video-audio-sheet.png](17-narrow-video-audio-sheet.png) | Centered transport and bottom-sheet options at phone width. |
| 18. Shared View session | [18-view-shared-session.png](18-view-shared-session.png) | Personal video plays with the shared transport; play, pause, seek, and close update the authoritative View session without a catalogue queue. |
| 19. Music restore regression | [19-beautiful-audio-dock.png](19-beautiful-audio-dock.png) | Beautiful opens in the centered music dock after the saved-session type fix; no stale video controls remain. |

Checks made during capture: transport position against the player surface, control order and available capabilities, white default controls with purple selected states, progress alignment, dock clearance, sidebar reflow, and horizontal overflow. The in-app browser's separate tabs did not expose synchronized music popup state for check 6. The music popout lyrics change was built and tested in source after these captures; its visual result remains to be captured in a browser that supports the app's popup flow.

Final View runtime check on September 29: the owned Test Drive video advanced, paused, sought to 1:00, and stopped on viewer close. The viewer's shared-session marker matched each transition and cleared on close. The browser's fullscreen API could not be independently verified in the in-app browser, so native fullscreen and PiP remain covered by source review and focused tests rather than a live visual checkpoint.

Music regression check on September 29: the owned track Beautiful opened the music dock, continued playing while moving to Watch, and closed cleanly. This check used the rebuilt Dashboard after the saved-session media-type fix. The separate named popup still did not appear in the in-app browser's controllable window inventory, so check 6 remains a manual visual checkpoint in a regular browser.
