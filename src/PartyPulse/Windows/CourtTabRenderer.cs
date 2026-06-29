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
    private static readonly Vector4 UnavailableColor = new(0.65f, 0.18f, 0.18f, 1f);

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
    private int prepayGil;
    private long selectedAccountId;
    private long pendingCancelSaleId;
    private bool openCancelSalePopup;
    private long pendingCancelTransactionId;
    private bool openCancelTransactionPopup;
    private string cancelReason = string.Empty;

    public void Draw(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("Court Services"))
        {
            return;
        }

        ResetForVenue(venue);
        plugin.EnsureCourtLoaded(venue);
        plugin.EnsureVipPerksLoaded(venue);

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
            ImGui.EndTabItem();
            return;
        }

        var view = snapshot.View;
        ImGui.SameLine();
        ImGui.TextDisabled(
            $"Unsettled Court gil: {view.PersonalUnsettledCourtGil:N0} | " +
            $"unpaid salary: {view.PersonalUnpaidSalaryGil:N0}");

        if (view.Capabilities.CanSell)
        {
            DrawSale(venue, view, busy);
            DrawSettlement(venue, view, busy);
        }

        if (view.Capabilities.CanManage)
        {
            ImGui.Separator();
            DrawOfferManagement(venue, view, busy);
        }

        if (view.Capabilities.CanFinance || view.Capabilities.CanAccount)
        {
            ImGui.Separator();
            DrawAccountants(venue, view, busy);
        }

        ImGui.Separator();
        DrawTransactions(venue, view, busy);
        ImGui.Separator();
        DrawSales(venue, view, busy);

        OpenQueuedPopups();
        DrawSaleCancellationPopup(venue, busy);
        DrawTransactionCancellationPopup(venue, busy);
        ImGui.EndTabItem();
    }

    private void DrawSale(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.CollapsingHeader("Sell Court Service", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

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
        if (!ImGui.CollapsingHeader("Settle Court sales and salary"))
        {
            return;
        }

        var modes = new[] { "Finance manager", "Court Accountant" };
        ImGui.Combo("Settle with", ref collectorMode, modes, modes.Length);
        ImGui.TextDisabled(
            "Court revenue and unpaid salary are netted into one trade. " +
            "A zero net requires confirmation but no trade.");

        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetError))
        {
            ImGui.TextDisabled(targetError);
            return;
        }

        ImGui.TextUnformatted($"Target collector: {target!.DisplayName}");
        ImGui.BeginDisabled(
            busy ||
            (view.PersonalUnsettledCourtGil == 0 && view.PersonalUnpaidSalaryGil == 0));
        if (ImGui.Button("Create combined settlement"))
        {
            plugin.CreateCourtStaffSettlement(
                venue,
                new CreateCourtStaffSettlementRequest(
                    collectorMode == 0 ? "finance" : "accountant",
                    target.CharacterName,
                    target.WorldName,
                    null));
        }
        ImGui.EndDisabled();
    }

    private void DrawOfferManagement(
        VenueConnectionConfiguration venue,
        CourtManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.CollapsingHeader("Court Service offers"))
        {
            return;
        }

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
        if (!ImGui.CollapsingHeader("Court Accountant balances"))
        {
            return;
        }

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
        if (!ImGui.CollapsingHeader(
                "Court financial transactions",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        foreach (var transaction in view.Transactions.Take(100))
        {
            ImGui.PushID($"court-tx-{transaction.TransactionId}");
            ImGui.TextUnformatted(
                $"#{transaction.TransactionId} {transaction.TransactionType} — " +
                $"court {transaction.GrossCourtGil:N0}, salary {transaction.SalaryGil:N0}, " +
                $"trade {transaction.TradeAmountGil:N0} gil — {transaction.Status}");

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
        if (!ImGui.CollapsingHeader("Recent Court Service sales"))
        {
            return;
        }

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

            var canCancel =
                view.Capabilities.CanManage &&
                sale.VoidedAt is null &&
                (sale.SettledAt is null || sale.TotalPriceGil == 0) &&
                sale.FinancialTransactionId is null;
            if (canCancel)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Cancel"))
                {
                    pendingCancelSaleId = sale.SaleId;
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
            $"Cancel Court sale #{pendingCancelSaleId}? " +
            "A consumed VIP Perk will be released.");
        ImGui.InputText("Reason", ref cancelReason, 255);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Confirm cancellation"))
        {
            plugin.CancelCourtSale(venue, pendingCancelSaleId, cancelReason);
            pendingCancelSaleId = 0;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Keep sale"))
        {
            pendingCancelSaleId = 0;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
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
        prepayGil = 0;
        pendingCancelSaleId = 0;
        openCancelSalePopup = false;
        pendingCancelTransactionId = 0;
        openCancelTransactionPopup = false;
        cancelReason = string.Empty;
        ClearOffer();
    }
}
