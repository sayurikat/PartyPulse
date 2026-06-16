using System;

namespace PartyPulse.Models;

[Serializable]
public sealed class VenueConnectionConfiguration
{
    public Guid ProfileId { get; set; } = Guid.NewGuid();

    public int VenueId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public int DeviceId { get; set; }

    public string DeviceName { get; set; } = Environment.MachineName;

    // Refresh tokens are intentionally persisted per device. Access tokens are never saved.
    public string RefreshToken { get; set; } = string.Empty;

    public DateTimeOffset? RefreshTokenUpdatedAt { get; set; }

    public string DisplayLabel => !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName.Trim()
        : VenueId > 0
            ? $"Venue {VenueId}"
            : "Unconfigured venue";

    public bool TryValidate(out string error)
    {
        if (VenueId <= 0)
        {
            error = "Venue ID must be greater than zero.";
            return false;
        }

        if (DeviceId <= 0)
        {
            error = "Device ID must be greater than zero.";
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
}
