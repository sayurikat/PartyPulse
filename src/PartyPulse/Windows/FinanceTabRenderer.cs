using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Finance;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class FinanceTabRenderer(Plugin plugin)
{
    private Guid activeProfileId;
    private long selectedSettlementId;
    private long pendingResponseSettlementId;
    private string responseDecision = string.Empty;
    private string responseNote = string.Empty;

    public bool Draw(
        VenueConnectionConfiguration venue,
        bool requestSelection,
        long? requestedSettlementId)
    {
        var summary = plugin.Notifications.GetSummary(venue.ProfileId);
        var pendingCount = summary?.PendingSettlementCount ??
                           plugin.Finance.GetSnapshot(venue).View?.VenuePendingCount ?? 0;
        var label = pendingCount > 0
            ? $"Finances ({pendingCount})###PartyPulseFinances"
            : "Finances###PartyPulseFinances";
        var flags = requestSelection ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (!ImGui.BeginTabItem(label, flags))
        {
            return false;
        }

        if (activeProfileId != venue.ProfileId)
        {
            activeProfileId = venue.ProfileId;
            selectedSettlementId = 0;
        }

        if (requestedSettlementId is { } requestedId && requestedId > 0)
        {
            selectedSettlementId = requestedId;
        }

        plugin.EnsureFinanceLoaded(venue);
        var snapshot = plugin.Finance.GetSnapshot(venue);
        var busy = plugin.Finance.IsBusy(venue.ProfileId);

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh finance data"))
        {
            plugin.RefreshFinance(venue);
        }
        ImGui.EndDisabled();

        if (snapshot.Status != FinanceManagementStatus.Ready || snapshot.View is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return true;
        }

        var view = snapshot.View;
        ImGui.SameLine();
        ImGui.TextDisabled("Transactions are server-audited and remain visible after resolution.");

        ImGui.Spacing();
        ImGui.TextUnformatted($"My unsettled VIP sales: {view.PersonalUnpaidVipGil:N0} gil");
        ImGui.SameLine();
        ImGui.TextDisabled($"Pending: {view.PersonalPendingVipGil:N0} gil");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Available: {view.PersonalAvailableVipGil:N0} gil");

        if (view.Capabilities.CanManageSettlements)
        {
            ImGui.TextUnformatted($"Venue pending transactions: {view.VenuePendingCount}");
        }

        ImGui.Spacing();
        DrawSettlementList(venue, view);
        ImGui.Spacing();
        DrawSelectedSettlement(venue, view, busy);
        DrawResponseConfirmation(venue);

        ImGui.EndTabItem();
        return true;
    }

    private void DrawSettlementList(VenueConnectionConfiguration venue, FinanceViewResponse view)
    {
        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(
                "FinanceSettlements",
                6,
                flags,
                new Vector2(0, 230 * ImGuiHelpers.GlobalScale)))
        {
            return;
        }

        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Amount");
        ImGui.TableSetupColumn("From");
        ImGui.TableSetupColumn("To");
        ImGui.TableSetupColumn("Created");
        ImGui.TableSetupColumn("Items");
        ImGui.TableHeadersRow();

        foreach (var settlement in view.Settlements)
        {
            ImGui.PushID((int)(settlement.SettlementId % int.MaxValue));
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var selected = selectedSettlementId == settlement.SettlementId;
            if (ImGui.Selectable(settlement.Status, selected))
            {
                selectedSettlementId = settlement.SettlementId;
            }
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{settlement.AmountGil:N0} gil");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted($"{settlement.InitiatedByDisplayName}\n{settlement.InitiatedByCharacterName}");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted($"{settlement.TargetUserDisplayName}\n{settlement.TargetCharacterName}");
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, settlement.CreatedAt, "g"));
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(settlement.ItemCount.ToString());
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawSelectedSettlement(
        VenueConnectionConfiguration venue,
        FinanceViewResponse view,
        bool busy)
    {
        var settlement = view.Settlements.FirstOrDefault(value => value.SettlementId == selectedSettlementId);
        if (settlement is null)
        {
            ImGui.TextDisabled("Select a settlement transaction to see its details.");
            return;
        }

        ImGui.TextUnformatted($"Settlement #{settlement.SettlementId} — {settlement.Status}");
        ImGui.TextUnformatted($"{settlement.AmountGil:N0} gil from {settlement.InitiatedByDisplayName} to {settlement.TargetUserDisplayName}");
        ImGui.TextDisabled(
            $"Initiated by {settlement.InitiatedByCharacterName} @ {settlement.InitiatedByWorldName}; " +
            $"targeted {settlement.TargetCharacterName} @ {settlement.TargetWorldName}");

        if (settlement.RespondedAt is { } respondedAt)
        {
            ImGui.TextUnformatted(
                $"Resolved {VenueTimeZone.Format(venue, respondedAt, "g")} by {settlement.RespondedByDisplayName ?? "unknown"}.");
            if (!string.IsNullOrWhiteSpace(settlement.ResponseNote))
            {
                ImGui.TextWrapped($"Note: {settlement.ResponseNote}");
            }
        }

        var items = view.Items
            .Where(value => value.SettlementId == settlement.SettlementId)
            .OrderBy(value => value.SettlementItemId)
            .ToArray();
        if (ImGui.BeginTable(
                "FinanceSettlementItems",
                4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("VIP player");
            ImGui.TableSetupColumn("Package");
            ImGui.TableSetupColumn("Amount");
            ImGui.TableHeadersRow();
            foreach (var item in items)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"{item.SourceType} #{item.SourceId}");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(
                    !string.IsNullOrWhiteSpace(item.VipCharacterName)
                        ? $"{item.VipCharacterName} @ {item.VipWorldName}"
                        : item.VipPlayerId is { } vipPlayerId ? $"VIP #{vipPlayerId}" : "-");
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(item.PackageName ?? "-");
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted($"{item.AmountGil:N0} gil");
            }
            ImGui.EndTable();
        }

        if (!view.Capabilities.CanManageSettlements || !settlement.IsPending)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Confirm payment received"))
        {
            pendingResponseSettlementId = settlement.SettlementId;
            responseDecision = "confirm";
            responseNote = string.Empty;
            ImGui.OpenPopup("Resolve settlement###PartyPulseResolveSettlement");
        }
        ImGui.SameLine();
        if (ImGui.Button("Reject transaction"))
        {
            pendingResponseSettlementId = settlement.SettlementId;
            responseDecision = "reject";
            responseNote = string.Empty;
            ImGui.OpenPopup("Resolve settlement###PartyPulseResolveSettlement");
        }
        ImGui.EndDisabled();
    }

    private void DrawResponseConfirmation(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(
                "Resolve settlement###PartyPulseResolveSettlement",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var confirm = string.Equals(responseDecision, "confirm", StringComparison.Ordinal);
        ImGui.TextWrapped(confirm
            ? $"Confirm that settlement #{pendingResponseSettlementId} was paid to the club?"
            : $"Reject settlement #{pendingResponseSettlementId} as invalid or unpaid?");
        ImGui.TextWrapped(confirm
            ? "Confirming marks every included source payment as settled."
            : "Rejecting releases the included source payments so a new settlement can be initiated.");
        ImGui.SetNextItemWidth(420 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Note (optional)", ref responseNote, 255);
        if (ImGui.Button(confirm ? "Confirm settlement" : "Reject settlement"))
        {
            plugin.RespondSettlement(
                venue,
                pendingResponseSettlementId,
                new RespondSettlementRequest(responseDecision, responseNote));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
