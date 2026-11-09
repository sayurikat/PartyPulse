# PartyPulse local development

Maintained from 2026-10-14. Shared guidance for the six local PartyPulse repositories.

Read the repository's root AGENTS.md first. Local source and uncommitted work take precedence over GitHub snapshots and migration attachments.

| Guide | Purpose |
|---|---|
| [Development standards](PartyPulse_Development_Standards.md) | Working agreement and delivery rules |
| [Project map](PROJECT_MAP.md) | Roles, source navigation and behavior to preserve |
| [Build and verification](BUILD_AND_VERIFY.md) | Dependency order and existing checks |
| [Access plan](ACCESS_PLAN.md) | Requested capabilities and missing server/game information |
| [Runtime bridge plan](RUNTIME_BRIDGE_PLAN.md) | Reference findings and proposed FFXIV integration |
| [Decisions](DECISIONS.md) | Durable decisions, evidence and open work |
| [Venue user archive](VENUE_USER_ARCHIVE.md) | Staff access, recovery and verification |
| [Migration review](MIGRATION_REVIEW.md) | Attachment provenance, superseded instructions and local recheck findings |

Keep the repositories as siblings. Other repositories link here through ../PartyPulse/docs/development/. Each also carries essential rules in AGENTS.md for isolated use.

Keep AGENTS.md at each repository root. A file only in the common parent folder is not a reliable substitute for repository-root guidance and would mix PartyPulse rules with unrelated projects. The guides are one maintained shared set; the six short entry files preserve essential rules when a checkout is opened alone. See [official instruction discovery](https://learn.chatgpt.com/docs/agent-configuration/agents-md).

The private local recheck package is under the ignored .local/agent-setup/2026-10-14/ directory in this checkout. LOCAL_ENVIRONMENT.md records tool paths and actual checks; repository-baseline.json records branch/revision/status and hashes of pre-existing local changes. references/ preserves the two supplied Markdown documents unchanged, baseline/ preserves the guidance before this recheck, and deliveries/ contains this task's patch and application notes. These files are local conveniences, not shared runtime configuration. Keep credentials and runtime data outside source control.

For a new task: inspect local status, read the standards and relevant map section, trace affected consumers, choose checks from the build guide, and update the appropriate guide and decisions after meaningful work. Record newly verified access in ACCESS_PLAN.md; keep endpoint/account details in local inventory. No SSH session or game bridge was established by this documentation setup.

Update the relevant guide after meaningful changes. Record date, revision and actual verification; distinguish historical, proposed, implemented and deployed behavior. AGENTS.md describes working rules; it cannot grant filesystem or network access. Repository guidance is supported by [Codex documentation](https://learn.chatgpt.com/docs/agent-configuration/agents-md).
