using System;
using System.Collections.Generic;
using PartyPulse.Api;

namespace PartyPulse.VenueOpenings;

public enum VenueOpeningHistoryStatus
{
    NotLoaded,
    Loading,
    Ready,
    Failed,
}

public sealed record VenueOpeningHistorySnapshot(
    VenueOpeningHistoryStatus Status,
    string Message,
    IReadOnlyList<VenueOpeningScheduleItem> Openings,
    bool HasMore,
    DateTimeOffset? NextBeforeOpensAt,
    long? NextBeforeOpeningId,
    DateTimeOffset? LastAttemptAt)
{
    public IReadOnlyList<DjBookingSummary> DjBookings { get; init; } = Array.Empty<DjBookingSummary>();

    public static VenueOpeningHistorySnapshot NotLoaded { get; } = new(
        VenueOpeningHistoryStatus.NotLoaded,
        "Previous openings have not been loaded.",
        Array.Empty<VenueOpeningScheduleItem>(),
        false,
        null,
        null,
        null);
}
