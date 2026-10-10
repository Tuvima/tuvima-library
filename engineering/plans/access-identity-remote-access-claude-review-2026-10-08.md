# Independent Claude review: Tuvima access, identity and remote access

Review the companion `access-identity-remote-access-review-2026-10-08.md` as a proposed plan. The baseline is repository commit `8e9d91cf`, dated October 8, 2026. Follow the repository AGENTS.md. This request authorizes review only: do not implement, alter credentials, reset data, start public services, configure routers, or publish anything.

## Product intent

Tuvima Library should offer a first run without email/password for use on the host computer, while requiring security before extending access. Email/password can remain the familiar secured-account default, with a username alternative and passkeys. External identity is optional. Review literal no-proxy internet hosting as well as private VPN and HTTPS proxy choices. Preserve a future path to optional Tuvima ID, privacy-oriented Tuvima Social and hosted server discovery/relaying without building those products now.

## Review instructions

1. Read the full plan, especially its early product walkthrough, evidence limits, G01–G15 gap register, states, work-package dependencies and acceptance matrix.
2. Inspect the R1–R15 source references. Distinguish confirmed behavior, a plausible concern and a reproduced vulnerability. Existing tests and historical acceptance are not proof of a new threat scenario. If you lack repository access, mark source claims unverified and give an architecture-only review.
3. Challenge the host-claim design. Does no typed password become anonymous administrator access anywhere? Can a second browser/LAN client race setup? Can a proxy, container, mapped IPv6 address, null IP or stale restored database bypass the intended boundary? Is the headless workflow usable without publishing an unclaimed app?
4. Challenge request-time enforcement. Does remote-off govern existing sessions, images, HLS, readers, downloads, uploads, native tokens and SignalR/Blazor circuits? Is a no-PIN local session usable remotely? Is internal Dashboard transport ever confused with human authority?
5. Review account/profile/library/View separation, effective administrator rights, child restrictions, share recipients, integrations, cache keys, counts, search and event leakage. Verify the plan preserves the already accepted Access architecture.
6. Review username/email separation, password/passkey policy, RP IDs and canonical origins, step-up, recovery, concurrent mutation, last-usable-authenticator safety, key storage, rate limiting and migration/rollback.
7. Inspect OIDC/OAuth handling, including Microsoft tenant issuers, exact issuer comparison, standard claim mapping with inbound mapping disabled, GitHub numeric ID/private email, provider reload and disabled-provider sessions. Never suggest bypassing signature/issuer checks or automatic linking by email.
8. Verify direct Kestrel HTTPS feasibility and operational requirements; distinguish no proxy from no port forwarding. Evaluate DNS/ACME, CGNAT, IPv6, expiry/renewal, host filtering, external probes, SSRF and DoS exposure. Cloudflare DNS, Tunnel and Access are different offerings; verify current official terms before recommending media relay use.
9. Evaluate privacy claims precisely: who sees credentials, sign-in metadata, server addresses and content? Does any future ID/cloud/social choice become mandatory? Are pairwise subjects, social consent and relay encryption claims accurate? Is there unnecessary architecture for products that do not yet exist?
10. Assess whether the plan is implementable in safe increments, sufficiently tested, comprehensible to a product owner and appropriately bounded. Suggest simplification where it preserves the security outcomes.

## Required response format

Lead with **Approve**, **Approve with changes**, or **Revise before implementation**, explicitly describing this as plan review rather than deployment approval.

Provide:

- A brief plain-English assessment of the complete proposed user experience.
- Prioritized findings with severity, plan section/gap ID, exact repository file and symbol/line when available, concrete failure scenario, confidence, proposed correction and the test that closes the finding.
- Corrections to any overstated current-support or privacy claim, with official sources where external behavior matters.
- Missing product decisions, separating blockers from optional improvements.
- Work-package/order changes, if needed, with reasons.
- A concise acceptance checklist and residual uncertainties.
- A closing plain-English product-owner summary.

Do not pad findings with generic best practices. Do not treat every hypothesis as a proven exploit. If the core approach is sound, say so and focus on the remaining actionable issues. Do not declare the implementation secure based on reviewing the plan.
