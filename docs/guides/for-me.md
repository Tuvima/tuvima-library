---
title: "Keep your place with For Me"
description: "Use your profile's Continue, My List, Favorites, Collections, Playlists, and Galleries without confusing saved state with progress."
audience: user
category: guide
product_area: profiles
status: current
---

# Keep your place with For Me

Use For Me in Tuvima Library to return to saved items and work in progress. This two-minute guide explains the shelves for your active [profile](../reference/glossary.md#profile).

## Find your shelves

Open **For Me**. Overview shows Continue, My List, Favorites, Your Collections, Your Playlists, and Your Galleries when they have items.

Continue follows reading or playback progress. My List saves items for later. For Me's Favorites shelf currently shows songs you have marked with a heart. You can save, favorite, or start an item on its own. One choice does not make the others.

## Save something for later

1. Open a supported item's details or its save control.
2. Select **My List**.
3. Open **For Me > My List**, or use the global bookmark action, to find it.
4. Use the area and sort controls to narrow the list.

You can save a movie, TV show, book, comic, audiobook, album, or collection you can access. You can also save a library or shared playlist as one entry. For songs, use Favorite or Add to Playlist instead.

## Find favorites and your own groups

Open **For Me > Favorites** for your favorite songs. This view does not yet browse favorite items across all media types. Like/Dislike ratings are separate from Favorite. A song's heart adds it to Favorites. Favorites in View belong to that separate personal-media surface.

Your own collections, playlists, and galleries appear in their **Your** shelves. Library collections and shared playlists enter My List only when you choose to save them.

Switch profiles to change whose progress and saved items you see. You can switch only to profiles your account is allowed to use.

<details>
<summary>Technical details</summary>

Overview uses `/for-me`. The list views use `/for-me?view=my-list` and `/for-me?view=favorites`. Saved items, reactions, and progress have separate profile records. They are not stored as hidden collections or playlists.

Old Watchlist, Reading List, Listening Queue, and `/my-list` routes are retired terms.

</details>

## Next steps

- [Start or resume an experience](playback-and-reading.md)
- [Organize personal media in Galleries](view-personal-space.md)
- [Tour the Dashboard](dashboard-tour.md)
