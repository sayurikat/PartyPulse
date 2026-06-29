using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record FinanceCapabilities(bool CanManageSettlements);

public sealed record FinancialSettlementSummary(
    long SettlementId,
    string SettlementType,
    long AmountGil,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RespondedAt,
    string? ResponseNote,
    int InitiatedByUserId,
    string InitiatedByDisplayName,
    string InitiatedByCharacterName,
    string InitiatedByWorldName,
    int TargetUserId,
    string TargetUserDisplayName,
    string TargetCharacterName,
    string TargetWorldName,
    int? RespondedByUserId,
    string? RespondedByDisplayName,
    int ItemCount)
{
    public bool IsPending => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase);
}

public sealed record FinancialSettlementItemSummary(
    long SettlementItemId,
    long SettlementId,
    string SourceType,
    long SourceId,
    long AmountGil,
    DateTimeOffset? ReleasedAt,
    int? VipPlayerId,
    string? PackageName,
    DateTimeOffset? PurchasedAt,
    string? CustomerCharacterName,
    string? CustomerWorldName);

public sealed record FinanceViewResponse(
    FinanceCapabilities Capabilities,
    long PersonalUnpaidVipGil,
    long PersonalPendingVipGil,
    long PersonalAvailableVipGil,
    long PersonalUnpaidPhotoshootGil,
    long PersonalPendingPhotoshootGil,
    long PersonalAvailablePhotoshootGil,
    int VenuePendingCount,
    IReadOnlyList<FinancialSettlementSummary> Settlements,
    IReadOnlyList<FinancialSettlementItemSummary> Items);

public sealed record CreateVipSettlementRequest(
    string TargetCharacterName,
    string TargetWorldName);

public sealed record CreatePhotoshootSettlementRequest(
    string TargetCharacterName,
    string TargetWorldName);

public sealed record CreateVipSettlementResponse(
    long SettlementId,
    long AmountGil,
    int TargetUserId,
    int TargetCharacterId,
    string TargetCharacterName,
    string TargetWorldName,
    string TargetUserDisplayName,
    DateTimeOffset CreatedAt);

public sealed record CreatePhotoshootSettlementResponse(
    long SettlementId,
    long AmountGil,
    int TargetUserId,
    int TargetCharacterId,
    string TargetCharacterName,
    string TargetWorldName,
    string TargetUserDisplayName,
    DateTimeOffset CreatedAt);

public sealed record RespondSettlementRequest(
    string Decision,
    string? Note);

public sealed record RespondSettlementResponse(
    long SettlementId,
    string Status,
    long AmountGil,
    DateTimeOffset RespondedAt);
