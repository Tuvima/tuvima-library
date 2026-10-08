# Prepared squash description

This is the reviewed description used for the single local squash onto `main`. No pull request was published.

## Title

Unify account, application, and media access authority

## Description

People sign in with accounts and choose profiles. Accounts own feature and library permissions; profiles retain their experience and private View identity. Applications own explicit permissions and replaceable credentials. Current account, grant, application, credential, device, and consent records determine access at each protected boundary, so revocation also reaches media, playback, events, and webhooks.

Settings > Access provides Users, Applications, and Authentication. Administrators can manage up to eight profile grants per account, optional administrator PIN protection, local-only access, invitations, external sign-in readiness, and service integrations. The Dashboard uses the Engine's current authority for navigation and actions. Private View access, administrator inspection, Shared Library, contributions, and Gallery shares remain distinct.

The change also adds host-bound plugin capabilities, the bounded Fandom Lore service, durable playback telemetry, filtered real-time application events, and signed webhooks with bounded delivery and retries. Metadata and artwork requests check their concrete resource targets. Bootstrap cannot reopen when an existing administrator is disabled or loses sign-in credentials.

The accepted prerequisite fixes are included: aligned ingestion cards, compact batch lists, scrolling child lists, readable durations, matched paging controls, and recoverable Dashboard service-credential failures.

## Verification and deployment

The combined warnings-as-errors build and formatting verification pass. All 13 test projects pass: 3,632 passed, 37 existing provider skips, zero failures. Coverage is 46.19% lines and 31.45% branches, above the required 13% / 7% floors. Contract fixtures, native-client source checks, strict documentation checks, and isolated host smoke pass. Browser acceptance also passed for account/profile lifecycle, grants, administrator PIN protection, a Server Integration Application, one-time credential generation, and a signed private-network webhook whose event permission was enforced. See [execution status](./status.md) for exact evidence.

This is a destructive pre-beta access-state cutover, not a migration of old roles or API keys. Start with fresh access records, issue new application credentials, and re-pair native clients. Follow the [cutover procedure](./cutover.md) to preserve original media and existing private View directories; a new profile must never inherit an old private directory by name.

Plain English: permissions become consistent across the product, while people keep separate profile experiences and private media. The combined change passed its final automated and browser checks and is ready for the deliberate fresh-access-state startup described above.
