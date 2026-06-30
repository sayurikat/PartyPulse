using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.OtherSales;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class OtherSalesTabRenderer(Plugin plugin)
{
    private const string SalePopup = "Confirm Other Sale###PartyPulseOtherSaleConfirm";
    private const string PaymentPopup = "Confirm payment status###PartyPulseOtherSalePayment";
    private const string CancelPopup = "Cancel and refund Other Sale###PartyPulseOtherSaleCancel";
    private static readonly Vector4 Good = new(0.35f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.9f, 0.35f, 0.35f, 1f);

    private Guid activeProfileId;
    private int selectedItemId;
    private int quantity = 1;
    private string settlementTargetName = string.Empty;
    private string settlementTargetWorld = string.Empty;

    private int editingItemId;
    private string itemName = string.Empty;
    private int priceGil;
    private bool priceWithPerk;
    private int selectedPerkId;
    private bool canSellQuantity;
    private bool itemArchived;
    private float sellerPercentage;
    private decimal loadedSellerPercentage = -1m;

    private string buyerFilter = string.Empty;
    private string sellerFilter = string.Empty;
    private string itemFilter = string.Empty;
    private string statusFilter = string.Empty;

    private string pendingBuyerName = string.Empty;
    private string pendingBuyerWorld = string.Empty;
    private int pendingItemId;
    private int pendingQuantity;
    private long pendingPaymentSaleId;
    private bool pendingPaymentSettled;
    private bool openPaymentPopup;
    private long pendingCancelSaleId;
    private string pendingCancelLabel = string.Empty;
    private string cancelReason = string.Empty;
    private bool openCancelPopup;

    public void Draw(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("Other Sales")) return;

        ResetForVenue(venue);
        plugin.EnsureOtherSalesLoaded(venue);
        var snapshot = plugin.OtherSales.GetSnapshot(venue);
        var busy = plugin.OtherSales.IsBusy(venue.ProfileId) || plugin.Finance.IsBusy(venue.ProfileId);

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh Other Sales")) plugin.RefreshOtherSales(venue);
        ImGui.EndDisabled();

        if (snapshot.Status != OtherSalesManagementStatus.Ready || snapshot.View is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        var view = snapshot.View;
        if (view.Capabilities.CanSell)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(
                $"Unsettled collected: {view.PersonalGrossGil:N0} gil | Keep: {view.PersonalSellerShareGil:N0} gil | " +
                $"Available for venue: {view.PersonalAvailableGil:N0} gil" +
                (view.PersonalPendingGil > 0 ? $" | Pending: {view.PersonalPendingGil:N0} gil" : string.Empty));
            DrawSeller(venue, view, busy);
            DrawSettlement(venue, view, busy);
        }

        if (view.Capabilities.CanManageItems)
        {
            ImGui.Separator();
            DrawItemManagement(venue, view, busy);
        }

        ImGui.Separator();
        DrawHistory(venue, view, busy);
        QueuePopups();
        DrawSaleConfirmation(venue, view);
        DrawPaymentConfirmation(venue);
        DrawCancelConfirmation(venue);
        ImGui.EndTabItem();
    }

    private void DrawSeller(VenueConnectionConfiguration venue, OtherSalesManagementViewResponse view, bool busy)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Sell to targeted player");
        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out var reason))
        {
            ImGui.TextDisabled(reason);
            return;
        }

        ImGui.TextUnformatted(target!.DisplayName);
        var targetAvailability = view.VipPerkAvailability.Where(value =>
            string.Equals(value.CharacterName, target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.WorldName, target.WorldName, StringComparison.OrdinalIgnoreCase)).ToArray();

        foreach (var item in view.Items.Where(value => value.ArchivedAt is null).OrderBy(value => value.Name))
        {
            var selected = selectedItemId == item.ItemId;
            var availability = item.PricePerkId is { } perkId
                ? targetAvailability.FirstOrDefault(value => value.PerkId == perkId)
                : null;
            ImGui.PushID(item.ItemId);
            if (ImGui.RadioButton("##SelectOtherSale", selected))
            {
                selectedItemId = item.ItemId;
                quantity = 1;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(item.Name);
            ImGui.SameLine();
            if (item.PricePerUnitGil is { } gil)
                ImGui.TextDisabled($"— {gil:N0} gil each — seller keeps {item.SellerPercentage:0.##}%");
            else if (availability?.Available == true)
                ImGui.TextColored(Good, $"— VIP perk: {item.PricePerkName} (available)");
            else
                ImGui.TextColored(Bad, availability?.NextResetAt is { } next
                    ? $"— VIP perk: {item.PricePerkName} (next {VenueTimeZone.Format(venue, next, "g")})"
                    : $"— VIP perk: {item.PricePerkName} (not available)");
            ImGui.PopID();
        }

        var selectedItem = view.Items.FirstOrDefault(value => value.ItemId == selectedItemId && value.ArchivedAt is null);
        if (selectedItem is null) return;

        if (selectedItem.CanSellQuantity && selectedItem.PricePerkId is null)
        {
            ImGui.InputInt("Quantity", ref quantity);
            quantity = Math.Clamp(quantity, 1, 100000);
        }
        else
        {
            quantity = 1;
            ImGui.TextDisabled("Quantity: 1");
        }

        var total = (long)selectedItem.PricePerUnitGil.GetValueOrDefault() * quantity;
        var sellerShare = CalculateSellerShare(total, selectedItem.SellerPercentage);
        ImGui.TextDisabled($"Total: {total:N0} gil | Seller: {sellerShare:N0} | Venue: {total - sellerShare:N0}");

        var perkAvailable = selectedItem.PricePerkId is not { } requiredPerk ||
                            targetAvailability.Any(value => value.PerkId == requiredPerk && value.Available);
        ImGui.BeginDisabled(busy || !perkAvailable);
        if (ImGui.Button("Review sale"))
        {
            pendingBuyerName = target.CharacterName;
            pendingBuyerWorld = target.WorldName;
            pendingItemId = selectedItem.ItemId;
            pendingQuantity = quantity;
            ImGui.OpenPopup(SalePopup);
        }
        ImGui.EndDisabled();
        if (!perkAvailable) ImGui.TextColored(Bad, "The required VIP perk is not currently available for the targeted player.");
    }

    private void DrawSettlement(VenueConnectionConfiguration venue, OtherSalesManagementViewResponse view, bool busy)
    {
        if (view.PersonalAvailableGil <= 0) return;
        ImGui.Spacing();
        ImGui.TextUnformatted("Settle collected venue share");
        var hasTarget = plugin.TargetProvider.TryGetCurrentTarget(out var target, out var reason);
        if (hasTarget)
        {
            settlementTargetName = target!.CharacterName;
            settlementTargetWorld = target.WorldName;
            ImGui.TextUnformatted($"Collector: {target.DisplayName}");
        }
        else
        {
            ImGui.TextDisabled(reason);
        }

        ImGui.BeginDisabled(busy || !hasTarget);
        if (ImGui.Button($"Create settlement ({view.PersonalAvailableGil:N0} gil)"))
            plugin.CreateOtherSalesSettlement(venue, new CreateOtherSalesSettlementRequest(settlementTargetName, settlementTargetWorld));
        ImGui.EndDisabled();
        ImGui.TextDisabled("The targeted user must have finance settlement permission and will confirm receipt in Finance.");
    }

    private void DrawItemManagement(VenueConnectionConfiguration venue, OtherSalesManagementViewResponse view, bool busy)
    {
        ImGui.TextUnformatted("Items for sale");
        if (ImGui.Button("New item")) LoadEditor(null);
        ImGui.SameLine();
        var preview = editingItemId == 0 ? "Create new" : view.Items.FirstOrDefault(x => x.ItemId == editingItemId)?.Name ?? "Select item";
        if (ImGui.BeginCombo("Edit item", preview))
        {
            foreach (var item in view.Items.OrderBy(x => x.ArchivedAt is not null).ThenBy(x => x.Name))
            {
                if (ImGui.Selectable(item.Name + (item.ArchivedAt is null ? string.Empty : " (archived)"), item.ItemId == editingItemId))
                    LoadEditor(item);
            }
            ImGui.EndCombo();
        }

        ImGui.InputText("Name", ref itemName, 100);
        if (ImGui.RadioButton("Gil price", !priceWithPerk)) priceWithPerk = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("VIP perk price", priceWithPerk)) { priceWithPerk = true; canSellQuantity = false; }
        if (priceWithPerk)
        {
            var perkName = view.Perks.FirstOrDefault(x => x.PerkId == selectedPerkId)?.Name ?? "Select perk";
            if (ImGui.BeginCombo("Required perk", perkName))
            {
                foreach (var perk in view.Perks.OrderBy(x => x.Name))
                {
                    if (ImGui.Selectable(perk.Name, selectedPerkId == perk.PerkId)) selectedPerkId = perk.PerkId;
                }
                ImGui.EndCombo();
            }
            ImGui.TextDisabled("VIP-perk purchases are always quantity 1.");
        }
        else
        {
            ImGui.InputInt("Price per unit", ref priceGil);
            priceGil = Math.Max(0, priceGil);
            ImGui.Checkbox("Can be sold in quantity", ref canSellQuantity);
        }
        if (editingItemId > 0) ImGui.Checkbox("Archived", ref itemArchived);

        var valid = !string.IsNullOrWhiteSpace(itemName) && (!priceWithPerk || selectedPerkId > 0);
        ImGui.BeginDisabled(busy || !valid);
        if (editingItemId == 0)
        {
            if (ImGui.Button("Create item"))
                plugin.CreateOtherSaleItem(venue, new CreateOtherSaleItemRequest(itemName.Trim(), priceWithPerk ? null : priceGil, priceWithPerk ? selectedPerkId : null, !priceWithPerk && canSellQuantity));
        }
        else if (ImGui.Button("Save item"))
        {
            plugin.UpdateOtherSaleItem(venue, editingItemId, new UpdateOtherSaleItemRequest(itemName.Trim(), priceWithPerk ? null : priceGil, priceWithPerk ? selectedPerkId : null, !priceWithPerk && canSellQuantity, itemArchived));
        }
        ImGui.EndDisabled();

        if (editingItemId > 0)
        {
            var current = view.Items.FirstOrDefault(x => x.ItemId == editingItemId);
            if (current is not null && loadedSellerPercentage != current.SellerPercentage)
            {
                loadedSellerPercentage = current.SellerPercentage;
                sellerPercentage = (float)current.SellerPercentage;
            }
            ImGui.Spacing();
            ImGui.TextUnformatted($"Seller keeps: {current?.SellerPercentage:0.##}%");
            if (view.Capabilities.CanManageCommission)
            {
                ImGui.InputFloat("Seller percentage (%)", ref sellerPercentage, 0.25f, 1f, "%.2f");
                ImGui.BeginDisabled(busy || current is null || Math.Abs(sellerPercentage - (float)current.SellerPercentage) < 0.005f);
                if (ImGui.Button("Save seller percentage"))
                    plugin.UpdateOtherSaleSellerPercentage(venue, editingItemId, new UpdateOtherSaleSellerPercentageRequest(Math.Round((decimal)sellerPercentage, 2)));
                ImGui.EndDisabled();
                ImGui.TextDisabled("Only venue owners can change this percentage.");
            }
        }
    }

    private void DrawHistory(VenueConnectionConfiguration venue, OtherSalesManagementViewResponse view, bool busy)
    {
        ImGui.TextUnformatted("Sales history");
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale); ImGui.InputText("Buyer", ref buyerFilter, 100);
        ImGui.SameLine(); ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale); ImGui.InputText("Seller", ref sellerFilter, 100);
        ImGui.SameLine(); ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale); ImGui.InputText("Item", ref itemFilter, 100);
        ImGui.SameLine(); ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale); ImGui.InputText("Status", ref statusFilter, 50);

        var rows = view.Sales.Where(MatchesFilters).ToArray();
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("OtherSalesHistory", 9, flags, new Vector2(0, 330 * ImGuiHelpers.GlobalScale))) return;
        ImGui.TableSetupColumn("Time"); ImGui.TableSetupColumn("Buyer"); ImGui.TableSetupColumn("Seller");
        ImGui.TableSetupColumn("Item"); ImGui.TableSetupColumn("Qty"); ImGui.TableSetupColumn("Total");
        ImGui.TableSetupColumn("Venue"); ImGui.TableSetupColumn("Status"); ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 170 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var sale in rows)
        {
            ImGui.PushID((int)(sale.SaleId % int.MaxValue));
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(VenueTimeZone.Format(venue, sale.SoldAt, "g"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{sale.BuyerCharacterName} ({sale.BuyerWorldName})");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(sale.SellerDisplayName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(sale.ItemName + (sale.PricePerkId is null ? string.Empty : $" [{sale.PricePerkName}]"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(sale.Quantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(sale.PricePerkId is null ? $"{sale.TotalGil:N0}" : "VIP perk");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{sale.VenueShareGil:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(StatusFor(sale));
            ImGui.TableNextColumn();
            if (view.Capabilities.CanManageSettlements && sale.VoidedAt is null)
            {
                var locked = sale.PendingSettlementId is not null;
                ImGui.BeginDisabled(busy || locked);
                if (ImGui.SmallButton(sale.PaidToVenueAt is null ? "Settle" : "Unsettle"))
                {
                    pendingPaymentSaleId = sale.SaleId;
                    pendingPaymentSettled = sale.PaidToVenueAt is null;
                    openPaymentPopup = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel"))
                {
                    pendingCancelSaleId = sale.SaleId;
                    pendingCancelLabel = $"{sale.ItemName} for {sale.BuyerCharacterName} ({sale.BuyerWorldName})";
                    cancelReason = string.Empty;
                    openCancelPopup = true;
                }
                ImGui.EndDisabled();
                if (locked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Resolve the pending settlement first.");
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private bool MatchesFilters(OtherSaleSummary sale)
    {
        var status = StatusFor(sale);
        var buyerMatches = string.IsNullOrWhiteSpace(buyerFilter) ||
                           Contains(sale.BuyerCharacterName, buyerFilter) ||
                           Contains(sale.BuyerWorldName, buyerFilter);
        return buyerMatches &&
               Contains(sale.SellerDisplayName, sellerFilter) &&
               Contains(sale.ItemName, itemFilter) &&
               Contains(status, statusFilter);
    }

    private static bool Contains(string value, string filter) => string.IsNullOrWhiteSpace(filter) || value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string StatusFor(OtherSaleSummary sale) => sale.VoidedAt is not null ? "Cancelled / refunded" : sale.PendingSettlementId is not null ? "Pending settlement" : sale.PaidToVenueAt is not null || sale.VenueShareGil == 0 ? "Settled" : "Unsettled";

    private void QueuePopups()
    {
        if (openPaymentPopup) { openPaymentPopup = false; ImGui.OpenPopup(PaymentPopup); }
        if (openCancelPopup) { openCancelPopup = false; ImGui.OpenPopup(CancelPopup); }
    }

    private void DrawSaleConfirmation(VenueConnectionConfiguration venue, OtherSalesManagementViewResponse view)
    {
        if (!ImGui.BeginPopupModal(SalePopup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        var item = view.Items.FirstOrDefault(x => x.ItemId == pendingItemId);
        if (item is null) { ImGui.TextUnformatted("The selected item is no longer available."); }
        else
        {
            var total = (long)item.PricePerUnitGil.GetValueOrDefault() * pendingQuantity;
            var seller = CalculateSellerShare(total, item.SellerPercentage);
            ImGui.TextWrapped($"Sell {pendingQuantity:N0} × {item.Name} to {pendingBuyerName} ({pendingBuyerWorld})?");
            ImGui.TextUnformatted(item.PricePerkId is null ? $"Buyer pays {total:N0} gil." : $"Buyer redeems VIP perk: {item.PricePerkName}.");
            ImGui.TextDisabled($"Seller keeps {seller:N0} gil; venue receives {total - seller:N0} gil.");
            if (ImGui.Button("Confirm sale"))
            {
                plugin.SellOtherSale(venue, new SellOtherSaleRequest(pendingBuyerName, pendingBuyerWorld, pendingItemId, pendingQuantity));
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine(); if (ImGui.Button("Back")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawPaymentConfirmation(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(PaymentPopup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped(pendingPaymentSettled ? "Confirm that the seller has paid the venue share for this sale?" : "Mark this sale unpaid again? Any confirmed settlement reservation for this sale will be released.");
        if (ImGui.Button("Confirm"))
        {
            plugin.SetOtherSalePaymentStatus(venue, pendingPaymentSaleId, new SetOtherSalePaymentStatusRequest(pendingPaymentSettled));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(); if (ImGui.Button("Back")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawCancelConfirmation(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(CancelPopup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextColored(Bad, "This action cancels the sale and records that the buyer was refunded in full.");
        ImGui.TextWrapped(pendingCancelLabel);
        ImGui.TextWrapped("Any VIP perk used for this purchase will be released and become available again according to its renewal rules.");
        ImGui.InputText("Reason (optional)", ref cancelReason, 255);
        if (ImGui.Button("Cancel sale and confirm refund"))
        {
            plugin.CancelOtherSale(venue, pendingCancelSaleId, new CancelOtherSaleRequest(string.IsNullOrWhiteSpace(cancelReason) ? null : cancelReason.Trim()));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(); if (ImGui.Button("Back")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void LoadEditor(OtherSaleItemSummary? item)
    {
        editingItemId = item?.ItemId ?? 0;
        itemName = item?.Name ?? string.Empty;
        priceWithPerk = item?.PricePerkId is not null;
        priceGil = item?.PricePerUnitGil ?? 0;
        selectedPerkId = item?.PricePerkId ?? 0;
        canSellQuantity = item?.CanSellQuantity ?? false;
        itemArchived = item?.ArchivedAt is not null;
        loadedSellerPercentage = item?.SellerPercentage ?? -1m;
        sellerPercentage = (float)(item?.SellerPercentage ?? 0m);
    }

    private void ResetForVenue(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId) return;
        activeProfileId = venue.ProfileId;
        selectedItemId = 0; quantity = 1; editingItemId = 0; itemName = string.Empty;
        buyerFilter = sellerFilter = itemFilter = statusFilter = string.Empty;
        settlementTargetName = settlementTargetWorld = string.Empty;
        loadedSellerPercentage = -1m;
    }

    private static long CalculateSellerShare(long total, decimal percentage) =>
        total <= 0 ? 0 : (long)Math.Round(total * percentage / 100m, 0, MidpointRounding.AwayFromZero);
}
