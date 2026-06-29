using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record BarCapabilities(
    bool CanView,
    bool CanSell,
    bool CanManage,
    bool CanManageSettlements,
    bool CanManageGame,
    bool CanCancelGame);

public sealed record BarSettingsSummary(
    decimal BuyoutSellerPercentage,
    int GambaTicketPriceGil,
    decimal GambaHousePercentage,
    DateTimeOffset? UpdatedAt,
    int? UpdatedByUserId);

public sealed record BarBuyoutPackageSummary(
    long PackageId,
    string Name,
    int PriceGil,
    string DurationMode,
    int? DurationMinutes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record BarBuyoutSaleSummary(
    long SaleId,
    long PackageId,
    string PackageName,
    int SoldByUserId,
    string SellerDisplayName,
    string BuyerCharacterName,
    string BuyerWorldName,
    long OpeningId,
    int PriceGil,
    decimal SellerPercentage,
    int SellerShareGil,
    int VenueShareGil,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset SoldAt,
    DateTimeOffset? PaidToVenueAt,
    int? PaidToVenueByUserId,
    long? PendingSettlementId,
    DateTimeOffset? VoidedAt,
    string? VoidReason);

public sealed record BarGambaGameSummary(
    long GameId,
    string Status,
    int StartingJackpotGil,
    long CurrentJackpotGil,
    long? FinalJackpotGil,
    int TicketPriceGil,
    decimal HousePercentage,
    DateTimeOffset StartedAt,
    DateTimeOffset? WonAt,
    string? WinnerCharacterName,
    string? WinnerWorldName,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    int TicketQuantity,
    long GrossSalesGil,
    long HouseShareGil,
    long JackpotContributionGil);

public sealed record BarGambaTicketSaleSummary(
    long SaleId,
    long GameId,
    int SoldByUserId,
    string SellerDisplayName,
    string BuyerCharacterName,
    string BuyerWorldName,
    int Quantity,
    int TicketPriceGil,
    int GrossGil,
    decimal HousePercentage,
    int HouseShareGil,
    int JackpotContributionGil,
    DateTimeOffset SoldAt,
    DateTimeOffset? PaidToVenueAt,
    int? PaidToVenueByUserId,
    long? PendingSettlementId,
    DateTimeOffset? VoidedAt,
    string? VoidReason);

public sealed record BarManagementViewResponse(
    BarCapabilities Capabilities,
    DateTimeOffset ServerNow,
    BarSettingsSummary Settings,
    long PersonalUnpaidGil,
    long PersonalPendingGil,
    long PersonalAvailableGil,
    int SuggestedStartingJackpotGil,
    BarBuyoutSaleSummary? ActiveBuyout,
    BarGambaGameSummary? ActiveGame,
    IReadOnlyList<BarBuyoutPackageSummary> BuyoutPackages,
    IReadOnlyList<BarBuyoutSaleSummary> BuyoutSales,
    IReadOnlyList<BarGambaTicketSaleSummary> GambaTicketSales,
    IReadOnlyList<BarGambaGameSummary> GambaGameHistory);

public sealed record CreateBarBuyoutPackageRequest(
    string Name,
    int PriceGil,
    string DurationMode,
    int? DurationMinutes);

public sealed record UpdateBarBuyoutPackageRequest(
    string Name,
    int PriceGil,
    string DurationMode,
    int? DurationMinutes,
    bool Archived);

public sealed record UpdateBarSettingsRequest(
    decimal BuyoutSellerPercentage,
    int GambaTicketPriceGil,
    decimal GambaHousePercentage);

public sealed record SellBarBuyoutRequest(
    string TargetCharacterName,
    string TargetWorldName,
    long PackageId);

public sealed record StartGambaGameRequest(int StartingJackpotGil);

public sealed record SellGambaTicketsRequest(
    string TargetCharacterName,
    string TargetWorldName,
    int Quantity);

public sealed record CompleteGambaGameRequest(
    string WinnerCharacterName,
    string WinnerWorldName);

public sealed record CancelGambaGameRequest(string? Reason);

public sealed record SetBarSalePaymentStatusRequest(bool Settled);
public sealed record CancelBarSaleRequest(string? Reason);

public sealed record BarBuyoutPackageOperationResponse(long PackageId);
public sealed record UpdateBarSettingsResponse(decimal BuyoutSellerPercentage, int GambaTicketPriceGil, decimal GambaHousePercentage);
public sealed record SellBarBuyoutResponse(long SaleId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record StartGambaGameResponse(long GameId, long CurrentJackpotGil, DateTimeOffset StartedAt);
public sealed record SellGambaTicketsResponse(long SaleId, long GameId, int Quantity, int GrossGil, int HouseShareGil, int JackpotContributionGil, long CurrentJackpotGil, DateTimeOffset SoldAt);
public sealed record CompleteGambaGameResponse(long GameId, string WinnerCharacterName, string WinnerWorldName, long FinalJackpotGil, DateTimeOffset WonAt);
public sealed record CancelGambaGameResponse(long GameId, DateTimeOffset CancelledAt, int CancelledTicketSaleCount);
public sealed record BarSalePaymentStatusResponse(long SaleId, bool Settled, DateTimeOffset? PaidToVenueAt);
public sealed record BarSaleCancellationResponse(long SaleId, DateTimeOffset VoidedAt);
