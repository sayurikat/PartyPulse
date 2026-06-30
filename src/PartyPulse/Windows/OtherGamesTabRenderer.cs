using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.OtherGames;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class OtherGamesTabRenderer(Plugin plugin)
{
    private const string SalePopup = "Confirm Other Game sale###PartyPulseOtherGameConfirm";
    private const string SettlementStatusPopup = "Confirm settlement status###PartyPulseOtherGameSettlementStatus";
    private const string OutcomePopup = "Record game outcome###PartyPulseOtherGameOutcome";
    private const string CancelPopup = "Cancel and refund Other Game sale###PartyPulseOtherGameCancel";
    private const string PayoutPopup = "Create Other Games seller payout###PartyPulseOtherGamePayout";
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
    private long pendingSettlementSaleId;
    private bool pendingSettlementSettled;
    private bool openSettlementStatusPopup;
    private long pendingOutcomeSaleId;
    private string pendingOutcomeLabel = string.Empty;
    private bool pendingNoWin;
    private string pendingWinAmountText = string.Empty;
    private bool openOutcomePopup;
    private long pendingCancelSaleId;
    private string pendingCancelLabel = string.Empty;
    private string cancelReason = string.Empty;
    private bool openCancelPopup;
    private int pendingPayoutSellerUserId;
    private string pendingPayoutSellerName = string.Empty;
    private long pendingPayoutAmountGil;
    private bool openPayoutPopup;

    public void Draw(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("Other Games")) return;

        ResetForVenue(venue);
        plugin.EnsureOtherGamesLoaded(venue);
        var snapshot = plugin.OtherGames.GetSnapshot(venue);
        var busy = plugin.OtherGames.IsBusy(venue.ProfileId) || plugin.Finance.IsBusy(venue.ProfileId);

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh Other Games")) plugin.RefreshOtherGames(venue);
        ImGui.EndDisabled();

        if (snapshot.Status != OtherGamesManagementStatus.Ready || snapshot.View is null)
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
                $"Sales: {view.PersonalGrossGil:N0} gil | Seller share: {view.PersonalSellerShareGil:N0} gil | " +
                $"Wins: {view.PersonalWinGil:N0} gil | Awaiting outcome: {view.PersonalAwaitingOutcomeCount:N0}");
            ImGui.TextUnformatted("Available settlement balance: ");
            ImGui.SameLine();
            DrawSignedGil(view.PersonalAvailableNetGil);
            if (view.PersonalPendingNetGil != 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"(pending {view.PersonalPendingNetGil:N0} gil)");
            }
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
        DrawSettlementStatusConfirmation(venue);
        DrawOutcomeConfirmation(venue);
        DrawCancelConfirmation(venue);
        DrawPayoutConfirmation(venue);
        ImGui.EndTabItem();
    }

    private void DrawSeller(VenueConnectionConfiguration venue, OtherGamesManagementViewResponse view, bool busy)
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
            if (ImGui.RadioButton("##SelectOtherGame", selected))
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

    private void DrawSettlement(VenueConnectionConfiguration venue, OtherGamesManagementViewResponse view, bool busy)
    {
        if (view.PersonalAvailableSaleCount <= 0) return;

        ImGui.Spacing();
        ImGui.TextUnformatted("Settle resolved game sales");
        var amount = view.PersonalAvailableNetGil;
        if (amount < 0)
        {
            ImGui.TextColored(Bad, $"Venue owes seller {-amount:N0} gil.");
            ImGui.TextDisabled("A finance manager can create and trade this payout from the red seller balance below.");
            return;
        }

        var hasTarget = plugin.TargetProvider.TryGetCurrentTarget(out var target, out var reason);
        if (hasTarget)
        {
            settlementTargetName = target!.CharacterName;
            settlementTargetWorld = target.WorldName;
            ImGui.TextUnformatted($"Finance manager: {target.DisplayName}");
        }
        else
        {
            ImGui.TextDisabled(reason);
        }

        if (amount > 0)
            ImGui.TextUnformatted($"Seller owes venue {amount:N0} gil.");
        else
            ImGui.TextDisabled("The resolved sales net to 0 gil and can be closed without a trade.");

        ImGui.BeginDisabled(busy || !hasTarget);
        var label = amount > 0
            ? $"Create settlement ({amount:N0} gil)"
            : "Create zero-net settlement";
        if (ImGui.Button(label))
            plugin.CreateOtherGamesSettlement(venue, new CreateOtherGamesSettlementRequest(settlementTargetName, settlementTargetWorld));
        ImGui.EndDisabled();
        ImGui.TextDisabled("The targeted user must have finance settlement permission.");
    }

    private void DrawItemManagement(VenueConnectionConfiguration venue, OtherGamesManagementViewResponse view, bool busy)
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
                plugin.CreateOtherGameItem(venue, new CreateOtherGameItemRequest(itemName.Trim(), priceWithPerk ? null : priceGil, priceWithPerk ? selectedPerkId : null, !priceWithPerk && canSellQuantity));
        }
        else if (ImGui.Button("Save item"))
        {
            plugin.UpdateOtherGameItem(venue, editingItemId, new UpdateOtherGameItemRequest(itemName.Trim(), priceWithPerk ? null : priceGil, priceWithPerk ? selectedPerkId : null, !priceWithPerk && canSellQuantity, itemArchived));
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
                sellerPercentage = Math.Clamp(sellerPercentage, 0f, 100f);
                ImGui.BeginDisabled(busy || current is null || Math.Abs(sellerPercentage - (float)current.SellerPercentage) < 0.005f);
                if (ImGui.Button("Save seller percentage"))
                    plugin.UpdateOtherGameSellerPercentage(venue, editingItemId, new UpdateOtherGameSellerPercentageRequest(Math.Round((decimal)sellerPercentage, 2)));
                ImGui.EndDisabled();
                ImGui.TextDisabled("Only venue owners can change this percentage.");
            }
        }
    }

    private void DrawHistory(VenueConnectionConfiguration venue, OtherGamesManagementViewResponse view, bool busy)
    {
        ImGui.TextUnformatted("Game history");
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale); ImGui.InputText("Buyer", ref buyerFilter, 100);
        ImGui.SameLine(); ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale); ImGui.InputText("Seller", ref sellerFilter, 100);
        ImGui.SameLine(); ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale); ImGui.InputText("Game", ref itemFilter, 100);
        ImGui.SameLine(); ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale); ImGui.InputText("Status", ref statusFilter, 50);

        if (view.Capabilities.CanManageSettlements && view.SellerBalances.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Seller unsettled balances");
            if (ImGui.BeginTable("OtherGameSellerBalances", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Seller"); ImGui.TableSetupColumn("Available");
                ImGui.TableSetupColumn("Pending"); ImGui.TableSetupColumn("Awaiting outcome");
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();
                foreach (var balance in view.SellerBalances)
                {
                    ImGui.PushID(balance.SellerUserId);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(balance.SellerDisplayName);
                    ImGui.TableNextColumn(); DrawSignedGil(balance.AvailableNetGil);
                    ImGui.TableNextColumn(); DrawSignedGil(balance.PendingNetGil);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(balance.AwaitingOutcomeCount.ToString("N0"));
                    ImGui.TableNextColumn();
                    var canPay = balance.AvailableNetGil < 0 &&
                                 !string.IsNullOrWhiteSpace(balance.SellerCharacterName) &&
                                 !string.IsNullOrWhiteSpace(balance.SellerWorldName);
                    ImGui.BeginDisabled(busy || !canPay);
                    if (ImGui.SmallButton("Pay seller"))
                    {
                        pendingPayoutSellerUserId = balance.SellerUserId;
                        pendingPayoutSellerName = balance.SellerDisplayName;
                        pendingPayoutAmountGil = -balance.AvailableNetGil;
                        openPayoutPopup = true;
                    }
                    ImGui.EndDisabled();
                    if (balance.AvailableNetGil < 0 && !canPay && ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted("The seller needs a linked character before Dropbox can receive the payout.");
                        ImGui.EndTooltip();
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
        }

        var rows = view.Sales.Where(MatchesFilters).ToArray();
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("OtherGamesHistory", 11, flags, new Vector2(0, 360 * ImGuiHelpers.GlobalScale))) return;
        ImGui.TableSetupColumn("Time"); ImGui.TableSetupColumn("Buyer"); ImGui.TableSetupColumn("Seller");
        ImGui.TableSetupColumn("Game"); ImGui.TableSetupColumn("Qty"); ImGui.TableSetupColumn("Sale");
        ImGui.TableSetupColumn("Venue share"); ImGui.TableSetupColumn("Win"); ImGui.TableSetupColumn("Net");
        ImGui.TableSetupColumn("Status"); ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 255 * ImGuiHelpers.GlobalScale);
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
            ImGui.TableNextColumn(); ImGui.TextUnformatted(sale.WinAmountGil is { } win ? $"{win:N0}" : "—");
            ImGui.TableNextColumn();
            if (sale.NetVenueGil is { } net) DrawSignedGil(net); else ImGui.TextDisabled("Awaiting");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(StatusFor(sale));
            ImGui.TableNextColumn();

            var locked = sale.PendingSettlementId is not null;
            if (sale.CanSetOutcome)
            {
                ImGui.BeginDisabled(busy || locked);
                if (ImGui.SmallButton("No win"))
                {
                    pendingOutcomeSaleId = sale.SaleId;
                    pendingOutcomeLabel = $"{sale.ItemName} for {sale.BuyerCharacterName}";
                    pendingNoWin = true; pendingWinAmountText = string.Empty; openOutcomePopup = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Enter win"))
                {
                    pendingOutcomeSaleId = sale.SaleId;
                    pendingOutcomeLabel = $"{sale.ItemName} for {sale.BuyerCharacterName}";
                    pendingNoWin = false; pendingWinAmountText = sale.WinAmountGil?.ToString() ?? string.Empty; openOutcomePopup = true;
                }
                ImGui.EndDisabled();
            }

            if (view.Capabilities.CanManageSettlements && sale.VoidedAt is null && sale.OutcomeStatus != "pending")
            {
                if (sale.CanSetOutcome) ImGui.SameLine();
                ImGui.BeginDisabled(busy || locked);
                if (ImGui.SmallButton(sale.SettledAt is null ? "Settle" : "Unsettle"))
                {
                    pendingSettlementSaleId = sale.SaleId;
                    pendingSettlementSettled = sale.SettledAt is null;
                    openSettlementStatusPopup = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel"))
                {
                    pendingCancelSaleId = sale.SaleId;
                    pendingCancelLabel = $"{sale.ItemName} for {sale.BuyerCharacterName} ({sale.BuyerWorldName})";
                    cancelReason = string.Empty; openCancelPopup = true;
                }
                ImGui.EndDisabled();
            }
            else if (view.Capabilities.CanManageSettlements && sale.VoidedAt is null)
            {
                if (sale.CanSetOutcome) ImGui.SameLine();
                ImGui.BeginDisabled(busy || locked);
                if (ImGui.SmallButton("Cancel"))
                {
                    pendingCancelSaleId = sale.SaleId;
                    pendingCancelLabel = $"{sale.ItemName} for {sale.BuyerCharacterName} ({sale.BuyerWorldName})";
                    cancelReason = string.Empty; openCancelPopup = true;
                }
                ImGui.EndDisabled();
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private bool MatchesFilters(OtherGameSaleSummary sale)
    {
        var status = StatusFor(sale);
        var buyerMatches = string.IsNullOrWhiteSpace(buyerFilter) || Contains(sale.BuyerCharacterName, buyerFilter) || Contains(sale.BuyerWorldName, buyerFilter);
        return buyerMatches && Contains(sale.SellerDisplayName, sellerFilter) && Contains(sale.ItemName, itemFilter) && Contains(status, statusFilter);
    }

    private static bool Contains(string value, string filter) => string.IsNullOrWhiteSpace(filter) || value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string StatusFor(OtherGameSaleSummary sale) =>
        sale.VoidedAt is not null ? "Cancelled / refunded" :
        sale.PendingSettlementId is not null ? "Pending settlement" :
        sale.SettledAt is not null ? "Settled" :
        sale.OutcomeStatus == "pending" ? "Awaiting outcome" :
        sale.OutcomeStatus == "no_win" ? "No win / unsettled" : "Win / unsettled";

    private static void DrawSignedGil(long amount)
    {
        if (amount < 0) ImGui.TextColored(Bad, $"{amount:N0} gil (venue owes seller)");
        else ImGui.TextUnformatted($"{amount:N0} gil");
    }

    private void QueuePopups()
    {
        if (openSettlementStatusPopup) { openSettlementStatusPopup = false; ImGui.OpenPopup(SettlementStatusPopup); }
        if (openOutcomePopup) { openOutcomePopup = false; ImGui.OpenPopup(OutcomePopup); }
        if (openCancelPopup) { openCancelPopup = false; ImGui.OpenPopup(CancelPopup); }
        if (openPayoutPopup) { openPayoutPopup = false; ImGui.OpenPopup(PayoutPopup); }
    }

    private void DrawSaleConfirmation(VenueConnectionConfiguration venue, OtherGamesManagementViewResponse view)
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
                plugin.SellOtherGame(venue, new SellOtherGameRequest(pendingBuyerName, pendingBuyerWorld, pendingItemId, pendingQuantity));
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine(); if (ImGui.Button("Back")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawSettlementStatusConfirmation(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(SettlementStatusPopup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped(pendingSettlementSettled
            ? "Confirm that the net amount for this game sale has been settled between the seller and venue?"
            : "Mark this game sale unsettled again? Any confirmed settlement reservation for it will be released.");
        if (ImGui.Button("Confirm"))
        {
            plugin.SetOtherGameSettlementStatus(venue, pendingSettlementSaleId, new SetOtherGameSettlementStatusRequest(pendingSettlementSettled));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(); if (ImGui.Button("Back")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawOutcomeConfirmation(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(OutcomePopup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped(pendingOutcomeLabel);
        if (pendingNoWin)
        {
            ImGui.TextWrapped("Confirm that the buyer did not win. The settlement amount will remain the venue share for this sale.");
            if (ImGui.Button("Confirm no win"))
            {
                plugin.SetOtherGameOutcome(venue, pendingOutcomeSaleId, new SetOtherGameOutcomeRequest("no_win", null));
                ImGui.CloseCurrentPopup();
            }
        }
        else
        {
            ImGui.InputText("Win amount (gil)", ref pendingWinAmountText, 24);
            var valid = long.TryParse(pendingWinAmountText.Replace(",", string.Empty).Trim(), out var win) && win > 0;
            ImGui.TextDisabled("This amount is deducted from the seller's unsettled venue balance.");
            ImGui.BeginDisabled(!valid);
            if (ImGui.Button("Record win"))
            {
                plugin.SetOtherGameOutcome(venue, pendingOutcomeSaleId, new SetOtherGameOutcomeRequest("win", win));
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
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
            plugin.CancelOtherGame(venue, pendingCancelSaleId, new CancelOtherGameSaleRequest(string.IsNullOrWhiteSpace(cancelReason) ? null : cancelReason.Trim()));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(); if (ImGui.Button("Back")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawPayoutConfirmation(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(PayoutPopup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextColored(Bad, $"Venue owes {pendingPayoutSellerName} {pendingPayoutAmountGil:N0} gil.");
        ImGui.TextWrapped("Create a pending payout settlement and start Dropbox to trade the complete negative available balance to this seller?");
        if (ImGui.Button("Create payout and trade"))
        {
            plugin.CreateOtherGamesPayout(venue, new CreateOtherGamesPayoutRequest(pendingPayoutSellerUserId));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Back")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void LoadEditor(OtherGameItemSummary? item)
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
