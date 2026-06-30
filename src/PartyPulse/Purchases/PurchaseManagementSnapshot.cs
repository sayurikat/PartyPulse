using System;
using PartyPulse.Api;

namespace PartyPulse.Purchases;

public enum PurchaseManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Denied,
    Failed
}

public sealed record PurchaseManagementSnapshot(
    PurchaseManagementStatus Status,
    string Message,
    PurchasesManagementViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static PurchaseManagementSnapshot NotLoaded { get; } = new(
        PurchaseManagementStatus.NotLoaded,
        "Purchases have not been loaded.",
        null,
        null);
}
