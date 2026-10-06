---
name: tuvima-design
description: Use this skill to generate well-branded interfaces and assets for Tuvima Library, either for production or throwaway prototypes/mocks/etc. Contains essential design guidelines, colors, type, fonts, assets, and UI kit components for prototyping.
user-invocable: true
title: "Tuvima Design Skill"
summary: "Internal design-skill instructions for branded Tuvima Library visual work."
audience: "designer"
category: "reference"
product_area: "design"
---

Read the `README.md` file within this skill, and explore the other available files - `colors_and_type.css` for design tokens, `assets/` for logos and icons, `fonts/` for self-hosted Montserrat + Merriweather + JetBrains Mono, `preview/` for token specimens, and `ui_kits/dashboard/` for a full clickable recreation of the cinematic library browser (home + collection detail).

Tuvima Library is a dark-only, cinematic story-library product. The visual language is deep navy + glassmorphic panels + a single purple product chrome accent (`#8852FC`, with `#A46FFF` hover). Segoe UI Variable/system UI is the interface face; Montserrat is reserved for brand/media identity; Merriweather and amber highlight colors are scoped to the EPUB reader only. Use the established icon family for the surface: FontAwesome for navigation, shared outline PlaybackUtilityGlyph for player utilities. Validate peer icons together using the README icon-row rule. No emoji, ever.

If creating visual artifacts (slides, mocks, throwaway prototypes, etc.), copy assets out of this folder and create static HTML files that link `colors_and_type.css` for tokens. If working on production code, copy assets and read the rules in `README.md` to become an expert in designing with this brand - pay special attention to the CONTENT FUNDAMENTALS (voice, casing, person), VISUAL FOUNDATIONS (shadows, blur, transparency rules), and ICONOGRAPHY sections.

If the user invokes this skill without any other guidance, ask them what they want to build or design, ask some focused questions (audience, surface - dashboard vs reader, hi-fi vs wireframe, how many variations), and act as an expert designer who outputs HTML artifacts _or_ production code, depending on the need.

## Current Dashboard/Product UI Model

Home, Read, Watch, Listen, Collections, and Search are the user-facing discovery and media surfaces. Detail pages and media rows/cards launch inline editing through the shared media editor. Review Queue is only for blocked or uncertain items that need human confirmation. Settings/Admin is for configuration and operational/system concerns. Do not add all-in-one management routes, components, docs, or workbench flows.

Design changes must preserve the current quality gates: no removed all-in-one management workflow, no obsolete management navigation label, and media correction controls should launch the shared editor instead of creating a separate management surface.
