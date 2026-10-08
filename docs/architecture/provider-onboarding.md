---
title: "Provider onboarding contracts"
description: "Tuvima Library connects directly from the local Engine to metadata providers."
audience: "developer"
category: "guide"
product_area: "platform"
---
## Decision

Tuvima Library connects directly from the local Engine to metadata providers. A
Tuvima-operated credential gateway is not part of the provider architecture.
Provider credentials belong to the installation that supplied them and remain
under `config/secrets/` on that installation.

The retired Cloudflare Worker was never connected to the Engine. Its shared
caller secret and caller-selected target URL would have introduced a hosted
credential boundary, central quota coupling, and an unnecessary dependency for
a local-first product.

Any future Tuvima-hosted integration must be designed as a separate service with
per-install authentication, fixed upstream allowlists, quotas, operational
ownership, and explicit terms and privacy disclosure. It must not be introduced
as a fallback for local provider credentials.

## Contract ownership

Non-secret setup instructions live in each `config/providers/*.json` manifest
under `onboarding`. The provider catalogue projects that metadata for generic UI
consumers. Dashboard and setup components must not embed provider names,
credential labels, signup instructions, legal links, supported lanes, or skip
consequences.

Credential values use the dedicated provider credential endpoints. They are
accepted only in write requests and are never returned by catalogue, status,
test, save, or removal responses.

## Authentication checks

Each credentialed provider declares a non-mutating `GET` or `HEAD` probe beneath
its configured API origin. The Engine validates credential format locally before
making that request and returns one stable result category: valid, missing or
malformed credential, invalid credential, rate limit, provider outage, region
restriction, or local connectivity failure.

TMDB is the first complete reference contract. It uses application-level,
read-only API access and the provider's `/configuration` endpoint for its
non-mutating credential check.
