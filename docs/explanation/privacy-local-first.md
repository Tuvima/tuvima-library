---
title: "Privacy and Local-First Behavior"
description: "Understand what stays local, when Tuvima contacts external providers, and how local AI fits into the privacy model."
audience: "user"
category: "explanation"
product_area: "privacy"
tags:
  - "privacy"
  - "local-first"
  - "ai"
---
Tuvima Library is designed for people who want a capable media library without handing their files or reading habits to a hosted service.

The core rule is simple: **your library runs on your machine**.

## What Stays Local

- Your media files stay on your disk.
- The SQLite database is local.
- Internal artwork, thumbnails, cache files, staging files, and generated metadata stay under configured local paths.
- The Engine and Dashboard run as local apps.
- Local AI inference runs on your CPU/GPU.
- AI evaluation fixtures, outputs, promotion reports, and model files remain on the Engine host unless an administrator deliberately exports them.
- Hardware benchmarking and live model evaluation require explicit opt-in; ordinary test runs use recorded fixtures and do not load model weights.
- Gated models require deliberate license acceptance and installation of a verified artifact; a source link is provenance, not a cloud inference endpoint.
- Profiles, settings, playback preferences, and review state are stored locally.

There is no Tuvima-hosted account service and no built-in telemetry pipeline.

## When Network Calls Happen

Tuvima can contact external services when you enable or configure metadata providers. Those calls are for enrichment, not cloud storage.

Examples:

- Apple APIs, MusicBrainz, TMDB, TheTVDB, Comic Vine, LRCLIB, and SubDL may be used for metadata, artwork, identifiers, lyrics, subtitles, or lookup data when enabled and configured.
- Wikidata and Wikimedia Commons may be used for canonical identity, structured facts, relationships, images, and bridge resolution.
- Model download URLs are used to retrieve local AI model files.

Provider behavior depends on configuration, credentials, media type, and pipeline state. If no provider can safely identify an item, Tuvima routes it to Review Queue instead of guessing.

## Local AI

Local AI is not a cloud prompt service. Tuvima uses local model runtimes through LLamaSharp and Whisper.net. Supported model files are downloaded to a local model directory, loaded by the Engine, and run locally.

The AI helps with:

- filename cleanup
- ambiguous media type classification
- Wikidata candidate disambiguation
- vibe tags
- summaries
- description analysis
- natural-language search intent
- audio transcription and subtitle sync where supported

The AI does not become the authority for factual metadata. Canonical structured facts still flow through providers, Wikidata resolution, and the Priority Cascade.

## Secrets and Provider Keys

Provider secrets belong under `config/secrets/`, which is ignored by git. Keep provider API keys out of committed files.

Some providers require credentials before they can be used. The Dashboard labels unavailable, partial, read-only, or not-connected settings instead of pretending a missing credential is a live configuration.

## Practical Limits

Local-first does not mean network-free. Metadata enrichment can call public and commercial provider APIs. If you want a fully offline run, disable providers that make network calls and use only local file metadata.

Local-first also does not mean anonymous or automatically public access. Tuvima
starts local-network-only and requires its normal sign-in plus a verified
Tailscale Serve or HTTPS reverse-proxy path before remote access can be enabled.
Router mapping is an Advanced, opt-in deployment tool and is never the default.

## Related

- [How the Local AI Works](how-ai-works.md)
- [How Enrichment Works](how-hydration-works.md)
- [Configure Providers](../guides/configuring-providers.md)
- [Product Status](../product/status.md)
