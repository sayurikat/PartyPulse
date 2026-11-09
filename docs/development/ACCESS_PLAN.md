# Access and permission plan

Updated 2026-10-15. Proposed capabilities remain separate from authorization. SSH access is verified. The user explicitly requested an isolated test database from the local database repo; its schema build/package and server staging are complete. The user completed SQLCMD setup on the remote SQL instance and supplied the final Verified result: 98 tables, 170 procedures, two functions, three sequences, three table types and zero data rows. Local SSMS access is verified; exact route/process metadata is private. Production SQL changes, application deployments and runtime implementation remain separate tasks.

## Local development

| Capability | Useful scope | Status |
|---|---|---|
| Source edits | Six canonical checkouts and ignored .local/ delivery folder | All six are current writable roots; local guidance writes authorized |
| Builds/tests | Installed .NET, Node, working Python, SQL model tools; build/package caches | .NET SDK/MSBuild, Node and bundled Python ran; application builds/restore unverified |
| Restore/documentation network | Relevant package feeds, official docs, later selected API/SSH targets | Network restricted; feed authentication unverified |
| Git | Status/history/diffs and task-specific patches | Available; protected .git directories are read-only in this profile; user handles pushes |
| Requested plugin release | PartyPulse GitHub tag, release asset and fast-forward of `master` | Verified for `0.1.7.2` with the existing repository owner sign-in and scoped access. The [release runbook](BUILD_AND_VERIFY.md#partypulse-plugin-releases) records the checks and publication order. |
| Dalamud assets/logs | Relevant runtime assets and sanitized diagnostic files | Development Dalamud.dll metadata reports 15.0.3.2; SDK/build/load compatibility unverified |
| PartyPulse status bridge | Debug build loaded as a Dev Plugin, Windows desktop account and local pipe | Verified in the running client on 2026-11-09. The restricted command runner could not connect. A scoped desktop-account probe passed. |

This chat starts in the local PartyPulse checkout. All six connected PartyPulse repositories are explicitly listed as writable roots; the previous project-mirror limitation is obsolete here. Read-only local inspection and documentation edits are within the requested work. AGENTS.md cannot change Codex's permission profile.

Normal sandbox command startup failed with helper_unknown_error / setup refresh errors before executing even a location check. Explicit Windows PowerShell with login disabled and scoped out-of-sandbox review succeeded. The cause is unverified; do not diagnose it as a missing compiler or silently weaken permissions. Recheck normal execution in a later session and use the platform's review mechanism when needed.

Verified tool availability: .NET SDK 10.0.401/runtime 10.0.12 and MSBuild 18.9; Git 2.32; Node 24.19; bundled Python 3.12; OpenSSH client 9.5; SQLCMD 15.0.1300.359. The C# compiler is present. Tool availability does not imply successful application builds, database connectivity or server authorization. Exact executable paths and checks are in the ignored local inventory.

## Access wishlist in priority order

| Priority | Requested capability or information | When it is needed |
|---|---|---|
| Now | Stable local command runner; source/build-output/cache access for the six repos | Routine development; source roots are already available, normal runner needs recheck |
| When restoring | Scoped package-feed network access and any required feed authentication | NuGet/Dalamud/SQL SDK restore; no tool installation assumed |
| SSH: verified | Dedicated standard account, public-key authentication and pinned server identity | Login and selected deployment metadata reads work; live IIS/service/task management queries and selected log access remain unavailable |
| Runtime status: verified | Debug Dev Plugin and selected local game process | Run the read-only status probe. See the [runtime guide](RUNTIME_BRIDGE_PLAN.md). |
| For integration checks | Local/staging API URL, test venue/device identity and website test session | Auth and feature checks against a known test target |
| For SQL tests | Separate test database plus scoped diagnostic/test rights | Remote partypulse_test creation/schema verification completed through user-run SSMS; separate test database identities and application integration remain pending |
| For bot tests | Test guild/channels/roles, dedicated API HMAC identity and locally stored bot token | Requested Discord integration work with specified effects |
| Recovery planning | Database-content backup with restore verification; retain published artifacts separately from source | User reports source-only Git rollback and no database-content backup; setup not requested yet |
| Later deployment | Staging upload directory and delegated, named service/app-pool operations | A separately requested deployment task |
| Optional runtime actions | Screenshots, named UI/config/game actions and their target/retention rules | Add only the capabilities needed for the selected debugging workflow |

Use project and application scopes. Broad computer access, GitHub write access and blanket server administrator/sysadmin rights are not needed for the planned first steps. A new sibling runtime repository, game development-output directory or user SSH configuration may require adding an exact writable path later. Key provisioning also needs the appropriate server administrator action; current local repo access cannot grant it.

## Remote Windows server

Start with a dedicated non-administrator account allowed to read PartyPulse logs and selected service/runtime metadata. Add a staging upload directory or named service operations later as needed. IIS configuration/service control may need delegated commands or sanitized administrator exports; do not assume standard-user visibility.

Inputs and status:

1. Endpoint/port, Windows Server version and network route: recorded in ignored local inventory; TCP reachability verified.
2. Dedicated standard account and running OpenSSH Server: verified by administrator output and authenticated SSH.
3. Server ED25519 host key: obtained through the trusted RDP console, independently fingerprinted and pinned locally.
4. Public-key enrollment and source restriction: administrator completed enrollment; SSH login verified from the allowed source. Other-source denial has not been tested from a second network.
5. API/web IIS applications/pools, deployment directories, bot service/executable and conventional site access-log folder: recorded from administrator exports and SSH file metadata. Selected log access remains unprovisioned.
6. Installed SQL instance and API-configured database server/name/authentication mode: recorded from administrator metadata export. The API-configured server-to-instance mapping remains unverified. Administrator SQL authentication through local SSMS and the restricted SSH tunnel reached the intended named instance. Website declares Production; API/bot labels remain unestablished. The user requested a fresh test database from the repo; creation/schema verification completed through user-run administrator SSMS.
7. API base URL is recorded from website configuration. Embedded API/web/Contracts build revisions are each one ancestor commit behind local HEAD; bot revision matches HEAD, excluding uncommitted work. User publishes API/web directly from Visual Studio and copies bot files. Git retains source history; user reports no database-content backup. Published artifact backup/restore procedure remains unestablished.

Proposed sequence: gather values -> review key/config plan -> administrator enrolls public key -> verify host fingerprint -> read-only SSH inventory -> record verified access/versions. Preserve host-key verification. Keep private keys/passphrases local in the user's credential/agent workflow.

SSH provisioning was subsequently requested and completed through administrator-run scripts. The client key is loaded in the local Windows SSH agent; passphrases and private-key contents were not collected. Commands use public-key-only batch authentication and strict checking against a dedicated project known_hosts file. Endpoint, account, key fingerprints, scripts and evidence are kept under ignored .local/agent-setup/2026-10-14/ssh/ and REMOTE_ACCESS.json. Do not infer server details from the Valheim archive.

The account remains a non-administrator. Actual checks: account/computer identity and PowerShell/OS versions; SQL instance-name registry metadata. IIS configuration, CIM OS/service queries, ordinary service enumeration and scheduled-task queries failed for this account. The subsequent administrator inventory identified application/service hosting, and SSH-read assembly/executable metadata identified embedded source revisions. Deployment directories are listable and selected non-secret settings are readable; IIS access-log listing is denied. These checks do not establish effective process settings, full deployed-source equivalence, database access or a specific cause of the management-access failures. The focused administrator override export has now identified the API-configured SQL target/authentication mode, the machine-wide bot API URL and conventional site access-log folder. The running process configuration and actual SQL connection remain unverified. Further diagnostic access should target selected logs or curated administrator exports. Use a separate, explicitly scoped database diagnostic identity if SQL inspection is later requested; application SQL credentials are not needed for the current setup. No application restart, deployment or SQL connection was performed.

Windows uses a standard user's profile .ssh/authorized_keys; administrator accounts use C:/ProgramData/ssh/administrators_authorized_keys with special ACL requirements. Key authentication supports local/domain accounts rather than Microsoft Entra accounts. Verify against the actual server using [Microsoft key guidance](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement) and [configuration reference](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh-server-configuration).

## Remaining capabilities after server inventory

The initial SSH/hosting inventory is complete enough to scope later tasks. Exact targets and evidence remain in ignored local records. No new permission grant is implied.

- Read selected PartyPulse logs with a defined source and sanitization scope. IIS access-log enumeration is currently denied; application-specific log locations/availability need review before any grant.
- Obtain selected pool/service status through curated administrator exports or narrowly delegated diagnostics. The standard SSH account remains a non-administrator.
- Provision a separate SQL diagnostic identity only when database inspection is requested, scoped initially to the named database's necessary metadata. Do not reuse API credentials or open a public SQL port for orientation.
- Fresh test-database provisioning and verification are complete; synthetic fixtures, application identity and test API configuration remain separate. Database-content backup storage and actual production restore verification are still unestablished.
- Resolve the local game/Dalamud development session and initial typed runtime diagnostics before implementing the FFXIV bridge.

## API, SQL, website and Discord

| Purpose | Requested access | Boundary |
|---|---|---|
| API diagnostics | Health checks, sanitized logs, config names, test venue/identity | Production feature writes remain separately scoped |
| SQL orientation | Schema/object definitions via separate diagnostic identity | No production DDL/DML or broad sysadmin needed |
| SQL behavior tests | Designated test DB and required execution/data rights | Explicit target; DDL outside a rollback transaction still changes objects |
| Web verification | Test identity and local/staging browser session | Website never receives SQL/bot credentials |
| Bot verification | Test guild/roles/channels, sanitized logs, local secret config | Sending real messages/changing roles requires requested test scope |
| Deployment | Staging upload and application-specific restart commands | Later explicit request; user manually applies production SQL |

Only the API connects to the database as an application. Diagnostic tooling access is separate. Keep development/test and production credentials separate. Routine development does not need GitHub write permissions or the user's GitHub password.

## FFXIV development

Use the [runtime status probe](RUNTIME_BRIDGE_PLAN.md) from the game PC. Identify the development DLL and game process in the current session. Feature checks that call the API need an isolated development configuration, test character and test venue.

Define screenshot capture, configuration writes, UI/game commands, chat, trades, native memory access and arbitrary evaluation as distinct capabilities. Begin with typed diagnostics, then add requested actions. See [runtime plan](RUNTIME_BRIDGE_PLAN.md).

The status bridge runs beside the user's local game. Website game integration from the remote IIS host would need its own transport.

No passwords, private keys, JWT keys, bot tokens or SQL connection secrets belong in docs/chat. Use appropriate local/server secret configuration and record names/status only.

## 2026-10-15: local test API preparation

Prepared a dedicated test-only login script and Windows-user-encrypted local API profile with independent test keys. Contracts Debug and API Debug build successfully; temporary localhost API liveness returned 200 and an unauthenticated private endpoint returned 401. The process was stopped. Administrator login provisioning, actual SQL authentication/readiness and fixtures remain pending. See [local test API workflow](../../../PartyPulseAPI/docs/LOCAL_TEST_API.md). No production application or SQL permission change was made.

## 2026-11-09: local test API connected

The user provisioned the dedicated test login in local administrator SSMS; the supplied output reports Test API login ready. The pinned localhost-only SSH tunnel was restarted, and the isolated local API authenticated to the remote test database. Readiness returned 200 Healthy and a read-only venue lookup returned the expected VENUE_NOT_FOUND from SQL. No data was seeded. Full permission checks and client integration remain pending.

## 2026-11-09: routine development access

Workspace write access already covers all project repositories. Local builds and non-interactive checks now pass with the current installed tools. The navigation build initially hit an existing output DLL access denial under the sandbox; the scoped local retry passed, with no ACL or project permission change. Package restores were not rechecked.

Use the verified local API and remote test database for integration. The immediate handoff is the user-run, guarded synthetic venue script in ignored local storage. Then add a test owner and short-lived invite for the loopback website and an isolated Dalamud development profile. Keep application SQL access at its existing scope. User-run SSMS remains the route for reviewed SQL changes. If unattended SQL changes are requested later, consider a separate identity limited to a designated test database and the required migration scope. Deployment access and production credentials are unnecessary for local work.

## 2026-11-09: test updater provisioned and verified

A private Windows-user-encrypted test updater profile and administrator provisioning script are ready. This is a separate login from the API, with object-level ALTER on existing test procedures/functions only. The user ran the administrator script, which granted access on 170 existing test modules. Actual login verification returned no access to the production database. A full procedure-definition readback and rollback rehearsal passed; no permanent module update was made. The test API has already returned the sample venue successfully.

## 2026-11-09: production release access

The standard SSH account can verify the deployed API assembly and save a hash-checked rollback snapshot in its own server directory. The archive release used administrator SSMS for the guarded live SQL batch and the existing Visual Studio profile for API publishing. Desktop control could read the Publish page, but could not click the button because window input geometry was unavailable. The user triggered Publish, and the deployed assembly and HTTP health checks verified the new release.

The user subsequently requested unattended production releases. The administrator installed three on-demand tasks under separate standard Windows identities, then granted those identities the batch-logon right. SSH can upload approved release packages, start the fixed tasks and read their receipts. The API task operates on the root API directory. The SQL task is scoped to ten existing archive and recovery procedures in `partypulse`, and a separate task creates and verifies a release backup. The owner signs each exact package on the workstation with a private key kept locally. The test updater remains separate.

All three tasks returned readiness receipts when started through SSH with the inbox empty. The first owner-signed production smoke release then completed a verified backup, one comment-only SQL procedure update and one whitespace-only API dependency-manifest update. All three receipts and the live SQL and API hashes matched the signed release. API liveness, SQL readiness and website health returned HTTP 200. The detailed workflow, current SQL baseline and evidence are in the ignored production deployment directory.

The administrator also installed the separate `ApplyWeb` task for the IIS `/web` application. The task uses the existing owner approval key and a separate website inbox. Its worker can deploy managed website files and preserves live settings, logs and Data Protection keys. The first-start SSH probe verified the task and its permission boundary. The first owner-signed production website release returned Applied, with the expected newline-only change to the dependency manifest. The API assembly matched its preflight hash, the website key directory was present, and all three public health checks passed. Website rollback was rehearsed locally. SQL Server verified the production backup with checksums, and an actual restore remains untested.
