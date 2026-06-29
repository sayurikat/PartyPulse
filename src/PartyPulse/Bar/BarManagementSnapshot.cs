using System;
using PartyPulse.Api;

namespace PartyPulse.Bar;

public enum BarManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed
}

public sealed record BarManagementSnapshot(
    BarManagementStatus Status,
    string Message,
    BarManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static BarManagementSnapshot NotLoaded { get; } = new(
        BarManagementStatus.NotLoaded,
        "Bar data has not been loaded.",
        null,
        null);
}
