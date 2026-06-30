using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record OtherSalesCapabilities(
    bool CanView,
    bool CanSell,
    bool CanManageItems,
    bool CanManageSettlements,
    bool CanManageCommission);

public sealed record OtherSaleItemSummary(
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

public sealed record OtherSalePerkSummary(
    int PerkId,
    string Name);

public sealed record OtherSaleSummary(
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
    DateTimeOffset SoldAt,
    DateTimeOffset? PaidToVenueAt,
    int? PaidToVenueByUserId,
    long? PendingSettlementId,
    DateTimeOffset? VoidedAt,
    string? VoidReason);

public sealed record OtherSaleVipStatusSummary(
    int VipPlayerId,
    int CharacterId,
    string CharacterName,
    string WorldName,
    long SubscriptionId,
    int VipPackageId,
    string VipPackageName,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt);

public sealed record OtherSaleVipPerkAvailabilitySummary(
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

public sealed record OtherSalesManagementViewResponse(
    OtherSalesCapabilities Capabilities,
    long PersonalGrossGil,
    long PersonalSellerShareGil,
    long PersonalUnpaidGil,
    long PersonalPendingGil,
    long PersonalAvailableGil,
    IReadOnlyList<OtherSaleItemSummary> Items,
    IReadOnlyList<OtherSalePerkSummary> Perks,
    IReadOnlyList<OtherSaleSummary> Sales,
    IReadOnlyList<OtherSaleVipStatusSummary> VipStatuses,
    IReadOnlyList<OtherSaleVipPerkAvailabilitySummary> VipPerkAvailability);

public sealed record CreateOtherSaleItemRequest(
    string Name,
    int? PricePerUnitGil,
    int? PricePerkId,
    bool CanSellQuantity);

public sealed record UpdateOtherSaleItemRequest(
    string Name,
    int? PricePerUnitGil,
    int? PricePerkId,
    bool CanSellQuantity,
    bool Archived);

public sealed record UpdateOtherSaleSellerPercentageRequest(decimal SellerPercentage);

public sealed record OtherSaleItemOperationResponse(int ItemId);

public sealed record UpdateOtherSaleSellerPercentageResponse(
    int ItemId,
    decimal SellerPercentage);

public sealed record SellOtherSaleRequest(
    string TargetCharacterName,
    string TargetWorldName,
    int ItemId,
    int Quantity);

public sealed record SetOtherSalePaymentStatusRequest(bool Settled);

public sealed record CancelOtherSaleRequest(string? Reason);

public sealed record SellOtherSaleResponse(
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

public sealed record OtherSalePaymentStatusResponse(
    long SaleId,
    bool Settled,
    DateTimeOffset? PaidToVenueAt);

public sealed record OtherSaleCancellationResponse(
    long SaleId,
    DateTimeOffset VoidedAt,
    long? ReleasedPerkRedemptionId);
