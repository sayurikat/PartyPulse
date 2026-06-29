using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Bar;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class BarTabRenderer(Plugin plugin)
{
    private static readonly Vector4 AvailableColor = new(0.35f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 WarningColor = new(1f, 0.72f, 0.25f, 1f);

    private Guid profileId;
    private long selectedPackageId;
    private int gambaQuantity = 5;
    private int startingJackpotGil;
    private float buyoutSellerPercentage;
    private int gambaTicketPriceGil = 1;
    private float gambaHousePercentage;
    private string packageName = string.Empty;
    private int packagePriceGil;
    private bool packageForOpening;
    private int packageHours = 1;
    private long? editingPackageId;
    private string pendingTargetName = string.Empty;
    private string pendingTargetWorld = string.Empty;
    private long pendingPackageId;
    private long? pendingCancelBuyoutId;
    private long? pendingCancelTicketId;
    private long? pendingWinnerGameId;
    private string pendingWinnerName = string.Empty;
    private string pendingWinnerWorld = string.Empty;
    private bool winnerAcknowledged;
    private string settlementTargetName = string.Empty;
    private string settlementTargetWorld = string.Empty;
    private bool settingsInitialized;
    private DateTimeOffset? settingsUpdatedAt;

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenue(venue);
        if (!ImGui.BeginTabItem("Bar"))
            return;

        plugin.EnsureBarLoaded(venue);
        plugin.EnsureTimedMacrosLoaded(venue);
        var snapshot = plugin.Bar.GetSnapshot(venue);
        var view = snapshot.View;
        var busy = plugin.Bar.IsBusy(venue.ProfileId) || plugin.IsGameMacroBusy;

        ImGui.BeginDisabled(plugin.Bar.IsBusy(venue.ProfileId));
        if (ImGui.Button("Refresh bar"))
            plugin.RefreshBar(venue);
        ImGui.EndDisabled();

        if (view is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        SyncDefaults(view);
        DrawActiveBuyout(venue, view, busy);
        DrawGamba(venue, view, busy);
        DrawSettlement(venue, view, busy);

        if (view.Capabilities.CanManage)
        {
            ImGui.Spacing();
            ImGui.Separator();
            DrawManagement(venue, view, busy);
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawHistory(venue, view, busy);
        DrawPopups(venue, view, busy);
        ImGui.EndTabItem();
    }

    private void DrawActiveBuyout(VenueConnectionConfiguration venue, BarManagementViewResponse view, bool busy)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Bar buyout");
        if (view.ActiveBuyout is { } active)
        {
            ImGui.TextColored(AvailableColor, $"Bought out by {active.BuyerCharacterName} @ {active.BuyerWorldName}");
            ImGui.TextDisabled($"{active.PackageName} — until {VenueTimeZone.Format(venue, active.EndsAt, "g")}");
            DrawMacroButton(venue, TimedMacroTypeCodes.BarBuyout, "Execute buyout macro", busy);
        }
        else
        {
            ImGui.TextDisabled("The bar is not currently bought out.");
        }

        if (!view.Capabilities.CanSell)
            return;

        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetReason))
        {
            ImGui.TextDisabled(targetReason);
            return;
        }

        var availablePackages = view.BuyoutPackages
            .Where(value => value.ArchivedAt is null)
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (availablePackages.Length == 0)
        {
            ImGui.TextDisabled("No active buyout package is configured.");
            return;
        }

        if (selectedPackageId == 0 || availablePackages.All(value => value.PackageId != selectedPackageId))
            selectedPackageId = availablePackages[0].PackageId;

        var selected = availablePackages.First(value => value.PackageId == selectedPackageId);
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Package##BarBuyout", $"{selected.Name} — {selected.PriceGil:N0} gil"))
        {
            foreach (var package in availablePackages)
            {
                var isSelected = package.PackageId == selectedPackageId;
                var duration = package.DurationMode == "opening"
                    ? "for the night"
                    : $"{package.DurationMinutes.GetValueOrDefault() / 60m:0.##} hour(s)";
                if (ImGui.Selectable($"{package.Name} — {package.PriceGil:N0} gil — {duration}", isSelected))
                    selectedPackageId = package.PackageId;
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.TextDisabled($"Target: {target!.DisplayName}");
        ImGui.BeginDisabled(busy || view.ActiveBuyout is not null);
        if (ImGui.Button("Record buyout sale"))
        {
            pendingTargetName = target.CharacterName;
            pendingTargetWorld = target.WorldName;
            pendingPackageId = selectedPackageId;
            ImGui.OpenPopup("Confirm bar buyout###PartyPulseBarBuyout");
        }
        ImGui.EndDisabled();
        if (view.ActiveBuyout is not null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Another buyout is active.");
        }
    }

    private void DrawGamba(VenueConnectionConfiguration venue, BarManagementViewResponse view, bool busy)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Gamba Shot");

        if (view.ActiveGame is not { } game)
        {
            ImGui.TextDisabled("No Gamba Shot game is active.");
            if (view.Capabilities.CanManageGame)
            {
                ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
                ImGui.InputInt("Starting jackpot", ref startingJackpotGil, 100_000, 1_000_000);
                startingJackpotGil = Math.Max(0, startingJackpotGil);
                ImGui.BeginDisabled(busy);
                if (ImGui.Button("Start Gamba Shot"))
                    ImGui.OpenPopup("Start Gamba Shot###PartyPulseStartGamba");
                ImGui.EndDisabled();
            }
            return;
        }

        ImGui.TextColored(AvailableColor, $"Current jackpot: {game.CurrentJackpotGil:N0} gil");
        ImGui.TextDisabled($"Game #{game.GameId} — {game.TicketQuantity:N0} ticket(s) sold at {game.TicketPriceGil:N0} gil each");
        DrawMacroButton(venue, TimedMacroTypeCodes.BarGamba, "Execute jackpot macro", busy);

        if (view.Capabilities.CanSell)
        {
            if (plugin.TargetProvider.TryGetCurrentTarget(
                    out var target,
                    out var targetReason))
            {
                ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
                ImGui.InputInt("Quantity##Gamba", ref gambaQuantity, 1, 5);

                gambaQuantity = Math.Clamp(gambaQuantity, 1, 100_000);

                var gross = (long)gambaQuantity * game.TicketPriceGil;

                ImGui.TextDisabled(
                    $"Target: {target!.DisplayName} — total {gross:N0} gil");

                ImGui.BeginDisabled(busy || gross > int.MaxValue);

                if (ImGui.Button("Record ticket sale"))
                {
                    pendingTargetName = target.CharacterName;
                    pendingTargetWorld = target.WorldName;

                    ImGui.OpenPopup(
                        "Confirm Gamba ticket sale###PartyPulseGambaSale");
                }

                ImGui.EndDisabled();
            }
            else
            {
                ImGui.TextDisabled(targetReason);
            }
        }

        if (view.Capabilities.CanManageGame)
        {
            var hasWinnerTarget = plugin.TargetProvider.TryGetCurrentTarget(out var winnerTarget, out var winnerReason);
            ImGui.BeginDisabled(busy || !hasWinnerTarget);
            if (ImGui.Button("Confirm targeted player won"))
            {
                pendingWinnerGameId = game.GameId;
                pendingWinnerName = winnerTarget!.CharacterName;
                pendingWinnerWorld = winnerTarget.WorldName;
                winnerAcknowledged = false;
                ImGui.OpenPopup("Confirm Gamba winner###PartyPulseGambaWinner");
            }
            ImGui.EndDisabled();
            if (!hasWinnerTarget)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(winnerReason);
            }
        }
    }

    private void DrawSettlement(VenueConnectionConfiguration venue, BarManagementViewResponse view, bool busy)
    {
        if (view.PersonalUnpaidGil <= 0)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted($"Your unsettled bar money: {view.PersonalUnpaidGil:N0} gil");
        if (view.PersonalPendingGil > 0)
            ImGui.TextDisabled($"Already pending: {view.PersonalPendingGil:N0} gil");
        ImGui.TextUnformatted($"Available to settle: {view.PersonalAvailableGil:N0} gil");

        var hasTarget = plugin.TargetProvider.TryGetCurrentTarget(out var target, out var reason);
        ImGui.BeginDisabled(busy || !hasTarget || view.PersonalAvailableGil <= 0);
        if (ImGui.Button("Settle bar money"))
        {
            settlementTargetName = target!.CharacterName;
            settlementTargetWorld = target.WorldName;
            ImGui.OpenPopup("Create bar settlement###PartyPulseBarSettlement");
        }
        ImGui.EndDisabled();
        if (!hasTarget)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(reason);
        }
    }

    private void DrawManagement(VenueConnectionConfiguration venue, BarManagementViewResponse view, bool busy)
    {
        if (ImGui.CollapsingHeader("Bar settings"))
        {
            ImGui.TextDisabled("Percentages are snapshotted on every sale. Existing sales never change when settings are edited.");
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            ImGui.InputFloat("Bartender keeps buyout (%)", ref buyoutSellerPercentage, 0.25f, 1f, "%.2f");
            buyoutSellerPercentage = Math.Clamp(buyoutSellerPercentage, 0f, 100f);
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("Gamba ticket price", ref gambaTicketPriceGil, 1_000, 10_000);
            gambaTicketPriceGil = Math.Max(1, gambaTicketPriceGil);
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            ImGui.InputFloat("House keeps Gamba (%)", ref gambaHousePercentage, 0.25f, 1f, "%.2f");
            gambaHousePercentage = Math.Clamp(gambaHousePercentage, 0f, 100f);
            ImGui.BeginDisabled(busy);
            if (ImGui.Button("Save bar settings"))
                plugin.UpdateBarSettings(venue, new UpdateBarSettingsRequest((decimal)buyoutSellerPercentage, gambaTicketPriceGil, (decimal)gambaHousePercentage));
            ImGui.EndDisabled();
        }

        if (!ImGui.CollapsingHeader("Buyout packages"))
            return;

        foreach (var package in view.BuyoutPackages.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            ImGui.PushID($"package-{package.PackageId}");
            var duration = package.DurationMode == "opening"
                ? "for the night"
                : $"{package.DurationMinutes.GetValueOrDefault() / 60m:0.##} hour(s)";
            ImGui.TextUnformatted($"{package.Name} — {package.PriceGil:N0} gil — {duration}");
            if (package.ArchivedAt is not null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(archived)");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit"))
                BeginEditPackage(package);
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.InputText("Package name", ref packageName, 100);
        ImGui.InputInt("Price (gil)", ref packagePriceGil, 10_000, 100_000);
        packagePriceGil = Math.Max(0, packagePriceGil);
        ImGui.Checkbox("For the rest of the active opening", ref packageForOpening);
        if (!packageForOpening)
        {
            ImGui.InputInt("Duration (hours)", ref packageHours);
            packageHours = Math.Clamp(packageHours, 1, 168);
        }

        ImGui.BeginDisabled(busy || string.IsNullOrWhiteSpace(packageName));
        if (editingPackageId is { } packageId)
        {
            if (ImGui.Button("Save package"))
            {
                var existing = view.BuyoutPackages.First(value => value.PackageId == packageId);
                plugin.UpdateBarBuyoutPackage(
                    venue,
                    packageId,
                    new UpdateBarBuyoutPackageRequest(
                        packageName.Trim(),
                        packagePriceGil,
                        packageForOpening ? "opening" : "hours",
                        packageForOpening ? null : packageHours * 60,
                        existing.ArchivedAt is not null));
                ClearPackageDraft();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel edit"))
                ClearPackageDraft();
            ImGui.SameLine();
            var package = view.BuyoutPackages.First(value => value.PackageId == packageId);
            if (ImGui.Button(package.ArchivedAt is null ? "Archive" : "Restore"))
            {
                plugin.UpdateBarBuyoutPackage(
                    venue,
                    packageId,
                    new UpdateBarBuyoutPackageRequest(
                        package.Name,
                        package.PriceGil,
                        package.DurationMode,
                        package.DurationMinutes,
                        package.ArchivedAt is null));
                ClearPackageDraft();
            }
        }
        else if (ImGui.Button("Create package"))
        {
            plugin.CreateBarBuyoutPackage(
                venue,
                new CreateBarBuyoutPackageRequest(
                    packageName.Trim(),
                    packagePriceGil,
                    packageForOpening ? "opening" : "hours",
                    packageForOpening ? null : packageHours * 60));
            ClearPackageDraft();
        }
        ImGui.EndDisabled();
    }

    private void DrawHistory(VenueConnectionConfiguration venue, BarManagementViewResponse view, bool busy)
    {
        if (ImGui.CollapsingHeader("Recent buyout sales", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var sale in view.BuyoutSales.OrderByDescending(value => value.SoldAt).Take(100))
            {
                ImGui.PushID($"buyout-sale-{sale.SaleId}");
                ImGui.TextUnformatted($"#{sale.SaleId} {sale.PackageName} — {sale.BuyerCharacterName} @ {sale.BuyerWorldName}");
                ImGui.TextDisabled($"Seller: {sale.SellerDisplayName}; {sale.PriceGil:N0} gil; venue {sale.VenueShareGil:N0}; sold {VenueTimeZone.Format(venue, sale.SoldAt, "g")}");
                DrawSaleState(sale.PaidToVenueAt, sale.PendingSettlementId, sale.VoidedAt);
                if (view.Capabilities.CanManageSettlements && sale.VoidedAt is null && sale.PendingSettlementId is null)
                {
                    ImGui.BeginDisabled(busy);
                    if (ImGui.SmallButton(sale.PaidToVenueAt is null ? "Mark settled" : "Mark unpaid"))
                        plugin.SetBarBuyoutPaymentStatus(venue, sale.SaleId, sale.PaidToVenueAt is null);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Cancel sale"))
                    {
                        pendingCancelBuyoutId = sale.SaleId;
                        ImGui.OpenPopup("Cancel bar sale###PartyPulseCancelBarSale");
                    }
                    ImGui.EndDisabled();
                }
                ImGui.Separator();
                ImGui.PopID();
            }
        }

        if (ImGui.CollapsingHeader("Recent Gamba ticket sales"))
        {
            foreach (var sale in view.GambaTicketSales.OrderByDescending(value => value.SoldAt).Take(100))
            {
                ImGui.PushID($"ticket-sale-{sale.SaleId}");
                ImGui.TextUnformatted($"#{sale.SaleId} {sale.Quantity:N0} ticket(s) — {sale.BuyerCharacterName} @ {sale.BuyerWorldName}");
                ImGui.TextDisabled($"Seller: {sale.SellerDisplayName}; gross {sale.GrossGil:N0}; house {sale.HouseShareGil:N0}; jackpot +{sale.JackpotContributionGil:N0}");
                DrawSaleState(sale.PaidToVenueAt, sale.PendingSettlementId, sale.VoidedAt);
                if (view.Capabilities.CanManageSettlements && sale.VoidedAt is null && sale.PendingSettlementId is null)
                {
                    ImGui.BeginDisabled(busy);
                    if (ImGui.SmallButton(sale.PaidToVenueAt is null ? "Mark settled" : "Mark unpaid"))
                        plugin.SetGambaTicketPaymentStatus(venue, sale.SaleId, sale.PaidToVenueAt is null);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Cancel sale"))
                    {
                        pendingCancelTicketId = sale.SaleId;
                        ImGui.OpenPopup("Cancel Gamba sale###PartyPulseCancelGambaSale");
                    }
                    ImGui.EndDisabled();
                }
                ImGui.Separator();
                ImGui.PopID();
            }
        }

        if (ImGui.CollapsingHeader("Gamba Shot history"))
        {
            foreach (var game in view.GambaGameHistory.OrderByDescending(value => value.StartedAt).Take(50))
            {
                var winner = game.WinnerCharacterName is null
                    ? "No winner recorded"
                    : $"{game.WinnerCharacterName} @ {game.WinnerWorldName}";
                ImGui.TextUnformatted($"Game #{game.GameId} — {game.FinalJackpotGil.GetValueOrDefault(game.CurrentJackpotGil):N0} gil — {winner}");
                ImGui.TextDisabled($"Started {VenueTimeZone.Format(venue, game.StartedAt, "g")}; {game.TicketQuantity:N0} ticket(s); gross {game.GrossSalesGil:N0} gil");
                ImGui.Separator();
            }
        }
    }

    private void DrawMacroButton(VenueConnectionConfiguration venue, string typeCode, string label, bool busy)
    {
        var timedView = plugin.TimedMacros.GetSnapshot(venue).View;
        var macro = timedView?.Macros.FirstOrDefault(value =>
            string.Equals(value.TypeCode, typeCode, StringComparison.OrdinalIgnoreCase) &&
            !value.IsTemplate);
        var opening = timedView?.CurrentOpening;
        var locationOk = macro is not { RequiresActiveOpening: true } ||
                         opening is not null && plugin.LocationProvider.IsAtAddress(
                             opening.AddressWorldName,
                             opening.AddressCityName,
                             opening.AddressWard,
                             opening.AddressPlot,
                             out _);
        var canExecute = macro is { CanExecute: true, Enabled: true, IsConfigured: true } && locationOk;
        ImGui.BeginDisabled(busy || !canExecute);
        if (ImGui.Button(label) && macro is not null)
            plugin.RunTimedMacro(venue, macro, macro.RequiresActiveOpening ? opening : null);
        ImGui.EndDisabled();
        if (!canExecute)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(macro is null ? "Macro unavailable." : "Configure or enable this macro in Timed Macros.");
        }
    }

    private void DrawPopups(VenueConnectionConfiguration venue, BarManagementViewResponse view, bool busy)
    {
        if (ImGui.BeginPopupModal("Confirm bar buyout###PartyPulseBarBuyout", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var package = view.BuyoutPackages.FirstOrDefault(value => value.PackageId == pendingPackageId);
            ImGui.TextWrapped(package is null
                ? "The selected package is no longer available."
                : $"Record {package.Name} for {pendingTargetName} @ {pendingTargetWorld} at {package.PriceGil:N0} gil?");
            ImGui.TextWrapped("The server will re-check the active opening and ensure no other buyout is active.");
            ImGui.BeginDisabled(busy || package is null);
            if (ImGui.Button("Confirm sale") && package is not null)
            {
                plugin.SellBarBuyout(venue, new SellBarBuyoutRequest(pendingTargetName, pendingTargetWorld, package.PackageId));
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("Start Gamba Shot###PartyPulseStartGamba", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Start a new Gamba Shot game with a {startingJackpotGil:N0} gil jackpot?");
            ImGui.BeginDisabled(busy);
            if (ImGui.Button("Start game"))
            {
                plugin.StartGambaGame(venue, startingJackpotGil);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("Confirm Gamba ticket sale###PartyPulseGambaSale", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var game = view.ActiveGame;
            var total = (long)gambaQuantity * (game?.TicketPriceGil ?? 0);
            ImGui.TextWrapped($"Record {gambaQuantity:N0} ticket(s) for {pendingTargetName} @ {pendingTargetWorld} at {total:N0} gil?");
            ImGui.BeginDisabled(busy || game is null || total > int.MaxValue);
            if (ImGui.Button("Confirm sale"))
            {
                plugin.SellGambaTickets(venue, new SellGambaTicketsRequest(pendingTargetName, pendingTargetWorld, gambaQuantity));
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("Confirm Gamba winner###PartyPulseGambaWinner", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Confirm {pendingWinnerName} @ {pendingWinnerWorld} as winner? This permanently closes the game and freezes the jackpot.");
            ImGui.Checkbox("I have checked the target and winner", ref winnerAcknowledged);
            ImGui.BeginDisabled(busy || !winnerAcknowledged || pendingWinnerGameId is null);
            if (ImGui.Button("Confirm winner and close game"))
            {
                plugin.CompleteGambaGame(venue, pendingWinnerGameId!.Value, new CompleteGambaGameRequest(pendingWinnerName, pendingWinnerWorld));
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("Create bar settlement###PartyPulseBarSettlement", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Create a settlement with {settlementTargetName} @ {settlementTargetWorld} for {view.PersonalAvailableGil:N0} gil?");
            ImGui.TextWrapped("This includes the venue share of buyouts and the full gross collected from Gamba ticket sales, including the jackpot contribution.");
            ImGui.BeginDisabled(busy || view.PersonalAvailableGil <= 0);
            if (ImGui.Button("Create settlement and start trade"))
            {
                plugin.CreateBarSettlement(venue, new CreateBarSettlementRequest(settlementTargetName, settlementTargetWorld));
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("Cancel bar sale###PartyPulseCancelBarSale", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Cancel this buyout sale? The active buyout ends immediately and the sale is excluded from settlement.");
            ImGui.BeginDisabled(busy || pendingCancelBuyoutId is null);
            if (ImGui.Button("Cancel sale"))
            {
                plugin.CancelBarBuyout(venue, pendingCancelBuyoutId!.Value, "Cancelled by finance manager.");
                pendingCancelBuyoutId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Keep sale")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("Cancel Gamba sale###PartyPulseCancelGambaSale", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Cancel this ticket sale? Its jackpot contribution is removed. Completed games cannot be changed.");
            ImGui.BeginDisabled(busy || pendingCancelTicketId is null);
            if (ImGui.Button("Cancel sale"))
            {
                plugin.CancelGambaTicketSale(venue, pendingCancelTicketId!.Value, "Cancelled by finance manager.");
                pendingCancelTicketId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Keep sale")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private static void DrawSaleState(DateTimeOffset? paidAt, long? pendingSettlementId, DateTimeOffset? voidedAt)
    {
        if (voidedAt is not null)
            ImGui.TextColored(WarningColor, "Cancelled");
        else if (pendingSettlementId is { } settlementId)
            ImGui.TextDisabled($"Pending settlement #{settlementId}");
        else if (paidAt is not null)
            ImGui.TextColored(AvailableColor, "Settled");
        else
            ImGui.TextColored(WarningColor, "Unpaid");
    }

    private void ResetForVenue(VenueConnectionConfiguration venue)
    {
        if (profileId == venue.ProfileId)
            return;
        profileId = venue.ProfileId;
        selectedPackageId = 0;
        startingJackpotGil = 0;
        buyoutSellerPercentage = 0f;
        gambaTicketPriceGil = 1;
        gambaHousePercentage = 0f;
        settingsInitialized = false;
        settingsUpdatedAt = null;
        ClearPackageDraft();
    }

    private void SyncDefaults(BarManagementViewResponse view)
    {
        if (startingJackpotGil == 0)
            startingJackpotGil = view.SuggestedStartingJackpotGil;

        if (settingsInitialized && settingsUpdatedAt == view.Settings.UpdatedAt)
            return;

        buyoutSellerPercentage = (float)view.Settings.BuyoutSellerPercentage;
        gambaTicketPriceGil = view.Settings.GambaTicketPriceGil;
        gambaHousePercentage = (float)view.Settings.GambaHousePercentage;
        settingsUpdatedAt = view.Settings.UpdatedAt;
        settingsInitialized = true;
    }

    private void BeginEditPackage(BarBuyoutPackageSummary package)
    {
        editingPackageId = package.PackageId;
        packageName = package.Name;
        packagePriceGil = package.PriceGil;
        packageForOpening = string.Equals(package.DurationMode, "opening", StringComparison.OrdinalIgnoreCase);
        packageHours = Math.Max(1, package.DurationMinutes.GetValueOrDefault(60) / 60);
    }

    private void ClearPackageDraft()
    {
        editingPackageId = null;
        packageName = string.Empty;
        packagePriceGil = 0;
        packageForOpening = false;
        packageHours = 1;
    }
}
