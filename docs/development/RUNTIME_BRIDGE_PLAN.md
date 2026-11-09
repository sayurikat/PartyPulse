# FFXIV runtime bridge

## Local development status probe

The first implementation exposes one read-only status request inside PartyPulse. A PowerShell client connects to the selected game process over a Windows named pipe. The pipe grants access to the Windows account running the game and denies the Windows Network SID, which blocks remote SMB clients. The client verifies the server process ID through Windows before sending the request.

In Visual Studio, select **Debug | x64** and build PartyPulse. Debug includes the status bridge by default. The bridge starts when Dalamud loads the DLL as a Dev Plugin on Windows. Release builds exclude the bridge. To build the same Debug DLL from the repository root with restored dependencies, run the following command.

```powershell
dotnet build src/PartyPulse/PartyPulse.csproj -c Debug --no-restore -p:Platform=x64
```

Load the resulting `src/PartyPulse/bin/x64/Debug/PartyPulse.dll` through Dalamud Settings → Experimental → Dev Plugin Locations. If the game uses your regular production-linked PartyPulse profile, run this smoke check from character selection to keep login-driven PartyPulse work idle. The status request samples the running plugin directly through the pipe.

From PowerShell 7 on the game PC, identify the intended game process and substitute its actual ID in the second command.

```powershell
Get-Process -Name ffxiv_dx11 | Select-Object Id, StartTime
./scripts/Get-PartyPulseRuntimeStatus.ps1 -GameProcessId 12345
```

The JSON response reports the protocol version, game PID, bridge session ID, plugin version, build ID (module MVID), framework-thread UTC sample time, and logged-in state. The server allows three seconds for each connected request, and the client allows five seconds for the round trip. Repeated probes should report the same session ID and a fresh sample time. Reloading the Dev Plugin should create a new session ID. To disable the probe in a Debug build, pass `-p:PartyPulseRuntimeStatus=false` and reload the resulting DLL. The same client command should then fail to connect within its deadline.

On 2026-11-09 the opted-in Debug x64 build compiled with 42 existing warnings and no errors. Dalamud hot-reloaded the watched DLL. Two probes from the Windows desktop account verified the game process ID, a matching on-disk module MVID, the same session ID and advancing sample times. The client was logged in. The restricted Codex command runner received Windows access denied. A scoped desktop-account command passed with the same pipe permissions. A Release build with the opt-in property set contained no bridge type.

## Observed Valheim reference

The supplied Sunshine Alley Runtime Tools archive was read in place without extraction/execution. It separates RuntimeTools.Core (managed reflection), RuntimeTools.Plugin (BepInEx 5/Unity/Valheim adapter) and RuntimeTools.Client (.NET 10 CLI/stdio MCP).

Codex -> local CLI/stdio MCP -> authenticated named pipe -> queued game-thread work.

Observed protections: private per-process session discovery, PID identity, random token, account/SYSTEM ACLs, owner/PID verification, remote-client rejection, bounded JSON/queues/results, replay/expiry checks, audit-before-mutation and explicit unknown outcomes after execution starts. No public TCP listener or Valheim multiplayer debug RPC.

Tools include status/logs, types/assemblies, reflection/handles, scenes/objects, player/prefab/ZDO, console/evaluation and bounded watches. There is **no screenshot operation**. Recorded historical smoke/security tests were not rerun.

Mutation/evaluation are enabled by default in the reference. Evaluation has full game-process privileges, without a sandbox or safe forced cancellation. Its defaults, Valheim adapters and machine-specific SSH/standing permissions do not transfer automatically to PartyPulse.

## Remaining design

Reuse protocol/session/authentication, CLI/MCP, bounds/watch/audit patterns after checking licensing/source boundaries. Replace BepInEx/Unity adapters with Dalamud and typed PartyPulse diagnostic adapters. A future expansion could stay embedded in PartyPulse or move to a dedicated development plugin in a sibling PartyPulse.RuntimeTools checkout.

Use existing Dalamud services first and framework-thread scheduling for relevant game/plugin access. Managed reflection does not expose every native game object. Follow [game interaction guidance](https://dalamud.dev/plugin-development/interaction/) and [IFramework API](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IFramework/) while compiling against the installed local version.

Potential additions beyond the status probe:

- PartyPulse window/navigation and feature-manager snapshots.
- Bounded sanitized logs/errors.
- Selected player/target/location observations for PartyPulse debugging.
- Bounded event watches with cancellation, retention limits and timestamps.

Named in-game tests, debug actions, config edits and game-window screenshots are future work. API-backed tests need an isolated development configuration pointing to the local test API and a dedicated test character and venue. Arbitrary evaluation, unrestricted private reflection and native-memory tooling need separate design. Valheim's Mono evaluator is not a drop-in Dalamud component.

## Integration requirements

Keep the bridge on the local named pipe. MCP registration can follow to expose typed tools.

Keep session credentials, audits and player/runtime data outside source control; redact access/refresh tokens. Use explicit session selection, request identity/expiry, bounded payloads/queues and reliable disposal. Distinguish not-started timeouts from unknown outcomes; do not automatically retry uncertain mutations.

Verify identity/token rejection, stale sessions, concurrency/bounds, logout/reload/disposal, framework dispatch and redaction. Test actions only in the authorized development session. Screenshots need a separately implemented capture mechanism and retention/window scope.

Prerequisites and requested capabilities are in [ACCESS_PLAN.md](ACCESS_PLAN.md). Existing Valheim authorization does not establish PartyPulse server/game access.

## Reference source index and adaptation details

Useful archive paths (relative to its project root):

| Reference | What to inspect before reusing |
|---|---|
| docs/protocol.md | Versioned request/response envelope, session selection and timeout semantics |
| RuntimeTools.Client/SessionDiscovery.cs, RuntimeBridge.cs, WindowsPipePeer.cs | Stale-session detection, account/server-PID verification, response identity and uncertain outcomes |
| RuntimeTools.Plugin/PrivateSession.cs, WindowsSecurity.cs, WindowsAccount.cs | Protected discovery files, random session credentials and private local pipe |
| RuntimeTools.Plugin/PipeBridge.cs, RuntimeRequest.cs | Authentication, replay rejection, bounded queues and queued/running/expired state |
| RuntimeTools.Core/ObjectRegistry.cs, ReflectionInspector.cs | Handle identity, result limits and controlled property evaluation |
| RuntimeTools.Plugin/WatchManager.cs | Bounded sampling and explicit getter opt-in |
| RuntimeTools.Client/ToolCatalog.cs, McpServer.cs | Matching CLI and stdio MCP operations |
| docs/verification.md, tests/, THIRD_PARTY.md | Historical tests and dependency notices; tests were not rerun here |

The plugin adapter targets net462; the client targets net10.0. No root LICENSE/COPYING/NOTICE entry was found in the reviewed archive. Establish source reuse/redistribution terms and preserve dependency notices before copying code into a published package.

Reference payload/watch limits do not guarantee execution-time limits. Its Unity dispatcher checks a 5 ms budget between up to four calls; a slow getter/method/evaluation can still block a frame, and watch sampling follows that budget. Complex watch samples reference handles rather than immutable historical snapshots; stopping a watch does not itself release handles. The adaptation needs independent watch/handle cleanup and invalidation across logout/reload, especially for native wrappers.

The reference records full mutation/evaluation arguments in its audit and captures raw game logs/errors. Client redaction substitutes the session token only. PartyPulse must instead expose typed, allowlisted projections and redact arguments, logs and errors before storage/return. Do not reflect Configuration/VenueConnectionConfiguration refresh tokens or AuthenticationManager access-token sessions. Initial mutation/evaluation should be disabled rather than copying the reference defaults.

## PartyPulse integration points

Plugin.cs already owns the injected Dalamud services, managers/windows and Framework.Update lifecycle. PlayerIdentityProvider, TargetPlayerProvider and VenueLocationProvider supply typed observations. Manager GetSnapshot methods and AuthenticationSnapshot provide useful status projections without raw tokens. MainWindow navigation state is private; complete navigation diagnostics need a small typed adapter.

GameMacroExecutionService dispatches through the framework, checks target/busy state, bounds macro lines and restores temporary macro slot 99. Successful invocation means initiation. SettlementTradeService dispatches/revalidates the target; Dropbox has no completion callback, so initiation must not become automatic trade-completed reporting.

The proposed local agent bridge is separate from the website product boundary. The current website IGameBridge only defines status, macro and trade methods; target/nearby/location are fields in status. NullGameBridge is registered, only status is consumed, and action methods are unused placeholders. A remote IIS website cannot directly open the local game account's named pipe. Any website transport would need its own design; it is unnecessary for the first local diagnostic bridge.
