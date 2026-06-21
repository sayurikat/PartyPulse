using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Vip;

namespace PartyPulse.Windows;

public sealed class VipTabRenderer(Plugin plugin)
{
    private Guid activeProfileId;
    private string lastTargetKey = string.Empty;
    private int selectedPackageId;
    private int selectedExistingVipPlayerId;
    private string saleDiscordUsername = string.Empty;
    private bool customerPaymentConfirmed;

    private int editingPackageId;
    private string packageName = string.Empty;
    private int packagePriceGil;
    private int packageDays;
    private int packageMonths;
    private int packageYears;
    private bool packageLifetime;
    private string packageDiscordRoleId = string.Empty;
    private bool packageArchived;

    public void Draw(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("VIP"))
        {
            return;
        }

        ResetForVenueChange(venue);
        plugin.EnsureVipLoaded(venue);

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

        if (snapshot.Status != VipManagementStatus.Ready || snapshot.View is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        var view = snapshot.View;
        if (view.Capabilities.CanSell)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"My unpaid VIP sales: {view.PersonalUnpaidGil:N0} gil");
        }

        ImGui.Spacing();
        DrawTargetSection(venue, view, isBusy);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawVipPlayerList(view);

        if (view.Capabilities.CanManagePackages)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawPackageManagement(venue, view, isBusy);
        }

        ImGui.EndTabItem();
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
        ImGui.TextUnformatted($"Discord: {player.DiscordDisplay} (@{player.DiscordUsername})");
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
                        $"{player.CharacterDisplay} — @{player.DiscordUsername}##VipPlayer{player.VipPlayerId}",
                        selected))
                {
                    selectedExistingVipPlayerId = player.VipPlayerId;
                    saleDiscordUsername = player.DiscordUsername;
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
            .OrderBy(package => package.Name)
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

    private static void DrawVipPlayerList(VipManagementViewResponse view)
    {
        ImGui.TextUnformatted("VIP player list");

        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable(
                "VipPlayers",
                4,
                flags,
                new Vector2(0, 230 * ImGuiHelpers.GlobalScale)))
        {
            return;
        }

        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("Discord");
        ImGui.TableSetupColumn("Last subscription");
        ImGui.TableSetupColumn("Characters");
        ImGui.TableHeadersRow();

        foreach (var player in view.Players.OrderBy(player => player.DisplayCharacterName))
        {
            var characterCount = view.Characters.Count(character => character.VipPlayerId == player.VipPlayerId);
            ImGui.TableNextRow();
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
        }

        ImGui.EndTable();
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

            foreach (var package in view.Packages)
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
