using System;
using PartyPulse.Api;

namespace PartyPulse.Vip;

public enum VipPerkManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed
}

public sealed record VipPerkManagementSnapshot(
    VipPerkManagementStatus Status,
    string Message,
    VipPerkManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static VipPerkManagementSnapshot NotLoaded { get; } = new(
        VipPerkManagementStatus.NotLoaded,
        "VIP perks have not been loaded.",
        null,
        null);
}
