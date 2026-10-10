---
title: "Understand privacy and network access"
description: "Learn what stays on your server and when providers, models, plugins, maps, and remote access use the network."
audience: user
category: explanation
product_area: privacy
status: current
---

# Understand privacy and network access

Understand Tuvima Library's storage and network boundaries in about three minutes. Your library runs on your own host, while some optional services and the Places map can contact external systems.

## Keep your library on your host

Your media files and SQLite database stay in configured local storage. Profiles, progress, settings, and review state are stored there too. Managed artwork, thumbnails, caches, staging data, and generated metadata use configured local paths.

The [Engine](../reference/glossary.md#engine) and Dashboard run on that host. Local AI uses its CPU or GPU. There is no Tuvima-hosted account service or built-in telemetry pipeline.

Photos stay with the person who took them. Everyone in a household can browse each other's photos, and the household has one Shared Library. A server administrator can also open other households' photos through **Other people** in View, read-only. That is deliberate and visible: each time an administrator opens someone's space or a household's Shared Library, Tuvima records who looked and what they opened (at most once an hour per space), and the household owner sees it under **Account > Who viewed your photos**. People outside your household start with View switched off until an administrator turns it on for them.

This describes where Tuvima stores data. Your own network shares, backup destinations, reverse proxy, and installed plugins have their own access and privacy implications.

## Know when external requests happen

Enabled metadata providers can request identifiers, descriptions, artwork, lyrics, subtitles, and relationship data. Examples include Apple, MusicBrainz, TMDB, TheTVDB, Comic Vine, LRCLIB, SubDL, Wikidata, and Wikimedia Commons.

Provider behavior depends on configuration, credentials, media type, and processing state. Model downloads retrieve local model files from external hosts; subsequent supported inference runs locally.

Refreshing the approved plugin catalog contacts GitHub. Installed plugins can use external services according to their implementation and declared permissions. Only install code you trust.

## Understand Places map requests

**View > Places** opens Tuvima Atlas. It first attempts OpenFreeMap's dark map style, which can load external map resources. If that attempt fails, the map falls back to locally served country data.

Opening Places can therefore make external requests even though the media and place aggregates come from your authorized library. Do not describe this surface as always offline or free of third-party requests.

## Use local AI with clear expectations

Local model files run through LLamaSharp and Whisper.net rather than a cloud prompt endpoint. Model-download URLs retrieve artifacts; they do not send prompts for remote inference.

AI can assist with classification, matching, descriptions, search intent, and supported audio tasks when the relevant feature is enabled and ready. It does not replace the Priority Cascade's factual metadata rules.

Evaluation fixtures, outputs, reports, and models stay on the Engine host unless you deliberately export them. Gated models need deliberate license acceptance and a verified artifact. A source URL is provenance, rather than proof of an installed runtime.

## Protect credentials and remote access

Keep provider keys in ignored secret files, rather than committing them to the repository. Missing credentials should remain visible as a configuration problem, not as a successful connection.

New installs start local-network-only. Remote access requires normal sign-in plus a verified supported Tailscale Serve or HTTPS reverse-proxy path. Advanced router mapping is opt-in. [Remote access guidance](../guides/remote-access.md) explains the deployment checks.

## Plan an offline session

Browsing already indexed media and using ready local models can avoid provider lookups. For a restricted-network deployment, account for metadata providers, downloads, catalog refreshes, plugins, and the Places map.

Disabling metadata providers alone does not prove the entire Dashboard makes no external requests. Network policy and the surfaces you open determine that boundary.

<details>
<summary>Technical details</summary>

Long-lived provider credentials belong under `config/secrets/`. Base provider definitions remain under `config/providers/`; a blank base file does not establish that secrets are absent.

The Places client attempts `https://tiles.openfreemap.org/styles/dark`. Its local fallback uses `/maps/world-countries.geojson`. Local media authorization is separate from the map's network source selection.

AI evaluation and hardware benchmarking follow explicit opt-in controls. Ordinary recorded-fixture tests do not need to load model weights. See [security architecture](../architecture/security.md) for authentication and server boundaries.

</details>

## Next steps

- [Configure providers](../guides/configuring-providers.md)
- [Understand local AI readiness](how-ai-works.md)
- [Use View and Shared Library](../guides/view-personal-space.md)
- [Manage plugins](../guides/using-plugins.md)
