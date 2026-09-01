using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using PartyPulse.Api;
using PartyPulse.Court;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class CourtTabRenderer(Plugin plugin)
{
    private const string CancelSalePopupName =
        "Cancel Court sale###PartyPulseCourtCancel";
    private const string CancelTransactionPopupName =
        "Cancel Court transaction###PartyPulseCourtCancelTransaction";

    private static readonly Vector4 AvailableColor = new(0.35f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 UnavailableColor = new(0.95f, 0.25f, 0.25f, 1f);
    private static readonly Vector4 DueColor = new(1f, 0.72f, 0.25f, 1f);
    private static readonly Vector4 ZeroBalanceColor = new(0.25f, 1f, 0.46f, 1f);

    private Guid activeProfileId;
    private long selectedOfferId;
    private int saleQuantity = 1;
    private long editingOfferId;
    private string offerName = string.Empty;
    private int durationMinutes = 30;
    private bool perkPrice;
    private int priceGil;
    private int pricePerkId;
    private bool offerArchived;
    private int collectorMode;
    private string previewTargetName = string.Empty;
    private string previewTargetWorld = string.Empty;
    private string previewCollectorMode = string.Empty;
    private decimal loadedCourtKeepPercentage = -1m;
    private float courtKeepPercentage;
    private int prepayGil;
    private long selectedAccountId;
    private long pendingCancelSaleId;
    private bool openCancelSalePopup;
    private bool pendingSaleRequiresRefund;
    private bool refundConfirmed;
    private long pendingRefundGil;
    private string pendingRefundBuyer = string.Empty;
    private long pendingCancelTransactionId;
    private bool openCancelTransactionPopup;
    private string cancelReason = string.Empty;

    public void Draw(VenueConnectionConfiguration venue, MainSubtab subtab)
    {
        ResetForVenue(venue);
        plugin.EnsureCourtLoaded(venue);
        plugin.EnsureVipPerksLoaded(venue);
        plugin.EnsureTimedMacrosLoaded(venue);

        var snapshot = plugin.Court.GetSnapshot(venue);
        var busy = plugin.Court.IsBusy(venue.ProfileId);

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh Court Services"))
        {
            plugin.RefreshCourt(venue);
        }
        ImGui.EndDisabled();

        if (snapshot.Status != CourtManagementStatus.Ready || snapshot.View is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        var view = snapshot.View;
        SyncCommissionEditor(view);
        switch (subtab)
        {
            case MainSubtab.CourtSales when view.Capabilities.CanSell:
            {
                DrawCourtTimedMacro(venue);
                var personalNetGil =
                    (decimal)view.PersonalUnsettledCourtGil +
                    view.PersonalAdjustmentGil -
                    view.PersonalUnpaidSalaryGil;
                ImGui.TextDisabled(
                    $"Unsettled Court sales: {view.PersonalUnsettledCourtGil:N0} | " +
                    $"corrections: {view.PersonalAdjustmentGil:+#,0;-#,0;0} | " +
                    $"unpaid salary: {view.PersonalUnpaidSalaryGil:N0} | " +
                    $"net: {personalNetGil:+#,0;-#,0;0} gil");
                DrawSale(venue, view, busy);
                break;
            }
            case MainSubtab.CourtSettlements
                when view.Capabilities.CanFinance || view.Capabilities.CanAccount:
                DrawUnsettledCourtStaff(venue, view);
                DrawSettlement(venue, view, busy);
                break;
            case MainSubtab.CourtCommission when view.Capabilities.CanManageCommission:
                DrawCommissionSettings(venue, busy);
                break;
            case MainSubtab.CourtOffers when view.Capabilities.CanManage:
                DrawOfferManagement(venue, view, busy);
                break;
            case MainSubtab.CourtAccountants
                when view.Capabilities.CanFinance || view.Capabilities.CanAccount:
                DrawAccountants(venue, view, busy);
                break;
            case MainSubtab.CourtTransactions:
                DrawTransactions(venue, view, busy);
                break;
            case MainSubtab.CourtSalesHistory:
                DrawSales(venue, view, busy);
                break;
        }

        OpenQueuedPopups();
        DrawSaleCancellationPopup(venue, busy);
        DrawTransactionCancellationPopup(venue, busy);
    }

    private void DrawCourtTimedMacro(VenueConnectionConfiguration venue)
    {
        var snapshot = plugin.TimedMacros.GetSnapshot(venue);
        var view = snapshot.View;
        var macro = view?.Macros.FirstOrDefault(value =>
            string.Equals(value.TypeCode, TimedMacroTypeCodes.CourtAdvertisement, StringComparison.OrdinalIgnoreCase) &&
            value.CanExecute);
        if (view is null || macro is null)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Court Service advertisement");
        var opening = view.CurrentOpening;
        var locationMessage = string.Empty;
        var atAddress = opening is not null && plugin.LocationProvider.IsAtOpeningLocation(
            opening.AddressWorldName,
            opening.AddressCityName,
            opening.AddressWard,
            opening.AddressPlot,
            opening.LocationType,
            opening.OutdoorLocationName,
            out locationMessage);
        var now = snapshot.EstimatedServerNow;
        var stateText = opening is null
            ? "Paused: no active opening"
            : !atAddress
                ? "Paused: not at opening location"
                : macro.NextDueAt is not { } dueAt || dueAt <= now
                    ? "Due now"
                    : $"Next in {FormatTimedMacroRemaining(dueAt - now)}";
        if (stateText == "Due now")
        {
            ImGui.TextColored(DueColor, stateText);
            ImGui.SameLine();
            ImGui.TextDisabled($"· every {macro.IntervalMinutes} minutes · shared across accountants");
        }
        else
        {
            ImGui.TextDisabled($"{stateText} · every {macro.IntervalMinutes} minutes · shared across accountants");
        }

        ImGui.SameLine();
        var canExecute = opening is not null && atAddress && macro.Enabled && macro.IsConfigured;
        ImGui.BeginDisabled(
            plugin.TimedMacros.IsBusy(venue.ProfileId) ||
            plugin.IsGameMacroBusy ||
            !canExecute);
        if (ImGui.SmallButton("Execute Court Service ad"))
            plugin.RunTimedMacro(venue, macro, opening!);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canExecute)
        {
            ImGui.SetTooltip(
                opening is null
                    ? "There is no active opening."
                    : !atAddress
                        ? locationMessage
                        : !macro.Enabled
                            ? "The Court Service advertisement macro is disabled."
                            : "The Court Service advertisement macro has not been configured.");
        }
        else if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Running early is allowed and resets the shared timer.");
        }
    }

    private static string FormatTimedMacroRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";


    private void DrawUnsettledCourtStaff(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view)
    {
        var rows = view.UnsettledStaff
            .OrderByDescending(static row => row.RequiresSettlement)
            .ThenByDescending(static row => row.OpenTimeEntryCount)
            .ThenBy(static row => row.StaffDisplayName)
            .ToArray();
        if (rows.Length == 0)
        {
            ImGui.TextDisabled("No Court sellers, open Court clock-ins, unpaid salaries, or unsettled Court balances need attention.");
            return;
        }

        ImGui.TextDisabled(
            "Open clock-ins are reminders only; Court staff can still be settled before they clock out.");

        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("CourtUnsettledStaffTable", 8, flags))
        {
            return;
        }

        ImGui.TableSetupColumn("Staff", ImGuiTableColumnFlags.WidthFixed, 180f);
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn("Court sell", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Salary", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Adjustments", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Open", ImGuiTableColumnFlags.WidthFixed, 115f);
        ImGui.TableSetupColumn("Net", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.StaffDisplayName);

            ImGui.TableNextColumn();
            if (!string.IsNullOrWhiteSpace(row.StaffCharacterName) &&
                !string.IsNullOrWhiteSpace(row.StaffWorldName))
            {
                ImGui.TextUnformatted($"{row.StaffCharacterName} @ {row.StaffWorldName}");
            }
            else
            {
                ImGui.TextDisabled("No linked character");
            }

            ImGui.TableNextColumn();
            if (row.HasCourtSellPermission)
            {
                ImGui.TextColored(AvailableColor, "Yes");
            }
            else
            {
                ImGui.TextColored(DueColor, "Former");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.UnsettledCourtGil:N0}");
            if (row.UnsettledSaleCount > 0)
            {
                ImGui.TextDisabled($"{row.UnsettledSaleCount:N0} sale{(row.UnsettledSaleCount == 1 ? string.Empty : "s")}");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.UnpaidSalaryGil:N0}");
            if (row.UnpaidSalaryEntryCount > 0)
            {
                ImGui.TextDisabled($"{row.UnpaidSalaryEntryCount:N0} unpaid");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.UnsettledAdjustmentGil:+#,0;-#,0;0}");
            if (row.UnsettledAdjustmentCount > 0)
            {
                ImGui.TextDisabled($"{row.UnsettledAdjustmentCount:N0} adjustment{(row.UnsettledAdjustmentCount == 1 ? string.Empty : "s")}");
            }

            ImGui.TableNextColumn();
            if (row.OpenTimeEntryCount > 0)
            {
                ImGui.TextColored(DueColor, $"{row.OpenTimeEntryCount:N0} open");
                if (row.FirstOpenClockInAt is { } firstOpen)
                {
                    ImGui.TextDisabled(VenueTimeZone.Format(venue, firstOpen, "g"));
                }
            }
            else
            {
                ImGui.TextDisabled("None");
            }

            ImGui.TableNextColumn();
            var netColor = row.NetGil > 0
                ? AvailableColor
                : row.NetGil < 0
                    ? UnavailableColor
                    : ZeroBalanceColor;
            ImGui.TextColored(netColor, $"{row.NetGil:+#,0;-#,0;0}");
            if (row.RequiresSettlement)
            {
                ImGui.TextDisabled("ready");
            }
        }

        ImGui.EndTable();
    }

    private void DrawCommissionSettings(
        VenueConnectionConfiguration venue,
        bool busy)
    {
        ImGui.TextWrapped(
            "This percentage is retained by the Court worker from gil sales. " +
            "Each sale snapshots the percentage, so changing it does not rewrite existing sales.");
        ImGui.SetNextItemWidth(180f);
        ImGui.InputFloat("Court keeps (%)", ref courtKeepPercentage, 0.25f, 1f, "%.2f");
        courtKeepPercentage = Math.Clamp(courtKeepPercentage, 0f, 100f);
        ImGui.TextDisabled($"Saved: {loadedCourtKeepPercentage:0.##}%");
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Save Court percentage"))
        {
            plugin.UpdateCourtSettings(
                venue,
                new UpdateCourtSettingsRequest((decimal)courtKeepPercentage));
        }
        ImGui.EndDisabled();
    }

    private void SyncCommissionEditor(CourtManagementViewResponse view)
    {
        if (loadedCourtKeepPercentage == view.CourtKeepPercentage)
            return;

        loadedCourtKeepPercentage = view.CourtKeepPercentage;
        courtKeepPercentage = (float)view.CourtKeepPercentage;
    }

    private void DrawSale(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        bool busy)
    {
        var offers = view.Offers
            .Where(offer => offer.ArchivedAt is null)
            .OrderBy(offer => offer.Name)
            .ToArray();
        if (offers.Length == 0)
        {
            ImGui.TextDisabled("No active Court Service offers are configured.");
            return;
        }

        if (selectedOfferId == 0 || offers.All(offer => offer.OfferId != selectedOfferId))
        {
            selectedOfferId = offers[0].OfferId;
        }

        var selected = offers.First(offer => offer.OfferId == selectedOfferId);
        if (ImGui.BeginCombo("Service", $"{selected.Name} — {FormatPrice(selected)}"))
        {
            foreach (var offer in offers)
            {
                var isSelected = offer.OfferId == selectedOfferId;
                if (ImGui.Selectable(
                        $"{offer.Name} — {offer.DurationMinutes} min — {FormatPrice(offer)}",
                        isSelected))
                {
                    selectedOfferId = offer.OfferId;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetError))
        {
            ImGui.TextDisabled(targetError);
            return;
        }

        ImGui.TextUnformatted($"Target: {target!.DisplayName}");
        DrawTargetVipStatus(venue, view, selected, target.CharacterName, target.WorldName);

        if (selected.PriceType == "perk")
        {
            saleQuantity = 1;
            ImGui.TextDisabled("Quantity: 1 (VIP Perk redemptions are one service per redemption).");
        }
        else
        {
            ImGui.InputInt("Quantity", ref saleQuantity);
            saleQuantity = Math.Clamp(saleQuantity, 1, 100);
        }

        var totalDuration = selected.DurationMinutes * saleQuantity;
        var unitPrice = selected.PriceGil.GetValueOrDefault();
        var priceTooLarge = unitPrice > 0 && unitPrice > long.MaxValue / saleQuantity;
        var totalPrice = priceTooLarge ? 0 : unitPrice * saleQuantity;
        if (selected.PriceType == "perk")
        {
            ImGui.TextUnformatted($"Booking: 1 × {selected.DurationMinutes} min = {totalDuration:N0} min");
        }
        else if (priceTooLarge)
        {
            ImGui.TextColored(UnavailableColor, "The calculated sale price is too large.");
        }
        else
        {
            ImGui.TextUnformatted(
                $"Booking: {saleQuantity} × {selected.DurationMinutes} min = {totalDuration:N0} min; " +
                $"{saleQuantity} × {unitPrice:N0} = {totalPrice:N0} gil");
        }

        var perkAvailability = selected.PricePerkId is { } perkId
            ? view.VipPerkAvailability.FirstOrDefault(value =>
                value.PerkId == perkId &&
                value.CharacterName.Equals(target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                value.WorldName.Equals(target.WorldName, StringComparison.OrdinalIgnoreCase))
            : null;
        var canSell = selected.PriceType != "perk" || perkAvailability?.Available == true;

        ImGui.BeginDisabled(busy || !canSell || priceTooLarge);
        if (ImGui.Button("Confirm sale"))
        {
            plugin.SellCourtService(
                venue,
                new SellCourtServiceRequest(
                    selected.OfferId,
                    saleQuantity,
                    target.CharacterName,
                    target.WorldName));
        }
        ImGui.EndDisabled();

        DrawTargetSalesLast24Hours(venue, view, target.CharacterName, target.WorldName);
    }

    private static void DrawTargetVipStatus(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        CourtOfferSummary selected,
        string characterName,
        string worldName)
    {
        var vipStatus = view.VipStatuses.FirstOrDefault(value =>
            value.CharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase) &&
            value.WorldName.Equals(worldName, StringComparison.OrdinalIgnoreCase));
        if (vipStatus is not null)
        {
            ImGui.TextColored(
                AvailableColor,
                $"VIP: {vipStatus.VipPackageName}" +
                (vipStatus.EndsAt is { } end
                    ? $" until {VenueTimeZone.Format(venue, end, "g")}"
                    : " (lifetime)"));
        }
        else
        {
            ImGui.TextColored(
                UnavailableColor,
                "No active VIP package was found for this character.");
        }

        if (selected.PricePerkId is not { } perkId)
        {
            return;
        }

        var availability = view.VipPerkAvailability.FirstOrDefault(value =>
            value.PerkId == perkId &&
            value.CharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase) &&
            value.WorldName.Equals(worldName, StringComparison.OrdinalIgnoreCase));
        if (availability?.Available == true)
        {
            ImGui.TextColored(
                AvailableColor,
                $"{selected.PricePerkName ?? "VIP Perk"}: available");
            return;
        }

        var unavailable = availability?.NextResetAt is { } next
            ? $"{selected.PricePerkName ?? "VIP Perk"}: next available {VenueTimeZone.Format(venue, next, "g")}"
            : $"{selected.PricePerkName ?? "VIP Perk"}: not available";
        ImGui.TextColored(UnavailableColor, unavailable);
    }

    private static void DrawTargetSalesLast24Hours(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        string characterName,
        string worldName)
    {
        var cutoff = view.ServerNow.AddHours(-24);
        var recent = view.Sales
            .Where(sale =>
                sale.IsOwnSale &&
                sale.VoidedAt is null &&
                sale.SoldAt >= cutoff &&
                sale.BuyerCharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase) &&
                sale.BuyerWorldName.Equals(worldName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(sale => sale.SoldAt)
            .ThenByDescending(sale => sale.SaleId)
            .ToArray();

        ImGui.Spacing();
        ImGui.TextUnformatted("Your sales to this target in the last 24 hours");
        if (recent.Length == 0)
        {
            ImGui.TextDisabled("No Court Service sales registered by you to this target.");
            return;
        }

        var totalMinutes = recent.Sum(sale => (long)sale.TotalDurationMinutes);
        var totalGil = recent.Sum(sale => sale.TotalPriceGil);
        ImGui.TextDisabled(
            $"{recent.Length} sale(s), {totalMinutes:N0} total minutes" +
            (totalGil > 0 ? $", {totalGil:N0} total gil" : string.Empty));

        foreach (var sale in recent)
        {
            var price = sale.PriceType == "perk"
                ? sale.PricePerkName ?? "VIP Perk"
                : $"{sale.TotalPriceGil:N0} gil";
            ImGui.BulletText(
                $"{VenueTimeZone.Format(venue, sale.SoldAt, "g")} — " +
                $"{sale.Quantity} × {sale.UnitDurationMinutes} min = " +
                $"{sale.TotalDurationMinutes:N0} min — {sale.OfferName} — {price}");
        }
    }

    private void DrawSettlement(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        bool busy)
    {
        var canFinance = view.Capabilities.CanFinance;
        var canAccount = view.Capabilities.CanAccount;
        if (canFinance && canAccount)
        {
            var modes = new[] { "Venue finance", "My Court Accountant balance" };
            if (ImGui.Combo("Settle as", ref collectorMode, modes, modes.Length))
                plugin.ClearCourtStaffSettlementPreview(venue);
        }
        else
        {
            collectorMode = canAccount ? 1 : 0;
            ImGui.TextUnformatted(
                canAccount
                    ? "Settle as: My Court Accountant balance"
                    : "Settle as: Venue finance");
        }

        ImGui.TextDisabled(
            "Preview first. Creating the settlement only reserves the source rows; " +
            "Dropbox starts only when you click Execute with Dropbox below.");

        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetError))
        {
            ImGui.TextDisabled(targetError);
            plugin.ClearCourtStaffSettlementPreview(venue);
            previewTargetName = string.Empty;
            previewTargetWorld = string.Empty;
            return;
        }

        var mode = collectorMode == 0 ? "finance" : "accountant";
        if (!string.Equals(previewTargetName, target!.CharacterName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previewTargetWorld, target.WorldName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previewCollectorMode, mode, StringComparison.Ordinal))
        {
            plugin.ClearCourtStaffSettlementPreview(venue);
            previewTargetName = target.CharacterName;
            previewTargetWorld = target.WorldName;
            previewCollectorMode = mode;
        }

        ImGui.TextUnformatted($"Court worker target: {target.DisplayName}");
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Preview settlement"))
        {
            plugin.PreviewCourtStaffSettlement(
                venue,
                new CreateCourtStaffSettlementRequest(
                    mode,
                    target.CharacterName,
                    target.WorldName,
                    null));
        }
        ImGui.EndDisabled();

        var preview = plugin.Court.GetSettlementPreview(venue.ProfileId);
        if (preview is null ||
            !preview.StaffCharacterName.Equals(target.CharacterName, StringComparison.OrdinalIgnoreCase) ||
            !preview.StaffWorldName.Equals(target.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.TextDisabled("No current preview. Preview again after any sale, salary, or cancellation changes.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"Settlement preview for {preview.StaffDisplayName}");
        ImGui.BulletText($"Gross gil sales: {preview.GrossSalesGil:N0}");
        ImGui.BulletText($"Court worker retained share: {preview.CourtRetainedGil:N0}");
        ImGui.BulletText($"Venue share from Court sales: {preview.VenueShareGil:N0}");
        ImGui.BulletText($"Unpaid salary: {preview.SalaryGil:N0}");
        ImGui.BulletText($"Paid-entry salary deductions: {preview.SalaryDeductionGil:N0}");
        ImGui.BulletText($"Other Court adjustments: {preview.AdjustmentGil:+#,0;-#,0;0}");
        ImGui.TextDisabled(
            $"Sources: {preview.SaleCount} sale(s), {preview.TimeEntryCount} salary entry/entries, " +
            $"{preview.AdjustmentCount} adjustment(s).");

        if (preview.TradeDirection == "staff_to_collector")
        {
            ImGui.TextColored(
                UnavailableColor,
                $"COURT WORKER OWES VENUE: {preview.TradeAmountGil:N0} gil");
        }
        else if (preview.TradeDirection == "collector_to_staff")
        {
            ImGui.TextUnformatted($"Venue/accountant pays Court worker: {preview.TradeAmountGil:N0} gil");
        }
        else
        {
            ImGui.TextColored(AvailableColor, "No gil trade required; accounting entries net to zero.");
        }

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Create settlement from this preview"))
        {
            plugin.CreateCourtStaffSettlement(
                venue,
                new CreateCourtStaffSettlementRequest(
                    mode,
                    target.CharacterName,
                    target.WorldName,
                    null));
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled(
            "The server recalculates and locks the source rows when creating, so the final transaction remains authoritative.");
    }

    private void DrawOfferManagement(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        bool busy)
    {
        var perks = plugin.VipPerks.GetSnapshot(venue).View?.Perks
            .Where(perk => perk.ArchivedAt is null)
            .OrderBy(perk => perk.Name)
            .ToArray() ?? Array.Empty<VipPerkSummary>();

        foreach (var offer in view.Offers
                     .OrderBy(offer => offer.ArchivedAt is not null)
                     .ThenBy(offer => offer.Name))
        {
            ImGui.PushID($"court-offer-{offer.OfferId}");
            ImGui.TextUnformatted(
                $"{offer.Name} — {offer.DurationMinutes} min — {FormatPrice(offer)}" +
                (offer.ArchivedAt is null ? string.Empty : " (archived)"));
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit"))
            {
                LoadOffer(offer);
            }
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(editingOfferId == 0 ? "New offer" : "Edit offer");
        ImGui.InputText("Name##CourtOffer", ref offerName, 100);
        ImGui.InputInt("Duration minutes##CourtOffer", ref durationMinutes);
        durationMinutes = Math.Clamp(durationMinutes, 1, 1440);
        ImGui.Checkbox("Price with VIP Perk##CourtOffer", ref perkPrice);

        if (perkPrice)
        {
            DrawPerkPriceEditor(perks);
        }
        else
        {
            ImGui.InputInt("Price gil##CourtOffer", ref priceGil);
            priceGil = Math.Max(0, priceGil);
            ImGui.TextDisabled($"Saved price will be {priceGil:N0} gil.");
        }

        ImGui.Checkbox("Archived##CourtOffer", ref offerArchived);
        ImGui.BeginDisabled(
            busy ||
            string.IsNullOrWhiteSpace(offerName) ||
            durationMinutes <= 0 ||
            (perkPrice && pricePerkId <= 0));
        if (ImGui.Button(editingOfferId == 0 ? "Create offer" : "Save offer"))
        {
            plugin.SaveCourtOffer(
                venue,
                editingOfferId == 0 ? null : editingOfferId,
                new SaveCourtOfferRequest(
                    offerName.Trim(),
                    durationMinutes,
                    perkPrice ? "perk" : "gil",
                    perkPrice ? null : priceGil,
                    perkPrice ? pricePerkId : null,
                    offerArchived));
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear##CourtOffer"))
        {
            ClearOffer();
        }
    }

    private void DrawPerkPriceEditor(VipPerkSummary[] perks)
    {
        if (perks.Length == 0)
        {
            ImGui.TextDisabled("No active VIP perks are available.");
            return;
        }

        if (pricePerkId == 0 || perks.All(perk => perk.PerkId != pricePerkId))
        {
            pricePerkId = perks[0].PerkId;
        }

        var current = perks.First(perk => perk.PerkId == pricePerkId);
        if (!ImGui.BeginCombo("VIP Perk##CourtOffer", current.Name))
        {
            return;
        }

        foreach (var perk in perks)
        {
            var isSelected = perk.PerkId == pricePerkId;
            if (ImGui.Selectable(perk.Name, isSelected))
            {
                pricePerkId = perk.PerkId;
            }

            if (isSelected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private void DrawAccountants(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        bool busy)
    {
        foreach (var account in view.AccountantAccounts)
        {
            ImGui.TextUnformatted(
                $"{account.AccountantDisplayName}: standing {account.StandingBalanceGil:N0} gil; " +
                $"unpaid salary {account.UnpaidSalaryGil:N0} gil" +
                (account.CanReceiveSettlements ? string.Empty : " (legacy balance only)"));
        }

        if (view.AccountantAccounts.Count == 0)
        {
            return;
        }

        if (selectedAccountId == 0 ||
            view.AccountantAccounts.All(account => account.AccountantAccountId != selectedAccountId))
        {
            selectedAccountId = view.AccountantAccounts[0].AccountantAccountId;
        }

        var selected = view.AccountantAccounts.First(
            account => account.AccountantAccountId == selectedAccountId);
        if (ImGui.BeginCombo("Accountant", selected.AccountantDisplayName))
        {
            foreach (var account in view.AccountantAccounts)
            {
                var isSelected = account.AccountantAccountId == selectedAccountId;
                if (ImGui.Selectable(account.AccountantDisplayName, isSelected))
                {
                    selectedAccountId = account.AccountantAccountId;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetError))
        {
            ImGui.TextDisabled(targetError);
            return;
        }

        ImGui.TextUnformatted($"Trade target: {target!.DisplayName}");
        if (view.Capabilities.CanFinance)
        {
            ImGui.InputInt("Prepay gil", ref prepayGil);
            prepayGil = Math.Max(0, prepayGil);
            ImGui.TextDisabled($"Prepay amount: {prepayGil:N0} gil.");
            ImGui.BeginDisabled(busy || !selected.CanReceiveSettlements);
            if (ImGui.Button("Prepay accountant + unpaid salary"))
            {
                plugin.CreateCourtAccountantPrepay(
                    venue,
                    new CreateCourtAccountantPrepayRequest(
                        selectedAccountId,
                        target.CharacterName,
                        target.WorldName,
                        prepayGil,
                        null));
            }
            ImGui.EndDisabled();
        }

        if (view.Capabilities.CanFinance)
        {
            ImGui.SameLine();
        }
        ImGui.BeginDisabled(busy || selected.StandingBalanceGil == 0);
        if (ImGui.Button("Finalize standing balance"))
        {
            plugin.CreateCourtAccountantFinalization(
                venue,
                new CreateCourtAccountantFinalizationRequest(
                    selectedAccountId,
                    target.CharacterName,
                    target.WorldName,
                    null));
        }
        ImGui.EndDisabled();
    }

    private void DrawTransactions(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        bool busy)
    {
        foreach (var transaction in view.Transactions.Take(100))
        {
            ImGui.PushID($"court-tx-{transaction.TransactionId}");
            ImGui.TextUnformatted(
                $"#{transaction.TransactionId} {transaction.TransactionType} — " +
                $"gross sales {transaction.GrossSalesGil:N0}, " +
                $"Court retained {transaction.CourtRetainedGil:N0}, " +
                $"venue share {transaction.GrossCourtGil:N0}, " +
                $"adjustments/deductions {transaction.AdjustmentGil:+#,0;-#,0;0}, " +
                $"salary {transaction.SalaryGil:N0} — {transaction.Status}");
            if (transaction.TradeDirection == "staff_to_collector" && transaction.TradeAmountGil > 0)
                ImGui.TextColored(UnavailableColor, $"Court worker owes venue: {transaction.TradeAmountGil:N0} gil");
            else
                ImGui.TextDisabled($"Trade: {transaction.TradeAmountGil:N0} gil ({transaction.TradeDirection})");

            if (transaction.Status == "pending")
            {
                DrawPendingTransactionActions(venue, transaction, busy);
            }

            ImGui.PopID();
        }
    }

    private void DrawPendingTransactionActions(
        VenueConnectionConfiguration venue,
        CourtTransactionSummary transaction,
        bool busy)
    {
        if (transaction.TradeAmountGil == 0)
        {
            ImGui.TextDisabled(
                "No trade is required; the designated confirmer finalizes the accounting entries.");
        }

        if (transaction.CanExecuteTrade)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(busy);
            if (ImGui.SmallButton("Execute with Dropbox"))
            {
                plugin.ExecuteCourtTransactionTrade(venue, transaction);
            }
            ImGui.EndDisabled();
        }

        if (transaction.CanConfirm)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(busy);
            var confirmLabel = transaction.TradeAmountGil == 0
                ? "Finalize accounting"
                : "Confirm Trade Success";
            if (ImGui.SmallButton(confirmLabel))
            {
                plugin.ConfirmCourtTransaction(venue, transaction.TransactionId);
            }
            ImGui.EndDisabled();
        }

        if (transaction.CanCancel)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(busy);
            if (ImGui.SmallButton("Cancel pending"))
            {
                pendingCancelTransactionId = transaction.TransactionId;
                cancelReason = string.Empty;
                openCancelTransactionPopup = true;
            }
            ImGui.EndDisabled();
        }
    }

    private void DrawSales(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        bool busy)
    {
        foreach (var sale in view.Sales.Take(100))
        {
            ImGui.PushID($"court-sale-{sale.SaleId}");
            var status = sale.VoidedAt is not null
                ? "cancelled"
                : sale.SettledAt is not null
                    ? "settled"
                    : sale.FinancialTransactionId is not null
                        ? "pending"
                        : "unsettled";
            var price = sale.PriceType == "perk"
                ? sale.PricePerkName ?? "VIP Perk"
                : $"{sale.TotalPriceGil:N0} gil";
            ImGui.TextUnformatted(
                $"#{sale.SaleId} {sale.Quantity} × {sale.UnitDurationMinutes} min " +
                $"({sale.TotalDurationMinutes:N0} min) {sale.OfferName} → " +
                $"{sale.BuyerCharacterName} @ {sale.BuyerWorldName} by " +
                $"{sale.SellerDisplayName} — {price} — {status}");
            if (sale.PriceType == "gil")
            {
                ImGui.TextDisabled(
                    $"Court retained {sale.SellerShareGil:N0} ({sale.SellerPercentage:0.##}%); " +
                    $"venue share {sale.VenueShareGil:N0} gil.");
            }
            if (sale.RefundConfirmedAt is not null)
                ImGui.TextDisabled($"Full client refund confirmed: {sale.TotalPriceGil:N0} gil.");

            var canCancel =
                (view.Capabilities.CanManage || view.Capabilities.CanFinance) &&
                sale.VoidedAt is null &&
                (sale.FinancialTransactionId is null || sale.SettledAt is not null);
            if (canCancel)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Cancel"))
                {
                    pendingCancelSaleId = sale.SaleId;
                    pendingSaleRequiresRefund = sale.PriceType == "gil" && sale.SettledAt is not null;
                    pendingRefundGil = sale.TotalPriceGil;
                    pendingRefundBuyer = $"{sale.BuyerCharacterName} @ {sale.BuyerWorldName}";
                    refundConfirmed = false;
                    cancelReason = string.Empty;
                    openCancelSalePopup = true;
                }
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }
    }

    private void OpenQueuedPopups()
    {
        if (openCancelSalePopup)
        {
            openCancelSalePopup = false;
            ImGui.OpenPopup(CancelSalePopupName);
        }

        if (openCancelTransactionPopup)
        {
            openCancelTransactionPopup = false;
            ImGui.OpenPopup(CancelTransactionPopupName);
        }
    }

    private void DrawSaleCancellationPopup(
        VenueConnectionConfiguration venue,
        bool busy)
    {
        if (!ImGui.BeginPopupModal(
                CancelSalePopupName,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Cancel Court sale #{pendingCancelSaleId}? A consumed VIP Perk will be released. " +
            "Confirmed historical settlement transactions remain unchanged.");
        if (pendingSaleRequiresRefund)
        {
            ImGui.TextColored(
                UnavailableColor,
                $"This settled sale requires a full {pendingRefundGil:N0} gil refund to {pendingRefundBuyer}.");
            ImGui.Checkbox(
                "I confirm the client has received the full refund",
                ref refundConfirmed);
        }

        ImGui.InputText("Reason", ref cancelReason, 255);
        ImGui.BeginDisabled(busy || (pendingSaleRequiresRefund && !refundConfirmed));
        if (ImGui.Button("Confirm cancellation"))
        {
            plugin.CancelCourtSale(
                venue,
                pendingCancelSaleId,
                refundConfirmed,
                cancelReason);
            ClearPendingSaleCancellation();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Keep sale"))
        {
            ClearPendingSaleCancellation();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void ClearPendingSaleCancellation()
    {
        pendingCancelSaleId = 0;
        pendingSaleRequiresRefund = false;
        refundConfirmed = false;
        pendingRefundGil = 0;
        pendingRefundBuyer = string.Empty;
    }

    private void DrawTransactionCancellationPopup(
        VenueConnectionConfiguration venue,
        bool busy)
    {
        if (!ImGui.BeginPopupModal(
                CancelTransactionPopupName,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Cancel pending Court transaction #{pendingCancelTransactionId}? " +
            "Reserved sales and salary entries will be released for a new settlement.");
        ImGui.InputText("Reason##CourtTransaction", ref cancelReason, 255);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Confirm transaction cancellation"))
        {
            plugin.CancelCourtTransaction(
                venue,
                pendingCancelTransactionId,
                cancelReason);
            pendingCancelTransactionId = 0;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Keep transaction"))
        {
            pendingCancelTransactionId = 0;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static string FormatPrice(CourtOfferSummary offer) =>
        offer.PriceType == "perk"
            ? offer.PricePerkName ?? "VIP Perk"
            : $"{offer.PriceGil.GetValueOrDefault():N0} gil";

    private void LoadOffer(CourtOfferSummary offer)
    {
        editingOfferId = offer.OfferId;
        offerName = offer.Name;
        durationMinutes = offer.DurationMinutes;
        perkPrice = offer.PriceType == "perk";
        priceGil = (int)Math.Min(int.MaxValue, offer.PriceGil.GetValueOrDefault());
        pricePerkId = offer.PricePerkId.GetValueOrDefault();
        offerArchived = offer.ArchivedAt is not null;
    }

    private void ClearOffer()
    {
        editingOfferId = 0;
        offerName = string.Empty;
        durationMinutes = 30;
        perkPrice = false;
        priceGil = 0;
        pricePerkId = 0;
        offerArchived = false;
    }

    private void ResetForVenue(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
        {
            return;
        }

        activeProfileId = venue.ProfileId;
        selectedOfferId = 0;
        saleQuantity = 1;
        selectedAccountId = 0;
        collectorMode = 0;
        previewTargetName = string.Empty;
        previewTargetWorld = string.Empty;
        previewCollectorMode = string.Empty;
        loadedCourtKeepPercentage = -1m;
        courtKeepPercentage = 0f;
        plugin.ClearCourtStaffSettlementPreview(venue);
        prepayGil = 0;
        ClearPendingSaleCancellation();
        openCancelSalePopup = false;
        pendingCancelTransactionId = 0;
        openCancelTransactionPopup = false;
        cancelReason = string.Empty;
        ClearOffer();
    }
}
