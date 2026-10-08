---
title: "Tuvima Library"
description: "One library. Every story. Documentation for Tuvima Library, the private, local-first story library."
template: splash
audience: "user"
category: "landing"
product_area: "docs"
status: "early-access"
tags:
  - "landing"
hero:
  tagline: "One library. Every story. Books, audiobooks, movies, TV, music, comics, and photos, presented together on your own machine."
  image:
    file: ../assets/screenshots/home.jpg
    alt: "Tuvima Library Home showing a featured TV show and a shelf of movies"
  actions:
    - text: Install
      link: tutorials/getting-started/
      icon: right-arrow
      variant: primary
    - text: Windows installer
      link: install/windows/
      variant: secondary
    - text: GitHub
      link: https://github.com/Tuvima/tuvima_library
      variant: minimal
      attrs:
        target: _blank
---

Tuvima Library watches the folders you already have, works out what each file is, and presents it all as one coherent library. Nothing leaves your home: there is no cloud account and no subscription.

:::note[Early Access]
Some features are still in progress. [See what is ready today](product/status.md).
:::

## Choose your path

<div class="tl-cards">

<div class="tl-card">

### I want to use Tuvima

Get running, add your media, and fix anything that needs a look.

- [Getting Started](tutorials/getting-started.md)
- [Your First Library](tutorials/first-library.md)
- [Resolve items that need review](guides/resolving-reviews.md)

</div>

<div class="tl-card">

### I run the server

Install it on Docker, Windows, or a NAS, then keep it healthy.

- [Docker](install/docker.md) · [Windows (Early Access)](install/windows.md)
- [Unraid](install/unraid.md) · [Synology](install/synology.md) · [QNAP](install/qnap.md) · [TrueNAS SCALE](install/truenas-scale.md)
- [Operations and recovery](guides/operations-and-recovery.md)

</div>

<div class="tl-card">

### I build plugins or providers

Extend Tuvima with your own plugins, providers, and processors.

- [Build a plugin](guides/building-a-plugin.md)
- [Add a provider](guides/adding-a-provider.md)
- [Plugin catalog](reference/plugin-catalog.md)

</div>

<div class="tl-card">

### I want to contribute

Set up the code, run the tests, and learn how the pieces fit.

- [Developer setup](tutorials/dev-setup.md)
- [Technical overview](architecture/technical-overview.md)
- [Run the tests](guides/running-tests.md)

</div>

</div>

## See it

![Watch: movies and TV shelves in the Tuvima Library Dashboard](../assets/screenshots/watch.jpg)

![An album detail page showing Abbey Road](../assets/screenshots/music-album-abbey-road.jpg)

## Look something up

- [Configuration reference](reference/configuration.md)
- [Media types](reference/media-types.md)
- [Glossary](reference/glossary.md)
- [Engine actions](reference/api-endpoints.md)
- [Product status](product/status.md) and [Beta roadmap](product/beta-roadmap.md)
- [Report a security problem](product/security.md)
