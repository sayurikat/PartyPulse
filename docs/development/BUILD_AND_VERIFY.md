# Build and verification

Project files rechecked 2026-10-14. The database model Release build was subsequently verified on 2026-10-15 for test provisioning; application builds/tests remain unrun. Local SDK/MSBuild and tool version/help checks also ran. See the private local inventory described in README.md for exact executable paths and results.

## Prerequisites and build order

Keep the six checkouts as siblings. .NET SDK 10.0.401/runtime 10.0.12, Node 24.19.0 and bundled Python 3.12.14 ran successfully on the inspected Windows machine. Plain python resolves to an inactive Windows Store alias; use an actual Python installation or the bundled executable recorded in the local inventory.

Contracts targets net10.0, package version 0.3.0. Plugin uses Dalamud.NET.Sdk/15.0.0 and a sibling Contracts ProjectReference. API, website and bot target net10.0 but reference the sibling Contracts bin/Debug/net10.0/PartyPulse.Contracts.dll.

Build Contracts **in Debug before publishing the API or website**, including when publishing in Release. Build it in Debug before other consumer builds as well, and rebuild it after contract edits. The website's PartyPulseContractsProject property is unused by the active reference. Do not rely on its older README reference description. Reference cleanup is a separate change.

The bot's local csproj includes Microsoft.Extensions.Http 11.0.0-preview.6.26359.118; restore/build compatibility is unverified. SQL uses Microsoft.Build.Sql/2.1.0 and Sql170DatabaseSchemaProvider. Plugin compilation needs matching local Dalamud assets. Package restore may need scoped network/feed permission; do not install tools or change dependencies merely to hide an environment error.

The local Dalamud development DLL reports assembly version 15.0.3.2. This does not verify SDK compatibility or a successful plugin load. The normal sandbox command runner currently fails before process startup; reviewed Windows PowerShell fallback commands work. Record that separately from actual build failures. The bundled MSBuild runs through dotnet even though standalone msbuild is not on PATH. SQLCMD help runs locally; no SQL connection was made.

## Existing commands

Run each block from that repo's root. These can write bin/obj/package caches; they do not authorize deployment or live SQL.

Contracts, first:

    dotnet build src/PartyPulse.Contracts/PartyPulse.Contracts.csproj -c Debug
    dotnet build PartyPulse.Contracts.slnx -c Release

API:

    dotnet build PartyPulseAPI/PartyPulseAPI.csproj -c Release

Plugin:

    dotnet build src/PartyPulse/PartyPulse.csproj -c Release
    dotnet run --project tests/PartyPulse.NavigationTests/PartyPulse.NavigationTests.csproj -c Release

Navigation checks are a console executable; use dotnet run.

Website, with a working Python interpreter:

    python scripts/validate-source.py
    node scripts/test-reporting.mjs
    dotnet build src/PartyPulse.Web/PartyPulse.Web.csproj -c Release

Bot:

    dotnet build PartyPulse.DiscordBot.slnx -c Release
    dotnet test PartyPulse.DiscordBot.slnx -c Release --no-build

SQL model:

    dotnet build PartyPulse.Database/PartyPulse.Database/PartyPulse.Database.sqlproj -c Release

SQL builds do not run rollback tests or verify deployed schema. API and Contracts had no dedicated test projects in the inspected inventories.

## PartyPulse plugin releases

Use this sequence when the owner authorizes a PartyPulse plugin release to `sayurikat/PartyPulse`.

Release from the canonical PartyPulse checkout and include all of its current changes by default. Review the tracked and untracked source, keep ignored local files private, and leave the canonical checkout on the release commit. Build with the current sibling Contracts source and record its base revision and any local changes needed to reproduce the bundled DLL. Use an isolated or narrower release scope only when the owner explicitly asks for it.

1. Inspect local status, the remote `master`, the public plugin listing, published and draft GitHub releases, and local and remote tags. Use `gh release list --repo sayurikat/PartyPulse` and `git ls-remote --heads --tags origin`. Choose an unused four-part version higher than the current listing and releases. Increase `<Version>` in `src/PartyPulse/PartyPulse.csproj`. Set the Party Pulse entry's `AssemblyVersion` to that version and both download links in `repo/pluginmaster.json` to `https://github.com/sayurikat/PartyPulse/releases/download/<version>/latest.zip`.
2. Commit the release changes and record the exact PartyPulse commit and the sibling Contracts source used in the build. When a prior release is ahead of the canonical checkout, preserve the current source and verify that the new release descends from public `master`. With dependencies restored, run the following from the PartyPulse root.

   ```powershell
   dotnet build src/PartyPulse/PartyPulse.csproj -c Release --no-restore
   dotnet run --project tests/PartyPulse.NavigationTests/PartyPulse.NavigationTests.csproj -c Release --no-restore
   ```

   The direct project build produces `src/PartyPulse/bin/Release/PartyPulse/latest.zip`. Verify that the archive contains `PartyPulse.dll`, `PartyPulse.json` and `PartyPulse.Contracts.dll`. Compare the DLL's assembly version and source revision, the packaged manifest's version and API level, and the new listing. Confirm that the Release DLL excludes the runtime bridge. Keep `IconUrl` in both `repo/pluginmaster.json` and the packaged `PartyPulse.json` so the catalog and installed views can load the same icon. Verify the public icon URL serves the expected PNG. Record the ZIP's byte count and SHA-256 under `.local/`.
3. Verify the Git and GitHub CLI identities before publishing. If needed, temporarily select the existing `sayurikat` sign-in with `gh auth switch --hostname github.com --user sayurikat` and confirm it with `gh api user --jq .login`. Scope Git credential changes to the release commands and restore the previously active CLI account on completion or failure. If authorized access is missing, have the owner sign in or grant Contents write access to `sayurikat/PartyPulse` through a repository-scoped credential. Keep credentials out of source, logs and chat.
4. Push the bare version tag at the exact release commit. Create a GitHub draft release for that tag with `gh release create --verify-tag --draft`, title it `v<version>`, and attach the verified `latest.zip`. Check the draft's tag, asset name and size. Compare GitHub's asset SHA-256 digest with the local hash or download the draft asset with authenticated access and hash it.
5. Publish the verified draft. Download `latest.zip` from the public release URL without credentials and compare its SHA-256. Then fast-forward `master` to the release commit so Dalamud only sees the new listing once the download works. Confirm the public listing and remote refs, and record the release and asset URLs in the local release record.

If publication stops partway, inspect the remote tag, draft or published release, asset digest and `master` before retrying. Resume only when existing release objects match the reviewed commit and package. If the asset is public and verified, finish the pending fast-forward. If the commit or package must change, use a higher version and leave existing tags intact.

## Change-specific verification

For auth changes, check save-before-confirm, lost response/confirmation recovery, preserved expiry, cancelled/expired/revoked credentials, disabled users, venue/device isolation, character-link conflicts and concurrent refresh.

For other cross-component changes, check shapes, both client mappings, SQL permissions and bot dependencies. Report actual builds/tests, static inspection, environment failures and pending live/manual checks separately.

### Public opening Overview

Apply `stored-procedures-source/api.Venue_GetPublicOpeningByCode.sql` in the target database using administrator SSMS. The script creates the new read-only procedure and grants the API identity object-level execute access. Its optional rollback fixture is restricted to `partypulse_test`. Build Contracts Debug, then deploy the API before releasing the plugin. To remove the procedure, first roll back the API that calls it.

For Twitch profile pictures and periodically refreshed live stills, set `Twitch__ClientId` and `Twitch__ClientSecret` in the API's private runtime configuration. The API reads them at startup through its configuration providers. DJ names and links work when those settings are absent or Twitch is unavailable.

In an isolated test profile, add a venue by its public code and check the Overview as a visitor. Check the one-hour boundary, the transition to open, cancellation and early close, venue switching, an alternate opening address, and a giveaway's direct signup message. Verify housing travel from another plot and from the destination plot, with Lifestream available, busy and unloaded. For an outdoor opening, verify world travel from a different world and the displayed outdoor location on arrival.

The website's current publish-iis.ps1 removes its chosen output directory without a containment check. Inspect/fix its output-path handling in a separately requested task before relying on it. Do not run deployment scripts during orientation.

## 2026-10-15: database model verification

The current SQL project built Release successfully after restoring its existing dependencies. An initial --no-restore attempt found stale assets for the SDK-required netstandard2.1 target; normal build/restore resolved that without changing project dependencies. There were zero errors, 12 SQL71502 warnings for sys.sp_getapplock/sys.all_objects references and one newer-SDK advisory. No model/procedure edit or SDK upgrade was made.

The compiled model contains 98 tables, 170 procedures, two functions, three sequences and three table types. The reusable offline generator and test-database instructions live in the [database repo](../../../PartyPulse.Database/docs/TEST_DATABASE.md). PowerShell and generated SQL 170 parsing passed; package transfer hashes matched. These are local/static checks. Administrator local SSMS subsequently connected through the restricted SSH tunnel and confirmed the intended instance and absent test target. The earlier non-SQLCMD run produced directive/compile errors; the user subsequently completed corrected SQLCMD execution. The supplied final result reports Verified with all expected counts and zero UserRows; this is user-run live evidence, not agent-authenticated SQL execution. No application build or game test was run.

## 2026-10-15: local test API startup

Built Contracts Debug, then API Debug with zero warnings/errors. API initially failed NuGet audit due to transitive PKCS 9.0.13; an explicit patched 9.0.21 reference resolved NU1902/NU1903 without suppressions. The test launcher isolates content root/environment and binds loopback only. PowerShell/SQL parsing, 200 liveness and 401 unauthenticated private-route checks passed. No SQL readiness request was made before test-login provisioning. Full application/client/bot tests remain unrun.

## 2026-11-09: live test API smoke check

Used the existing verified Debug build, with no new build or deployment. The local test API bound to loopback only. Liveness and SQL readiness each returned 200 Healthy. A valid unseeded venue code returned HTTP 404 with the application code VENUE_NOT_FOUND from the read-only SQL procedure. An unauthenticated private route returned 401. These checks verify connection and one procedure call; they do not verify all permissions or a successful populated feature flow.

## 2026-11-09: local development baseline

Built Contracts Debug followed by Release builds of the plugin, website, API and SQL model using existing restored dependencies. All passed; the plugin reported 42 compiler warnings and zero errors, and the other build invocations reported zero warnings/errors. All 16 navigation checks, website source validation and reporting JavaScript checks passed. The navigation build used scoped local execution after the sandbox could not overwrite an existing output DLL. These checks do not load the plugin into the game, sign into the website, apply SQL changes, or deploy anything.

The one-venue fixture was parsed successfully with SQL Server 170 ScriptDom. It is staged for user-run SSMS only and was not executed. The API's previously verified read-only lookup is the follow-up check after the sample row exists.

## 2026-11-09 opening edits and DJ payments rollout

The user reported applying the SQL updates. The first API publish failed. After building Contracts in Debug, the user published the API and website, built the plugin, and confirmed PartyPulse was running.

## Giveaway winners verification

Apply the giveaway winner schema migration and the affected procedure scripts in the order listed in the database guide before updating the API. Build Contracts Debug first, then build the API, bot, website and plugin.

In an isolated venue, use an owner or grant `venue.finance.settlements` to a payout operator. Test a separate user without that permission and a user from another venue. Verify a manual giveaway and a scheduled giveaway carry the expected gil prize into Discord. Enter once with a guild nickname and once without one. Check that a draw creates one notification, the toast logs the win in chat, and both Main and Mini show the winner.

Configure an announcement such as `/sh Congrats <discordname>! Please come collect your prize.` Run it twice and check the last-announced time. Configure the giveaway advertisement under Timed Macros and verify both run buttons while entries are open and their disappearance after close. Target the verified in-game winner, check that one Dropbox reservation blocks other operators, and confirm only after the full prize has been delivered. Exercise release after 30 seconds, retry and cancellation with a different winner.
