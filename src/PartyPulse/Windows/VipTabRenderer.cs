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

namespace PartyPulse.Windows;

public sealed class VipTabRenderer(Plugin plugin)
{
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
    private string packageDiscordRoleId = string.Empty;
    private bool packageArchived;
    private string settlementTargetName = string.Empty;
    private string settlementTargetWorld = string.Empty;

    public void Draw(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("VIP"))
        {
            return;
        }

        ResetForVenueChange(venue);
        plugin.EnsureVipLoaded(venue);
        plugin.EnsureVipArrivalsLoaded(venue);
        plugin.EnsureTimedMacrosLoaded(venue);

        var snapshot = plugin.Vip.GetSnapshot(venue);
        var isBusy = plugin.Vip.IsBusy(venue.ProfileId);

        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh VIP data"))
        {
            plugin.RefreshVip(venue);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("Data is cached for this venue until refreshed or changed.");

        var vipDataReady = snapshot.Status == VipManagementStatus.Ready && snapshot.View is not null;
        DrawArrivalToolbar(venue, vipDataReady);
        DrawNewVipOffer(venue);
        DrawVipTimedMacro(venue);

        if (!vipDataReady)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            DrawArrivalAdministration(venue);
            ImGui.EndTabItem();
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

        ImGui.EndTabItem();
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
        var atAddress = opening is not null && plugin.LocationProvider.IsAtAddress(
            opening.AddressWorldName,
            opening.AddressCityName,
            opening.AddressWard,
            opening.AddressPlot,
            out locationMessage);
        var now = snapshot.EstimatedServerNow;
        var stateText = opening is null
            ? "Paused: no active opening"
            : !atAddress
                ? "Paused: not at opening address"
                : macro.NextDueAt is not { } dueAt || dueAt <= now
                    ? "Due now"
                    : $"Next in {FormatTimedMacroRemaining(dueAt - now)}";

        ImGui.TextDisabled($"{stateText} · every {macro.IntervalMinutes} minutes · shared across users");
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
                ImGui.TextDisabled($"Opening #{opening.OpeningId} until {opening.ClosesAt.ToLocalTime():g}");
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
                    $"#{opening.OpeningId}: {opening.OpensAt.ToLocalTime():g} – {opening.ClosesAt.ToLocalTime():g} at {opening.AddressDisplay}");
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
                    "Temporary placeholder: starts now at the venue's published address. The future calendar will create the same opening records.");
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

        DrawSubscriptionHistory(view, player.VipPlayerId);

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
        ImGui.TextDisabled("This character is not linked to a VIP player.");

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

            foreach (var player in view.Players.OrderBy(player => player.DisplayCharacterName))
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

    private static void DrawSubscriptionHistory(VipManagementViewResponse view, int vipPlayerId)
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
            ImGui.TextUnformatted(subscription.PurchasedAt.ToLocalTime().ToString("g"));
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(subscription.StartsAt.ToLocalTime().ToString("g"));
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(subscription.Lifetime
                ? "Lifetime"
                : subscription.EndsAt!.Value.ToLocalTime().ToString("g"));
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
            .OrderBy(player => player.DisplayCharacterName)
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
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable(
                "VipPlayers",
                5,
                flags,
                new Vector2(0, 230 * ImGuiHelpers.GlobalScale)))
        {
            return;
        }

        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("Discord");
        ImGui.TableSetupColumn("Expires at");
        ImGui.TableSetupColumn("Characters");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
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
                : player.LastSubscriptionEndsAt?.ToLocalTime().ToString("g") ?? "None");
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

        if (player.DisplayCharacterName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
            player.DisplayWorldName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return characters.Any(character =>
            character.CharacterName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
            character.WorldName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVipPlayerActive(VipPlayerSummary player, DateTimeOffset now) =>
        player.HasLifetime || player.LastSubscriptionEndsAt > now;

    private static bool IsVipPlayerExpiringSoon(
        VipPlayerSummary player,
        DateTimeOffset now,
        DateTimeOffset expiringSoonCutoff) =>
        !player.HasLifetime &&
        player.LastSubscriptionEndsAt is { } expiresAt &&
        expiresAt > now &&
        expiresAt < expiringSoonCutoff;

    private static Vector4? GetVipPlayerExpiryColor(
        VipPlayerSummary player,
        DateTimeOffset now,
        DateTimeOffset expiringSoonCutoff)
    {
        if (player.HasLifetime || player.LastSubscriptionEndsAt is not { } expiresAt)
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

        if (ImGui.BeginTable("VipPackages", 5, flags))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Price");
            ImGui.TableSetupColumn("Duration");
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
                ImGui.TextUnformatted(package.IsArchived ? "Archived" : "Active");
                ImGui.TableSetColumnIndex(4);
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

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Discord role ID (optional)", ref packageDiscordRoleId, 20);
        if (editingPackageId > 0)
        {
            ImGui.Checkbox("Archived", ref packageArchived);
        }

        var validDuration = packageLifetime || packageDays > 0 || packageMonths > 0 || packageYears > 0;
        var validRole = string.IsNullOrWhiteSpace(packageDiscordRoleId) ||
                        long.TryParse(packageDiscordRoleId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRoleId) &&
                        parsedRoleId > 0;
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
            long? discordRoleId = string.IsNullOrWhiteSpace(packageDiscordRoleId)
                ? null
                : long.Parse(
                    packageDiscordRoleId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture);

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
                        discordRoleId));
            }
            else
            {
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
                        packageArchived));
            }
        }
        ImGui.EndDisabled();
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
        arrivalMacroDrafts.Clear();
        temporaryOpeningDurationMinutes = 480;
        temporaryOpeningTitle = string.Empty;
        plugin.NearbyVipPlayers.Clear();
        plugin.VipArrivalNearby.Clear();
        ResetPackageEditor();
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
        packageDiscordRoleId = string.Empty;
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
        packageDiscordRoleId = package.DiscordRoleId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        packageArchived = package.IsArchived;
    }
}
