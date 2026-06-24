using System;
using PartyPulse.Api;

namespace PartyPulse.Greeter;

public enum GreeterManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed,
}

public sealed record GreeterManagementSnapshot(
    GreeterManagementStatus Status,
    string Message,
    GreeterContextResponse? Context,
    DateTimeOffset? LastAttemptAt)
{
    public static GreeterManagementSnapshot NotLoaded { get; } = new(
        GreeterManagementStatus.NotLoaded,
        "Greeter data has not been loaded.",
        null,
        null);
}
