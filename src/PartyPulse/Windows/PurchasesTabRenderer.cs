using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Purchases;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class PurchasesTabRenderer
{
    private const string ConfirmPaidPopup = "Confirm purchase trade###PartyPulseConfirmPurchasePaid";
    private const string RejectPopup = "Reject purchase###PartyPulseRejectPurchase";
    private const string CancelPopup = "Cancel purchase###PartyPulseCancelPurchase";

    private readonly Plugin plugin;
    private Guid activeProfileId;
    private string title = string.Empty;
    private string details = string.Empty;
    private int totalPriceGil;
    private string filter = string.Empty;
    private long selectedPurchaseId;
    private long pendingPurchaseId;
    private string rejectionReason = string.Empty;
    private bool pendingCancellationWasSettled;
    private long pendingCancellationTotalPriceGil;

    public PurchasesTabRenderer(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("Purchases"))
        {
            return;
        }

        ResetForVenue(venue);
        plugin.EnsurePurchasesLoaded(venue);
        var snapshot = plugin.Purchases.GetSnapshot(venue);

        if (ImGui.Button("Refresh Purchases"))
        {
            plugin.RefreshPurchases(venue);
        }

        if (snapshot.Status is PurchaseManagementStatus.NotLoaded or PurchaseManagementStatus.Loading)
        {
            ImGui.TextDisabled(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        if (snapshot.Status is PurchaseManagementStatus.Denied or PurchaseManagementStatus.Failed ||
            snapshot.View is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        var view = snapshot.View;
        var busy = plugin.Purchases.IsBusy(venue.ProfileId);

        if (view.Capabilities.CanCreate)
        {
            DrawCreateForm(venue, view, busy);
            ImGui.Separator();
        }

        DrawHistory(venue, view);
        DrawSelectedPurchase(venue, view, busy);
        DrawConfirmPaidPopup(venue);
        DrawRejectPopup(venue);
        DrawCancelPopup(venue);

        ImGui.EndTabItem();
    }

    private void DrawCreateForm(
        VenueConnectionConfiguration venue,
        PurchasesManagementViewResponse view,
        bool busy)
    {
        ImGui.TextUnformatted("Record a venue purchase");
        ImGui.TextWrapped(
            view.Capabilities.CanManage
                ? "Because you manage finance settlements, purchases you record are approved and settled immediately."
                : "This submits a reimbursement request for approval by a finance settlement manager.");

        ImGui.SetNextItemWidth(420 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Title", ref title, 150);
        ImGui.InputTextMultiline(
            "Details",
            ref details,
            4000,
            new Vector2(0, 105 * ImGuiHelpers.GlobalScale));
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Total price (gil)", ref totalPriceGil, 10_000, 100_000);
        if (totalPriceGil < 0)
        {
            totalPriceGil = 0;
        }

        ImGui.TextDisabled($"Entered total: {totalPriceGil:N0} gil");

        var valid = !string.IsNullOrWhiteSpace(title) &&
                    !string.IsNullOrWhiteSpace(details) &&
                    totalPriceGil is > 0;
        ImGui.BeginDisabled(busy || !valid);
        if (ImGui.Button(view.Capabilities.CanManage ? "Record settled purchase" : "Submit for approval"))
        {
            plugin.CreatePurchase(
                venue,
                new CreatePurchaseRequest(title.Trim(), details.Trim(), totalPriceGil));
            title = string.Empty;
            details = string.Empty;
            totalPriceGil = 0;
        }
        ImGui.EndDisabled();
    }

    private void DrawHistory(
        VenueConnectionConfiguration venue,
        PurchasesManagementViewResponse view)
    {
        ImGui.TextUnformatted("Purchase history");
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##PurchaseFilter", "Filter title, purchaser, or status", ref filter, 150);

        var purchases = view.Purchases
            .Where(MatchesFilter)
            .ToArray();

        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(
                "PurchaseHistory",
                5,
                flags,
                new Vector2(0, 250 * ImGuiHelpers.GlobalScale)))
        {
            return;
        }

        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Title");
        ImGui.TableSetupColumn("Total");
        ImGui.TableSetupColumn("Purchased by");
        ImGui.TableSetupColumn("Created");
        ImGui.TableHeadersRow();

        foreach (var purchase in purchases)
        {
            ImGui.PushID((int)(purchase.PurchaseId % int.MaxValue));
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawStatus(purchase.Status);

            ImGui.TableSetColumnIndex(1);
            if (ImGui.Selectable(
                    purchase.Title,
                    selectedPurchaseId == purchase.PurchaseId,
                    ImGuiSelectableFlags.SpanAllColumns))
            {
                selectedPurchaseId = purchase.PurchaseId;
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted($"{purchase.TotalPriceGil:N0} gil");

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(purchase.CreatedByDisplayName);
            ImGui.TextDisabled($"{purchase.CreatedByCharacterName} @ {purchase.CreatedByWorldName}");

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, purchase.CreatedAt, "g"));
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawSelectedPurchase(
        VenueConnectionConfiguration venue,
        PurchasesManagementViewResponse view,
        bool busy)
    {
        var purchase = view.Purchases.FirstOrDefault(value => value.PurchaseId == selectedPurchaseId);
        if (purchase is null)
        {
            ImGui.TextDisabled("Select a purchase to see its details and actions.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"Purchase #{purchase.PurchaseId}: {purchase.Title}");
        ImGui.SameLine();
        DrawStatus(purchase.Status);
        ImGui.TextUnformatted($"Total: {purchase.TotalPriceGil:N0} gil");
        ImGui.TextWrapped(purchase.Details);
        ImGui.TextDisabled(
            $"Submitted by {purchase.CreatedByDisplayName} as " +
            $"{purchase.CreatedByCharacterName} @ {purchase.CreatedByWorldName} on " +
            VenueTimeZone.Format(venue, purchase.CreatedAt, "g"));

        if (purchase.ApprovedAt is { } approvedAt)
        {
            ImGui.TextDisabled(
                $"Approved by {purchase.ApprovedByDisplayName ?? "unknown"} on " +
                VenueTimeZone.Format(venue, approvedAt, "g"));
        }

        if (purchase.SettledAt is { } settledAt)
        {
            ImGui.TextDisabled(
                $"Settled by {purchase.SettledByDisplayName ?? "unknown"} on " +
                VenueTimeZone.Format(venue, settledAt, "g"));
        }

        if (purchase.RejectedAt is { } rejectedAt)
        {
            ImGui.TextDisabled(
                $"Rejected by {purchase.RejectedByDisplayName ?? "unknown"} on " +
                VenueTimeZone.Format(venue, rejectedAt, "g"));
            ImGui.TextWrapped($"Reason: {purchase.RejectionReason}");
        }

        if (purchase.CancelledAt is { } cancelledAt)
        {
            ImGui.TextDisabled(
                $"Cancelled by {purchase.CancelledByDisplayName ?? "unknown"} on " +
                VenueTimeZone.Format(venue, cancelledAt, "g"));
            if (purchase.SettledAt is not null)
            {
                ImGui.TextWrapped(
                    "The reimbursement was recorded as repaid to the club when this purchase was cancelled.");
            }
        }

        if (!view.Capabilities.CanManage)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(busy);
        if (string.Equals(purchase.Status, "pending_approval", StringComparison.Ordinal))
        {
            if (ImGui.Button("Approve purchase"))
            {
                plugin.ApprovePurchase(venue, purchase.PurchaseId);
            }
            ImGui.SameLine();
            if (ImGui.Button("Reject purchase"))
            {
                pendingPurchaseId = purchase.PurchaseId;
                rejectionReason = string.Empty;
                ImGui.OpenPopup(RejectPopup);
            }
        }
        else if (string.Equals(purchase.Status, "approved", StringComparison.Ordinal))
        {
            if (ImGui.Button($"Pay {purchase.TotalPriceGil:N0} gil with Dropbox"))
            {
                plugin.StartPurchasePayment(venue, purchase);
            }
            ImGui.SameLine();
            if (ImGui.Button("Confirm trade success"))
            {
                pendingPurchaseId = purchase.PurchaseId;
                ImGui.OpenPopup(ConfirmPaidPopup);
            }
            ImGui.SameLine();
            if (ImGui.Button("Reject instead"))
            {
                pendingPurchaseId = purchase.PurchaseId;
                rejectionReason = string.Empty;
                ImGui.OpenPopup(RejectPopup);
            }
            ImGui.TextDisabled(
                $"Target {purchase.CreatedByCharacterName} @ {purchase.CreatedByWorldName} before starting Dropbox.");
        }

        if (purchase.Status is "pending_approval" or "approved" or "settled")
        {
            ImGui.Spacing();
            if (ImGui.Button("Cancel purchase"))
            {
                pendingPurchaseId = purchase.PurchaseId;
                pendingCancellationWasSettled =
                    string.Equals(purchase.Status, "settled", StringComparison.Ordinal);
                pendingCancellationTotalPriceGil = purchase.TotalPriceGil;
                ImGui.OpenPopup(CancelPopup);
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawConfirmPaidPopup(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(ConfirmPaidPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Confirm that the reimbursement trade for purchase #{pendingPurchaseId} completed successfully?");
        ImGui.TextWrapped(
            "Dropbox only starts the trade. Confirm this only after the purchaser has actually received the gil.");

        if (ImGui.Button("Confirm paid"))
        {
            plugin.ConfirmPurchasePaid(venue, pendingPurchaseId);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawRejectPopup(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(RejectPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped($"Reject purchase #{pendingPurchaseId}?");
        ImGui.SetNextItemWidth(420 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Reason", ref rejectionReason, 255);

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(rejectionReason));
        if (ImGui.Button("Reject purchase"))
        {
            plugin.RejectPurchase(
                venue,
                pendingPurchaseId,
                new RejectPurchaseRequest(rejectionReason.Trim()));
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawCancelPopup(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(CancelPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped($"Cancel purchase #{pendingPurchaseId}?");
        if (pendingCancellationWasSettled)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.4f, 0.4f, 1f),
                $"This purchase was already paid from club funds ({pendingCancellationTotalPriceGil:N0} gil).");
            ImGui.TextWrapped(
                "Confirm only after the purchaser has paid the gil back to the club. " +
                "PartyPulse will record the repayment as completed but cannot start or verify the return trade.");
        }
        else
        {
            ImGui.TextWrapped(
                "The purchase will remain in history as cancelled and cannot be approved or paid afterward.");
        }

        var confirmLabel = pendingCancellationWasSettled
            ? "Confirm repaid and cancel"
            : "Confirm cancellation";
        if (ImGui.Button(confirmLabel))
        {
            plugin.CancelPurchase(venue, pendingPurchaseId, pendingCancellationWasSettled);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep purchase"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private bool MatchesFilter(PurchaseSummary purchase)
    {
        var value = filter.Trim();
        return value.Length == 0 ||
               purchase.Title.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               purchase.CreatedByDisplayName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               purchase.CreatedByCharacterName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               purchase.Status.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawStatus(string status)
    {
        var display = status switch
        {
            "pending_approval" => "Pending approval",
            "approved" => "Approved / unpaid",
            "settled" => "Settled",
            "rejected" => "Rejected",
            "cancelled" => "Cancelled",
            _ => status
        };

        var color = status switch
        {
            "pending_approval" => new Vector4(1f, 0.8f, 0.35f, 1f),
            "approved" => new Vector4(0.35f, 0.7f, 1f, 1f),
            "settled" => new Vector4(0.35f, 0.85f, 0.45f, 1f),
            "rejected" => new Vector4(1f, 0.4f, 0.4f, 1f),
            "cancelled" => new Vector4(0.65f, 0.65f, 0.65f, 1f),
            _ => new Vector4(0.75f, 0.75f, 0.75f, 1f)
        };
        ImGui.TextColored(color, display);
    }

    private void ResetForVenue(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
        {
            return;
        }

        activeProfileId = venue.ProfileId;
        title = string.Empty;
        details = string.Empty;
        totalPriceGil = 0;
        filter = string.Empty;
        selectedPurchaseId = 0;
        pendingPurchaseId = 0;
        rejectionReason = string.Empty;
        pendingCancellationWasSettled = false;
        pendingCancellationTotalPriceGil = 0;
    }
}
