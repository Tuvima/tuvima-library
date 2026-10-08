---
title: "How to Set Up Language Preferences"
description: "Configure display, metadata, and accepted content languages for your library."
audience: "user"
category: "guide"
product_area: "language"
tags:
  - "language"
  - "localization"
  - "cjk"
---
This guide explains Tuvima Library's language settings, what each one controls, and how to configure them for a multilingual collection.

---

## The four language settings

Tuvima Library separates language into four distinct settings because there are four different things that "language" can mean for a media library:

| Setting | What it controls |
|---|---|
| **Display language** | The language of the Dashboard interface itself (menus, labels, buttons). |
| **Metadata language** | The language in which the Engine queries external providers for titles, descriptions, and other information. |
| **Additional languages** | A list of extra languages you're willing to accept for content - useful if you own media in multiple languages. |
| **Accept any** | A master toggle that, when on, tells the Engine to accept files in any language without requiring them to match your settings. |

---

## Where to configure language preferences

1. Open the Dashboard at `http://localhost:5016`.
2. Go to **Settings -> Preferences -> Profile**.
3. Scroll to the **Language Preferences** section.

All four settings are in this section. Changes take effect immediately for the display language; metadata and content language settings apply on the next enrichment run.

---

## Display language

The display language controls what language the Dashboard itself is shown in. The following languages are fully supported:

- English
- French
- German
- Spanish

When you change the display language, the Dashboard reloads and all interface text - navigation labels, button names, section headings, status messages - switches to the selected language. Your media content is not affected.

If a translation is incomplete for any interface string, English is shown as a fallback.

---

## Metadata language

The metadata language controls which language the Engine uses when querying providers. For example, if you set metadata language to French, the Engine will request French-language titles and descriptions from providers that support it (such as TMDB and Apple API).

This affects how your library is displayed - titles and descriptions from providers will come back in your chosen language where available.

> **Note:** Some providers always return data in English regardless of this setting. This is controlled per provider by a language strategy (see below and in the Configuring Providers guide). If a provider doesn't have data in your metadata language, the Engine silently falls back to English rather than returning an empty result.

---

## Additional languages

If you own media in languages other than your primary metadata language, add those languages here. For example: if your metadata language is English but you also own French films and Spanish novels, add French and Spanish to your additional languages list.

The Engine uses this list when searching Wikidata. For a file whose embedded metadata is in French, the Engine will search Wikidata in both French and English, then compare results to find the best match. This significantly improves identification accuracy for foreign-language titles.

---

## Accept any

The **Accept any** toggle is on by default. When it is on, the Engine will process files in any language - even if their language doesn't appear in your display language, metadata language, or additional languages list. This is the recommended setting for most users.

When you turn Accept any off, the Engine will flag files whose language doesn't match any of your configured languages with an amber informational banner in the Review Queue. The file is still processed and stored; the banner is informational only and does not block identification or enrichment.

---

## How foreign-language files are handled

When the Engine processes a file in a language that differs from your metadata language, it handles it intelligently:

- **Search** - Wikidata is searched in both the file's detected language and your metadata language. Results are compared and duplicates are removed before scoring.
- **Title display** - The title in your metadata language is shown as the primary title. If the file's embedded title is in a different language, it is shown as a smaller subtitle beneath it. For example, a Japanese film would show the English title (from Wikidata) as the main title, with the Japanese original as a subtitle.
- **Search indexing** - Romanized titles are indexed automatically. Searching for "Sen to Chihiro no Kamikakushi" will find the film even if your library displays it as "Spirited Away".

---

## CJK support (Japanese, Korean, Chinese)

Japanese, Korean, and Chinese (Simplified and Traditional) are supported with specific handling for their writing systems, which do not use spaces between words the way Latin-script languages do.

**What the Engine does automatically:**
- Uses a specialised text-matching approach (trigram tokenization) for CJK content. This means searching for any three-character sequence will find matches, even in the middle of a word - which is how CJK search needs to work.
- Short searches (fewer than three characters) fall back to a broader matching approach that still finds partial results.
- Romanized forms of CJK titles (such as pinyin, romaji, or romanized Korean) are indexed alongside the original script, so you can find titles by typing either form.

**Optional CJK AI model:**
If your metadata language or additional languages include Japanese, Korean, or Chinese, the Dashboard's Settings screen will offer the configured CJK-capable model from `config/ai.json`. The default is Qwen3 4B Q4_K_M, selected because it covers CJK and broader multilingual analysis while staying below the old large enrichment tier.

- The model is not downloaded automatically - you need to enable it in **Settings -> Intelligence -> Models**.
- It is validated by the `text_multilingual` gate rather than selected only because hardware is available.
- Once downloaded, it is used automatically when processing CJK files.

---

## Per-provider language strategy

Each provider has a language strategy that controls which language is used when the Engine queries it. You can view and change these in **Settings -> Providers** by clicking on any provider.

The three strategies are:

**Source** - always query in English. These are providers whose catalogues are English-only or whose English data is significantly more complete. MusicBrainz uses this strategy.

**Localized** - query in your metadata language. These providers have strong international catalogues and will return better results in your language. Examples: TMDB, Apple API.

**Both** - query in your metadata language first, then in English if the first query returns nothing. Results are merged and the best match is selected. Wikidata uses this strategy by default.

You generally don't need to change the default strategy for any provider. The defaults are set to produce the best results for most users. If you notice that a particular provider is returning titles or descriptions in the wrong language, check its language strategy setting.

---

## Search across languages

The Engine's search index covers multiple languages at once. When you type in the search box in the Review Queue or Command Palette, the Engine searches:

- Titles in your display language
- Original-script titles (Japanese, Korean, Chinese, etc.)
- Romanized titles
- Alternate titles and aliases from Wikidata

This means you can find any item in your library regardless of which script or language you type in. A search for "Dune" will find the English novel, and a search for "Dune" in a library where the metadata language is French will find the French edition as well.

## Related

- [Configuration Reference](../reference/configuration.md)
- [How the Local AI Works](../explanation/how-ai-works.md)
- [How to Configure Metadata Providers](configuring-providers.md)
