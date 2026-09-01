using System;
using PartyPulse.Api;

namespace PartyPulse.VenueOpenings;

public enum VenueOpeningScheduleStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed,
}

public sealed record VenueOpeningScheduleSnapshot(
    VenueOpeningScheduleStatus Status,
    string Message,
    VenueOpeningScheduleResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static VenueOpeningScheduleSnapshot NotLoaded { get; } = new(
        VenueOpeningScheduleStatus.NotLoaded,
        "Opening schedule has not been loaded.",
        null,
        null);
}
