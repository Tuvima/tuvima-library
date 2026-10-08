---
title: "Security policy"
description: "How to privately report a security problem in Tuvima Library, which versions get fixes, and what to expect."
audience: "user"
category: "policy"
product_area: "security"
---

Tuvima Library holds personal photos, viewing history, and accounts, so we take security reports seriously. This page mirrors [`SECURITY.md`](https://github.com/Tuvima/tuvima_library/blob/main/SECURITY.md) in the repository.

## Report a problem privately

Please do not open a public issue. On GitHub, open the repository's **Security** tab and choose **Report a vulnerability**, or use the [private report form](https://github.com/Tuvima/tuvima_library/security/advisories/new). Only you and the maintainer can see it. No email address is published.

Include the version or commit, how you run Tuvima Library (Docker, Windows installer, or source), steps to reproduce, and what an attacker could do.

## What to expect

- We aim to acknowledge reports within 7 days. This is a goal, not a guarantee.
- Fixes land on `main` and ship in the next release. Reporters are credited if they wish.
- Only the latest Early Access release and `main` receive fixes.
- There is no bug bounty.

## Scope

In scope: the Engine, Dashboard, Docker image, Windows installer, and official plugins. Out of scope: third-party plugins, provider services, and misconfiguration such as exposing the Engine port publicly against the [remote access guidance](../guides/remote-access.md).

## Next steps

- [Security architecture](../architecture/security.md)
- [Account security](../guides/account-security.md)
