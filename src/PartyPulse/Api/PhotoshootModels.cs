using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record PhotoshootCapabilities(
    bool CanView,
    bool CanSell,
    bool CanManagePackages,
    bool CanManageSettlements,
    bool CanManageCommission);

public sealed record PhotoshootPackageSummary(
    int PackageId,
    string Name,
    int IncludedCharacters,
    int? BasePriceGil,
    int? PricePerkId,
    string? PricePerkName,
    int AdditionalCharacterPriceGil,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record PhotoshootSaleSummary(
    long SaleId,
    int PackageId,
    string PackageName,
    int SoldByUserId,
    string SellerDisplayName,
    string BuyerCharacterName,
    string BuyerWorldName,
    int IncludedCharacters,
    int AdditionalCharacters,
    string BaseCostType,
    int? BasePriceGil,
    int AdditionalCharacterPriceGil,
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

public sealed record PhotoshootVipStatusSummary(
    int VipPlayerId,
    int CharacterId,
    string CharacterName,
    string WorldName,
    long SubscriptionId,
    int VipPackageId,
    string VipPackageName,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt);

public sealed record PhotoshootVipPerkAvailabilitySummary(
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

public sealed record PhotoshootManagementViewResponse(
    PhotoshootCapabilities Capabilities,
    decimal SellerPercentage,
    long PersonalGrossGil,
    long PersonalSellerShareGil,
    long PersonalUnpaidGil,
    long PersonalPendingGil,
    long PersonalAvailableGil,
    IReadOnlyList<PhotoshootPackageSummary> Packages,
    IReadOnlyList<PhotoshootSaleSummary> Sales,
    IReadOnlyList<PhotoshootVipStatusSummary> VipStatuses,
    IReadOnlyList<PhotoshootVipPerkAvailabilitySummary> VipPerkAvailability);

public sealed record CreatePhotoshootPackageRequest(
    string Name,
    int IncludedCharacters,
    int? BasePriceGil,
    int? PricePerkId,
    int AdditionalCharacterPriceGil);

public sealed record UpdatePhotoshootPackageRequest(
    string Name,
    int IncludedCharacters,
    int? BasePriceGil,
    int? PricePerkId,
    int AdditionalCharacterPriceGil,
    bool Archived);

public sealed record UpdatePhotoshootSettingsRequest(decimal SellerPercentage);

public sealed record PhotoshootPackageOperationResponse(int PackageId);

public sealed record UpdatePhotoshootSettingsResponse(decimal SellerPercentage);

public sealed record SellPhotoshootRequest(
    string TargetCharacterName,
    string TargetWorldName,
    int PackageId,
    int AdditionalCharacters);

public sealed record SetPhotoshootSalePaymentStatusRequest(bool Settled);

public sealed record CancelPhotoshootSaleRequest(string? Reason);

public sealed record SellPhotoshootResponse(
    long SaleId,
    int PackageId,
    string PackageName,
    string BuyerCharacterName,
    string BuyerWorldName,
    int IncludedCharacters,
    int AdditionalCharacters,
    string BaseCostType,
    long TotalGil,
    decimal SellerPercentage,
    long SellerShareGil,
    long VenueShareGil,
    int? PricePerkId,
    string? PricePerkName,
    long? PerkRedemptionId,
    DateTimeOffset? PerkNextAvailableAt,
    DateTimeOffset SoldAt);

public sealed record PhotoshootSalePaymentStatusResponse(
    long SaleId,
    bool Settled,
    DateTimeOffset? PaidToVenueAt);

public sealed record PhotoshootSaleCancellationResponse(
    long SaleId,
    DateTimeOffset VoidedAt,
    long? ReleasedPerkRedemptionId);
