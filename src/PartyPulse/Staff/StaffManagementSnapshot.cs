using System;
using PartyPulse.Api;

namespace PartyPulse.Staff;

public enum StaffManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed
}

public sealed record StaffManagementSnapshot(
    StaffManagementStatus Status,
    string Message,
    StaffManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static StaffManagementSnapshot NotLoaded { get; } = new(
        StaffManagementStatus.NotLoaded,
        "Staff has not been loaded.",
        null,
        null);
}
