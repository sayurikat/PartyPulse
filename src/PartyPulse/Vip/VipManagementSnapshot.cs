using System;
using PartyPulse.Api;

namespace PartyPulse.Vip;

public enum VipManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed,
}

public sealed record VipManagementSnapshot(
    VipManagementStatus Status,
    string Message,
    VipManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static VipManagementSnapshot NotLoaded { get; } = new(
        VipManagementStatus.NotLoaded,
        "VIP data has not been loaded.",
        null,
        null);
}
