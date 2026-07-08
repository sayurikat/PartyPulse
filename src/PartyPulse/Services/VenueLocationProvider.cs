using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using PartyPulse.Models;

namespace PartyPulse.Services;

public sealed class VenueLocationProvider(
    IPlayerState playerState,
    IClientState clientState,
    IDataManager dataManager)
{
    private static readonly string[] HousingDistrictNames =
    [
        "Mist",
        "The Lavender Beds",
        "The Goblet",
        "Shirogane",
        "Empyreum"
    ];

    private static readonly string[] HousingInteriorPrefixes =
    [
        "Cottage",
        "House",
        "Mansion",
        "Private ",
        "Company ",
        "Estate ",
        "Apartment",
        "Chambers"
    ];

    public unsafe bool TryGetCurrentHousingAddress(out VenueAddress? address, out string reason)
    {
        address = null;

        if (!TryGetCurrentWorldName(out var worldName, out reason))
            return false;

        var housingManager = HousingManager.Instance();
        if (housingManager == null)
        {
            reason = "Housing location data is not available.";
            return false;
        }

        var wardIndex = housingManager->GetCurrentWard();
        var plotIndex = housingManager->GetCurrentPlot();
        if (wardIndex < 0 || plotIndex < 0)
        {
            reason = "Stand on a residential plot before using the current housing address.";
            return false;
        }

        var territoryId = HousingManager.GetOriginalHouseTerritoryTypeId();
        if (territoryId == 0)
        {
            territoryId = clientState.TerritoryType;
        }

        var territory = dataManager.GetExcelSheet<TerritoryType>().GetRow(territoryId);
        var placeName = territory.PlaceName.Value.Name.ToString().Trim() ?? string.Empty;
        var cityName = NormalizeHousingDistrictName(placeName);
        if (cityName.Length == 0)
        {
            reason = "The current housing district name could not be determined.";
            return false;
        }

        address = new VenueAddress(
            worldName,
            cityName,
            wardIndex + 1,
            plotIndex + 1);
        reason = string.Empty;
        return true;
    }

    public bool TryGetCurrentLocation(out VenueLocation? location, out string reason)
    {
        location = null;

        if (!TryGetCurrentWorldName(out var worldName, out reason))
            return false;

        if (!TryGetCurrentLocationName(out var locationName, out reason))
            return false;

        location = new VenueLocation(worldName, NormalizeHousingDistrictName(locationName));
        reason = string.Empty;
        return true;
    }

    public bool IsAtAddress(
        string worldName,
        string cityName,
        int ward,
        int plot,
        out string message)
    {
        if (!TryGetCurrentHousingAddress(out var current, out var reason) || current is null)
        {
            message = reason;
            return false;
        }

        var matches =
            string.Equals(current.WorldName, worldName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CityName, cityName, StringComparison.OrdinalIgnoreCase) &&
            current.Ward == ward &&
            current.Plot == plot;
        message = matches
            ? string.Empty
            : $"Current: {current.DisplayText}\nRequired: {worldName}, {cityName}, Ward {ward}, Plot {plot}";
        return matches;
    }

    public bool IsAtLocation(
        string worldName,
        string locationName,
        out string message)
    {
        if (!TryGetCurrentLocation(out var current, out var reason) || current is null)
        {
            message = reason;
            return false;
        }

        var matches =
            string.Equals(current.WorldName, worldName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.LocationName, NormalizeHousingDistrictName(locationName), StringComparison.OrdinalIgnoreCase);
        message = matches
            ? string.Empty
            : $"Current: {current.DisplayText}\nRequired: {worldName}, {locationName}";
        return matches;
    }

    public bool IsAtOpeningLocation(
        string worldName,
        string cityName,
        int ward,
        int plot,
        string locationType,
        string? outdoorLocationName,
        out string message)
    {
        if (string.Equals(locationType, VenueOpeningLocationTypes.Outdoor, StringComparison.OrdinalIgnoreCase))
        {
            var requiredLocationName = outdoorLocationName?.Trim() ?? string.Empty;
            if (requiredLocationName.Length == 0)
            {
                message = "The active opening is missing its outdoor location name.";
                return false;
            }

            return IsAtLocation(worldName, requiredLocationName, out message);
        }

        return IsAtAddress(worldName, cityName, ward, plot, out message);
    }

    private bool TryGetCurrentWorldName(out string worldName, out string reason)
    {
        worldName = string.Empty;

        if (!playerState.IsLoaded || !clientState.IsLoggedIn)
        {
            reason = "Log into a character before using the current-location lookup.";
            return false;
        }

        if (!playerState.CurrentWorld.IsValid)
        {
            reason = "Dalamud has not loaded the current world yet.";
            return false;
        }

        worldName = playerState.CurrentWorld.Value.Name.ToString().Trim();
        if (worldName.Length == 0)
        {
            reason = "The current world name could not be determined.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryGetCurrentLocationName(out string locationName, out string reason)
    {
        var territory = dataManager.GetExcelSheet<TerritoryType>().GetRow(clientState.TerritoryType);
        var placeName = territory.PlaceName.Value.Name.ToString();
        var zoneName = territory.PlaceNameZone.Value.Name.ToString();
        var regionName = territory.PlaceNameRegion.Value.Name.ToString();
        locationName = ResolveTerritoryLocationName(placeName, zoneName, regionName);
        if (locationName.Length == 0)
        {
            reason = "The current location name could not be determined.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string ResolveTerritoryLocationName(
        string? placeName,
        string? zoneName,
        string? regionName)
    {
        var normalizedPlaceName = string.Empty;

        foreach (var candidate in new[] { placeName, zoneName, regionName })
        {
            var normalizedCandidate = NormalizeHousingDistrictName(candidate ?? string.Empty);
            if (IsHousingDistrictName(normalizedCandidate, out var canonicalDistrictName))
            {
                return canonicalDistrictName;
            }

            if (normalizedPlaceName.Length == 0)
            {
                normalizedPlaceName = normalizedCandidate;
            }
        }

        if (normalizedPlaceName.Length == 0 || !IsHousingInteriorName(normalizedPlaceName))
        {
            return normalizedPlaceName;
        }

        foreach (var candidate in new[] { zoneName, regionName })
        {
            var normalizedCandidate = NormalizeHousingDistrictName(candidate ?? string.Empty);
            if (IsHousingDistrictName(normalizedCandidate, out string canonicalDistrictName))
            {
                return canonicalDistrictName;
            }
        }

        return normalizedPlaceName;
    }

    private static string NormalizeHousingDistrictName(string placeName)
    {
        var normalized = placeName.Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        // Housing estate territories are named like "Private Mansion - Mist".
        // The public venue API and dbo.cities store only the district name.
        var separatorIndex = normalized.LastIndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            var districtName = normalized[(separatorIndex + 3)..].Trim();
            if (districtName.Length > 0)
            {
                return districtName;
            }
        }

        return normalized;
    }

    private static bool IsHousingInteriorName(string? placeName)
    {
        var normalized = placeName?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (var prefix in HousingInteriorPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHousingDistrictName(string locationName, out string canonicalName)
    {
        foreach (var districtName in HousingDistrictNames)
        {
            if (string.Equals(locationName.Trim(), districtName, StringComparison.OrdinalIgnoreCase))
            {
                canonicalName = districtName;
                return true;
            }
        }

        canonicalName = string.Empty;
        return false;
    }
}
