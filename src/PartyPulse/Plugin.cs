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
using PartyPulse.Finance;
using PartyPulse.Notifications;
using PartyPulse.Models;
using PartyPulse.SelfService;
using PartyPulse.Services;
using PartyPulse.VenueUsers;
using PartyPulse.Vip;
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
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly PartyPulseApiClient apiClient;
    private readonly ConfigWindow configWindow;
    private readonly MainWindow mainWindow;
    private readonly VenueUserEditWindow venueUserEditWindow;
    private readonly VipPlayerEditWindow vipPlayerEditWindow;
    private readonly NotificationToastWindow notificationToastWindow;
    private readonly SettlementTradeService settlementTradeService;

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
        Vip = new VipManagementManager(
            Configuration,
            Authentication,
            apiClient,
            IdentityProvider,
            Log);
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
        settlementTradeService = new SettlementTradeService();

        configWindow = new ConfigWindow(this);
        mainWindow = new MainWindow(this);
        venueUserEditWindow = new VenueUserEditWindow(this);
        vipPlayerEditWindow = new VipPlayerEditWindow(this);
        notificationToastWindow = new NotificationToastWindow(this);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(venueUserEditWindow);
        WindowSystem.AddWindow(vipPlayerEditWindow);
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

    public FinanceManagementManager Finance { get; }

    public NotificationPollingManager Notifications { get; }

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
        venueUserEditWindow.Dispose();
        vipPlayerEditWindow.Dispose();
        notificationToastWindow.Dispose();
        Notifications.Dispose();
        Finance.Dispose();
        Vip.Dispose();
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
        Finance.RemoveProfile(venue.ProfileId);
        Notifications.RemoveProfile(venue.ProfileId);
        Configuration.VenueConnections.RemoveAll(x => x.ProfileId == venue.ProfileId);
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

    public void RefreshVip(VenueConnectionConfiguration venue) =>
        Observe(
            Vip.LoadAsync(venue, true, LifetimeToken),
            $"refresh VIP data for {venue.VenueCode}");

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
                $"Device pairing code for {venue.DisplayLabel}: {result.Value.PairingCode} (expires {result.Value.ExpiresAt.ToLocalTime():g}).",
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
                $"Created venue user '{displayName}'. Invite code: {result.Value.InviteCode} (expires {result.Value.InviteExpiresAt.ToLocalTime():g}).",
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
                $"Recovery code for '{user.DisplayName}': {result.Value.RecoveryCode} (expires {result.Value.RecoveryCodeExpiresAt.ToLocalTime():g}).",
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
                $"Restored venue user '{user.DisplayName}'. Invite code: {result.Value.InviteCode} (expires {result.Value.InviteExpiresAt.ToLocalTime():g}). Permissions remain cleared until reassigned.",
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
                : $"until {result.Value.EndsAt!.Value.ToLocalTime():g}";
            ChatGui.Print(
                $"Sold VIP to {request.CharacterName} @ {request.WorldName} ({period}).",
                "PartyPulse");
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

    private async Task CreateVipSettlementAndReportAsync(
        VenueConnectionConfiguration venue,
        CreateVipSettlementRequest request)
    {
        var result = await Finance.CreateVipSettlementAsync(venue, request, LifetimeToken);
        if (!result.Success || result.Value is null)
        {
            ReportVipFailure(result.Failure, "The settlement transaction could not be created.");
            return;
        }

        await settlementTradeService.InitiateTradeAsync(result.Value, LifetimeToken);
        await Vip.LoadAsync(venue, true, LifetimeToken);
        Notifications.PollSoon();
        ChatGui.Print(
            $"Created pending settlement #{result.Value.SettlementId} for {result.Value.AmountGil:N0} gil with {result.Value.TargetUserDisplayName}. Complete the in-game trade manually.",
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

    private static void ReportVipFailure(ApiFailure? failure, string fallback) =>
        ChatGui.PrintError(failure?.Message ?? fallback, "PartyPulse");

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
