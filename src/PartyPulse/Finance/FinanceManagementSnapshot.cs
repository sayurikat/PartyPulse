using System;
using PartyPulse.Api;

namespace PartyPulse.Finance;

public enum FinanceManagementStatus
{
    NotLoaded,
    Loading,
    Ready,
    Failed,
}

public sealed record FinanceManagementSnapshot(
    FinanceManagementStatus Status,
    string Message,
    FinanceViewResponse? View,
    DateTimeOffset? LastAttemptAt)
{
    public static FinanceManagementSnapshot NotLoaded { get; } = new(
        FinanceManagementStatus.NotLoaded,
        "Finance data has not been loaded.",
        null,
        null);
}
