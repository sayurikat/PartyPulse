using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed partial class DjsTabRenderer
{
    private const string PaymentCloseOutPopup = "Record unpaid DJ set###DjPaymentCloseOut";
    private long? selectedHistoryDjId;
    private PaymentCloseOutDraft? pendingCloseOut;
    private bool closeOutRefundConfirmed;

    private void DrawPaymentHistory(VenueConnectionConfiguration venue, bool isBusy)
    {
        var snapshot = plugin.Djs.GetPaymentHistory(venue);
        if (snapshot is null || snapshot.DjId != selectedHistoryDjId)
        {
            if (!isBusy)
                plugin.LoadDjPaymentHistory(venue, selectedHistoryDjId);
            ImGui.TextDisabled("Loading payment history…");
            return;
        }

        var history = snapshot.View;
        if (history is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        var selectedDj = history.Djs.FirstOrDefault(dj => dj.DjId == selectedHistoryDjId);
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.BeginDisabled(isBusy);
        if (ImGui.BeginCombo("DJ##PaymentHistory", selectedDj?.DjName ?? "Select DJ"))
        {
            foreach (var dj in history.Djs.OrderBy(dj => dj.DjName, StringComparer.OrdinalIgnoreCase))
            {
                if (ImGui.Selectable($"{dj.DjName}##PaymentHistory{dj.DjId}", dj.DjId == selectedHistoryDjId))
                    selectedHistoryDjId = dj.DjId;
            }

            ImGui.EndCombo();
        }
        ImGui.EndDisabled();

        if (selectedDj is null || selectedDj.DjId != selectedHistoryDjId)
        {
            if (history.Djs.Count == 0)
                ImGui.TextDisabled("There are no DJs in this venue.");
            return;
        }

        if (history.Entries.Count == 0)
        {
            ImGui.TextDisabled("No payment history for this DJ.");
            return;
        }

        ImGui.TextWrapped("Mark a trade after gil changes hands. Cancel a payment to record the set as unpaid.");
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("DjPaymentHistory", 5, flags, new Vector2(0, 350 * ImGuiHelpers.GlobalScale)))
            return;

        ImGui.TableSetupColumn("Scheduled set");
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Gil");
        ImGui.TableSetupColumn("Trade target");
        ImGui.TableSetupColumn("Actions");
        ImGui.TableHeadersRow();

        var openCloseOut = false;
        foreach (var entry in history.Entries)
        {
            ImGui.PushID($"dj-payment-{entry.BookingId}-{entry.PaymentId}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, entry.StartsAt, "yyyy-MM-dd HH:mm"));
            ImGui.TextDisabled($"to {VenueTimeZone.Format(venue, entry.EndsAt, "yyyy-MM-dd HH:mm")}");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(entry.Status switch
            {
                DjPaymentStatusCodes.Scheduled => "Scheduled",
                DjPaymentStatusCodes.Started => "Pending",
                DjPaymentStatusCodes.Traded => "Traded",
                DjPaymentStatusCodes.Paid => "Confirmed",
                DjPaymentStatusCodes.Cancelled => "Cancelled",
                _ => entry.Status
            });
            if (entry.Status == DjPaymentStatusCodes.Cancelled)
            {
                ImGui.TextDisabled(entry.CancellationReason switch
                {
                    DjPaymentCancellationReasons.OptedOut => "DJ opted out",
                    DjPaymentCancellationReasons.Cancelled => "Set recorded as unpaid",
                    _ => entry.PaymentId is null ? "Set or opening cancelled" : "Attempt discarded"
                });
            }
            if ((entry.CancelledAt ?? entry.CompletedAt ?? entry.TradedAt ?? entry.StartedAt) is { } updated)
                ImGui.TextDisabled(VenueTimeZone.Format(venue, updated, "yyyy-MM-dd HH:mm"));
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted($"{entry.AmountGil:N0}");
            ImGui.TableSetColumnIndex(3);
            if (entry.TargetCharacterName is { Length: > 0 } targetName)
                ImGui.TextWrapped($"{targetName} @ {entry.TargetWorldName}{(entry.PaidViaProxy ? " (proxy)" : string.Empty)}");
            ImGui.TableSetColumnIndex(4);
            ImGui.BeginDisabled(isBusy);
            if (entry.PaymentId is { } paymentId)
            {
                if (entry.Status == DjPaymentStatusCodes.Started && ImGui.SmallButton("Mark traded"))
                    plugin.MarkDjPaymentTraded(venue, paymentId);
                else if (entry.Status == DjPaymentStatusCodes.Traded && ImGui.SmallButton("Confirm paid"))
                    plugin.ConfirmDjPayment(venue, paymentId);
            }
            if ((entry.Status is DjPaymentStatusCodes.Scheduled or DjPaymentStatusCodes.Started or
                DjPaymentStatusCodes.Traded or DjPaymentStatusCodes.Paid) && ImGui.SmallButton("Cancel payment"))
            {
                pendingCloseOut = new(entry.BookingId, entry.DjId, entry.PaymentId, entry.AmountGil,
                    DjPaymentCancellationReasons.Cancelled);
                closeOutRefundConfirmed = false;
                openCloseOut = true;
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (openCloseOut)
            ImGui.OpenPopup(PaymentCloseOutPopup);
    }

    private void DrawPaymentOptOuts(VenueConnectionConfiguration venue, DjViewResponse view, bool isBusy)
    {
        var now = DateTimeOffset.UtcNow;
        var eligible = view.Bookings
            .Where(booking => booking.DjId == selectedPayoutDjId && booking.IsPaymentEligible(now))
            .OrderBy(booking => booking.StartsAt)
            .ToArray();
        if (eligible.Length == 0)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted("Eligible sets");
        ImGui.BeginDisabled(isBusy);
        foreach (var booking in eligible)
        {
            ImGui.TextUnformatted($"{VenueTimeZone.Format(venue, booking.StartsAt, "yyyy-MM-dd HH:mm")} · {booking.PriceGil:N0} gil");
            ImGui.SameLine();
            if (ImGui.SmallButton($"DJ opt out payment##{booking.BookingId}"))
            {
                pendingCloseOut = new(booking.BookingId, booking.DjId, null, booking.PriceGil,
                    DjPaymentCancellationReasons.OptedOut);
                closeOutRefundConfirmed = false;
                ImGui.OpenPopup(PaymentCloseOutPopup);
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawPaymentCloseOutPopup(VenueConnectionConfiguration venue, bool isBusy)
    {
        if (!ImGui.BeginPopupModal(PaymentCloseOutPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (pendingCloseOut is { } draft)
        {
            var optedOut = draft.Reason == DjPaymentCancellationReasons.OptedOut;
            ImGui.TextWrapped(optedOut
                ? "Confirm that the DJ opted out of payment for this set. The set will be recorded as unpaid."
                : $"Cancel this {draft.AmountGil:N0} gil payment? The set will be recorded as unpaid.");
            if (draft.PaymentId is not null)
                ImGui.Checkbox("I confirm any gil paid has already been returned in full", ref closeOutRefundConfirmed);

            ImGui.BeginDisabled(isBusy || (draft.PaymentId is not null && !closeOutRefundConfirmed));
            if (ImGui.Button(optedOut ? "Confirm opt-out" : "Confirm cancellation"))
            {
                plugin.CloseOutDjPayment(venue, draft.BookingId,
                    new CloseOutDjPaymentRequest(draft.DjId, draft.PaymentId, closeOutRefundConfirmed, draft.Reason));
                pendingCloseOut = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
        }

        if (ImGui.Button("Back"))
        {
            pendingCloseOut = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private sealed record PaymentCloseOutDraft(long BookingId, long DjId, long? PaymentId, long AmountGil, string Reason);
}
