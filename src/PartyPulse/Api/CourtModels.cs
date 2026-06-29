using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record CourtCapabilities(
    bool CanSell,
    bool CanAccount,
    bool CanManage,
    bool CanFinance);

public sealed record CourtOfferSummary(
    long OfferId,
    string Name,
    int DurationMinutes,
    string PriceType,
    long? PriceGil,
    int? PricePerkId,
    string? PricePerkName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record CourtSaleSummary(
    long SaleId,
    long OfferId,
    string OfferName,
    int Quantity,
    int UnitDurationMinutes,
    int TotalDurationMinutes,
    string PriceType,
    long UnitPriceGil,
    long TotalPriceGil,
    int? PricePerkId,
    string? PricePerkName,
    long? PerkRedemptionId,
    int SoldByUserId,
    string SellerDisplayName,
    long? SellerStaffMemberId,
    string? SellerStaffDisplayName,
    string BuyerCharacterName,
    string BuyerWorldName,
    DateTimeOffset SoldAt,
    DateTimeOffset? SettledAt,
    long? FinancialTransactionId,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    bool IsOwnSale);

public sealed record CourtVipStatusSummary(
    int VipPlayerId,
    int CharacterId,
    string CharacterName,
    string WorldName,
    long SubscriptionId,
    int VipPackageId,
    string VipPackageName,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt);

public sealed record CourtVipPerkAvailabilitySummary(
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

public sealed record CourtAccountantSummary(
    long AccountantAccountId,
    int AccountantUserId,
    string AccountantDisplayName,
    long? StaffMemberId,
    string? StaffDisplayName,
    long StandingBalanceGil,
    long UnpaidSalaryGil,
    bool CanReceiveSettlements);

public sealed record CourtTransactionItemSummary(
    long TransactionId,
    string ItemType,
    long SourceId,
    long AmountGil,
    string ItemName,
    string ItemDetail);

public sealed record CourtTransactionSummary(
    long TransactionId,
    string TransactionType,
    string CollectorMode,
    long? StaffMemberId,
    string? StaffDisplayName,
    int? StaffUserId,
    long? AccountantAccountId,
    int? CollectorUserId,
    string? CollectorDisplayName,
    int? RequiredConfirmerUserId,
    string? StaffCharacterName,
    string? StaffWorldName,
    string? CollectorCharacterName,
    string? CollectorWorldName,
    long GrossCourtGil,
    long SalaryGil,
    long AdjustmentGil,
    long RequestedPrepayGil,
    long StandingBalanceBeforeGil,
    long LedgerDeltaGil,
    string TradeDirection,
    long TradeAmountGil,
    string Status,
    DateTimeOffset CreatedAt,
    int CreatedByUserId,
    string? CreatedByDisplayName,
    DateTimeOffset? ConfirmedAt,
    int? ConfirmedByUserId,
    string? Note,
    bool CanExecuteTrade,
    bool CanConfirm,
    bool CanCancel,
    string? TradeTargetCharacterName,
    string? TradeTargetWorldName,
    IReadOnlyList<CourtTransactionItemSummary> Items);

public sealed record CourtManagementViewResponse(
    CourtCapabilities Capabilities,
    DateTimeOffset ServerNow,
    long? CurrentStaffMemberId,
    long PersonalUnsettledCourtGil,
    long PersonalAdjustmentGil,
    long PersonalUnpaidSalaryGil,
    IReadOnlyList<CourtOfferSummary> Offers,
    IReadOnlyList<CourtSaleSummary> Sales,
    IReadOnlyList<CourtAccountantSummary> AccountantAccounts,
    IReadOnlyList<CourtTransactionSummary> Transactions,
    IReadOnlyList<CourtVipStatusSummary> VipStatuses,
    IReadOnlyList<CourtVipPerkAvailabilitySummary> VipPerkAvailability);

public sealed record SaveCourtOfferRequest(
    string Name,
    int DurationMinutes,
    string PriceType,
    long? PriceGil,
    int? PricePerkId,
    bool Archived);

public sealed record SellCourtServiceRequest(
    long OfferId,
    int Quantity,
    string TargetCharacterName,
    string TargetWorldName);

public sealed record CancelCourtSaleRequest(string? Reason);

public sealed record CreateCourtStaffSettlementRequest(
    string CollectorMode,
    string StaffCharacterName,
    string StaffWorldName,
    string? Note);

public sealed record CreateCourtAccountantPrepayRequest(
    long AccountantAccountId,
    string TargetCharacterName,
    string TargetWorldName,
    long PrepayGil,
    string? Note);

public sealed record CreateCourtAccountantFinalizationRequest(
    long AccountantAccountId,
    string CounterpartyCharacterName,
    string CounterpartyWorldName,
    string? Note);

public sealed record ConfirmCourtTransactionRequest(string? Note);
public sealed record CancelCourtTransactionRequest(string? Reason);
public sealed record CourtOfferOperationResponse(long OfferId);

public sealed record SellCourtServiceResponse(
    long SaleId,
    long OfferId,
    string OfferName,
    int Quantity,
    int UnitDurationMinutes,
    int TotalDurationMinutes,
    string PriceType,
    long UnitPriceGil,
    long TotalPriceGil,
    int? PricePerkId,
    string? PricePerkName,
    long? PerkRedemptionId,
    DateTimeOffset? PerkNextAvailableAt,
    DateTimeOffset SoldAt);

public sealed record CourtSaleCancellationResponse(
    long SaleId,
    DateTimeOffset VoidedAt,
    long? ReleasedPerkRedemptionId,
    long? AdjustmentId,
    long AdjustmentGil);

public sealed record CourtFinancialTransactionResponse(
    long TransactionId,
    long GrossCourtGil,
    long SalaryGil,
    long AdjustmentGil,
    long NetGil,
    string TradeDirection,
    long TradeAmountGil,
    string? TradeTargetCharacterName,
    string? TradeTargetWorldName,
    bool CanExecuteNow,
    DateTimeOffset CreatedAt);

public sealed record CourtTransactionConfirmationResponse(
    long TransactionId,
    DateTimeOffset ConfirmedAt);

public sealed record CourtTransactionCancellationResponse(
    long TransactionId,
    DateTimeOffset CancelledAt);
