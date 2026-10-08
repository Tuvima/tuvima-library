---
title: "Artwork Types"
description: "Understand the artwork roles Tuvima Library stores and how the editor uses each one."
audience: "user"
category: "reference"
product_area: "artwork"
tags:
  - "artwork"
  - "media editor"
  - "images"
status: current
---

# Artwork Types

## In this page

Understand the artwork roles Tuvima Library stores and how the editor uses each one.

## Where this lives in the code

- `src/MediaEngine.Domain/ArtworkAspectClasses.cs`
- `src/MediaEngine.Api/Services/ArtworkAssetService.cs`
- `src/MediaEngine.Web/Components/Artwork`

Tuvima Library stores multiple artwork roles because different surfaces need different image shapes. The editor lets users choose the preferred variant for each role without changing the underlying media file unless export settings require it.

| Artwork type | Primary use |
| --- | --- |
| Poster / Cover | Primary display image for books, movies, TV, albums, audiobooks, and detail cards. |
| Square Art | Square tiles for music, playlists, compact collection views, and surfaces that need a 1:1 image. |
| Background | Wide backdrop or fanart image for hero sections, detail headers, and blurred washes. |
| Banner | Short wide artwork for list headers, TV-style rows, and narrow horizontal placements. |
| Logo | Transparent or title-only mark layered over hero artwork or premium cards. |
| Clear Art | Transparent character/object artwork used as an overlay when providers supply it. |
| Disc Art | Disc-shaped media artwork used for music, video disc, and collector-style presentations. |

## How the Artwork Editor Works

Each artwork type has its own gallery. Click an image once to make it the active artwork for that role. Use the magnifier button to inspect the full-size image, or use the delete button to remove that variant from the item.

Provider artwork can be removed from an item without deleting the shared provider/image cache file. User-uploaded artwork is owned by that item and its local file can be cleaned up when the variant is deleted.

## Artwork and Generated Backgrounds

Artwork also feeds the color-aware background system used by collection tiles, hero banners, playlist cards, and artwork stacks. Tuvima extracts a dark-safe palette from visible artwork so these surfaces can use gradients and glows without sacrificing text readability.

The extracted palette is cached and reusable across UI surfaces. The artwork stack itself does not perform expensive image analysis during rendering.
