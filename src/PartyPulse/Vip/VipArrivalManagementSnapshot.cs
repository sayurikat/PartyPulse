using System;
using PartyPulse.Api;

namespace PartyPulse.Vip;

public enum VipArrivalManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed,
}

public sealed record VipArrivalManagementSnapshot(
    VipArrivalManagementStatus Status,
    string Message,
    VipArrivalContextResponse? Context,
    DateTimeOffset? LastAttemptAt)
{
    public static VipArrivalManagementSnapshot NotLoaded { get; } = new(
        VipArrivalManagementStatus.NotLoaded,
        "VIP arrival data has not been loaded.",
        null,
        null);
}
