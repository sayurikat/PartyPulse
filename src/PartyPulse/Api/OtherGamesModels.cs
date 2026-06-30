using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record OtherGamesCapabilities(
    bool CanView,
    bool CanSell,
    bool CanManageItems,
    bool CanManageSettlements,
    bool CanManageCommission);

public sealed record OtherGameItemSummary(
    int ItemId,
    string Name,
    int? PricePerUnitGil,
    int? PricePerkId,
    string? PricePerkName,
    bool CanSellQuantity,
    decimal SellerPercentage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record OtherGamePerkSummary(int PerkId, string Name);

public sealed record OtherGameSaleSummary(
    long SaleId,
    int ItemId,
    string ItemName,
    int SoldByUserId,
    string SellerDisplayName,
    string BuyerCharacterName,
    string BuyerWorldName,
    int Quantity,
    string PriceType,
    long UnitPriceGil,
    long TotalGil,
    decimal SellerPercentage,
    long SellerShareGil,
    long VenueShareGil,
    int? PricePerkId,
    string? PricePerkName,
    long? PerkRedemptionId,
    string OutcomeStatus,
    long? WinAmountGil,
    long? NetVenueGil,
    DateTimeOffset? OutcomeRecordedAt,
    int? OutcomeRecordedByUserId,
    bool CanSetOutcome,
    DateTimeOffset SoldAt,
    DateTimeOffset? SettledAt,
    int? SettledByUserId,
    long? PendingSettlementId,
    DateTimeOffset? VoidedAt,
    string? VoidReason);

public sealed record OtherGameSellerBalanceSummary(
    int SellerUserId,
    string SellerDisplayName,
    string? SellerCharacterName,
    string? SellerWorldName,
    long UnsettledNetGil,
    long PendingNetGil,
    long AvailableNetGil,
    int AwaitingOutcomeCount);

public sealed record OtherGameVipStatusSummary(
    int VipPlayerId,
    int CharacterId,
    string CharacterName,
    string WorldName,
    long SubscriptionId,
    int VipPackageId,
    string VipPackageName,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt);

public sealed record OtherGameVipPerkAvailabilitySummary(
    int VipPlayerId,
    int CharacterId,
    string CharacterName,
    string WorldName,
    long SubscriptionId,
    int VipPackageId,
    string VipPackageName,
    int PackagePerkId,
    int PerkId,
    string PerkName,
    string? RenewalUnit,
    int? RenewalInterval,
    DateTimeOffset PeriodStart,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset? NextResetAt,
    bool Available,
    long? RedemptionId,
    DateTimeOffset? LastRedeemedAt);

public sealed record OtherGamesManagementViewResponse(
    OtherGamesCapabilities Capabilities,
    long PersonalGrossGil,
    long PersonalSellerShareGil,
    long PersonalWinGil,
    long PersonalUnsettledNetGil,
    long PersonalPendingNetGil,
    long PersonalAvailableNetGil,
    int PersonalAwaitingOutcomeCount,
    int PersonalAvailableSaleCount,
    IReadOnlyList<OtherGameItemSummary> Items,
    IReadOnlyList<OtherGamePerkSummary> Perks,
    IReadOnlyList<OtherGameSaleSummary> Sales,
    IReadOnlyList<OtherGameSellerBalanceSummary> SellerBalances,
    IReadOnlyList<OtherGameVipStatusSummary> VipStatuses,
    IReadOnlyList<OtherGameVipPerkAvailabilitySummary> VipPerkAvailability);

public sealed record CreateOtherGameItemRequest(string Name, int? PricePerUnitGil, int? PricePerkId, bool CanSellQuantity);
public sealed record UpdateOtherGameItemRequest(string Name, int? PricePerUnitGil, int? PricePerkId, bool CanSellQuantity, bool Archived);
public sealed record UpdateOtherGameSellerPercentageRequest(decimal SellerPercentage);
public sealed record OtherGameItemOperationResponse(int ItemId);
public sealed record UpdateOtherGameSellerPercentageResponse(int ItemId, decimal SellerPercentage);
public sealed record SellOtherGameRequest(string TargetCharacterName, string TargetWorldName, int ItemId, int Quantity);
public sealed record SetOtherGameOutcomeRequest(string Outcome, long? WinAmountGil);
public sealed record SetOtherGameSettlementStatusRequest(bool Settled);
public sealed record CancelOtherGameSaleRequest(string? Reason);

public sealed record SellOtherGameResponse(
    long SaleId,
    int ItemId,
    string ItemName,
    string BuyerCharacterName,
    string BuyerWorldName,
    int Quantity,
    string PriceType,
    long UnitPriceGil,
    long TotalGil,
    decimal SellerPercentage,
    long SellerShareGil,
    long VenueShareGil,
    int? PricePerkId,
    string? PricePerkName,
    long? PerkRedemptionId,
    DateTimeOffset? PerkNextAvailableAt,
    DateTimeOffset SoldAt);

public sealed record OtherGameOutcomeResponse(long SaleId, string OutcomeStatus, long WinAmountGil, long NetVenueGil, DateTimeOffset OutcomeRecordedAt);
public sealed record OtherGameSettlementStatusResponse(long SaleId, bool Settled, DateTimeOffset? SettledAt);
public sealed record OtherGameSaleCancellationResponse(long SaleId, DateTimeOffset VoidedAt, long? ReleasedPerkRedemptionId);
