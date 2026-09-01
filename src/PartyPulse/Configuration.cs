using Dalamud.Configuration;
using PartyPulse.Models;
using PartyPulse.Shoutrunner;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PartyPulse;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const string DefaultApiBaseUrl = "https://partypulse.fyi";

    private const string LegacyAzureApiBaseUrl = "https://partypulse.azurewebsites.net";

    public int Version { get; set; } = 8;

    public bool IsConfigWindowMovable { get; set; } = true;

    public bool AutoConnect { get; set; } = true;

    public bool NavigationCollapsed { get; set; }

    public List<string> MiniTabOrder { get; set; } = [];

    public List<string> HiddenMiniTabs { get; set; } = [];

    public int PartyFinderRefreshMinutes { get; set; } = 60;

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    public Guid SelectedVenueProfileId { get; set; } = Guid.Empty;

    public List<VenueConnectionConfiguration> VenueConnections { get; set; } = [];

    public List<ShoutrunnerProfileConfiguration> ShoutrunnerProfiles { get; set; } = [];

    public bool Normalize()
    {
        var changed = false;

        if (string.Equals(ApiBaseUrl, LegacyAzureApiBaseUrl, StringComparison.Ordinal))
        {
            ApiBaseUrl = DefaultApiBaseUrl;
            changed = true;
        }
        else
        {
            var normalizedApiBaseUrl = ApiBaseUrl?.Trim() ?? string.Empty;
            if (!string.Equals(ApiBaseUrl, normalizedApiBaseUrl, StringComparison.Ordinal))
            {
                ApiBaseUrl = normalizedApiBaseUrl;
                changed = true;
            }
        }

        var normalizedPartyFinderRefreshMinutes = Math.Clamp(PartyFinderRefreshMinutes, 1, 1440);
        if (PartyFinderRefreshMinutes != normalizedPartyFinderRefreshMinutes)
        {
            PartyFinderRefreshMinutes = normalizedPartyFinderRefreshMinutes;
            changed = true;
        }

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
            venue.DisplayTimeZoneId ??= string.Empty;
            if (!PartyPulse.Services.VenueTimeZone.IsValid(venue.DisplayTimeZoneId))
            {
                venue.DisplayTimeZoneId = TimeZoneInfo.Local.Id;
                changed = true;
            }
            venue.DeviceName ??= string.Empty;
            venue.RefreshToken ??= string.Empty;
        }

        if (ShoutrunnerProfiles is null)
        {
            ShoutrunnerProfiles = [];
            changed = true;
        }

        var normalizedMiniTabOrder = NormalizeLocalStringList(MiniTabOrder);
        if (MiniTabOrder is null || !MiniTabOrder.SequenceEqual(normalizedMiniTabOrder, StringComparer.Ordinal))
        {
            MiniTabOrder = normalizedMiniTabOrder;
            changed = true;
        }

        var normalizedHiddenMiniTabs = NormalizeLocalStringList(HiddenMiniTabs);
        if (HiddenMiniTabs is null || !HiddenMiniTabs.SequenceEqual(normalizedHiddenMiniTabs, StringComparer.Ordinal))
        {
            HiddenMiniTabs = normalizedHiddenMiniTabs;
            changed = true;
        }

        foreach (var profile in ShoutrunnerProfiles)
        {
            if (profile.SelectedWorldNames is null)
            {
                profile.SelectedWorldNames = [];
                changed = true;
            }
            if (profile.CompletedDestinationKeys is null)
            {
                profile.CompletedDestinationKeys = [];
                changed = true;
            }
            if (profile.PendingLogs is null)
            {
                profile.PendingLogs = [];
                changed = true;
            }

            foreach (var entry in profile.PendingLogs)
            {
                if (entry.ClientEntryId == Guid.Empty)
                {
                    entry.ClientEntryId = Guid.NewGuid();
                    changed = true;
                }
                var normalizedEventType = entry.EventType?.Trim() ?? string.Empty;
                if (!string.Equals(entry.EventType, normalizedEventType, StringComparison.Ordinal))
                {
                    entry.EventType = normalizedEventType;
                    changed = true;
                }

                var normalizedWorldName = NormalizeOptional(entry.WorldName);
                if (!string.Equals(entry.WorldName, normalizedWorldName, StringComparison.Ordinal))
                {
                    entry.WorldName = normalizedWorldName;
                    changed = true;
                }

                var normalizedDatacenterName = NormalizeOptional(entry.DatacenterName);
                if (!string.Equals(entry.DatacenterName, normalizedDatacenterName, StringComparison.Ordinal))
                {
                    entry.DatacenterName = normalizedDatacenterName;
                    changed = true;
                }

                var normalizedCityName = NormalizeOptional(entry.CityName);
                if (!string.Equals(entry.CityName, normalizedCityName, StringComparison.Ordinal))
                {
                    entry.CityName = normalizedCityName;
                    changed = true;
                }

                var normalizedReason = NormalizeOptional(entry.Reason);
                if (!string.Equals(entry.Reason, normalizedReason, StringComparison.Ordinal))
                {
                    entry.Reason = normalizedReason;
                    changed = true;
                }
            }

            var normalizedWorlds = profile.SelectedWorldNames
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!profile.SelectedWorldNames.SequenceEqual(normalizedWorlds, StringComparer.Ordinal))
            {
                profile.SelectedWorldNames = normalizedWorlds;
                changed = true;
            }

            var normalizedDestinations = profile.CompletedDestinationKeys
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!profile.CompletedDestinationKeys.SequenceEqual(normalizedDestinations, StringComparer.Ordinal))
            {
                profile.CompletedDestinationKeys = normalizedDestinations;
                changed = true;
            }
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

        if (Version < 8)
        {
            Version = 8;
            changed = true;
        }

        return changed;
    }

    public VenueConnectionConfiguration? GetSelectedVenue() =>
        VenueConnections.FirstOrDefault(x => x.ProfileId == SelectedVenueProfileId)
        ?? VenueConnections.FirstOrDefault();

    public ShoutrunnerProfileConfiguration GetShoutrunnerProfile(Guid venueProfileId)
    {
        var existing = ShoutrunnerProfiles.FirstOrDefault(value => value.VenueProfileId == venueProfileId);
        if (existing is not null)
            return existing;

        var created = new ShoutrunnerProfileConfiguration { VenueProfileId = venueProfileId };
        ShoutrunnerProfiles.Add(created);
        return created;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> NormalizeLocalStringList(IEnumerable<string>? values) =>
        values?
            .Select(static value => value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
