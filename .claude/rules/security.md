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

Full model: the Security architecture page on docs.tuvima.com (`architecture/security.md` in `tuvima-docs`).

- Obsolete pre-beta identity state fails fast. Never restore role-based guest keys, automatic compatibility conversion, localhost administration, seed-owner fallback, or private-space reassignment. Original source media remain protected.
- Profiles own roles, feature access (Read/Watch/Listen/View) and hidden libraries (every library in a profile's areas is visible unless hidden from that profile); a user is a sign-in attached to one profile. Effective administration requires the current enabled user acting as a profile that holds the role, plus any optional surface PIN unlock. Applications own permissions and revocable credentials; delegated native access intersects current user, profile access, device, token, and consent.
- Check resource scope before counts, grouping, paging, artwork, streams, and personal state. Preserve distinct View scopes.
- Admin-only presentation must be enforced by role on the Engine, never by a URL/query flag alone.
- `MediaEngine.Identity` builds with `TreatWarningsAsErrors` (as does every project); keep it warning-free.
- Security/authorization changes are a high-risk area: builder at high effort, reviewer pass required, and Fable escalation only with Shy's approval.
