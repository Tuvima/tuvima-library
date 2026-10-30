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
- Profiles own roles, feature access (Read/Watch/Listen/View) and hidden libraries (a library is visible to a profile when it is not hidden for its household, `household_library_hidden`, set by the server administrator as the household ceiling, and not hidden for the profile, `profile_library_hidden`; administrators see all, a household administrator cannot show what the household lacks, and the Vault is a fifth area with its own access that this rule never covers). A Kid's profile is the restricted role (`is_kids` on the wire) and follows household Kid levels for Watch, Read and Listen plus one "Show unrated items" switch; there is no per-profile limit, no per-library access value and no export or import of people (backup is the only way to move data); a user is a sign-in attached to one profile. Effective administration requires the current enabled user acting as a profile that holds the role, plus any optional surface PIN unlock. Applications own permissions and revocable credentials; delegated native access intersects current user, profile access, device, token, and consent.
- Check resource scope before counts, grouping, paging, artwork, streams, and personal state. Preserve distinct View scopes.
- Admin-only presentation must be enforced by role on the Engine, never by a URL/query flag alone.
- `MediaEngine.Identity` builds with `TreatWarningsAsErrors` (as does every project); keep it warning-free.
- Security/authorization changes are a high-risk area: builder at high effort, reviewer pass required, and Fable escalation only with Shy's approval.
