using System;
using System.Collections.Generic;
using System.Linq;

namespace PartyPulse.Windows;

internal enum MiniTabId
{
    Dj,
    Greet,
    Vip,
    Photo,
    BarBuyout,
    GambaShot,
    Court,
    PayCourt,
    Accountant,
    OtherSales,
    OtherGames,
    NewPurchase,
    PayStaff,
    Settlements,
    Configuration,
}

internal enum MiniTabCondition
{
    None,
    AnyTarget,
    LinkedDjTarget,
    LinkedStaffTarget,
    CourtAccountantTarget,
    PendingSettlement,
}

internal sealed record MiniTabDefinition(
    MiniTabId Id,
    string Label,
    MainPage Page,
    MainSubtab Subtab,
    MiniTabCondition Condition);

internal static class MiniTabCatalog
{
    public const int TabsPerRow = 8;

    public static readonly MiniTabDefinition[] Features =
    [
        new(MiniTabId.Dj, "DJ", MainPage.Djs, MainSubtab.DjsPayments, MiniTabCondition.LinkedDjTarget),
        new(MiniTabId.Greet, "Greet", MainPage.Greeter, MainSubtab.GreeterArrivals, MiniTabCondition.None),
        new(MiniTabId.Vip, "VIP", MainPage.Vip, MainSubtab.VipSales, MiniTabCondition.AnyTarget),
        new(MiniTabId.Photo, "Photo", MainPage.Photoshoots, MainSubtab.PhotoshootsSales, MiniTabCondition.AnyTarget),
        new(MiniTabId.BarBuyout, "Bar Buyout", MainPage.Bar, MainSubtab.BarBuyouts, MiniTabCondition.AnyTarget),
        new(MiniTabId.GambaShot, "Gamba Shot", MainPage.Bar, MainSubtab.BarGamba, MiniTabCondition.AnyTarget),
        new(MiniTabId.Court, "Court", MainPage.Court, MainSubtab.CourtSales, MiniTabCondition.AnyTarget),
        new(MiniTabId.PayCourt, "Pay Court", MainPage.Court, MainSubtab.CourtSettlements, MiniTabCondition.LinkedStaffTarget),
        new(MiniTabId.Accountant, "Accountant", MainPage.Court, MainSubtab.CourtAccountants, MiniTabCondition.CourtAccountantTarget),
        new(MiniTabId.OtherSales, "Other Sales", MainPage.OtherSales, MainSubtab.OtherSalesSell, MiniTabCondition.AnyTarget),
        new(MiniTabId.OtherGames, "Other Games", MainPage.OtherGames, MainSubtab.OtherGamesSell, MiniTabCondition.AnyTarget),
        new(MiniTabId.NewPurchase, "New Purchase", MainPage.Purchases, MainSubtab.PurchasesCreate, MiniTabCondition.None),
        new(MiniTabId.PayStaff, "Pay Staff", MainPage.Staff, MainSubtab.StaffPayouts, MiniTabCondition.LinkedStaffTarget),
        new(MiniTabId.Settlements, "Settlements", MainPage.Finance, MainSubtab.FinanceSettlements, MiniTabCondition.PendingSettlement),
    ];

    private static readonly Dictionary<MiniTabId, MiniTabDefinition> FeaturesById =
        Features.ToDictionary(static definition => definition.Id);

    public static MiniTabDefinition Get(MiniTabId id) => FeaturesById[id];

    public static IReadOnlyList<MiniTabDefinition> ResolveOrder(IEnumerable<string>? configuredOrder)
    {
        var ordered = new List<MiniTabDefinition>(Features.Length);
        var included = new HashSet<MiniTabId>();
        if (configuredOrder is not null)
        {
            foreach (var value in configuredOrder)
            {
                if (!Enum.TryParse<MiniTabId>(value, false, out var id) ||
                    !FeaturesById.TryGetValue(id, out var definition) ||
                    !included.Add(id))
                {
                    continue;
                }

                ordered.Add(definition);
            }
        }

        foreach (var definition in Features)
        {
            if (included.Add(definition.Id))
            {
                ordered.Add(definition);
            }
        }

        return ordered;
    }

    public static HashSet<MiniTabId> ResolveHidden(IEnumerable<string>? configuredHidden)
    {
        var hidden = new HashSet<MiniTabId>();
        if (configuredHidden is null)
        {
            return hidden;
        }

        foreach (var value in configuredHidden)
        {
            if (Enum.TryParse<MiniTabId>(value, false, out var id) && FeaturesById.ContainsKey(id))
            {
                hidden.Add(id);
            }
        }

        return hidden;
    }

    public static List<string> Move(
        IEnumerable<string>? configuredOrder,
        MiniTabId id,
        int direction,
        IReadOnlySet<MiniTabId> permitted)
    {
        var ordered = ResolveOrder(configuredOrder)
            .Select(static definition => definition.Id)
            .ToList();
        var permittedOrder = ordered.Where(permitted.Contains).ToList();
        var permittedIndex = permittedOrder.IndexOf(id);
        var destination = permittedIndex + direction;
        if (permittedIndex < 0 || destination < 0 || destination >= permittedOrder.Count)
        {
            return ordered.Select(static value => value.ToString()).ToList();
        }

        var other = permittedOrder[destination];
        var sourceIndex = ordered.IndexOf(id);
        var destinationIndex = ordered.IndexOf(other);
        (ordered[sourceIndex], ordered[destinationIndex]) =
            (ordered[destinationIndex], ordered[sourceIndex]);
        return ordered.Select(static value => value.ToString()).ToList();
    }

    public static int GetRowCount(int tabCount) =>
        tabCount <= 0 ? 0 : (tabCount + TabsPerRow - 1) / TabsPerRow;
}

internal sealed record MiniWindowSelection(
    MiniTabId SelectedTab,
    MiniTabDefinition? MissingTargetTab);

internal sealed class MiniWindowSelectionState
{
    private MiniTabId? preferredTab;

    public void Select(MiniTabId tab) => preferredTab = tab;

    public MiniWindowSelection Resolve(
        IReadOnlyList<MiniTabDefinition> visibleFeatures,
        IReadOnlySet<MiniTabId> configuredFeatures,
        IReadOnlySet<MiniTabId> targetBlockedFeatures)
    {
        preferredTab ??= visibleFeatures.FirstOrDefault()?.Id ?? MiniTabId.Configuration;

        if (preferredTab == MiniTabId.Configuration)
        {
            return new MiniWindowSelection(MiniTabId.Configuration, null);
        }

        var visible = visibleFeatures.FirstOrDefault(definition => definition.Id == preferredTab);
        if (visible is not null)
        {
            return new MiniWindowSelection(visible.Id, null);
        }

        if (configuredFeatures.Contains(preferredTab.Value) &&
            targetBlockedFeatures.Contains(preferredTab.Value))
        {
            return new MiniWindowSelection(
                preferredTab.Value,
                MiniTabCatalog.Get(preferredTab.Value));
        }

        preferredTab = visibleFeatures.FirstOrDefault()?.Id ?? MiniTabId.Configuration;
        return new MiniWindowSelection(preferredTab.Value, null);
    }
}
