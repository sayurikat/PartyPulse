using Dalamud.Configuration;
using PartyPulse.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PartyPulse;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool IsConfigWindowMovable { get; set; } = true;

    public bool AutoConnect { get; set; } = true;

    public string ApiBaseUrl { get; set; } = "https://partypulse.azurewebsites.net";

    public Guid SelectedVenueProfileId { get; set; } = Guid.Empty;

    public List<VenueConnectionConfiguration> VenueConnections { get; set; } = [];

    public bool Normalize()
    {
        var changed = false;

        ApiBaseUrl = ApiBaseUrl?.Trim() ?? string.Empty;

        foreach (var venue in VenueConnections)
        {
            if (venue.ProfileId == Guid.Empty)
            {
                venue.ProfileId = Guid.NewGuid();
                changed = true;
            }

            venue.VenueCode ??= string.Empty;
            var normalizedVenueCode = VenueConnectionConfiguration.NormalizeVenueCode(venue.VenueCode);
            if (!string.Equals(venue.VenueCode, normalizedVenueCode, StringComparison.Ordinal))
            {
                venue.VenueCode = normalizedVenueCode;
                changed = true;
            }

            venue.VenueName ??= string.Empty;
            venue.AddressWorldName ??= string.Empty;
            venue.AddressCityName ??= string.Empty;
            venue.DisplayName ??= string.Empty;
            venue.DeviceName ??= string.Empty;
            venue.RefreshToken ??= string.Empty;
        }

        if (SelectedVenueProfileId == Guid.Empty ||
            VenueConnections.All(x => x.ProfileId != SelectedVenueProfileId))
        {
            var newSelection = VenueConnections.FirstOrDefault()?.ProfileId ?? Guid.Empty;
            if (newSelection != SelectedVenueProfileId)
            {
                SelectedVenueProfileId = newSelection;
                changed = true;
            }
        }

        if (Version < 2)
        {
            Version = 2;
            changed = true;
        }

        return changed;
    }

    public VenueConnectionConfiguration? GetSelectedVenue() =>
        VenueConnections.FirstOrDefault(x => x.ProfileId == SelectedVenueProfileId)
        ?? VenueConnections.FirstOrDefault();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
