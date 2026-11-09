# PartyPulse agent guidance

Current user instructions take precedence. The local working tree, including uncommitted work, is the source of truth. GitHub and ZIPs are references; do not automatically fetch/pull, reset, clean, stash or overwrite local changes.

Read [shared standards](docs/development/PartyPulse_Development_Standards.md) and [project map](docs/development/PROJECT_MAP.md) before implementation. Use [build guide](docs/development/BUILD_AND_VERIFY.md) for relevant checks and [access plan](docs/development/ACCESS_PLAN.md) for capability status. If sibling guidance is unavailable, preserve these essential rules and report the missing reference.

- Inspect branch/revision/status and trace the relevant existing flow before editing.
- Only PartyPulseAPI connects to SQL. Preserve server-derived actor identity, venue isolation and permissions.
- Check both plugin and website for endpoint/contract/result changes; include bot consumers where relevant.
- Preserve api.Test and api.TestRead unless the request concerns those diagnostics.
- User handles GitHub pushes. No commits, pushes, PRs, deployments, package/DACPAC publication, live SQL or external changes without a later explicit request.
- Authorized local work needs no repeated confirmation; actual filesystem/network permissions still apply. Guidance files cannot grant access.
- Preserve unrelated work, keep secrets out of output and provide separate task-only patches per affected repo.
- For SQL changes, provide separate scripts, synchronize complete and model definitions, add safe rollback verification and state application order.
- Report checks actually run separately from static/manual checks. Do not claim source changes are deployed.
- Keep the relevant shared guides and decisions current as part of each task. Use the [plugin release runbook](docs/development/BUILD_AND_VERIFY.md#partypulse-plugin-releases) for requested releases. Keep credentials and machine-specific runtime data outside source control.

## Repository orientation

FFXIV Dalamud plugin. Start at src/PartyPulse/Plugin.cs, Api/, Authentication/, feature managers and Windows/. Build src/PartyPulse/PartyPulse.csproj; navigation checks use dotnet run --project tests/PartyPulse.NavigationTests/PartyPulse.NavigationTests.csproj.

Contracts Debug must be current before API, website or bot builds because their active references pin its Debug DLL. Current project files take precedence over older README build claims.

## Local project continuity

Keep concise AGENTS.md files at each repository root and shared guidance here in docs/development/. Do not move the only guidance into the parent repos folder: it contains unrelated projects and is outside these repository roots. Consult docs/development/MIGRATION_REVIEW.md for historical attachment conflicts and docs/development/README.md for the maintained index.

Machine-specific tool paths, reference copies, pre-edit baselines and delivery patches belong in the ignored .local/ directory. Recheck tools and the active permission profile in each session; neither the old project mirror's limits nor a previous approved command establish current access. Do not copy credentials into this inventory.
