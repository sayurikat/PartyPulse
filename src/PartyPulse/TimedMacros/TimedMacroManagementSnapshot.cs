using System;
using PartyPulse.Api;

namespace PartyPulse.TimedMacros;

public enum TimedMacroManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed,
}

public sealed record TimedMacroManagementSnapshot(
    TimedMacroManagementStatus Status,
    string Message,
    TimedMacroViewResponse? View,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? ReceivedAt)
{
    public static TimedMacroManagementSnapshot NotLoaded { get; } = new(
        TimedMacroManagementStatus.NotLoaded,
        "Timed macros have not been loaded.",
        null,
        null,
        null);

    public DateTimeOffset EstimatedServerNow =>
        View is null || ReceivedAt is null
            ? DateTimeOffset.UtcNow
            : View.ServerNow + (DateTimeOffset.UtcNow - ReceivedAt.Value);
}
