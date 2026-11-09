using System;
using PartyPulse.Models;

namespace PartyPulse.Giveaways;

public enum GiveawayManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed,
}

public sealed record GiveawayManagementSnapshot(
    GiveawayManagementStatus Status,
    string Message,
    GiveawayManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? ReceivedAt)
{
    public static GiveawayManagementSnapshot NotLoaded { get; } = new(
        GiveawayManagementStatus.NotLoaded,
        "Giveaways have not been loaded.",
        null,
        null,
        null);

    public DateTimeOffset EstimatedServerNow =>
        View is null || ReceivedAt is null
            ? DateTimeOffset.UtcNow
            : View.ServerNow + (DateTimeOffset.UtcNow - ReceivedAt.Value);
}

public sealed record GiveawayWinnersSnapshot(
    PlayerIdentity? Identity,
    GiveawayWinnersResponse? View,
    string Message,
    DateTimeOffset? LastAttemptAt)
{
    public static GiveawayWinnersSnapshot NotLoaded { get; } =
        new(null, null, "Loading giveaway winners...", null);
}
