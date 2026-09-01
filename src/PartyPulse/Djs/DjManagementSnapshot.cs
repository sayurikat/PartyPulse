using System;
using PartyPulse.Api;

namespace PartyPulse.Djs;

public enum DjManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed,
}

public sealed record DjManagementSnapshot(
    DjManagementStatus Status,
    string Message,
    DjViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static DjManagementSnapshot NotLoaded { get; } = new(
        DjManagementStatus.NotLoaded,
        "DJ data has not been loaded.",
        null,
        null);
}
