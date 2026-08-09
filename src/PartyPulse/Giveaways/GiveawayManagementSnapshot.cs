using System;

namespace PartyPulse.Giveaways;

public enum GiveawayManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
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
