using System;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace PartyPulse.Integrations;

/// <summary>
/// Common guard and error-mapping layer for optional third-party plugin IPC.
/// Feature code receives structured failures and decides whether an operation
/// should be blocked, retried, or allowed to continue manually.
/// </summary>
public abstract class ExternalPluginIpcClient
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    protected ExternalPluginIpcClient(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        string pluginName,
        string pluginInternalName)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        PluginName = pluginName;
        PluginInternalName = pluginInternalName;
    }

    protected IDalamudPluginInterface PluginInterface => pluginInterface;

    protected string PluginName { get; }

    protected string PluginInternalName { get; }

    public PluginIntegrationResult CheckAvailability(string operation)
    {
        try
        {
            var plugin = pluginInterface.InstalledPlugins.FirstOrDefault(value =>
                string.Equals(value.InternalName, PluginInternalName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value.Name, PluginName, StringComparison.OrdinalIgnoreCase));

            if (plugin is null)
            {
                return Failed(
                    PluginIntegrationFailureKind.PluginNotInstalled,
                    "PLUGIN_NOT_INSTALLED",
                    $"{PluginName} is not installed.",
                    operation);
            }

            if (!plugin.IsLoaded)
            {
                return Failed(
                    PluginIntegrationFailureKind.PluginNotLoaded,
                    "PLUGIN_NOT_LOADED",
                    $"{PluginName} is installed but is not currently loaded.",
                    operation);
            }

            return PluginIntegrationResult.Succeeded();
        }
        catch (Exception exception)
        {
            return LogAndFail(
                exception,
                PluginIntegrationFailureKind.OperationFailed,
                "PLUGIN_STATE_CHECK_FAILED",
                $"PartyPulse could not inspect the current {PluginName} plugin state.",
                operation);
        }
    }

    protected PluginIntegrationResult ExecutePluginCall(
        string operation,
        Action action)
    {
        var availability = CheckAvailability(operation);
        if (!availability.Success)
        {
            return availability;
        }

        try
        {
            action();
            return PluginIntegrationResult.Succeeded();
        }
        catch (Exception exception)
        {
            return MapFailure(exception, operation);
        }
    }

    protected PluginIntegrationResult<T> ExecutePluginCall<T>(
        string operation,
        Func<T> action)
    {
        var availability = CheckAvailability(operation);
        if (!availability.Success)
        {
            return PluginIntegrationResult<T>.Failed(availability.Failure!);
        }

        try
        {
            return PluginIntegrationResult<T>.Succeeded(action());
        }
        catch (Exception exception)
        {
            var failure = MapFailure(exception, operation);
            return PluginIntegrationResult<T>.Failed(failure.Failure!);
        }
    }

    protected PluginIntegrationResult Failed(
        PluginIntegrationFailureKind kind,
        string code,
        string message,
        string operation) =>
        PluginIntegrationResult.Failed(
            new PluginIntegrationFailure(kind, code, message, PluginName, operation));

    private PluginIntegrationResult MapFailure(Exception exception, string operation)
    {
        var unwrapped = exception is TargetInvocationException { InnerException: not null } targetException
            ? targetException.InnerException
            : exception;

        return unwrapped switch
        {
            OperationCanceledException => LogAndFail(
                unwrapped,
                PluginIntegrationFailureKind.Cancelled,
                "PLUGIN_CALL_CANCELLED",
                $"The {PluginName} operation was cancelled.",
                operation),
            IpcNotReadyError => LogAndFail(
                unwrapped,
                PluginIntegrationFailureKind.IpcUnavailable,
                "IPC_NOT_READY",
                $"{PluginName} is loaded, but the required IPC endpoint is not available.",
                operation),
            IpcTypeMismatchError => LogAndFail(
                unwrapped,
                PluginIntegrationFailureKind.Incompatible,
                "IPC_TYPE_MISMATCH",
                $"{PluginName}'s IPC contract is incompatible with this PartyPulse version.",
                operation),
            PluginIntegrationContractException contractException => LogAndFail(
                contractException,
                PluginIntegrationFailureKind.Incompatible,
                contractException.Code,
                contractException.Message,
                operation),
            _ => LogAndFail(
                unwrapped,
                PluginIntegrationFailureKind.OperationFailed,
                "PLUGIN_CALL_FAILED",
                $"{PluginName} failed while PartyPulse was attempting to {operation}.",
                operation),
        };
    }

    private PluginIntegrationResult LogAndFail(
        Exception exception,
        PluginIntegrationFailureKind kind,
        string code,
        string message,
        string operation)
    {
        log.Warning(
            exception,
            "External plugin call failed. Plugin={PluginName}, Operation={Operation}, Code={Code}.",
            PluginName,
            operation,
            code);
        return Failed(kind, code, message, operation);
    }
}
