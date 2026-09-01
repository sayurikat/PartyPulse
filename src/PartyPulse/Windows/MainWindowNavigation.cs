using System;
using System.Collections.Generic;

namespace PartyPulse.Windows;

internal enum MainPage
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
    Giveaways,
    DiscordStatus,
    Shoutrunner,
    PartyFinder,
    Finance,
    Users,
    MyAccount,
}

public enum MainSubtab
{
    OverviewStatus,
    OpeningsSchedule,
    OpeningsHistory,
    OpeningsDjs,
    OpeningsPublications,
    DjsDirectory,
    DjsCharacters,
    DjsPayments,
    DjsSettings,
    GreeterArrivals,
    GreeterMacros,
    VipArrivals,
    VipSales,
    VipPlayers,
    VipPackages,
    VipPerks,
    PhotoshootsSales,
    PhotoshootsPackages,
    PhotoshootsCommission,
    PhotoshootsHistory,
    BarBuyouts,
    BarGamba,
    BarSettlements,
    BarSettings,
    BarPackages,
    BarBuyoutHistory,
    BarGambaSalesHistory,
    BarGambaGamesHistory,
    CourtSales,
    CourtSettlements,
    CourtCommission,
    CourtOffers,
    CourtAccountants,
    CourtTransactions,
    CourtSalesHistory,
    OtherSalesSell,
    OtherSalesCatalog,
    OtherSalesHistory,
    OtherGamesSell,
    OtherGamesCatalog,
    OtherGamesHistory,
    PurchasesCreate,
    PurchasesHistory,
    StaffAttendance,
    StaffDirectory,
    StaffCharacters,
    StaffLifecycle,
    StaffJobs,
    StaffTimeEntries,
    StaffPayouts,
    TimedMacrosRun,
    TimedMacrosSetup,
    GiveawaysManage,
    GiveawaysScheduler,
    DiscordStatusPublication,
    DiscordStatusSettings,
    DiscordStatusNotifications,
    ShoutrunnerRun,
    ShoutrunnerRoute,
    ShoutrunnerTemplates,
    PartyFinderRun,
    PartyFinderTemplates,
    FinanceBalances,
    FinanceSettlements,
    UsersCreate,
    UsersDirectory,
    MyAccountCharacters,
    MyAccountDevices,
    MyAccountAuthorization,
    MyAccountLocalData,
}

internal sealed record MainSubtabDefinition(MainSubtab Id, string Label);

internal sealed class MainWindowNavigationState
{
    private readonly HashSet<(Guid ProfileId, MainPage Page)> expandedPages = [];
    private readonly Dictionary<(Guid ProfileId, MainPage Page), MainSubtab> preferredSubtabs = [];

    public bool IsExpanded(Guid profileId, MainPage page) =>
        expandedPages.Contains((profileId, page));

    public MainSubtab TogglePage(
        Guid profileId,
        MainPage page,
        IReadOnlyList<MainSubtabDefinition> visibleSubtabs)
    {
        if (visibleSubtabs.Count == 0)
        {
            throw new ArgumentException("At least one visible subtab is required.", nameof(visibleSubtabs));
        }

        var key = (profileId, page);
        if (!expandedPages.Remove(key))
        {
            expandedPages.Add(key);
            preferredSubtabs[key] = visibleSubtabs[0].Id;
        }

        return Resolve(profileId, page, visibleSubtabs).Id;
    }

    public void ExpandAndSelect(
        Guid profileId,
        MainPage page,
        MainSubtab subtab)
    {
        var key = (profileId, page);
        expandedPages.Add(key);
        preferredSubtabs[key] = subtab;
    }

    public void Select(Guid profileId, MainPage page, MainSubtab subtab) =>
        preferredSubtabs[(profileId, page)] = subtab;

    public MainSubtabDefinition Resolve(
        Guid profileId,
        MainPage page,
        IReadOnlyList<MainSubtabDefinition> visibleSubtabs)
    {
        if (visibleSubtabs.Count == 0)
        {
            throw new ArgumentException("At least one visible subtab is required.", nameof(visibleSubtabs));
        }

        if (preferredSubtabs.TryGetValue((profileId, page), out var preferred))
        {
            foreach (var subtab in visibleSubtabs)
            {
                if (subtab.Id == preferred)
                {
                    return subtab;
                }
            }
        }

        // Do not overwrite the preferred subtab when permissions temporarily hide it.
        // If it becomes visible again, its retained ImGui child ID restores its scroll.
        return visibleSubtabs[0];
    }
}

internal sealed class SubtabVisibilityState
{
    private readonly Dictionary<(Guid ProfileId, MainSubtab Subtab), bool> knownVisibility = [];

    public bool Resolve(
        (Guid ProfileId, MainSubtab Subtab) key,
        bool? currentVisibility,
        bool defaultVisible = false)
    {
        if (currentVisibility is { } resolvedVisibility)
        {
            knownVisibility[key] = resolvedVisibility;
            return resolvedVisibility;
        }

        // Managers commonly clear their view while reloading. Keep the last
        // resolved capability so a refresh cannot flicker or close navigation.
        return knownVisibility.TryGetValue(key, out var retainedVisibility)
            ? retainedVisibility
            : defaultVisible;
    }

    public void Clear(Guid profileId)
    {
        var keysToRemove = new List<(Guid ProfileId, MainSubtab Subtab)>();
        foreach (var key in knownVisibility.Keys)
        {
            if (key.ProfileId == profileId)
            {
                keysToRemove.Add(key);
            }
        }

        foreach (var key in keysToRemove)
        {
            knownVisibility.Remove(key);
        }
    }
}

internal sealed class NavigationAccessLoadState
{
    private readonly Dictionary<Guid, DateTimeOffset?> startedSessions = [];
    private readonly HashSet<Guid> remainderStartedProfiles = [];
    private readonly HashSet<Guid> resolvedProfiles = [];

    public bool ShouldStart(Guid profileId, DateTimeOffset? sessionStartedAt)
    {
        if (startedSessions.TryGetValue(profileId, out var startedAt) &&
            startedAt == sessionStartedAt)
        {
            return false;
        }

        startedSessions[profileId] = sessionStartedAt;
        remainderStartedProfiles.Remove(profileId);
        return true;
    }

    public bool HasStarted(Guid profileId) => startedSessions.ContainsKey(profileId);

    public bool ShouldStartRemainder(Guid profileId) => remainderStartedProfiles.Add(profileId);

    public bool HasStartedRemainder(Guid profileId) => remainderStartedProfiles.Contains(profileId);

    public bool IsResolved(Guid profileId) => resolvedProfiles.Contains(profileId);

    public void MarkResolved(Guid profileId) => resolvedProfiles.Add(profileId);

    public void Reset(Guid profileId)
    {
        startedSessions.Remove(profileId);
        remainderStartedProfiles.Remove(profileId);
        resolvedProfiles.Remove(profileId);
    }
}

internal sealed class RefreshDeferralState
{
    internal static readonly TimeSpan MaximumDeferral = TimeSpan.FromMinutes(1);

    private DateTimeOffset? textInputFocusedAt;

    public void Observe(bool textInputFocused, DateTimeOffset now)
    {
        if (!textInputFocused)
        {
            textInputFocusedAt = null;
            return;
        }

        textInputFocusedAt ??= now;
    }

    public bool ShouldDefer(DateTimeOffset now) =>
        textInputFocusedAt is { } focusedAt && now - focusedAt < MaximumDeferral;
}
