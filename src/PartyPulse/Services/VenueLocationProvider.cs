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
    public unsafe bool TryGetCurrentHousingAddress(out VenueAddress? address, out string reason)
    {
        address = null;

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
            reason = "Stand on a residential plot before adding a venue by location.";
            return false;
        }

        var territoryId = HousingManager.GetOriginalHouseTerritoryTypeId();
        if (territoryId == 0)
        {
            territoryId = clientState.TerritoryType;
        }

        var territory = dataManager.GetExcelSheet<TerritoryType>().GetRow(territoryId);
        var cityName = territory.PlaceName.Value.Name.ToString().Trim() ?? string.Empty;
        if (cityName.Length == 0)
        {
            reason = "The current housing district name could not be determined.";
            return false;
        }

        var worldName = playerState.CurrentWorld.Value.Name.ToString().Trim();
        if (worldName.Length == 0)
        {
            reason = "The current world name could not be determined.";
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
}
