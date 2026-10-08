# Security Policy

Tuvima Library holds personal photos, viewing history, and accounts, so we take security reports seriously.

## Supported versions

| Version | Supported |
|---|---|
| Latest Early Access release | Yes |
| `main` branch | Yes |
| Older releases | No |

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

Use GitHub's private vulnerability reporting instead: open the repository's **Security** tab and choose **Report a vulnerability**, or go straight to <https://github.com/Tuvima/tuvima_library/security/advisories/new>. Only you and the maintainer can see the report. No email address is published.

Please include:

- The affected version or commit.
- How you run Tuvima Library (Docker, Windows installer, or from source).
- Steps to reproduce the problem.
- What an attacker could do with it.

## What to expect

- We aim to acknowledge reports within **7 days**. This is a goal, not a guarantee.
- Fixes land on `main` and ship in the next release. We credit reporters who want to be credited.
- There is no bug bounty.

## Scope

In scope: the Engine, the Dashboard, the Docker image, the Windows installer, and the official plugins.

Out of scope: third-party plugins, metadata provider services, and self-inflicted misconfiguration, such as exposing the Engine port to the internet against the guidance in [Remote access](https://tuvima.github.io/tuvima_library/guides/remote-access/).

For how Tuvima Library protects accounts and media, see [Security architecture](https://tuvima.github.io/tuvima_library/architecture/security/).
