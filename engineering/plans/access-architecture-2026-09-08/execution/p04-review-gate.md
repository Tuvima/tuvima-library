# P04 review gate

The initial private View delivery `e2b78c22` passed 45 focused API and nine Storage tests. It is **not accepted**: review found uncovered production paths and authorization fallbacks.

Correction checkpoint `b281adfd` removes the production fallback constructors and fabricated authority, and requires the Shared asset library to match the authorized scope. Root reviewed those removals; 45 focused worker tests pass. The route, contribution, collection, Shared-source, and complete matrix requirements below remain open. This checkpoint is not a completed P04 delivery.

- Remove the fallback `HttpViewRequestProfileContext` constructor and legacy profile conversion that create an enabled human account with a random ID. Require the real authority resolver. Remove the production `AllowTestEvaluator` and old profile-only authorization overload. Test fakes belong in tests and must be explicitly supplied.
- Apply account View and live delegated/Application permission checks before scopes, preferences, share targets, counts, folder queries, mutations, and contribution operations. A bare active profile ID is identity context, not proof of access.
- Replace remaining profile-role checks in collection personal-media services. Contribution decisions, retries, and direct Shared additions require effective human administrator authority plus the independent contribution policy; contributor operations remain exact-owner scoped.
- Complete real Shared source CRUD/reconcile endpoints and persisted multi-source behavior. Private-profile source administration remains explicitly scoped. A Shared marker alone must not bypass checking the authorized Shared library identity.
- Audit every View and collection-personal-media registration against the endpoint matrix. Remove legacy role helpers from the new route contract, and verify both ordinary service denial and the available, audited, exact-profile administrator Application read exception.
- Register request context and resource authorization with lifetimes compatible with the scoped common evaluator; remove the duplicate singleton context registration.

Required evidence includes actual disabled-account/grant/Application and missing-View cases across these routes, exact-profile isolation, Gallery membership/revocation, multiple Shared sources, and the existing verified-transfer/original-file safety tests. The passing initial subset cannot substitute for those cases.

Plain English: the new storage work is useful, but every way of entering View must use the same trusted account decision before private information is listed or files are changed.
