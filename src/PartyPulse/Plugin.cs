using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;
using PartyPulse.Windows;

namespace PartyPulse;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/pulse";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly PartyPulseApiClient apiClient;
    private readonly ConfigWindow configWindow;
    private readonly MainWindow mainWindow;

    private PlayerIdentity? observedIdentity;
    private bool autoConnectStarted;
    private bool disposed;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Normalize())
        {
            Configuration.Save();
        }

        IdentityProvider = new PlayerIdentityProvider(PlayerState);
        LocationProvider = new VenueLocationProvider(PlayerState, ClientState, DataManager);
        apiClient = new PartyPulseApiClient();
        VenueDirectory = new VenueDirectoryManager(Configuration, apiClient, Framework, Log);
        Authentication = new AuthenticationManager(Configuration, apiClient, Framework, Log);

        configWindow = new ConfigWindow(this);
        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Party Pulse. Use '/pulse config' for settings or '/pulse addvenue PULSE-XXXXXX' to add a venue.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        Log.Information("Party Pulse initialized.");
    }

    public Configuration Configuration { get; }

    public AuthenticationManager Authentication { get; }

    public VenueDirectoryManager VenueDirectory { get; }

    public VenueLocationProvider LocationProvider { get; }

    public PlayerIdentityProvider IdentityProvider { get; }

    public WindowSystem WindowSystem { get; } = new("PartyPulse");

    public CancellationToken LifetimeToken => lifetimeCancellation.Token;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetimeCancellation.Cancel();

        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        CommandManager.RemoveHandler(CommandName);

        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();
        mainWindow.Dispose();
        Authentication.Dispose();
        VenueDirectory.Dispose();
        apiClient.Dispose();
        lifetimeCancellation.Dispose();
    }

    public void ToggleConfigUi() => configWindow.Toggle();

    public void ToggleMainUi() => mainWindow.Toggle();

    public void AddVenueByCode(string venueCode)
    {
        Observe(
            AddVenueByCodeAndReportAsync(venueCode),
            $"add venue code {VenueConnectionConfiguration.NormalizeVenueCode(venueCode)}");
    }

    public void AddVenueAtCurrentLocation()
    {
        if (!LocationProvider.TryGetCurrentHousingAddress(out var address, out var reason))
        {
            ChatGui.PrintError(reason, "PartyPulse");
            return;
        }

        Observe(
            AddVenueByAddressAndReportAsync(address!),
            $"add venue at {address!.DisplayText}");
    }

    public void ConnectVenue(VenueConnectionConfiguration venue)
    {
        if (!IdentityProvider.TryGetCurrent(out var identity, out var reason))
        {
            Authentication.SetClientError(venue, reason);
            return;
        }

        Observe(
            Authentication.RefreshAsync(venue, identity!, Configuration.ApiBaseUrl, LifetimeToken),
            $"authenticate venue {venue.VenueCode}");
    }

    public void ConnectAllConfiguredVenues()
    {
        if (!IdentityProvider.TryGetCurrent(out var identity, out var reason))
        {
            foreach (var venue in Configuration.VenueConnections.Where(x => x.IsRegistered))
            {
                Authentication.SetClientError(venue, reason);
            }

            return;
        }

        var venues = Configuration.VenueConnections.ToArray();
        Observe(
            Authentication.ConnectConfiguredAsync(venues, identity!, Configuration.ApiBaseUrl, LifetimeToken),
            "authenticate configured venues");
    }

    public void RedeemInvite(VenueConnectionConfiguration venue, string inviteCode)
    {
        if (!IdentityProvider.TryGetCurrent(out var identity, out var reason))
        {
            Authentication.SetClientError(venue, reason);
            return;
        }

        Observe(
            Authentication.RedeemInviteAsync(
                venue,
                identity!,
                inviteCode,
                Configuration.ApiBaseUrl,
                LifetimeToken),
            $"redeem invite for venue {venue.VenueCode}");
    }

    public void RecoverVenue(VenueConnectionConfiguration venue, string recoveryCode)
    {
        if (!IdentityProvider.TryGetCurrent(out var identity, out var reason))
        {
            Authentication.SetClientError(venue, reason);
            return;
        }

        Observe(
            Authentication.RecoverAsync(
                venue,
                identity!,
                recoveryCode,
                Configuration.ApiBaseUrl,
                LifetimeToken),
            $"recover venue {venue.VenueCode}");
    }

    private void OnCommand(string command, string arguments)
    {
        var trimmed = arguments.Trim();
        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        const string addVenueCommand = "addvenue";
        if (trimmed.StartsWith(addVenueCommand, StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Length == addVenueCommand.Length || char.IsWhiteSpace(trimmed[addVenueCommand.Length])))
        {
            var code = trimmed.Length == addVenueCommand.Length
                ? string.Empty
                : trimmed[addVenueCommand.Length..].Trim();
            if (code.Length == 0)
            {
                ChatGui.PrintError("Usage: /pulse addvenue PULSE-XXXXXX", "PartyPulse");
                return;
            }

            AddVenueByCode(code);
            return;
        }

        ToggleMainUi();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!IdentityProvider.TryGetCurrent(out var identity, out _))
        {
            if (observedIdentity is not null)
            {
                observedIdentity = null;
                autoConnectStarted = false;
                Authentication.ClearAccessTokens("Character logged out or changed.");
            }

            return;
        }

        if (observedIdentity != identity)
        {
            observedIdentity = identity;
            autoConnectStarted = false;
            Authentication.ClearAccessTokens("Character changed; authentication must be renewed.");
        }

        if (!Configuration.AutoConnect)
        {
            autoConnectStarted = false;
            return;
        }

        if (autoConnectStarted || Configuration.VenueConnections.All(x => !x.IsRegistered))
        {
            return;
        }

        autoConnectStarted = true;
        ConnectAllConfiguredVenues();
    }

    private async Task AddVenueByCodeAndReportAsync(string venueCode)
    {
        var result = await VenueDirectory.AddByCodeAsync(
            venueCode,
            Configuration.ApiBaseUrl,
            LifetimeToken);
        ReportVenueLookup(result);
    }

    private async Task AddVenueByAddressAndReportAsync(VenueAddress address)
    {
        var result = await VenueDirectory.AddByAddressAsync(
            address,
            Configuration.ApiBaseUrl,
            LifetimeToken);
        ReportVenueLookup(result);
    }

    private static void ReportVenueLookup(ApiResult<VenueConnectionConfiguration> result)
    {
        if (result.Success && result.Value is not null)
        {
            ChatGui.Print(
                $"Added {result.Value.DisplayLabel} ({result.Value.VenueCode}) — {result.Value.AddressDisplay}.",
                "PartyPulse");
            return;
        }

        ChatGui.PrintError(result.Failure?.Message ?? "The venue could not be added.", "PartyPulse");
    }

    private void Observe(Task task, string operation)
    {
        _ = ObserveAsync(task, operation);
    }

    private async Task ObserveAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
        {
            // Expected during plugin unload.
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unhandled plugin task failure while attempting to {Operation}.", operation);
        }
    }
}
