# PartyPulse decisions and state

## 2026-10-14: local migration

User-established decisions:

- Local repositories, including uncommitted work, are source of truth.
- User handles GitHub pushes; other external-change boundaries remain in the adapted standards.
- Maintain project-local guidance and tools/access inventory.
- First establish structure and wishlist; SSH and FFXIV runtime bridge follow later.

Organization chosen for this setup:

- Shared docs in PartyPulse/docs/development/; concise AGENTS.md in all six checkouts.
- Preserve supplied references separately. The old project mirror is historical context; the six local checkouts are the maintained workspace.
- Keep machine paths/account preference, exact hashes and local-work baseline in ignored .local/agent-setup/2026-10-14/.

Observed: six checkouts and migration-matching HEADs; existing bot edits; .NET 10 works here; API/web/bot pin Contracts Debug DLL; Valheim reference uses a private authenticated local pipe with no screenshot operation.

Open work:

- Stabilize normal sandbox shell; approved fallback worked.
- Verify restores/builds/tests and matching Dalamud assets during later development.
- Establish SSH endpoint/account/key provisioning and remote inventory.
- Identify deployed versions/schema and API/web/bot hosting details.
- Review pre-existing bot work when relevant to a requested task.
- Resolve bridge placement/initial tools before implementation.

This setup is documentation only; it does not establish production state or future live-operation permission. Add dated decisions and evidence without credentials.

## 2026-10-14: recheck in the actual local project

Confirmed all six PartyPulse repositories are writable roots in this chat. Retained repository-root AGENTS.md files and one shared guide set in PartyPulse/docs/development/. A parent repos/AGENTS.md would not reliably replace repository-root discovery and would mix unrelated projects. No global instruction or permission configuration was changed.

Reviewed the supplied documents and Valheim source reference again, alongside current local flows. Corrected the stale project-mirror access claim, preserved the existing bot changes, and recorded pre-edit hashes/status. Added migration conflict/provenance notes and expanded the access wishlist. Original Markdown references, machine/tool inventory, baseline and task-only delivery live under ignored .local/.

Ran tool metadata/version/help checks: .NET SDK/MSBuild, Git, Node, bundled Python, SSH and SQLCMD. Development Dalamud assembly metadata reports 15.0.3.2. Normal sandbox process creation still failed; scoped reviewed Windows PowerShell commands succeeded. The file-edit helper also rejected workspace path traversal through a reparse point; approved in-scope file writes through the working runner succeeded. No permissions were weakened to resolve these issues.

FFXIV bridge first stage is proposed typed diagnostics via a private local connection. Do not copy the Valheim reference's default mutation/evaluation permissions, raw audits or raw log projections. Remote website integration remains a separate transport/design concern.

Application builds/tests, restores, SQL execution, SSH authentication and game connection remain pending. This task changed local guidance and its private inventory/delivery only. Existing bot application edits were preserved; deployed state remains unknown.

## 2026-10-14: SSH bootstrap verified

Following the user's explicit request to establish server access, the administrator enrolled a dedicated standard account with public-key-only authentication and IPv4 source-scoped firewall rules. Existing Windows firewall rules were preserved. A host-private-key ACL correction resolved the service startup failure; the host key was retained. Source restrictions use an allow rule and a complementary block rule together.

The user enabled the local Windows SSH agent and loaded the client key interactively. Verified authenticated SSH with strict checking against the server public key obtained through the trusted RDP console. The account/computer match the intended server; the token has no Administrators-group SID. Exact identities, paths, fingerprints, rule details and evidence remain in ignored local storage.

Read-only access is limited: SQL instance-name metadata is readable, but IIS configuration, CIM queries, ordinary service enumeration and scheduled-task queries are unavailable. Prepared an administrator-run read-only hosting inventory rather than granting broad administrator access. Application paths, database names/access, deployed revisions, backup/rollback details and selected log permissions remain to be established. No live SQL connection, application change, deployment or game connection was performed.

## 2026-10-14: hosting inventory and current deployment practice

The user-run administrator inventory identified API/web IIS hosting, bot service/executable, SQL instance and installed runtime versions. SSH read-only file inspection confirmed deployment roles, selected website environment/API URL settings and embedded source revision identifiers. API, website and shared Contracts identifiers each resolve to one ancestor commit behind their respective local HEADs; bot identifier matches local HEAD. This is build metadata, not proof that all deployed files exactly match those source trees or include uncommitted work. Exact paths, revisions and evidence remain ignored local inventory.

User reports no test database. API and website are published directly from Visual Studio into the live IIS folders; bot files are copied and run manually for console inspection or as a service. Git provides source/schema history. Database contents are not backed up under the reported process. No published-artifact restore procedure has been established. Record this current practice; do not infer authorization to deploy, create a test database or configure backups.

API database name/target/authentication and the bot's effective endpoint remain unresolved; selected IIS pool/service override export is prepared. Deployment folders are readable, but IIS access-log enumeration and management queries remain unavailable to the standard SSH account. Future access should target selected logs or curated diagnostics rather than broad administrator rights. No SQL connection, application restart or external modification was performed during inventory.

## 2026-10-14: configured database and remaining access scopes

The user's administrator override export identifies the API database server/name and SQL-credential authentication from its IIS pool setting, plus the machine-wide API URL used as a default environment override for the bot. It also identifies the IIS site ID and conventional W3C access-log path. Exact values and safe exported metadata are retained in ignored local inventory; credential values were not collected.

These are declared settings. They do not verify the configured SQL server-to-installed-instance mapping, a database connection, the running bot's inherited environment, API/bot environment labels or the complete deployed source/configuration. Preserve those distinctions. No application or SQL change was made.

The initial server inventory now supports a concrete wishlist: selected log reads/curated diagnostics, a separately scoped SQL diagnostic identity, database-content backup/restore and an isolated test database, followed by local FFXIV runtime integration when requested. Administrator exports remain available for protected metadata. Do not infer broad administrator, SQL sysadmin, deployment, backup creation or game-action permission from this inventory.

## 2026-10-15: isolated test database requested

The user requested establishing a test database from the local database repo and confirmed an administrator SSMS session. Prepare a fresh empty schema named partypulse_test from the current local model. The user supplied SQL Server 2025 Express preflight results with no existing test target and matching production/model collation. Exact instance/default paths and evidence remain private. No production database data is copied or application connection changed.

Built the existing SQL project successfully after restoring its declared dependencies: zero errors, 12 existing unresolved system-reference warnings plus one SDK advisory. Added reusable provisioning/generation sources outside the model directory. The private generated package is fixed to the confirmed SQL instance and test name, refuses existing targets/nonempty schema, and includes expected-object/constraint checks plus a synthetic insert/rollback. Schema DDL/database creation are persistent; rollback verifies only the synthetic row. Existing model/procedures and connectivity diagnostics are unchanged.

Local SQL 170/PowerShell parsing passed. Staged the package in a new private server profile folder and verified every file hash. An initial stdin transfer timed out before creating a staging folder; inspection confirmed no partial extracted files, and bounded legacy SCP succeeded. Administrator SQL execution/final Verified row remain pending. Execution follows the existing manual SQL workflow in SSMS; the agent did not acquire a SQL administrator credential or grant the standard SSH account new SQL rights. Production backup/restore, reference fixtures and separate test API integration remain later work.

## 2026-10-15: local SSMS over restricted SSH forwarding

The user chose SSMS on the local PC while SQL Server and the test database stay on the remote server. The first setup run lacked SQLCMD Mode and reported directive/compile errors. Follow-up output found no matching partial setup objects in master; a later authenticated local SSMS query confirmed the intended instance and an absent test database. Do not treat compile errors alone as evidence of database changes or success.

The administrator enabled local SSH forwarding limited to the SQL loopback destination, with remote forwarding denied, config backup and validation. Only SSH was restarted; no SQL service or firewall change was made. A hidden, localhost-only client tunnel uses the existing pinned host identity and SSH agent. SQL credentials remain in the user's SSMS session; the agent has not received a SQL login or password. Exact endpoints, process identity and backup path are recorded privately.

The corrected setup is to open the local instance-bound package in a fresh SSMS window, select master, explicitly enable SQLCMD Mode in that same window, then execute the entire script once. Final Verified output is still pending. Do not rerun against an existing or partial test target without inspection.

## 2026-10-15: remote test database verified

After a short SQLCMD_OK probe confirmed the intended remote instance, master context, zero open transactions and absent target, the user executed the instance-bound setup package with SQLCMD Mode enabled. The supplied result screenshot reports partypulse_test / Verified, 98 tables, 170 procedures, two functions, three sequences, three table types and zero UserRows. It also shows the preserved API schema diagnostic and an empty TestRead result. This is user-run live verification evidence, separate from agent-run local build/parser/patch checks.

The test database now exists on the remote SQL instance alongside production. It represents the current compiled repository schema with no production data copied. The verification synthetic insert was rolled back; database/schema creation persists. No application connection switch, fixture seeding, test application identity, production schema comparison or production backup/restore was performed. The creation package is for one-time fresh provisioning and must not be rerun against this existing target. Next integration work needs dedicated test access, synthetic reference data and a separate test API configuration.

## 2026-10-15: first integration milestone kept small

Following the user's request to proceed, prepare a local API using the remote test database over the restricted tunnel. First establish a dedicated test SQL identity and readiness, then choose minimal synthetic fixtures for a specific feature. The user manually applies rendered SQL in SSMS. Generated credentials remain protected in ignored local storage; test JWT/bot keys are independent of production. No production deployment, database permission change or real Discord worker start is part of local preparation. A patched transitive PKCS reference was required by the existing audit-as-error build policy; both Contracts and API Debug now pass.

## 2026-11-09: first integration milestone verified

The user applied the test-only login SQL manually and supplied the successful final SSMS result. The API remains on the developer PC with the database on the remote server. The dedicated login passed live readiness and a read-only procedure smoke check through the pinned tunnel. No production settings or database rows were changed during verification. The next milestone is one synthetic test venue for a successful read path, before connecting any client.

## 2026-11-09: unattended test module updates requested

The user created one fictional venue and requested unattended SQL updates in the context of the test database. The local API returned the expected populated venue. Prepared a separate, object-scoped test-only updater for existing procedures and functions, with offline planning, rollback rehearsal, and explicit apply. The user ran the one-time administrator script to provision the login. Actual login verification and a rollback-only module update passed, and the sample venue remained readable through the API. New objects, table changes and production deployment are outside this first workflow. See [test updater guide](../../../PartyPulse.Database/docs/UNATTENDED_TEST_UPDATES.md).

## 2026-11-09: unattended production API and SQL release setup

The user asked for an unattended path for future production releases. The existing SSH account uploads owner-approved packages and starts fixed on-demand tasks. A separate API worker operates on the API physical root. A SQL worker updates ten allowlisted archive and recovery procedures. A separate backup worker verifies the release backup before SQL, and combined releases require a SQL success receipt before API publication. The owner signs each exact package locally. New SQL objects, tables, permission changes and procedures outside the allowlist retain the administrator route.

The administrator installed the tasks, scoped accounts and SQL release ledger. Scheduler startup initially failed with Windows error 1385. Granting the three worker identities the batch-logon right resolved the failure. All three tasks reported ready over SSH with an empty queue. The ten SQL definitions matched their previous release and production health checks passed. Full backup, SQL apply and API rollback were rehearsed locally. No signed production feature package was applied in setup.

## 2026-11-09: signed website deployment setup

Use the existing owner approval key for website packages. The website uses a website-only manifest, separate inbox, dedicated standard worker and fixed `ApplyWeb` task. Its deploy scope is `/web`, with live configuration, logs and the persistent Data Protection directory protected from worker writes. Existing API and SQL tasks retain their release format and routing. Submit one production release at a time.

The corrected installer matched the live IIS apphost form, and the administrator completed installation. The first SSH task start verified permissions and returned ready. A real website publish and rollback were rehearsed locally. The first owner-signed production website release returned Applied for a newline-only dependency manifest change, and all three public health endpoints passed. Each release still requires the owner's approval. On the owner's PC, signing accepts paste in a masked Windows password dialog after the release ID confirmation.

## 2026-11-09 - Staff Directory archived listings

The plugin Staff Directory shows archived listings by default only when they have at least one unfinished offboarding task. Show all archived staff reveals the full list and resets when the venue changes.

The Debug x64 build passed with 42 existing warnings and zero errors. All 16 navigation checks and seven local filter scenarios passed. The running Dalamud client logged a successful reload after the new DLL was written.

## 2026-11-09 - VIP Arrival Tracker tabs

Main mode opens the tracker under VIP > Arrival Tracker. Mini mode offers VIP Arrivals without requiring a target. Both tabs share the same venue and permission checks. Opening and macro administration remain under VIP > Arrivals in Main mode. Nearby scanning and arrival polling run while the tracker is open on the selected tab.

The Debug x64 solution build passed with 42 existing warnings and zero errors. All 16 navigation checks passed. The active Dalamud profile automatically reloaded the DLL from the solution output, as confirmed by the client log. The tracker controls were not exercised in game.

## 2026-11-09 - VIP registration without a purchase

VIP Sales offers Register only in the package selector in Main and Mini. A seller enters a Discord username for the current target. Registration uses the same venue, actor and identity checks as a sale, and returns the linked character before any purchase or arrival is recorded. A matching orphan Server Booster record can become the character's VIP entry or merge into a VIP entry already linked to the character. Other username conflicts require choosing the correct existing player or resolving the conflict. A stale bot snapshot defers orphan creation when one unbound VIP username matches, so the next reconciliation can link the Discord account to that player.

Apply the Discord identity procedure, then the VIP procedure, then the API with current Contracts, then the plugin. Both SQL scripts include opt-in rollback rehearsals for `partypulse_test`. Local builds passed for Contracts, API, website, plugin and database model. Navigation, offline API and Discord identity matcher checks passed. Administrator SSMS applied both procedures after verifying a copy-only backup. The owner-signed API release applied and the deployed file hashes and health checks passed. The Debug x64 solution built and Dalamud logged a completed reload at 07:38:21 Oslo time. Authenticated in-game registration is pending verification.

## 2026-11-09 - VIP Discord role management confirmation

VIP package creation and updates preserve the caller's Discord role management confirmation when normalizing the request. SQL uses that value to require confirmation when creating a package with a role or adding, replacing, or removing a role on an existing package.

On API base 5d9a5989, the offline service check dropped all three explicit confirmations. With the fix, all nine cases passed for package creation with a role, role changes, and role removal with true, false, and omitted confirmation. Contracts Debug, API Release, and five API auth checks passed. Deployed verification remains pending.

## 2026-11-09 - Giveaway winners and payouts

Main and Mini share a queue of drawn, unresolved winners. The announcement macro is saved on the device for each venue. The `<discordname>` placeholder uses the guild nickname captured at signup, or the recorded Discord username. Each successful macro start updates the shared last-announced time. Draw notices use the existing notification poll and appear in the local game chat log when the toast opens.

The operator selects and verifies the winner's in-game character. PartyPulse reserves the payout before asking Dropbox to start the trade. The operator confirms the completed trade, or verifies that no gil moved and releases the reservation before retrying or cancelling. Manual payouts can be confirmed directly. Resolved winners leave the queue.

The exact `venue.finance.settlements` permission uses the existing venue owner inheritance. It gates the queue, draw notices, prize changes and the built-in giveaway advertisement macro. Giveaway editors also need their existing giveaway management permission. Scheduled giveaways copy the configured prize when created. The advertisement timer is shared per venue and runs while at least one posted giveaway is accepting entries.

## 2026-11-09 - Plugin release workflow and installed icon

Use the [plugin release runbook](BUILD_AND_VERIFY.md#partypulse-plugin-releases) for each requested release and update the shared guides as the workflow changes. The `0.1.7.2` release verified the staged order on GitHub. The release asset was published and downloaded successfully before `master` advanced to the new listing.

Dalamud uses the catalog `IconUrl` before install and the packaged `PartyPulse.json` `IconUrl` in the installed view. The `0.1.7.3` icon fix keeps the same PNG URL in both manifests. Include their match in the archive verification for future releases.

The owner set the default release scope with `0.1.8.0`. Include all current changes in the canonical PartyPulse checkout and build with the current sibling Contracts source. Use an isolated checkout or a narrower scope only at the owner's explicit request.

## 2026-11-09 - Public opening Overview

Visitors use the selected venue's public code to load the Overview. The opening appears one hour before its start and disappears at close. An active opening takes precedence when the next opening is also within the hour. The feed includes temporary openings under the same rules as scheduled ones. The plugin refreshes while the Overview is visible and advances the countdown and set labels locally.

Use the opening address for travel. Lifestream takes housing destinations through its typed API. Outdoor openings store a world and a free-text place, so the travel action for them goes to the destination world. Twitch media uses optional server-side app credentials and the official Twitch image host. Signup buttons open the posted Discord giveaway message.
