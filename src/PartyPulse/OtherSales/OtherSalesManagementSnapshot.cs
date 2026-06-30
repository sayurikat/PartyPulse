using System;
using PartyPulse.Api;

namespace PartyPulse.OtherSales;

public enum OtherSalesManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed
}

public sealed record OtherSalesManagementSnapshot(
    OtherSalesManagementStatus Status,
    string Message,
    OtherSalesManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static OtherSalesManagementSnapshot NotLoaded { get; } = new(
        OtherSalesManagementStatus.NotLoaded,
        "Other Sales have not been loaded.",
        null,
        null);
}
