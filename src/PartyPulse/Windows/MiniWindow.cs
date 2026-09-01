using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Models;

namespace PartyPulse.Windows;

public sealed class MiniWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly MainWindow mainWindow;
    private readonly MiniWindowSelectionState selectionState = new();

    public MiniWindow(Plugin plugin, MainWindow mainWindow)
        : base("Party Pulse Mini###PartyPulseMini")
    {
        this.plugin = plugin;
        this.mainWindow = mainWindow;
        IsOpen = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(740, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var venue = plugin.Configuration.GetSelectedVenue();
        if (DrawWindowActions(venue))
        {
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var accessReady = venue is not null && mainWindow.PrepareMiniAccess(venue);
        if (accessReady)
        {
            plugin.EnsureFinanceLoaded(venue!);
        }

        var ordered = MiniTabCatalog.ResolveOrder(plugin.Configuration.MiniTabOrder);
        var permitted = accessReady
            ? ordered.Where(definition => mainWindow.IsMiniSubtabAvailable(venue!, definition.Subtab)).ToArray()
            : Array.Empty<MiniTabDefinition>();
        var hidden = MiniTabCatalog.ResolveHidden(plugin.Configuration.HiddenMiniTabs);
        var configured = permitted.Where(definition => !hidden.Contains(definition.Id)).ToArray();
        var conditionState = ResolveConditions(venue);
        var visible = configured
            .Where(definition => IsConditionSatisfied(definition.Condition, conditionState))
            .ToArray();
        var configuredIds = configured.Select(static definition => definition.Id).ToHashSet();
        var targetBlocked = configured
            .Where(definition =>
                IsTargetCondition(definition.Condition) &&
                !IsConditionSatisfied(definition.Condition, conditionState))
            .Select(static definition => definition.Id)
            .ToHashSet();
        var selection = accessReady
            ? selectionState.Resolve(visible, configuredIds, targetBlocked)
            : new MiniWindowSelection(MiniTabId.Configuration, null);

        DrawTabs(visible, selection);
        ImGui.Separator();
        ImGui.Spacing();

        var contentId = selection.MissingTargetTab is not null
            ? $"NoTarget-{selection.SelectedTab}"
            : selection.SelectedTab.ToString();
        if (!ImGui.BeginChild($"PartyPulseMiniContent##{contentId}", Vector2.Zero, false))
        {
            ImGui.EndChild();
            return;
        }

        if (selection.MissingTargetTab is { } blocked)
        {
            ImGui.TextWrapped($"No Valid Target for {blocked.Label}.");
        }
        else if (selection.SelectedTab == MiniTabId.Configuration)
        {
            DrawConfiguration(permitted, accessReady, venue);
        }
        else if (venue is not null)
        {
            mainWindow.DrawMiniSubtab(venue, MiniTabCatalog.Get(selection.SelectedTab));
        }

        ImGui.EndChild();
    }

    public void Dispose()
    {
    }

    private bool DrawWindowActions(VenueConnectionConfiguration? venue)
    {
        if (ImGui.Button("Main"))
        {
            plugin.SwitchToMainUi();
            return true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
        {
            plugin.OpenConfigUi();
        }

        if (venue is not null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(venue.DisplayLabel);
        }

        return false;
    }

    private void DrawTabs(
        IReadOnlyList<MiniTabDefinition> visible,
        MiniWindowSelection selection)
    {
        var tabs = new List<(MiniTabId Id, string Label, bool Selected, bool Placeholder)>(visible.Count + 2);
        if (selection.MissingTargetTab is not null)
        {
            tabs.Add((selection.SelectedTab, "No Target", true, true));
        }

        tabs.AddRange(visible.Select(definition =>
            (definition.Id, definition.Label, definition.Id == selection.SelectedTab, false)));
        tabs.Add((
            MiniTabId.Configuration,
            "Mini Config",
            selection.SelectedTab == MiniTabId.Configuration,
            false));

        for (var index = 0; index < tabs.Count; index++)
        {
            if (index > 0 && index % MiniTabCatalog.TabsPerRow != 0)
            {
                ImGui.SameLine(0, 3f * ImGuiHelpers.GlobalScale);
            }

            var tab = tabs[index];
            if (!PartyPulseUi.MiniTabButton(
                    tab.Label,
                    tab.Placeholder ? $"NoTarget{tab.Id}" : tab.Id.ToString(),
                    tab.Selected))
            {
                continue;
            }

            if (!tab.Placeholder)
            {
                selectionState.Select(tab.Id);
            }
        }
    }

    private void DrawConfiguration(
        IReadOnlyList<MiniTabDefinition> permitted,
        bool accessReady,
        VenueConnectionConfiguration? venue)
    {
        PartyPulseUi.SectionHeader(
            "Mini tabs",
            "Choose which permitted tabs appear and their order. These preferences are saved on this device only.");

        if (venue is null)
        {
            ImGui.TextWrapped("No venue is selected. Open Main or Settings to select a venue.");
            return;
        }

        if (!accessReady)
        {
            ImGui.TextDisabled("Loading available mini tabs...");
            return;
        }

        if (permitted.Count == 0)
        {
            ImGui.TextDisabled("No mini tabs are available with the current venue permissions.");
            return;
        }

        var hidden = MiniTabCatalog.ResolveHidden(plugin.Configuration.HiddenMiniTabs);
        var permittedIds = permitted.Select(static definition => definition.Id).ToHashSet();
        var flags = ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("PartyPulseMiniTabConfiguration", 4, flags))
        {
            return;
        }

        ImGui.TableSetupColumn("Show", ImGuiTableColumnFlags.WidthFixed, 52f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Tab");
        ImGui.TableSetupColumn("Up", ImGuiTableColumnFlags.WidthFixed, 48f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Down", ImGuiTableColumnFlags.WidthFixed, 58f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        for (var index = 0; index < permitted.Count; index++)
        {
            var definition = permitted[index];
            ImGui.PushID($"mini-config-{definition.Id}");
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var shown = !hidden.Contains(definition.Id);
            if (ImGui.Checkbox("##Shown", ref shown))
            {
                if (shown)
                {
                    hidden.Remove(definition.Id);
                }
                else
                {
                    hidden.Add(definition.Id);
                }

                plugin.Configuration.HiddenMiniTabs = MiniTabCatalog.Features
                    .Where(value => hidden.Contains(value.Id))
                    .Select(static value => value.Id.ToString())
                    .ToList();
                plugin.Configuration.Save();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(definition.Label);

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(index == 0);
            if (ImGui.SmallButton("Up"))
            {
                MoveTab(definition.Id, -1, permittedIds);
                ImGui.EndDisabled();
                ImGui.PopID();
                ImGui.EndTable();
                return;
            }
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(index == permitted.Count - 1);
            if (ImGui.SmallButton("Down"))
            {
                MoveTab(definition.Id, 1, permittedIds);
                ImGui.EndDisabled();
                ImGui.PopID();
                ImGui.EndTable();
                return;
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void MoveTab(
        MiniTabId tab,
        int direction,
        IReadOnlySet<MiniTabId> permitted)
    {
        plugin.Configuration.MiniTabOrder = MiniTabCatalog.Move(
            plugin.Configuration.MiniTabOrder,
            tab,
            direction,
            permitted);
        plugin.Configuration.Save();
    }

    private MiniConditionState ResolveConditions(VenueConnectionConfiguration? venue)
    {
        var hasTarget = plugin.TargetProvider.TryGetCurrentTarget(out var target, out _) && target is not null;
        if (venue is null)
        {
            return new MiniConditionState(hasTarget, false, false, false, false);
        }

        var linkedDj = hasTarget && plugin.Djs.GetSnapshot(venue).View?.Characters.Any(character =>
            CharacterMatches(character.CharacterName, character.WorldName)) == true;

        var staffCharacter = hasTarget
            ? plugin.Staff.GetSnapshot(venue).View?.Characters.FirstOrDefault(character =>
                CharacterMatches(character.CharacterName, character.WorldName))
            : null;
        var linkedStaff = staffCharacter?.StaffMemberId is not null;
        var linkedAccountant = linkedStaff &&
                               plugin.Court.GetSnapshot(venue).View?.AccountantAccounts.Any(account =>
                                   account.CanReceiveSettlements &&
                                   (account.StaffMemberId == staffCharacter!.StaffMemberId ||
                                    staffCharacter.VenueUserId is not null &&
                                    account.AccountantUserId == staffCharacter.VenueUserId)) == true;
        var pendingSettlementCount =
            plugin.Finance.GetSnapshot(venue).View?.VenuePendingCount ??
            plugin.Notifications.GetSummary(venue.ProfileId)?.PendingSettlementCount ??
            0;

        return new MiniConditionState(
            hasTarget,
            linkedDj,
            linkedStaff,
            linkedAccountant,
            pendingSettlementCount > 0);

        bool CharacterMatches(string characterName, string worldName) =>
            target is not null &&
            string.Equals(characterName, target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(worldName, target.WorldName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConditionSatisfied(
        MiniTabCondition condition,
        MiniConditionState state) =>
        condition switch
        {
            MiniTabCondition.None => true,
            MiniTabCondition.AnyTarget => state.HasTarget,
            MiniTabCondition.LinkedDjTarget => state.LinkedDj,
            MiniTabCondition.LinkedStaffTarget => state.LinkedStaff,
            MiniTabCondition.CourtAccountantTarget => state.LinkedAccountant,
            MiniTabCondition.PendingSettlement => state.PendingSettlements,
            _ => false,
        };

    private static bool IsTargetCondition(MiniTabCondition condition) =>
        condition is MiniTabCondition.AnyTarget or
            MiniTabCondition.LinkedDjTarget or
            MiniTabCondition.LinkedStaffTarget or
            MiniTabCondition.CourtAccountantTarget;

    private sealed record MiniConditionState(
        bool HasTarget,
        bool LinkedDj,
        bool LinkedStaff,
        bool LinkedAccountant,
        bool PendingSettlements);
}
