---
title: "Secondary Title Text"
description: "Semantic source, storage scope, and display fallback rules for the short line beneath a media title."
audience: "developer"
category: "reference"
product_area: "details"
---
The detail UI has one visual slot beneath the title, but the stored metadata remains semantic.

| Media | Preferred semantic field | Primary source | Scope | Editor label |
|-------|--------------------------|----------------|-------|--------------|
| Movie | `tagline` | TMDB explicit tagline | Movie Work | Tagline |
| TV show | `tagline` | TMDB explicit series tagline | Show/root Work | Tagline |
| TV episode | none | — | — | No tagline |
| Book | `subtitle` | Apple explicit subtitle, then other explicit metadata | Literary Work | Subtitle |
| Audiobook | `subtitle` | Edition metadata, then literary Work subtitle | Edition then Work | Subtitle |
| Comic issue | `subtitle` | Explicit provider field only | Issue Work | Subtitle |
| Music | none | — | — | No tagline/subtitle |

TMDB's dedicated tagline is high-confidence provider metadata. Descriptions, plots, annotations, solicitation copy, and AI output are never persisted as taglines or subtitles.

For presentation only, an empty preferred field falls back to `short_description`, then to a bounded excerpt of `description`. The API reports the selected source in `SecondaryTitleTextKind`; the Dashboard adds “more in overview” when the fallback does not contain the complete overview. This fallback does not create or lock a canonical tagline/subtitle claim.

Missing tagline or subtitle is optional and does not create Needs Review work. Existing items acquire newly supported values through normal versioned enrichment refresh rather than library re-import.
