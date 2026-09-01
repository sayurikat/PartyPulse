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
    private sealed record NavigationItem(
        MainPage Page,
        string Group,
        string Label,
        string Abbreviation,
        string Description,
        MainSubtabDefinition[] Subtabs);

    private static readonly NavigationItem[] NavigationItems =
    [
        new(MainPage.Overview, "GENERAL", "Overview", "OV",
            "Current character and venue status.",
            [new(MainSubtab.OverviewStatus, "Status")]),
        new(MainPage.Openings, "VENUE", "Openings", "OP",
            "Schedule openings, assign DJs, manage publication text, and review previous events.",
            [
                new(MainSubtab.OpeningsSchedule, "Schedule"),
                new(MainSubtab.OpeningsHistory, "Previous openings"),
                new(MainSubtab.OpeningsDjs, "DJ schedule"),
                new(MainSubtab.OpeningsPublications, "Publicity"),
            ]),
        new(MainPage.Djs, "VENUE", "DJs", "DJ",
            "Manage the venue DJ directory, characters, pricing, and payments.",
            [
                new(MainSubtab.DjsDirectory, "Directory"),
                new(MainSubtab.DjsCharacters, "Characters"),
                new(MainSubtab.DjsPayments, "Payments"),
                new(MainSubtab.DjsSettings, "Pricing"),
            ]),
        new(MainPage.Greeter, "GUESTS & SALES", "Greeter", "GR",
            "Track arriving players and manage greeting macros.",
            [
                new(MainSubtab.GreeterArrivals, "Arrivals"),
                new(MainSubtab.GreeterMacros, "Macros"),
            ]),
        new(MainPage.Vip, "GUESTS & SALES", "VIP", "VIP",
            "Manage VIP arrivals, sales, players, packages, and perks.",
            [
                new(MainSubtab.VipArrivals, "Arrivals"),
                new(MainSubtab.VipSales, "Sales"),
                new(MainSubtab.VipPlayers, "Players"),
                new(MainSubtab.VipPackages, "Packages"),
                new(MainSubtab.VipPerks, "Perks"),
            ]),
        new(MainPage.Photoshoots, "GUESTS & SALES", "Photoshoots", "PH",
            "Sell packages and manage commission, package setup, and sales history.",
            [
                new(MainSubtab.PhotoshootsSales, "Sales & settlement"),
                new(MainSubtab.PhotoshootsPackages, "Packages"),
                new(MainSubtab.PhotoshootsCommission, "Commission"),
                new(MainSubtab.PhotoshootsHistory, "History"),
            ]),
        new(MainPage.Bar, "GUESTS & SALES", "Bar", "BAR",
            "Run buyouts and Gamba Shot, settle revenue, and manage bar setup.",
            [
                new(MainSubtab.BarBuyouts, "Buyouts"),
                new(MainSubtab.BarGamba, "Gamba Shot"),
                new(MainSubtab.BarSettlements, "Settlement"),
                new(MainSubtab.BarSettings, "Settings"),
                new(MainSubtab.BarPackages, "Buyout packages"),
                new(MainSubtab.BarBuyoutHistory, "Buyout history"),
                new(MainSubtab.BarGambaSalesHistory, "Gamba sales"),
                new(MainSubtab.BarGambaGamesHistory, "Gamba history"),
            ]),
        new(MainPage.Court, "GUESTS & SALES", "Court Services", "CRT",
            "Sell court services and manage staff balances, offers, and history.",
            [
                new(MainSubtab.CourtSales, "Sales"),
                new(MainSubtab.CourtSettlements, "Staff settlement"),
                new(MainSubtab.CourtCommission, "Commission"),
                new(MainSubtab.CourtOffers, "Offers"),
                new(MainSubtab.CourtAccountants, "Accountants"),
                new(MainSubtab.CourtTransactions, "Transactions"),
                new(MainSubtab.CourtSalesHistory, "Sales history"),
            ]),
        new(MainPage.OtherSales, "GUESTS & SALES", "Other Sales", "SAL",
            "Sell configured items and manage catalog and history.",
            [
                new(MainSubtab.OtherSalesSell, "Sell & settle"),
                new(MainSubtab.OtherSalesCatalog, "Catalog"),
                new(MainSubtab.OtherSalesHistory, "History"),
            ]),
        new(MainPage.OtherGames, "GUESTS & SALES", "Other Games", "GM",
            "Sell game entries, settle outcomes, and manage games and history.",
            [
                new(MainSubtab.OtherGamesSell, "Sell & settle"),
                new(MainSubtab.OtherGamesCatalog, "Catalog"),
                new(MainSubtab.OtherGamesHistory, "History"),
            ]),
        new(MainPage.Purchases, "GUESTS & SALES", "Purchases", "PUR",
            "Record venue expenses and review purchase requests and history.",
            [
                new(MainSubtab.PurchasesCreate, "New purchase"),
                new(MainSubtab.PurchasesHistory, "History"),
            ]),
        new(MainPage.Staff, "OPERATIONS", "Staff", "STF",
            "Manage attendance, staff records, jobs, time entries, and payouts.",
            [
                new(MainSubtab.StaffAttendance, "Attendance"),
                new(MainSubtab.StaffDirectory, "Staff directory"),
                new(MainSubtab.StaffCharacters, "Character links"),
                new(MainSubtab.StaffLifecycle, "Lifecycle tasks"),
                new(MainSubtab.StaffJobs, "Jobs"),
                new(MainSubtab.StaffTimeEntries, "Time entries"),
                new(MainSubtab.StaffPayouts, "Payouts"),
            ]),
        new(MainPage.TimedMacros, "OPERATIONS", "Timed Macros", "TMR",
            "Run shared venue timers or manage their definitions.",
            [
                new(MainSubtab.TimedMacrosRun, "Run macros"),
                new(MainSubtab.TimedMacrosSetup, "Setup"),
            ]),
        new(MainPage.Giveaways, "OPERATIONS", "Giveaways", "GIV",
            "Manage Discord giveaways and opening-based schedules.",
            [
                new(MainSubtab.GiveawaysManage, "Giveaways"),
                new(MainSubtab.GiveawaysScheduler, "Scheduler"),
            ]),
        new(MainPage.DiscordStatus, "OPERATIONS", "Discord Status", "DS",
            "Configure and monitor the venue status post.",
            [
                new(MainSubtab.DiscordStatusPublication, "Current publication"),
                new(MainSubtab.DiscordStatusSettings, "Settings"),
                new(MainSubtab.DiscordStatusNotifications, "Notifications"),
            ]),
        new(MainPage.Shoutrunner, "OPERATIONS", "Shoutrunner", "SHR",
            "Run advertisement routes and manage route and template setup.",
            [
                new(MainSubtab.ShoutrunnerRun, "Run route"),
                new(MainSubtab.ShoutrunnerRoute, "Route setup"),
                new(MainSubtab.ShoutrunnerTemplates, "Templates"),
            ]),
        new(MainPage.PartyFinder, "OPERATIONS", "Party Finder", "PF",
            "Publish the active opening text and manage its template.",
            [
                new(MainSubtab.PartyFinderRun, "Publication"),
                new(MainSubtab.PartyFinderTemplates, "Templates"),
            ]),
        new(MainPage.Finance, "ADMINISTRATION", "Finance", "FIN",
            "Review balances and resolve settlement transactions.",
            [
                new(MainSubtab.FinanceBalances, "Balances"),
                new(MainSubtab.FinanceSettlements, "Settlements"),
            ]),
        new(MainPage.Users, "ADMINISTRATION", "Users", "USR",
            "Create venue users and review access.",
            [
                new(MainSubtab.UsersCreate, "Add user"),
                new(MainSubtab.UsersDirectory, "Directory"),
            ]),
        new(MainPage.MyAccount, "ADMINISTRATION", "My Account", "ME",
            "Manage characters, devices, and venue authorization.",
            [
                new(MainSubtab.MyAccountCharacters, "Characters"),
                new(MainSubtab.MyAccountDevices, "Devices"),
                new(MainSubtab.MyAccountAuthorization, "Authorization"),
                new(MainSubtab.MyAccountLocalData, "Local data"),
            ]),
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
    private readonly GiveawaysTabRenderer giveawaysTab;
    private readonly DiscordStatusTabRenderer discordStatusTab;
    private readonly FinanceTabRenderer financeTab;
    private readonly GreeterTabRenderer greeterTab;
    private readonly ShoutrunnerTabRenderer shoutrunnerTab;
    private readonly PartyFinderTabRenderer partyFinderTab;
    private readonly Dictionary<(Guid ProfileId, MainPage Page), DateTimeOffset> activePageRefreshes = new();
    private readonly MainWindowNavigationState navigationState = new();
    private readonly SubtabVisibilityState subtabVisibility = new();
    private readonly NavigationAccessLoadState navigationAccess = new();
    private readonly RefreshDeferralState refreshDeferral = new();

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
        giveawaysTab = new GiveawaysTabRenderer(plugin);
        discordStatusTab = new DiscordStatusTabRenderer(plugin);
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
        navigationState.ExpandAndSelect(
            venue.ProfileId,
            MainPage.Finance,
            MainSubtab.FinanceSettlements);
        IsOpen = true;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        // ImGui reports text-entry focus globally, so current and future text boxes
        // participate without renderer-specific refresh code.
        refreshDeferral.Observe(ImGui.GetIO().WantTextInput, DateTimeOffset.UtcNow);

        var selectedVenue = plugin.Configuration.GetSelectedVenue();
        var authentication = selectedVenue?.IsRegistered == true
            ? plugin.Authentication.GetSnapshot(selectedVenue)
            : null;
        var authenticated = authentication is not null && CanDrawAuthenticatedFeatures(authentication);
        PrepareNavigationAccess(selectedVenue, authentication, authenticated);
        var activePage = ResolveVisiblePage(selectedVenue, authenticated);

        var sidebarWidth = (plugin.Configuration.NavigationCollapsed ? 62f : 196f) * ImGuiHelpers.GlobalScale;
        if (ImGui.BeginChild("PartyPulseSidebar", new Vector2(sidebarWidth, 0), true))
        {
            DrawSidebar(selectedVenue, authenticated, activePage, sidebarWidth);
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("PartyPulseContent", Vector2.Zero, false))
        {
            selectedVenue = DrawCompactHeader();
            authentication = selectedVenue?.IsRegistered == true
                ? plugin.Authentication.GetSnapshot(selectedVenue)
                : null;
            authenticated = authentication is not null && CanDrawAuthenticatedFeatures(authentication);
            activePage = ResolveVisiblePage(selectedVenue, authenticated);
            DrawSelectedPage(selectedVenue, authenticated, activePage);
        }
        ImGui.EndChild();

        DrawConfirmationPopups();
    }

    private void DrawSidebar(
        VenueConnectionConfiguration? selectedVenue,
        bool authenticated,
        MainPage activePage,
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

        var toggleHeight = 34f * ImGuiHelpers.GlobalScale;
        var navigationHeight = Math.Max(0, ImGui.GetContentRegionAvail().Y - toggleHeight);
        if (ImGui.BeginChild("PartyPulseNavigationItems", new Vector2(0, navigationHeight), false))
        {
            DrawNavigationItems(selectedVenue, authenticated, activePage);
        }
        ImGui.EndChild();

        var collapseLabel = plugin.Configuration.NavigationCollapsed ? ">>" : "<<  Collapse";
        if (ImGui.Button($"{collapseLabel}##PartyPulseToggleNavigation", new Vector2(-1, 0)))
        {
            plugin.Configuration.NavigationCollapsed = !plugin.Configuration.NavigationCollapsed;
            plugin.Configuration.Save();
        }
    }

    private void DrawNavigationItems(
        VenueConnectionConfiguration? selectedVenue,
        bool authenticated,
        MainPage activePage)
    {
        var profileId = selectedVenue?.ProfileId ?? Guid.Empty;
        string? currentGroup = null;
        foreach (var item in NavigationItems)
        {
            var visibleSubtabs = GetVisibleSubtabs(item, selectedVenue, authenticated);
            if (visibleSubtabs.Count == 0)
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
            if (PartyPulseUi.NavigationButton(label, $"Nav{item.Page}", activePage == item.Page, size))
            {
                selectedPage = item.Page;
                navigationState.TogglePage(profileId, item.Page, visibleSubtabs);
            }

            if (plugin.Configuration.NavigationCollapsed && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(item.Label);
            }

            if (plugin.Configuration.NavigationCollapsed ||
                !navigationState.IsExpanded(profileId, item.Page))
            {
                continue;
            }

            var selectedSubtab = navigationState.Resolve(profileId, item.Page, visibleSubtabs);
            ImGui.Indent(18f * ImGuiHelpers.GlobalScale);
            foreach (var subtab in visibleSubtabs)
            {
                var selected = activePage == item.Page && selectedSubtab.Id == subtab.Id;
                var subtabSize = new Vector2(-1, 25f * ImGuiHelpers.GlobalScale);
                if (PartyPulseUi.SubNavigationButton(
                        subtab.Label,
                        $"SubNav{subtab.Id}",
                        selected,
                        subtabSize))
                {
                    selectedPage = item.Page;
                    navigationState.Select(profileId, item.Page, subtab.Id);
                }
            }
            ImGui.Unindent(18f * ImGuiHelpers.GlobalScale);
        }

        if (!plugin.Configuration.NavigationCollapsed &&
            selectedVenue is not null &&
            authenticated &&
            navigationAccess.HasStarted(profileId) &&
            !navigationAccess.IsResolved(profileId))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Loading available tools...");
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

            if (auth.Status == AuthenticationStatus.CharacterNotLinked &&
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
        bool authenticated,
        MainPage activePage)
    {
        var item = NavigationItems.First(value => value.Page == activePage);
        var visibleSubtabs = GetVisibleSubtabs(item, selectedVenue, authenticated);
        if (visibleSubtabs.Count == 0)
        {
            return;
        }

        var profileId = selectedVenue?.ProfileId ?? Guid.Empty;
        var subtab = navigationState.Resolve(profileId, activePage, visibleSubtabs);

        if (activePage != MainPage.Overview &&
            (selectedVenue is null || !authenticated))
        {
            return;
        }

        if (!ImGui.BeginChild(
                $"PartyPulseSubtabContent##{profileId:N}-{subtab.Id}",
                Vector2.Zero,
                false))
        {
            ImGui.EndChild();
            return;
        }

        PartyPulseUi.PageHeader($"{item.Label} · {subtab.Label}", item.Description);

        switch (activePage)
        {
            case MainPage.Overview:
                DrawOverviewPage(selectedVenue);
                break;
            case MainPage.Openings:
            {
                venueOpeningsTab.Draw(selectedVenue!, subtab.Id);
                if (venueOpeningsTab.RequestedSubtab is { } requestedSubtab)
                {
                    navigationState.Select(profileId, MainPage.Openings, requestedSubtab);
                }
                break;
            }
            case MainPage.Djs:
                djsTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Greeter:
                greeterTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Vip:
                vipTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Photoshoots:
                photoshootsTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Bar:
                barTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Court:
                courtTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.OtherSales:
                otherSalesTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.OtherGames:
                otherGamesTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Purchases:
                purchasesTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Staff:
                staffTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.TimedMacros:
                timedMacrosTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Giveaways:
                giveawaysTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.DiscordStatus:
                discordStatusTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Shoutrunner:
                shoutrunnerTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.PartyFinder:
                partyFinderTab.Draw(selectedVenue!, subtab.Id);
                break;
            case MainPage.Finance:
                financeTab.Draw(selectedVenue!, subtab.Id, requestedFinanceSettlementId);
                requestedFinanceSettlementId = null;
                break;
            case MainPage.Users:
            {
                var snapshot = plugin.UserManagement.GetSnapshot(selectedVenue!);
                if (snapshot.View?.Capabilities.CanView == true)
                {
                    DrawUsersPage(selectedVenue!, snapshot, subtab.Id);
                }
                else
                {
                    ImGui.TextWrapped(snapshot.Message);
                }
                break;
            }
            case MainPage.MyAccount:
                DrawMyAccountPage(selectedVenue!, subtab.Id);
                break;
        }

        ImGui.EndChild();

        if (activePage != MainPage.Overview)
        {
            var now = DateTimeOffset.UtcNow;
            refreshDeferral.Observe(ImGui.GetIO().WantTextInput, now);
            RunActivePageAutoRefresh(selectedVenue!, activePage);
        }
    }

    private void RunActivePageAutoRefresh(
        VenueConnectionConfiguration venue,
        MainPage page)
    {
        var interval = page switch
        {
            MainPage.TimedMacros => TimeSpan.FromSeconds(10),
            MainPage.Giveaways => TimeSpan.FromSeconds(15),
            MainPage.DiscordStatus => TimeSpan.FromSeconds(30),
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

        if (refreshDeferral.ShouldDefer(now))
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
            case MainPage.Giveaways:
                plugin.RefreshGiveaways(venue);
                break;
            case MainPage.DiscordStatus:
                plugin.RefreshDiscordStatus(venue);
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

    private void PrepareNavigationAccess(
        VenueConnectionConfiguration? venue,
        AuthenticationSnapshot? authentication,
        bool authenticated)
    {
        if (venue is null)
        {
            return;
        }

        if (!authenticated)
        {
            navigationAccess.Reset(venue.ProfileId);
            subtabVisibility.Clear(venue.ProfileId);
            return;
        }

        if (authentication?.Status != AuthenticationStatus.Connected)
        {
            return;
        }

        var sessionStartedAt = authentication.LastAttemptAt ?? authentication.LastSuccessAt;
        if (navigationAccess.ShouldStart(venue.ProfileId, sessionStartedAt))
        {
            // Let one authorized request establish or refresh the access token
            // before starting the remaining capability requests in parallel.
            plugin.RefreshVenueOpenings(venue);
            return;
        }

        if (!navigationAccess.HasStartedRemainder(venue.ProfileId))
        {
            if (plugin.VenueOpenings.IsBusy(venue.ProfileId))
            {
                return;
            }

            if (navigationAccess.ShouldStartRemainder(venue.ProfileId))
            {
                RefreshRemainingNavigationAccess(venue);
            }
        }

        if (navigationAccess.HasStartedRemainder(venue.ProfileId) &&
            !navigationAccess.IsResolved(venue.ProfileId) &&
            !IsNavigationAccessRefreshBusy(venue.ProfileId))
        {
            navigationAccess.MarkResolved(venue.ProfileId);
        }
    }

    private void RefreshRemainingNavigationAccess(VenueConnectionConfiguration venue)
    {
        // Access tokens intentionally contain identity rather than the dynamic
        // venue permission list. Resolve the existing capability views once per
        // authenticated session before drawing permission-based navigation.
        plugin.RefreshDjs(venue);
        plugin.RefreshOpeningPublications(venue);
        plugin.RefreshGreeter(venue);
        plugin.RefreshVip(venue);
        plugin.RefreshVipArrivals(venue);
        plugin.RefreshVipPerks(venue);
        plugin.RefreshPhotoshoots(venue);
        plugin.RefreshBar(venue);
        plugin.RefreshCourt(venue);
        plugin.RefreshOtherSales(venue);
        plugin.RefreshOtherGames(venue);
        plugin.RefreshPurchases(venue);
        plugin.RefreshStaff(venue);
        plugin.RefreshTimedMacros(venue);
        plugin.RefreshGiveaways(venue);
        plugin.RefreshDiscordStatus(venue);
        plugin.RefreshVenueUsers(venue);
    }

    private bool IsNavigationAccessRefreshBusy(Guid profileId) =>
        plugin.VenueOpenings.IsBusy(profileId) ||
        plugin.Djs.IsBusy(profileId) ||
        plugin.OpeningPublications.IsBusy(profileId) ||
        plugin.Greeter.IsBusy(profileId) ||
        plugin.Vip.IsBusy(profileId) ||
        plugin.VipArrivals.IsBusy(profileId) ||
        plugin.VipPerks.IsBusy(profileId) ||
        plugin.Photoshoots.IsBusy(profileId) ||
        plugin.Bar.IsBusy(profileId) ||
        plugin.Court.IsBusy(profileId) ||
        plugin.OtherSales.IsBusy(profileId) ||
        plugin.OtherGames.IsBusy(profileId) ||
        plugin.Purchases.IsBusy(profileId) ||
        plugin.Staff.IsBusy(profileId) ||
        plugin.TimedMacros.IsBusy(profileId) ||
        plugin.Giveaways.IsBusy(profileId) ||
        plugin.DiscordStatus.IsBusy(profileId) ||
        plugin.UserManagement.IsBusy(profileId);

    private IReadOnlyList<MainSubtabDefinition> GetVisibleSubtabs(
        NavigationItem item,
        VenueConnectionConfiguration? venue,
        bool authenticated)
    {
        if (item.Page == MainPage.Overview)
        {
            return item.Subtabs;
        }

        if (venue is null || !authenticated)
        {
            return Array.Empty<MainSubtabDefinition>();
        }

        if (item.Page is MainPage.Finance or MainPage.MyAccount)
        {
            return item.Subtabs;
        }

        if (!navigationAccess.IsResolved(venue.ProfileId))
        {
            return Array.Empty<MainSubtabDefinition>();
        }

        return item.Subtabs
            .Where(subtab => IsSubtabVisible(subtab.Id, venue))
            .ToArray();
    }

    private bool IsSubtabVisible(MainSubtab subtab, VenueConnectionConfiguration venue)
    {
        var visibilityKey = (venue.ProfileId, subtab);
        if (IsSubtabDenied(subtab, venue))
        {
            return subtabVisibility.Resolve(visibilityKey, false);
        }

        bool? visibility = subtab switch
        {
            MainSubtab.OverviewStatus => true,

            MainSubtab.OpeningsSchedule =>
                plugin.VenueOpenings.GetSnapshot(venue).View?.Capabilities.CanManage,
            MainSubtab.OpeningsHistory =>
                plugin.VenueOpenings.GetSnapshot(venue).View?.Capabilities.CanManage,
            MainSubtab.OpeningsDjs => CanManageOpeningDjs(),
            MainSubtab.OpeningsPublications => CanManageOpeningPublications(),

            MainSubtab.DjsSettings or MainSubtab.DjsDirectory or MainSubtab.DjsCharacters =>
                plugin.Djs.GetSnapshot(venue).View?.Capabilities.CanManageDirectory,
            MainSubtab.DjsPayments => CanManageDjPayments(),

            MainSubtab.GreeterArrivals =>
                plugin.Greeter.GetSnapshot(venue).Context?.Capabilities.CanUse,
            MainSubtab.GreeterMacros =>
                plugin.Greeter.GetSnapshot(venue).Context?.Capabilities.CanManageMacros,

            MainSubtab.VipArrivals => CanUseVipArrivals(),
            MainSubtab.VipSales =>
                plugin.Vip.GetSnapshot(venue).View?.Capabilities.CanSell,
            MainSubtab.VipPlayers =>
                plugin.Vip.GetSnapshot(venue).View?.Capabilities.CanView,
            MainSubtab.VipPackages =>
                plugin.Vip.GetSnapshot(venue).View?.Capabilities.CanManagePackages,
            MainSubtab.VipPerks => CanViewVipPerks(),

            MainSubtab.PhotoshootsSales =>
                plugin.Photoshoots.GetSnapshot(venue).View?.Capabilities.CanSell,
            MainSubtab.PhotoshootsPackages =>
                plugin.Photoshoots.GetSnapshot(venue).View?.Capabilities.CanManagePackages,
            MainSubtab.PhotoshootsCommission =>
                plugin.Photoshoots.GetSnapshot(venue).View?.Capabilities.CanManageCommission,
            MainSubtab.PhotoshootsHistory =>
                plugin.Photoshoots.GetSnapshot(venue).View?.Capabilities.CanView,

            MainSubtab.BarBuyouts or
                MainSubtab.BarBuyoutHistory or
                MainSubtab.BarGambaSalesHistory or
                MainSubtab.BarGambaGamesHistory or
                MainSubtab.BarSettlements =>
                plugin.Bar.GetSnapshot(venue).View?.Capabilities.CanView,
            MainSubtab.BarGamba => CanUseBarGamba(),
            MainSubtab.BarSettings or MainSubtab.BarPackages =>
                plugin.Bar.GetSnapshot(venue).View?.Capabilities.CanManage,

            MainSubtab.CourtSales =>
                plugin.Court.GetSnapshot(venue).View?.Capabilities.CanSell,
            MainSubtab.CourtSettlements or MainSubtab.CourtAccountants => CanManageCourtFinance(),
            MainSubtab.CourtCommission =>
                plugin.Court.GetSnapshot(venue).View?.Capabilities.CanManageCommission,
            MainSubtab.CourtOffers =>
                plugin.Court.GetSnapshot(venue).View?.Capabilities.CanManage,
            MainSubtab.CourtTransactions or MainSubtab.CourtSalesHistory => true,

            MainSubtab.OtherSalesSell =>
                plugin.OtherSales.GetSnapshot(venue).View?.Capabilities.CanSell,
            MainSubtab.OtherSalesCatalog =>
                plugin.OtherSales.GetSnapshot(venue).View?.Capabilities.CanManageItems,
            MainSubtab.OtherSalesHistory =>
                plugin.OtherSales.GetSnapshot(venue).View?.Capabilities.CanView,
            MainSubtab.OtherGamesSell =>
                plugin.OtherGames.GetSnapshot(venue).View?.Capabilities.CanSell,
            MainSubtab.OtherGamesCatalog =>
                plugin.OtherGames.GetSnapshot(venue).View?.Capabilities.CanManageItems,
            MainSubtab.OtherGamesHistory =>
                plugin.OtherGames.GetSnapshot(venue).View?.Capabilities.CanView,

            MainSubtab.PurchasesCreate =>
                plugin.Purchases.GetSnapshot(venue).View?.Capabilities.CanCreate,
            MainSubtab.PurchasesHistory =>
                plugin.Purchases.GetSnapshot(venue).View?.Capabilities.CanView,

            MainSubtab.StaffAttendance => CanManageStaffAttendance(),
            MainSubtab.StaffDirectory or MainSubtab.StaffCharacters or MainSubtab.StaffLifecycle =>
                plugin.Staff.GetSnapshot(venue).View?.Capabilities.CanManage,
            MainSubtab.StaffJobs =>
                plugin.Staff.GetSnapshot(venue).View?.Capabilities.CanManageJobs,
            MainSubtab.StaffTimeEntries => true,
            MainSubtab.StaffPayouts =>
                plugin.Staff.GetSnapshot(venue).View?.Capabilities.CanPay,

            MainSubtab.TimedMacrosRun =>
                plugin.TimedMacros.GetSnapshot(venue).View?.Capabilities.CanExecuteAny,
            MainSubtab.TimedMacrosSetup =>
                plugin.TimedMacros.GetSnapshot(venue).View?.Capabilities.CanManageAny,

            MainSubtab.GiveawaysManage or MainSubtab.GiveawaysScheduler =>
                plugin.Giveaways.GetSnapshot(venue).View?.Capabilities.CanManage,
            MainSubtab.DiscordStatusSettings or
                MainSubtab.DiscordStatusNotifications or
                MainSubtab.DiscordStatusPublication =>
                plugin.DiscordStatus.GetSnapshot(venue).View?.Capabilities.CanManage,

            MainSubtab.ShoutrunnerRun or MainSubtab.ShoutrunnerRoute =>
                plugin.OpeningPublications.GetSnapshot(venue).View?.Capabilities.CanUseShoutrunner,
            MainSubtab.ShoutrunnerTemplates =>
                plugin.OpeningPublications.GetSnapshot(venue).View?.Capabilities.CanManageShoutrunnerTemplates,
            MainSubtab.PartyFinderRun =>
                plugin.OpeningPublications.GetSnapshot(venue).View?.Capabilities.CanUsePartyFinder,
            MainSubtab.PartyFinderTemplates =>
                plugin.OpeningPublications.GetSnapshot(venue).View?.Capabilities.CanManagePartyFinderTemplates,

            MainSubtab.FinanceBalances or MainSubtab.FinanceSettlements => true,
            MainSubtab.UsersCreate => CanCreateUsers(),
            MainSubtab.UsersDirectory =>
                plugin.UserManagement.GetSnapshot(venue).View?.Capabilities.CanView,
            MainSubtab.MyAccountCharacters or
                MainSubtab.MyAccountDevices or
                MainSubtab.MyAccountAuthorization or
                MainSubtab.MyAccountLocalData => true,
            _ => true,
        };

        return subtabVisibility.Resolve(visibilityKey, visibility);

        bool? CanManageOpeningDjs()
        {
            var openings = plugin.VenueOpenings.GetSnapshot(venue).View;
            var djs = plugin.Djs.GetSnapshot(venue).View;
            if (openings?.Capabilities.CanManage == false)
            {
                return false;
            }

            if (openings is null || djs is null)
            {
                return null;
            }

            return djs.Capabilities.CanManageSchedule || djs.Capabilities.CanManagePayments;
        }

        bool? CanManageOpeningPublications()
        {
            var openings = plugin.VenueOpenings.GetSnapshot(venue).View;
            var publications = plugin.OpeningPublications.GetSnapshot(venue).View;
            if (openings?.Capabilities.CanManage == false ||
                publications?.Capabilities.CanManageOpenings == false)
            {
                return false;
            }

            return openings is null || publications is null ? null : true;
        }

        bool? CanManageDjPayments()
        {
            var djs = plugin.Djs.GetSnapshot(venue).View;
            return djs is null
                ? null
                : djs.Capabilities.CanManageDirectory && djs.Capabilities.CanManagePayments;
        }

        bool? CanUseVipArrivals()
        {
            var arrivals = plugin.VipArrivals.GetSnapshot(venue).Context;
            return arrivals is null
                ? null
                : arrivals.Capabilities.CanUseArrival ||
                  arrivals.Capabilities.CanManageOpenings ||
                  arrivals.Capabilities.CanManageMacros;
        }

        bool? CanViewVipPerks()
        {
            var vip = plugin.Vip.GetSnapshot(venue).View;
            var perks = plugin.VipPerks.GetSnapshot(venue).View;
            if (vip?.Capabilities.CanView == false || perks?.Capabilities.CanView == false)
            {
                return false;
            }

            return vip is null || perks is null ? null : true;
        }

        bool? CanUseBarGamba()
        {
            var bar = plugin.Bar.GetSnapshot(venue).View;
            return bar is null
                ? null
                : bar.Capabilities.CanSell ||
                  bar.Capabilities.CanManageGame ||
                  bar.Capabilities.CanCancelGame;
        }

        bool? CanManageCourtFinance()
        {
            var court = plugin.Court.GetSnapshot(venue).View;
            return court is null
                ? null
                : court.Capabilities.CanFinance || court.Capabilities.CanAccount;
        }

        bool? CanManageStaffAttendance()
        {
            var staff = plugin.Staff.GetSnapshot(venue).View;
            return staff is null
                ? null
                : staff.Capabilities.CanManage || staff.Capabilities.CanManageCourtAttendance;
        }

        bool? CanCreateUsers()
        {
            var capabilities = plugin.UserManagement.GetSnapshot(venue).View?.Capabilities;
            return capabilities is null ? null : capabilities.CanView && capabilities.CanCreate;
        }
    }

    private bool IsSubtabDenied(MainSubtab subtab, VenueConnectionConfiguration venue) =>
        subtab switch
        {
            MainSubtab.OpeningsSchedule or MainSubtab.OpeningsHistory =>
                plugin.VenueOpenings.GetSnapshot(venue).Status ==
                PartyPulse.VenueOpenings.VenueOpeningScheduleStatus.Denied,
            MainSubtab.OpeningsDjs =>
                plugin.VenueOpenings.GetSnapshot(venue).Status ==
                    PartyPulse.VenueOpenings.VenueOpeningScheduleStatus.Denied ||
                plugin.Djs.GetSnapshot(venue).Status == PartyPulse.Djs.DjManagementStatus.Denied,
            MainSubtab.OpeningsPublications =>
                plugin.VenueOpenings.GetSnapshot(venue).Status ==
                    PartyPulse.VenueOpenings.VenueOpeningScheduleStatus.Denied ||
                plugin.OpeningPublications.GetSnapshot(venue).Status ==
                    PartyPulse.OpeningPublications.OpeningPublicationManagementStatus.Denied,
            MainSubtab.DjsSettings or
                MainSubtab.DjsDirectory or
                MainSubtab.DjsCharacters or
                MainSubtab.DjsPayments =>
                plugin.Djs.GetSnapshot(venue).Status == PartyPulse.Djs.DjManagementStatus.Denied,
            MainSubtab.GreeterArrivals or MainSubtab.GreeterMacros =>
                plugin.Greeter.GetSnapshot(venue).Status == PartyPulse.Greeter.GreeterManagementStatus.Denied,
            MainSubtab.VipArrivals =>
                plugin.VipArrivals.GetSnapshot(venue).Status == PartyPulse.Vip.VipArrivalManagementStatus.Denied,
            MainSubtab.VipSales or MainSubtab.VipPlayers or MainSubtab.VipPackages =>
                plugin.Vip.GetSnapshot(venue).Status == PartyPulse.Vip.VipManagementStatus.Denied,
            MainSubtab.VipPerks =>
                plugin.Vip.GetSnapshot(venue).Status == PartyPulse.Vip.VipManagementStatus.Denied ||
                plugin.VipPerks.GetSnapshot(venue).Status == PartyPulse.Vip.VipPerkManagementStatus.Denied,
            MainSubtab.PhotoshootsSales or
                MainSubtab.PhotoshootsPackages or
                MainSubtab.PhotoshootsCommission or
                MainSubtab.PhotoshootsHistory =>
                plugin.Photoshoots.GetSnapshot(venue).Status == PartyPulse.Photoshoots.PhotoshootManagementStatus.Denied,
            MainSubtab.BarBuyouts or
                MainSubtab.BarGamba or
                MainSubtab.BarSettlements or
                MainSubtab.BarSettings or
                MainSubtab.BarPackages or
                MainSubtab.BarBuyoutHistory or
                MainSubtab.BarGambaSalesHistory or
                MainSubtab.BarGambaGamesHistory =>
                plugin.Bar.GetSnapshot(venue).Status == PartyPulse.Bar.BarManagementStatus.Denied,
            MainSubtab.CourtSales or
                MainSubtab.CourtSettlements or
                MainSubtab.CourtCommission or
                MainSubtab.CourtOffers or
                MainSubtab.CourtAccountants or
                MainSubtab.CourtTransactions or
                MainSubtab.CourtSalesHistory =>
                plugin.Court.GetSnapshot(venue).Status == PartyPulse.Court.CourtManagementStatus.Denied,
            MainSubtab.OtherSalesSell or MainSubtab.OtherSalesCatalog or MainSubtab.OtherSalesHistory =>
                plugin.OtherSales.GetSnapshot(venue).Status == PartyPulse.OtherSales.OtherSalesManagementStatus.Denied,
            MainSubtab.OtherGamesSell or MainSubtab.OtherGamesCatalog or MainSubtab.OtherGamesHistory =>
                plugin.OtherGames.GetSnapshot(venue).Status == PartyPulse.OtherGames.OtherGamesManagementStatus.Denied,
            MainSubtab.PurchasesCreate or MainSubtab.PurchasesHistory =>
                plugin.Purchases.GetSnapshot(venue).Status == PartyPulse.Purchases.PurchaseManagementStatus.Denied,
            MainSubtab.StaffAttendance or
                MainSubtab.StaffDirectory or
                MainSubtab.StaffCharacters or
                MainSubtab.StaffLifecycle or
                MainSubtab.StaffJobs or
                MainSubtab.StaffTimeEntries or
                MainSubtab.StaffPayouts =>
                plugin.Staff.GetSnapshot(venue).Status == PartyPulse.Staff.StaffManagementStatus.Denied,
            MainSubtab.TimedMacrosRun or MainSubtab.TimedMacrosSetup =>
                plugin.TimedMacros.GetSnapshot(venue).Status ==
                PartyPulse.TimedMacros.TimedMacroManagementStatus.Denied,
            MainSubtab.GiveawaysManage or MainSubtab.GiveawaysScheduler =>
                plugin.Giveaways.GetSnapshot(venue).Status ==
                PartyPulse.Giveaways.GiveawayManagementStatus.Denied,
            MainSubtab.DiscordStatusSettings or
                MainSubtab.DiscordStatusNotifications or
                MainSubtab.DiscordStatusPublication =>
                plugin.DiscordStatus.GetSnapshot(venue).Status ==
                PartyPulse.DiscordStatus.DiscordStatusManagementStatus.Denied,
            MainSubtab.ShoutrunnerRun or
                MainSubtab.ShoutrunnerRoute or
                MainSubtab.ShoutrunnerTemplates or
                MainSubtab.PartyFinderRun or
                MainSubtab.PartyFinderTemplates =>
                plugin.OpeningPublications.GetSnapshot(venue).Status ==
                PartyPulse.OpeningPublications.OpeningPublicationManagementStatus.Denied,
            MainSubtab.UsersCreate or MainSubtab.UsersDirectory =>
                plugin.UserManagement.GetSnapshot(venue).Status == VenueUserManagementStatus.Denied,
            _ => false,
        };

    private MainPage ResolveVisiblePage(
        VenueConnectionConfiguration? venue,
        bool authenticated)
    {
        var selectedItem = NavigationItems.First(value => value.Page == selectedPage);
        if (GetVisibleSubtabs(selectedItem, venue, authenticated).Count > 0)
        {
            return selectedPage;
        }

        return NavigationItems
            .First(item => GetVisibleSubtabs(item, venue, authenticated).Count > 0)
            .Page;
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

    private void DrawMyAccountPage(
        VenueConnectionConfiguration venue,
        MainSubtab subtab)
    {
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

        switch (subtab)
        {
            case MainSubtab.MyAccountCharacters:
                DrawAccountCharacters(venue, view);
                break;
            case MainSubtab.MyAccountDevices:
                DrawAccountDevices(venue, snapshot);
                break;
            case MainSubtab.MyAccountAuthorization:
                DrawAccountAuthorization(venue, view);
                break;
            case MainSubtab.MyAccountLocalData:
                DrawAccountLocalData(venue);
                break;
        }
    }

    private void DrawAccountCharacters(
        VenueConnectionConfiguration venue,
        SelfServiceViewResponse view)
    {
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
    }

    private void DrawAccountDevices(
        VenueConnectionConfiguration venue,
        SelfServiceSnapshot snapshot)
    {
        ImGui.TextWrapped(
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
    }

    private void DrawAccountAuthorization(
        VenueConnectionConfiguration venue,
        SelfServiceViewResponse view)
    {
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
    }

    private void DrawAccountLocalData(VenueConnectionConfiguration venue)
    {
        if (ImGui.Button("Remove venue from this device"))
        {
            pendingLocalRemovalVenue = venue;
            requestOpenLocalRemovalPopup = true;
        }
        ImGui.TextDisabled("Removes only the venue and credential stored by this plugin on this computer.");
    }

    private void DrawUsersPage(
        VenueConnectionConfiguration venue,
        VenueUserManagementSnapshot snapshot,
        MainSubtab subtab)
    {
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

        if (subtab == MainSubtab.UsersCreate && view.Capabilities.CanCreate)
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

        if (subtab == MainSubtab.UsersCreate && snapshot.LastInviteCode is { } inviteCode)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Invite code for {inviteCode.DisplayName}: {inviteCode.Code}");
            ImGui.TextDisabled($"Expires: {VenueTimeZone.Format(venue, inviteCode.ExpiresAt, "g")}");
            if (ImGui.Button("Copy latest invite code"))
            {
                ImGui.SetClipboardText(inviteCode.Code);
            }
        }

        if (subtab == MainSubtab.UsersCreate)
        {
            return;
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
