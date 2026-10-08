---
title: "Set language preferences"
description: "Choose interface and metadata languages, configure accepted content languages, and check provider language behavior."
audience: "user"
category: "guide"
product_area: "language"
status: current
---

# Set language preferences

Choose how Tuvima Library displays text and requests provider metadata. Allow a few minutes to choose defaults; existing metadata changes only when it is refreshed.

## Choose languages during setup

In the setup wizard's **Language and region** stage, choose the display language, metadata language, and country.

These choices do not restrict the languages of your media. Translated interface coverage varies; missing strings fall back to English. A language appearing in the selector does not mean every screen is translated.

## Understand the four preferences

| Preference | What it changes |
| --- | --- |
| Display | Dashboard interface language |
| Metadata | Language requested from providers that support it |
| Additional | Other content languages you accept |
| Accept any | Accept all content languages; enabled by default |

Your media files are not translated or rewritten by these settings.

For an installed server, administrators can review the saved values under `language` in `config/core.json`:

```json
{
  "language": {
    "display": "en",
    "metadata": "fr",
    "additional": ["es", "ja"],
    "accept_any": true
  }
}
```

Merge these values into the existing file; do not replace its other settings. Use the server's configuration folder, such as the host folder mounted at `/config` in Docker. Restart after manual edits.

Turning off **Accept any** lets language-mismatch checks compare detected content with metadata and additional languages. It is not a guarantee that every file's language can be detected.

## Check each provider's behavior

1. Open **Settings → Providers**.
2. Open the provider and find **Language strategy**.
3. Choose the intended strategy and save.

| Strategy | Behavior |
| --- | --- |
| Source | Use the provider's source language, English |
| Localized | Use your metadata language |
| Both | Combine localized and English lookup behavior |

Defaults differ by provider. English fallback and merging depend on that provider's adapter; a localized setting cannot create translations the provider does not have.

Changing language does not itself replace every saved title or description. Check a representative item after its next enrichment or refresh.

## Use multilingual search and AI

Search can use the titles and aliases actually indexed for a work. Try its displayed title, original title, or known alias. Do not assume a romanized title exists unless the provider supplied one.

The search store includes substring matching for CJK text and a fallback for short queries. Coverage still depends on the indexed metadata.

A multilingual AI model is optional. Its role must pass the `text_multilingual` gate before dependent features are ready. Downloading a model alone does not enable a feature. Use **Settings → Local AI → Models & Runtime** to inspect readiness.

## Next steps

- [Configure providers](configuring-providers.md).
- [Understand local AI](../explanation/how-ai-works.md).
- [Read configuration keys](../reference/configuration.md).
