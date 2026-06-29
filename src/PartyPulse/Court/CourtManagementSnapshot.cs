using System;
using PartyPulse.Api;

namespace PartyPulse.Court;

public enum CourtManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed
}

public sealed record CourtManagementSnapshot(
    CourtManagementStatus Status,
    string Message,
    CourtManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static CourtManagementSnapshot NotLoaded { get; } = new(
        CourtManagementStatus.NotLoaded,
        "Court Services have not been loaded.",
        null,
        null);
}
