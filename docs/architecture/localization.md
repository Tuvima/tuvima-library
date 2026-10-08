---
title: "Dashboard Localization"
description: "Architecture notes for localized UI text, language preferences, and multi-language provider behavior."
audience: "developer"
category: "architecture"
product_area: "language"
status: current
---

# Dashboard Localization

## In this page

Architecture notes for localized UI text, language preferences, and multi-language provider behavior.

## Where this lives in the code

- `src/MediaEngine.Web/Resources/SharedStrings.resx`
- `src/MediaEngine.Web/Resources/SharedStrings.cs`
- `tests/MediaEngine.Web.Tests`

Tuvima Library localizes high-visible Dashboard copy through `SharedStrings` resources in `src/MediaEngine.Web/Resources`.

## Current Standard

- Inject `IStringLocalizer<SharedStrings>` into Razor components that render user-facing copy.
- Use `L["Key"]` for labels, buttons, empty states, headings, and validation messages.
- Use `L["Key", value]` for interpolated copy instead of concatenating localized and non-localized fragments.
- Keep English, French, German, and Spanish resource files key-compatible. If a translation is uncertain, use the English text as the fallback value in the non-English resource file.

## Do Not Localize

- CSS class names.
- API routes.
- enum keys, provider IDs, and storage keys.
- log-only developer diagnostics.
- test fixture identifiers.

## Test Coverage

`MediaEngine.Web.Tests` includes a resource parity test for `SharedStrings.resx`, `SharedStrings.fr.resx`, `SharedStrings.de.resx`, and `SharedStrings.es.resx`. Add new keys to every file in the same change.
