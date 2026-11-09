# PartyPulse project map

Local source inspected 2026-10-14. Orientation, not an exhaustive code/security audit. Recheck current source for each task.

## Repository registry

All remote identities belong to sayurikat; development uses local sibling checkouts.

| Directory | Branch / inspected HEAD | Role / entry points |
|---|---|---|
| PartyPulse | master / 625d15a | src/PartyPulse/Plugin.cs, Configuration.cs, Api/, Authentication/, feature managers, Windows/ |
| PartyPulse.Contracts | main / 605ff9d | src/PartyPulse.Contracts/*Contracts.cs |
| PartyPulseAPI | main / de73ace | PartyPulseAPI/Program.cs, Auth/, Authorization/, Features/ |
| PartyPulse.Database | main / 201d3c9 | stored-procedures-source/, deployment-scripts/, PartyPulse.Database/PartyPulse.Database/ |
| PartyPulse.Web-repo | main / 2d66ef3 | src/PartyPulse.Web/, docs/, scripts/ |
| PartyPulse.DiscordBot | main / 7abf93d | src/PartyPulse.DiscordBot/, tests/PartyPulse.DiscordBot.Tests/ |

At the local recheck, all six contained untracked AGENTS.md files and PartyPulse contained the shared development guides from the earlier attempt. The bot additionally had existing modified/untracked application and test work. HEAD alone does not represent a dirty checkout. Exact status and pre-existing-work hashes are retained in the ignored local inventory. No remote refresh or production inspection was performed.

## Source navigation

Plugin / website / Discord bot -> HTTP and shared Contracts -> PartyPulseAPI -> SQL Server.

API features generally follow controller -> service -> SQL repository. Features/Shared/SqlFeatureReader.cs and feature-local reader helpers map returned columns and UTC timestamps. SQL result names/types and result-set order must match API mappings, Contracts and clients. A representative traced flow is Contracts VipPerksContracts.cs -> Features/VipPerks/VipPerksController.cs -> VipPerkService.cs -> SqlVipPerkRepository.cs -> declared api.VipPerks_* objects.

Plugin and website have independent PartyPulseApiClient implementations; inspect both. Plugin managers maintain snapshots consumed by window/tab renderers. Game boundaries include Services/, Vip/GameMacroExecutionService.cs, Finance/SettlementTradeService.cs, PartyFinder/ and Integrations/ under src/PartyPulse/.

Website uses MVC/Razor. Start with Infrastructure/Auth/WebApiSessionService.cs, Infrastructure/Api/PartyPulseApiClient.cs, Infrastructure/Operations/, Infrastructure/Presentation/, Infrastructure/Reporting/ and Platform/IGameBridge.cs. NullGameBridge leaves game-dependent capabilities unavailable.

Only game-bridge status is currently consumed. Its macro/trade methods are unused placeholders, not an existing game transport. The local agent runtime bridge and any future remote website integration are separate design concerns. Website operation results remain in memory for ten minutes; local hosting requires a writable DataProtection-key directory under App_Data.

Existing website references: [architecture](../../../PartyPulse.Web-repo/docs/ARCHITECTURE.md), [game bridge](../../../PartyPulse.Web-repo/docs/GAME_BRIDGE.md), [reporting](../../../PartyPulse.Web-repo/docs/REPORTING.md), [workbench](../../../PartyPulse.Web-repo/docs/FEATURE_WORKBENCH.md), [coverage](../../../PartyPulse.Web-repo/docs/FEATURE_COVERAGE.md), [IIS deployment](../../../PartyPulse.Web-repo/docs/IIS_DEPLOYMENT.md). Some reference descriptions lag current project files; verify the implementation.

Bot entry points include GuildReconciler.cs, DiscordIdentityMatcher.cs, DiscordRolePlanner.cs, PartyPulseBotApiClient.cs, GiveawayProcessor.cs and VenueStatusProcessor.cs. Its existing local edits need their own review before being treated as verified behavior.

Database: use the [README](../../../PartyPulse.Database/README.md), complete scripts and SQL-project definitions. Folder names do not determine object types: api.Authorization_HasPermissionForActor is a function. Compatibility scripts can declare the same wrapper despite different filenames.

Giveaway prizes and winner settlement flow through `GiveawayContracts`, `GiveawaysController`, `SqlGiveawayRepository` and the `api.Giveaway_*` procedures. `api.GiveawayBot_ClaimWork` selects the winner and creates the notification in one transaction. The bot captures the Discord guild nickname when an entrant signs up. The plugin's shared Giveaways renderer serves Main and Mini. Winner actions use a separate API capability guarded by `venue.finance.settlements`.

Specifically, api.Self_LeaveVenue.sql and api.Self_LeaveVenue-compatibility.sql declare the same wrapper. Complete sources do not contain the intentional model-only api.Test and api.TestRead diagnostics; preserve them. The limited source/model textual comparison is recorded in [migration review](MIGRATION_REVIEW.md), not treated as compilation or live verification.

## Preserve these behaviors

- Actor identity comes from validated UserId + VenueId + DeviceId + CharacterId claims. Public venue codes identify venues; they are not credentials.
- Refresh-token hashing uses decoded random bytes rather than Base64Url text.
- Save returned refresh credentials before explicit /auth/confirm. Preserve per-device serialization and lost-response/confirmation recovery.
- Recovery of a pending refresh token preserves its original expiry. Confirmed, recovered and cancelled rotations are distinct states.
- Keep CHARACTER_NOT_LINKED separate from generic authentication failure to permit explicit character linking.
- Bot authentication uses distinct HMAC keys and durable claim/complete/fail work leases. Preserve idempotence and guild/venue boundaries.
- Website refresh coordination and some bot replay tracking are process-local in the reviewed baseline; verify before scaling instances.
- Reports derive attendance rows from explicit time entries/absences. Planned absences do not lower reliability; finance fields and export have separate permissions.

The product already includes substantial venue, staff, VIP, publications, finance, reporting, Discord and other modules. Older milestone README language can lag source. Inspect a feature before treating it as missing.

Subsequent server inventory recorded embedded build revision identifiers, hosting and selected configured endpoints in ignored local access records; see [access status](ACCESS_PLAN.md). Complete deployed-source equivalence, live schema, effective per-process settings and current backlog remain unverified. Historical migrations are not instructions to reapply every script.
