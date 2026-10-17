---
paths:
  - "src/MediaEngine.Identity/**"
  - "src/MediaEngine.Admin/**"
  - "src/MediaEngine.Api/Security/**"
  - "src/MediaEngine.Api/Endpoints/**"
  - "src/MediaEngine.Domain/Authorization/**"
  - "src/MediaEngine.Web/Services/Integration/**"
  - "tests/MediaEngine.Identity.Tests/**"
  - "tests/MediaEngine.Api.Tests/**"
---

# Security and access rules

Full model: `docs/architecture/security.md`.

- Obsolete pre-beta identity state fails fast. Never restore role-based guest keys, automatic compatibility conversion, localhost administration, seed-owner fallback, or private-space reassignment. Original source media remain protected.
- Accounts own feature and actual library grants; profiles retain experience and privacy identity. Effective administration requires the current enabled account and active AdminEnabled grant, plus any optional surface PIN unlock. Applications own permissions and revocable credentials; delegated native access intersects current account, grant, device, token, and consent.
- Check resource scope before counts, grouping, paging, artwork, streams, and personal state. Preserve distinct View scopes.
- Admin-only presentation must be enforced by role on the Engine, never by a URL/query flag alone.
- `MediaEngine.Identity` builds with `TreatWarningsAsErrors` (as does every project); keep it warning-free.
- Security/authorization changes are a high-risk area: builder at high effort, reviewer pass required, and Fable escalation only with Shy's approval.
