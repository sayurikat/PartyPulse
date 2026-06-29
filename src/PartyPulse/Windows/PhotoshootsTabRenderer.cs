using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Photoshoots;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class PhotoshootsTabRenderer(Plugin plugin)
{
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

    private string settlementTargetName = string.Empty;
    private string settlementTargetWorld = string.Empty;

    public void Draw(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("Photoshoots")) return;
        ResetForVenue(venue);
        plugin.EnsurePhotoshootsLoaded(venue);
        plugin.EnsureVipPerksLoaded(venue);

        var snapshot = plugin.Photoshoots.GetSnapshot(venue);
        var busy = plugin.Photoshoots.IsBusy(venue.ProfileId) || plugin.Finance.IsBusy(venue.ProfileId);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh photoshoots")) plugin.RefreshPhotoshoots(venue);
        ImGui.EndDisabled();

        if (snapshot.Status != PhotoshootManagementStatus.Ready || snapshot.View is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        var view = snapshot.View;
        if (view.Capabilities.CanSell)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"My unsettled photoshoots: {view.PersonalAvailableGil:N0} gil");
            if (view.PersonalPendingGil > 0) { ImGui.SameLine(); ImGui.TextDisabled($"Pending: {view.PersonalPendingGil:N0} gil"); }
            DrawSeller(venue, view, busy);
            DrawSettlement(venue, view, busy);
        }

        if (view.Capabilities.CanManagePackages)
        {
            ImGui.Separator();
            DrawPackageManagement(venue, view, busy);
        }

        ImGui.Separator();
        DrawRecentSales(venue, view);
        DrawSaleConfirmation(venue, view);
        ImGui.EndTabItem();
    }

    private void DrawSeller(VenueConnectionConfiguration venue, PhotoshootManagementViewResponse view, bool busy)
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
            ImGui.TextDisabled($"VIP: {vipStatus.VipPackageName}" + (vipStatus.EndsAt is { } end ? $" until {VenueTimeZone.Format(venue, end, "g")}" : " (lifetime)"));
        else
            ImGui.TextDisabled("No active VIP package was found for this character.");

        ImGui.TextUnformatted("Choose package");
        foreach (var package in view.Packages.Where(value => value.ArchivedAt is null).OrderBy(value => value.Name))
        {
            var selected = selectedPackageId == package.PackageId;
            ImGui.PushID(package.PackageId);
            var availability = package.PricePerkId is { } perkId
                ? vipAvailability.FirstOrDefault(value => value.PerkId == perkId)
                : null;
            var baseLabel = package.PricePerkId is null
                ? $"{package.BasePriceGil.GetValueOrDefault():N0} gil"
                : availability?.Available == true
                    ? $"VIP perk: {package.PricePerkName} (available)"
                    : availability?.NextResetAt is { } next
                        ? $"VIP perk: {package.PricePerkName} (next {VenueTimeZone.Format(venue, next, "g")})"
                        : $"VIP perk: {package.PricePerkName} (not available)";
            if (ImGui.Selectable($"{package.Name} — {package.IncludedCharacters} character(s) — {baseLabel}##package", selected))
                selectedPackageId = package.PackageId;
            ImGui.PopID();
        }

        var selectedPackage = view.Packages.FirstOrDefault(value => value.PackageId == selectedPackageId && value.ArchivedAt is null);
        if (selectedPackage is null) return;
        ImGui.InputInt("Additional characters", ref additionalCharacters);
        additionalCharacters = Math.Clamp(additionalCharacters, 0, 100);
        var totalGil = (long)selectedPackage.BasePriceGil.GetValueOrDefault() +
                       (long)additionalCharacters * selectedPackage.AdditionalCharacterPriceGil;
        var perkState = selectedPackage.PricePerkId is { } selectedPerkId
            ? vipAvailability.FirstOrDefault(value => value.PerkId == selectedPerkId)
            : null;
        var canSell = selectedPackage.PricePerkId is null || perkState?.Available == true;
        ImGui.TextUnformatted(selectedPackage.PricePerkId is null
            ? $"Total: {totalGil:N0} gil"
            : $"Total: {selectedPackage.PricePerkName} + {totalGil:N0} gil");
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

    private void DrawSaleConfirmation(VenueConnectionConfiguration venue, PhotoshootManagementViewResponse view)
    {
        if (!ImGui.BeginPopupModal("Confirm photoshoot sale###PartyPulsePhotoshootSale", ImGuiWindowFlags.AlwaysAutoResize)) return;
        var package = view.Packages.FirstOrDefault(value => value.PackageId == pendingPackageId);
        if (package is null) { ImGui.TextUnformatted("Package is no longer available."); }
        else
        {
            var total = (long)package.BasePriceGil.GetValueOrDefault() + (long)pendingAdditionalCharacters * package.AdditionalCharacterPriceGil;
            var cost = package.PricePerkId is null ? $"{total:N0} gil" : $"{package.PricePerkName} plus {total:N0} gil";
            ImGui.TextWrapped($"Record {package.Name} for {pendingBuyerName} @ {pendingBuyerWorld} at {cost}?");
            ImGui.TextWrapped("VIP perk availability and pricing will be checked again by the server before the sale is committed.");
            if (ImGui.Button("Confirm sale"))
            {
                plugin.SellPhotoshoot(venue, new SellPhotoshootRequest(pendingBuyerName, pendingBuyerWorld, pendingPackageId, pendingAdditionalCharacters));
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
        }
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
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
        ImGui.TextUnformatted($"Available to settle: {view.PersonalAvailableGil:N0} gil");

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

    private void DrawPackageManagement(VenueConnectionConfiguration venue, PhotoshootManagementViewResponse view, bool busy)
    {
        if (!ImGui.CollapsingHeader("Photoshoot package definitions", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (ImGui.Button("New package")) ResetEditor();
        foreach (var package in view.Packages.OrderBy(value => value.ArchivedAt is not null).ThenBy(value => value.Name))
        {
            ImGui.PushID(package.PackageId);
            var price = package.PricePerkId is null ? $"{package.BasePriceGil:N0} gil" : $"perk: {package.PricePerkName}";
            ImGui.TextUnformatted($"{package.Name} — {package.IncludedCharacters} included — {price} — +{package.AdditionalCharacterPriceGil:N0}/extra{(package.ArchivedAt is null ? string.Empty : " — archived")}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit")) LoadEditor(package);
            ImGui.PopID();
        }

        ImGui.InputText("Package name", ref packageName, 100);
        ImGui.InputInt("Included characters", ref includedCharacters);
        includedCharacters = Math.Clamp(includedCharacters, 1, 100);
        ImGui.Checkbox("Use VIP perk for base price", ref priceWithPerk);
        if (priceWithPerk)
        {
            var perks = plugin.VipPerks.GetSnapshot(venue).View?.Perks.Where(value => value.ArchivedAt is null).OrderBy(value => value.Name).ToArray() ?? Array.Empty<VipPerkSummary>();
            var preview = perks.FirstOrDefault(value => value.PerkId == selectedPricePerkId)?.Name ?? "Select perk";
            if (ImGui.BeginCombo("Price perk", preview))
            {
                foreach (var perk in perks)
                {
                    if (ImGui.Selectable(perk.Name, selectedPricePerkId == perk.PerkId)) selectedPricePerkId = perk.PerkId;
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
        if (ImGui.Button(editingPackageId == 0 ? "Create photoshoot package" : "Save photoshoot package"))
        {
            if (editingPackageId == 0)
                plugin.CreatePhotoshootPackage(venue, new CreatePhotoshootPackageRequest(packageName.Trim(), includedCharacters, priceWithPerk ? null : basePriceGil, priceWithPerk ? selectedPricePerkId : null, extraPriceGil));
            else
                plugin.UpdatePhotoshootPackage(venue, editingPackageId, new UpdatePhotoshootPackageRequest(packageName.Trim(), includedCharacters, priceWithPerk ? null : basePriceGil, priceWithPerk ? selectedPricePerkId : null, extraPriceGil, packageArchived));
        }
        ImGui.EndDisabled();
    }

    private static void DrawRecentSales(VenueConnectionConfiguration venue, PhotoshootManagementViewResponse view)
    {
        if (!ImGui.CollapsingHeader("Recent photoshoot sales")) return;
        foreach (var sale in view.Sales.Take(100))
        {
            var cost = sale.BaseCostType == "vip_perk" ? $"{sale.PricePerkName} + {sale.TotalGil:N0} gil" : $"{sale.TotalGil:N0} gil";
            var status = sale.VoidedAt is not null ? "voided" : sale.PaidToVenueAt is not null ? "settled" : sale.PendingSettlementId is not null ? "pending settlement" : "unsettled";
            ImGui.BulletText($"{sale.BuyerCharacterName} @ {sale.BuyerWorldName} — {sale.PackageName} — {cost} — {status} — {VenueTimeZone.Format(venue, sale.SoldAt, "g")}");
        }
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
        ResetEditor();
    }

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
