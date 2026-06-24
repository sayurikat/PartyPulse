using System;
using PartyPulse.Api;

namespace PartyPulse.Models;

[Serializable]
public sealed class VenueConnectionConfiguration
{
    public Guid ProfileId { get; set; } = Guid.NewGuid();

    // VenueId is an internal API/SQL identifier. Visitors and staff use VenueCode.
    public int VenueId { get; set; }

    public string VenueCode { get; set; } = string.Empty;

    public string VenueName { get; set; } = string.Empty;

    public string AddressWorldName { get; set; } = string.Empty;

    public string AddressCityName { get; set; } = string.Empty;

    public int? AddressWard { get; set; }

    public int? AddressPlot { get; set; }

    // Optional local alias. The published venue name remains in VenueName.
    public string DisplayName { get; set; } = string.Empty;

    // Local-only display/input timezone for this venue profile.
    public string DisplayTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;

    public int DeviceId { get; set; }

    public string DeviceName { get; set; } = Environment.MachineName;

    // Refresh tokens are intentionally persisted per device. Access tokens are never saved.
    public string RefreshToken { get; set; } = string.Empty;

    public DateTimeOffset? RefreshTokenUpdatedAt { get; set; }

    public bool IsPublicVenue =>
        VenueId > 0 &&
        TryNormalizeVenueCode(VenueCode, out _);

    public bool IsRegistered => DeviceId > 0 && !string.IsNullOrWhiteSpace(RefreshToken);

    public bool HasCompleteAddress =>
        !string.IsNullOrWhiteSpace(AddressWorldName) &&
        !string.IsNullOrWhiteSpace(AddressCityName) &&
        AddressWard > 0 &&
        AddressPlot > 0;

    public string DisplayLabel => !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName.Trim()
        : !string.IsNullOrWhiteSpace(VenueName)
            ? VenueName.Trim()
            : !string.IsNullOrWhiteSpace(VenueCode)
                ? VenueCode.Trim().ToUpperInvariant()
                : VenueId > 0
                    ? $"Venue {VenueId}"
                    : "Unconfigured venue";

    public string AddressDisplay => HasCompleteAddress
        ? $"{AddressWorldName}, {AddressCityName}, Ward {AddressWard}, Plot {AddressPlot}"
        : "Address not published";

    public bool TryValidate(out string error) => TryValidateForRefresh(out error);

    public bool TryValidateForEnrollment(out string error)
    {
        if (!TryNormalizeVenueCode(VenueCode, out _))
        {
            error = "A valid venue code is required (PULSE-XXXXXX).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            error = "A device name is required.";
            return false;
        }

        if (DeviceName.Trim().Length > 50)
        {
            error = "Device name must be 50 characters or fewer.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TryValidateForRefresh(out string error)
    {
        if (!TryValidateForEnrollment(out error))
        {
            return false;
        }

        if (VenueId <= 0)
        {
            error = "This venue profile has not downloaded its public venue details yet.";
            return false;
        }

        if (DeviceId <= 0)
        {
            error = "This venue profile has not registered a device yet.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(RefreshToken))
        {
            error = "A refresh token is required.";
            return false;
        }

        if (RefreshToken.Trim().Length != 43)
        {
            error = "The current API expects a 43-character refresh token.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public void ApplyPublicVenue(PublicVenueResponse venue)
    {
        VenueId = venue.VenueId;
        VenueCode = NormalizeVenueCode(venue.VenueCode);
        VenueName = venue.VenueName?.Trim() ?? string.Empty;
        AddressWorldName = venue.AddressWorldName?.Trim() ?? string.Empty;
        AddressCityName = venue.AddressCityName?.Trim() ?? string.Empty;
        AddressWard = venue.AddressWard;
        AddressPlot = venue.AddressPlot;
    }

    public static bool TryNormalizeVenueCode(string? value, out string normalized)
    {
        normalized = NormalizeVenueCode(value);
        if (normalized.Length != 12 || !normalized.StartsWith("PULSE-", StringComparison.Ordinal))
        {
            return false;
        }

        const string alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        foreach (var character in normalized.AsSpan(6))
        {
            if (alphabet.IndexOf(character) < 0)
            {
                return false;
            }
        }

        return true;
    }

    public static string NormalizeVenueCode(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        return normalized.Length == 6 ? $"PULSE-{normalized}" : normalized;
    }
}
