# P02 review gate

Private worker commits publish interfaces for parallel implementation. They do not establish an accepted authorization cutover. P02–P05 must pass together before reaching main.

The following findings were sent to the P02 owner for implementation and behavioral tests. This is an open review list, not a record of passing checks.

- Use one common permission decision for Application endpoints and their services. Administrator Applications receive dynamically available permissions only after registry availability, Application type, and user/resource-context requirements are checked. An unavailable or unclassified service remains denied.
- Every delegated path checks the enabled account, profile grant, and Application. Effective human administrators receive the documented feature/library override. Detached service calls deny safely when HTTP consent context is absent.
- Administrator protection defaults off; enabled fixed-duration protection defaults to 30 minutes. Lock-on-leave leases are bounded. Failures increment transactionally, and profile switches, sign-out, protection/version changes, and explicit leave revoke the relevant unlock. Validate unknown modes explicitly.
- Native client IDs resolve through server-managed exact Application bindings. Reject unknown/disabled/wrong-type bindings at authorization, approved-code exchange, access validation, and refresh. Removing or reassigning a binding during pending approval cannot issue a token under the former binding. Preserve the existing external wire contract.
- Seed the first-party Application and bindings only on fresh initialization. Restart cannot restore an administrator-deleted Application, permission, or binding. Permission/credential mutations increment the live version and revoke stale native authority as specified.
- Preserve maximum-eight grants, one enabled default, last-effective-administrator protection, and concurrent mutation guarantees in real SQLite transactions. One-time credential responses never replay verifier hashes or secrets in lists/audit facts.
- Enforce View personal/shared library and ownership consistency on both child and parent mutations. Reject reciprocal Shared/Personal Space identity collisions. P04 owns scoped services and safe transfers; P02 owns schema constraints.
- Remove duplicate Application persistence from AccountRepository when integrating the dedicated implementation. Remove legacy runtime Role/key authority rather than retaining parallel decisions.
- Complete account lifecycle paths: typed Application admission for registered identity read/write services, service-level actor checks, actual configured catalogued-library validation and names, account deletion, invitation/credential setup, and external-link administration. Audit profile/setup/recovery/passkey entry points separately; replacing the main account route does not complete them.
- External identity linking requires verified callback-bound proof, not caller-supplied provider/issuer/subject strings. The private `b834bd71` route fails this check; it cannot be accepted merely because broader provider work is scheduled for P07. Last-sign-in-method protection must survive concurrent external-login/passkey removals through one transactional invariant.
- Managed profile creation targets the selected account and atomically enforces its grant limits/default. It must not implicitly create a local-only user or grant the administrator's account. Add User needs an atomic new-default-profile path, with deliberate sharing only when an existing profile is explicitly selected, and remote users need a usable invitation/credential setup for that same account.
- Setting a profile-selection PIN must not create a local-only account or grant View access. Bootstrap with an optional profile PIN still creates only the requested administrator account. The existing unique-local-account lookup denies ambiguity; P07 separately owes trusted local-entry binding and remote local-only denial.

Progress: the Applications implementation has passed five Storage and nine API/service tests and is integrated into the private authority branch. The common evaluator compiles after centralization. Neither result closes the remaining native/session/schema/account behavior checks above.

Apps service tests with fake evaluators prove service delegation only. Actual evaluator, policy handler, repository, session, and native lifecycle tests must cover the common decisions above. Final acceptance also requires endpoint inventory checks and P03–P05 integration results.

Plain English: the review checks that access changes take effect immediately, stay changed after restart, and cannot be bypassed through a different client or API path.
