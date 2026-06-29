using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record StaffCapabilities(
    bool CanManage,
    bool CanManageJobs,
    bool CanPay);

public sealed record StaffOpeningSummary(
    long OpeningId,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    string? Title,
    bool IsActive);

public sealed record StaffJobSummary(
    long JobDefinitionId,
    string Name,
    long HourlyRateGil,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record StaffVenueUserOption(
    int VenueUserId,
    string DisplayName,
    DateTimeOffset? DisabledAt,
    long? AssignedStaffMemberId);

public sealed record StaffMemberSummary(
    long StaffMemberId,
    string DisplayName,
    long JobDefinitionId,
    string JobName,
    long JobHourlyRateGil,
    int? VenueUserId,
    string? VenueUserDisplayName,
    long? CustomHourlyRateGil,
    long CustomFixedAmountGil,
    long EffectiveHourlyRateGil,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long UnpaidSalaryGil);

public sealed record StaffCharacterSummary(
    int CharacterId,
    string CharacterName,
    string WorldName,
    int? VenueUserId,
    long? StaffMemberId);

public sealed record StaffTimeEntrySummary(
    long TimeEntryId,
    long StaffMemberId,
    string StaffDisplayName,
    long OpeningId,
    DateTimeOffset ClockInAt,
    DateTimeOffset? ClockOutAt,
    long HourlyRateGil,
    long FixedAmountGil,
    long? SalaryGil,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    DateTimeOffset? PaidAt,
    long? FinancialTransactionId);

public sealed record StaffManagementViewResponse(
    StaffCapabilities Capabilities,
    DateTimeOffset ServerNow,
    long? DefaultOpeningId,
    IReadOnlyList<StaffOpeningSummary> Openings,
    IReadOnlyList<StaffJobSummary> Jobs,
    IReadOnlyList<StaffVenueUserOption> VenueUsers,
    IReadOnlyList<StaffMemberSummary> StaffMembers,
    IReadOnlyList<StaffCharacterSummary> Characters,
    IReadOnlyList<StaffTimeEntrySummary> TimeEntries);

public sealed record SaveStaffJobRequest(
    string Name,
    long HourlyRateGil,
    bool Archived);

public sealed record SaveStaffMemberRequest(
    string DisplayName,
    long JobDefinitionId,
    int? VenueUserId,
    long? CustomHourlyRateGil,
    long CustomFixedAmountGil,
    string? Note,
    bool Archived);

public sealed record LinkStaffCharacterRequest(
    long? StaffMemberId,
    string CharacterName,
    string WorldName);

public sealed record SaveStaffTimeEntryRequest(
    long StaffMemberId,
    long OpeningId,
    DateTimeOffset ClockInAt,
    DateTimeOffset? ClockOutAt);

public sealed record CancelStaffTimeEntryRequest(string? Reason);

public sealed record CreateStaffPayoutRequest(
    long StaffMemberId,
    string TargetCharacterName,
    string TargetWorldName,
    string? Note);

public sealed record StaffJobOperationResponse(long JobDefinitionId);
public sealed record StaffMemberOperationResponse(long StaffMemberId);
public sealed record StaffCharacterLinkResponse(int CharacterId, long? StaffMemberId);

public sealed record StaffTimeEntryOperationResponse(
    long TimeEntryId,
    long? SalaryGil,
    string Status);

public sealed record StaffTimeEntryCancellationResponse(
    long TimeEntryId,
    DateTimeOffset CancelledAt);

public sealed record StaffPayoutResponse(
    long TransactionId,
    long GrossCourtGil,
    long SalaryGil,
    long NetGil,
    string TradeDirection,
    long TradeAmountGil,
    string? TradeTargetCharacterName,
    string? TradeTargetWorldName,
    bool CanExecuteNow,
    DateTimeOffset CreatedAt);
