using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Integrations;
using PartyPulse.Integrations.Dropbox;

namespace PartyPulse.Finance;

/// <summary>
/// Coordinates the PartyPulse settlement record with the optional Dropbox
/// plugin. Dropbox begins the trade but provides no completion callback, so a
/// successful result only means that the trade was started successfully.
/// </summary>
public sealed class SettlementTradeService
{
    private const uint GilItemId = 1;
    private const string IntegrationName = "Dropbox";
    private static readonly TimeSpan FocusTargetDelay = TimeSpan.FromMilliseconds(1500);

    private readonly DropboxApi dropboxApi;
    private readonly IFramework framework;
    private readonly ICommandManager commandManager;
    private readonly ITargetManager targetManager;
    private readonly IPluginLog log;

    public SettlementTradeService(
        DropboxApi dropboxApi,
        IFramework framework,
        ICommandManager commandManager,
        ITargetManager targetManager,
        IPluginLog log)
    {
        this.dropboxApi = dropboxApi;
        this.framework = framework;
        this.commandManager = commandManager;
        this.targetManager = targetManager;
        this.log = log;
    }

    public Task<PluginIntegrationResult> CheckReadyAsync(
        CreateVipSettlementRequest request,
        CancellationToken cancellationToken) =>
        CheckReadyAsync(request.TargetCharacterName, request.TargetWorldName, cancellationToken);

    public Task<PluginIntegrationResult> CheckReadyAsync(
        string targetCharacterName,
        string targetWorldName,
        CancellationToken cancellationToken) =>
        RunOnFrameworkAsync(
            "preflight the Dropbox settlement trade",
            () =>
            {
                var targetResult = ValidatePlayer(
                    targetManager.Target,
                    targetCharacterName,
                    targetWorldName,
                    "validate the current trade target");
                if (!targetResult.Success)
                {
                    return targetResult;
                }

                var commandResult = EnsureDropboxCommandAvailable();
                if (!commandResult.Success)
                {
                    return commandResult;
                }

                var readyResult = dropboxApi.EnsureNotBusy();
                if (!readyResult.Success)
                {
                    return readyResult;
                }

                return dropboxApi.ValidateQueueAccess();
            },
            TimeSpan.Zero,
            cancellationToken);

    public Task<PluginIntegrationResult> InitiateTradeAsync(
        CreateVipSettlementResponse settlement,
        CancellationToken cancellationToken) =>
        InitiateTradeAsync(settlement.TargetCharacterName, settlement.TargetWorldName, settlement.AmountGil, cancellationToken);

    public async Task<PluginIntegrationResult> InitiateTradeAsync(
        string targetCharacterName,
        string targetWorldName,
        long amountGil,
        CancellationToken cancellationToken)
    {
        if (amountGil <= 0 || amountGil > int.MaxValue)
        {
            return Failed(
                PluginIntegrationFailureKind.InvalidRequest,
                "INVALID_GIL_AMOUNT",
                $"The settlement amount must be between 1 and {int.MaxValue:N0} gil.",
                "validate the settlement amount");
        }

        // First framework tick: validate the target, confirm Dropbox is idle,
        // clear stale selections, and ask the game to adopt the target as the
        // focus target. The game does not apply FocusTarget immediately.
        var prepareResult = await RunOnFrameworkAsync(
            "prepare the Dropbox settlement target",
            () => PrepareTarget(
                targetCharacterName,
                targetWorldName),
            TimeSpan.Zero,
            cancellationToken);
        if (!prepareResult.Success)
        {
            return prepareResult;
        }

        // A delayed RunOnTick is used instead of Task.Delay. This guarantees
        // that the continuation which opens Dropbox, writes its queue, and
        // begins trading is back on the framework thread after the game has
        // had time to apply the focus target.
        return await RunOnFrameworkAsync(
            "start the Dropbox settlement trade",
            () => OpenQueueAndBeginTrade(
                targetCharacterName,
                targetWorldName,
                (int)amountGil),
            FocusTargetDelay,
            cancellationToken);
    }

    private PluginIntegrationResult PrepareTarget(
        string targetCharacterName,
        string targetWorldName)
    {
        var targetResult = ValidatePlayer(
            targetManager.Target,
            targetCharacterName,
            targetWorldName,
            "validate the current trade target");
        if (!targetResult.Success)
        {
            return targetResult;
        }

        var readyResult = dropboxApi.EnsureNotBusy();
        if (!readyResult.Success)
        {
            return readyResult;
        }

        var clearResult = dropboxApi.ClearQueue();
        if (!clearResult.Success)
        {
            return clearResult;
        }

        targetManager.FocusTarget = targetManager.Target;
        return PluginIntegrationResult.Succeeded();
    }

    private PluginIntegrationResult OpenQueueAndBeginTrade(
        string targetCharacterName,
        string targetWorldName,
        int amountGil)
    {
        var focusTargetResult = ValidatePlayer(
            targetManager.FocusTarget,
            targetCharacterName,
            targetWorldName,
            "validate the Dropbox focus target");
        if (!focusTargetResult.Success)
        {
            return focusTargetResult;
        }

        var readyResult = dropboxApi.EnsureNotBusy();
        if (!readyResult.Success)
        {
            return readyResult;
        }

        if (!commandManager.ProcessCommand("/dropbox"))
        {
            return Failed(
                PluginIntegrationFailureKind.IpcUnavailable,
                "DROPBOX_COMMAND_UNAVAILABLE",
                "Dropbox is loaded, but its /dropbox command is not currently available.",
                "open the Dropbox interface");
        }

        var quantityResult = dropboxApi.TrySetDropboxItemQuantity(
            GilItemId,
            false,
            amountGil);
        if (!quantityResult.Success)
        {
            return quantityResult;
        }

        return dropboxApi.BeginTrade();
    }


    private PluginIntegrationResult EnsureDropboxCommandAvailable()
    {
        var registered = commandManager.Commands.Keys.Any(command =>
            string.Equals(command, "/dropbox", StringComparison.OrdinalIgnoreCase));
        return registered
            ? PluginIntegrationResult.Succeeded()
            : Failed(
                PluginIntegrationFailureKind.IpcUnavailable,
                "DROPBOX_COMMAND_UNAVAILABLE",
                "Dropbox is loaded, but its /dropbox command is not currently available.",
                "validate the Dropbox command");
    }

    private async Task<PluginIntegrationResult> RunOnFrameworkAsync(
        string operation,
        Func<PluginIntegrationResult> action,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            return await framework.RunOnTick(
                action,
                delay: delay,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            log.Debug(
                exception,
                "External plugin operation was cancelled. Plugin={PluginName}, Operation={Operation}.",
                IntegrationName,
                operation);
            return Failed(
                PluginIntegrationFailureKind.Cancelled,
                "INTEGRATION_CANCELLED",
                "The Dropbox trade operation was cancelled.",
                operation);
        }
        catch (Exception exception)
        {
            log.Error(
                exception,
                "External plugin orchestration failed. Plugin={PluginName}, Operation={Operation}.",
                IntegrationName,
                operation);
            return Failed(
                PluginIntegrationFailureKind.OperationFailed,
                "INTEGRATION_OPERATION_FAILED",
                $"PartyPulse could not {operation}.",
                operation);
        }
    }

    private static PluginIntegrationResult ValidatePlayer(
        IGameObject? gameObject,
        string expectedCharacterName,
        string expectedWorldName,
        string operation)
    {
        if (gameObject is not IPlayerCharacter player)
        {
            return Failed(
                PluginIntegrationFailureKind.InvalidState,
                "TARGET_NOT_PLAYER",
                "The selected trade target is no longer a player character.",
                operation);
        }

        var characterName = player.Name.TextValue.Trim();
        var worldName = player.HomeWorld.IsValid
            ? player.HomeWorld.Value.Name.ToString().Trim()
            : string.Empty;

        if (!string.Equals(
                characterName,
                expectedCharacterName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                worldName,
                expectedWorldName,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failed(
                PluginIntegrationFailureKind.InvalidState,
                "TARGET_CHANGED",
                $"Keep {expectedCharacterName} @ {expectedWorldName} targeted until Dropbox starts the trade.",
                operation);
        }

        return PluginIntegrationResult.Succeeded();
    }

    private static PluginIntegrationResult Failed(
        PluginIntegrationFailureKind kind,
        string code,
        string message,
        string operation) =>
        PluginIntegrationResult.Failed(
            new PluginIntegrationFailure(
                kind,
                code,
                message,
                IntegrationName,
                operation));
}
