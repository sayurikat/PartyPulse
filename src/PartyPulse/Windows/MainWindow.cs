using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.SelfService;
using PartyPulse.Services;
using PartyPulse.VenueUsers;

namespace PartyPulse.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private enum MainPage
    {
        Overview,
        Openings,
        Djs,
        Greeter,
        Vip,
        Photoshoots,
        Bar,
        Court,
        OtherSales,
        OtherGames,
        Purchases,
        Staff,
        TimedMacros,
        Shoutrunner,
        PartyFinder,
        Finance,
        Users,
        MyAccount,
    }

    private sealed record NavigationItem(
        MainPage Page,
        string Group,
        string Label,
        string Abbreviation);

    private static readonly NavigationItem[] NavigationItems =
    [
        new(MainPage.Overview, "GENERAL", "Overview", "OV"),
        new(MainPage.Openings, "VENUE", "Openings", "OP"),
        new(MainPage.Djs, "VENUE", "DJs", "DJ"),
        new(MainPage.Greeter, "GUESTS & SALES", "Greeter", "GR"),
        new(MainPage.Vip, "GUESTS & SALES", "VIP", "VIP"),
        new(MainPage.Photoshoots, "GUESTS & SALES", "Photoshoots", "PH"),
        new(MainPage.Bar, "GUESTS & SALES", "Bar", "BAR"),
        new(MainPage.Court, "GUESTS & SALES", "Court Services", "CRT"),
        new(MainPage.OtherSales, "GUESTS & SALES", "Other Sales", "SAL"),
        new(MainPage.OtherGames, "GUESTS & SALES", "Other Games", "GM"),
        new(MainPage.Purchases, "GUESTS & SALES", "Purchases", "PUR"),
        new(MainPage.Staff, "OPERATIONS", "Staff", "STF"),
        new(MainPage.TimedMacros, "OPERATIONS", "Timed Macros", "TMR"),
        new(MainPage.Shoutrunner, "OPERATIONS", "Shoutrunner", "SHR"),
        new(MainPage.PartyFinder, "OPERATIONS", "Party Finder", "PF"),
        new(MainPage.Finance, "ADMINISTRATION", "Finance", "FIN"),
        new(MainPage.Users, "ADMINISTRATION", "Users", "USR"),
        new(MainPage.MyAccount, "ADMINISTRATION", "My Account", "ME"),
    ];

    private readonly Plugin plugin;
    private readonly VipTabRenderer vipTab;
    private readonly PhotoshootsTabRenderer photoshootsTab;
    private readonly OtherSalesTabRenderer otherSalesTab;
    private readonly OtherGamesTabRenderer otherGamesTab;
    private readonly PurchasesTabRenderer purchasesTab;
    private readonly BarTabRenderer barTab;
    private readonly CourtTabRenderer courtTab;
    private readonly StaffTabRenderer staffTab;
    private readonly VenueOpeningsTabRenderer venueOpeningsTab;
    private readonly DjsTabRenderer djsTab;
    private readonly TimedMacrosTabRenderer timedMacrosTab;
    private readonly FinanceTabRenderer financeTab;
    private readonly GreeterTabRenderer greeterTab;
    private readonly ShoutrunnerTabRenderer shoutrunnerTab;
    private readonly PartyFinderTabRenderer partyFinderTab;
    private readonly Dictionary<(Guid ProfileId, MainPage Page), DateTimeOffset> activePageRefreshes = new();

    private MainPage selectedPage = MainPage.Overview;
    private long? requestedFinanceSettlementId;
    private Guid addUserProfileId;
    private string addUserDisplayName = string.Empty;
    private string addUserDiscordHandle = string.Empty;
    private VenueConnectionConfiguration? pendingLinkVenue;
    private VenueConnectionConfiguration? pendingUnauthorizeVenue;
    private bool requestOpenUnauthorizePopup;
    private VenueConnectionConfiguration? pendingLocalRemovalVenue;
    private bool requestOpenLocalRemovalPopup;
    private VenueConnectionConfiguration? pendingUnlinkVenue;
    private SelfCharacterSummary? pendingUnlinkCharacter;
    private bool requestOpenUnlinkPopup;

    public MainWindow(Plugin plugin)
        : base("Party Pulse###PartyPulseMain")
    {
        this.plugin = plugin;
        vipTab = new VipTabRenderer(plugin);
        photoshootsTab = new PhotoshootsTabRenderer(plugin);
        otherSalesTab = new OtherSalesTabRenderer(plugin);
        otherGamesTab = new OtherGamesTabRenderer(plugin);
        purchasesTab = new PurchasesTabRenderer(plugin);
        barTab = new BarTabRenderer(plugin);
        courtTab = new CourtTabRenderer(plugin);
        staffTab = new StaffTabRenderer(plugin);
        venueOpeningsTab = new VenueOpeningsTabRenderer(plugin);
        djsTab = new DjsTabRenderer(plugin);
        timedMacrosTab = new TimedMacrosTabRenderer(plugin);
        financeTab = new FinanceTabRenderer(plugin);
        greeterTab = new GreeterTabRenderer(plugin);
        shoutrunnerTab = new ShoutrunnerTabRenderer(plugin);
        partyFinderTab = new PartyFinderTabRenderer(plugin);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void OpenFinance(Guid venueProfileId, long? settlementId)
    {
        var venue = plugin.Configuration.VenueConnections.FirstOrDefault(
            value => value.ProfileId == venueProfileId);
        if (venue is null)
        {
            return;
        }

        plugin.Configuration.SelectedVenueProfileId = venue.ProfileId;
        plugin.Configuration.Save();
        requestedFinanceSettlementId = settlementId;
        selectedPage = MainPage.Finance;
        IsOpen = true;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var selectedVenue = plugin.Configuration.GetSelectedVenue();
        var authenticated = selectedVenue?.IsRegistered == true &&
                            CanDrawAuthenticatedFeatures(plugin.Authentication.GetSnapshot(selectedVenue));

        EnsureSelectedPageVisible(selectedVenue, authenticated);

        var sidebarWidth = (plugin.Configuration.NavigationCollapsed ? 62f : 196f) * ImGuiHelpers.GlobalScale;
        if (ImGui.BeginChild("PartyPulseSidebar", new Vector2(sidebarWidth, 0), true))
        {
            DrawSidebar(selectedVenue, authenticated, sidebarWidth);
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("PartyPulseContent", Vector2.Zero, false))
        {
            selectedVenue = DrawCompactHeader();
            authenticated = selectedVenue?.IsRegistered == true &&
                            CanDrawAuthenticatedFeatures(plugin.Authentication.GetSnapshot(selectedVenue));
            EnsureSelectedPageVisible(selectedVenue, authenticated);
            DrawSelectedPage(selectedVenue, authenticated);
        }
        ImGui.EndChild();

        DrawConfirmationPopups();
    }

    private void DrawSidebar(
        VenueConnectionConfiguration? selectedVenue,
        bool authenticated,
        float sidebarWidth)
    {
        DrawLogo(sidebarWidth, plugin.Configuration.NavigationCollapsed);

        if (!plugin.Configuration.NavigationCollapsed)
        {
            var venueLabel = selectedVenue?.VenueName;
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + sidebarWidth - (20f * ImGuiHelpers.GlobalScale));
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(venueLabel) ? "Party Pulse" : venueLabel);
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
        }

        string? currentGroup = null;
        foreach (var item in NavigationItems)
        {
            if (!IsPageVisible(item.Page, selectedVenue, authenticated))
            {
                continue;
            }

            if (!string.Equals(currentGroup, item.Group, StringComparison.Ordinal))
            {
                currentGroup = item.Group;
                ImGui.Spacing();
                if (plugin.Configuration.NavigationCollapsed)
                {
                    ImGui.Separator();
                }
                else
                {
                    ImGui.TextDisabled(currentGroup);
                }
            }

            var label = plugin.Configuration.NavigationCollapsed
                ? item.Abbreviation
                : item.Label;
            if (item.Page == MainPage.Finance)
            {
                var pending = plugin.Notifications.GetSummary(selectedVenue?.ProfileId ?? Guid.Empty)?.PendingSettlementCount ??
                              (selectedVenue is null ? 0 : plugin.Finance.GetSnapshot(selectedVenue).View?.VenuePendingCount ?? 0);
                if (pending > 0)
                {
                    label += plugin.Configuration.NavigationCollapsed ? $" {pending}" : $" ({pending})";
                }
            }

            var size = new Vector2(-1, 30f * ImGuiHelpers.GlobalScale);
            if (PartyPulseUi.NavigationButton(label, $"Nav{item.Page}", selectedPage == item.Page, size))
            {
                selectedPage = item.Page;
            }

            if (plugin.Configuration.NavigationCollapsed && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(item.Label);
            }
        }

        var toggleHeight = 34f * ImGuiHelpers.GlobalScale;
        var available = ImGui.GetContentRegionAvail();
        if (available.Y > toggleHeight)
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + available.Y - toggleHeight);
        }

        var collapseLabel = plugin.Configuration.NavigationCollapsed ? ">>" : "<<  Collapse";
        if (ImGui.Button($"{collapseLabel}##PartyPulseToggleNavigation", new Vector2(-1, 0)))
        {
            plugin.Configuration.NavigationCollapsed = !plugin.Configuration.NavigationCollapsed;
            plugin.Configuration.Save();
        }
    }

    private static void DrawLogo(float sidebarWidth, bool collapsed)
    {
        var size = collapsed
            ? 40f * ImGuiHelpers.GlobalScale
            : 88f * ImGuiHelpers.GlobalScale;
        var path = Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty,
            "Assets",
            "icon.png");
        var texture = Plugin.TextureProvider.GetFromFileAbsolute(path).GetWrapOrEmpty();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), (sidebarWidth - size) / 2f));
        ImGui.Image(texture.Handle, new Vector2(size, size));
        ImGui.Spacing();
    }

    private VenueConnectionConfiguration? DrawCompactHeader()
    {
        var selectedVenue = DrawVenueSelector();

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
        {
            plugin.ToggleConfigUi();
        }

        if (selectedVenue is null)
        {
            ImGui.Spacing();
            PartyPulseUi.InlineStatus("No venue selected", PartyPulseUi.Warning);
            ImGui.Separator();
            return null;
        }

        if (selectedVenue.IsRegistered)
        {
            var auth = plugin.Authentication.GetSnapshot(selectedVenue);
            ImGui.SameLine();
            DrawConnectionStatus(auth);

            ImGui.SameLine();
            var authenticationAction = auth.Status == AuthenticationStatus.Connected
                ? "Reconnect"
                : "Authenticate";
            if (ImGui.SmallButton(authenticationAction))
            {
                plugin.ConnectVenue(selectedVenue);
            }

            if (auth.Status == AuthenticationStatus.Connected)
            {
                plugin.EnsureVenueUsersLoaded(selectedVenue);
                plugin.EnsureSelfServiceLoaded(selectedVenue);
            }
            else if (auth.Status == AuthenticationStatus.CharacterNotLinked &&
                     plugin.IdentityProvider.TryGetCurrent(out var identity, out _))
            {
                ImGui.Spacing();
                ImGui.TextColored(
                    PartyPulseUi.Warning,
                    $"{identity!.DisplayName} is not linked to this venue user.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Link current character"))
                {
                    pendingLinkVenue = selectedVenue;
                    ImGui.OpenPopup("Link current character###PartyPulseLinkCurrentCharacter");
                }
            }
        }
        else
        {
            ImGui.SameLine();
            PartyPulseUi.InlineStatus("Visitor mode", PartyPulseUi.Muted);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        return selectedVenue;
    }

    private VenueConnectionConfiguration? DrawVenueSelector()
    {
        var configuration = plugin.Configuration;
        var selected = configuration.GetSelectedVenue();
        var preview = selected?.DisplayLabel ?? "Select venue";

        ImGui.SetNextItemWidth(Math.Min(360f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - (90f * ImGuiHelpers.GlobalScale)));
        if (ImGui.BeginCombo("##VenueSelector", preview))
        {
            foreach (var venue in configuration.VenueConnections)
            {
                var isSelected = selected?.ProfileId == venue.ProfileId;
                if (ImGui.Selectable(venue.DisplayLabel, isSelected))
                {
                    configuration.SelectedVenueProfileId = venue.ProfileId;
                    configuration.Save();
                    selected = venue;
                    selectedPage = MainPage.Overview;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        return selected;
    }

    private static void DrawConnectionStatus(AuthenticationSnapshot snapshot)
    {
        var (label, color) = snapshot.Status switch
        {
            AuthenticationStatus.Connected => ("Connected", PartyPulseUi.Success),
            AuthenticationStatus.Connecting => ("Connecting", PartyPulseUi.Info),
            AuthenticationStatus.WaitingForPlayer => ("Waiting for player", PartyPulseUi.Warning),
            AuthenticationStatus.CharacterNotLinked => ("Character not linked", PartyPulseUi.Warning),
            AuthenticationStatus.Failed => ("Connection failed", PartyPulseUi.Danger),
            AuthenticationStatus.Expired => ("Session expired", PartyPulseUi.Warning),
            _ => ("Disconnected", PartyPulseUi.Muted),
        };

        PartyPulseUi.InlineStatus(label, color);
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(snapshot.Message))
        {
            ImGui.SetTooltip(snapshot.Message);
        }
    }

    private void DrawSelectedPage(
        VenueConnectionConfiguration? selectedVenue,
        bool authenticated)
    {
        if (selectedPage == MainPage.Overview)
        {
            DrawOverviewPage(selectedVenue);
            return;
        }

        if (selectedVenue is null || !authenticated)
        {
            PartyPulseUi.PageHeader("Venue access required", "Authenticate a registered venue account to use this page.");
            return;
        }

        RunActivePageAutoRefresh(selectedVenue, selectedPage);

        switch (selectedPage)
        {
            case MainPage.Openings:
                venueOpeningsTab.Draw(selectedVenue);
                break;
            case MainPage.Djs:
                djsTab.Draw(selectedVenue);
                break;
            case MainPage.Greeter:
                greeterTab.Draw(selectedVenue);
                break;
            case MainPage.Vip:
                vipTab.Draw(selectedVenue);
                break;
            case MainPage.Photoshoots:
                photoshootsTab.Draw(selectedVenue);
                break;
            case MainPage.Bar:
                barTab.Draw(selectedVenue);
                break;
            case MainPage.Court:
                courtTab.Draw(selectedVenue);
                break;
            case MainPage.OtherSales:
                otherSalesTab.Draw(selectedVenue);
                break;
            case MainPage.OtherGames:
                otherGamesTab.Draw(selectedVenue);
                break;
            case MainPage.Purchases:
                purchasesTab.Draw(selectedVenue);
                break;
            case MainPage.Staff:
                staffTab.Draw(selectedVenue);
                break;
            case MainPage.TimedMacros:
                timedMacrosTab.Draw(selectedVenue);
                break;
            case MainPage.Shoutrunner:
                shoutrunnerTab.Draw(selectedVenue);
                break;
            case MainPage.PartyFinder:
                partyFinderTab.Draw(selectedVenue);
                break;
            case MainPage.Finance:
                financeTab.Draw(selectedVenue, requestedFinanceSettlementId);
                requestedFinanceSettlementId = null;
                break;
            case MainPage.Users:
            {
                var snapshot = plugin.UserManagement.GetSnapshot(selectedVenue);
                if (snapshot.View?.Capabilities.CanView == true)
                {
                    DrawUsersPage(selectedVenue, snapshot);
                }
                else
                {
                    PartyPulseUi.PageHeader("Users", snapshot.Message);
                }
                break;
            }
            case MainPage.MyAccount:
                DrawMyAccountPage(selectedVenue);
                break;
        }
    }

    private void RunActivePageAutoRefresh(
        VenueConnectionConfiguration venue,
        MainPage page)
    {
        var interval = page switch
        {
            MainPage.TimedMacros => TimeSpan.FromSeconds(10),
            MainPage.Shoutrunner or MainPage.PartyFinder or MainPage.Greeter or MainPage.Staff => TimeSpan.FromSeconds(15),
            MainPage.Djs => TimeSpan.FromMinutes(1),
            MainPage.Users or MainPage.MyAccount => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromSeconds(30),
        };

        var key = (venue.ProfileId, page);
        var now = DateTimeOffset.UtcNow;
        if (!activePageRefreshes.TryGetValue(key, out var lastRefresh))
        {
            activePageRefreshes[key] = now;
            return;
        }

        if (now - lastRefresh < interval)
        {
            return;
        }

        activePageRefreshes[key] = now;
        switch (page)
        {
            case MainPage.Openings:
                plugin.RefreshVenueOpenings(venue);
                plugin.RefreshDjs(venue);
                plugin.RefreshOpeningPublications(venue);
                break;
            case MainPage.Djs:
                plugin.RefreshDjs(venue);
                break;
            case MainPage.Greeter:
                plugin.RefreshGreeter(venue);
                break;
            case MainPage.Vip:
                plugin.RefreshVip(venue);
                plugin.RefreshVipPerks(venue);
                break;
            case MainPage.Photoshoots:
                plugin.RefreshPhotoshoots(venue);
                plugin.RefreshVipPerks(venue);
                break;
            case MainPage.Bar:
                plugin.RefreshBar(venue);
                break;
            case MainPage.Court:
                plugin.RefreshCourt(venue);
                plugin.RefreshVipPerks(venue);
                break;
            case MainPage.OtherSales:
                plugin.RefreshOtherSales(venue);
                break;
            case MainPage.OtherGames:
                plugin.RefreshOtherGames(venue);
                break;
            case MainPage.Purchases:
                plugin.RefreshPurchases(venue);
                break;
            case MainPage.Staff:
                plugin.RefreshStaff(venue);
                plugin.RefreshCourt(venue);
                break;
            case MainPage.TimedMacros:
                plugin.RefreshTimedMacros(venue);
                break;
            case MainPage.Shoutrunner:
            case MainPage.PartyFinder:
                plugin.RefreshOpeningPublications(venue);
                break;
            case MainPage.Finance:
                plugin.RefreshFinance(venue);
                break;
            case MainPage.Users:
                plugin.RefreshVenueUsers(venue);
                break;
            case MainPage.MyAccount:
                plugin.RefreshSelfService(venue);
                break;
        }
    }

    private bool IsPageVisible(
        MainPage page,
        VenueConnectionConfiguration? venue,
        bool authenticated)
    {
        if (page == MainPage.Overview)
        {
            return true;
        }

        if (venue is null || !authenticated)
        {
            return false;
        }

        return page switch
        {
            MainPage.Djs => plugin.Djs.GetSnapshot(venue).View?.Capabilities.CanManageDirectory ?? true,
            MainPage.Openings => plugin.VenueOpenings.GetSnapshot(venue).View?.Capabilities.CanManage ?? true,
            MainPage.TimedMacros => plugin.TimedMacros.GetSnapshot(venue).View?.Capabilities is not { } capabilities ||
                                    capabilities.CanExecuteAny || capabilities.CanManageAny,
            MainPage.Shoutrunner => plugin.OpeningPublications.GetSnapshot(venue).View?.Capabilities.CanUseShoutrunner ?? true,
            MainPage.PartyFinder => plugin.OpeningPublications.GetSnapshot(venue).View?.Capabilities.CanUsePartyFinder ?? true,
            MainPage.Greeter => plugin.Greeter.GetSnapshot(venue).Context?.Capabilities.CanUse ?? true,
            MainPage.Users => plugin.UserManagement.GetSnapshot(venue).View?.Capabilities.CanView == true,
            _ => true,
        };
    }

    private void EnsureSelectedPageVisible(
        VenueConnectionConfiguration? venue,
        bool authenticated)
    {
        if (!IsPageVisible(selectedPage, venue, authenticated))
        {
            selectedPage = MainPage.Overview;
        }
    }

    private static bool CanDrawAuthenticatedFeatures(AuthenticationSnapshot snapshot)
    {
        if (snapshot.Status == AuthenticationStatus.Connected)
        {
            return true;
        }

        return snapshot.LastSuccessAt is not null &&
               (snapshot.Status == AuthenticationStatus.Connecting ||
                snapshot.Status == AuthenticationStatus.Expired ||
                snapshot.Status == AuthenticationStatus.Failed);
    }

    private void DrawOverviewPage(VenueConnectionConfiguration? selectedVenue)
    {
        PartyPulseUi.PageHeader(
            "Overview",
            "Current character and venue status. Detailed venue connection information is available in Settings.");

        if (plugin.IdentityProvider.TryGetCurrent(out var identity, out var reason))
        {
            PartyPulseUi.SectionHeader("Current character");
            ImGui.TextUnformatted(identity!.DisplayName);
        }
        else
        {
            ImGui.TextDisabled(reason);
        }

        PartyPulseUi.SectionHeader("Selected venue");
        ImGui.TextUnformatted(selectedVenue?.VenueName ?? "Not configured");
        ImGui.TextDisabled(selectedVenue?.AddressDisplay ?? "Add a venue from Settings.");

        var accessText = selectedVenue?.IsRegistered == true
            ? "Registered venue account"
            : "Visitor access";
        var accessColor = selectedVenue?.IsRegistered == true
            ? PartyPulseUi.Success
            : PartyPulseUi.Muted;
        PartyPulseUi.SectionHeader("Access");
        ImGui.TextColored(accessColor, accessText);
        ImGui.TextWrapped(
            "Use the navigation on the left to move between operational areas. Only pages available to this venue account are shown.");
    }

    private void DrawMyAccountPage(VenueConnectionConfiguration venue)
    {
        PartyPulseUi.PageHeader(
            "My Account",
            "Manage your linked characters, additional devices, venue authorization, and local venue data.");

        var snapshot = plugin.SelfService.GetSnapshot(venue);
        if (snapshot.Status is SelfServiceStatus.NotLoaded or SelfServiceStatus.Loading)
        {
            ImGui.TextDisabled(snapshot.Message);
            return;
        }

        if (snapshot.Status == SelfServiceStatus.Failed || snapshot.View is null)
        {
            ImGui.TextColored(PartyPulseUi.Danger, snapshot.Message);
            if (ImGui.Button("Retry"))
            {
                plugin.RefreshSelfService(venue);
            }
            return;
        }

        var view = snapshot.View;
        ImGui.TextColored(
            view.IsOwner ? PartyPulseUi.Warning : PartyPulseUi.Info,
            view.IsOwner ? "Venue owner account" : "Venue staff account");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
        {
            plugin.RefreshSelfService(venue);
        }

        PartyPulseUi.SectionHeader("Registered characters");
        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("SelfCharacters", 3, tableFlags))
        {
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var character in view.Characters)
            {
                ImGui.PushID(character.CharacterId);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(character.DisplayName);

                ImGui.TableSetColumnIndex(1);
                if (character.IsCurrent)
                {
                    ImGui.TextColored(PartyPulseUi.Success, "Current");
                }
                else if (character.IsMain)
                {
                    ImGui.TextUnformatted("Main");
                }
                else
                {
                    ImGui.TextDisabled("Linked");
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.BeginDisabled(character.IsCurrent);
                if (ImGui.SmallButton("Unlink"))
                {
                    pendingUnlinkVenue = venue;
                    pendingUnlinkCharacter = character;
                    requestOpenUnlinkPopup = true;
                }
                ImGui.EndDisabled();
                if (character.IsCurrent && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("The currently logged-in character cannot be unlinked.");
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        PartyPulseUi.SectionHeader(
            "Register another device",
            "Create a short-lived code, then add this venue on the second computer and choose Register with device code.");
        if (ImGui.Button("Create device pairing code"))
        {
            plugin.CreateDevicePairingCode(venue);
        }

        if (snapshot.LatestPairingCode is { } pairing)
        {
            ImGui.TextWrapped($"Pairing code: {pairing.PairingCode}");
            ImGui.TextDisabled($"Expires: {VenueTimeZone.Format(venue, pairing.ExpiresAt, "g")}");
            if (ImGui.Button("Copy pairing code"))
            {
                ImGui.SetClipboardText(pairing.PairingCode);
            }
        }

        PartyPulseUi.SectionHeader("Venue authorization");
        if (view.IsLastOwner)
        {
            ImGui.TextColored(
                PartyPulseUi.Warning,
                "You are the venue's last active owner and cannot unauthorize until another owner exists.");
        }

        ImGui.BeginDisabled(view.IsLastOwner);
        if (ImGui.Button("Unauthorize from venue"))
        {
            pendingUnauthorizeVenue = venue;
            requestOpenUnauthorizePopup = true;
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled("Disables your venue user and revokes every registered device for that user.");

        PartyPulseUi.SectionHeader("Local venue data");
        if (ImGui.Button("Remove venue from this device"))
        {
            pendingLocalRemovalVenue = venue;
            requestOpenLocalRemovalPopup = true;
        }
        ImGui.TextDisabled("Removes only the venue and credential stored by this plugin on this computer.");
    }

    private void DrawUsersPage(
        VenueConnectionConfiguration venue,
        VenueUserManagementSnapshot snapshot)
    {
        PartyPulseUi.PageHeader(
            "Users",
            "Create venue users, review access, and open a user to manage profile, recovery, and permissions.");

        var view = snapshot.View!;
        var isBusy = plugin.UserManagement.IsBusy(venue.ProfileId);
        if (addUserProfileId != venue.ProfileId)
        {
            addUserProfileId = venue.ProfileId;
            addUserDisplayName = string.Empty;
            addUserDiscordHandle = string.Empty;
        }

        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh"))
        {
            plugin.RefreshVenueUsers(venue);
        }
        ImGui.EndDisabled();

        if (view.Capabilities.CanCreate)
        {
            PartyPulseUi.SectionHeader("Add venue user");
            ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
            ImGui.InputText("Display name", ref addUserDisplayName, 50);
            ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
            ImGui.InputText("Discord handle (optional)", ref addUserDiscordHandle, 50);

            ImGui.BeginDisabled(isBusy || string.IsNullOrWhiteSpace(addUserDisplayName));
            if (ImGui.Button("Create user"))
            {
                plugin.CreateVenueUser(venue, addUserDisplayName, addUserDiscordHandle);
            }
            ImGui.EndDisabled();

            if (plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetReason))
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(isBusy);
                if (ImGui.Button($"Add target: {target!.DisplayName}"))
                {
                    addUserDisplayName = target.CharacterName;
                    plugin.CreateVenueUser(venue, target.CharacterName, addUserDiscordHandle);
                }
                ImGui.EndDisabled();
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextDisabled(targetReason);
            }
        }

        if (snapshot.LastInviteCode is { } inviteCode)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Invite code for {inviteCode.DisplayName}: {inviteCode.Code}");
            ImGui.TextDisabled($"Expires: {VenueTimeZone.Format(venue, inviteCode.ExpiresAt, "g")}");
            if (ImGui.Button("Copy latest invite code"))
            {
                ImGui.SetClipboardText(inviteCode.Code);
            }
        }

        PartyPulseUi.SectionHeader("Venue users");
        var tableFlags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("VenueUsers", 5, tableFlags, new Vector2(0, 340 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("Display name");
            ImGui.TableSetupColumn("Discord");
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Access");
            ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var user in view.Users)
            {
                ImGui.PushID(user.UserId);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(user.DisplayName);
                if (user.DisabledAt is not null)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(disabled)");
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(user.DiscordDisplay);
                if (!string.IsNullOrWhiteSpace(user.DiscordName) && !string.IsNullOrWhiteSpace(user.DiscordHandle))
                {
                    ImGui.TextDisabled($"@{user.DiscordHandle}");
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(user.CharacterDisplay);

                ImGui.TableSetColumnIndex(3);
                if (user.IsOwner)
                {
                    ImGui.TextColored(PartyPulseUi.Warning, "Owner");
                }
                else
                {
                    ImGui.TextUnformatted($"{user.Permissions.Count} permission(s)");
                }

                ImGui.TableSetColumnIndex(4);
                var canOpenEditor =
                    view.Capabilities.CanEdit ||
                    view.Capabilities.CanRecover ||
                    view.Capabilities.CanRestore ||
                    view.Capabilities.CanManagePermissions;
                ImGui.BeginDisabled(!canOpenEditor);
                if (ImGui.SmallButton("Edit"))
                {
                    plugin.OpenVenueUserEditor(venue, user);
                }
                ImGui.EndDisabled();

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawConfirmationPopups()
    {
        if (requestOpenUnauthorizePopup)
        {
            ImGui.OpenPopup("Unauthorize from venue###PartyPulseUnauthorizeVenue");
            requestOpenUnauthorizePopup = false;
        }

        if (requestOpenUnlinkPopup)
        {
            ImGui.OpenPopup("Unlink character###PartyPulseUnlinkCharacter");
            requestOpenUnlinkPopup = false;
        }

        if (requestOpenLocalRemovalPopup)
        {
            ImGui.OpenPopup("Remove venue from device###PartyPulseRemoveVenueFromDevice");
            requestOpenLocalRemovalPopup = false;
        }

        if (ImGui.BeginPopupModal(
                "Link current character###PartyPulseLinkCurrentCharacter",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (pendingLinkVenue is not null &&
                plugin.IdentityProvider.TryGetCurrent(out var identity, out var reason))
            {
                ImGui.TextWrapped("Link the currently logged-in character to this venue account?");
                ImGui.Spacing();
                ImGui.TextUnformatted($"Venue: {pendingLinkVenue.DisplayLabel}");
                ImGui.TextUnformatted($"Character: {identity!.CharacterName}");
                ImGui.TextUnformatted($"Home world: {identity.WorldName}");
                ImGui.Spacing();
                if (ImGui.Button("Link character"))
                {
                    plugin.LinkCurrentCharacter(pendingLinkVenue);
                    pendingLinkVenue = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            else
            {
                ImGui.TextDisabled("The current character is not available.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingLinkVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal(
                "Unlink character###PartyPulseUnlinkCharacter",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(
                pendingUnlinkCharacter is null
                    ? "Unlink this character?"
                    : $"Unlink {pendingUnlinkCharacter.DisplayName} from this venue account?");
            ImGui.TextDisabled("The character can be linked again later while logged into it on a registered device.");
            if (ImGui.Button("Unlink") && pendingUnlinkVenue is not null && pendingUnlinkCharacter is not null)
            {
                plugin.UnlinkCharacter(pendingUnlinkVenue, pendingUnlinkCharacter.CharacterId);
                pendingUnlinkVenue = null;
                pendingUnlinkCharacter = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingUnlinkVenue = null;
                pendingUnlinkCharacter = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal(
                "Unauthorize from venue###PartyPulseUnauthorizeVenue",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Unauthorize your user from {pendingUnauthorizeVenue?.DisplayLabel ?? "this venue"}?");
            ImGui.TextWrapped("Your venue user will be disabled and all of its devices will be revoked. The public venue remains in your local list as a visitor venue.");
            if (ImGui.Button("Unauthorize") && pendingUnauthorizeVenue is not null)
            {
                plugin.UnauthorizeFromVenue(pendingUnauthorizeVenue);
                pendingUnauthorizeVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingUnauthorizeVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal(
                "Remove venue from device###PartyPulseRemoveVenueFromDevice",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Remove {pendingLocalRemovalVenue?.DisplayLabel ?? "this venue"} from this device?");
            ImGui.TextWrapped("This removes the saved venue and local device credential only. Your server-side venue user and other devices are not changed.");
            if (ImGui.Button("Remove from this device") && pendingLocalRemovalVenue is not null)
            {
                plugin.RemoveVenueLocally(pendingLocalRemovalVenue);
                pendingLocalRemovalVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingLocalRemovalVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

}