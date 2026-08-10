using System;

namespace PartyPulse.DiscordStatus;

public enum DiscordStatusManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Failed,
}

public sealed record DiscordStatusManagementSnapshot(
    DiscordStatusManagementStatus Status,
    string Message,
    DiscordManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? ReceivedAt)
{
    public static DiscordStatusManagementSnapshot NotLoaded { get; } = new(
        DiscordStatusManagementStatus.NotLoaded,
        "Discord venue status has not been loaded.",
        null,
        null,
        null);
}
