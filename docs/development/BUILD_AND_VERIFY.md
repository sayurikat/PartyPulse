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

For an authorized plugin release, check GitHub for the latest published version and existing tags. Increase the four-part version in `src/PartyPulse/PartyPulse.csproj` and match it in the Party Pulse entry's `AssemblyVersion`, `DownloadLinkInstall`, and `DownloadLinkUpdate` in `repo/pluginmaster.json`.

Build the plugin project directly in Release to produce `src/PartyPulse/bin/Release/PartyPulse/latest.zip`. Verify that the archive contains the plugin DLL, its manifest and the Contracts DLL. Check that the assembly and manifest versions match the new listing and that the runtime bridge is excluded.

Tag the exact release commit with the bare version number. Use `v` followed by the same number for the GitHub release title and upload `latest.zip`. Verify the published download before advancing `master` with the new listing, so Dalamud sees an available package when it offers the update.

## Change-specific verification

For auth changes, check save-before-confirm, lost response/confirmation recovery, preserved expiry, cancelled/expired/revoked credentials, disabled users, venue/device isolation, character-link conflicts and concurrent refresh.

For other cross-component changes, check shapes, both client mappings, SQL permissions and bot dependencies. Report actual builds/tests, static inspection, environment failures and pending live/manual checks separately.

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
