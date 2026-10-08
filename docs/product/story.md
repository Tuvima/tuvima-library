---
title: "Why I built Tuvima Library"
description: "The personal story behind Tuvima Library and its long-term vision for connecting owned media."
audience: user
category: explanation
product_area: documentation
status: current
---

# Why I built Tuvima Library

Tuvima Library began with a gap I felt in my own library.

I was—and still am—an avid user of Plex, Audiobookshelf, and other media managers. I appreciated what each one did well, but I was still responsible for remembering how everything connected. The book lived in one library, its audiobook in another, the film adaptation somewhere else, and the soundtrack somewhere else again. The more formats I collected, the less the whole thing felt like one collection.

Books made the problem especially clear. I might read an ebook at home, then want to continue the same story as an audiobook while driving to work. Amazon's Kindle and Audible apps use [Whispersync for Voice](https://help.audible.com/s/article/listen-with-whispersync-for-voice?language=en_US) to make that switch feel natural—but only for supported Kindle and Audible editions. I wanted that kind of continuity for media I already owned and controlled.

Watching adaptations raised a different set of questions:

- Where did this scene happen in the book?
- Was it changed for the film?
- Who is this character, and what is their history?
- How are they connected to the other people, places, and events in this world?
- Which actor played the same character in another adaptation?

A normal remote can pause the movie or change the volume, but it cannot help explore the story. I imagined a phone becoming a true companion: following where I was in the film and offering timely, spoiler-aware context about a character, location, event, performer, or source chapter.

That was the realization behind Tuvima Library. The missing piece was not another player. It was a shared understanding of the works and the universe around them. As I looked beyond my own setup, I found many other collectors trying to bridge the same gaps with separate servers, manual collections, spreadsheets, plugins, and memory.

The name came from the same idea. [ElfDict lists **túvima**](https://www.elfdict.com/w/tuuvima/q) as a Quenya adjective meaning **“discoverable,”** citing Tolkien's linguistic material through its Eldamo entry. A product intended to reveal the stories and connections hidden across a media collection needed a name that meant exactly that. **Tuvima Library** was the logical choice.

Tuvima Library is the library I wanted for myself, built in the hope that it can become that library for others too.

## From remote control to story companion

The Universe model is intended to remain useful after you press Play or begin reading.

In the fuller vision, a phone could follow the current point in a film and show spoiler-aware context about the character on screen, their background so far, the performer, the location, and the matching passage in the source book. Instead of merely asking a phone to pause *The Lord of the Rings*, you could ask, “Who is this character?” or “Where did this happen in the book?” and receive an answer grounded in the right adaptation and moment.

The same foundation can support cross-format position mapping: stop reading an ebook at home, begin the audiobook in the car, and continue from the corresponding narrative point. It is a local-first version of the continuity that makes Whispersync for Voice compelling, designed for the editions you own.

Wikidata supplies canonical identities and relationships; Wikipedia supplies readable context; Tuvima Library's local analysis can align positions, chapters, scenes, and playback time. Today, the foundations include shared identities, progress, media relationships, people and character links, and sourced Universe Graph data. Automatic cross-format position matching, scene mapping, and the real-time companion are still in development.

Learn more in [How Universes and Series Work](https://tuvima.github.io/tuvima_library/explanation/how-universes-work/) and the technical [Universe Graph](https://tuvima.github.io/tuvima_library/architecture/universe-graph/) documentation.

## Next steps

- [See what is ready today](status.md).
- [Understand Universes and shelves](../explanation/how-universes-work.md).
