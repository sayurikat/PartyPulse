using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Vip;
using PartyPulse.TimedMacros;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class VipTabRenderer(Plugin plugin)
{
    private const string DiscordRoleManagementPopupName =
        "Confirm VIP Discord role management###PartyPulseVipDiscordRoleManagement";
    private const string RedeemPerkPopupName = "Confirm VIP perk redemption###PartyPulseVipPerkRedeem";
    private const string UndoPerkPopupName = "Undo VIP perk redemption###PartyPulseUndoVipPerk";

    private static readonly Vector4 AvailableColor = new(0.35f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 UnavailableColor = new(0.65f, 0.18f, 0.18f, 1f);

    private Guid activeProfileId;
    private string lastTargetKey = string.Empty;
    private int selectedPackageId;
    private int selectedExistingVipPlayerId;
    private string saleDiscordUsername = string.Empty;
    private bool customerPaymentConfirmed;
    private string vipPlayerNameFilter = string.Empty;
    private bool vipPlayerActiveOnly;
    private bool vipPlayerExpiringSoonOnly;
    private bool vipPlayerNearbyOnly;
    private bool vipPlayerNoCharacterOnly;
    private bool vipPlayerServerBoosterOnly;
    private readonly Dictionary<string, string> arrivalMacroDrafts = new(StringComparer.OrdinalIgnoreCase);
    private int temporaryOpeningDurationMinutes = 480;
    private string temporaryOpeningTitle = string.Empty;

    private int editingPackageId;
    private string packageName = string.Empty;
    private int packagePriceGil;
    private int packageDays;
    private int packageMonths;
    private int packageYears;
    private bool packageLifetime;
    private long packageDiscordRoleId;
    private long loadedPackageDiscordRoleId;
    private bool packageGrantedByServerBoost;
    private bool packageArchived;
    private bool openDiscordRoleManagementPopup;
    private string settlementTargetName = string.Empty;
    private string settlementTargetWorld = string.Empty;
    private int editingPerkId;
    private string perkName = string.Empty;
    private bool perkArchived;
    private int assignmentPackageId;
    private int assignmentPerkId;
    private int renewalMode;
    private int renewalInterval = 1;
    private int loadedAssignmentPackageId;
    private int loadedAssignmentPerkId;
    private string pendingRedeemCharacterName = string.Empty;
    private string pendingRedeemWorldName = string.Empty;
    private string pendingRedeemPerkName = string.Empty;
    private int pendingRedeemPerkId;
    private long pendingUndoRedemptionId;
    private string undoReason = string.Empty;
    private bool openRedeemPerkPopup;
    private bool openUndoPerkPopup;

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenueChange(venue);
        plugin.EnsureVipLoaded(venue);
        plugin.EnsureVipPerksLoaded(venue);
        plugin.EnsureVipArrivalsLoaded(venue);
        plugin.EnsureTimedMacrosLoaded(venue);

        var snapshot = plugin.Vip.GetSnapshot(venue);
        var isBusy = plugin.Vip.IsBusy(venue.ProfileId);

        PartyPulseUi.PageHeader("VIP", "Manage VIP players, subscriptions, arrivals, packages, perks, and related macros.");

        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh VIP data"))
        {
            plugin.RefreshVip(venue);
            plugin.RefreshVipPerks(venue);
            plugin.RefreshPhotoshoots(venue);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("Data is cached for this venue until refreshed or changed.");

        var vipDataReady = snapshot.View is not null;
        DrawArrivalToolbar(venue, vipDataReady);
        DrawNewVipOffer(venue);
        DrawVipTimedMacro(venue);

        if (!vipDataReady)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            DrawArrivalAdministration(venue);
            return;
        }

        var view = snapshot.View!;
        if (view.Capabilities.CanSell)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"My unpaid VIP sales: {view.PersonalUnpaidGil:N0} gil");
            if (view.PersonalPendingSettlementGil > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"Pending: {view.PersonalPendingSettlementGil:N0} gil");
            }
        }

        ImGui.Spacing();
        DrawTargetSection(venue, view, isBusy);
        DrawCurrentTargetPerks(venue, isBusy);
        DrawSettlementControls(venue, view, isBusy);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawVipPlayerList(venue, view, isBusy);

        if (view.Capabilities.CanManagePackages)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawPackageManagement(venue, view, isBusy);
        }

        DrawArrivalAdministration(venue);
        OpenQueuedPerkPopups();
        DrawRedeemPerkPopup(venue);
        DrawUndoPerkPopup(venue);

    }

    private void DrawVipTimedMacro(VenueConnectionConfiguration venue)
    {
        var snapshot = plugin.TimedMacros.GetSnapshot(venue);
        var view = snapshot.View;
        var macro = view?.Macros.FirstOrDefault(value =>
            string.Equals(value.TypeCode, TimedMacroTypeCodes.VipAdvertisement, StringComparison.OrdinalIgnoreCase) &&
            value.CanExecute);
        if (view is null || macro is null)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("VIP advertisement");

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

        if (string.Equals(stateText, "Due now", StringComparison.Ordinal))
        {
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.25f, 1f), stateText);
            ImGui.SameLine();
            ImGui.TextDisabled($"· every {macro.IntervalMinutes} minutes · shared across users");
        }
        else
        {
            ImGui.TextDisabled($"{stateText} · every {macro.IntervalMinutes} minutes · shared across users");
        }
        ImGui.SameLine();
        var canExecute =
            opening is not null &&
            atAddress &&
            macro.Enabled &&
            macro.IsConfigured;
        ImGui.BeginDisabled(
            plugin.TimedMacros.IsBusy(venue.ProfileId) ||
            plugin.IsGameMacroBusy ||
            !canExecute);
        if (ImGui.SmallButton("Execute VIP ad"))
            plugin.RunTimedMacro(venue, macro, opening!);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canExecute)
        {
            var reason = opening is null
                ? "There is no active opening."
                : !atAddress
                    ? locationMessage
                    : !macro.Enabled
                        ? "The VIP advertisement macro is disabled."
                        : "The VIP advertisement macro has not been configured.";
            ImGui.SetTooltip(reason);
        }
        else if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Running early is allowed and resets the shared timer.");
        }
    }

    private static string FormatTimedMacroRemaining(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        return $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void DrawArrivalToolbar(VenueConnectionConfiguration venue, bool vipDataReady)
    {
        var snapshot = plugin.VipArrivals.GetSnapshot(venue);
        if (snapshot.Context is null)
        {
            return;
        }

        var context = snapshot.Context;
        if (context.Capabilities.CanUseArrival)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(!vipDataReady);
            if (ImGui.Button("Open arrival tracker"))
            {
                plugin.OpenVipArrivalTracker(venue);
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !vipDataReady)
            {
                ImGui.SetTooltip("VIP player data permission is also required to run the arrival tracker.");
            }

            if (context.CurrentOpening is { } opening)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"Opening #{opening.OpeningId} until {VenueTimeZone.Format(venue, opening.ClosesAt, "g")}");
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextDisabled("No active opening");
            }
        }
    }

    private void DrawNewVipOffer(VenueConnectionConfiguration venue)
    {
        if (!plugin.VipArrivals.TryGetNewMemberOffer(venue.ProfileId, out var offer) || offer is null)
        {
            return;
        }

        var context = plugin.VipArrivals.GetSnapshot(venue).Context;
        var macro = context?.Macros.FirstOrDefault(value =>
            string.Equals(value.MacroCode, VipArrivalMacroCodes.NewMember, StringComparison.OrdinalIgnoreCase));

        ImGui.Spacing();
        ImGui.TextColored(
            new Vector4(0.45f, 0.9f, 0.55f, 1f),
            $"New or returning VIP: {offer.CharacterName} @ {offer.WorldName}");
        ImGui.SameLine();
        ImGui.BeginDisabled(
            macro?.IsConfigured != true ||
            plugin.VipArrivals.IsBusy(venue.ProfileId) ||
            plugin.IsGameMacroBusy);
        if (ImGui.SmallButton("Send new VIP message"))
        {
            plugin.RunNewVipMacro(venue, offer, macro!);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && macro?.IsConfigured != true)
        {
            ImGui.SetTooltip("Configure the New VIP message macro first.");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Dismiss##NewVipOffer"))
        {
            plugin.DismissNewVipOffer(venue.ProfileId);
        }
    }

    private void DrawArrivalAdministration(VenueConnectionConfiguration venue)
    {
        var snapshot = plugin.VipArrivals.GetSnapshot(venue);
        if (snapshot.Context is null)
        {
            return;
        }

        var context = snapshot.Context;
        if (!context.Capabilities.CanManageMacros && !context.Capabilities.CanManageOpenings)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("VIP arrival setup"))
        {
            return;
        }

        var isBusy = plugin.VipArrivals.IsBusy(venue.ProfileId);
        if (context.Capabilities.CanManageOpenings)
        {
            ImGui.TextUnformatted("Current venue opening");
            if (context.CurrentOpening is { } opening)
            {
                ImGui.TextWrapped(
                    $"#{opening.OpeningId}: {VenueTimeZone.Format(venue, opening.OpensAt, "g")} – {VenueTimeZone.Format(venue, opening.ClosesAt, "g")} at {opening.AddressDisplay}");
                ImGui.BeginDisabled(isBusy);
                if (ImGui.Button("Close current opening"))
                {
                    plugin.CloseVenueOpening(venue, opening.OpeningId);
                }
                ImGui.EndDisabled();
            }
            else
            {
                ImGui.TextDisabled(
                    "Temporary placeholder: starts now at the venue's published location. The future calendar will create the same opening records.");
                ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
                ImGui.InputInt("Duration (minutes)", ref temporaryOpeningDurationMinutes);
                temporaryOpeningDurationMinutes = Math.Clamp(temporaryOpeningDurationMinutes, 30, 1440);
                ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
                ImGui.InputText("Opening title (optional)", ref temporaryOpeningTitle, 100);
                ImGui.BeginDisabled(isBusy);
                if (ImGui.Button("Start temporary opening"))
                {
                    plugin.StartTemporaryVenueOpening(
                        venue,
                        temporaryOpeningDurationMinutes,
                        string.IsNullOrWhiteSpace(temporaryOpeningTitle)
                            ? null
                            : temporaryOpeningTitle.Trim());
                }
                ImGui.EndDisabled();
            }
        }

        if (!context.Capabilities.CanManageMacros)
        {
            return;
        }

        if (context.Capabilities.CanManageOpenings)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        ImGui.TextUnformatted("VIP macros");
        ImGui.TextDisabled(
            "Each line is copied into a temporary in-game macro. Game macro syntax such as <wait.1> is supported.");

        foreach (var macro in context.Macros.Where(value => value.CanManage))
        {
            if (!arrivalMacroDrafts.TryGetValue(macro.MacroCode, out var draft))
            {
                draft = macro.MacroText ?? string.Empty;
                arrivalMacroDrafts[macro.MacroCode] = draft;
            }

            ImGui.PushID(macro.MacroCode);
            ImGui.Spacing();
            ImGui.TextUnformatted(macro.DisplayName);
            if (!string.IsNullOrWhiteSpace(macro.Description))
            {
                ImGui.TextDisabled(macro.Description);
            }

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextMultiline(
                "##MacroText",
                ref draft,
                4000,
                new Vector2(0, 105 * ImGuiHelpers.GlobalScale));
            arrivalMacroDrafts[macro.MacroCode] = draft;

            var normalizedLines = draft
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
            var lineCount = normalizedLines.Length == 1 && normalizedLines[0].Length == 0
                ? 0
                : normalizedLines.Length;
            var longestLine = normalizedLines.Length == 0 ? 0 : normalizedLines.Max(value => value.Length);
            var valid = lineCount <= macro.MaxLines && longestLine <= macro.MaxLineLength;
            ImGui.TextDisabled(
                $"{lineCount}/{macro.MaxLines} lines; longest line {longestLine}/{macro.MaxLineLength} characters");

            ImGui.BeginDisabled(isBusy || !valid);
            if (ImGui.SmallButton("Save"))
            {
                plugin.UpdateVenueMacro(
                    venue,
                    macro.MacroCode,
                    string.IsNullOrWhiteSpace(draft) ? null : draft);
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(isBusy);
            if (ImGui.SmallButton("Clear"))
            {
                arrivalMacroDrafts[macro.MacroCode] = string.Empty;
                plugin.UpdateVenueMacro(venue, macro.MacroCode, null);
            }
            ImGui.EndDisabled();

            if (!valid)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Macro exceeds the configured game limits.");
            }
            ImGui.PopID();
        }
    }

    private void DrawTargetSection(
        VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("Targeted player");

        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetReason))
        {
            lastTargetKey = string.Empty;
            ImGui.TextDisabled(targetReason);
            return;
        }

        var targetKey = $"{target!.CharacterName}\n{target.WorldName}";
        var targetCharacter = view.Characters.FirstOrDefault(character =>
            string.Equals(character.CharacterName, target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(character.WorldName, target.WorldName, StringComparison.OrdinalIgnoreCase));
        var targetPlayer = targetCharacter is null
            ? null
            : view.Players.FirstOrDefault(player => player.VipPlayerId == targetCharacter.VipPlayerId);

        if (!string.Equals(lastTargetKey, targetKey, StringComparison.Ordinal))
        {
            lastTargetKey = targetKey;
            selectedExistingVipPlayerId = targetPlayer?.VipPlayerId ?? 0;
            saleDiscordUsername = targetPlayer?.DiscordUsername ?? string.Empty;
            customerPaymentConfirmed = false;
        }

        ImGui.TextUnformatted(target.DisplayName);

        if (targetPlayer is not null && targetCharacter is not null)
        {
            DrawRegisteredTarget(venue, view, targetPlayer, targetCharacter, isBusy);
        }
        else
        {
            DrawUnregisteredTarget(venue, view, target, isBusy);
        }
    }

    private void DrawRegisteredTarget(
        VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        VipPlayerSummary player,
        VipCharacterSummary targetCharacter,
        bool isBusy)
    {
        ImGui.TextUnformatted($"Discord: {player.DiscordDisplay}");
        ImGui.TextUnformatted($"List name: {player.CharacterDisplay}");

        var now = DateTimeOffset.UtcNow;
        var activeSubscription = view.Subscriptions
            .Where(subscription =>
                subscription.VipPlayerId == player.VipPlayerId &&
                !subscription.IsCancelled &&
                subscription.StartsAt <= now &&
                (subscription.Lifetime || subscription.EndsAt > now))
            .OrderByDescending(subscription => subscription.StartsAt)
            .ThenByDescending(subscription => subscription.SubscriptionId)
            .FirstOrDefault();
        if (activeSubscription is not null)
        {
            ImGui.TextColored(
                AvailableColor,
                $"VIP: {activeSubscription.PackageName}" +
                (activeSubscription.IsServerBoost
                    ? " (Server Booster)"
                    : activeSubscription.EndsAt is { } endsAt
                    ? $" until {VenueTimeZone.Format(venue, endsAt, "g")}"
                    : " (lifetime)"));
        }
        else
        {
            ImGui.TextColored(UnavailableColor, "No active VIP package.");
        }

        var linkedCharacters = view.Characters
            .Where(character => character.VipPlayerId == player.VipPlayerId)
            .OrderBy(character => character.CharacterId)
            .ToArray();

        ImGui.TextUnformatted("Linked characters:");
        foreach (var character in linkedCharacters)
        {
            ImGui.BulletText(character.IsPreferred
                ? $"{character.DisplayName} (preferred)"
                : character.DisplayName);
        }

        if (view.Capabilities.CanSell && !targetCharacter.IsPreferred)
        {
            ImGui.BeginDisabled(isBusy);
            if (ImGui.Button("Use target as list character"))
            {
                plugin.SetVipPreferredCharacter(
                    venue,
                    player.VipPlayerId,
                    targetCharacter.CharacterId);
            }
            ImGui.EndDisabled();
        }

        DrawSubscriptionHistory(view, player.VipPlayerId, venue);

        if (view.Capabilities.CanSell)
        {
            ImGui.Spacing();
            DrawSaleControls(venue, view, player.VipPlayerId, isBusy);
        }
    }

    private void DrawUnregisteredTarget(
        VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        PlayerIdentity target,
        bool isBusy)
    {
        ImGui.TextColored(UnavailableColor, "This character is not linked to a VIP player.");

        if (!view.Capabilities.CanSell)
        {
            return;
        }

        var selectedPlayer = view.Players.FirstOrDefault(
            player => player.VipPlayerId == selectedExistingVipPlayerId);
        var preview = selectedPlayer?.CharacterDisplay ?? "Create a new VIP player";

        ImGui.SetNextItemWidth(340 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Link target to", preview))
        {
            if (ImGui.Selectable("Create a new VIP player", selectedExistingVipPlayerId == 0))
            {
                selectedExistingVipPlayerId = 0;
                saleDiscordUsername = string.Empty;
            }

            foreach (var player in view.Players.OrderBy(player => player.DisplayCharacterName ?? player.DiscordUsername))
            {
                var selected = selectedExistingVipPlayerId == player.VipPlayerId;
                if (ImGui.Selectable(
                        $"{player.CharacterDisplay} — {player.DiscordDisplay}##VipPlayer{player.VipPlayerId}",
                        selected))
                {
                    selectedExistingVipPlayerId = player.VipPlayerId;
                    saleDiscordUsername = player.DiscordUsername ?? string.Empty;
                }
            }

            ImGui.EndCombo();
        }

        if (selectedExistingVipPlayerId > 0)
        {
            ImGui.BeginDisabled(isBusy);
            if (ImGui.Button("Link target without a sale"))
            {
                plugin.LinkVipCharacter(
                    venue,
                    selectedExistingVipPlayerId,
                    new LinkVipCharacterRequest(target.CharacterName, target.WorldName));
            }
            ImGui.EndDisabled();
        }

        ImGui.Spacing();
        DrawSaleControls(
            venue,
            view,
            selectedExistingVipPlayerId > 0 ? selectedExistingVipPlayerId : null,
            isBusy);
    }

    private void DrawSaleControls(
        VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        int? vipPlayerId,
        bool isBusy)
    {
        var activePackages = view.Packages
            .Where(package => !package.IsArchived)
            .OrderBy(package => package.PriceGil)
            .ThenBy(package => package.Name)
            .ThenBy(package => package.PackageId)
            .ToArray();

        if (activePackages.Length == 0)
        {
            ImGui.TextDisabled("No active VIP package is available.");
            return;
        }

        if (activePackages.All(package => package.PackageId != selectedPackageId))
        {
            selectedPackageId = activePackages[0].PackageId;
        }

        var selectedPackage = activePackages.First(package => package.PackageId == selectedPackageId);
        ImGui.TextUnformatted("Sell VIP subscription");

        ImGui.SetNextItemWidth(340 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo(
                "Package",
                $"{selectedPackage.Name} — {selectedPackage.PriceGil:N0} gil — {selectedPackage.DurationDisplay}"))
        {
            foreach (var package in activePackages)
            {
                var selected = package.PackageId == selectedPackageId;
                if (ImGui.Selectable(
                        $"{package.Name} — {package.PriceGil:N0} gil — {package.DurationDisplay}##VipPackage{package.PackageId}",
                        selected))
                {
                    selectedPackageId = package.PackageId;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Discord username", ref saleDiscordUsername, 100);
        ImGui.Checkbox("I confirm the targeted player paid the seller", ref customerPaymentConfirmed);

        var hasDiscord = !string.IsNullOrWhiteSpace(saleDiscordUsername);
        ImGui.BeginDisabled(isBusy || !hasDiscord || !customerPaymentConfirmed);
        if (ImGui.Button($"Record paid sale ({selectedPackage.PriceGil:N0} gil)"))
        {
            if (plugin.TargetProvider.TryGetCurrentTarget(out var target, out _))
            {
                plugin.SellVipSubscription(
                    venue,
                    new SellVipSubscriptionRequest(
                        target!.CharacterName,
                        target.WorldName,
                        selectedPackage.PackageId,
                        saleDiscordUsername.Trim(),
                        vipPlayerId,
                        true));
                customerPaymentConfirmed = false;
            }
        }
        ImGui.EndDisabled();

        ImGui.TextDisabled(
            "The sale is added to your unpaid-to-club total. Shift settlement will be implemented separately.");
    }

    private static void DrawSubscriptionHistory(VipManagementViewResponse view, int vipPlayerId, VenueConnectionConfiguration venue)
    {
        var subscriptions = view.Subscriptions
            .Where(subscription => subscription.VipPlayerId == vipPlayerId)
            .OrderByDescending(subscription => subscription.StartsAt)
            .ThenByDescending(subscription => subscription.SubscriptionId)
            .ToArray();

        ImGui.Spacing();
        ImGui.TextUnformatted("Purchase history");

        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("VipTargetHistory", 6, flags))
        {
            return;
        }

        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Package");
        ImGui.TableSetupColumn("Purchased");
        ImGui.TableSetupColumn("Starts");
        ImGui.TableSetupColumn("Ends");
        ImGui.TableSetupColumn("Seller");
        ImGui.TableHeadersRow();

        foreach (var subscription in subscriptions)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(GetSubscriptionStatus(subscription));
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{subscription.PackageName} ({subscription.PurchasePriceGil:N0} gil)");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, subscription.PurchasedAt, "g"));
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, subscription.StartsAt, "g"));
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(subscription.IsServerBoost
                ? "While boosting"
                : subscription.Lifetime
                    ? "Lifetime"
                : VenueTimeZone.Format(venue, subscription.EndsAt!.Value, "g"));
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(subscription.SellerDisplayName);
            if (subscription.PaidToVenueAt is null)
            {
                ImGui.TextDisabled("Not settled");
            }
        }

        ImGui.EndTable();
    }

    private void DrawVipPlayerList(
        VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("VIP player list");

        plugin.NearbyVipPlayers.Prepare(venue.ProfileId, view);
        plugin.NearbyVipPlayers.ScanIfDue();

        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Name filter", ref vipPlayerNameFilter, 100);

        ImGui.Checkbox("Active only", ref vipPlayerActiveOnly);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Shows lifetime VIPs and VIPs whose expiry is later than the current UTC time.");
        }

        ImGui.SameLine();
        ImGui.Checkbox("Expires within 8 days", ref vipPlayerExpiringSoonOnly);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Shows active, non-lifetime VIPs expiring in less than eight days.");
        }

        ImGui.SameLine();
        ImGui.Checkbox("Nearby only", ref vipPlayerNearbyOnly);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Shows VIP players with at least one linked character currently detected nearby.");
        }

        ImGui.Checkbox("No character linked", ref vipPlayerNoCharacterOnly);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Shows VIP players that do not have any FFXIV character linked.");
        }

        ImGui.SameLine();
        ImGui.Checkbox("Server Booster", ref vipPlayerServerBoosterOnly);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Shows VIP players with an active Discord Server Booster subscription.");
        }

        var now = DateTimeOffset.UtcNow;
        var expiringSoonCutoff = now.AddDays(8);
        var trimmedNameFilter = vipPlayerNameFilter.Trim();
        var charactersByPlayer = view.Characters.ToLookup(character => character.VipPlayerId);
        var filteredPlayers = view.Players
            .Where(player => MatchesVipPlayerNameFilter(
                player,
                charactersByPlayer[player.VipPlayerId],
                trimmedNameFilter))
            .Where(player => !vipPlayerActiveOnly || IsVipPlayerActive(player, now))
            .Where(player =>
                !vipPlayerExpiringSoonOnly ||
                IsVipPlayerExpiringSoon(player, now, expiringSoonCutoff))
            .Where(player =>
                !vipPlayerNearbyOnly ||
                plugin.NearbyVipPlayers.IsNearby(player.VipPlayerId))
            .Where(player =>
                !vipPlayerNoCharacterOnly ||
                !charactersByPlayer[player.VipPlayerId].Any())
            .Where(player =>
                !vipPlayerServerBoosterOnly ||
                player.HasServerBoostSubscription)
            .OrderBy(player => player.DisplayCharacterName ?? player.DiscordUsername)
            .ThenBy(player => player.DisplayWorldName)
            .ToArray();

        ImGui.TextDisabled(
            $"Showing {filteredPlayers.Length:N0} of {view.Players.Count:N0} — " +
            $"Nearby: {plugin.NearbyVipPlayers.NearbyVipPlayerCount:N0}");

        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.ScrollX |
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable(
                "VipPlayers",
                5,
                flags,
                new Vector2(0, 230 * ImGuiHelpers.GlobalScale)))
        {
            return;
        }

        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Discord", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Expires at", ImGuiTableColumnFlags.WidthFixed, 135 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Characters", ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var player in filteredPlayers)
        {
            var characterCount = charactersByPlayer[player.VipPlayerId].Count();
            var expiryColor = GetVipPlayerExpiryColor(player, now, expiringSoonCutoff);
            ImGui.TableNextRow();
            if (expiryColor is { } rowTextColor)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, rowTextColor);
            }

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(player.CharacterDisplay);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(player.DiscordDisplay);
            if (player.DiscordId is not null)
            {
                ImGui.TextDisabled("Discord linked");
            }
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(player.HasLifetime
                ? "Lifetime"
                : player.HasServerBoostSubscription
                    ? "Server Booster"
                : player.LastSubscriptionEndsAt is { } lastSubscriptionEnd ? VenueTimeZone.Format(venue, lastSubscriptionEnd, "g") : "None");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(characterCount.ToString(CultureInfo.InvariantCulture));

            if (expiryColor is not null)
            {
                ImGui.PopStyleColor();
            }

            ImGui.TableSetColumnIndex(4);
            ImGui.PushID(player.VipPlayerId);
            var hasNearbyCharacter = plugin.NearbyVipPlayers.TryGetNearbyCharacter(
                player.VipPlayerId,
                out var nearbyCharacter);
            if (hasNearbyCharacter)
            {
                if (ImGui.SmallButton("Target"))
                {
                    if (!plugin.NearbyVipPlayers.TryTarget(player.VipPlayerId, out var targetError))
                    {
                        Plugin.ChatGui.PrintError(targetError, "PartyPulse");
                    }
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Target {nearbyCharacter!.DisplayName}");
                }
            }

            if (view.Capabilities.CanManagePlayers || view.Capabilities.CanManagePayments)
            {
                if (hasNearbyCharacter)
                {
                    ImGui.SameLine();
                }

                ImGui.BeginDisabled(isBusy);
                if (ImGui.SmallButton("Edit"))
                {
                    plugin.OpenVipPlayerEditor(venue, player.VipPlayerId);
                }
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static bool MatchesVipPlayerNameFilter(
        VipPlayerSummary player,
        IEnumerable<VipCharacterSummary> characters,
        string nameFilter)
    {
        if (string.IsNullOrWhiteSpace(nameFilter))
        {
            return true;
        }

        if ((player.DisplayCharacterName?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (player.DisplayWorldName?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (player.DiscordUsername?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        return characters.Any(character =>
            character.CharacterName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
            character.WorldName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVipPlayerActive(VipPlayerSummary player, DateTimeOffset now) =>
        player.HasLifetime || player.HasServerBoostSubscription || player.LastSubscriptionEndsAt > now;

    private static bool IsVipPlayerExpiringSoon(
        VipPlayerSummary player,
        DateTimeOffset now,
        DateTimeOffset expiringSoonCutoff) =>
        !player.HasLifetime &&
        !player.HasServerBoostSubscription &&
        player.LastSubscriptionEndsAt is { } expiresAt &&
        expiresAt > now &&
        expiresAt < expiringSoonCutoff;

    private static Vector4? GetVipPlayerExpiryColor(
        VipPlayerSummary player,
        DateTimeOffset now,
        DateTimeOffset expiringSoonCutoff)
    {
        if (player.HasLifetime || player.HasServerBoostSubscription || player.LastSubscriptionEndsAt is not { } expiresAt)
        {
            return null;
        }

        if (expiresAt <= now)
        {
            return new Vector4(1f, 0.35f, 0.35f, 1f);
        }

        return expiresAt < expiringSoonCutoff
            ? new Vector4(1f, 0.82f, 0.2f, 1f)
            : null;
    }

    private void DrawSettlementControls(
        VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        bool isBusy)
    {
        if (!view.Capabilities.CanSell || view.PersonalAvailableSettlementGil <= 0)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(
            $"Available to settle: {view.PersonalAvailableSettlementGil:N0} gil");

        var hasTarget = plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetReason);
        ImGui.BeginDisabled(isBusy || !hasTarget);
        if (ImGui.Button("Settle payment"))
        {
            settlementTargetName = target!.CharacterName;
            settlementTargetWorld = target.WorldName;
            ImGui.OpenPopup("Initiate VIP settlement###PartyPulseVipSettlement");
        }
        ImGui.EndDisabled();
        if (!hasTarget)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(targetReason);
        }

        if (!ImGui.BeginPopupModal(
                "Initiate VIP settlement###PartyPulseVipSettlement",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Initiate a trade with {settlementTargetName} @ {settlementTargetWorld} for " +
            $"{view.PersonalAvailableSettlementGil:N0} gil?");
        ImGui.TextWrapped(
            "The targeted character must belong to an active venue user with finance.settlements.manage or venue.owner.");
        ImGui.TextColored(
            new Vector4(1f, 0.65f, 0.25f, 1f),
            "Confirming checks Dropbox, creates a pending server transaction, focuses the target, opens Dropbox, and starts the trade queue. The collector must still confirm that payment was received.");

        if (ImGui.Button("Create settlement and start trade"))
        {
            plugin.CreateVipSettlement(
                venue,
                new CreateVipSettlementRequest(settlementTargetName, settlementTargetWorld));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawPackageManagement(
        VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        bool isBusy)
    {
        if (!ImGui.CollapsingHeader("VIP package definitions"))
        {
            return;
        }

        if (ImGui.Button("New package"))
        {
            ResetPackageEditor();
        }

        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("VipPackages", 6, flags))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Price");
            ImGui.TableSetupColumn("Duration");
            ImGui.TableSetupColumn("Server Boost");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("##Edit", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var package in view.Packages
                         .OrderBy(package => package.PriceGil)
                         .ThenBy(package => package.Name)
                         .ThenBy(package => package.PackageId))
            {
                ImGui.PushID(package.PackageId);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(package.Name);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted($"{package.PriceGil:N0} gil");
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(package.DurationDisplay);
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(package.GrantedByServerBoost ? "Granted" : "No");
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(package.IsArchived ? "Archived" : "Active");
                ImGui.TableSetColumnIndex(5);
                if (ImGui.SmallButton("Edit"))
                {
                    LoadPackageEditor(package);
                }
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(editingPackageId == 0 ? "Create package" : $"Edit package #{editingPackageId}");
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Package name", ref packageName, 50);
        ImGui.InputInt("Price (gil)", ref packagePriceGil);
        ImGui.Checkbox("Lifetime", ref packageLifetime);

        ImGui.BeginDisabled(packageLifetime);
        ImGui.InputInt("Days", ref packageDays);
        ImGui.InputInt("Months", ref packageMonths);
        ImGui.InputInt("Years", ref packageYears);
        ImGui.EndDisabled();

        DrawDiscordRoleCombo(view, venue, isBusy);
        ImGui.Checkbox("Grant while Discord Server Boosting", ref packageGrantedByServerBoost);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Only one active package per venue can be granted by Server Boosting.");
        }
        if (editingPackageId > 0)
        {
            ImGui.Checkbox("Archived", ref packageArchived);
        }

        var validDuration = packageLifetime || packageDays > 0 || packageMonths > 0 || packageYears > 0;
        var validRole = packageDiscordRoleId == 0 ||
                        view.DiscordRoles.Any(role =>
                            role.RoleId == packageDiscordRoleId &&
                            role.CanAssign);
        var valid = !string.IsNullOrWhiteSpace(packageName) &&
                    packagePriceGil >= 0 &&
                    packageDays >= 0 &&
                    packageMonths >= 0 &&
                    packageYears >= 0 &&
                    validDuration &&
                    validRole;

        ImGui.BeginDisabled(isBusy || !valid);
        if (ImGui.Button(editingPackageId == 0 ? "Create package" : "Save package"))
        {
            if (packageDiscordRoleId != loadedPackageDiscordRoleId)
            {
                openDiscordRoleManagementPopup = true;
            }
            else
            {
                SavePackage(venue, confirmDiscordRoleManagement: false);
            }
        }
        ImGui.EndDisabled();

        if (openDiscordRoleManagementPopup)
        {
            openDiscordRoleManagementPopup = false;
            ImGui.OpenPopup(DiscordRoleManagementPopupName);
        }

        DrawDiscordRoleManagementConfirmation(venue, view, isBusy);
    }

    private void DrawDiscordRoleManagementConfirmation(
        VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        bool isBusy)
    {
        if (!ImGui.BeginPopupModal(
                DiscordRoleManagementPopupName,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var previousRole = view.DiscordRoles.FirstOrDefault(role =>
            role.RoleId == loadedPackageDiscordRoleId);
        var selectedRole = view.DiscordRoles.FirstOrDefault(role =>
            role.RoleId == packageDiscordRoleId);
        var previousName = loadedPackageDiscordRoleId == 0
            ? "No Discord role"
            : previousRole is null
                ? "Unavailable role"
                : DiscordChannelDisplayName.ToAsciiLetters(previousRole.Name);
        var selectedName = packageDiscordRoleId == 0
            ? "No Discord role"
            : selectedRole is null
                ? "Unavailable role"
                : DiscordChannelDisplayName.ToAsciiLetters(selectedRole.Name);

        ImGui.TextWrapped($"Discord role: {previousName} -> {selectedName}");
        if (packageDiscordRoleId > 0)
        {
            ImGui.TextColored(
                PartyPulseUi.Warning,
                "PartyPulse will fully manage the selected role. On the next reconciliation, the bot removes it from every member without a matching active VIP subscription, including roles assigned manually in Discord.");
        }
        else
        {
            ImGui.TextColored(
                PartyPulseUi.Warning,
                "PartyPulse will remove assignments it previously managed for the old role, then stop treating that role as authoritative.");
        }
        ImGui.TextWrapped("Confirm only if this is the intended VIP role.");

        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Confirm role change and save"))
        {
            SavePackage(venue, confirmDiscordRoleManagement: true);
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

    private void SavePackage(
        VenueConnectionConfiguration venue,
        bool confirmDiscordRoleManagement)
    {
        long? discordRoleId = packageDiscordRoleId > 0
            ? packageDiscordRoleId
            : null;

        if (editingPackageId == 0)
        {
            plugin.CreateVipPackage(
                venue,
                new CreateVipPackageRequest(
                    packageName.Trim(),
                    packagePriceGil,
                    packageLifetime ? 0 : packageDays,
                    packageLifetime ? 0 : packageMonths,
                    packageLifetime ? 0 : packageYears,
                    packageLifetime,
                    discordRoleId,
                    packageGrantedByServerBoost,
                    confirmDiscordRoleManagement));
            return;
        }

        plugin.UpdateVipPackage(
            venue,
            editingPackageId,
            new UpdateVipPackageRequest(
                packageName.Trim(),
                packagePriceGil,
                packageLifetime ? 0 : packageDays,
                packageLifetime ? 0 : packageMonths,
                packageLifetime ? 0 : packageYears,
                packageLifetime,
                discordRoleId,
                packageArchived,
                packageGrantedByServerBoost,
                confirmDiscordRoleManagement));
    }
    private void DrawDiscordRoleCombo(VipManagementViewResponse view, VenueConnectionConfiguration venue, bool isBusy)
    {
        var assignableRoles = view.DiscordRoles
            .Where(role => role.CanAssign)
            .OrderByDescending(role => role.Position)
            .ThenBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedRole = view.DiscordRoles.FirstOrDefault(role => role.RoleId == packageDiscordRoleId);
        var preview = packageDiscordRoleId == 0
            ? "No Discord role"
            : selectedRole is not null
                ? DiscordChannelDisplayName.ToAsciiLetters(selectedRole.Name)
                : "Stored role no longer available";

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Discord role (optional)", preview))
        {
            if (ImGui.Selectable("No Discord role", packageDiscordRoleId == 0))
            {
                packageDiscordRoleId = 0;
            }

            foreach (var role in assignableRoles)
            {
                if (ImGui.Selectable(
                        $"{DiscordChannelDisplayName.ToAsciiLetters(role.Name)}##VipDiscordRole{role.RoleId}",
                        packageDiscordRoleId == role.RoleId))
                {
                    packageDiscordRoleId = role.RoleId;
                }
            }

            ImGui.EndCombo();
        }

        if (view.DiscordRoles.Count == 0)
        {
            ImGui.TextDisabled("No active Discord roles are stored for this venue.");
        }
        else if (assignableRoles.Length == 0)
        {
            ImGui.TextDisabled("No stored Discord role can currently be assigned by the bot.");
        }
        else if (packageDiscordRoleId > 0 && selectedRole?.CanAssign != true)
        {
            ImGui.TextColored(
                PartyPulseUi.Warning,
                "The saved role is no longer assignable. Select another role or clear it.");
        }

        ImGui.TextDisabled(
            "Configured VIP roles are fully managed by PartyPulse; manual assignments are removed during reconciliation.");
    

        var perkSnapshot = plugin.VipPerks.GetSnapshot(venue);
        if (perkSnapshot.Status == VipPerkManagementStatus.Ready &&
            perkSnapshot.View is { Capabilities.CanManage: true } perkView)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawPerkCatalog(venue, view, perkView, isBusy || plugin.VipPerks.IsBusy(venue.ProfileId));
        }
    }

    private static string GetSubscriptionStatus(VipSubscriptionSummary subscription)
    {
        if (subscription.IsCancelled)
        {
            return "Cancelled";
        }

        var now = DateTimeOffset.UtcNow;
        if (subscription.StartsAt > now)
        {
            return "Future";
        }

        if (subscription.Lifetime || subscription.EndsAt > now)
        {
            return "Active";
        }

        return "Expired";
    }

    private void DrawCurrentTargetPerks(
        VenueConnectionConfiguration venue,
        bool vipBusy)
    {
        var snapshot = plugin.VipPerks.GetSnapshot(venue);
        if (snapshot.Status != VipPerkManagementStatus.Ready || snapshot.View is null)
        {
            if (snapshot.Status is VipPerkManagementStatus.Denied or VipPerkManagementStatus.Failed)
            {
                ImGui.TextDisabled(snapshot.Message);
            }
            return;
        }

        DrawTargetPerks(
            venue,
            snapshot.View,
            vipBusy || plugin.VipPerks.IsBusy(venue.ProfileId));
    }

    private void DrawTargetPerks(VenueConnectionConfiguration venue, VipPerkManagementViewResponse view, bool busy)
    {
        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out _)) return;
        var rows = view.Availability.Where(value =>
            string.Equals(value.CharacterName, target!.CharacterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.WorldName, target.WorldName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rows.Length == 0)
        {
            ImGui.TextColored(
                UnavailableColor,
                "No VIP perks are assigned to this target's current active package.");
            DrawTargetPerkHistory(venue, view, target!, busy);
            return;
        }

        ImGui.TextColored(
            AvailableColor,
            $"VIP perks for {target!.DisplayName} ({rows[0].PackageName})");
        foreach (var row in rows.OrderBy(value => value.PerkName))
        {
            ImGui.PushID(row.PackagePerkId);
            ImGui.TextUnformatted(row.PerkName);
            ImGui.SameLine();
            if (row.Available)
            {
                ImGui.TextColored(AvailableColor, "Available");
                if (view.Capabilities.CanRedeem)
                {
                    ImGui.SameLine();
                    ImGui.BeginDisabled(busy);
                    if (ImGui.SmallButton("Redeem"))
                    {
                        pendingRedeemCharacterName = target.CharacterName;
                        pendingRedeemWorldName = target.WorldName;
                        pendingRedeemPerkName = row.PerkName;
                        pendingRedeemPerkId = row.PerkId;
                        openRedeemPerkPopup = true;
                    }
                    ImGui.EndDisabled();
                }
            }
            else
            {
                var next = row.NextResetAt is { } reset
                    ? $"Next {VenueTimeZone.Format(venue, reset, "g")}" : "Used for this subscription";
                ImGui.TextColored(UnavailableColor, next);
            }
            ImGui.PopID();
        }

        DrawTargetPerkHistory(venue, view, target, busy);
    }

    private void DrawTargetPerkHistory(
        VenueConnectionConfiguration venue,
        VipPerkManagementViewResponse view,
        PlayerIdentity target,
        bool busy)
    {
        var history = view.Redemptions
            .Where(value =>
                string.Equals(value.TargetCharacterName, target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(value.TargetWorldName, target.WorldName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.RedeemedAt)
            .ThenByDescending(value => value.RedemptionId)
            .Take(50)
            .ToArray();

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader(
                $"Perk redemption history for {target.DisplayName}##PartyPulseTargetPerkHistory",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (history.Length == 0)
        {
            ImGui.TextDisabled("No recorded VIP perk redemptions for this character.");
            return;
        }

        foreach (var row in history)
        {
            ImGui.PushID((int)(row.RedemptionId % int.MaxValue));
            var source = string.Equals(row.SourceType, "photoshoot_sale", StringComparison.OrdinalIgnoreCase)
                ? "photoshoot purchase"
                : "manual redemption";
            var status = row.UndoneAt is null ? "spent" : "cancelled";
            ImGui.BulletText(
                $"#{row.RedemptionId} {row.PerkName} — {source} — {status} — " +
                $"by {row.RedeemedByDisplayName} — {VenueTimeZone.Format(venue, row.RedeemedAt, "g")}");
            if (row.UndoneAt is not null && !string.IsNullOrWhiteSpace(row.UndoReason))
            {
                ImGui.Indent();
                ImGui.TextDisabled($"Cancellation reason: {row.UndoReason}");
                ImGui.Unindent();
            }

            if (row.UndoneAt is null && view.Capabilities.CanUndo)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Cancel redemption"))
                {
                    pendingUndoRedemptionId = row.RedemptionId;
                    undoReason = string.Empty;
                    openUndoPerkPopup = true;
                }
                ImGui.EndDisabled();
            }
            ImGui.PopID();
        }
    }

    private void DrawPerkCatalog(
        VenueConnectionConfiguration venue,
        VipManagementViewResponse vipView,
        VipPerkManagementViewResponse view,
        bool busy)
    {
        ImGui.TextUnformatted("Perk definitions");
        if (ImGui.SmallButton("New perk")) { editingPerkId = 0; perkName = string.Empty; perkArchived = false; }
        foreach (var perk in view.Perks.OrderBy(value => value.ArchivedAt is not null).ThenBy(value => value.Name))
        {
            ImGui.PushID(perk.PerkId);
            ImGui.BulletText(perk.Name + (perk.ArchivedAt is null ? string.Empty : " (archived)"));
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit")) { editingPerkId = perk.PerkId; perkName = perk.Name; perkArchived = perk.ArchivedAt is not null; }
            ImGui.PopID();
        }
        ImGui.InputText("Perk name", ref perkName, 100);
        if (editingPerkId > 0) ImGui.Checkbox("Perk archived", ref perkArchived);
        ImGui.BeginDisabled(busy || string.IsNullOrWhiteSpace(perkName));
        if (ImGui.Button(editingPerkId == 0 ? "Create perk" : "Save perk"))
        {
            if (editingPerkId == 0) plugin.CreateVipPerk(venue, new CreateVipPerkRequest(perkName.Trim()));
            else plugin.UpdateVipPerk(venue, editingPerkId, new UpdateVipPerkRequest(perkName.Trim(), perkArchived));
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextUnformatted("Assign perk to package");
        if (ImGui.SmallButton("New assignment"))
        {
            assignmentPackageId = 0;
            assignmentPerkId = 0;
            loadedAssignmentPackageId = 0;
            loadedAssignmentPerkId = 0;
            renewalMode = 0;
            renewalInterval = 1;
        }

        var activePackages = vipView.Packages
            .Where(value => !value.IsArchived)
            .OrderBy(value => value.Lifetime ? 1 : 0)
            .ThenBy(value => value.YearsGranted)
            .ThenBy(value => value.MonthsGranted)
            .ThenBy(value => value.DaysGranted)
            .ThenBy(value => value.PriceGil)
            .ThenBy(value => value.Name)
            .ToArray();
        var activePerks = view.Perks.Where(value => value.ArchivedAt is null).OrderBy(value => value.Name).ToArray();
        var packagePreview = activePackages.FirstOrDefault(value => value.PackageId == assignmentPackageId)?.Name ?? "Select package";
        if (ImGui.BeginCombo("VIP package", packagePreview))
        {
            foreach (var package in activePackages)
                if (ImGui.Selectable(package.Name, assignmentPackageId == package.PackageId)) assignmentPackageId = package.PackageId;
            ImGui.EndCombo();
        }
        var perkPreview = activePerks.FirstOrDefault(value => value.PerkId == assignmentPerkId)?.Name ?? "Select perk";
        if (ImGui.BeginCombo("VIP perk", perkPreview))
        {
            foreach (var perk in activePerks)
                if (ImGui.Selectable(perk.Name, assignmentPerkId == perk.PerkId)) assignmentPerkId = perk.PerkId;
            ImGui.EndCombo();
        }
        var existing = view.PackageAssignments.FirstOrDefault(value =>
            value.PackageId == assignmentPackageId &&
            value.PerkId == assignmentPerkId &&
            value.ArchivedAt is null);
        SyncAssignmentEditor(existing);

        var modes = new[] { "One time per subscription", "Every X days", "Every X weeks", "Every X months" };
        renewalMode = Math.Clamp(renewalMode, 0, modes.Length - 1);
        if (ImGui.BeginCombo("Renewal", modes[renewalMode]))
        {
            for (var index = 0; index < modes.Length; index++)
            {
                if (ImGui.Selectable(modes[index], renewalMode == index)) renewalMode = index;
            }
            ImGui.EndCombo();
        }
        if (renewalMode > 0) ImGui.InputInt("Renew every", ref renewalInterval);
        renewalInterval = Math.Max(1, renewalInterval);
        ImGui.BeginDisabled(busy || assignmentPackageId <= 0 || assignmentPerkId <= 0);
        if (ImGui.Button(existing is null ? "Assign perk" : "Update assignment"))
        {
            var unit = renewalMode switch { 1 => "day", 2 => "week", 3 => "month", _ => null };
            plugin.SetVipPackagePerk(venue, assignmentPackageId, assignmentPerkId,
                new SetVipPackagePerkRequest(true, unit, unit is null ? null : renewalInterval));
        }
        if (existing is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove assignment"))
                plugin.SetVipPackagePerk(venue, assignmentPackageId, assignmentPerkId, new SetVipPackagePerkRequest(false, null, null));
        }
        ImGui.EndDisabled();

        var packageById = vipView.Packages.ToDictionary(value => value.PackageId);
        var assignments = view.PackageAssignments
            .Where(value => value.ArchivedAt is null)
            .OrderBy(value => value.PerkName)
            .ThenBy(value => packageById.TryGetValue(value.PackageId, out var package) && package.Lifetime ? 1 : 0)
            .ThenBy(value => packageById.TryGetValue(value.PackageId, out var package) ? package.YearsGranted : int.MaxValue)
            .ThenBy(value => packageById.TryGetValue(value.PackageId, out var package) ? package.MonthsGranted : int.MaxValue)
            .ThenBy(value => packageById.TryGetValue(value.PackageId, out var package) ? package.DaysGranted : int.MaxValue)
            .ThenBy(value => packageById.TryGetValue(value.PackageId, out var package) ? package.PriceGil : int.MaxValue)
            .ThenBy(value => value.PackageName)
            .ThenBy(value => value.PackagePerkId)
            .ToArray();
        if (assignments.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Current assignments, ordered by perk and then package duration:");
            var assignmentFlags =
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.SizingStretchProp;
            if (ImGui.BeginTable("VipPerkAssignments", 4, assignmentFlags))
            {
                ImGui.TableSetupColumn("Perk");
                ImGui.TableSetupColumn("VIP package");
                ImGui.TableSetupColumn("Renewal");
                ImGui.TableSetupColumn("##Edit", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();

                foreach (var assignment in assignments)
                {
                    var renewal = assignment.RenewalUnit is null
                        ? "One time per subscription"
                        : $"Every {assignment.RenewalInterval} {assignment.RenewalUnit}(s)";
                    ImGui.PushID(assignment.PackagePerkId);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(assignment.PerkName);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(assignment.PackageName);
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(renewal);
                    ImGui.TableSetColumnIndex(3);
                    if (ImGui.SmallButton("Edit"))
                    {
                        LoadAssignmentEditor(assignment);
                    }
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        }
    }


    private void LoadAssignmentEditor(VipPackagePerkSummary assignment)
    {
        assignmentPackageId = assignment.PackageId;
        assignmentPerkId = assignment.PerkId;
        loadedAssignmentPackageId = 0;
        loadedAssignmentPerkId = 0;
        SyncAssignmentEditor(assignment);
    }

    private void SyncAssignmentEditor(VipPackagePerkSummary? assignment)
    {
        if (loadedAssignmentPackageId == assignmentPackageId &&
            loadedAssignmentPerkId == assignmentPerkId)
        {
            return;
        }

        loadedAssignmentPackageId = assignmentPackageId;
        loadedAssignmentPerkId = assignmentPerkId;
        renewalInterval = Math.Max(1, assignment?.RenewalInterval ?? 1);
        renewalMode = assignment?.RenewalUnit switch
        {
            "day" => 1,
            "week" => 2,
            "month" => 3,
            _ => 0
        };
    }

    private void DrawRedeemPerkPopup(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(
                RedeemPerkPopupName,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Spend {pendingRedeemPerkName} for {pendingRedeemCharacterName} @ {pendingRedeemWorldName}?");
        ImGui.TextColored(
            new Vector4(1f, 0.65f, 0.25f, 1f),
            "This immediately consumes the perk for its current subscription renewal period. This manual redemption will not create a photoshoot sale.");
        ImGui.TextWrapped(
            "The server will verify the active subscription, package assignment, and current renewal period before recording it.");

        if (ImGui.Button("Confirm redemption"))
        {
            plugin.RedeemVipPerk(
                venue,
                new RedeemVipPerkRequest(
                    pendingRedeemCharacterName,
                    pendingRedeemWorldName,
                    pendingRedeemPerkId,
                    null));
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawUndoPerkPopup(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(UndoPerkPopupName, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped($"Undo VIP perk redemption #{pendingUndoRedemptionId}? This restores availability for its original renewal period and remains in the audit log.");
        ImGui.InputText("Reason (optional)", ref undoReason, 255);
        if (ImGui.Button("Undo redemption"))
        {
            plugin.UndoVipPerkRedemption(venue, pendingUndoRedemptionId, string.IsNullOrWhiteSpace(undoReason) ? null : undoReason.Trim());
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void OpenQueuedPerkPopups()
    {
        // Redeem and undo buttons are rendered under per-row PushID scopes.
        // Open their modals here at the same root ID scope used by BeginPopupModal.
        if (openRedeemPerkPopup)
        {
            ImGui.OpenPopup(RedeemPerkPopupName);
            openRedeemPerkPopup = false;
        }

        if (openUndoPerkPopup)
        {
            ImGui.OpenPopup(UndoPerkPopupName);
            openUndoPerkPopup = false;
        }
    }

    private void ResetForVenueChange(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
        {
            return;
        }

        activeProfileId = venue.ProfileId;
        lastTargetKey = string.Empty;
        selectedPackageId = 0;
        selectedExistingVipPlayerId = 0;
        saleDiscordUsername = string.Empty;
        customerPaymentConfirmed = false;
        vipPlayerNameFilter = string.Empty;
        vipPlayerActiveOnly = false;
        vipPlayerExpiringSoonOnly = false;
        vipPlayerNearbyOnly = false;
        vipPlayerNoCharacterOnly = false;
        vipPlayerServerBoosterOnly = false;
        arrivalMacroDrafts.Clear();
        temporaryOpeningDurationMinutes = 480;
        temporaryOpeningTitle = string.Empty;
        plugin.NearbyVipPlayers.Clear();
        plugin.VipArrivalNearby.Clear();
        openRedeemPerkPopup = false;
        openUndoPerkPopup = false;
        ResetPackageEditor();
        ResetVipPerkEditor();
    }

    private void ResetVipPerkEditor()
    {
        editingPerkId = 0;
        perkName = string.Empty;
        perkArchived = false;
        assignmentPackageId = 0;
        assignmentPerkId = 0;
        renewalMode = 0;
        renewalInterval = 1;
        loadedAssignmentPackageId = 0;
        loadedAssignmentPerkId = 0;
        pendingRedeemCharacterName = string.Empty;
        pendingRedeemWorldName = string.Empty;
        pendingRedeemPerkName = string.Empty;
        pendingRedeemPerkId = 0;
        pendingUndoRedemptionId = 0;
        undoReason = string.Empty;
    }

    private void ResetPackageEditor()
    {
        editingPackageId = 0;
        packageName = string.Empty;
        packagePriceGil = 0;
        packageDays = 30;
        packageMonths = 0;
        packageYears = 0;
        packageLifetime = false;
        packageDiscordRoleId = 0;
        loadedPackageDiscordRoleId = 0;
        packageGrantedByServerBoost = false;
        packageArchived = false;
    }

    private void LoadPackageEditor(VipPackageSummary package)
    {
        editingPackageId = package.PackageId;
        packageName = package.Name;
        packagePriceGil = package.PriceGil;
        packageDays = package.DaysGranted;
        packageMonths = package.MonthsGranted;
        packageYears = package.YearsGranted;
        packageLifetime = package.Lifetime;
        packageDiscordRoleId = package.DiscordRoleId ?? 0;
        loadedPackageDiscordRoleId = package.DiscordRoleId ?? 0;
        packageGrantedByServerBoost = package.GrantedByServerBoost;
        packageArchived = package.IsArchived;
    }
}
