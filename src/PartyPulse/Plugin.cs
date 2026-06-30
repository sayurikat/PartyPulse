using System;
using System.Collections.Generic;
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
using PartyPulse.Bar;
using PartyPulse.Court;
using PartyPulse.Finance;
using PartyPulse.Greeter;
using PartyPulse.Integrations;
using PartyPulse.Integrations.Dropbox;
using PartyPulse.Notifications;
using PartyPulse.OpeningPublications;
using PartyPulse.PartyFinder;
using PartyPulse.Photoshoots;
using PartyPulse.OtherSales;
using PartyPulse.OtherGames;
using PartyPulse.Purchases;
using PartyPulse.Models;
using PartyPulse.SelfService;
using PartyPulse.Staff;
using PartyPulse.Shoutrunner;
using PartyPulse.Services;
using PartyPulse.VenueUsers;
using PartyPulse.Vip;
using PartyPulse.VenueOpenings;
using PartyPulse.TimedMacros;
using PartyPulse.Djs;
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
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly PartyPulseApiClient apiClient;
    private readonly ConfigWindow configWindow;
    private readonly MainWindow mainWindow;
    private readonly VenueUserEditWindow venueUserEditWindow;
    private readonly VipPlayerEditWindow vipPlayerEditWindow;
    private readonly VipArrivalWindow vipArrivalWindow;
    private readonly NotificationToastWindow notificationToastWindow;
    private readonly SettlementTradeService settlementTradeService;
    private readonly GameMacroExecutionService gameMacroExecutionService;

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
        TargetProvider = new TargetPlayerProvider(TargetManager);
        apiClient = new PartyPulseApiClient();
        VenueDirectory = new VenueDirectoryManager(Configuration, apiClient, Framework, Log);
        Authentication = new AuthenticationManager(Configuration, apiClient, Framework, Log);
        UserManagement = new VenueUserManagementManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
        SelfService = new SelfServiceManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
        VipPerks = new VipPerkManagementManager(Configuration, Authentication, apiClient, IdentityProvider);
        Photoshoots = new PhotoshootManagementManager(Configuration, Authentication, apiClient, IdentityProvider);
        OtherSales = new OtherSalesManagementManager(Configuration, Authentication, apiClient, IdentityProvider);
        OtherGames = new OtherGamesManagementManager(Configuration, Authentication, apiClient, IdentityProvider);
        Purchases = new PurchaseManagementManager(Configuration, Authentication, apiClient, IdentityProvider);
        Court = new CourtManagementManager(Configuration, Authentication, apiClient, IdentityProvider);
        Staff = new StaffManagementManager(Configuration, Authentication, apiClient, IdentityProvider);
        Bar = new BarManagementManager(Configuration, Authentication, apiClient, IdentityProvider);
        Vip = new VipManagementManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
        VipArrivals = new VipArrivalManagementManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
        Greeter = new GreeterManagementManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
        VenueOpenings = new VenueOpeningScheduleManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
        TimedMacros = new TimedMacroManagementManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
        Djs = new DjManagementManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
        OpeningPublications = new OpeningPublicationManagementManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
        PartyFinderAutomation = new PartyFinderAutomationService(
            Configuration,
            OpeningPublications,
            Condition,
            GameGui,
            PlayerState,
            Log);
        NearbyVipPlayers = new NearbyVipPlayerTracker(ObjectTable, TargetManager);
        VipArrivalNearby = new VipArrivalNearbyTracker(ObjectTable);
        GreeterNearby = new GreeterNearbyTracker(ObjectTable);
        gameMacroExecutionService = new GameMacroExecutionService(
            Framework,
            ObjectTable,
            TargetManager,
            Log);
        ShoutrunnerDuty = new ShoutrunnerDutyManager(
            Configuration,
            CommandManager,
            PlayerState,
            ClientState,
            DataManager,
            gameMacroExecutionService);
        Finance = new FinanceManagementManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider);
        Notifications = new NotificationPollingManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider);
        var dropboxApi = new DropboxApi(PluginInterface, Log);
        settlementTradeService = new SettlementTradeService(
            dropboxApi,
            Framework,
            CommandManager,
            TargetManager,
            Log);

        configWindow = new ConfigWindow(this);
        mainWindow = new MainWindow(this);
        venueUserEditWindow = new VenueUserEditWindow(this);
        vipPlayerEditWindow = new VipPlayerEditWindow(this);
        vipArrivalWindow = new VipArrivalWindow(this);
        notificationToastWindow = new NotificationToastWindow(this);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(venueUserEditWindow);
        WindowSystem.AddWindow(vipPlayerEditWindow);
        WindowSystem.AddWindow(vipArrivalWindow);
        WindowSystem.AddWindow(notificationToastWindow);

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

    public TargetPlayerProvider TargetProvider { get; }

    public VenueUserManagementManager UserManagement { get; }

    public SelfServiceManager SelfService { get; }

    public VipManagementManager Vip { get; }

    public VipPerkManagementManager VipPerks { get; }

    public PhotoshootManagementManager Photoshoots { get; }

    public OtherSalesManagementManager OtherSales { get; }

    public OtherGamesManagementManager OtherGames { get; }

    public PurchaseManagementManager Purchases { get; }

    public CourtManagementManager Court { get; }

    public StaffManagementManager Staff { get; }
    public BarManagementManager Bar { get; }

    public VipArrivalManagementManager VipArrivals { get; }

    public GreeterManagementManager Greeter { get; }

    public VenueOpeningScheduleManager VenueOpenings { get; }

    public TimedMacroManagementManager TimedMacros { get; }

    public DjManagementManager Djs { get; }

    public OpeningPublicationManagementManager OpeningPublications { get; }

    public PartyFinderAutomationService PartyFinderAutomation { get; }

    public ShoutrunnerDutyManager ShoutrunnerDuty { get; }

    public NearbyVipPlayerTracker NearbyVipPlayers { get; }

    public VipArrivalNearbyTracker VipArrivalNearby { get; }

    public GreeterNearbyTracker GreeterNearby { get; }

    public FinanceManagementManager Finance { get; }

    public NotificationPollingManager Notifications { get; }

    public WindowSystem WindowSystem { get; } = new("PartyPulse");

    public CancellationToken LifetimeToken => lifetimeCancellation.Token;

    public bool IsGameMacroBusy => gameMacroExecutionService.IsBusy;

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
        venueUserEditWindow.Dispose();
        vipPlayerEditWindow.Dispose();
        vipArrivalWindow.Dispose();
        notificationToastWindow.Dispose();
        Notifications.Dispose();
        Finance.Dispose();
        Photoshoots.Dispose();
        OtherSales.Dispose();
        OtherGames.Dispose();
        Purchases.Dispose();
        Court.Dispose();
        Staff.Dispose();
        Bar.Dispose();
        VipPerks.Dispose();
        PartyFinderAutomation.Stop("PartyPulse is unloading.");
        OpeningPublications.Dispose();
        Djs.Dispose();
        TimedMacros.Dispose();
        VenueOpenings.Dispose();
        Greeter.Dispose();
        VipArrivals.Dispose();
        Vip.Dispose();
        gameMacroExecutionService.Dispose();
        SelfService.Dispose();
        UserManagement.Dispose();
        Authentication.Dispose();
        VenueDirectory.Dispose();
        apiClient.Dispose();
        lifetimeCancellation.Dispose();
    }

    public void ToggleConfigUi() => configWindow.Toggle();

    public void ToggleMainUi() => mainWindow.Toggle();

    public void AddVenueByCode(string venueCode) =>
        Observe(
            AddVenueByCodeAndReportAsync(venueCode),
            $"add venue code {VenueConnectionConfiguration.NormalizeVenueCode(venueCode)}");

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
        if (!TryGetCurrentIdentity(venue, out var identity))
        {
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
        if (!TryGetCurrentIdentity(venue, out var identity))
        {
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
        if (!TryGetCurrentIdentity(venue, out var identity))
        {
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

    public void RedeemDevicePairingCode(VenueConnectionConfiguration venue, string pairingCode)
    {
        if (!TryGetCurrentIdentity(venue, out var identity))
        {
            return;
        }

        Observe(
            RedeemDevicePairingCodeAndReportAsync(venue, identity!, pairingCode),
            $"pair device for venue {venue.VenueCode}");
    }

    public void LinkCurrentCharacter(VenueConnectionConfiguration venue)
    {
        if (!TryGetCurrentIdentity(venue, out var identity))
        {
            return;
        }

        Observe(
            LinkCurrentCharacterAndReportAsync(venue, identity!),
            $"link current character for venue {venue.VenueCode}");
    }

    public void EnsureSelfServiceLoaded(VenueConnectionConfiguration venue)
    {
        if (!SelfService.ShouldLoad(venue))
        {
            return;
        }

        Observe(
            SelfService.LoadAsync(venue, false, LifetimeToken),
            $"load self-service data for {venue.VenueCode}");
    }

    public void RefreshSelfService(VenueConnectionConfiguration venue) =>
        Observe(
            SelfService.LoadAsync(venue, true, LifetimeToken),
            $"refresh self-service data for {venue.VenueCode}");

    public void UnlinkCharacter(VenueConnectionConfiguration venue, int characterId) =>
        Observe(
            UnlinkCharacterAndReportAsync(venue, characterId),
            $"unlink character {characterId} from {venue.VenueCode}");

    public void CreateDevicePairingCode(VenueConnectionConfiguration venue) =>
        Observe(
            CreateDevicePairingCodeAndReportAsync(venue),
            $"create device pairing code for {venue.VenueCode}");

    public void UnauthorizeFromVenue(VenueConnectionConfiguration venue) =>
        Observe(
            UnauthorizeFromVenueAndReportAsync(venue),
            $"unauthorize from venue {venue.VenueCode}");

    public void RemoveVenueLocally(VenueConnectionConfiguration venue)
    {
        Authentication.RemoveProfile(venue.ProfileId);
        UserManagement.RemoveProfile(venue.ProfileId);
        SelfService.RemoveProfile(venue.ProfileId);
        Vip.RemoveProfile(venue.ProfileId);
        VipPerks.RemoveProfile(venue.ProfileId);
        Photoshoots.RemoveProfile(venue.ProfileId);
        OtherSales.RemoveProfile(venue.ProfileId);
        OtherGames.RemoveProfile(venue.ProfileId);
        Purchases.RemoveProfile(venue.ProfileId);
        Court.RemoveProfile(venue.ProfileId);
        Staff.RemoveProfile(venue.ProfileId);
        Bar.RemoveProfile(venue.ProfileId);
        VipArrivals.ClearProfile(venue.ProfileId);
        Greeter.ClearProfile(venue.ProfileId);
        VenueOpenings.RemoveProfile(venue.ProfileId);
        TimedMacros.RemoveProfile(venue.ProfileId);
        Djs.RemoveProfile(venue.ProfileId);
        OpeningPublications.RemoveProfile(venue.ProfileId);
        if (PartyFinderAutomation.ProfileId == venue.ProfileId)
            PartyFinderAutomation.Stop("Party Finder refresher stopped because venue authorization changed.");
        NearbyVipPlayers.ClearProfile(venue.ProfileId);
        VipArrivalNearby.Clear();
        GreeterNearby.Clear();
        Finance.RemoveProfile(venue.ProfileId);
        Notifications.RemoveProfile(venue.ProfileId);
        Configuration.VenueConnections.RemoveAll(x => x.ProfileId == venue.ProfileId);
        Configuration.ShoutrunnerProfiles.RemoveAll(x => x.VenueProfileId == venue.ProfileId);
        Configuration.Normalize();
        Configuration.Save();
        ChatGui.Print($"Removed {venue.DisplayLabel} from this plugin. Server-side membership was not changed.", "PartyPulse");
    }

    public void EnsureVenueUsersLoaded(VenueConnectionConfiguration venue)
    {
        if (!UserManagement.ShouldLoad(venue))
        {
            return;
        }

        Observe(
            UserManagement.LoadAsync(venue, false, LifetimeToken),
            $"load venue users for {venue.VenueCode}");
    }

    public void RefreshVenueUsers(VenueConnectionConfiguration venue) =>
        Observe(
            UserManagement.LoadAsync(venue, true, LifetimeToken),
            $"refresh venue users for {venue.VenueCode}");

    public void CreateVenueUser(
        VenueConnectionConfiguration venue,
        string displayName,
        string? discordHandle) =>
        Observe(
            CreateVenueUserAndReportAsync(venue, displayName, discordHandle),
            $"create venue user for {venue.VenueCode}");

    public void UpdateVenueUserProfile(
        VenueConnectionConfiguration venue,
        int userId,
        string displayName,
        string? discordHandle) =>
        Observe(
            UpdateVenueUserProfileAndReportAsync(venue, userId, displayName, discordHandle),
            $"update venue user {userId} for {venue.VenueCode}");

    public void UpdateVenueUserPermissions(
        VenueConnectionConfiguration venue,
        int userId,
        string[] permissionKeys) =>
        Observe(
            UpdateVenueUserPermissionsAndReportAsync(venue, userId, permissionKeys),
            $"update permissions for venue user {userId} at {venue.VenueCode}");

    public void CreateVenueUserRecoveryCode(
        VenueConnectionConfiguration venue,
        VenueUserSummary user) =>
        Observe(
            CreateVenueUserRecoveryCodeAndReportAsync(venue, user),
            $"create recovery code for venue user {user.UserId} at {venue.VenueCode}");

    public void RestoreVenueUser(
        VenueConnectionConfiguration venue,
        VenueUserSummary user) =>
        Observe(
            RestoreVenueUserAndReportAsync(venue, user),
            $"restore venue user {user.UserId} at {venue.VenueCode}");

    public void EnsureVipLoaded(VenueConnectionConfiguration venue)
    {
        if (!Vip.ShouldLoad(venue))
        {
            return;
        }

        Observe(
            Vip.LoadAsync(venue, false, LifetimeToken),
            $"load VIP data for {venue.VenueCode}");
    }

    public void RefreshVip(VenueConnectionConfiguration venue)
    {
        NearbyVipPlayers.ClearProfile(venue.ProfileId);
        VipArrivalNearby.Clear();
        Observe(
            Vip.LoadAsync(venue, true, LifetimeToken),
            $"refresh VIP data for {venue.VenueCode}");
    }

    public void CreateVipPackage(
        VenueConnectionConfiguration venue,
        CreateVipPackageRequest request) =>
        Observe(
            CreateVipPackageAndReportAsync(venue, request),
            $"create VIP package for {venue.VenueCode}");

    public void UpdateVipPackage(
        VenueConnectionConfiguration venue,
        int packageId,
        UpdateVipPackageRequest request) =>
        Observe(
            UpdateVipPackageAndReportAsync(venue, packageId, request),
            $"update VIP package {packageId} for {venue.VenueCode}");

    public void SellVipSubscription(
        VenueConnectionConfiguration venue,
        SellVipSubscriptionRequest request) =>
        Observe(
            SellVipSubscriptionAndReportAsync(venue, request),
            $"sell VIP subscription for {venue.VenueCode}");

    public void LinkVipCharacter(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        LinkVipCharacterRequest request) =>
        Observe(
            LinkVipCharacterAndReportAsync(venue, vipPlayerId, request),
            $"link VIP character for {venue.VenueCode}");

    public void SetVipPreferredCharacter(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        int characterId) =>
        Observe(
            SetVipPreferredCharacterAndReportAsync(venue, vipPlayerId, characterId),
            $"set preferred VIP character for {venue.VenueCode}");

    public void UpdateVipPlayer(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        UpdateVipPlayerRequest request) =>
        Observe(
            UpdateVipPlayerAndReportAsync(venue, vipPlayerId, request),
            $"update VIP player {vipPlayerId} for {venue.VenueCode}");

    public void UnlinkVipCharacter(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        int characterId) =>
        Observe(
            UnlinkVipCharacterAndReportAsync(venue, vipPlayerId, characterId),
            $"unlink VIP character {characterId} for {venue.VenueCode}");

    public void CancelVipSubscription(
        VenueConnectionConfiguration venue,
        long subscriptionId,
        CancelVipSubscriptionRequest request) =>
        Observe(
            CancelVipSubscriptionAndReportAsync(venue, subscriptionId, request),
            $"cancel VIP subscription {subscriptionId} for {venue.VenueCode}");

    public void SetVipSubscriptionPaymentStatus(
        VenueConnectionConfiguration venue,
        long subscriptionId,
        SetVipSubscriptionPaymentStatusRequest request) =>
        Observe(
            SetVipSubscriptionPaymentStatusAndReportAsync(venue, subscriptionId, request),
            $"set VIP payment status {subscriptionId} for {venue.VenueCode}");

    public void EnsureVipArrivalsLoaded(VenueConnectionConfiguration venue)
    {
        if (!VipArrivals.ShouldLoad(venue))
        {
            return;
        }

        Observe(
            VipArrivals.LoadAsync(venue, false, LifetimeToken),
            $"load VIP arrival data for {venue.VenueCode}");
    }

    public void RefreshVipArrivals(VenueConnectionConfiguration venue)
    {
        VipArrivalNearby.Clear();
        Observe(
            VipArrivals.LoadAsync(venue, true, LifetimeToken),
            $"refresh VIP arrival data for {venue.VenueCode}");
    }

    public void OpenVipArrivalTracker(VenueConnectionConfiguration venue)
    {
        EnsureVipArrivalsLoaded(venue);
        vipArrivalWindow.Open(venue.ProfileId);
    }

    public void SubmitVipArrivalObservations(
        VenueConnectionConfiguration venue,
        long openingId,
        IReadOnlyList<VipArrivalObservationRequest> observations) =>
        Observe(
            SubmitVipArrivalObservationsAsync(venue, openingId, observations),
            $"submit VIP arrival observations for {venue.VenueCode}");

    public void RunVipArrivalMacro(
        VenueConnectionConfiguration venue,
        VipArrivalSummary arrival,
        NearbyVipArrivalCharacter nearby,
        VenueMacroSummary macro,
        string actionKey) =>
        Observe(
            RunVipArrivalMacroAndReportAsync(venue, arrival, nearby, macro, actionKey),
            $"run VIP arrival {actionKey} macro for {venue.VenueCode}");

    public void DismissVipArrival(
        VenueConnectionConfiguration venue,
        VipArrivalSummary arrival) =>
        Observe(
            RecordVipArrivalActionAndReportAsync(
                venue,
                arrival.VipPlayerId,
                new RecordVipArrivalActionRequest(arrival.OpeningId, "dismiss", arrival.LastSeenCharacterId),
                "VIP arrival dismissed."),
            $"dismiss VIP arrival for {venue.VenueCode}");

    public void EnsureGreeterLoaded(VenueConnectionConfiguration venue)
    {
        if (!Greeter.ShouldLoad(venue))
            return;

        Observe(
            Greeter.LoadAsync(venue, false, LifetimeToken),
            $"load greeter data for {venue.VenueCode}");
    }

    public void RefreshGreeter(VenueConnectionConfiguration venue)
    {
        GreeterNearby.Clear();
        Observe(
            Greeter.LoadAsync(venue, true, LifetimeToken),
            $"refresh greeter data for {venue.VenueCode}");
    }

    public void SubmitGreeterObservations(
        VenueConnectionConfiguration venue,
        long openingId,
        IReadOnlyList<GreeterObservationRequest> observations) =>
        Observe(
            SubmitGreeterObservationsAsync(venue, openingId, observations),
            $"submit greeter observations for {venue.VenueCode}");

    public void RunGreeterMacro(
        VenueConnectionConfiguration venue,
        GreeterArrivalSummary arrival,
        NearbyGreeterPlayer nearby,
        GreeterMacroSummary macro,
        GreeterCurrentDjSummary? currentDj) =>
        Observe(
            RunGreeterMacroAndReportAsync(venue, arrival, nearby, macro, currentDj),
            $"run greeter macro for {venue.VenueCode}");

    public void DismissGreeterArrival(
        VenueConnectionConfiguration venue,
        GreeterArrivalSummary arrival) =>
        Observe(
            RecordGreeterActionAndReportAsync(
                venue,
                new RecordGreeterActionRequest(
                    arrival.OpeningId,
                    arrival.CharacterName,
                    arrival.WorldName,
                    "dismiss"),
                "Arrival dismissed."),
            $"dismiss greeter arrival for {venue.VenueCode}");

    public void UpdateGreeterMacro(
        VenueConnectionConfiguration venue,
        string macroCode,
        string? macroText) =>
        Observe(
            UpdateGreeterMacroAndReportAsync(venue, macroCode, macroText),
            $"update greeter macro {macroCode} for {venue.VenueCode}");

    public void UpdateVenueMacro(
        VenueConnectionConfiguration venue,
        string macroCode,
        string? macroText) =>
        Observe(
            UpdateVenueMacroAndReportAsync(venue, macroCode, macroText),
            $"update venue macro {macroCode} for {venue.VenueCode}");

    public void StartTemporaryVenueOpening(
        VenueConnectionConfiguration venue,
        int durationMinutes,
        string? title) =>
        Observe(
            StartTemporaryVenueOpeningAndReportAsync(venue, durationMinutes, title),
            $"start temporary opening for {venue.VenueCode}");

    public void CloseVenueOpening(
        VenueConnectionConfiguration venue,
        long openingId) =>
        Observe(
            CloseVenueOpeningAndReportAsync(venue, openingId),
            $"close opening {openingId} for {venue.VenueCode}");

    public void EnsureVenueOpeningsLoaded(VenueConnectionConfiguration venue)
    {
        if (!VenueOpenings.ShouldLoad(venue))
            return;

        Observe(
            VenueOpenings.LoadAsync(venue, false, LifetimeToken),
            $"load venue openings for {venue.VenueCode}");
    }

    public void RefreshVenueOpenings(VenueConnectionConfiguration venue) =>
        Observe(
            VenueOpenings.LoadAsync(venue, true, LifetimeToken),
            $"refresh venue openings for {venue.VenueCode}");

    public void RefreshVenueOpeningHistory(VenueConnectionConfiguration venue) =>
        Observe(
            VenueOpenings.LoadHistoryAsync(venue, false, LifetimeToken),
            $"refresh venue opening history for {venue.VenueCode}");

    public void LoadMoreVenueOpeningHistory(VenueConnectionConfiguration venue) =>
        Observe(
            VenueOpenings.LoadHistoryAsync(venue, true, LifetimeToken),
            $"load more venue opening history for {venue.VenueCode}");

    public void SaveVenueOpening(
        VenueConnectionConfiguration venue,
        long? openingId,
        SaveVenueOpeningRequest request) =>
        Observe(
            SaveVenueOpeningAndReportAsync(venue, openingId, request),
            $"save venue opening for {venue.VenueCode}");

    public void CancelVenueOpening(
        VenueConnectionConfiguration venue,
        long openingId) =>
        Observe(
            CancelVenueOpeningAndReportAsync(venue, openingId),
            $"cancel opening {openingId} for {venue.VenueCode}");

    public void CloseScheduledVenueOpening(
        VenueConnectionConfiguration venue,
        long openingId) =>
        Observe(
            CloseScheduledVenueOpeningAndReportAsync(venue, openingId),
            $"close scheduled opening {openingId} for {venue.VenueCode}");

    public void RunNewVipMacro(
        VenueConnectionConfiguration venue,
        VipNewMemberOffer offer,
        VenueMacroSummary macro) =>
        Observe(
            RunNewVipMacroAndReportAsync(venue, offer, macro),
            $"run new VIP macro for {venue.VenueCode}");

    public void DismissNewVipOffer(Guid profileId) =>
        VipArrivals.ClearNewMemberOffer(profileId);

    public void EnsureDjsLoaded(VenueConnectionConfiguration venue)
    {
        if (!Djs.ShouldLoad(venue))
            return;

        Observe(
            Djs.LoadAsync(venue, false, LifetimeToken),
            $"load DJs for {venue.VenueCode}");
    }

    public void RefreshDjs(VenueConnectionConfiguration venue) =>
        Observe(
            Djs.LoadAsync(venue, true, LifetimeToken),
            $"refresh DJs for {venue.VenueCode}");

    public void SaveDj(
        VenueConnectionConfiguration venue,
        long? djId,
        SaveDjRequest request) =>
        Observe(
            SaveDjAndReportAsync(venue, djId, request),
            $"save DJ for {venue.VenueCode}");

    public void ArchiveDj(VenueConnectionConfiguration venue, long djId) =>
        Observe(
            ArchiveDjAndReportAsync(venue, djId),
            $"archive DJ {djId} for {venue.VenueCode}");

    public void SaveDjBooking(
        VenueConnectionConfiguration venue,
        long? bookingId,
        SaveDjBookingRequest request) =>
        Observe(
            SaveDjBookingAndReportAsync(venue, bookingId, request),
            $"save DJ booking for {venue.VenueCode}");

    public void DeleteDjBooking(
        VenueConnectionConfiguration venue,
        long openingId,
        long bookingId) =>
        Observe(
            DeleteDjBookingAndReportAsync(venue, openingId, bookingId),
            $"delete DJ booking {bookingId} for {venue.VenueCode}");

    public void EnsureOpeningPublicationsLoaded(VenueConnectionConfiguration venue)
    {
        if (!OpeningPublications.ShouldLoad(venue))
            return;

        Observe(
            OpeningPublications.LoadAsync(venue, false, LifetimeToken),
            $"load opening publications for {venue.VenueCode}");
    }

    public void RefreshOpeningPublications(VenueConnectionConfiguration venue) =>
        Observe(
            OpeningPublications.LoadAsync(venue, true, LifetimeToken),
            $"refresh opening publications for {venue.VenueCode}");

    public void SaveOpeningPublicationTemplate(
        VenueConnectionConfiguration venue,
        string publicationCode,
        string? templateText) =>
        Observe(
            SaveOpeningPublicationTemplateAndReportAsync(venue, publicationCode, templateText),
            $"save opening publication template {publicationCode} for {venue.VenueCode}");

    public void GenerateOpeningPublications(
        VenueConnectionConfiguration venue,
        OpeningPublicationOpeningSummary opening,
        string channelCode) =>
        Observe(
            GenerateOpeningPublicationsAndReportAsync(venue, opening, channelCode),
            $"generate {channelCode} publications for opening {opening.OpeningId}");

    public void SaveOpeningPublicationText(
        VenueConnectionConfiguration venue,
        long openingId,
        string publicationCode,
        string? publicationText) =>
        Observe(
            SaveOpeningPublicationTextAndReportAsync(venue, openingId, publicationCode, publicationText),
            $"save publication {publicationCode} for opening {openingId}");

    public void TravelShoutrunner(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication)
    {
        var result = ShoutrunnerDuty.TravelNext(venue, context, publication);
        if (result.Success)
            ChatGui.Print(result.Message, "PartyPulse");
        else
            ChatGui.PrintError(result.Message, "PartyPulse");
    }

    public void RunShoutrunnerShout(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication) =>
        Observe(
            RunShoutrunnerShoutAndReportAsync(venue, context, publication),
            $"run Shoutrunner macro for opening {publication.OpeningId}");

    public void ResetShoutrunner(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication,
        string reason)
    {
        var result = ShoutrunnerDuty.Reset(venue, context, publication, reason);
        if (result.Success)
            ChatGui.Print(result.Message, "PartyPulse");
        else
            ChatGui.PrintError(result.Message, "PartyPulse");
    }

    public void CompleteShoutrunnerRound(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication)
    {
        var result = ShoutrunnerDuty.CompleteRound(venue, context, publication);
        if (result.Success)
            ChatGui.Print(result.Message, "PartyPulse");
        else
            ChatGui.PrintError(result.Message, "PartyPulse");
    }

    public void ReturnShoutrunnerToVenue(
        VenueConnectionConfiguration venue,
        OpeningPublicationOpeningSummary? opening)
    {
        var result = ShoutrunnerDuty.ReturnToVenue(venue, opening);
        if (result.Success)
            ChatGui.Print(result.Message, "PartyPulse");
        else
            ChatGui.PrintError(result.Message, "PartyPulse");
    }

    public void ReportShoutrunnerDuty(VenueConnectionConfiguration venue) =>
        Observe(
            ReportShoutrunnerDutyAndReportAsync(venue),
            $"report Shoutrunner duty for {venue.VenueCode}");

    public void EnsureTimedMacrosLoaded(VenueConnectionConfiguration venue)
    {
        if (!TimedMacros.ShouldLoad(venue))
            return;

        Observe(
            TimedMacros.LoadAsync(venue, false, LifetimeToken),
            $"load timed macros for {venue.VenueCode}");
    }

    public void RefreshTimedMacros(VenueConnectionConfiguration venue) =>
        Observe(
            TimedMacros.LoadAsync(venue, true, LifetimeToken),
            $"refresh timed macros for {venue.VenueCode}");

    public void CreateTimedMacro(
        VenueConnectionConfiguration venue,
        CreateTimedMacroRequest request) =>
        Observe(
            CreateTimedMacroAndReportAsync(venue, request),
            $"create timed macro for {venue.VenueCode}");

    public void UpdateTimedMacro(
        VenueConnectionConfiguration venue,
        long timedMacroId,
        UpdateTimedMacroRequest request) =>
        Observe(
            UpdateTimedMacroAndReportAsync(venue, timedMacroId, request),
            $"update timed macro {timedMacroId} for {venue.VenueCode}");

    public void ArchiveTimedMacro(
        VenueConnectionConfiguration venue,
        long timedMacroId) =>
        Observe(
            ArchiveTimedMacroAndReportAsync(venue, timedMacroId),
            $"archive timed macro {timedMacroId} for {venue.VenueCode}");

    public void RunTimedMacro(
        VenueConnectionConfiguration venue,
        TimedMacroSummary macro,
        TimedMacroOpeningSummary? opening) =>
        Observe(
            RunTimedMacroAndReportAsync(venue, macro, opening),
            $"run timed macro {macro.TimedMacroId} for {venue.VenueCode}");

    public void EnsureVipPerksLoaded(VenueConnectionConfiguration venue)
    {
        if (VipPerks.ShouldLoad(venue))
            Observe(VipPerks.LoadAsync(venue, false, LifetimeToken), $"load VIP perks for {venue.VenueCode}");
    }

    public void RefreshVipPerks(VenueConnectionConfiguration venue) =>
        Observe(VipPerks.LoadAsync(venue, true, LifetimeToken), $"refresh VIP perks for {venue.VenueCode}");

    public void CreateVipPerk(VenueConnectionConfiguration venue, CreateVipPerkRequest request) =>
        Observe(
            CreateVipPerkAndReportAsync(venue, request),
            $"create VIP perk for {venue.VenueCode}");

    public void UpdateVipPerk(
        VenueConnectionConfiguration venue,
        int perkId,
        UpdateVipPerkRequest request) =>
        Observe(
            UpdateVipPerkAndReportAsync(venue, perkId, request),
            $"update VIP perk {perkId} for {venue.VenueCode}");

    public void SetVipPackagePerk(
        VenueConnectionConfiguration venue,
        int packageId,
        int perkId,
        SetVipPackagePerkRequest request) =>
        Observe(
            SetVipPackagePerkAndReportAsync(venue, packageId, perkId, request),
            $"assign VIP perk {perkId} for {venue.VenueCode}");

    public void RedeemVipPerk(VenueConnectionConfiguration venue, RedeemVipPerkRequest request) =>
        Observe(RedeemVipPerkAndReportAsync(venue, request), $"redeem VIP perk for {venue.VenueCode}");

    public void UndoVipPerkRedemption(VenueConnectionConfiguration venue, long redemptionId, string? reason) =>
        Observe(UndoVipPerkAndReportAsync(venue, redemptionId, reason), $"undo VIP perk redemption {redemptionId}");

    public void EnsurePhotoshootsLoaded(VenueConnectionConfiguration venue)
    {
        if (Photoshoots.ShouldLoad(venue))
            Observe(Photoshoots.LoadAsync(venue, false, LifetimeToken), $"load photoshoots for {venue.VenueCode}");
    }

    public void RefreshPhotoshoots(VenueConnectionConfiguration venue) =>
        Observe(Photoshoots.LoadAsync(venue, true, LifetimeToken), $"refresh photoshoots for {venue.VenueCode}");

    public void CreatePhotoshootPackage(
        VenueConnectionConfiguration venue,
        CreatePhotoshootPackageRequest request) =>
        Observe(
            CreatePhotoshootPackageAndReportAsync(venue, request),
            $"create photoshoot package for {venue.VenueCode}");

    public void UpdatePhotoshootPackage(
        VenueConnectionConfiguration venue,
        int packageId,
        UpdatePhotoshootPackageRequest request) =>
        Observe(
            UpdatePhotoshootPackageAndReportAsync(venue, packageId, request),
            $"update photoshoot package {packageId}");

    public void UpdatePhotoshootSettings(
        VenueConnectionConfiguration venue,
        UpdatePhotoshootSettingsRequest request) =>
        Observe(
            UpdatePhotoshootSettingsAndReportAsync(venue, request),
            $"update photoshoot settings for {venue.VenueCode}");

    public void SellPhotoshoot(VenueConnectionConfiguration venue, SellPhotoshootRequest request) =>
        Observe(SellPhotoshootAndReportAsync(venue, request), $"sell photoshoot for {venue.VenueCode}");

    public void SetPhotoshootSalePaymentStatus(
        VenueConnectionConfiguration venue,
        long saleId,
        SetPhotoshootSalePaymentStatusRequest request) =>
        Observe(
            SetPhotoshootSalePaymentStatusAndReportAsync(venue, saleId, request),
            $"set photoshoot sale {saleId} payment status");

    public void CancelPhotoshootSale(
        VenueConnectionConfiguration venue,
        long saleId,
        CancelPhotoshootSaleRequest request) =>
        Observe(
            CancelPhotoshootSaleAndReportAsync(venue, saleId, request),
            $"cancel photoshoot sale {saleId}");

    public void CreatePhotoshootSettlement(VenueConnectionConfiguration venue, CreatePhotoshootSettlementRequest request) =>
        Observe(CreatePhotoshootSettlementAndReportAsync(venue, request), $"create photoshoot settlement for {venue.VenueCode}");

    public void EnsureOtherSalesLoaded(VenueConnectionConfiguration venue)
    {
        if (OtherSales.ShouldLoad(venue))
            Observe(OtherSales.LoadAsync(venue, false, LifetimeToken), $"load Other Sales for {venue.VenueCode}");
    }

    public void RefreshOtherSales(VenueConnectionConfiguration venue) =>
        Observe(OtherSales.LoadAsync(venue, true, LifetimeToken), $"refresh Other Sales for {venue.VenueCode}");

    public void CreateOtherSaleItem(VenueConnectionConfiguration venue, CreateOtherSaleItemRequest request) =>
        Observe(CreateOtherSaleItemAndReportAsync(venue, request), $"create Other Sales item for {venue.VenueCode}");

    public void UpdateOtherSaleItem(VenueConnectionConfiguration venue, int itemId, UpdateOtherSaleItemRequest request) =>
        Observe(UpdateOtherSaleItemAndReportAsync(venue, itemId, request), $"update Other Sales item {itemId}");

    public void UpdateOtherSaleSellerPercentage(VenueConnectionConfiguration venue, int itemId, UpdateOtherSaleSellerPercentageRequest request) =>
        Observe(UpdateOtherSaleSellerPercentageAndReportAsync(venue, itemId, request), $"update Other Sales seller percentage {itemId}");

    public void SellOtherSale(VenueConnectionConfiguration venue, SellOtherSaleRequest request) =>
        Observe(SellOtherSaleAndReportAsync(venue, request), $"record Other Sale for {venue.VenueCode}");

    public void SetOtherSalePaymentStatus(VenueConnectionConfiguration venue, long saleId, SetOtherSalePaymentStatusRequest request) =>
        Observe(SetOtherSalePaymentStatusAndReportAsync(venue, saleId, request), $"set Other Sale {saleId} payment status");

    public void CancelOtherSale(VenueConnectionConfiguration venue, long saleId, CancelOtherSaleRequest request) =>
        Observe(CancelOtherSaleAndReportAsync(venue, saleId, request), $"cancel Other Sale {saleId}");

    public void CreateOtherSalesSettlement(VenueConnectionConfiguration venue, CreateOtherSalesSettlementRequest request) =>
        Observe(CreateOtherSalesSettlementAndReportAsync(venue, request), $"create Other Sales settlement for {venue.VenueCode}");

    public void EnsurePurchasesLoaded(VenueConnectionConfiguration venue)
    {
        if (Purchases.ShouldLoad(venue))
            Observe(Purchases.LoadAsync(venue, false, LifetimeToken), $"load Purchases for {venue.VenueCode}");
    }

    public void RefreshPurchases(VenueConnectionConfiguration venue) =>
        Observe(Purchases.LoadAsync(venue, true, LifetimeToken), $"refresh Purchases for {venue.VenueCode}");

    public void CreatePurchase(VenueConnectionConfiguration venue, CreatePurchaseRequest request) =>
        Observe(CreatePurchaseAndReportAsync(venue, request), $"create purchase for {venue.VenueCode}");

    public void ApprovePurchase(VenueConnectionConfiguration venue, long purchaseId) =>
        Observe(ApprovePurchaseAndReportAsync(venue, purchaseId), $"approve purchase {purchaseId}");

    public void StartPurchasePayment(VenueConnectionConfiguration venue, PurchaseSummary purchase) =>
        Observe(StartPurchasePaymentAndReportAsync(venue, purchase), $"pay purchase {purchase.PurchaseId}");

    public void ConfirmPurchasePaid(VenueConnectionConfiguration venue, long purchaseId) =>
        Observe(ConfirmPurchasePaidAndReportAsync(venue, purchaseId), $"confirm purchase {purchaseId} paid");

    public void RejectPurchase(
        VenueConnectionConfiguration venue,
        long purchaseId,
        RejectPurchaseRequest request) =>
        Observe(RejectPurchaseAndReportAsync(venue, purchaseId, request), $"reject purchase {purchaseId}");

    public void CancelPurchase(
        VenueConnectionConfiguration venue,
        long purchaseId,
        bool wasSettled) =>
        Observe(CancelPurchaseAndReportAsync(venue, purchaseId, wasSettled), $"cancel purchase {purchaseId}");

    public void EnsureOtherGamesLoaded(VenueConnectionConfiguration venue)
    {
        if (OtherGames.ShouldLoad(venue))
            Observe(OtherGames.LoadAsync(venue, false, LifetimeToken), $"load Other Games for {venue.VenueCode}");
    }

    public void RefreshOtherGames(VenueConnectionConfiguration venue) =>
        Observe(OtherGames.LoadAsync(venue, true, LifetimeToken), $"refresh Other Games for {venue.VenueCode}");

    public void CreateOtherGameItem(VenueConnectionConfiguration venue, CreateOtherGameItemRequest request) =>
        Observe(CreateOtherGameItemAndReportAsync(venue, request), $"create Other Games item for {venue.VenueCode}");

    public void UpdateOtherGameItem(VenueConnectionConfiguration venue, int itemId, UpdateOtherGameItemRequest request) =>
        Observe(UpdateOtherGameItemAndReportAsync(venue, itemId, request), $"update Other Games item {itemId}");

    public void UpdateOtherGameSellerPercentage(VenueConnectionConfiguration venue, int itemId, UpdateOtherGameSellerPercentageRequest request) =>
        Observe(UpdateOtherGameSellerPercentageAndReportAsync(venue, itemId, request), $"update Other Games seller percentage {itemId}");

    public void SellOtherGame(VenueConnectionConfiguration venue, SellOtherGameRequest request) =>
        Observe(SellOtherGameAndReportAsync(venue, request), $"record Other Game sale for {venue.VenueCode}");

    public void SetOtherGameOutcome(VenueConnectionConfiguration venue, long saleId, SetOtherGameOutcomeRequest request) =>
        Observe(SetOtherGameOutcomeAndReportAsync(venue, saleId, request), $"set Other Game {saleId} outcome");

    public void SetOtherGameSettlementStatus(VenueConnectionConfiguration venue, long saleId, SetOtherGameSettlementStatusRequest request) =>
        Observe(SetOtherGameSettlementStatusAndReportAsync(venue, saleId, request), $"set Other Game {saleId} settlement status");

    public void CancelOtherGame(VenueConnectionConfiguration venue, long saleId, CancelOtherGameSaleRequest request) =>
        Observe(CancelOtherGameAndReportAsync(venue, saleId, request), $"cancel Other Game sale {saleId}");

    public void CreateOtherGamesSettlement(VenueConnectionConfiguration venue, CreateOtherGamesSettlementRequest request) =>
        Observe(CreateOtherGamesSettlementAndReportAsync(venue, request), $"create Other Games settlement for {venue.VenueCode}");

    public void CreateOtherGamesPayout(VenueConnectionConfiguration venue, CreateOtherGamesPayoutRequest request) =>
        Observe(CreateOtherGamesPayoutAndReportAsync(venue, request), $"create Other Games payout for seller {request.SellerUserId}");

    public void TradeOtherGamesSeller(VenueConnectionConfiguration venue, FinancialSettlementSummary settlement) =>
        Observe(TradeOtherGamesSellerAndReportAsync(venue, settlement), $"trade Other Games seller for settlement {settlement.SettlementId}");

    public void EnsureCourtLoaded(VenueConnectionConfiguration venue)
    {
        if (Court.ShouldLoad(venue))
            Observe(Court.LoadAsync(venue, false, LifetimeToken), $"load Court Services for {venue.VenueCode}");
    }

    public void RefreshCourt(VenueConnectionConfiguration venue) =>
        Observe(Court.LoadAsync(venue, true, LifetimeToken), $"refresh Court Services for {venue.VenueCode}");

    public void UpdateCourtSettings(
        VenueConnectionConfiguration venue,
        UpdateCourtSettingsRequest request) =>
        Observe(UpdateCourtSettingsAndReportAsync(venue, request), "update Court Service settings");

    public void PreviewCourtStaffSettlement(
        VenueConnectionConfiguration venue,
        CreateCourtStaffSettlementRequest request) =>
        Observe(PreviewCourtStaffSettlementAndReportAsync(venue, request), "preview Court staff settlement");

    public void ClearCourtStaffSettlementPreview(VenueConnectionConfiguration venue) =>
        Court.ClearSettlementPreview(venue.ProfileId);

    public void SaveCourtOffer(VenueConnectionConfiguration venue, long? offerId, SaveCourtOfferRequest request) =>
        Observe(ReportApiResultAsync(Court.SaveOfferAsync(venue, offerId, request, LifetimeToken), offerId is null ? "Court Service offer created." : "Court Service offer updated."), "save Court Service offer");

    public void SellCourtService(VenueConnectionConfiguration venue, SellCourtServiceRequest request) =>
        Observe(ReportApiResultAsync(Court.SellAsync(venue, request, LifetimeToken), "Court Service sale recorded."), "sell Court Service");

    public void CancelCourtSale(
        VenueConnectionConfiguration venue,
        long saleId,
        bool refundConfirmed,
        string? reason) =>
        Observe(
            CancelCourtSaleAndReportAsync(venue, saleId, refundConfirmed, reason),
            "cancel Court Service sale");

    public void CreateCourtStaffSettlement(VenueConnectionConfiguration venue, CreateCourtStaffSettlementRequest request) =>
        Observe(ReportCourtTransactionAsync(venue, Court.CreateStaffSettlementAsync(venue, request, LifetimeToken)), "create Court staff settlement");

    public void CreateCourtAccountantPrepay(VenueConnectionConfiguration venue, CreateCourtAccountantPrepayRequest request) =>
        Observe(ReportCourtTransactionAsync(venue, Court.CreateAccountantPrepayAsync(venue, request, LifetimeToken)), "prepay Court Accountant");

    public void CreateCourtAccountantFinalization(VenueConnectionConfiguration venue, CreateCourtAccountantFinalizationRequest request) =>
        Observe(ReportCourtTransactionAsync(venue, Court.CreateAccountantFinalizationAsync(venue, request, LifetimeToken)), "finalize Court Accountant balance");

    public void ExecuteCourtTransactionTrade(VenueConnectionConfiguration venue, CourtTransactionSummary transaction) =>
        Observe(ExecuteCourtTransactionTradeAsync(venue, transaction), $"execute Court transaction {transaction.TransactionId}");

    public void ConfirmCourtTransaction(VenueConnectionConfiguration venue, long transactionId) =>
        Observe(ConfirmCourtTransactionAndReportAsync(venue, transactionId), "confirm Court transaction");

    public void CancelCourtTransaction(VenueConnectionConfiguration venue, long transactionId, string? reason) =>
        Observe(CancelCourtTransactionAndReportAsync(venue, transactionId, reason), "cancel Court transaction");

    public void EnsureStaffLoaded(VenueConnectionConfiguration venue)
    {
        if (Staff.ShouldLoad(venue))
            Observe(Staff.LoadAsync(venue, false, LifetimeToken), $"load Staff for {venue.VenueCode}");
    }

    public void RefreshStaff(VenueConnectionConfiguration venue) =>
        Observe(Staff.LoadAsync(venue, true, LifetimeToken), $"refresh Staff for {venue.VenueCode}");

    public void SaveStaffJob(VenueConnectionConfiguration venue, long? jobId, SaveStaffJobRequest request) =>
        Observe(ReportApiResultAsync(Staff.SaveJobAsync(venue, jobId, request, LifetimeToken), jobId is null ? "Staff job created." : "Staff job updated."), "save Staff job");

    public void SaveStaffMember(VenueConnectionConfiguration venue, long? staffId, SaveStaffMemberRequest request) =>
        Observe(ReportApiResultAsync(Staff.SaveMemberAsync(venue, staffId, request, LifetimeToken), staffId is null ? "Staff listing created." : "Staff listing updated."), "save Staff listing");

    public void LinkStaffCharacter(VenueConnectionConfiguration venue, LinkStaffCharacterRequest request) =>
        Observe(ReportApiResultAsync(Staff.LinkCharacterAsync(venue, request, LifetimeToken), request.StaffMemberId is null ? "Target character unlinked from Staff." : "Target character linked to Staff."), "link Staff character");

    public void SaveStaffTimeEntry(VenueConnectionConfiguration venue, long? timeEntryId, SaveStaffTimeEntryRequest request) =>
        Observe(ReportApiResultAsync(Staff.SaveTimeEntryAsync(venue, timeEntryId, request, LifetimeToken), timeEntryId is null ? "Clock-in recorded." : "Clock-out recorded and salary locked."), "save Staff time entry");

    public void CancelStaffTimeEntry(VenueConnectionConfiguration venue, long timeEntryId, string? reason) =>
        Observe(CancelStaffTimeEntryAndReportAsync(venue, timeEntryId, reason), "cancel Staff time entry");

    public void CreateStaffPayout(VenueConnectionConfiguration venue, CreateStaffPayoutRequest request) =>
        Observe(ReportStaffPayoutAsync(venue, Staff.CreatePayoutAsync(venue, request, LifetimeToken)), "create Staff payout");

    public void EnsureBarLoaded(VenueConnectionConfiguration venue)
    {
        if (Bar.ShouldLoad(venue))
            Observe(Bar.LoadAsync(venue, false, LifetimeToken), $"load bar for {venue.VenueCode}");
    }

    public void RefreshBar(VenueConnectionConfiguration venue) =>
        Observe(Bar.LoadAsync(venue, true, LifetimeToken), $"refresh bar for {venue.VenueCode}");

    public void CreateBarBuyoutPackage(VenueConnectionConfiguration venue, CreateBarBuyoutPackageRequest request) =>
        Observe(BarMutationAndReportAsync(venue, Bar.CreateBuyoutPackageAsync(venue, request, LifetimeToken), "Bar buyout package created."), "create bar buyout package");

    public void UpdateBarBuyoutPackage(VenueConnectionConfiguration venue, long packageId, UpdateBarBuyoutPackageRequest request) =>
        Observe(BarMutationAndReportAsync(venue, Bar.UpdateBuyoutPackageAsync(venue, packageId, request, LifetimeToken), "Bar buyout package updated."), "update bar buyout package");

    public void UpdateBarSettings(VenueConnectionConfiguration venue, UpdateBarSettingsRequest request) =>
        Observe(BarMutationAndReportAsync(venue, Bar.UpdateSettingsAsync(venue, request, LifetimeToken), "Bar settings updated."), "update bar settings");

    public void SellBarBuyout(VenueConnectionConfiguration venue, SellBarBuyoutRequest request) =>
        Observe(SellBarBuyoutAndReportAsync(venue, request), "record bar buyout");

    public void SetBarBuyoutPaymentStatus(VenueConnectionConfiguration venue, long saleId, bool settled) =>
        Observe(BarMutationAndReportAsync(venue, Bar.SetBuyoutPaymentStatusAsync(venue, saleId, new SetBarSalePaymentStatusRequest(settled), LifetimeToken), settled ? $"Bar buyout sale #{saleId} marked settled." : $"Bar buyout sale #{saleId} marked unpaid."), "set bar buyout payment status");

    public void CancelBarBuyout(VenueConnectionConfiguration venue, long saleId, string? reason) =>
        Observe(BarMutationAndReportAsync(venue, Bar.CancelBuyoutAsync(venue, saleId, new CancelBarSaleRequest(reason), LifetimeToken), $"Bar buyout sale #{saleId} cancelled."), "cancel bar buyout");

    public void StartGambaGame(VenueConnectionConfiguration venue, int startingJackpotGil) =>
        Observe(StartGambaGameAndReportAsync(venue, startingJackpotGil), "start Gamba Shot");

    public void SellGambaTickets(VenueConnectionConfiguration venue, SellGambaTicketsRequest request) =>
        Observe(SellGambaTicketsAndReportAsync(venue, request), "sell Gamba Shot tickets");

    public void SetGambaTicketPaymentStatus(VenueConnectionConfiguration venue, long saleId, bool settled) =>
        Observe(BarMutationAndReportAsync(venue, Bar.SetGambaTicketPaymentStatusAsync(venue, saleId, new SetBarSalePaymentStatusRequest(settled), LifetimeToken), settled ? $"Gamba ticket sale #{saleId} marked settled." : $"Gamba ticket sale #{saleId} marked unpaid."), "set Gamba ticket payment status");

    public void CancelGambaTicketSale(VenueConnectionConfiguration venue, long saleId, string? reason) =>
        Observe(BarMutationAndReportAsync(venue, Bar.CancelGambaTicketSaleAsync(venue, saleId, new CancelBarSaleRequest(reason), LifetimeToken), $"Gamba ticket sale #{saleId} cancelled."), "cancel Gamba ticket sale");

    public void CompleteGambaGame(VenueConnectionConfiguration venue, long gameId, CompleteGambaGameRequest request) =>
        Observe(CompleteGambaGameAndReportAsync(venue, gameId, request), "complete Gamba Shot");

    public void CancelGambaGame(VenueConnectionConfiguration venue, long gameId, string? reason) =>
        Observe(CancelGambaGameAndReportAsync(venue, gameId, new CancelGambaGameRequest(reason)), "cancel Gamba Shot session");

    public void CreateBarSettlement(VenueConnectionConfiguration venue, CreateBarSettlementRequest request) =>
        Observe(CreateBarSettlementAndReportAsync(venue, request), "create bar settlement");

    public void EnsureFinanceLoaded(VenueConnectionConfiguration venue)
    {
        if (!Finance.ShouldLoad(venue))
        {
            return;
        }

        Observe(
            Finance.LoadAsync(venue, false, LifetimeToken),
            $"load finance data for {venue.VenueCode}");
    }

    public void RefreshFinance(VenueConnectionConfiguration venue) =>
        Observe(
            Finance.LoadAsync(venue, true, LifetimeToken),
            $"refresh finance data for {venue.VenueCode}");

    public void CreateVipSettlement(
        VenueConnectionConfiguration venue,
        CreateVipSettlementRequest request) =>
        Observe(
            CreateVipSettlementAndReportAsync(venue, request),
            $"create VIP settlement for {venue.VenueCode}");

    public void RespondSettlement(
        VenueConnectionConfiguration venue,
        long settlementId,
        RespondSettlementRequest request) =>
        Observe(
            RespondSettlementAndReportAsync(venue, settlementId, request),
            $"respond to settlement {settlementId} for {venue.VenueCode}");

    public void OpenVipPlayerEditor(VenueConnectionConfiguration venue, int vipPlayerId) =>
        vipPlayerEditWindow.Open(venue.ProfileId, vipPlayerId);

    public void OpenNotificationAction(QueuedPartyPulseNotification queued)
    {
        var settlementId = string.Equals(
                queued.Notification.ActionKey,
                "finance.settlement",
                StringComparison.OrdinalIgnoreCase)
            ? queued.Notification.ActionEntityId
            : null;
        mainWindow.OpenFinance(queued.VenueProfileId, settlementId);
    }

    public void MarkNotificationSeen(QueuedPartyPulseNotification queued, bool dismissed) =>
        Observe(
            MarkNotificationSeenAsync(queued, dismissed),
            $"mark notification {queued.Notification.NotificationId} seen");

    public void OpenVenueUserEditor(VenueConnectionConfiguration venue, VenueUserSummary user) =>
        venueUserEditWindow.Open(venue.ProfileId, user.UserId);

    private bool TryGetCurrentIdentity(
        VenueConnectionConfiguration venue,
        out PlayerIdentity? identity)
    {
        if (IdentityProvider.TryGetCurrent(out identity, out var reason))
        {
            return true;
        }

        Authentication.SetClientError(venue, reason);
        return false;
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
        notificationToastWindow.Tick();
        if (PartyFinderAutomation.IsRunning)
        {
            var partyFinderVenue = Configuration.VenueConnections.FirstOrDefault(
                venue => venue.ProfileId == PartyFinderAutomation.ProfileId);
            if (partyFinderVenue is not null)
                EnsureOpeningPublicationsLoaded(partyFinderVenue);
        }
        var partyFinderWasRunning = PartyFinderAutomation.IsRunning;
        PartyFinderAutomation.Tick();
        if (partyFinderWasRunning && !PartyFinderAutomation.IsRunning)
            ChatGui.Print(PartyFinderAutomation.StatusMessage, "PartyPulse");

        if (!IdentityProvider.TryGetCurrent(out var identity, out _))
        {
            if (observedIdentity is not null)
            {
                observedIdentity = null;
                autoConnectStarted = false;
                Authentication.ClearAccessTokens("Character logged out or changed.");
                UserManagement.Clear("Character logged out or changed.");
                SelfService.Clear("Character logged out or changed.");
                Vip.Clear("Character logged out or changed.");
                VipArrivals.Clear();
                Greeter.Clear();
                VenueOpenings.Clear("Character logged out or changed.");
                TimedMacros.Clear("Character logged out or changed.");
                Djs.Clear("Character logged out or changed.");
                OpeningPublications.Clear("Character logged out or changed.");
                PartyFinderAutomation.Stop("Party Finder refresher stopped because the character logged out or changed.");
                NearbyVipPlayers.Clear();
                VipArrivalNearby.Clear();
                GreeterNearby.Clear();
                Finance.Clear("Character logged out or changed.");
                Notifications.Clear();
            }

            return;
        }

        if (observedIdentity != identity)
        {
            observedIdentity = identity;
            autoConnectStarted = false;
            Authentication.ClearAccessTokens("Character changed; authentication must be renewed.");
            UserManagement.Clear("Character changed; venue-user data was cleared.");
            SelfService.Clear("Character changed; self-service data was cleared.");
            Vip.Clear("Character changed; VIP data was cleared.");
            VipArrivals.Clear();
            Greeter.Clear();
            VenueOpenings.Clear("Character changed; opening schedule was cleared.");
            TimedMacros.Clear("Character changed; timed macro data was cleared.");
            Djs.Clear("Character changed; DJ data was cleared.");
            OpeningPublications.Clear("Character changed; opening-publication data was cleared.");
            PartyFinderAutomation.Stop("Party Finder refresher stopped because the character changed.");
            NearbyVipPlayers.Clear();
            VipArrivalNearby.Clear();
            GreeterNearby.Clear();
            Finance.Clear("Character changed; finance data was cleared.");
            Notifications.Clear();
        }

        var notificationVenues = Configuration.VenueConnections
            .Where(venue =>
                venue.IsRegistered &&
                Authentication.GetSnapshot(venue).Status is
                    AuthenticationStatus.Connected or AuthenticationStatus.Expired)
            .ToArray();
        if (Notifications.IsPollDue && notificationVenues.Length > 0)
        {
            Observe(
                Notifications.PollDueAsync(notificationVenues, LifetimeToken),
                "poll PartyPulse notifications");
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

    private async Task RedeemDevicePairingCodeAndReportAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string pairingCode)
    {
        var result = await Authentication.RedeemPairingCodeAsync(
            venue,
            identity,
            pairingCode,
            Configuration.ApiBaseUrl,
            LifetimeToken);

        if (result.Success)
        {
            ChatGui.Print($"Registered this device for {venue.DisplayLabel}.", "PartyPulse");
            SelfService.RemoveProfile(venue.ProfileId);
            return;
        }

        ChatGui.PrintError(result.Failure?.Message ?? "The device pairing code could not be redeemed.", "PartyPulse");
    }

    private async Task LinkCurrentCharacterAndReportAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity)
    {
        var result = await Authentication.LinkCurrentCharacterAsync(
            venue,
            identity,
            Configuration.ApiBaseUrl,
            LifetimeToken);

        if (result.Success)
        {
            ChatGui.Print($"Linked {identity.DisplayName} to {venue.DisplayLabel}.", "PartyPulse");
            SelfService.RemoveProfile(venue.ProfileId);
            EnsureSelfServiceLoaded(venue);
            return;
        }

        ChatGui.PrintError(result.Failure?.Message ?? "The current character could not be linked.", "PartyPulse");
    }

    private async Task UnlinkCharacterAndReportAsync(
        VenueConnectionConfiguration venue,
        int characterId)
    {
        var result = await SelfService.UnlinkCharacterAsync(venue, characterId, LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Character unlinked from the venue account.", "PartyPulse");
            return;
        }

        ChatGui.PrintError(result.Failure?.Message ?? "The character could not be unlinked.", "PartyPulse");
    }

    private async Task CreateDevicePairingCodeAndReportAsync(VenueConnectionConfiguration venue)
    {
        var result = await SelfService.CreatePairingCodeAsync(venue, LifetimeToken);
        if (result.Success && result.Value is not null)
        {
            ChatGui.Print(
                $"Device pairing code for {venue.DisplayLabel}: {result.Value.PairingCode} (expires {VenueTimeZone.Format(venue, result.Value.ExpiresAt, "g")}).",
                "PartyPulse");
            return;
        }

        ChatGui.PrintError(result.Failure?.Message ?? "The device pairing code could not be created.", "PartyPulse");
    }

    private async Task UnauthorizeFromVenueAndReportAsync(VenueConnectionConfiguration venue)
    {
        var result = await SelfService.UnauthorizeFromVenueAsync(venue, LifetimeToken);
        if (!result.Success)
        {
            ChatGui.PrintError(result.Failure?.Message ?? "The venue user could not be unauthorized.", "PartyPulse");
            return;
        }

        venue.DeviceId = 0;
        venue.RefreshToken = string.Empty;
        venue.RefreshTokenUpdatedAt = null;
        Configuration.Save();
        Authentication.RemoveProfile(venue.ProfileId);
        UserManagement.RemoveProfile(venue.ProfileId);
        SelfService.RemoveProfile(venue.ProfileId);
        Vip.RemoveProfile(venue.ProfileId);
        VipPerks.RemoveProfile(venue.ProfileId);
        Photoshoots.RemoveProfile(venue.ProfileId);
        OtherSales.RemoveProfile(venue.ProfileId);
        OtherGames.RemoveProfile(venue.ProfileId);
        Purchases.RemoveProfile(venue.ProfileId);
        Court.RemoveProfile(venue.ProfileId);
        Staff.RemoveProfile(venue.ProfileId);
        Bar.RemoveProfile(venue.ProfileId);
        VipArrivals.ClearProfile(venue.ProfileId);
        Greeter.ClearProfile(venue.ProfileId);
        VenueOpenings.RemoveProfile(venue.ProfileId);
        TimedMacros.RemoveProfile(venue.ProfileId);
        Djs.RemoveProfile(venue.ProfileId);
        OpeningPublications.RemoveProfile(venue.ProfileId);
        if (PartyFinderAutomation.ProfileId == venue.ProfileId)
            PartyFinderAutomation.Stop("Party Finder refresher stopped because venue authorization changed.");
        NearbyVipPlayers.ClearProfile(venue.ProfileId);
        VipArrivalNearby.Clear();
        GreeterNearby.Clear();
        Finance.RemoveProfile(venue.ProfileId);
        Notifications.RemoveProfile(venue.ProfileId);
        ChatGui.Print($"Unauthorized from {venue.DisplayLabel}. The venue remains saved in visitor mode.", "PartyPulse");
    }

    private async Task CreateVenueUserAndReportAsync(
        VenueConnectionConfiguration venue,
        string displayName,
        string? discordHandle)
    {
        var result = await UserManagement.CreateAsync(
            venue,
            displayName,
            discordHandle,
            LifetimeToken);

        if (result.Success && result.Value is not null)
        {
            ChatGui.Print(
                $"Created venue user '{displayName}'. Invite code: {result.Value.InviteCode} (expires {VenueTimeZone.Format(venue, result.Value.InviteExpiresAt, "g")}).",
                "PartyPulse");
            return;
        }

        ReportUserManagementFailure(result.Failure, "The venue user could not be created.");
    }

    private async Task UpdateVenueUserProfileAndReportAsync(
        VenueConnectionConfiguration venue,
        int userId,
        string displayName,
        string? discordHandle)
    {
        var result = await UserManagement.UpdateProfileAsync(
            venue,
            userId,
            displayName,
            discordHandle,
            LifetimeToken);

        if (result.Success)
        {
            ChatGui.Print($"Updated venue user '{displayName}'.", "PartyPulse");
            return;
        }

        ReportUserManagementFailure(result.Failure, "The venue user could not be updated.");
    }

    private async Task UpdateVenueUserPermissionsAndReportAsync(
        VenueConnectionConfiguration venue,
        int userId,
        string[] permissionKeys)
    {
        var result = await UserManagement.SetPermissionsAsync(
            venue,
            userId,
            permissionKeys,
            LifetimeToken);

        if (result.Success)
        {
            ChatGui.Print($"Updated permissions for venue user #{userId}.", "PartyPulse");
            return;
        }

        ReportUserManagementFailure(result.Failure, "Venue-user permissions could not be updated.");
    }

    private async Task CreateVenueUserRecoveryCodeAndReportAsync(
        VenueConnectionConfiguration venue,
        VenueUserSummary user)
    {
        var result = await UserManagement.CreateRecoveryCodeAsync(
            venue,
            user,
            LifetimeToken);

        if (result.Success && result.Value is not null)
        {
            ChatGui.Print(
                $"Recovery code for '{user.DisplayName}': {result.Value.RecoveryCode} (expires {VenueTimeZone.Format(venue, result.Value.RecoveryCodeExpiresAt, "g")}).",
                "PartyPulse");
            return;
        }

        ReportUserManagementFailure(result.Failure, "The recovery code could not be created.");
    }

    private async Task RestoreVenueUserAndReportAsync(
        VenueConnectionConfiguration venue,
        VenueUserSummary user)
    {
        var result = await UserManagement.RestoreAsync(
            venue,
            user,
            LifetimeToken);

        if (result.Success && result.Value is not null)
        {
            ChatGui.Print(
                $"Restored venue user '{user.DisplayName}'. Invite code: {result.Value.InviteCode} (expires {VenueTimeZone.Format(venue, result.Value.InviteExpiresAt, "g")}). Permissions remain cleared until reassigned.",
                "PartyPulse");
            return;
        }

        ReportUserManagementFailure(result.Failure, "The venue user could not be restored.");
    }

    private async Task CreateVipPackageAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateVipPackageRequest request)
    {
        var result = await Vip.CreatePackageAsync(venue, request, LifetimeToken);
        if (result.Success && result.Value is not null)
        {
            await VipPerks.LoadAsync(venue, true, LifetimeToken);
            await RefreshPhotoshootsIfLoadedAsync(venue);
            await RefreshOtherSalesIfLoadedAsync(venue);
            await RefreshOtherGamesIfLoadedAsync(venue);
            ChatGui.Print($"Created VIP package '{request.Name}'.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The VIP package could not be created.");
    }

    private async Task UpdateVipPackageAndReportAsync(
        VenueConnectionConfiguration venue,
        int packageId,
        UpdateVipPackageRequest request)
    {
        var result = await Vip.UpdatePackageAsync(venue, packageId, request, LifetimeToken);
        if (result.Success)
        {
            await VipPerks.LoadAsync(venue, true, LifetimeToken);
            await RefreshPhotoshootsIfLoadedAsync(venue);
            await RefreshOtherSalesIfLoadedAsync(venue);
            await RefreshOtherGamesIfLoadedAsync(venue);
            ChatGui.Print($"Updated VIP package '{request.Name}'.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The VIP package could not be updated.");
    }

    private async Task SellVipSubscriptionAndReportAsync(
        VenueConnectionConfiguration venue,
        SellVipSubscriptionRequest request)
    {
        var result = await Vip.SellSubscriptionAsync(venue, request, LifetimeToken);
        if (result.Success && result.Value is not null)
        {
            var period = result.Value.Lifetime
                ? "lifetime"
                : $"until {VenueTimeZone.Format(venue, result.Value.EndsAt!.Value, "g")}";
            ChatGui.Print(
                $"Sold VIP to {request.CharacterName} @ {request.WorldName} ({period}).",
                "PartyPulse");

            await VipPerks.LoadAsync(venue, true, LifetimeToken);
            await RefreshPhotoshootsIfLoadedAsync(venue);
            await RefreshOtherSalesIfLoadedAsync(venue);
            await RefreshOtherGamesIfLoadedAsync(venue);

            if (result.Value.WasNewVip && result.Value.OpeningId is { } openingId)
            {
                var character = Vip.GetSnapshot(venue).View?.Characters.FirstOrDefault(value =>
                    value.VipPlayerId == result.Value.VipPlayerId &&
                    string.Equals(value.CharacterName, request.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(value.WorldName, request.WorldName, StringComparison.OrdinalIgnoreCase));
                if (character is not null)
                {
                    VipArrivals.SetNewMemberOffer(new VipNewMemberOffer(
                        venue.ProfileId,
                        openingId,
                        result.Value.VipPlayerId,
                        character.CharacterId,
                        character.CharacterName,
                        character.WorldName));
                    await VipArrivals.LoadAsync(venue, true, LifetimeToken);
                    ChatGui.Print("This player had no active VIP before the sale. An optional new-member message is available in the VIP tab.", "PartyPulse");
                }
            }
            return;
        }

        ReportVipFailure(result.Failure, "The VIP subscription could not be sold.");
    }

    private async Task LinkVipCharacterAndReportAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        LinkVipCharacterRequest request)
    {
        var result = await Vip.LinkCharacterAsync(
            venue,
            vipPlayerId,
            request,
            LifetimeToken);
        if (result.Success)
        {
            await VipPerks.LoadAsync(venue, true, LifetimeToken);
            await RefreshPhotoshootsIfLoadedAsync(venue);
            await RefreshOtherSalesIfLoadedAsync(venue);
            await RefreshOtherGamesIfLoadedAsync(venue);
            ChatGui.Print(
                $"Linked {request.CharacterName} @ {request.WorldName} to the VIP player.",
                "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The VIP character could not be linked.");
    }

    private async Task SetVipPreferredCharacterAndReportAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        int characterId)
    {
        var result = await Vip.SetPreferredCharacterAsync(
            venue,
            vipPlayerId,
            characterId,
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Updated the VIP player's preferred display character.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The preferred VIP character could not be updated.");
    }

    private async Task UpdateVipPlayerAndReportAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        UpdateVipPlayerRequest request)
    {
        var result = await Vip.UpdatePlayerAsync(venue, vipPlayerId, request, LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Updated the VIP player's Discord username.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The VIP player could not be updated.");
    }

    private async Task UnlinkVipCharacterAndReportAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        int characterId)
    {
        var result = await Vip.UnlinkCharacterAsync(
            venue,
            vipPlayerId,
            characterId,
            LifetimeToken);
        if (result.Success)
        {
            await VipPerks.LoadAsync(venue, true, LifetimeToken);
            await RefreshPhotoshootsIfLoadedAsync(venue);
            await RefreshOtherSalesIfLoadedAsync(venue);
            await RefreshOtherGamesIfLoadedAsync(venue);
            ChatGui.Print("Unlinked the character from the VIP player.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The VIP character could not be unlinked.");
    }

    private async Task CancelVipSubscriptionAndReportAsync(
        VenueConnectionConfiguration venue,
        long subscriptionId,
        CancelVipSubscriptionRequest request)
    {
        var result = await Vip.CancelSubscriptionAsync(
            venue,
            subscriptionId,
            request,
            LifetimeToken);
        if (result.Success)
        {
            await VipPerks.LoadAsync(venue, true, LifetimeToken);
            await RefreshPhotoshootsIfLoadedAsync(venue);
            await RefreshOtherSalesIfLoadedAsync(venue);
            await RefreshOtherGamesIfLoadedAsync(venue);
            ChatGui.Print(
                $"Cancelled VIP subscription #{subscriptionId}. Refunds must be handled separately.",
                "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The VIP subscription could not be cancelled.");
    }

    private async Task SetVipSubscriptionPaymentStatusAndReportAsync(
        VenueConnectionConfiguration venue,
        long subscriptionId,
        SetVipSubscriptionPaymentStatusRequest request)
    {
        var result = await Vip.SetSubscriptionPaymentStatusAsync(
            venue,
            subscriptionId,
            request,
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print(
                request.Settled
                    ? $"Marked VIP subscription #{subscriptionId} as settled."
                    : $"Marked VIP subscription #{subscriptionId} as unpaid.",
                "PartyPulse");
            await Finance.LoadAsync(venue, true, LifetimeToken);
            return;
        }

        ReportVipFailure(result.Failure, "The VIP payment status could not be updated.");
    }

    private async Task SubmitGreeterObservationsAsync(
        VenueConnectionConfiguration venue,
        long openingId,
        IReadOnlyList<GreeterObservationRequest> observations)
    {
        var result = await Greeter.ObserveAsync(
            venue,
            new ObserveGreeterArrivalsRequest(openingId, observations),
            LifetimeToken);
        if (result.Success)
        {
            GreeterNearby.MarkSubmitted(observations);
            return;
        }

        GreeterNearby.ReleaseSubmission(observations);
        Log.Warning(
            "Greeter observation upload failed: {Code} {Message}",
            result.Failure?.Code,
            result.Failure?.Message);
    }

    private async Task RunGreeterMacroAndReportAsync(
        VenueConnectionConfiguration venue,
        GreeterArrivalSummary arrival,
        NearbyGreeterPlayer nearby,
        GreeterMacroSummary macro,
        GreeterCurrentDjSummary? currentDj)
    {
        if (!macro.IsConfigured)
        {
            ChatGui.PrintError("The selected greeter macro has not been configured.", "PartyPulse");
            return;
        }

        var macroText = macro.MacroText!
            .Replace("<name>", currentDj?.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<twitch>", currentDj?.TwitchUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var execution = await gameMacroExecutionService.ExecuteAsync(
            nearby.GameObjectId,
            nearby.CharacterName,
            nearby.WorldName,
            macroText,
            LifetimeToken);
        if (!execution.Success)
        {
            ChatGui.PrintError(execution.ErrorMessage ?? "The greeter macro could not be executed.", "PartyPulse");
            return;
        }

        var result = await Greeter.RecordActionAsync(
            venue,
            new RecordGreeterActionRequest(
                arrival.OpeningId,
                arrival.CharacterName,
                arrival.WorldName,
                "greet"),
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print($"Greeted {arrival.DisplayName}.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The greeting ran, but its arrival state could not be recorded.");
    }

    private async Task RecordGreeterActionAndReportAsync(
        VenueConnectionConfiguration venue,
        RecordGreeterActionRequest request,
        string successMessage)
    {
        var result = await Greeter.RecordActionAsync(venue, request, LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print(successMessage, "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The greeter arrival action could not be recorded.");
    }

    private async Task UpdateGreeterMacroAndReportAsync(
        VenueConnectionConfiguration venue,
        string macroCode,
        string? macroText)
    {
        var result = await Greeter.UpdateMacroAsync(
            venue,
            macroCode,
            new UpdateGreeterMacroRequest(macroText),
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Updated greeter macro.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The greeter macro could not be updated.");
    }

    private async Task SubmitVipArrivalObservationsAsync(
        VenueConnectionConfiguration venue,
        long openingId,
        IReadOnlyList<VipArrivalObservationRequest> observations)
    {
        var result = await VipArrivals.ObserveAsync(
            venue,
            new ObserveVipArrivalsRequest(openingId, observations),
            LifetimeToken);
        if (result.Success)
        {
            VipArrivalNearby.MarkSubmitted(observations);
            return;
        }

        VipArrivalNearby.ReleaseSubmission(observations);
        Log.Warning(
            "VIP arrival observation upload failed: {Code} {Message}",
            result.Failure?.Code,
            result.Failure?.Message);
    }

    private async Task RunVipArrivalMacroAndReportAsync(
        VenueConnectionConfiguration venue,
        VipArrivalSummary arrival,
        NearbyVipArrivalCharacter nearby,
        VenueMacroSummary macro,
        string actionKey)
    {
        if (!macro.IsConfigured)
        {
            ChatGui.PrintError($"The {macro.DisplayName} macro is not configured.", "PartyPulse");
            return;
        }

        var execution = await gameMacroExecutionService.ExecuteAsync(
            nearby.GameObjectId,
            nearby.CharacterName,
            nearby.WorldName,
            macro.MacroText!,
            LifetimeToken);
        if (!execution.Success)
        {
            ChatGui.PrintError($"{execution.ErrorMessage} [{execution.ErrorCode}]", "PartyPulse");
            return;
        }

        var result = await VipArrivals.RecordActionAsync(
            venue,
            arrival.VipPlayerId,
            new RecordVipArrivalActionRequest(arrival.OpeningId, actionKey, nearby.CharacterId),
            LifetimeToken);
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The macro started, but the arrival action could not be recorded.");
        }
    }

    private async Task RecordVipArrivalActionAndReportAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        RecordVipArrivalActionRequest request,
        string successMessage)
    {
        var result = await VipArrivals.RecordActionAsync(
            venue,
            vipPlayerId,
            request,
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print(successMessage, "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The VIP arrival action could not be recorded.");
    }

    private async Task UpdateVenueMacroAndReportAsync(
        VenueConnectionConfiguration venue,
        string macroCode,
        string? macroText)
    {
        var result = await VipArrivals.UpdateMacroAsync(
            venue,
            macroCode,
            new UpdateVenueMacroRequest(macroText),
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Venue macro updated.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The venue macro could not be updated.");
    }

    private async Task StartTemporaryVenueOpeningAndReportAsync(
        VenueConnectionConfiguration venue,
        int durationMinutes,
        string? title)
    {
        var result = await VipArrivals.StartTemporaryOpeningAsync(
            venue,
            new StartTemporaryOpeningRequest(durationMinutes, title),
            LifetimeToken);
        if (result.Success && result.Value is not null)
        {
            VipArrivalNearby.Clear();
            GreeterNearby.Clear();
            await Greeter.LoadAsync(venue, true, LifetimeToken);
            await TimedMacros.LoadAsync(venue, true, LifetimeToken);
            await OpeningPublications.LoadAsync(venue, true, LifetimeToken);
            ChatGui.Print(
                $"Started opening #{result.Value.OpeningId} until {VenueTimeZone.Format(venue, result.Value.ClosesAt, "g")} at {result.Value.AddressDisplay}.",
                "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The temporary venue opening could not be started.");
    }

    private async Task CloseVenueOpeningAndReportAsync(
        VenueConnectionConfiguration venue,
        long openingId)
    {
        var result = await VipArrivals.CloseOpeningAsync(venue, openingId, LifetimeToken);
        if (result.Success)
        {
            VipArrivalNearby.Clear();
            GreeterNearby.Clear();
            await Greeter.LoadAsync(venue, true, LifetimeToken);
            await TimedMacros.LoadAsync(venue, true, LifetimeToken);
            await OpeningPublications.LoadAsync(venue, true, LifetimeToken);
            ChatGui.Print($"Closed venue opening #{openingId}.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The venue opening could not be closed.");
    }

    private async Task SaveOpeningPublicationTemplateAndReportAsync(
        VenueConnectionConfiguration venue,
        string publicationCode,
        string? templateText)
    {
        var result = await OpeningPublications.SaveTemplateAsync(
            venue,
            publicationCode,
            new SaveOpeningPublicationTemplateRequest(templateText),
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Opening-publication template saved.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The opening-publication template could not be saved.");
    }

    private async Task GenerateOpeningPublicationsAndReportAsync(
        VenueConnectionConfiguration venue,
        OpeningPublicationOpeningSummary opening,
        string channelCode)
    {
        var result = await OpeningPublications.GenerateAsync(
            venue, opening, channelCode, LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print($"Generated {channelCode} text for opening #{opening.OpeningId}.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "Opening-publication text could not be generated.");
    }

    private async Task SaveOpeningPublicationTextAndReportAsync(
        VenueConnectionConfiguration venue,
        long openingId,
        string publicationCode,
        string? publicationText)
    {
        var result = await OpeningPublications.SaveTextAsync(
            venue,
            openingId,
            publicationCode,
            new SaveOpeningPublicationTextRequest(publicationText),
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Opening-specific publication text saved.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "Opening-specific publication text could not be saved.");
    }

    private async Task RunShoutrunnerShoutAndReportAsync(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse context,
        ActiveShoutrunnerPublication publication)
    {
        var result = await ShoutrunnerDuty.ExecuteShoutAsync(
            venue,
            context,
            publication,
            LifetimeToken);
        if (result.Success)
            ChatGui.Print(result.Message, "PartyPulse");
        else
            ChatGui.PrintError(result.Message, "PartyPulse");
    }

    private async Task ReportShoutrunnerDutyAndReportAsync(
        VenueConnectionConfiguration venue)
    {
        var batch = ShoutrunnerDuty.CreateReportBatch(venue);
        if (batch is null)
        {
            ChatGui.PrintError("There are no pending Shoutrunner duty log entries to report.", "PartyPulse");
            return;
        }

        var result = await OpeningPublications.ReportShoutrunnerDutyAsync(
            venue,
            batch.Request,
            LifetimeToken);
        if (result.Success && result.Value is not null)
        {
            ShoutrunnerDuty.ConfirmReported(venue, batch.ClientEntryIds);
            ChatGui.Print(
                $"Reported {result.Value.AcceptedCount} Shoutrunner log entries" +
                (result.Value.DuplicateCount > 0 ? $" ({result.Value.DuplicateCount} already existed)." : "."),
                "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The Shoutrunner duty report could not be saved.");
    }

    private async Task SaveVenueOpeningAndReportAsync(
        VenueConnectionConfiguration venue,
        long? openingId,
        SaveVenueOpeningRequest request)
    {
        var result = await VenueOpenings.SaveAsync(
            venue,
            openingId,
            request,
            LifetimeToken);
        if (result.Success && result.Value is not null)
        {
            VipArrivalNearby.Clear();
            GreeterNearby.Clear();
            await VipArrivals.LoadAsync(venue, true, LifetimeToken);
            await Greeter.LoadAsync(venue, true, LifetimeToken);
            await TimedMacros.LoadAsync(venue, true, LifetimeToken);
            await OpeningPublications.LoadAsync(venue, true, LifetimeToken);
            ChatGui.Print(
                $"{(openingId is null ? "Scheduled" : "Updated")} opening #{result.Value.OpeningId} for {VenueTimeZone.Format(venue, result.Value.OpensAt, "g")}.",
                "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The venue opening could not be saved.");
    }

    private async Task CancelVenueOpeningAndReportAsync(
        VenueConnectionConfiguration venue,
        long openingId)
    {
        var result = await VenueOpenings.CancelAsync(venue, openingId, LifetimeToken);
        if (result.Success)
        {
            VipArrivalNearby.Clear();
            GreeterNearby.Clear();
            await VipArrivals.LoadAsync(venue, true, LifetimeToken);
            await Greeter.LoadAsync(venue, true, LifetimeToken);
            await TimedMacros.LoadAsync(venue, true, LifetimeToken);
            await OpeningPublications.LoadAsync(venue, true, LifetimeToken);
            ChatGui.Print($"Cancelled venue opening #{openingId}.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The scheduled venue opening could not be cancelled.");
    }

    private async Task CloseScheduledVenueOpeningAndReportAsync(
        VenueConnectionConfiguration venue,
        long openingId)
    {
        var result = await VenueOpenings.CloseAsync(venue, openingId, LifetimeToken);
        if (result.Success)
        {
            VipArrivalNearby.Clear();
            GreeterNearby.Clear();
            await VipArrivals.LoadAsync(venue, true, LifetimeToken);
            await Greeter.LoadAsync(venue, true, LifetimeToken);
            await TimedMacros.LoadAsync(venue, true, LifetimeToken);
            await OpeningPublications.LoadAsync(venue, true, LifetimeToken);
            ChatGui.Print($"Closed venue opening #{openingId}.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The active venue opening could not be closed.");
    }

    private async Task SaveDjAndReportAsync(
        VenueConnectionConfiguration venue,
        long? djId,
        SaveDjRequest request)
    {
        var result = await Djs.SaveDjAsync(venue, djId, request, LifetimeToken);
        if (result.Success && result.Value is not null)
        {
            ChatGui.Print(
                $"{(djId is null ? "Registered" : "Updated")} DJ {result.Value.Name}.",
                "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The DJ could not be saved.");
    }

    private async Task ArchiveDjAndReportAsync(
        VenueConnectionConfiguration venue,
        long djId)
    {
        var result = await Djs.ArchiveDjAsync(venue, djId, LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("DJ removed from the active directory. Booking history was preserved.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The DJ could not be removed.");
    }

    private async Task SaveDjBookingAndReportAsync(
        VenueConnectionConfiguration venue,
        long? bookingId,
        SaveDjBookingRequest request)
    {
        var result = await Djs.SaveBookingAsync(venue, bookingId, request, LifetimeToken);
        if (result.Success && result.Value is not null)
        {
            await Greeter.LoadAsync(venue, true, LifetimeToken);
            await TimedMacros.LoadAsync(venue, true, LifetimeToken);
            await OpeningPublications.LoadAsync(venue, true, LifetimeToken);
            ChatGui.Print(
                $"{(bookingId is null ? "Scheduled" : "Updated")} {result.Value.DjName} for {VenueTimeZone.Format(venue, result.Value.StartsAt, "g")}.",
                "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The DJ booking could not be saved.");
    }

    private async Task DeleteDjBookingAndReportAsync(
        VenueConnectionConfiguration venue,
        long openingId,
        long bookingId)
    {
        var result = await Djs.DeleteBookingAsync(venue, openingId, bookingId, LifetimeToken);
        if (result.Success)
        {
            await Greeter.LoadAsync(venue, true, LifetimeToken);
            await TimedMacros.LoadAsync(venue, true, LifetimeToken);
            await OpeningPublications.LoadAsync(venue, true, LifetimeToken);
            ChatGui.Print("DJ booking removed. Historical status changes were preserved.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The DJ booking could not be removed.");
    }

    private async Task RunNewVipMacroAndReportAsync(
        VenueConnectionConfiguration venue,
        VipNewMemberOffer offer,
        VenueMacroSummary macro)
    {
        if (!macro.IsConfigured)
        {
            ChatGui.PrintError("The new VIP message macro is not configured.", "PartyPulse");
            return;
        }

        var execution = await gameMacroExecutionService.ExecuteForIdentityAsync(
            offer.CharacterName,
            offer.WorldName,
            macro.MacroText!,
            LifetimeToken);
        if (!execution.Success)
        {
            ChatGui.PrintError($"{execution.ErrorMessage} [{execution.ErrorCode}]", "PartyPulse");
            return;
        }

        var result = await VipArrivals.RecordActionAsync(
            venue,
            offer.VipPlayerId,
            new RecordVipArrivalActionRequest(offer.OpeningId, "new_vip", offer.CharacterId),
            LifetimeToken);
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The message started, but the new-VIP action could not be recorded.");
            return;
        }

        VipArrivals.ClearNewMemberOffer(venue.ProfileId);
        ChatGui.Print("Started the new VIP message macro.", "PartyPulse");
    }

    private async Task CreateTimedMacroAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateTimedMacroRequest request)
    {
        var result = await TimedMacros.CreateAsync(venue, request, LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Custom timed macro created.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The timed macro could not be created.");
    }

    private async Task UpdateTimedMacroAndReportAsync(
        VenueConnectionConfiguration venue,
        long timedMacroId,
        UpdateTimedMacroRequest request)
    {
        var result = await TimedMacros.UpdateAsync(
            venue,
            timedMacroId,
            request,
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Timed macro updated.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The timed macro could not be updated.");
    }

    private async Task ArchiveTimedMacroAndReportAsync(
        VenueConnectionConfiguration venue,
        long timedMacroId)
    {
        var result = await TimedMacros.ArchiveAsync(
            venue,
            timedMacroId,
            LifetimeToken);
        if (result.Success)
        {
            ChatGui.Print("Timed macro archived.", "PartyPulse");
            return;
        }

        ReportVipFailure(result.Failure, "The timed macro could not be archived.");
    }

    private async Task RunTimedMacroAndReportAsync(
        VenueConnectionConfiguration venue,
        TimedMacroSummary macro,
        TimedMacroOpeningSummary? opening)
    {
        var refresh = await TimedMacros.LoadAsync(venue, true, LifetimeToken);
        if (!refresh.Success || refresh.Value is null)
        {
            ReportVipFailure(
                refresh.Failure,
                "The latest timed-macro state could not be loaded, so the macro was not started.");
            return;
        }

        var currentOpening = refresh.Value.CurrentOpening;

        var currentMacro = refresh.Value.Macros.FirstOrDefault(value =>
            value.TimedMacroId == macro.TimedMacroId);
        if (currentMacro is null || !currentMacro.CanExecute)
        {
            ChatGui.PrintError("You no longer have permission to execute this timed macro.", "PartyPulse");
            return;
        }

        if (!currentMacro.Enabled || !currentMacro.IsConfigured)
        {
            ChatGui.PrintError("The timed macro is disabled or not configured.", "PartyPulse");
            return;
        }

        if (currentMacro.RequiresActiveOpening)
        {
            if (currentOpening is null || opening is null || currentOpening.OpeningId != opening.OpeningId)
            {
                ChatGui.PrintError("The venue opening changed. Refresh the timed-macro data and try again.", "PartyPulse");
                return;
            }

            if (!LocationProvider.IsAtAddress(
                    currentOpening.AddressWorldName,
                    currentOpening.AddressCityName,
                    currentOpening.AddressWard,
                    currentOpening.AddressPlot,
                    out var locationMessage))
            {
                ChatGui.PrintError($"This timed macro only runs at the active opening address. {locationMessage}", "PartyPulse");
                return;
            }
        }

        var execution = await gameMacroExecutionService.ExecuteUntargetedAsync(
            currentMacro.MacroText!,
            LifetimeToken);
        if (!execution.Success)
        {
            ChatGui.PrintError($"{execution.ErrorMessage} [{execution.ErrorCode}]", "PartyPulse");
            return;
        }

        var clientExecutionId = Guid.NewGuid();
        var result = await TimedMacros.RecordExecutionAsync(
            venue,
            currentMacro.TimedMacroId,
            new RecordTimedMacroExecutionRequest(currentMacro.RequiresActiveOpening ? currentOpening!.OpeningId : null, clientExecutionId),
            LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(
                result.Failure,
                "The macro started, but the shared timer could not be reset. Refresh and verify the timer before running it again.");
            return;
        }

        ChatGui.Print(
            $"Started {currentMacro.DisplayName}. Shared timer reset until {VenueTimeZone.Format(venue, result.Value.NextDueAt, "t")}.",
            "PartyPulse");
    }

    private async Task CreateVipPerkAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateVipPerkRequest request)
    {
        var result = await VipPerks.CreateAsync(venue, request, LifetimeToken);
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The VIP perk could not be created.");
            return;
        }

        await RefreshPhotoshootsIfLoadedAsync(venue);
        await RefreshOtherSalesIfLoadedAsync(venue);
        await RefreshOtherGamesIfLoadedAsync(venue);
        ChatGui.Print($"Created VIP perk '{request.Name}'.", "PartyPulse");
    }

    private async Task UpdateVipPerkAndReportAsync(
        VenueConnectionConfiguration venue,
        int perkId,
        UpdateVipPerkRequest request)
    {
        var result = await VipPerks.UpdateAsync(venue, perkId, request, LifetimeToken);
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The VIP perk could not be updated.");
            return;
        }

        await RefreshPhotoshootsIfLoadedAsync(venue);
        await RefreshOtherSalesIfLoadedAsync(venue);
        await RefreshOtherGamesIfLoadedAsync(venue);
        ChatGui.Print($"Updated VIP perk '{request.Name}'.", "PartyPulse");
    }

    private async Task SetVipPackagePerkAndReportAsync(
        VenueConnectionConfiguration venue,
        int packageId,
        int perkId,
        SetVipPackagePerkRequest request)
    {
        var result = await VipPerks.SetPackagePerkAsync(
            venue,
            packageId,
            perkId,
            request,
            LifetimeToken);
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The VIP package perk assignment could not be updated.");
            return;
        }

        await RefreshPhotoshootsIfLoadedAsync(venue);
        await RefreshOtherSalesIfLoadedAsync(venue);
        await RefreshOtherGamesIfLoadedAsync(venue);
        ChatGui.Print(
            request.Assigned ? "VIP perk assigned to package." : "VIP perk removed from package.",
            "PartyPulse");
    }

    private async Task CreatePhotoshootPackageAndReportAsync(
        VenueConnectionConfiguration venue,
        CreatePhotoshootPackageRequest request)
    {
        var result = await Photoshoots.CreatePackageAsync(venue, request, LifetimeToken);
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The photoshoot package could not be created.");
            return;
        }

        ChatGui.Print($"Created photoshoot package '{request.Name}'.", "PartyPulse");
    }

    private async Task UpdatePhotoshootPackageAndReportAsync(
        VenueConnectionConfiguration venue,
        int packageId,
        UpdatePhotoshootPackageRequest request)
    {
        var result = await Photoshoots.UpdatePackageAsync(
            venue,
            packageId,
            request,
            LifetimeToken);
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The photoshoot package could not be updated.");
            return;
        }

        ChatGui.Print($"Updated photoshoot package '{request.Name}'.", "PartyPulse");
    }

    private async Task UpdatePhotoshootSettingsAndReportAsync(
        VenueConnectionConfiguration venue,
        UpdatePhotoshootSettingsRequest request)
    {
        var result = await Photoshoots.UpdateSettingsAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The photoshoot seller percentage could not be updated.");
            return;
        }

        ChatGui.Print(
            $"Photoshoot sellers now keep {result.Value.SellerPercentage:0.##}% of collected gil.",
            "PartyPulse");
    }

    private async Task RefreshPhotoshootsIfLoadedAsync(VenueConnectionConfiguration venue)
    {
        if (Photoshoots.GetSnapshot(venue).Status == PhotoshootManagementStatus.NotLoaded)
        {
            return;
        }

        await Photoshoots.LoadAsync(venue, true, LifetimeToken);
    }

    private async Task RedeemVipPerkAndReportAsync(VenueConnectionConfiguration venue, RedeemVipPerkRequest request)
    {
        var result = await VipPerks.RedeemAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null) { ReportVipFailure(result.Failure, "The VIP perk could not be redeemed."); return; }
        await Photoshoots.LoadAsync(venue, true, LifetimeToken);
        await RefreshOtherSalesIfLoadedAsync(venue);
        await RefreshOtherGamesIfLoadedAsync(venue);
        ChatGui.Print($"Redeemed {result.Value.PerkName} for {request.TargetCharacterName}.", "PartyPulse");
    }

    private async Task UndoVipPerkAndReportAsync(VenueConnectionConfiguration venue, long redemptionId, string? reason)
    {
        var result = await VipPerks.UndoAsync(venue, redemptionId, new UndoVipPerkRedemptionRequest(reason), LifetimeToken);
        if (!result.Success) { ReportVipFailure(result.Failure, "The VIP perk redemption could not be undone."); return; }
        await Photoshoots.LoadAsync(venue, true, LifetimeToken);
        await RefreshOtherSalesIfLoadedAsync(venue);
        await RefreshOtherGamesIfLoadedAsync(venue);
        ChatGui.Print($"VIP perk redemption #{redemptionId} was undone.", "PartyPulse");
    }

    private async Task SellPhotoshootAndReportAsync(VenueConnectionConfiguration venue, SellPhotoshootRequest request)
    {
        var result = await Photoshoots.SellAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null) { ReportVipFailure(result.Failure, "The photoshoot sale could not be recorded."); return; }
        await VipPerks.LoadAsync(venue, true, LifetimeToken);
        await Finance.LoadAsync(venue, true, LifetimeToken);
        var cost = result.Value.BaseCostType == "vip_perk"
            ? $"VIP perk {result.Value.PricePerkName}" + (result.Value.TotalGil > 0 ? $" plus {result.Value.TotalGil:N0} gil" : string.Empty)
            : $"{result.Value.TotalGil:N0} gil";
        ChatGui.Print(
            $"Recorded photoshoot sale #{result.Value.SaleId} to {result.Value.BuyerCharacterName} for {cost}. " +
            $"Seller keeps {result.Value.SellerShareGil:N0} gil; {result.Value.VenueShareGil:N0} gil is owed to the venue.",
            "PartyPulse");
    }

    private async Task SetPhotoshootSalePaymentStatusAndReportAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        SetPhotoshootSalePaymentStatusRequest request)
    {
        var result = await Photoshoots.SetSalePaymentStatusAsync(
            venue,
            saleId,
            request,
            LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The photoshoot payment status could not be updated.");
            return;
        }

        await Finance.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print(
            request.Settled
                ? $"Photoshoot sale #{saleId} was marked settled."
                : $"Photoshoot sale #{saleId} was marked unpaid.",
            "PartyPulse");
    }

    private async Task CancelPhotoshootSaleAndReportAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        CancelPhotoshootSaleRequest request)
    {
        var result = await Photoshoots.CancelSaleAsync(
            venue,
            saleId,
            request,
            LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The photoshoot sale could not be cancelled.");
            return;
        }

        await VipPerks.LoadAsync(venue, true, LifetimeToken);
        await Finance.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print(
            result.Value.ReleasedPerkRedemptionId is { } redemptionId
                ? $"Photoshoot sale #{saleId} was cancelled and VIP perk redemption #{redemptionId} was restored."
                : $"Photoshoot sale #{saleId} was cancelled.",
            "PartyPulse");
    }

    private async Task CreateOtherSaleItemAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateOtherSaleItemRequest request)
    {
        var result = await OtherSales.CreateItemAsync(venue, request, LifetimeToken);
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The Other Sales item could not be created.");
            return;
        }

        ChatGui.Print($"Created Other Sales item '{request.Name}'.", "PartyPulse");
    }

    private async Task UpdateOtherSaleItemAndReportAsync(
        VenueConnectionConfiguration venue,
        int itemId,
        UpdateOtherSaleItemRequest request)
    {
        var result = await OtherSales.UpdateItemAsync(venue, itemId, request, LifetimeToken);
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The Other Sales item could not be updated.");
            return;
        }

        ChatGui.Print($"Updated Other Sales item '{request.Name}'.", "PartyPulse");
    }

    private async Task UpdateOtherSaleSellerPercentageAndReportAsync(
        VenueConnectionConfiguration venue,
        int itemId,
        UpdateOtherSaleSellerPercentageRequest request)
    {
        var result = await OtherSales.UpdateSellerPercentageAsync(venue, itemId, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The seller percentage could not be updated.");
            return;
        }

        ChatGui.Print(
            $"Seller keeps {result.Value.SellerPercentage:0.##}% for Other Sales item #{itemId}.",
            "PartyPulse");
    }

    private async Task SellOtherSaleAndReportAsync(
        VenueConnectionConfiguration venue,
        SellOtherSaleRequest request)
    {
        var result = await OtherSales.SellAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The Other Sale could not be recorded.");
            return;
        }

        await VipPerks.LoadAsync(venue, true, LifetimeToken);
        await Finance.LoadAsync(venue, true, LifetimeToken);
        var price = result.Value.PriceType == "vip_perk"
            ? $"VIP perk {result.Value.PricePerkName}"
            : $"{result.Value.TotalGil:N0} gil";
        ChatGui.Print(
            $"Recorded Other Sale #{result.Value.SaleId}: {result.Value.Quantity:N0} × {result.Value.ItemName} " +
            $"to {result.Value.BuyerCharacterName} for {price}. Seller keeps {result.Value.SellerShareGil:N0} gil; " +
            $"{result.Value.VenueShareGil:N0} gil is owed to the venue.",
            "PartyPulse");
    }

    private async Task SetOtherSalePaymentStatusAndReportAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        SetOtherSalePaymentStatusRequest request)
    {
        var result = await OtherSales.SetSalePaymentStatusAsync(venue, saleId, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The Other Sale payment status could not be updated.");
            return;
        }

        await Finance.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print(
            request.Settled
                ? $"Other Sale #{saleId} was marked settled."
                : $"Other Sale #{saleId} was marked unpaid.",
            "PartyPulse");
    }

    private async Task CancelOtherSaleAndReportAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        CancelOtherSaleRequest request)
    {
        var result = await OtherSales.CancelSaleAsync(venue, saleId, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The Other Sale could not be cancelled.");
            return;
        }

        await VipPerks.LoadAsync(venue, true, LifetimeToken);
        await Finance.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print(
            result.Value.ReleasedPerkRedemptionId is { } redemptionId
                ? $"Other Sale #{saleId} was cancelled, the buyer was confirmed refunded, and VIP perk redemption #{redemptionId} was restored."
                : $"Other Sale #{saleId} was cancelled and the buyer was confirmed refunded.",
            "PartyPulse");
    }

    private async Task RefreshOtherSalesIfLoadedAsync(VenueConnectionConfiguration venue)
    {
        if (OtherSales.GetSnapshot(venue).Status == OtherSalesManagementStatus.NotLoaded)
            return;

        await OtherSales.LoadAsync(venue, true, LifetimeToken);
    }

    private async Task CreatePurchaseAndReportAsync(
        VenueConnectionConfiguration venue,
        CreatePurchaseRequest request)
    {
        var result = await Purchases.CreateAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The purchase could not be recorded.");
            return;
        }

        ChatGui.Print(
            string.Equals(result.Value.Status, "settled", StringComparison.Ordinal)
                ? $"Recorded purchase #{result.Value.PurchaseId} as approved and settled."
                : $"Submitted purchase #{result.Value.PurchaseId} for finance approval.",
            "PartyPulse");
    }

    private async Task ApprovePurchaseAndReportAsync(
        VenueConnectionConfiguration venue,
        long purchaseId)
    {
        var result = await Purchases.ApproveAsync(venue, purchaseId, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The purchase could not be approved.");
            return;
        }

        ChatGui.Print(
            $"Approved purchase #{purchaseId}. Target the purchaser and use Pay with Dropbox from Purchases.",
            "PartyPulse");
    }

    private async Task StartPurchasePaymentAndReportAsync(
        VenueConnectionConfiguration venue,
        PurchaseSummary purchase)
    {
        if (!string.Equals(purchase.Status, "approved", StringComparison.Ordinal))
        {
            ChatGui.PrintError("Only approved, unpaid purchases can be paid.", "PartyPulse");
            return;
        }

        var readiness = await settlementTradeService.CheckReadyAsync(
            purchase.CreatedByCharacterName,
            purchase.CreatedByWorldName,
            LifetimeToken);
        if (!readiness.Success)
        {
            ReportPluginIntegrationFailure(
                readiness.Failure,
                "Dropbox is unavailable or the purchaser is not currently targeted.");
            return;
        }

        var trade = await settlementTradeService.InitiateTradeAsync(
            purchase.CreatedByCharacterName,
            purchase.CreatedByWorldName,
            purchase.TotalPriceGil,
            LifetimeToken);
        if (!trade.Success)
        {
            ReportPluginIntegrationFailure(
                trade.Failure,
                "Dropbox did not start the purchase reimbursement trade.");
            return;
        }

        ChatGui.Print(
            $"Started a {purchase.TotalPriceGil:N0} gil reimbursement to {purchase.CreatedByCharacterName}. Confirm trade success from Purchases after it completes.",
            "PartyPulse");
    }

    private async Task ConfirmPurchasePaidAndReportAsync(
        VenueConnectionConfiguration venue,
        long purchaseId)
    {
        var result = await Purchases.ConfirmPaidAsync(venue, purchaseId, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The purchase could not be marked settled.");
            return;
        }

        ChatGui.Print($"Purchase #{purchaseId} was confirmed paid and settled.", "PartyPulse");
    }

    private async Task RejectPurchaseAndReportAsync(
        VenueConnectionConfiguration venue,
        long purchaseId,
        RejectPurchaseRequest request)
    {
        var result = await Purchases.RejectAsync(venue, purchaseId, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The purchase could not be rejected.");
            return;
        }

        ChatGui.Print($"Purchase #{purchaseId} was rejected.", "PartyPulse");
    }

    private async Task CancelPurchaseAndReportAsync(
        VenueConnectionConfiguration venue,
        long purchaseId,
        bool wasSettled)
    {
        var result = await Purchases.CancelAsync(
            venue,
            purchaseId,
            new CancelPurchaseRequest(wasSettled),
            LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The purchase could not be cancelled.");
            return;
        }

        ChatGui.Print(
            wasSettled
                ? $"Purchase #{purchaseId} was cancelled and the reimbursement was recorded as repaid to the club."
                : $"Purchase #{purchaseId} was cancelled.",
            "PartyPulse");
    }

    private async Task CreateOtherGameItemAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateOtherGameItemRequest request)
    {
        var result = await OtherGames.CreateItemAsync(venue, request, LifetimeToken);
        if (!result.Success) { ReportVipFailure(result.Failure, "The Other Games item could not be created."); return; }
        ChatGui.Print($"Created Other Games item '{request.Name}'.", "PartyPulse");
    }

    private async Task UpdateOtherGameItemAndReportAsync(
        VenueConnectionConfiguration venue,
        int itemId,
        UpdateOtherGameItemRequest request)
    {
        var result = await OtherGames.UpdateItemAsync(venue, itemId, request, LifetimeToken);
        if (!result.Success) { ReportVipFailure(result.Failure, "The Other Games item could not be updated."); return; }
        ChatGui.Print($"Updated Other Games item '{request.Name}'.", "PartyPulse");
    }

    private async Task UpdateOtherGameSellerPercentageAndReportAsync(
        VenueConnectionConfiguration venue,
        int itemId,
        UpdateOtherGameSellerPercentageRequest request)
    {
        var result = await OtherGames.UpdateSellerPercentageAsync(venue, itemId, request, LifetimeToken);
        if (!result.Success || result.Value is null) { ReportVipFailure(result.Failure, "The seller percentage could not be updated."); return; }
        ChatGui.Print($"Seller keeps {result.Value.SellerPercentage:0.##}% for Other Games item #{itemId}.", "PartyPulse");
    }

    private async Task SellOtherGameAndReportAsync(
        VenueConnectionConfiguration venue,
        SellOtherGameRequest request)
    {
        var result = await OtherGames.SellAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null) { ReportVipFailure(result.Failure, "The Other Game sale could not be recorded."); return; }
        await VipPerks.LoadAsync(venue, true, LifetimeToken);
        await Finance.LoadAsync(venue, true, LifetimeToken);
        var price = result.Value.PriceType == "vip_perk" ? $"VIP perk {result.Value.PricePerkName}" : $"{result.Value.TotalGil:N0} gil";
        ChatGui.Print(
            $"Recorded Other Game sale #{result.Value.SaleId}: {result.Value.Quantity:N0} × {result.Value.ItemName} " +
            $"to {result.Value.BuyerCharacterName} for {price}. Record the outcome from Game history.",
            "PartyPulse");
    }

    private async Task SetOtherGameOutcomeAndReportAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        SetOtherGameOutcomeRequest request)
    {
        var result = await OtherGames.SetOutcomeAsync(venue, saleId, request, LifetimeToken);
        if (!result.Success || result.Value is null) { ReportVipFailure(result.Failure, "The game outcome could not be recorded."); return; }
        await Finance.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print(
            result.Value.OutcomeStatus == "no_win"
                ? $"Other Game sale #{saleId} was marked no win. Net venue balance: {result.Value.NetVenueGil:N0} gil."
                : $"Other Game sale #{saleId} win recorded: {result.Value.WinAmountGil:N0} gil. Net venue balance: {result.Value.NetVenueGil:N0} gil.",
            "PartyPulse");
    }

    private async Task SetOtherGameSettlementStatusAndReportAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        SetOtherGameSettlementStatusRequest request)
    {
        var result = await OtherGames.SetSaleSettlementStatusAsync(venue, saleId, request, LifetimeToken);
        if (!result.Success || result.Value is null) { ReportVipFailure(result.Failure, "The Other Game settlement status could not be updated."); return; }
        await Finance.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print(request.Settled ? $"Other Game sale #{saleId} was marked settled." : $"Other Game sale #{saleId} was marked unsettled.", "PartyPulse");
    }

    private async Task CancelOtherGameAndReportAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        CancelOtherGameSaleRequest request)
    {
        var result = await OtherGames.CancelSaleAsync(venue, saleId, request, LifetimeToken);
        if (!result.Success || result.Value is null) { ReportVipFailure(result.Failure, "The Other Game sale could not be cancelled."); return; }
        await VipPerks.LoadAsync(venue, true, LifetimeToken);
        await Finance.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print(
            result.Value.ReleasedPerkRedemptionId is { } redemptionId
                ? $"Other Game sale #{saleId} was cancelled, the buyer was confirmed refunded, and VIP perk redemption #{redemptionId} was restored."
                : $"Other Game sale #{saleId} was cancelled and the buyer was confirmed refunded.",
            "PartyPulse");
    }

    private async Task RefreshOtherGamesIfLoadedAsync(VenueConnectionConfiguration venue)
    {
        if (OtherGames.GetSnapshot(venue).Status == OtherGamesManagementStatus.NotLoaded) return;
        await OtherGames.LoadAsync(venue, true, LifetimeToken);
    }

    private async Task CreateOtherGamesSettlementAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateOtherGamesSettlementRequest request)
    {
        var result = await Finance.CreateOtherGamesSettlementAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The Other Games settlement could not be created.");
            return;
        }

        await OtherGames.LoadAsync(venue, true, LifetimeToken);
        Notifications.PollSoon();

        if (result.Value.AmountGil > 0)
        {
            var readiness = await settlementTradeService.CheckReadyAsync(
                result.Value.TargetCharacterName,
                result.Value.TargetWorldName,
                LifetimeToken);
            if (!readiness.Success)
            {
                ReportPluginIntegrationFailure(
                    readiness.Failure,
                    $"Settlement #{result.Value.SettlementId} was created, but Dropbox is unavailable. Open it from Finance when ready.");
                return;
            }

            var trade = await settlementTradeService.InitiateTradeAsync(
                result.Value.TargetCharacterName,
                result.Value.TargetWorldName,
                result.Value.AmountGil,
                LifetimeToken);
            if (!trade.Success)
            {
                ReportPluginIntegrationFailure(trade.Failure, $"Settlement #{result.Value.SettlementId} was created, but Dropbox did not start the trade.");
                return;
            }

            ChatGui.Print($"Created Other Games settlement #{result.Value.SettlementId}. Trade {result.Value.AmountGil:N0} gil to the finance manager.", "PartyPulse");
            return;
        }

        ChatGui.Print($"Created zero-net Other Games settlement #{result.Value.SettlementId}; no trade is required.", "PartyPulse");
    }

    private async Task CreateOtherGamesPayoutAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateOtherGamesPayoutRequest request)
    {
        var result = await Finance.CreateOtherGamesPayoutAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The Other Games seller payout could not be created.");
            return;
        }

        await OtherGames.LoadAsync(venue, true, LifetimeToken);
        Notifications.PollSoon();

        var amount = Math.Abs(result.Value.AmountGil);
        var readiness = await settlementTradeService.CheckReadyAsync(
            result.Value.TargetCharacterName,
            result.Value.TargetWorldName,
            LifetimeToken);
        if (!readiness.Success)
        {
            ReportPluginIntegrationFailure(
                readiness.Failure,
                $"Payout settlement #{result.Value.SettlementId} was created, but Dropbox is unavailable. Open it from Finance when ready.");
            return;
        }

        var trade = await settlementTradeService.InitiateTradeAsync(
            result.Value.TargetCharacterName,
            result.Value.TargetWorldName,
            amount,
            LifetimeToken);
        if (!trade.Success)
        {
            ReportPluginIntegrationFailure(
                trade.Failure,
                $"Payout settlement #{result.Value.SettlementId} was created, but Dropbox did not start the trade.");
            return;
        }

        ChatGui.Print(
            $"Created Other Games payout settlement #{result.Value.SettlementId} and started a {amount:N0} gil trade to {result.Value.TargetCharacterName}. Confirm it from Finance after the trade completes.",
            "PartyPulse");
    }

    private async Task TradeOtherGamesSellerAndReportAsync(
        VenueConnectionConfiguration venue,
        FinancialSettlementSummary settlement)
    {
        if (!settlement.IsPending || settlement.SettlementType != "other_games" || settlement.AmountGil >= 0)
        {
            ChatGui.PrintError("This settlement is not a pending Other Games venue-to-seller payout.", "PartyPulse");
            return;
        }

        var amount = Math.Abs(settlement.AmountGil);
        var readiness = await settlementTradeService.CheckReadyAsync(
            settlement.TargetCharacterName,
            settlement.TargetWorldName,
            LifetimeToken);
        if (!readiness.Success)
        {
            ReportPluginIntegrationFailure(readiness.Failure, "Dropbox is unavailable or the seller is not currently targeted.");
            return;
        }

        var trade = await settlementTradeService.InitiateTradeAsync(
            settlement.TargetCharacterName,
            settlement.TargetWorldName,
            amount,
            LifetimeToken);
        if (!trade.Success)
        {
            ReportPluginIntegrationFailure(trade.Failure, "Dropbox did not start the venue payout trade.");
            return;
        }

        ChatGui.Print($"Started a {amount:N0} gil venue payout to {settlement.TargetCharacterName}. Confirm the settlement after the trade completes.", "PartyPulse");
    }

    private async Task BarMutationAndReportAsync<T>(
        VenueConnectionConfiguration venue,
        Task<ApiResult<T>> operation,
        string successMessage)
    {
        var result = await operation;
        if (!result.Success)
        {
            ReportVipFailure(result.Failure, "The bar operation could not be completed.");
            return;
        }

        await Finance.LoadAsync(venue, true, LifetimeToken);
        await TimedMacros.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print(successMessage, "PartyPulse");
    }

    private async Task SellBarBuyoutAndReportAsync(VenueConnectionConfiguration venue, SellBarBuyoutRequest request)
    {
        var result = await Bar.SellBuyoutAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The bar buyout could not be recorded.");
            return;
        }
        await Finance.LoadAsync(venue, true, LifetimeToken);
        await TimedMacros.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print($"Recorded bar buyout #{result.Value.SaleId} until {VenueTimeZone.Format(venue, result.Value.EndsAt, "t")}.", "PartyPulse");
    }

    private async Task StartGambaGameAndReportAsync(VenueConnectionConfiguration venue, int startingJackpotGil)
    {
        var result = await Bar.StartGambaGameAsync(venue, new StartGambaGameRequest(startingJackpotGil), LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The Gamba Shot game could not be started.");
            return;
        }
        await TimedMacros.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print($"Started Gamba Shot #{result.Value.GameId} with {result.Value.CurrentJackpotGil:N0} gil jackpot.", "PartyPulse");
    }

    private async Task SellGambaTicketsAndReportAsync(VenueConnectionConfiguration venue, SellGambaTicketsRequest request)
    {
        var result = await Bar.SellGambaTicketsAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The Gamba Shot ticket sale could not be recorded.");
            return;
        }
        await Finance.LoadAsync(venue, true, LifetimeToken);
        await TimedMacros.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print($"Recorded {result.Value.Quantity:N0} Gamba Shot ticket(s) for {result.Value.GrossGil:N0} gil. Jackpot is now {result.Value.CurrentJackpotGil:N0} gil.", "PartyPulse");
    }

    private async Task CompleteGambaGameAndReportAsync(VenueConnectionConfiguration venue, long gameId, CompleteGambaGameRequest request)
    {
        var result = await Bar.CompleteGambaGameAsync(venue, gameId, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            if (string.Equals(result.Failure?.Code, "REQUEST_TIMEOUT", StringComparison.OrdinalIgnoreCase))
            {
                var refresh = await Bar.LoadAsync(venue, true, LifetimeToken);
                var completed = refresh.Value?.GambaGameHistory.FirstOrDefault(game =>
                    game.GameId == gameId &&
                    string.Equals(game.Status, "won", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(game.WinnerCharacterName, request.WinnerCharacterName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(game.WinnerWorldName, request.WinnerWorldName, StringComparison.OrdinalIgnoreCase));
                if (completed is not null)
                {
                    await TimedMacros.LoadAsync(venue, true, LifetimeToken);
                    ChatGui.Print(
                        $"Gamba Shot #{gameId} was confirmed despite the request timeout: {completed.WinnerCharacterName} @ {completed.WinnerWorldName} won {completed.FinalJackpotGil.GetValueOrDefault(completed.CurrentJackpotGil):N0} gil.",
                        "PartyPulse");
                    return;
                }
            }

            ReportVipFailure(result.Failure, "The Gamba Shot winner could not be confirmed.");
            return;
        }
        await TimedMacros.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print($"Gamba Shot #{gameId} won by {result.Value.WinnerCharacterName} @ {result.Value.WinnerWorldName} for {result.Value.FinalJackpotGil:N0} gil.", "PartyPulse");
    }

    private async Task CancelGambaGameAndReportAsync(
        VenueConnectionConfiguration venue,
        long gameId,
        CancelGambaGameRequest request)
    {
        var result = await Bar.CancelGambaGameAsync(venue, gameId, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The Gamba Shot session could not be cancelled.");
            return;
        }

        await Finance.LoadAsync(venue, true, LifetimeToken);
        await TimedMacros.LoadAsync(venue, true, LifetimeToken);
        ChatGui.Print(
            $"Cancelled Gamba Shot #{gameId} and cancelled {result.Value.CancelledTicketSaleCount:N0} ticket sale(s).",
            "PartyPulse");
    }

    private async Task CreateBarSettlementAndReportAsync(VenueConnectionConfiguration venue, CreateBarSettlementRequest request)
    {
        var readiness = await settlementTradeService.CheckReadyAsync(request.TargetCharacterName, request.TargetWorldName, LifetimeToken);
        if (!readiness.Success)
        {
            ReportPluginIntegrationFailure(readiness.Failure, "The bar settlement was not created because Dropbox is unavailable.");
            return;
        }
        var result = await Finance.CreateBarSettlementAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The bar settlement could not be created.");
            return;
        }
        var trade = await settlementTradeService.InitiateTradeAsync(result.Value.TargetCharacterName, result.Value.TargetWorldName, result.Value.AmountGil, LifetimeToken);
        await Bar.LoadAsync(venue, true, LifetimeToken);
        Notifications.PollSoon();
        if (!trade.Success)
        {
            ReportPluginIntegrationFailure(trade.Failure, $"Settlement #{result.Value.SettlementId} was created, but Dropbox did not start the trade.");
            return;
        }
        ChatGui.Print($"Created bar settlement #{result.Value.SettlementId} for {result.Value.AmountGil:N0} gil.", "PartyPulse");
    }

    private async Task CreatePhotoshootSettlementAndReportAsync(VenueConnectionConfiguration venue, CreatePhotoshootSettlementRequest request)
    {
        var readiness = await settlementTradeService.CheckReadyAsync(request.TargetCharacterName, request.TargetWorldName, LifetimeToken);
        if (!readiness.Success) { ReportPluginIntegrationFailure(readiness.Failure, "The settlement was not created because Dropbox is unavailable."); return; }
        var result = await Finance.CreatePhotoshootSettlementAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null) { ReportVipFailure(result.Failure, "The photoshoot settlement could not be created."); return; }
        var trade = await settlementTradeService.InitiateTradeAsync(result.Value.TargetCharacterName, result.Value.TargetWorldName, result.Value.AmountGil, LifetimeToken);
        await Photoshoots.LoadAsync(venue, true, LifetimeToken);
        Notifications.PollSoon();
        if (!trade.Success) { ReportPluginIntegrationFailure(trade.Failure, $"Settlement #{result.Value.SettlementId} was created, but Dropbox did not start the trade."); return; }
        ChatGui.Print($"Created photoshoot settlement #{result.Value.SettlementId} for {result.Value.AmountGil:N0} gil.", "PartyPulse");
    }

    private async Task CreateOtherSalesSettlementAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateOtherSalesSettlementRequest request)
    {
        var readiness = await settlementTradeService.CheckReadyAsync(
            request.TargetCharacterName,
            request.TargetWorldName,
            LifetimeToken);
        if (!readiness.Success)
        {
            ReportPluginIntegrationFailure(
                readiness.Failure,
                "The settlement was not created because Dropbox is unavailable.");
            return;
        }

        var result = await Finance.CreateOtherSalesSettlementAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The Other Sales settlement could not be created.");
            return;
        }

        var trade = await settlementTradeService.InitiateTradeAsync(
            result.Value.TargetCharacterName,
            result.Value.TargetWorldName,
            result.Value.AmountGil,
            LifetimeToken);
        await OtherSales.LoadAsync(venue, true, LifetimeToken);
        Notifications.PollSoon();
        if (!trade.Success)
        {
            ReportPluginIntegrationFailure(
                trade.Failure,
                $"Settlement #{result.Value.SettlementId} was created, but Dropbox did not start the trade.");
            return;
        }

        ChatGui.Print(
            $"Created Other Sales settlement #{result.Value.SettlementId} for {result.Value.AmountGil:N0} gil.",
            "PartyPulse");
    }

    private async Task CreateVipSettlementAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateVipSettlementRequest request)
    {
        var readiness = await settlementTradeService.CheckReadyAsync(
            request,
            LifetimeToken);
        if (!readiness.Success)
        {
            if (readiness.Failure?.Kind == PluginIntegrationFailureKind.Cancelled &&
                LifetimeToken.IsCancellationRequested)
            {
                return;
            }

            ReportPluginIntegrationFailure(
                readiness.Failure,
                "The settlement was not created because Dropbox is unavailable.");
            return;
        }

        var result = await Finance.CreateVipSettlementAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The settlement transaction could not be created.");
            return;
        }

        var tradeResult = await settlementTradeService.InitiateTradeAsync(
            result.Value,
            LifetimeToken);
        await Vip.LoadAsync(venue, true, LifetimeToken);
        Notifications.PollSoon();

        if (!tradeResult.Success)
        {
            if (tradeResult.Failure?.Kind == PluginIntegrationFailureKind.Cancelled &&
                LifetimeToken.IsCancellationRequested)
            {
                return;
            }

            ChatGui.PrintError(
                $"Pending settlement #{result.Value.SettlementId} was created, but Dropbox did not start the trade: " +
                $"{FormatPluginIntegrationFailure(tradeResult.Failure, "Unknown Dropbox error")} " +
                "Complete the trade manually or have the collector reject the pending settlement.",
                "PartyPulse");
            return;
        }

        ChatGui.Print(
            $"Created pending settlement #{result.Value.SettlementId} for {result.Value.AmountGil:N0} gil with " +
            $"{result.Value.TargetUserDisplayName}. Dropbox was instructed to begin the trade; the collector must still confirm the payment.",
            "PartyPulse");
    }

    private async Task RespondSettlementAndReportAsync(
        VenueConnectionConfiguration venue,
        long settlementId,
        RespondSettlementRequest request)
    {
        var result = await Finance.RespondSettlementAsync(
            venue,
            settlementId,
            request,
            LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The settlement transaction could not be resolved.");
            return;
        }

        await Vip.LoadAsync(venue, true, LifetimeToken);
        await Photoshoots.LoadAsync(venue, true, LifetimeToken);
        await RefreshOtherSalesIfLoadedAsync(venue);
        await RefreshOtherGamesIfLoadedAsync(venue);
        await VipPerks.LoadAsync(venue, true, LifetimeToken);
        Notifications.PollSoon();
        ChatGui.Print(
            $"Settlement #{settlementId} was {result.Value.Status}.",
            "PartyPulse");
    }

    private async Task MarkNotificationSeenAsync(
        QueuedPartyPulseNotification queued,
        bool dismissed)
    {
        var venue = Configuration.VenueConnections.FirstOrDefault(
            value => value.ProfileId == queued.VenueProfileId);
        if (venue is null)
        {
            return;
        }

        var result = await Notifications.MarkSeenAsync(
            venue,
            queued.Notification.NotificationId,
            dismissed,
            LifetimeToken);
        if (!result.Success)
        {
            Log.Warning(
                "Could not mark notification {NotificationId} seen: {Code} {Message}",
                queued.Notification.NotificationId,
                result.Failure?.Code,
                result.Failure?.Message);
        }
    }

    private static async Task ReportApiResultAsync<T>(Task<ApiResult<T>> operation, string successMessage)
    {
        var result = await operation;
        if (result.Success)
            ChatGui.Print(successMessage, "PartyPulse");
        else
            ChatGui.PrintError(result.Failure?.Message ?? "The operation failed.", "PartyPulse");
    }

    private async Task UpdateCourtSettingsAndReportAsync(
        VenueConnectionConfiguration venue,
        UpdateCourtSettingsRequest request)
    {
        var result = await Court.UpdateSettingsAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ChatGui.PrintError(
                result.Failure?.Message ?? "The Court Service retained percentage could not be updated.",
                "PartyPulse");
            return;
        }

        ChatGui.Print(
            $"Court workers now keep {result.Value.CourtKeepPercentage:0.##}% of gil Court Service sales.",
            "PartyPulse");
    }

    private async Task PreviewCourtStaffSettlementAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateCourtStaffSettlementRequest request)
    {
        var result = await Court.PreviewStaffSettlementAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ChatGui.PrintError(
                result.Failure?.Message ?? "The Court settlement preview could not be calculated.",
                "PartyPulse");
        }
    }

    private async Task CancelCourtSaleAndReportAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        bool refundConfirmed,
        string? reason)
    {
        var result = await Court.CancelSaleAsync(
            venue,
            saleId,
            new CancelCourtSaleRequest(refundConfirmed, reason),
            LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ChatGui.PrintError(result.Failure?.Message ?? "The Court Service sale could not be cancelled.", "PartyPulse");
            return;
        }

        var refund = result.Value.RefundConfirmedAt is not null
            ? $" Full client refund of {result.Value.RefundedGil:N0} gil was confirmed."
            : string.Empty;
        ChatGui.Print($"Court Service sale #{saleId} cancelled.{refund}", "PartyPulse");
        await Staff.LoadAsync(venue, true, LifetimeToken);
    }

    private async Task CancelStaffTimeEntryAndReportAsync(
        VenueConnectionConfiguration venue,
        long timeEntryId,
        string? reason)
    {
        var result = await Staff.CancelTimeEntryAsync(
            venue,
            timeEntryId,
            new CancelStaffTimeEntryRequest(reason),
            LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ChatGui.PrintError(result.Failure?.Message ?? "The Staff time entry could not be cancelled.", "PartyPulse");
            return;
        }

        var deduction = result.Value.AdjustmentGil == 0
            ? string.Empty
            : $" {result.Value.AdjustmentGil:N0} gil will be deducted from the next Staff salary balance.";
        ChatGui.Print($"Time entry #{timeEntryId} cancelled.{deduction}", "PartyPulse");
        await Staff.LoadAsync(venue, true, LifetimeToken);
        await Court.LoadAsync(venue, true, LifetimeToken);
    }

    private async Task ConfirmCourtTransactionAndReportAsync(
        VenueConnectionConfiguration venue,
        long transactionId)
    {
        var result = await Court.ConfirmTransactionAsync(
            venue,
            transactionId,
            new ConfirmCourtTransactionRequest(null),
            LifetimeToken);
        if (!result.Success)
        {
            ChatGui.PrintError(result.Failure?.Message ?? "The Court transaction could not be confirmed.", "PartyPulse");
            return;
        }

        ChatGui.Print($"Court financial transaction #{transactionId} confirmed.", "PartyPulse");
        await Staff.LoadAsync(venue, true, LifetimeToken);
    }

    private async Task CancelCourtTransactionAndReportAsync(
        VenueConnectionConfiguration venue,
        long transactionId,
        string? reason)
    {
        var result = await Court.CancelTransactionAsync(
            venue,
            transactionId,
            new CancelCourtTransactionRequest(reason),
            LifetimeToken);
        if (!result.Success)
        {
            ChatGui.PrintError(result.Failure?.Message ?? "The Court transaction could not be cancelled.", "PartyPulse");
            return;
        }

        ChatGui.Print(
            $"Court financial transaction #{transactionId} cancelled and its source rows released.",
            "PartyPulse");
        await Staff.LoadAsync(venue, true, LifetimeToken);
    }

    private async Task ReportCourtTransactionAsync(
        VenueConnectionConfiguration venue,
        Task<ApiResult<CourtFinancialTransactionResponse>> operation)
    {
        var result = await operation;
        if (!result.Success || result.Value is null)
        {
            ChatGui.PrintError(result.Failure?.Message ?? "The Court financial transaction could not be created.", "PartyPulse");
            return;
        }

        var value = result.Value;
        ChatGui.Print(
            $"Court transaction #{value.TransactionId}: gross sales {value.GrossSalesGil:N0}, " +
            $"Court retained {value.CourtRetainedGil:N0}, venue share {value.GrossCourtGil:N0}, " +
            $"adjustments {value.AdjustmentGil:+#,0;-#,0;0}, salary {value.SalaryGil:N0}, " +
            $"trade {value.TradeAmountGil:N0} gil. Use Execute with Dropbox when ready.",
            "PartyPulse");
        await Staff.LoadAsync(venue, true, LifetimeToken);
    }

    private async Task ReportStaffPayoutAsync(
        VenueConnectionConfiguration venue,
        Task<ApiResult<StaffPayoutResponse>> operation)
    {
        var result = await operation;
        if (!result.Success || result.Value is null)
        {
            ChatGui.PrintError(result.Failure?.Message ?? "The Staff payout could not be created.", "PartyPulse");
            return;
        }
        var value = result.Value;
        var summary = value.TradeDirection == "staff_to_collector"
            ? $"Staff balance transaction #{value.TransactionId}: {value.TradeAmountGil:N0} gil received by finance and confirmed."
            : $"Staff payout transaction #{value.TransactionId}: salary {value.SalaryGil:N0}, deductions {value.AdjustmentGil:N0}, net payout {value.TradeAmountGil:N0} gil.";
        ChatGui.Print(summary, "PartyPulse");
        await Staff.LoadAsync(venue, true, LifetimeToken);
        await Court.LoadAsync(venue, true, LifetimeToken);
        if (value.CanExecuteNow && value.TradeAmountGil > 0 && value.TradeTargetCharacterName is not null && value.TradeTargetWorldName is not null)
        {
            var ready = await settlementTradeService.CheckReadyAsync(
                value.TradeTargetCharacterName,
                value.TradeTargetWorldName,
                LifetimeToken);
            if (!ready.Success)
            {
                ReportPluginIntegrationFailure(ready.Failure, "Dropbox is not ready for the Staff payout trade.");
                return;
            }

            var integration = await settlementTradeService.InitiateTradeAsync(
                value.TradeTargetCharacterName,
                value.TradeTargetWorldName,
                value.TradeAmountGil,
                LifetimeToken);
            if (!integration.Success)
                ReportPluginIntegrationFailure(integration.Failure, "Dropbox could not start the Staff payout trade.");
            else
                ChatGui.Print("Dropbox was instructed to begin the Staff payout. Confirm Trade Success after the trade completes.", "PartyPulse");
        }
    }

    private async Task ExecuteCourtTransactionTradeAsync(VenueConnectionConfiguration venue, CourtTransactionSummary transaction)
    {
        if (!transaction.CanExecuteTrade || transaction.TradeAmountGil <= 0 || transaction.TradeTargetCharacterName is null || transaction.TradeTargetWorldName is null)
        {
            ChatGui.PrintError("This Court transaction is not executable by the current user.", "PartyPulse");
            return;
        }
        var ready = await settlementTradeService.CheckReadyAsync(transaction.TradeTargetCharacterName, transaction.TradeTargetWorldName, LifetimeToken);
        if (!ready.Success)
        {
            ReportPluginIntegrationFailure(ready.Failure, "Dropbox is not ready for this Court trade.");
            return;
        }
        var result = await settlementTradeService.InitiateTradeAsync(transaction.TradeTargetCharacterName, transaction.TradeTargetWorldName, transaction.TradeAmountGil, LifetimeToken);
        if (!result.Success)
        {
            ReportPluginIntegrationFailure(result.Failure, "Dropbox could not start the Court trade.");
            return;
        }
        ChatGui.Print($"Dropbox started Court transaction #{transaction.TransactionId}. Confirm Trade Success after the trade completes.", "PartyPulse");
        await Court.LoadAsync(venue, true, LifetimeToken);
    }

    private static void ReportVipFailure(ApiFailure? failure, string fallback) =>
        ChatGui.PrintError(failure?.Message ?? fallback, "PartyPulse");

    private static void ReportPluginIntegrationFailure(
        PluginIntegrationFailure? failure,
        string fallback) =>
        ChatGui.PrintError(FormatPluginIntegrationFailure(failure, fallback), "PartyPulse");

    private static string FormatPluginIntegrationFailure(
        PluginIntegrationFailure? failure,
        string fallback) =>
        failure is null
            ? fallback
            : $"{failure.Message} [{failure.Code}]";

    private static void ReportUserManagementFailure(ApiFailure? failure, string fallback) =>
        ChatGui.PrintError(failure?.Message ?? fallback, "PartyPulse");

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

    private void Observe(Task task, string operation) => _ = ObserveAsync(task, operation);

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
            ChatGui.PrintError("PartyPulse encountered an unexpected error. See the Dalamud log for details.", "PartyPulse");
        }
    }
}
