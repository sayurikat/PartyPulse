using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public static class DjBookingStatusCodes
{
    public const string Pending = "pending";
    public const string Unavailable = "unavailable";
    public const string Confirmed = "confirmed";
    public const string Cancelled = "cancelled";
}

public static class DjPaymentStatusCodes
{
    public const string Started = "started";
    public const string Paid = "paid";
    public const string Cancelled = "cancelled";
}

public sealed record DjCapabilities(bool CanManageDirectory, bool CanManageSchedule, bool CanManagePayments);
public sealed record DjSummary(long DjId, string Name, string? TwitchUrl, bool Resident, string? Note, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);
public sealed record DjCharacterSummary(int CharacterId, long DjId, string CharacterName, string WorldName);
public sealed record DjBookingStatusSummary(string StatusCode, string DisplayName, byte SortOrder);

public sealed record DjBookingSummary(
    long BookingId,
    long OpeningId,
    long DjId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string StatusCode,
    string StatusName,
    string? Note,
    string DjName,
    string? TwitchUrl,
    bool Resident,
    long TimedMacroId,
    string? CustomMacroText,
    long PriceGil,
    long? PaymentId,
    string? PaymentStatus,
    string? PaymentTargetCharacterName,
    string? PaymentTargetWorldName,
    bool PaymentViaProxy,
    DateTimeOffset? PaymentStartedAt,
    DateTimeOffset? PaymentCompletedAt,
    DateTimeOffset? PaymentCancelledAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public bool ReservesTime =>
        string.Equals(StatusCode, DjBookingStatusCodes.Pending, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(StatusCode, DjBookingStatusCodes.Confirmed, StringComparison.OrdinalIgnoreCase);

    public bool HasActivePayment =>
        PaymentId is not null &&
        (string.Equals(PaymentStatus, DjPaymentStatusCodes.Started, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(PaymentStatus, DjPaymentStatusCodes.Paid, StringComparison.OrdinalIgnoreCase));

    public bool IsPaid => string.Equals(PaymentStatus, DjPaymentStatusCodes.Paid, StringComparison.OrdinalIgnoreCase);
}

public sealed record DjViewResponse(
    DjCapabilities Capabilities,
    DateTimeOffset ServerNow,
    long DefaultHourlyRateGil,
    IReadOnlyList<DjSummary> Djs,
    IReadOnlyList<DjCharacterSummary> Characters,
    IReadOnlyList<DjBookingSummary> Bookings,
    IReadOnlyList<DjBookingStatusSummary> Statuses);

public sealed record SaveDjRequest(string Name, string? TwitchUrl, bool Resident, string? Note);
public sealed record ArchiveDjResponse(long DjId, DateTimeOffset ArchivedAt);
public sealed record UpdateDjSettingsRequest(long DefaultHourlyRateGil);
public sealed record UpdateDjSettingsResponse(long DefaultHourlyRateGil, DateTimeOffset UpdatedAt);
public sealed record LinkDjCharacterRequest(long? DjId, string CharacterName, string WorldName);
public sealed record DjCharacterLinkResponse(int CharacterId, long? DjId);
public sealed record SaveDjBookingRequest(long OpeningId, long DjId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string StatusCode, string? Note, string? CustomMacroText, long PriceGil);
public sealed record DeleteDjBookingResponse(long BookingId, DateTimeOffset DeletedAt);
public sealed record StartDjPaymentRequest(string TargetCharacterName, string TargetWorldName, bool ProxyConfirmed);
public sealed record CancelDjPaymentRequest(bool RefundConfirmed);
public sealed record DjPaymentOperationResponse(long PaymentId, long BookingId, long AmountGil, string Status, string TargetCharacterName, string TargetWorldName, bool PaidViaProxy, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancelledAt);
