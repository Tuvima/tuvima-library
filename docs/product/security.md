---
title: "Report a security vulnerability"
description: "Report vulnerabilities privately and understand the current supported development line and response goals."
audience: administrator
category: policy
product_area: project
status: current
---

<!-- Generated from SECURITY.md by scripts/docs/sync-community-docs.mjs. -->

# Security policy

## Supported versions

| Version | Security maintenance |
| --- | --- |
| Current `main` branch | Supported development line |
| Older snapshots | No separate security maintenance |

Tuvima Library is Early Access. No installer releases are currently published. This policy will identify supported releases when they become available.

## Report a vulnerability privately

Use [GitHub private vulnerability reporting](https://github.com/Tuvima/tuvima_library/security/advisories/new), also available under **Security → Advisories → Report a vulnerability**.

Do not post vulnerability details in a public issue. Do not include passwords, API keys, recovery codes or private media in a report. Use a minimal example with sensitive values removed.

Include:

- The affected commit or version and whether you run Docker, Windows or from source.
- The steps needed to reproduce the problem.
- The expected behavior, observed behavior and likely impact.
- Relevant configuration with secrets removed.

Reports about the Engine, Dashboard, official plugins and distributed deployment files are welcome. Problems in third-party plugins or provider services may need coordination with their authors. A deployment choice does not by itself rule out a vulnerability in Tuvima Library.

## What to expect

We aim to acknowledge reports within **seven days**. This is a goal, not a guarantee.

Fixes are developed on the active branch and included in a subsequent release when available. We coordinate disclosure with the reporter where possible. Credit is optional; tell us whether and how you want to be named. There is no bug bounty.

## Deployment guidance

Follow the [remote-access guide](https://tuvima.github.io/tuvima_library/guides/remote-access/). Do not expose the Engine port directly as a public entry point. Plugins run in the Engine process, so install only plugins you trust.

Read the [security architecture](https://tuvima.github.io/tuvima_library/architecture/security/) for the account, profile, library and application access model.
