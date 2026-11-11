using System;
using System.Globalization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using PartyPulse.Models;

using HousingAddress = (string Name, int World, int City, int Ward, int PropertyType, int Plot,
    int Apartment, bool ApartmentSubdivision, bool AliasEnabled, string Alias);

namespace PartyPulse.Integrations.Lifestream;

public sealed class LifestreamApi : ExternalPluginIpcClient
{
    private const string TravelOperation = "request venue travel";

    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, string, string, string, bool, bool, HousingAddress> buildAddress;
    private readonly ICallGateSubscriber<HousingAddress, object> goToHousingAddress;
    private readonly ICallGateSubscriber<string, bool> changeWorld;

    public LifestreamApi(IDalamudPluginInterface pluginInterface, IPluginLog log)
        : base(pluginInterface, log, "Lifestream", "Lifestream")
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        buildAddress = pluginInterface.GetIpcSubscriber<string, string, string, string, bool, bool, HousingAddress>(
            "Lifestream.BuildAddressBookEntry");
        goToHousingAddress = pluginInterface.GetIpcSubscriber<HousingAddress, object>("Lifestream.GoToHousingAddress");
        changeWorld = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
    }

    public PluginIntegrationResult TravelToHousingAddress(string worldName, string cityName, int ward, int plot)
    {
        if (string.IsNullOrWhiteSpace(worldName) || string.IsNullOrWhiteSpace(cityName) ||
            ward is < 1 or > 30 || plot is < 1 or > 60)
        {
            return Failed(PluginIntegrationFailureKind.InvalidRequest, "INVALID_HOUSING_ADDRESS",
                "The opening needs a world, housing district, ward and plot for Lifestream travel.", TravelOperation);
        }

        return RequestTravel(() =>
        {
            var address = BuildHousingAddress(worldName, cityName, ward, plot);
            goToHousingAddress.InvokeAction(address);
            return PluginIntegrationResult.Succeeded();
        });
    }

    public PluginIntegrationResult TravelToWorld(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return Failed(PluginIntegrationFailureKind.InvalidRequest, "INVALID_WORLD",
                "The opening needs a destination world for Lifestream travel.", TravelOperation);
        }

        return RequestTravel(() => changeWorld.InvokeFunc(worldName.Trim())
            ? PluginIntegrationResult.Succeeded()
            : Failed(PluginIntegrationFailureKind.OperationFailed, "WORLD_TRAVEL_UNAVAILABLE",
                "Lifestream could not request travel to the opening's world.", TravelOperation));
    }

    private PluginIntegrationResult RequestTravel(Func<PluginIntegrationResult> request)
    {
        var result = ExecutePluginCall(TravelOperation, () => isBusy.InvokeFunc()
            ? Failed(PluginIntegrationFailureKind.Busy, "PLUGIN_BUSY",
                "Lifestream is already handling another task.", TravelOperation)
            : request());

        return result.Success ? result.Value! : PluginIntegrationResult.Failed(result.Failure!);
    }

    private HousingAddress BuildHousingAddress(string worldName, string cityName, int ward, int plot) =>
        // Lifestream uses the plot number for subdivisions. The final flags only apply to apartments.
        buildAddress.InvokeFunc(worldName.Trim(), cityName.Trim(), ward.ToString(CultureInfo.InvariantCulture),
            plot.ToString(CultureInfo.InvariantCulture), false, false);

#if PARTYPULSE_RUNTIME_STATUS
    internal object ReadRuntimeStatus(VenueAddress? currentAddress)
    {
        var busy = ExecutePluginCall("check travel status", isBusy.InvokeFunc);
        var address = currentAddress is null ? null : ExecutePluginCall("validate housing address", () =>
        {
            var entry = BuildHousingAddress(currentAddress.WorldName, currentAddress.CityName,
                currentAddress.Ward, currentAddress.Plot);
            return entry.World > 0 && entry.Ward == currentAddress.Ward && entry.Plot == currentAddress.Plot;
        });
        return new
        {
            Busy = busy.Success ? (bool?)busy.Value : null,
            StatusError = busy.Failure?.Code,
            HousingTravelRegistered = goToHousingAddress.HasAction,
            WorldTravelRegistered = changeWorld.HasFunction,
            HousingAddressAccepted = address?.Success == true ? (bool?)address.Value : null,
            AddressError = address?.Failure?.Code,
        };
    }
#endif
}
