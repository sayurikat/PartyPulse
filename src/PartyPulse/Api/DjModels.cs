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

public sealed record DjCapabilities(
    bool CanManageDirectory,
    bool CanManageSchedule);

public sealed record DjSummary(
    long DjId,
    string Name,
    string? TwitchUrl,
    bool Resident,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record DjBookingStatusSummary(
    string StatusCode,
    string DisplayName,
    byte SortOrder);

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
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public bool ReservesTime =>
        string.Equals(StatusCode, DjBookingStatusCodes.Pending, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(StatusCode, DjBookingStatusCodes.Confirmed, StringComparison.OrdinalIgnoreCase);
}

public sealed record DjViewResponse(
    DjCapabilities Capabilities,
    DateTimeOffset ServerNow,
    IReadOnlyList<DjSummary> Djs,
    IReadOnlyList<DjBookingSummary> Bookings,
    IReadOnlyList<DjBookingStatusSummary> Statuses);

public sealed record SaveDjRequest(
    string Name,
    string? TwitchUrl,
    bool Resident,
    string? Note);

public sealed record ArchiveDjResponse(long DjId, DateTimeOffset ArchivedAt);

public sealed record SaveDjBookingRequest(
    long OpeningId,
    long DjId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string StatusCode,
    string? Note,
    string? CustomMacroText);

public sealed record DeleteDjBookingResponse(long BookingId, DateTimeOffset DeletedAt);
