using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Photoshoots;
using PartyPulse.Services;
using PartyPulse.TimedMacros;

namespace PartyPulse.Windows;

public sealed class PhotoshootsTabRenderer(Plugin plugin)
{
    private const string PaymentStatusPopupName = "Photoshoot payment status###PartyPulsePhotoshootPaymentStatus";
    private const string CancelSalePopupName = "Cancel photoshoot purchase###PartyPulsePhotoshootCancelSale";

    private static readonly Vector4 AvailableColor = new(0.35f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 UnavailableColor = new(0.65f, 0.18f, 0.18f, 1f);

    private Guid activeProfileId;
    private int selectedPackageId;
    private int additionalCharacters;
    private string pendingBuyerName = string.Empty;
    private string pendingBuyerWorld = string.Empty;
    private int pendingPackageId;
    private int pendingAdditionalCharacters;

    private int editingPackageId;
    private string packageName = string.Empty;
    private int includedCharacters = 1;
    private int basePriceGil;
    private int extraPriceGil;
    private bool priceWithPerk;
    private int selectedPricePerkId;
    private bool packageArchived;

    private decimal loadedSellerPercentage = -1m;
    private float sellerPercentage;

    private string settlementTargetName = string.Empty;
    private string settlementTargetWorld = string.Empty;

    private string saleBuyerFilter = string.Empty;
    private string saleSellerFilter = string.Empty;
    private string salePackageFilter = string.Empty;
    private long pendingPaymentSaleId;
    private bool pendingPaymentSettled;
    private bool openPaymentStatusPopup;
    private long pendingCancelSaleId;
    private string pendingCancelBuyer = string.Empty;
    private string pendingCancelPackage = string.Empty;
    private string cancelSaleReason = string.Empty;
    private bool openCancelSalePopup;

    public void Draw(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("Photoshoots"))
        {
            return;
        }

        ResetForVenue(venue);
        plugin.EnsurePhotoshootsLoaded(venue);
        plugin.EnsureVipPerksLoaded(venue);
        plugin.EnsureTimedMacrosLoaded(venue);

        var snapshot = plugin.Photoshoots.GetSnapshot(venue);
        var busy = plugin.Photoshoots.IsBusy(venue.ProfileId) || plugin.Finance.IsBusy(venue.ProfileId);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh photoshoots"))
        {
            plugin.RefreshPhotoshoots(venue);
        }
        ImGui.EndDisabled();

        if (snapshot.Status != PhotoshootManagementStatus.Ready || snapshot.View is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        var view = snapshot.View;
        SyncCommissionEditor(view);
        DrawPhotoshootTimedMacro(venue);

        if (view.Capabilities.CanSell)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Seller keeps {view.SellerPercentage:0.##}%");
            ImGui.TextDisabled(
                $"Unsettled collected: {view.PersonalGrossGil:N0} gil | " +
                $"Your retained share: {view.PersonalSellerShareGil:N0} gil | " +
                $"Available for venue: {view.PersonalAvailableGil:N0} gil" +
                (view.PersonalPendingGil > 0 ? $" | Pending: {view.PersonalPendingGil:N0} gil" : string.Empty));
            DrawSeller(venue, view, busy);
            DrawSettlement(venue, view, busy);
        }

        if (view.Capabilities.CanManageCommission)
        {
            ImGui.Separator();
            DrawCommissionSettings(venue, busy);
        }

        if (view.Capabilities.CanManagePackages)
        {
            ImGui.Separator();
            DrawPackageManagement(venue, view, busy);
        }

        ImGui.Separator();
        DrawRecentSales(venue, view, busy);
        OpenQueuedSalePopups();
        DrawPaymentStatusConfirmation(venue);
        DrawCancelSaleConfirmation(venue);
        DrawSaleConfirmation(venue, view);
        ImGui.EndTabItem();
    }


    private void DrawPhotoshootTimedMacro(VenueConnectionConfiguration venue)
    {
        var snapshot = plugin.TimedMacros.GetSnapshot(venue);
        var view = snapshot.View;
        var macro = view?.Macros.FirstOrDefault(value =>
            string.Equals(value.TypeCode, TimedMacroTypeCodes.PhotoshootAdvertisement, StringComparison.OrdinalIgnoreCase) &&
            value.CanExecute);
        if (view is null || macro is null)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Photoshoot advertisement");
        var opening = view.CurrentOpening;
        var locationMessage = string.Empty;
        var atAddress = opening is not null && plugin.LocationProvider.IsAtAddress(
            opening.AddressWorldName, opening.AddressCityName, opening.AddressWard, opening.AddressPlot, out locationMessage);
        var now = snapshot.EstimatedServerNow;
        var stateText = opening is null
            ? "Paused: no active opening"
            : !atAddress
                ? "Paused: not at opening address"
                : macro.NextDueAt is not { } dueAt || dueAt <= now
                    ? "Due now"
                    : $"Next in {FormatTimedMacroRemaining(dueAt - now)}";
        if (stateText == "Due now")
        {
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.25f, 1f), stateText);
            ImGui.SameLine();
            ImGui.TextDisabled($"· every {macro.IntervalMinutes} minutes · shared across users");
        }
        else
            ImGui.TextDisabled($"{stateText} · every {macro.IntervalMinutes} minutes · shared across users");
        ImGui.SameLine();
        var canExecute = opening is not null && atAddress && macro.Enabled && macro.IsConfigured;
        ImGui.BeginDisabled(plugin.TimedMacros.IsBusy(venue.ProfileId) || plugin.IsGameMacroBusy || !canExecute);
        if (ImGui.SmallButton("Execute photoshoot ad"))
            plugin.RunTimedMacro(venue, macro, opening!);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canExecute)
            ImGui.SetTooltip(opening is null ? "There is no active opening." : !atAddress ? locationMessage : !macro.Enabled ? "The photoshoot advertisement macro is disabled." : "The photoshoot advertisement macro has not been configured.");
        else if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Running early is allowed and resets the shared timer.");
    }

    private static string FormatTimedMacroRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";

    private void DrawSeller(
        VenueConnectionConfiguration venue,
        PhotoshootManagementViewResponse view,
        bool busy)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Sell to targeted player");
        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out var reason))
        {
            ImGui.TextDisabled(reason);
            return;
        }

        ImGui.TextUnformatted(target!.DisplayName);
        var vipStatus = view.VipStatuses.FirstOrDefault(value =>
            string.Equals(value.CharacterName, target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.WorldName, target.WorldName, StringComparison.OrdinalIgnoreCase));
        var vipAvailability = view.VipPerkAvailability.Where(value =>
            string.Equals(value.CharacterName, target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.WorldName, target.WorldName, StringComparison.OrdinalIgnoreCase)).ToArray();

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
            ImGui.TextColored(UnavailableColor, "No active VIP package was found for this character.");
        }

        ImGui.TextUnformatted("Choose package");
        foreach (var package in view.Packages
                     .Where(value => value.ArchivedAt is null)
                     .OrderBy(value => value.Name))
        {
            var selected = selectedPackageId == package.PackageId;
            var availability = package.PricePerkId is { } perkId
                ? vipAvailability.FirstOrDefault(value => value.PerkId == perkId)
                : null;

            ImGui.PushID(package.PackageId);
            if (ImGui.RadioButton("##SelectPackage", selected))
            {
                selectedPackageId = package.PackageId;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"{package.Name} — {package.IncludedCharacters} character(s)");
            ImGui.SameLine();

            if (package.PricePerkId is null)
            {
                ImGui.TextUnformatted($"— {package.BasePriceGil.GetValueOrDefault():N0} gil");
            }
            else if (availability?.Available == true)
            {
                ImGui.TextColored(AvailableColor, $"— VIP perk: {package.PricePerkName} (available)");
            }
            else
            {
                var unavailable = availability?.NextResetAt is { } next
                    ? $"— VIP perk: {package.PricePerkName} (next {VenueTimeZone.Format(venue, next, "g")})"
                    : $"— VIP perk: {package.PricePerkName} (not available)";
                ImGui.TextColored(UnavailableColor, unavailable);
            }

            ImGui.PopID();
        }

        var selectedPackage = view.Packages.FirstOrDefault(value =>
            value.PackageId == selectedPackageId && value.ArchivedAt is null);
        if (selectedPackage is null)
        {
            return;
        }

        ImGui.InputInt("Additional characters", ref additionalCharacters);
        additionalCharacters = Math.Clamp(additionalCharacters, 0, 100);
        var totalGil = (long)selectedPackage.BasePriceGil.GetValueOrDefault() +
                       (long)additionalCharacters * selectedPackage.AdditionalCharacterPriceGil;
        var sellerShare = CalculateSellerShare(totalGil, view.SellerPercentage);
        var venueShare = totalGil - sellerShare;
        var perkState = selectedPackage.PricePerkId is { } selectedPerkId
            ? vipAvailability.FirstOrDefault(value => value.PerkId == selectedPerkId)
            : null;
        var canSell = selectedPackage.PricePerkId is null || perkState?.Available == true;

        ImGui.TextUnformatted(selectedPackage.PricePerkId is null
            ? $"Total: {totalGil:N0} gil"
            : $"Total: {selectedPackage.PricePerkName} + {totalGil:N0} gil");
        if (totalGil > 0)
        {
            ImGui.TextDisabled(
                $"Seller retains {sellerShare:N0} gil; {venueShare:N0} gil will be included in venue settlement.");
        }

        ImGui.BeginDisabled(busy || !canSell);
        if (ImGui.Button("Record photoshoot sale"))
        {
            pendingBuyerName = target.CharacterName;
            pendingBuyerWorld = target.WorldName;
            pendingPackageId = selectedPackage.PackageId;
            pendingAdditionalCharacters = additionalCharacters;
            ImGui.OpenPopup("Confirm photoshoot sale###PartyPulsePhotoshootSale");
        }
        ImGui.EndDisabled();
    }

    private void DrawSaleConfirmation(
        VenueConnectionConfiguration venue,
        PhotoshootManagementViewResponse view)
    {
        if (!ImGui.BeginPopupModal(
                "Confirm photoshoot sale###PartyPulsePhotoshootSale",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var package = view.Packages.FirstOrDefault(value => value.PackageId == pendingPackageId);
        if (package is null)
        {
            ImGui.TextUnformatted("Package is no longer available.");
        }
        else
        {
            var total = (long)package.BasePriceGil.GetValueOrDefault() +
                        (long)pendingAdditionalCharacters * package.AdditionalCharacterPriceGil;
            var sellerShare = CalculateSellerShare(total, view.SellerPercentage);
            var venueShare = total - sellerShare;
            var cost = package.PricePerkId is null
                ? $"{total:N0} gil"
                : $"{package.PricePerkName} plus {total:N0} gil";
            ImGui.TextWrapped(
                $"Record {package.Name} for {pendingBuyerName} @ {pendingBuyerWorld} at {cost}?");
            ImGui.TextWrapped(
                $"At the current {view.SellerPercentage:0.##}% seller rate, the seller keeps " +
                $"{sellerShare:N0} gil and {venueShare:N0} gil is owed to the venue.");
            ImGui.TextWrapped(
                "VIP perk availability, commission, and pricing will be checked again by the server before the sale is committed.");
            if (ImGui.Button("Confirm sale"))
            {
                plugin.SellPhotoshoot(
                    venue,
                    new SellPhotoshootRequest(
                        pendingBuyerName,
                        pendingBuyerWorld,
                        pendingPackageId,
                        pendingAdditionalCharacters));
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
        }

        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawSettlement(
        VenueConnectionConfiguration venue,
        PhotoshootManagementViewResponse view,
        bool busy)
    {
        if (view.PersonalAvailableGil <= 0)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"Available to settle with venue: {view.PersonalAvailableGil:N0} gil");

        var hasTarget = plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetReason);
        ImGui.BeginDisabled(busy || !hasTarget);
        if (ImGui.Button("Settle photoshoot payment"))
        {
            settlementTargetName = target!.CharacterName;
            settlementTargetWorld = target.WorldName;
            ImGui.OpenPopup("Initiate photoshoot settlement###PartyPulsePhotoshootSettlement");
        }
        ImGui.EndDisabled();
        if (!hasTarget)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(targetReason);
        }

        if (!ImGui.BeginPopupModal(
                "Initiate photoshoot settlement###PartyPulsePhotoshootSettlement",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Initiate a trade with {settlementTargetName} @ {settlementTargetWorld} for " +
            $"{view.PersonalAvailableGil:N0} gil?");
        ImGui.TextWrapped(
            "This is the venue share after the seller percentage has already been retained on each sale.");
        ImGui.TextWrapped(
            "The targeted character must belong to an active venue user with finance.settlements.manage or venue.owner.");
        ImGui.TextWrapped(
            "Confirming checks Dropbox, creates a pending server transaction, and starts the trade queue. " +
            "The collector must still confirm that payment was received.");

        if (ImGui.Button("Create settlement and start trade"))
        {
            plugin.CreatePhotoshootSettlement(
                venue,
                new CreatePhotoshootSettlementRequest(settlementTargetName, settlementTargetWorld));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawCommissionSettings(VenueConnectionConfiguration venue, bool busy)
    {
        if (!ImGui.CollapsingHeader("Photoshoot seller percentage"))
        {
            return;
        }

        ImGui.TextDisabled(
            "Venue owner setting. The percentage is snapshotted when each sale is recorded and only applies to gil collected.");
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        ImGui.InputFloat("Seller keeps (%)", ref sellerPercentage, 0.25f, 1f, "%.2f");
        sellerPercentage = Math.Clamp(sellerPercentage, 0f, 100f);

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Save seller percentage"))
        {
            var normalized = decimal.Round((decimal)sellerPercentage, 2, MidpointRounding.AwayFromZero);
            plugin.UpdatePhotoshootSettings(venue, new UpdatePhotoshootSettingsRequest(normalized));
        }
        ImGui.EndDisabled();
    }

    private void DrawPackageManagement(
        VenueConnectionConfiguration venue,
        PhotoshootManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.CollapsingHeader("Photoshoot package definitions", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (ImGui.Button("New package"))
        {
            ResetEditor();
        }

        foreach (var package in view.Packages
                     .OrderBy(value => value.ArchivedAt is not null)
                     .ThenBy(value => value.Name))
        {
            ImGui.PushID(package.PackageId);
            var price = package.PricePerkId is null
                ? $"{package.BasePriceGil:N0} gil"
                : $"perk: {package.PricePerkName}";
            ImGui.TextUnformatted(
                $"{package.Name} — {package.IncludedCharacters} included — {price} — " +
                $"+{package.AdditionalCharacterPriceGil:N0}/extra" +
                (package.ArchivedAt is null ? string.Empty : " — archived"));
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit"))
            {
                LoadEditor(package);
            }
            ImGui.PopID();
        }

        ImGui.InputText("Package name", ref packageName, 100);
        ImGui.InputInt("Included characters", ref includedCharacters);
        includedCharacters = Math.Clamp(includedCharacters, 1, 100);
        ImGui.Checkbox("Use VIP perk for base price", ref priceWithPerk);
        if (priceWithPerk)
        {
            var perks = plugin.VipPerks.GetSnapshot(venue).View?.Perks
                .Where(value => value.ArchivedAt is null)
                .OrderBy(value => value.Name)
                .ToArray() ?? Array.Empty<VipPerkSummary>();
            var preview = perks.FirstOrDefault(value => value.PerkId == selectedPricePerkId)?.Name ?? "Select perk";
            if (ImGui.BeginCombo("Price perk", preview))
            {
                foreach (var perk in perks)
                {
                    if (ImGui.Selectable(perk.Name, selectedPricePerkId == perk.PerkId))
                    {
                        selectedPricePerkId = perk.PerkId;
                    }
                }
                ImGui.EndCombo();
            }
        }
        else
        {
            ImGui.InputInt("Base price (gil)", ref basePriceGil);
        }

        ImGui.InputInt("Price per additional character", ref extraPriceGil);
        if (editingPackageId > 0)
        {
            ImGui.Checkbox("Archived", ref packageArchived);
        }

        var valid = !string.IsNullOrWhiteSpace(packageName) &&
                    includedCharacters is >= 1 and <= 100 &&
                    extraPriceGil >= 0 &&
                    (priceWithPerk ? selectedPricePerkId > 0 : basePriceGil >= 0);
        ImGui.BeginDisabled(busy || !valid);
        if (ImGui.Button(editingPackageId == 0
                ? "Create photoshoot package"
                : "Save photoshoot package"))
        {
            if (editingPackageId == 0)
            {
                plugin.CreatePhotoshootPackage(
                    venue,
                    new CreatePhotoshootPackageRequest(
                        packageName.Trim(),
                        includedCharacters,
                        priceWithPerk ? null : basePriceGil,
                        priceWithPerk ? selectedPricePerkId : null,
                        extraPriceGil));
            }
            else
            {
                plugin.UpdatePhotoshootPackage(
                    venue,
                    editingPackageId,
                    new UpdatePhotoshootPackageRequest(
                        packageName.Trim(),
                        includedCharacters,
                        priceWithPerk ? null : basePriceGil,
                        priceWithPerk ? selectedPricePerkId : null,
                        extraPriceGil,
                        packageArchived));
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawRecentSales(
        VenueConnectionConfiguration venue,
        PhotoshootManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.CollapsingHeader("Recent photoshoot sales"))
        {
            return;
        }

        var filterWidth = Math.Min(
            420 * ImGuiHelpers.GlobalScale,
            Math.Max(180 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.6f));
        ImGui.SetNextItemWidth(filterWidth);
        ImGui.InputText("Buyer filter", ref saleBuyerFilter, 100);
        ImGui.SetNextItemWidth(filterWidth);
        ImGui.InputText("Seller filter", ref saleSellerFilter, 100);
        ImGui.SetNextItemWidth(filterWidth);
        ImGui.InputText("Package filter", ref salePackageFilter, 100);

        var buyerFilter = saleBuyerFilter.Trim();
        var sellerFilter = saleSellerFilter.Trim();
        var packageFilter = salePackageFilter.Trim();
        var filteredSales = view.Sales
            .Where(sale =>
                (buyerFilter.Length == 0 ||
                 sale.BuyerCharacterName.Contains(buyerFilter, StringComparison.OrdinalIgnoreCase) ||
                 sale.BuyerWorldName.Contains(buyerFilter, StringComparison.OrdinalIgnoreCase)) &&
                (sellerFilter.Length == 0 ||
                 sale.SellerDisplayName.Contains(sellerFilter, StringComparison.OrdinalIgnoreCase)) &&
                (packageFilter.Length == 0 ||
                 sale.PackageName.Contains(packageFilter, StringComparison.OrdinalIgnoreCase)))
            .Take(100)
            .ToArray();

        ImGui.TextDisabled($"Showing {filteredSales.Length:N0} of {view.Sales.Count:N0} recent sales.");

        foreach (var sale in filteredSales)
        {
            ImGui.PushID((int)(sale.SaleId % int.MaxValue));
            var cost = sale.BaseCostType == "vip_perk"
                ? $"{sale.PricePerkName} + {sale.TotalGil:N0} gil"
                : $"{sale.TotalGil:N0} gil";
            var status = sale.VoidedAt is not null
                ? "cancelled"
                : sale.PaidToVenueAt is not null
                    ? "settled"
                    : sale.PendingSettlementId is not null
                        ? "pending settlement"
                        : "unsettled";
            ImGui.BulletText(
                $"{sale.BuyerCharacterName} @ {sale.BuyerWorldName} — seller: {sale.SellerDisplayName} — " +
                $"{sale.PackageName} — {cost} — " +
                $"seller {sale.SellerShareGil:N0} / venue {sale.VenueShareGil:N0} — {status} — " +
                VenueTimeZone.Format(venue, sale.SoldAt, "g"));

            if (sale.VoidedAt is not null && !string.IsNullOrWhiteSpace(sale.VoidReason))
            {
                ImGui.Indent();
                ImGui.TextDisabled($"Cancellation reason: {sale.VoidReason}");
                ImGui.Unindent();
            }

            if (view.Capabilities.CanManageSettlements)
            {
                ImGui.Indent();
                ImGui.BeginDisabled(busy || sale.PendingSettlementId is not null);
                var paymentLabel = sale.PaidToVenueAt is null ? "Mark settled" : "Mark unpaid";
                if (ImGui.SmallButton(paymentLabel))
                {
                    pendingPaymentSaleId = sale.SaleId;
                    pendingPaymentSettled = sale.PaidToVenueAt is null;
                    openPaymentStatusPopup = true;
                }
                ImGui.EndDisabled();
                if (sale.PendingSettlementId is not null &&
                    ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("Resolve or reject the pending settlement transaction first.");
                }

                ImGui.SameLine();
                ImGui.BeginDisabled(busy || sale.VoidedAt is not null || sale.PendingSettlementId is not null);
                if (ImGui.SmallButton("Cancel purchase"))
                {
                    pendingCancelSaleId = sale.SaleId;
                    pendingCancelBuyer = $"{sale.BuyerCharacterName} @ {sale.BuyerWorldName}";
                    pendingCancelPackage = sale.PackageName;
                    cancelSaleReason = string.Empty;
                    openCancelSalePopup = true;
                }
                ImGui.EndDisabled();
                if (sale.PendingSettlementId is not null &&
                    ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("Resolve or reject the pending settlement transaction before cancelling the purchase.");
                }
                ImGui.Unindent();
            }

            ImGui.PopID();
        }
    }

    private void OpenQueuedSalePopups()
    {
        if (openPaymentStatusPopup)
        {
            ImGui.OpenPopup(PaymentStatusPopupName);
            openPaymentStatusPopup = false;
        }

        if (openCancelSalePopup)
        {
            ImGui.OpenPopup(CancelSalePopupName);
            openCancelSalePopup = false;
        }
    }

    private void DrawPaymentStatusConfirmation(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(PaymentStatusPopupName, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(pendingPaymentSettled
            ? $"Mark photoshoot sale #{pendingPaymentSaleId} as paid to the venue?"
            : $"Mark photoshoot sale #{pendingPaymentSaleId} as unpaid to the venue?");
        ImGui.TextWrapped(
            "This is a privileged manual accounting override. The change is retained in the photoshoot payment audit history.");
        if (ImGui.Button(pendingPaymentSettled ? "Mark settled" : "Mark unpaid"))
        {
            plugin.SetPhotoshootSalePaymentStatus(
                venue,
                pendingPaymentSaleId,
                new SetPhotoshootSalePaymentStatusRequest(pendingPaymentSettled));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawCancelSaleConfirmation(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(CancelSalePopupName, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Cancel photoshoot sale #{pendingCancelSaleId}: {pendingCancelPackage} for {pendingCancelBuyer}?");
        ImGui.TextColored(
            new Vector4(1f, 0.65f, 0.25f, 1f),
            "Cancelling the purchase restores any VIP perk spent by this sale. Payment settlement is tracked separately and is not silently changed.");
        ImGui.SetNextItemWidth(420 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Reason (optional)", ref cancelSaleReason, 255);
        if (ImGui.Button("Cancel purchase"))
        {
            plugin.CancelPhotoshootSale(
                venue,
                pendingCancelSaleId,
                new CancelPhotoshootSaleRequest(
                    string.IsNullOrWhiteSpace(cancelSaleReason) ? null : cancelSaleReason.Trim()));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Go back"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void ResetForVenue(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
        {
            return;
        }

        activeProfileId = venue.ProfileId;
        selectedPackageId = 0;
        additionalCharacters = 0;
        pendingBuyerName = string.Empty;
        pendingBuyerWorld = string.Empty;
        pendingPackageId = 0;
        pendingAdditionalCharacters = 0;
        settlementTargetName = string.Empty;
        settlementTargetWorld = string.Empty;
        saleBuyerFilter = string.Empty;
        saleSellerFilter = string.Empty;
        salePackageFilter = string.Empty;
        pendingPaymentSaleId = 0;
        pendingPaymentSettled = false;
        openPaymentStatusPopup = false;
        pendingCancelSaleId = 0;
        pendingCancelBuyer = string.Empty;
        pendingCancelPackage = string.Empty;
        cancelSaleReason = string.Empty;
        openCancelSalePopup = false;
        loadedSellerPercentage = -1m;
        sellerPercentage = 0f;
        ResetEditor();
    }

    private void SyncCommissionEditor(PhotoshootManagementViewResponse view)
    {
        if (loadedSellerPercentage == view.SellerPercentage)
        {
            return;
        }

        loadedSellerPercentage = view.SellerPercentage;
        sellerPercentage = (float)view.SellerPercentage;
    }

    private static long CalculateSellerShare(long totalGil, decimal percentage) =>
        decimal.ToInt64(decimal.Round(
            totalGil * percentage / 100m,
            0,
            MidpointRounding.AwayFromZero));

    private void ResetEditor()
    {
        editingPackageId = 0;
        packageName = string.Empty;
        includedCharacters = 1;
        basePriceGil = 0;
        extraPriceGil = 0;
        priceWithPerk = false;
        selectedPricePerkId = 0;
        packageArchived = false;
    }

    private void LoadEditor(PhotoshootPackageSummary value)
    {
        editingPackageId = value.PackageId;
        packageName = value.Name;
        includedCharacters = value.IncludedCharacters;
        basePriceGil = value.BasePriceGil.GetValueOrDefault();
        extraPriceGil = value.AdditionalCharacterPriceGil;
        priceWithPerk = value.PricePerkId is not null;
        selectedPricePerkId = value.PricePerkId.GetValueOrDefault();
        packageArchived = value.ArchivedAt is not null;
    }
}
