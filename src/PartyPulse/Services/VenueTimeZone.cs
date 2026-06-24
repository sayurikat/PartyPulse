using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PartyPulse.Models;

namespace PartyPulse.Services;

public static class VenueTimeZone
{
    private static readonly IReadOnlyList<TimeZoneInfo> SystemTimeZones = TimeZoneInfo
        .GetSystemTimeZones()
        .OrderBy(static value => value.BaseUtcOffset)
        .ThenBy(static value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyList<TimeZoneInfo> Available => SystemTimeZones;

    public static TimeZoneInfo Resolve(VenueConnectionConfiguration venue) =>
        Resolve(venue.DisplayTimeZoneId);

    public static TimeZoneInfo Resolve(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
                // Fall back to the device zone below.
            }
            catch (InvalidTimeZoneException)
            {
                // Fall back to the device zone below.
            }
        }

        return TimeZoneInfo.Local;
    }

    public static bool IsValid(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return false;
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    public static DateTimeOffset Convert(VenueConnectionConfiguration venue, DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Resolve(venue));

    public static DateTimeOffset Convert(TimeZoneInfo timeZone, DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, timeZone);

    public static string Format(
        VenueConnectionConfiguration venue,
        DateTimeOffset value,
        string format,
        IFormatProvider? provider = null) =>
        Convert(venue, value).ToString(format, provider ?? CultureInfo.CurrentCulture);

    public static DateTime DisplayDate(VenueConnectionConfiguration venue, DateTimeOffset value) =>
        Convert(venue, value).Date;

    public static bool TryParseExact(
        VenueConnectionConfiguration venue,
        string value,
        string format,
        IFormatProvider provider,
        out DateTimeOffset result,
        out string error)
    {
        result = default;
        if (!DateTime.TryParseExact(
                value.Trim(),
                format,
                provider,
                DateTimeStyles.None,
                out var displayDateTime))
        {
            error = $"Time must use {format}.";
            return false;
        }

        displayDateTime = DateTime.SpecifyKind(displayDateTime, DateTimeKind.Unspecified);
        var timeZone = Resolve(venue);
        if (timeZone.IsInvalidTime(displayDateTime))
        {
            error = $"That time does not exist in {timeZone.DisplayName} because of a daylight-saving transition.";
            return false;
        }

        if (timeZone.IsAmbiguousTime(displayDateTime))
        {
            error = $"That time occurs twice in {timeZone.DisplayName} because of a daylight-saving transition. Choose an unambiguous time.";
            return false;
        }

        try
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(displayDateTime, timeZone);
            result = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
