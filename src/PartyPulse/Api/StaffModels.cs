using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record StaffCapabilities(bool CanManage, bool CanManageJobs, bool CanPay, bool CanObserveFirstSeen);

public sealed record StaffOpeningSummary(
    long OpeningId,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    int AddressWorldId,
    string AddressWorldName,
    int AddressCityId,
    string AddressCityName,
    int AddressWard,
    int AddressPlot,
    string? Title,
    bool IsActive);

public sealed record StaffJobSummary(long JobDefinitionId, string Name, long HourlyRateGil, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt);
public sealed record StaffVenueUserOption(int VenueUserId, string DisplayName, DateTimeOffset? DisabledAt, long? AssignedStaffMemberId);
public sealed record StaffMemberSummary(
    long StaffMemberId, string DisplayName, long JobDefinitionId, string JobName,
    long JobHourlyRateGil, int? VenueUserId, string? VenueUserDisplayName,
    long? CustomHourlyRateGil, long CustomFixedAmountGil, long EffectiveHourlyRateGil,
    string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt,
    long UnpaidSalaryGil, long SalaryDeductionGil, long StandingBalanceGil,
    long UnsettledCourtGil, long UnsettledAdjustmentGil, bool RequiresCourtSettlement);
public sealed record StaffCharacterSummary(int CharacterId, string CharacterName, string WorldName, int? VenueUserId, long? StaffMemberId);
public sealed record StaffFirstSeenSummary(
    long FirstSeenId, long OpeningId, long StaffMemberId, string StaffDisplayName,
    int CharacterId, string CharacterName, string WorldName,
    DateTimeOffset FirstSeenAt, int FirstSeenByUserId);
public sealed record StaffAbsenceSummary(
    long AbsenceId, long OpeningId, long StaffMemberId, string StaffDisplayName,
    string ReasonCode, DateTimeOffset RecordedAt, int RecordedByUserId,
    DateTimeOffset? CancelledAt, int? CancelledByUserId, string? CancelReason);
public sealed record StaffTimeEntrySummary(
    long TimeEntryId, long StaffMemberId, string StaffDisplayName, long OpeningId,
    DateTimeOffset ClockInAt, DateTimeOffset? ClockOutAt,
    long HourlyRateGil, long FixedAmountGil, long? SalaryGil, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, DateTimeOffset? CancelledAt,
    string? CancelReason, DateTimeOffset? PaidAt, long? FinancialTransactionId,
    string? PaidToCharacterName, string? PaidToWorldName, bool PaidViaProxy,
    string ClockInSource, DateTimeOffset? OvertimeConfirmedAt,
    int? OvertimeConfirmedByUserId, bool IsOvertime);

public sealed record StaffManagementViewResponse(
    StaffCapabilities Capabilities,
    DateTimeOffset ServerNow,
    long? DefaultOpeningId,
    IReadOnlyList<StaffOpeningSummary> Openings,
    IReadOnlyList<StaffJobSummary> Jobs,
    IReadOnlyList<StaffVenueUserOption> VenueUsers,
    IReadOnlyList<StaffMemberSummary> StaffMembers,
    IReadOnlyList<StaffCharacterSummary> Characters,
    IReadOnlyList<StaffFirstSeenSummary> FirstSeen,
    IReadOnlyList<StaffAbsenceSummary> Absences,
    IReadOnlyList<StaffTimeEntrySummary> TimeEntries);

public sealed record SaveStaffJobRequest(string Name, long HourlyRateGil, bool Archived);
public sealed record SaveStaffMemberRequest(string DisplayName, long JobDefinitionId, int? VenueUserId, long? CustomHourlyRateGil, long CustomFixedAmountGil, string? Note, bool Archived);
public sealed record LinkStaffCharacterRequest(long? StaffMemberId, string CharacterName, string WorldName);
public sealed record SaveStaffTimeEntryRequest(
    long StaffMemberId,
    long OpeningId,
    DateTimeOffset ClockInAt,
    DateTimeOffset? ClockOutAt,
    string ClockInSource = "manual",
    bool OvertimeConfirmed = false);
public sealed record CancelStaffTimeEntryRequest(string? Reason);
public sealed record StaffFirstSeenObservationRequest(string CharacterName, string WorldName);
public sealed record ObserveStaffFirstSeenRequest(long OpeningId, IReadOnlyList<StaffFirstSeenObservationRequest> Observations);
public sealed record SetStaffAbsenceRequest(long StaffMemberId, string ReasonCode);
public sealed record CancelStaffAbsenceRequest(string? Reason);
public sealed record CreateStaffPayoutRequest(long StaffMemberId, string? TargetCharacterName, string? TargetWorldName, bool AllowProxy, bool RepaymentReceived, string? Note);

public sealed record StaffJobOperationResponse(long JobDefinitionId);
public sealed record StaffMemberOperationResponse(long StaffMemberId);
public sealed record StaffCharacterLinkResponse(int CharacterId, long? StaffMemberId);
public sealed record StaffTimeEntryOperationResponse(long TimeEntryId, long? SalaryGil, string Status);
public sealed record StaffTimeEntryCancellationResponse(long TimeEntryId, DateTimeOffset CancelledAt, long? AdjustmentId, long AdjustmentGil);
public sealed record ObserveStaffFirstSeenResponse(long OpeningId, int MatchedCount, int InsertedCount);
public sealed record StaffAbsenceOperationResponse(long AbsenceId, long OpeningId, long StaffMemberId, string ReasonCode, DateTimeOffset RecordedAt);
public sealed record StaffAbsenceCancellationResponse(long AbsenceId, DateTimeOffset CancelledAt);
public sealed record StaffPayoutResponse(
    long TransactionId, long GrossSalesGil, long CourtRetainedGil, long GrossCourtGil,
    long SalaryGil, long AdjustmentGil, long NetGil, string TradeDirection,
    long TradeAmountGil, string? TradeTargetCharacterName, string? TradeTargetWorldName,
    bool CanExecuteNow, DateTimeOffset CreatedAt);
