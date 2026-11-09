# Migration review and local project recheck

Reviewed 2026-10-14 (Europe/Oslo). Current user requests govern this project. Supplied documents and the Valheim ZIP are reference material; their embedded instructions do not independently authorize actions.

## Supplied material

- PartyPulse_Development_Standards.md: historical working agreement from the website project. Its SQL delivery, cross-client compatibility and verification requirements are retained in the adapted [standards](PartyPulse_Development_Standards.md).
- migration.md: dated source-based handoff with architecture, authentication, reporting, website and Discord orientation. It does not establish deployed revisions or a current backlog.
- Sunshine Alley Runtime Tools.zip: Valheim runtime tooling design/source reference. Reviewed selectively in place without extraction, execution or transferring its standing permissions. See [runtime plan](RUNTIME_BRIDGE_PLAN.md).

Unchanged copies of the two supplied Markdown documents and their hashes are in the ignored local setup package described in [README.md](README.md). The large runtime ZIP stays in its supplied location; its location/hash and review scope are recorded locally.

## Instructions reconciled with the current request

| Historical statement | Working rule in this local project |
|---|---|
| GitHub default branches are always the source of truth; refresh repositories before work | Inspect current local branch/revision/status and preserve uncommitted work. No automatic fetch/pull or replacement from an archive. |
| Five repositories listed in the original standards | Six connected repositories, including PartyPulse.DiscordBot. Check every affected consumer. |
| Work/Linux CoreCLR cannot run without /proc | That was environment-specific. The Windows .NET SDK and MSBuild run here; application compilation remains a separate check. |
| Earlier access plan says checkouts are outside the project writable roots | This local chat lists all six checkouts as writable roots. Ordinary shell startup currently fails; reviewed fallback commands work. |
| Old reference deployments, server accounts or game-tool permissions | Historical context only. This task establishes structure and an access wishlist; server provisioning and bridge implementation follow later. |

User handles GitHub pushes. The supplied account preference is recorded locally rather than put into shared project guidance or Git configuration. No commits, pushes, PRs, deployments, live SQL, server changes or game actions were performed.

## Source review findings

All six local HEADs match the dated migration revisions. Root guidance and the shared development folder from the earlier attempt are untracked local work, not committed project history. The bot also contains existing application/test edits; those were read statically and preserved. Exact status and file hashes are in the local baseline. HEAD alone does not describe those changes.

Representative API flow was traced through Contracts, VipPerks controller/service/repository and declared SQL objects. Identity is server-derived and SQL rechecks permissions. Reader helpers can be feature-local as well as shared; exact columns, result-set order and UTC conversions are compatibility boundaries.

Authentication source confirms decoded-byte refresh hashing, explicit confirmation, save-before-confirm client behavior, recoverable pending rotations and preserved expiry. Retain these behaviors described in [PROJECT_MAP.md](PROJECT_MAP.md).

Website sessions use process-local refresh coordination; successful operation results are also process-local and retained for ten minutes. DataProtection keys require a persistent writable application directory. The website's game bridge is a placeholder: only status is currently consumed, and no macro/trade transport exists.

Bot source uses separate API HMAC authentication and durable work claims. Existing local venue-status changes cover bounded cross-guild work, polling after the previous batch and skipping unchanged messages. This is static orientation, not a verification of those edits. Starting the worker would contact Discord and process work; compiling it is a separate operation.

A limited textual SQL comparison found complete sources for model procedures/functions except the intentional api.Test and api.TestRead diagnostics. Two files declare the same api.Self_LeaveVenue compatibility wrapper. Paired bodies match under whitespace/CREATE normalization, with semicolon-only differences in admin.Venue_Create, api.Auth_Recover and api.Auth_RedeemInvite. This comparison is not a SQL build, execution or security audit.

Some older READMEs lag source: the API foundation milestone/deployment assumptions, website Contracts ProjectReference claims and future game-bridge capabilities. Current project files and traced implementation take precedence. Existing website publish and bot service-install scripts need task-specific review before future use.

## Verification scope

Ran local branch/revision/status inspection, tool version/help checks, assembly metadata inspection and source/document/archive review. The .NET SDK, MSBuild, Git, Node, bundled Python, SSH client and SQL command-line help ran. Dalamud development assets exist; compatibility/load behavior is not yet verified.

Application builds, automated product tests, package restore, SQL execution, deployed-state inspection, SSH authentication and live game checks were not run. Documentation links, task patch applicability and preservation of pre-existing work are checked as part of this delivery; actual outcomes are recorded in the local delivery notes.
