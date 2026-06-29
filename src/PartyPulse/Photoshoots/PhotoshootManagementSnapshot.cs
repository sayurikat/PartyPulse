using System;
using PartyPulse.Api;

namespace PartyPulse.Photoshoots;

public enum PhotoshootManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed
}

public sealed record PhotoshootManagementSnapshot(
    PhotoshootManagementStatus Status,
    string Message,
    PhotoshootManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static PhotoshootManagementSnapshot NotLoaded { get; } = new(
        PhotoshootManagementStatus.NotLoaded,
        "Photoshoots have not been loaded.",
        null,
        null);
}
