---
title: "Play, listen, and read"
description: "Start and resume books, music, audiobooks, movies, and owned TV episodes with the built-in players."
audience: user
category: guide
product_area: playback
status: current
---

# Play, listen, and read

Start an item in Tuvima Library in a few steps. Use its reader or player to control your session. Your active [profile](../reference/glossary.md#profile) keeps its own progress and settings.

## Read a book or comic

1. Open **Read** and select a book or comic.
2. Choose **Read** or the resume action on its detail page.
3. Use the reader's page controls and other tools.
4. Return to the library when you finish the session.

The EPUB reader opens supported ebooks in the browser. Reading tools and progress depend on the format. Use playback and reading settings to change the options offered.

## Play music

1. Open **Listen > Music**.
2. Select a song, or open an album and choose **Play** or **Shuffle**.
3. Use the bottom audio dock to play, pause, or seek.
4. Expand the player for more room and tools.

**Queue** shows upcoming songs. **History** shows past playback. **Lyrics** shows text when available. It highlights words or lines when the source has valid timing. Untimed lyrics stay readable text.

## Listen to an audiobook

1. Open a book in **Listen > Audiobooks**.
2. Choose start or resume, or select a track on Overview.
3. Open **Chapters** to browse the source segments.
4. Use bookmarks and History to return to saved or past positions.
5. Change **Speed** or set **Sleep** when needed.

Audiobooks keep track progress and offer bookmarks, speed, history, and sleep tools. Videos have the same Bookmarks panel (see below). Chapter titles come from the source unless you edit them. Chapter times and boundaries stay file facts.

Sleep offers timed choices and chapter ends that the player can verify. A chapter option may be disabled if its end cannot be checked.

On desktop, **Close player** saves a checked, paused position and ends the audio session. On a phone, collapsing the player makes it smaller and keeps audio playing.

## Watch a movie or TV episode

1. Open **Watch** and select a movie or TV show.
2. Choose **Watch** or **Resume**. A show starts an in-progress episode or the first owned episode.
3. Use the controls to seek, choose available subtitles, or open **Up Next**.
4. Choose **Close** to return to the movie or the episode's show details.

Episode lists and Up Next use episodes you own. A provider listing alone is not playable. Audio tracks, subtitles, and quality depend on the file and server.

Video can play directly when the client supports the source. Other files may need browser-ready delivery first. That can delay playback.

## Bookmarks in videos and audiobooks

Choose **Bookmarks** in the video player (or the audiobook player) to open one panel with **Add** and **Saved**. Add captures the moment the panel opens, so it is not lost while you type. It shows the chapter name when there is one, lets you preview the captured spot, and takes a short note (up to 200 characters) and, in a video, an optional longer description (up to 4,000). Saved lists the title's bookmarks, newest first, with jump-to and delete.

Each bookmark is a tick on the progress rail beside the file's own chapters. Point at a tick (or tap it on a touch screen) to see its short note; if it has a longer description, choose **More**. Bookmarks are personal to the profile that made them. A server administrator can switch on **Share with everyone** when adding, which makes a shared bookmark with a different tick shade and a small "Everyone" label; everyone who can see the title sees it, and only administrators can delete it. Adding a bookmark works on a phone or tablet and does not need Editing Mode.

### Bookmark packs

At the bottom of **Saved** in the video player, **Export** lets you pick some of your own bookmarks, review them and save a small pack file you can give to someone else; **Import** reads a pack someone gave you. The pack holds only how to recognise the film or show and each bookmark's time, end, chapter name, short note and longer description: no file paths, no profile names and nothing about what you watched. If the pack is for a different title it is refused; if it was made on a different cut (another running time or edition) you can **Align by chapter** or **Skip**. Imported bookmarks are read-only **pack bookmarks**, shown with the pack's name and author and their own tick shade on the progress rail, and each pack can be switched off or removed. They belong to your profile, are included in your library backups, and are not offered in the Vault.

## Resume and save separately

Continue shelves and resume actions follow progress. Completed items show their completion state. Albums, tracks, and View items do not use long-form completion bars.

**My List** saves an item for later. **Rate** means Like or Dislike. **Favorite** is a separate choice. A song's heart adds it to Favorites. These choices do not start playback or replace progress.

<details>
<summary>Technical details</summary>

The Engine saves supported player and reader settings. Saved positions belong to the active profile and exact media item. Stream access expires and is resolved for the current session. A saved position is not a download link.

Prepared video may use HLS variants, alternate audio, and WebVTT captions. See [playback architecture](../architecture/playback.md) for delivery, resume, bookmarks, and player ownership rules.

</details>

## Next steps

- [Use For Me to find saved items and progress](for-me.md)
- [Correct an item or chapter title](editing-items.md)
- [Check playback availability](../product/status.md)
