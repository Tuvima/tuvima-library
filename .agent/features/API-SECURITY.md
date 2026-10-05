# Access authority

Updated 2026-09-09. Mirrors CLAUDE.md section 3.3. See docs/architecture/security.md and the Access execution status for the implementation and acceptance gates.

Accounts own feature/library grants and administrator eligibility. Profiles own experience, restrictions, history, and Personal Spaces. Effective administration requires the enabled account and exact active grant with AdminEnabled; optional administrator PIN protection is grant-specific. No Curator/Consumer/StandardUser role or localhost/seed-owner fallback authorizes requests.

Applications own registered service permissions; credentials are hashed, independently revocable, and shown once. Native clients intersect live account, profile, Application, device, token, and consent. Unavailable service permissions stay unavailable with a reason.

Use TuvimaAuthentication, IRequestAuthorityResolver, and IAuthorizationEvaluator. Require operation policy plus resource checks on each endpoint. Filter authorized concrete assets before counts, grouping, representative artwork, and pagination. Personal writes require the exact active profile. View private, explicit administrator inspection, Shared Library, and resource-specific Gallery scopes remain separate.

Dashboard navigation and actions use Engine-projected authority. Metadata editing, Review, and administrator configuration require effective administration and any configured surface unlock; personal settings remain available without that unlock. Use the shared PIN gate and editor unlock prompt.

Protect originals. Obsolete pre-beta identity/configuration state fails fast; do not recreate legacy role schemas or compatibility key conversion. Run actual mapped-endpoint, revocation, cross-resource, and privacy regression tests before accepting a cutover.

Sign-in rejects stale, missing, mismatched, or unreadable antiforgery tokens with HTTP 400 and a freshly tokenized sign-in form. The expired-form notice requests a new submission; rejected credentials never reach the Engine. A local return destination is preserved, while credential fields are cleared. Other security actions retain their existing validation.
