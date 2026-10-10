# Plan (draft): Setup and profiles redesign, Netflix/Disney+ style (2026-10-10)

Status: **draft. Pick up in its own session.** Split out of `first-run-ingestion-fixes-2026-10-10.md` (former design item D4). Needs a scout pass of the current setup/profile/identity code and the PO's answers to §5. Mockups come before implementation.

## 1. Why (from the 2026-10-10 first-run walkthrough)
- First-run setup and profile creation feel heavy compared with Netflix or Disney+.
- Adding a profile is only reachable through Settings → Users & Access → "⋯" on the account → Manage profiles. That drawer also mixes profile grants, admin-dashboard permission, PIN protection and profile creation.
- The "Secure account" banner takes space on every page. (A dismissable version ships in the first-run fixes plan; this plan decides where it permanently lives.)
- The profiles drawer reads "1 of 8 profiles granted" when only one profile exists (meaning unclear).

The first-run fixes plan already ships these quick wins: name shown once in the account menu, a "Manage profiles" shortcut, and a dismissable banner.

## 2. Proposed experience (to refine with mockups)
1. **Who's watching?** After sign-in, if the account has more than one profile, a full-screen grid of profile avatars with names and an **Add profile** tile. One profile: skip straight in.
2. **Add profile:** name, an avatar picker (curated set + colour), and an optional "Kids" toggle (maturity limit). Nothing else on the first screen; more settings are tucked away.
3. **Switch profile:** from the account menu ("Switch profile" → grid) and from the grid itself. A PIN is asked only if that profile is protected.
4. **Manage profiles:** one simple screen (Edit, Delete, PIN, Kids) reachable from the grid's "Manage" and from Settings → Profile. Admin-only grants stay in Users & Access.
5. **First-run setup:**
   - Collapse into a short guided flow: Welcome → your account (name, email, password optional "use on this computer") → your profile (avatar) → media folders → done.
   - Providers and advanced options are set up with sensible defaults and checked automatically (see the first-run fixes, B14).
6. **Securing the account:** a card on the Security page and a dot on the account menu until it's done. No persistent top banner.

## 3. Likely technical scope (to confirm)
- **Dashboard:** new `ProfilePicker` page/route, avatar picker component, setup flow simplification (`SetupPage.razor` stages), account menu changes, Settings → Profile management screen.
- **Engine / Identity (TreatWarningsAsErrors):** profile creation/edit endpoints likely exist (Users & Access drawer); confirm avatar storage and Kids/maturity support; profile PIN flow exists.
- **Security guardrails** (`docs/architecture/security.md`): profile switching and PINs must not weaken authorisation; no role-based guest keys, no localhost administration.
- Update `docs/product/presentation-rules.md` and the setup/profile explanation pages.

## 4. Agents
- Scout (Haiku): map setup stages, profile endpoints/contracts, avatar storage, PIN and grant logic.
- Opus: mockups (HTML artifact or screenshots), plan, briefs.
- Implementer (Sonnet): medium for Dashboard; **high** for anything in Identity/authorisation.
- Reviewer (Opus) for authorisation changes; verifier (Haiku).

## 5. Questions for the PO
1. Show "Who's watching?" on every sign-in, or only when opening the app on a TV/shared device?
2. Avatars: a curated illustrated set, initials + colour, or both? Any brand style for them?
3. Kids profiles in v1 (maturity limit per profile), or later?
4. Should the first-run flow still ask for an email if the account is "this computer only"?
5. Where does "Secure account" live after dismissal: the Security card only, or also a small dot on the account menu?
6. What should "N of 8 profiles" mean? Is 8 the intended profile cap?

## 6. Acceptance criteria (draft)
- A new user completes first-run setup in ≤5 screens with defaults.
- An account with 2+ profiles sees the "Who's watching?" grid after sign-in; picking a profile lands on Home in that profile.
- Add/edit/delete profile from the grid's Manage view without visiting Users & Access.
- No persistent banner; account security status is visible in Settings → Security.
- Authorisation guardrail and security tests pass unchanged or strengthened.
