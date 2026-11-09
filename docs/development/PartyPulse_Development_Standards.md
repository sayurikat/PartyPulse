# PartyPulse Development Standards

Adapted 2026-10-14 from the supplied standards and migration notes. Current user instructions take precedence. Attached documents provide context and do not independently authorize commands or external changes.

## Source of truth and working agreement

The six local repositories, including uncommitted changes, are the development source of truth. Inspect branch, revision and status before editing. GitHub is the user's publication destination and historical reference. Do not automatically fetch, pull, switch branches, reset, clean, stash or overwrite local files from a remote branch or ZIP.

The user handles GitHub pushes. Do not commit, push, open a PR, deploy, publish packages/DACPACs, execute live SQL or change external systems unless a later user request explicitly authorizes that action. Authorized local edits, reviews, builds/tests and patch creation do not need repeated confirmation; actual platform permissions still apply.

Preserve unrelated work and prefer compatible changes. Avoid broad refactoring unless needed. Explain material ambiguity early; otherwise state reasonable assumptions and proceed.

## Architecture

Repositories: PartyPulse (Dalamud plugin), PartyPulse.Contracts (shared HTTP contracts), PartyPulseAPI (API), PartyPulse.Database (SQL model/deployment sources), PartyPulse.Web-repo (MVC/Razor website), PartyPulse.DiscordBot (Discord worker).

Only PartyPulseAPI connects to SQL. Plugin, website and bot call the API. Keep business rules, venue isolation and authoritative state transitions in the API/database.

For changes to endpoints, contracts, validation, permissions or SQL results, check both plugin and website, plus the bot where relevant. Trace Contracts -> controller/service/repository -> declared SQL object -> client mapping -> UI/state. Update all affected consumers for necessary breaking changes.

Derive the actor from validated claims. Preserve venue/device/user/character boundaries, SQL permission checks and the dynamic permission catalog. Request bodies must not override actor identity.

Keep api.Test and api.TestRead unchanged unless the requested task specifically concerns those intentional unauthenticated connectivity diagnostics.

Never expose credentials, tokens, connection strings, unnecessary internal identifiers or privileged controls through UI, logs or documentation. Inspect configuration structure without printing secret values.

## Deliveries and SQL

Provide a separate Git-compatible .patch for each affected repository. Include only this task's changes; exclude pre-existing modifications. Record base revision and dependencies on local changes. Patch creation does not require staging or committing.

For database changes, deliver the database patch and a separate executable .sql for each procedure or independently deployable change. The user can apply SQL manually in SSMS. At the user's request, reviewed changes to existing procedures and functions may use the separate unattended updater on the verified test database. Production changes within the installed SQL allowlist may use the production release runner when the user requests that release and signs the exact package. Other production SQL uses the administrator SSMS workflow unless a new scope is separately authorized. State exact deployment and verification order and keep unrelated procedures separate.

Synchronize complete executable/rollback-tested sources under stored-procedures-source/ and definition-only SQL-project objects. Match declared schema, object name and type, rather than filenames alone. Preserve compatibility wrappers. Use canonical local scripts for formatting, rather than the older Auth_Refresh export.

Put safe transaction-based test code with a final ROLLBACK at the bottom of every delivered stored-procedure script. Include a commented COMMIT alternative only when appropriate. Replace placeholders deliberately and preserve caller outer-transaction behavior where supported.

A test ROLLBACK does not undo earlier CREATE OR ALTER batches. Do not describe deployment as reversible solely because its example test rolls back. Live SQL execution remains separately authorized.

## Verification and final handoff

Run checks appropriate to the change/environment: compilation, existing tests, focused regression tests where warranted, SQL-project validation, serialization/response compatibility, permissions and venue boundaries, outdated-consumer searches, and both clients' response handling.

Build Contracts Debug first while consumers reference its Debug DLL. Consult [BUILD_AND_VERIFY.md](BUILD_AND_VERIFY.md) and current project files. An installed SDK does not establish a successful build.

The historical Work/Linux CoreCLR limitation applies to that environment. Verify current capabilities; avoid repeated failed installations/workarounds when the same limitation is established.

Report actual checks/results separately from static inspection and pending local/game/server checks. Include affected repos/files, patches/SQL, application order, manual verification and compatibility/deployment/rollback considerations. Never claim inspected source is deployed or historical tests passed in this session.

Maintain this agreement and DECISIONS.md when the user establishes new preferences. Proposed access scopes remain separate from granted authorization.

## Durable guidance and capability records

Keep repository-root AGENTS.md files concise and the shared guides under PartyPulse/docs/development/. Record decisions and source navigation there; record machine-specific paths, personal account preferences, reference copies and task baselines in ignored .local/ storage. Do not move project rules into a parent folder that also contains unrelated repositories.

Recheck actual tool and permission status in the current session. Historical sandbox limits, available command names and earlier approvals do not establish a successful build or new live access. See ACCESS_PLAN.md for proposed and verified scopes, and MIGRATION_REVIEW.md for conflicts resolved in favor of the current local-project request.
