# P05 Dashboard Access preflight

Base inspected: `7060a837`. This is a contract and source map only; P05 implementation waits for P02's frozen interfaces.

## Current Dashboard flow and files

Authentication enters through `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs` and `DashboardIdentityClient.cs`. Login and validation currently hydrate a cookie and `DashboardSessionAccessor` with `session/account/active-profile/token/Role`; `AuthSessionResponse` and `SessionValidationResponse` still serialize `role`. `ActiveProfileSessionService.cs` reads all profiles through `IEngineApiClient.GetProfilesAsync`, finds the active one from cookie claims, and calls `/auth/session/switch-profile` with the legacy profile PIN. It then force-reloads `/`. `MainLayout.razor` copies `Profile.Role` into `_profileRole`; its review and activity affordances call `SettingsNav.IsVisible(section, role)`. `TopNavAccountMenu.razor`, the profile switcher, and `AdministratorElevationNavigationService.cs` retain the separate legacy elevation lifecycle.

`Models/ViewDTOs/SettingsNav.cs` determines both Settings visibility and direct-route acceptance from role strings. Its single `Access` section maps to `/settings/access` with subsections `accounts`, `authentication`, `api-keys`, and `session-policy`. `UsersAccessSettingsTab.razor` dispatches these to `AccountsAccessTab.razor`, `SecurityTab`, and `ApiKeysTab.razor`; its default is legacy `UsersTab.razor`. `AccountsAccessTab.razor` uses legacy `/accounts` DTOs and profile IDs; `ApiKeysTab.razor` exposes role-labelled guest keys. `AccountSettingsTab.razor` is self-service but links to legacy `/account/elevate`.

Engine requests pass through `DashboardEngineAuthenticationHandler.cs`, which obtains a request-time session token and then invokes the base service-credential handler. `ViewProfileAssertionHandler.cs` signs `/view` and `/collections` using the service credential inserted on that same request. `DashboardServiceCredentialProvider.cs` rereads the protected credential bundle on every request and returns a recoverable 503 when unavailable; retain this behavior. `ViewMediaProxyEndpoint.cs`, `ViewMediaEngineClient.cs`, and `ViewMediaGrantService.cs` proxy signed View grants; `Program.cs` sends ordinary artwork through `/engine-image` with only the Dashboard service credential. These proxies need a current authenticated request authority and must not manufacture a seed Owner/profile.

## P02 interfaces P05 requires frozen

1. A single Dashboard authority projection returned by authenticated login, session validation, and profile switch: account and active profile/grant IDs; enabled state; account/grant authority versions; effective-admin eligibility; administrator-surface unlock state, expiry, and protection version; exact granted profiles (max eight); and server-calculated navigation/action capabilities. Anonymous bootstrap status remains minimal readiness/setup information and never enumerates identities or grants. P05 must use the authenticated projection, never `Role`, `IsAdministrator` alone, or browser-selected profile IDs.
2. Stable managed-access endpoints and error contract (401 expired/invalid session, 403 admin/capability denial, 404 hidden resource, 409 concurrency/last-admin/eight-profile conflict): list/read/create/update/delete account; replace feature and library grants; create/update/revoke profile grant; configure grant protection; unlock, inspect unlock, and explicit admin exit/lock. The self-service account response remains separate.
3. Stable Application endpoints: permission definitions (availability/risk/type), presets, list/read/create/update/delete application, replace permissions, and credential create (one-time plaintext), rotate, revoke. Responses must disclose enabled state, actual last use, credential metadata, and Custom/preset state without plaintext replay.
4. Typed View-admin scope choices/capabilities, supplied by the server for the effective authority and scoped to exact profiles/Personal Spaces. P05/P04 must reject unauthorized explicit IDs rather than falling back to Mine. Define proxy session forwarding and bounded revocation behavior for View media, stream/range/HLS/download, and artwork.

## Route/component replacement map

| Existing target | P05 target |
| --- | --- |
| `/settings/access` + `UsersAccessSettingsTab` default `UsersTab` | Redirect/canonicalize to `/settings/access/users`; render server-authorized managed accounts and grants. |
| `/settings/access/accounts` + `AccountsAccessTab` | Remove the old route, with no compatibility alias; fold account/profile-grant functions into canonical Users. |
| `/settings/access/api-keys` + `ApiKeysTab` | Remove the old route, with no compatibility alias; replace role-bearing keys with Applications and credentials. |
| `/settings/access/authentication` + `SecurityTab` | Keep canonical Authentication page, but authorization/readiness data must be server-owned; P07 owns policy presentation. |
| `/settings/access/session-policy` | Remove the old route, with no compatibility redirect; Authentication contains the policy controls owned by P07. |
| `/account/elevate` legacy elevation + `SetAdministratorPinAsync` | Replace with active-grant admin unlock/exit lifecycle. Profile-selection PIN remains a distinct switch flow. |
| Role-based `SettingsNav`, `MainLayout`, `TopNavAccountMenu`, setup/editor launchers | Gate menus, routes, setup, actions, and View scope selection from the authority projection/capabilities. |

P06 may refine Users/Applications presentation after P05 establishes working backend data, denial behavior, and the three canonical Access routes.
