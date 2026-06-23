using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace PartyPulse.Vip;

public sealed record GameMacroExecutionResult(bool Success, string? ErrorCode, string? ErrorMessage)
{
    public static GameMacroExecutionResult Ok() => new(true, null, null);
    public static GameMacroExecutionResult Fail(string code, string message) => new(false, code, message);
}

/// <summary>
/// Executes server-managed multiline macros through a temporary copy in the
/// individual macro slot 99. ExecuteMacro copies the macro into the shell
/// module synchronously, allowing the user's original slot to be restored
/// immediately after execution starts.
/// </summary>
public sealed class GameMacroExecutionService : IDisposable
{
    private static readonly TimeSpan TargetSettleDelay = TimeSpan.FromMilliseconds(300);
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IPluginLog log;
    private readonly SemaphoreSlim gate = new(1, 1);

    public GameMacroExecutionService(
        IFramework framework,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IPluginLog log)
    {
        this.framework = framework;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.log = log;
    }

    public bool IsBusy => gate.CurrentCount == 0;


    public Task<GameMacroExecutionResult> ExecuteForIdentityAsync(
        string characterName,
        string worldName,
        string macroText,
        CancellationToken cancellationToken)
    {
        foreach (var battleChara in objectTable.PlayerObjects)
        {
            if (battleChara is not IPlayerCharacter player ||
                !player.IsValid() ||
                !player.IsTargetable ||
                player.GameObjectId == 0)
            {
                continue;
            }

            var name = player.Name.TextValue.Trim();
            var world = player.HomeWorld.IsValid
                ? player.HomeWorld.Value.Name.ToString().Trim()
                : string.Empty;
            if (string.Equals(name, characterName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(world, worldName, StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteAsync(
                    player.GameObjectId,
                    characterName,
                    worldName,
                    macroText,
                    cancellationToken);
            }
        }

        return Task.FromResult(GameMacroExecutionResult.Fail(
            "TARGET_NOT_AVAILABLE",
            "The player is no longer nearby."));
    }

    public async Task<GameMacroExecutionResult> ExecuteAsync(
        ulong gameObjectId,
        string characterName,
        string worldName,
        string macroText,
        CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            return GameMacroExecutionResult.Fail("MACRO_BUSY", "Another PartyPulse macro is already being started.");

        try
        {
            var lines = NormalizeLines(macroText);
            if (lines.Length == 0)
                return GameMacroExecutionResult.Fail("MACRO_NOT_CONFIGURED", "This venue macro has not been configured.");
            if (lines.Length > 15)
                return GameMacroExecutionResult.Fail("MACRO_TOO_LONG", "Game macros can contain at most 15 lines.");
            if (lines.Any(static line => line.Length > 180))
                return GameMacroExecutionResult.Fail("MACRO_LINE_TOO_LONG", "Game macro lines can contain at most 180 characters.");

            var targetResult = await framework.RunOnTick(
                () => TrySetTarget(gameObjectId, characterName, worldName),
                cancellationToken: cancellationToken);
            if (!targetResult.Success) return targetResult;

            return await framework.RunOnTick(
                () => ExecuteOnFrameworkThread(gameObjectId, characterName, worldName, lines),
                delay: TargetSettleDelay,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return GameMacroExecutionResult.Fail("MACRO_CANCELLED", "Macro execution was cancelled.");
        }
        catch (Exception exception)
        {
            log.Error(exception, "PartyPulse macro execution failed.");
            return GameMacroExecutionResult.Fail("MACRO_EXECUTION_FAILED", "The in-game macro could not be started.");
        }
        finally
        {
            gate.Release();
        }
    }

    private GameMacroExecutionResult TrySetTarget(
        ulong gameObjectId,
        string characterName,
        string worldName)
    {
        if (!TryResolvePlayer(gameObjectId, characterName, worldName, out var player))
            return GameMacroExecutionResult.Fail("TARGET_NOT_AVAILABLE", "The player is no longer nearby.");

        targetManager.Target = player;
        return GameMacroExecutionResult.Ok();
    }

    private unsafe GameMacroExecutionResult ExecuteOnFrameworkThread(
        ulong gameObjectId,
        string characterName,
        string worldName,
        string[] lines)
    {
        if (!TryResolvePlayer(gameObjectId, characterName, worldName, out var player) ||
            targetManager.Target?.GameObjectId != player!.GameObjectId)
            return GameMacroExecutionResult.Fail("TARGET_VERIFICATION_FAILED", "The intended player could not be verified as the current target.");

        var macroModule = RaptureMacroModule.Instance();
        var shellModule = RaptureShellModule.Instance();
        if (macroModule == null || shellModule == null)
            return GameMacroExecutionResult.Fail("GAME_MACRO_UNAVAILABLE", "The game's macro subsystem is not available.");
        if (shellModule->MacroLocked || shellModule->MacroCurrentLine >= 0)
            return GameMacroExecutionResult.Fail("GAME_MACRO_BUSY", "Another in-game macro is currently running.");

        var macro = macroModule->GetMacro(0, 99);
        if (macro == null)
            return GameMacroExecutionResult.Fail("GAME_MACRO_UNAVAILABLE", "Temporary macro slot 99 is not available.");

        var originalIconId = macro->IconId;
        var originalMacroIconRowId = macro->MacroIconRowId;
        var originalName = macro->Name.ToString();
        var originalLines = new string[15];
        for (var index = 0; index < 15; index++)
            originalLines[index] = macro->Lines[index].ToString();

        try
        {
            macro->Name.SetString("PartyPulse temporary macro");
            for (var index = 0; index < 15; index++)
                macro->Lines[index].SetString(index < lines.Length ? lines[index] : string.Empty);

            shellModule->ExecuteMacro(macro);
            return GameMacroExecutionResult.Ok();
        }
        finally
        {
            macro->IconId = originalIconId;
            macro->MacroIconRowId = originalMacroIconRowId;
            macro->Name.SetString(originalName);
            for (var index = 0; index < 15; index++)
                macro->Lines[index].SetString(originalLines[index]);
        }
    }

    private bool TryResolvePlayer(
        ulong gameObjectId,
        string characterName,
        string worldName,
        out IPlayerCharacter? player)
    {
        player = objectTable.SearchById(gameObjectId) as IPlayerCharacter;
        if (player is null || !player.IsValid() || !player.IsTargetable)
            return false;
        var name = player.Name.TextValue.Trim();
        var world = player.HomeWorld.IsValid ? player.HomeWorld.Value.Name.ToString().Trim() : string.Empty;
        return string.Equals(name, characterName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(world, worldName, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] NormalizeLines(string value)
    {
        var lines = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => line.TrimEnd())
            .ToList();

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines.ToArray();
    }

    public void Dispose() => gate.Dispose();
}
