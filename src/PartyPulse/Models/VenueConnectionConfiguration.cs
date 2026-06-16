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

    public bool IsRegistered => DeviceId > 0 && !string.IsNullOrWhiteSpace(RefreshToken);

    public string DisplayLabel => !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName.Trim()
        : VenueId > 0
            ? $"Venue {VenueId}"
            : "Unconfigured venue";

    public bool TryValidate(out string error) => TryValidateForRefresh(out error);

    public bool TryValidateForEnrollment(out string error)
    {
        if (VenueId <= 0)
        {
            error = "Venue ID must be greater than zero.";
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
}
