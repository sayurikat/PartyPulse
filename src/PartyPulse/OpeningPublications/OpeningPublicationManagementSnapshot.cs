using System;
using PartyPulse.Api;

namespace PartyPulse.OpeningPublications;

public enum OpeningPublicationManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed,
}

public sealed record OpeningPublicationManagementSnapshot(
    OpeningPublicationManagementStatus Status,
    string Message,
    OpeningPublicationContextResponse? View,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? ReceivedAt)
{
    public static OpeningPublicationManagementSnapshot NotLoaded { get; } = new(
        OpeningPublicationManagementStatus.NotLoaded,
        "Opening publication data has not been loaded.",
        null,
        null,
        null);

    public DateTimeOffset EstimatedServerNow =>
        View is null || ReceivedAt is null
            ? DateTimeOffset.UtcNow
            : View.ServerNow + (DateTimeOffset.UtcNow - ReceivedAt.Value);
}
