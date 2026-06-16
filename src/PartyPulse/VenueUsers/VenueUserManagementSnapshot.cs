using System;
using PartyPulse.Api;

namespace PartyPulse.VenueUsers;

public enum VenueUserManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed,
}

public sealed record IssuedOneTimeCode(
    int UserId,
    string DisplayName,
    string Code,
    DateTimeOffset ExpiresAt);

public sealed record VenueUserManagementSnapshot(
    VenueUserManagementStatus Status,
    string Message,
    VenueUserManagementViewResponse? View,
    IssuedOneTimeCode? LastInviteCode,
    DateTimeOffset? LastAttemptAt)
{
    public static VenueUserManagementSnapshot NotLoaded { get; } = new(
        VenueUserManagementStatus.NotLoaded,
        "Venue users have not been loaded.",
        null,
        null,
        null);
}
