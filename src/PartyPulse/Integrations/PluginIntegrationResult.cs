using System;

namespace PartyPulse.Integrations;

public enum PluginIntegrationFailureKind
{
    PluginNotInstalled,
    PluginNotLoaded,
    IpcUnavailable,
    Incompatible,
    Busy,
    InvalidRequest,
    InvalidState,
    OperationFailed,
    Cancelled,
}

public sealed record PluginIntegrationFailure(
    PluginIntegrationFailureKind Kind,
    string Code,
    string Message,
    string PluginName,
    string Operation);

public sealed record PluginIntegrationResult(
    bool Success,
    PluginIntegrationFailure? Failure)
{
    public static PluginIntegrationResult Succeeded() => new(true, null);

    public static PluginIntegrationResult Failed(PluginIntegrationFailure failure) =>
        new(false, failure);
}

public sealed record PluginIntegrationResult<T>(
    bool Success,
    T? Value,
    PluginIntegrationFailure? Failure)
{
    public static PluginIntegrationResult<T> Succeeded(T value) =>
        new(true, value, null);

    public static PluginIntegrationResult<T> Failed(PluginIntegrationFailure failure) =>
        new(false, default, failure);
}

/// <summary>
/// Signals that an external plugin's runtime contract no longer matches the
/// adapter PartyPulse was built against. The IPC base maps this exception to
/// a structured Incompatible result instead of allowing reflection failures to
/// escape into feature code.
/// </summary>
public sealed class PluginIntegrationContractException : Exception
{
    public PluginIntegrationContractException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public PluginIntegrationContractException(
        string code,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
