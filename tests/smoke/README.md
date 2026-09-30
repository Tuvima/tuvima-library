# Disposable authenticated editor smoke fixture

This fixture uses only generated temp directories, synthetic catalogue rows, and a disposable administrator. It copies an explicit nonsecret config allowlist, disables AI downloads and provider integrations, and never scans a personal library.

From the repository root:

```powershell
dotnet build MediaEngine.slnx
./tests/smoke/New-IsolatedEngineDashboardSmoke.ps1 -Start -Keep
dotnet restore tests/smoke/SmokeAdmin/SmokeAdmin.csproj --configfile nuget.config
dotnet build tests/smoke/SmokeAdmin/SmokeAdmin.csproj --no-restore
dotnet tests/smoke/SmokeAdmin/bin/Debug/net10.0/SmokeAdmin.dll <FixtureRoot> http://127.0.0.1:61497
dotnet tests/smoke/SmokeAdmin/bin/Debug/net10.0/SmokeAdmin.dll <FixtureRoot> http://127.0.0.1:61497 --complete
./tests/smoke/New-IsolatedEngineDashboardSmoke.ps1 -SeedExistingFixture <FixtureRoot>
```

Use the `FixtureRoot` printed by the start command. The helper writes login details only to `<FixtureRoot>/smoke-login.json`; read them there for browser login and do not paste them into chat or logs. Setup completion must precede seeding. If the Engine restarts, seed again: startup removes synthetic rows whose files are absent.

Open the Dashboard at `http://127.0.0.1:5018`. The fixture includes a movie, book, audiobook, 10-track album, three-issue comic run, and 1,000-episode show. The generated work and collection IDs vary per seed; obtain them from the temporary SQLite database or Dashboard. `SmokeAdmin --probe <API-path>` makes an authenticated read-only API request.

When finished, stop only the two process IDs printed by the fixture after verifying they are the fixture's `MediaEngine.Api` and `MediaEngine.Web` executables. Then remove the generated `tuvima-smoke-*` directory directly under the system temp folder, including `smoke-login.json`. The script checks that prefix and location before its automatic cleanup when run without `-Keep`.
