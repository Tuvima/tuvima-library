# Native client API v1 conformance

Native clients consume only the Dashboard origin and the public `/api/v1`
surface. The executable fixtures live in `tests/fixtures/native-client-v1` and
are validated against `MediaEngine.Contracts` by
`NativeClientContractFixtureTests`.

Client rules:

- discover the server through `/.well-known/tuvima` or an explicitly entered
  Dashboard HTTPS address;
- treat the address the person typed (or the one discovery was reached on) as
  the server's home address, and read the optional `outside_address` field of
  the discovery answer as its address away from home. It is an origin such as
  `https://tuvima.example.com` or a Tailscale `https://host.tailnet.ts.net`,
  and is `null` or absent when the owner has not set up a way in from the
  internet. Try the home address first, fall back to the outside address, and
  keep the same sign-in on both: tokens belong to the server, not to one
  address;
- never connect to the Engine port or receive the Dashboard service key;
- ignore additive JSON fields that the client does not understand;
- treat bearer tokens, refresh tokens, and signed HLS URLs as secrets;
- follow `recommendedDelivery` from the playback manifest;
- bind all progress and queue mutations to the profile and device claims in the
  access token;
- use `OfflineVariantDto.downloadUrl`, never a server filesystem path;
- re-pair to switch profiles until a separately approved public profile-switch
  contract exists.

Changing an existing v1 field name, type, meaning, route, or required status is
a breaking change and requires a new major API path. New optional fields are
allowed when all clients remain tolerant readers.
