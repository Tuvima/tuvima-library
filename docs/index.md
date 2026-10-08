---
title: "Tuvima Library Documentation"
summary: "Start here to install Tuvima Library, understand product status, and choose the right user or developer path."
audience: "user"
category: "guide"
product_area: "docs"
tags:
  - "landing"
  - "start-here"
  - "navigation"
---

<div class="tl-hero" markdown="1">

<span class="tl-kicker">Early Access documentation</span>

# Tuvima Library Documentation

Tuvima Library is a private, local-first story library. It watches your folders, identifies catalogue media, keeps personal content local when requested, and presents books, audiobooks, movies, TV, music, comics, and photos in one Dashboard.

<div class="tl-actions" markdown="1">

[Get Started](tutorials/getting-started.md){ .md-button .md-button--primary }
[Product Status](product/status.md){ .md-button }
[Technical Overview](architecture/technical-overview.md){ .md-button }

</div>

</div>

## Start Here

<div class="tl-card-grid" markdown="1">

<div class="tl-card" markdown="1">

### Install and launch

Install with Docker or a supported NAS container manager, claim the server,
configure governed sources, and begin catalogued intake or a View Personal Space.

[Open Getting Started](tutorials/getting-started.md)
[Docker](install/docker.md) · [Unraid](install/unraid.md) · [Synology](install/synology.md) · [QNAP](install/qnap.md) · [TrueNAS SCALE](install/truenas-scale.md)

<div class="tl-meta">
  <span class="tl-pill">Tutorial</span>
  <span class="tl-pill">15-30 min</span>
</div>

</div>

<div class="tl-card" markdown="1">

### Build your first library

Add catalogued sources or personal View sources, watch the applicable intake
path, and resolve only catalogued items that need identity review.

[Open Your First Library](tutorials/first-library.md)

<div class="tl-meta">
  <span class="tl-pill">Tutorial</span>
  <span class="tl-pill">Hands-on</span>
</div>

</div>

<div class="tl-card" markdown="1">

### Know what is ready

Tuvima is Early Access. Check what is live, partial, planned, or intentionally not connected.

[Open Product Status](product/status.md)
[Open Beta Roadmap](product/beta-roadmap.md)
[Open Feature Truth Inventory](product/feature-truth-inventory.md)
[Open Presentation and Product Rules](product/presentation-rules.md)

<div class="tl-meta">
  <span class="tl-pill">Reference</span>
  <span class="tl-pill">Early Access</span>
</div>

</div>

</div>

## Choose Your Path

<div class="tl-card-grid" markdown="1">

<div class="tl-card" markdown="1">

### Use Tuvima

- [Add Media to Your Library](guides/adding-media.md)
- [Resolve Items That Need Review](guides/resolving-reviews.md)
- [Troubleshooting](guides/troubleshooting.md)
- [Privacy and Local-First Behavior](explanation/privacy-local-first.md)
- [How Universes and Shelves Work](explanation/how-universes-work.md)

</div>

<div class="tl-card" markdown="1">

### Configure Tuvima

- [Operations and Recovery](guides/operations-and-recovery.md)
- [Configure Metadata Providers](guides/configuring-providers.md)
- [Language Setup](guides/language-setup.md)
- [Supported Media Types](reference/media-types.md)
- [Configuration Reference](reference/configuration.md)
- [Providers Reference](reference/providers.md)

</div>

<div class="tl-card" markdown="1">

### Build on Tuvima

- [Technical Overview](architecture/technical-overview.md)
- [Architecture Summary](architecture/architecture-summary.md)
- [Developer Setup](tutorials/dev-setup.md)
- [Add a Metadata Provider](guides/adding-a-provider.md)
- [Write a File Processor](guides/writing-a-processor.md)
- [Run Tests](guides/running-tests.md)

</div>

</div>

## How The Product Fits Together

```text
Watch folders
  -> Ingestion
  -> File processors
  -> Identity and scoring
  -> Retail identification
  -> Wikidata bridge resolution
  -> SQLite, artwork, organization, write-back
  -> API and SignalR
  -> Dashboard
```

The user-facing Dashboard is organized around current workflows:

- **Home** for discovery and overview.
- **Read**, **Watch**, and **Listen** for media lanes and shelves.
- **Collections** for automatic broader rollups, administrator-curated collections, a cross-lane shelf index, and canonical people.
- **Search** for cross-library discovery.
- **View** for profile-owned Personal Spaces and the server-owned Shared Library,
  with Photos, Folders, Galleries, People, and Places.
- **Detail pages** for viewing items and launching inline corrections.
- **Review Queue** for blocked or uncertain items.
- **Settings/Admin** for configuration and operations.

## Reference and Architecture

- [Engine API Reference](reference/api-endpoints.md)
- [Database Schema Reference](reference/database-schema.md)
- [Wikidata Property Map](reference/wikidata-property-map.md)
- [How the Pipeline Works](explanation/how-the-pipeline-works.md)
- [Ingestion Pipeline](architecture/ingestion-pipeline.md)
- [Network and Remote Access](architecture/network-and-remote-access.md)
- [Libraries, Sources, and Intake](architecture/library-model-and-intake.md)
- [View Personal Media Architecture and Static Review](architecture/view-personal-media.md)
- [Hydration Pipeline and Providers](architecture/hydration-and-providers.md)
- [Provider Enrichment Strategy](architecture/provider-enrichment-strategy.md)
- [Priority Cascade Engine](architecture/scoring-and-cascade.md)
- [Attributions](reference/attributions.md)

## Related

- [Getting Started](tutorials/getting-started.md)
- [Product Status](product/status.md)
- [Privacy and Local-First Behavior](explanation/privacy-local-first.md)
