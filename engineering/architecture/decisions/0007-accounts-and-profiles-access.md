# 0007 — Accounts own access; profiles own experience; no guest keys

**Date:** 2026-09-08 (access architecture plan, completed P00–P13) · **Status:** Accepted

## What
An **account** (someone who signs in) holds feature and library permissions; a **profile** holds the experience — watch history, preferences, content limits. Administration needs an enabled account with an active admin grant, plus an optional PIN unlock. Apps get their own revocable permissions. Old role-based guest keys, "localhost is admin" and seed-owner fallbacks were removed and must not return. Details: `docs/architecture/security.md`; history: `engineering/plans/access-architecture-2026-09-08/`.

## Why
- **Privacy:** household members can't see each other's private spaces by accident.
- **Reliability / security:** every request is checked against current grants; old identity data fails fast instead of being guessed at.

## Trade-offs accepted
Pre-beta identity data was reset rather than converted. Admin-only screens must check roles on the Engine — an address flag is not enough.
