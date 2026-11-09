# Venue user archive and recovery

Implemented locally on 2026-11-09. In the plugin, Users > Directory > Edit opens, expands and focuses the selected editor. Users > Archived lists disabled accounts with restore actions. The website follows the same archive and restore rules.

Archive is scoped to one venue. It preserves the user, links, permissions and history, disables staff access, revokes devices, ends sessions and invalidates outstanding credentials for that account. New staff requests check the live user, device and character state even when the JWT has not expired. Public endpoints retain anonymous access. The server rejects archiving yourself or a venue owner. Archived profiles and permissions cannot be edited until restored.

For a linked account, restore requires restore and recovery permissions, preserves links and roles, and issues a 24-hour recovery key without reactivating old devices. The code is shown for copying and is not printed in plugin chat. For an unlinked account, use the existing invite restore flow; it clears old role grants. The directory adds capability properties without replacing the existing endpoint or legacy invite response. The bot does not consume this directory, and its SQL roster already excludes disabled users.

## Deployment and verification

Use the ordered standalone SQL files in the task delivery, then deploy the API, then update the website and plugin. The API requires `api.Auth_ValidateActor` before accepting staff JWTs. The three new procedures require administrator creation. The unattended updater is limited to its existing test database object grants.

On 2026-11-09 the administrator applied the guarded archive SQL release to the shared production database after a verified copy-only backup. The release installed ten procedure definitions and confirmed the API user's execute rights. The API was then published from Visual Studio. SSH confirmed the new code in the deployed assembly, liveness and SQL readiness returned HTTP 200, and an anonymous request to the private self endpoint returned HTTP 401. The user confirmed that Archive user appears in the authenticated Club Pulse directory editor. The ignored rollout record contains the backup, prior API snapshot and deployment verification. The shared test database did not receive this release.

The regression fixture at PartyPulse.Database/scripts/test-environment/Test-VenueUserArchive.sql requires a disposable database named partypulse_archive_regression. It rolls back test rows, not deployed definitions or identity/sequence counters. Plan definition rollback and API/client compatibility separately; never re-enable revoked credentials to roll back code.

Contracts Debug and plugin, website, API and SQL Release builds passed, with existing plugin and SQL warnings. All 16 navigation checks, five JWT staff access checks and website checks passed. SQL 170 parsing and complete/model comparisons passed. All ten upgrade scripts and the SQL rollback fixture passed on isolated SQL Server 17 LocalDB. Loopback HTTP tests covered archive, old-token rejection before and after restore, public lookup, recovery code no-store, new-device recovery, confirmation and restored access. The disposable instance was removed. Hands-on checks of FFXIV window focus and browser interaction are pending.
