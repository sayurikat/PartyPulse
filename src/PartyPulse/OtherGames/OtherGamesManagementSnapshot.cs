using System;
using PartyPulse.Api;

namespace PartyPulse.OtherGames;

public enum OtherGamesManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed
}

public sealed record OtherGamesManagementSnapshot(
    OtherGamesManagementStatus Status,
    string Message,
    OtherGamesManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static OtherGamesManagementSnapshot NotLoaded { get; } = new(
        OtherGamesManagementStatus.NotLoaded,
        "Other Games have not been loaded.",
        null,
        null);
}
