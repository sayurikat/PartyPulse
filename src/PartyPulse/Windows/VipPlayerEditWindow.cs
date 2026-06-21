using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Api;

namespace PartyPulse.Windows;

public sealed class VipPlayerEditWindow : Window, IDisposable
{
    private const string UnlinkCharacterPopupName = "Unlink VIP character###PartyPulseVipUnlinkCharacter";
    private const string CancelSubscriptionPopupName = "Cancel VIP subscription###PartyPulseVipCancelSubscription";
    private const string PaymentStatusPopupName = "Change club payment###PartyPulseVipPaymentStatus";

    private readonly Plugin plugin;
    private Guid profileId;
    private int vipPlayerId;
    private string discordUsername = string.Empty;
    private int pendingUnlinkCharacterId;
    private string pendingUnlinkCharacterName = string.Empty;
    private long pendingCancelSubscriptionId;
    private string cancellationReason = string.Empty;
    private long pendingPaymentSubscriptionId;
    private bool pendingPaymentSettled;
    private bool openUnlinkCharacterPopup;
    private bool openCancelSubscriptionPopup;
    private bool openPaymentStatusPopup;

    public VipPlayerEditWindow(Plugin plugin)
        : base("Edit VIP Player###PartyPulseVipPlayerEdit")
    {
        this.plugin = plugin;
        IsOpen = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 600),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Open(Guid venueProfileId, int playerId)
    {
        profileId = venueProfileId;
        vipPlayerId = playerId;
        pendingUnlinkCharacterId = 0;
        pendingCancelSubscriptionId = 0;
        pendingPaymentSubscriptionId = 0;
        cancellationReason = string.Empty;
        openUnlinkCharacterPopup = false;
        openCancelSubscriptionPopup = false;
        openPaymentStatusPopup = false;

        var venue = plugin.Configuration.VenueConnections.FirstOrDefault(value => value.ProfileId == profileId);
        var player = venue is null
            ? null
            : plugin.Vip.GetSnapshot(venue).View?.Players.FirstOrDefault(value => value.VipPlayerId == vipPlayerId);
        discordUsername = player?.DiscordUsername ?? string.Empty;
        IsOpen = true;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var venue = plugin.Configuration.VenueConnections.FirstOrDefault(value => value.ProfileId == profileId);
        if (venue is null)
        {
            ImGui.TextDisabled("The selected venue is no longer configured.");
            return;
        }

        var snapshot = plugin.Vip.GetSnapshot(venue);
        var view = snapshot.View;
        var player = view?.Players.FirstOrDefault(value => value.VipPlayerId == vipPlayerId);
        if (view is null || player is null)
        {
            ImGui.TextDisabled("The VIP player is no longer available.");
            if (ImGui.Button("Refresh"))
            {
                plugin.RefreshVip(venue);
            }
            return;
        }

        var busy = plugin.Vip.IsBusy(venue.ProfileId);
        ImGui.TextUnformatted(player.CharacterDisplay);
        ImGui.TextDisabled($"VIP player #{player.VipPlayerId}");

        ImGui.Spacing();
        ImGui.TextUnformatted("Discord profile");
        ImGui.Separator();
        ImGui.BeginDisabled(busy || !view.Capabilities.CanManagePlayers);
        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Discord username", ref discordUsername, 100);
        if (ImGui.Button("Save Discord username"))
        {
            plugin.UpdateVipPlayer(
                venue,
                player.VipPlayerId,
                new UpdateVipPlayerRequest(string.IsNullOrWhiteSpace(discordUsername)
                    ? null
                    : discordUsername.Trim()));
        }
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(player.DiscordNickname))
        {
            ImGui.TextDisabled($"Discord nickname (bot managed): {player.DiscordNickname}");
        }
        if (player.DiscordId is { } discordId)
        {
            ImGui.TextDisabled($"Discord ID (bot managed): {discordId}");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Linked characters");
        ImGui.Separator();
        DrawCharacters(venue, view, player, busy);

        ImGui.Spacing();
        ImGui.TextUnformatted("Subscription history");
        ImGui.Separator();
        DrawSubscriptions(venue, view, player, busy);

        OpenQueuedConfirmationPopups();
        DrawUnlinkConfirmation(venue);
        DrawCancellationConfirmation(venue);
        DrawPaymentConfirmation(venue);

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Close", new Vector2(100 * ImGuiHelpers.GlobalScale, 0)))
        {
            IsOpen = false;
        }
    }

    private void DrawCharacters(
        PartyPulse.Models.VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        VipPlayerSummary player,
        bool busy)
    {
        var characters = view.Characters
            .Where(value => value.VipPlayerId == player.VipPlayerId)
            .OrderBy(value => value.CharacterId)
            .ToArray();

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("VipEditCharacters", 3, flags))
        {
            return;
        }

        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("Display status");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 210 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var character in characters)
        {
            ImGui.PushID(character.CharacterId);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(character.DisplayName);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(character.IsPreferred ? "Preferred" : "Linked");
            ImGui.TableSetColumnIndex(2);

            ImGui.BeginDisabled(busy || !view.Capabilities.CanManagePlayers || character.IsPreferred);
            if (ImGui.SmallButton("Set preferred"))
            {
                plugin.SetVipPreferredCharacter(venue, player.VipPlayerId, character.CharacterId);
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(busy || !view.Capabilities.CanManagePlayers || characters.Length <= 1);
            if (ImGui.SmallButton("Unlink"))
            {
                pendingUnlinkCharacterId = character.CharacterId;
                pendingUnlinkCharacterName = character.DisplayName;
                openUnlinkCharacterPopup = true;
            }
            ImGui.EndDisabled();
            if (characters.Length <= 1 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("A VIP player must keep at least one linked character.");
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawSubscriptions(
        PartyPulse.Models.VenueConnectionConfiguration venue,
        VipManagementViewResponse view,
        VipPlayerSummary player,
        bool busy)
    {
        var subscriptions = view.Subscriptions
            .Where(value => value.VipPlayerId == player.VipPlayerId)
            .OrderByDescending(value => value.StartsAt)
            .ThenByDescending(value => value.SubscriptionId)
            .ToArray();

        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(
                "VipEditSubscriptions",
                7,
                flags,
                new Vector2(0, 280 * ImGuiHelpers.GlobalScale)))
        {
            return;
        }

        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Package");
        ImGui.TableSetupColumn("Period");
        ImGui.TableSetupColumn("Seller");
        ImGui.TableSetupColumn("Club payment");
        ImGui.TableSetupColumn("Cancel", ImGuiTableColumnFlags.WidthFixed, 72 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Payment", ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var subscription in subscriptions)
        {
            ImGui.PushID((int)(subscription.SubscriptionId % int.MaxValue));
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(GetStatus(subscription));
            if (subscription.IsCancelled && !string.IsNullOrWhiteSpace(subscription.CancellationReason))
            {
                ImGui.TextDisabled(subscription.CancellationReason);
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{subscription.PackageName}\n{subscription.PurchasePriceGil:N0} gil");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(subscription.Lifetime
                ? $"{subscription.StartsAt.ToLocalTime():g}\nLifetime"
                : $"{subscription.StartsAt.ToLocalTime():g}\n{subscription.EndsAt!.Value.ToLocalTime():g}");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(subscription.SellerDisplayName);
            ImGui.TableSetColumnIndex(4);
            if (subscription.IsSettled)
            {
                ImGui.TextUnformatted($"Settled\n{subscription.PaidToVenueAt!.Value.ToLocalTime():g}");
            }
            else if (subscription.IsInPendingSettlement)
            {
                ImGui.TextUnformatted($"Pending #{subscription.PendingSettlementId}");
            }
            else
            {
                ImGui.TextDisabled("Unsettled");
            }

            ImGui.TableSetColumnIndex(5);
            ImGui.BeginDisabled(busy || !view.Capabilities.CanManagePlayers || subscription.IsCancelled);
            if (ImGui.SmallButton("Cancel"))
            {
                pendingCancelSubscriptionId = subscription.SubscriptionId;
                cancellationReason = string.Empty;
                openCancelSubscriptionPopup = true;
            }
            ImGui.EndDisabled();

            ImGui.TableSetColumnIndex(6);
            ImGui.BeginDisabled(
                busy ||
                !view.Capabilities.CanManagePayments ||
                subscription.IsInPendingSettlement);
            var paymentLabel = subscription.IsSettled ? "Mark unpaid" : "Mark settled";
            if (ImGui.SmallButton(paymentLabel))
            {
                pendingPaymentSubscriptionId = subscription.SubscriptionId;
                pendingPaymentSettled = !subscription.IsSettled;
                openPaymentStatusPopup = true;
            }
            ImGui.EndDisabled();
            if (subscription.IsInPendingSettlement && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("Resolve or reject the pending settlement transaction first.");
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void OpenQueuedConfirmationPopups()
    {
        // The action buttons are rendered inside per-row PushID scopes. Dear ImGui
        // includes the current ID stack when resolving popup IDs, so opening a
        // modal from inside a row and beginning it later at the window root uses
        // two different IDs. Queue the request and open each modal here, where
        // the matching BeginPopupModal calls are also rendered.
        if (openUnlinkCharacterPopup)
        {
            ImGui.OpenPopup(UnlinkCharacterPopupName);
            openUnlinkCharacterPopup = false;
        }

        if (openCancelSubscriptionPopup)
        {
            ImGui.OpenPopup(CancelSubscriptionPopupName);
            openCancelSubscriptionPopup = false;
        }

        if (openPaymentStatusPopup)
        {
            ImGui.OpenPopup(PaymentStatusPopupName);
            openPaymentStatusPopup = false;
        }
    }

    private void DrawUnlinkConfirmation(PartyPulse.Models.VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(
                UnlinkCharacterPopupName,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped($"Unlink {pendingUnlinkCharacterName} from this VIP player?");
        ImGui.TextColored(
            new Vector4(1f, 0.65f, 0.25f, 1f),
            "This cannot be reversed from the VIP list. To link it again, a seller must target that character in game.");
        if (ImGui.Button("Unlink character"))
        {
            plugin.UnlinkVipCharacter(venue, vipPlayerId, pendingUnlinkCharacterId);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep linked"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawCancellationConfirmation(PartyPulse.Models.VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(
                CancelSubscriptionPopupName,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped($"Cancel VIP subscription #{pendingCancelSubscriptionId}?");
        ImGui.TextColored(
            new Vector4(1f, 0.65f, 0.25f, 1f),
            "PartyPulse only records the cancellation. Any refund must be handled separately and will not be issued automatically.");
        ImGui.SetNextItemWidth(420 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Reason (optional)", ref cancellationReason, 255);
        if (ImGui.Button("Cancel subscription"))
        {
            plugin.CancelVipSubscription(
                venue,
                pendingCancelSubscriptionId,
                new CancelVipSubscriptionRequest(cancellationReason));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Go back"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawPaymentConfirmation(PartyPulse.Models.VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginPopupModal(
                PaymentStatusPopupName,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(pendingPaymentSettled
            ? $"Mark subscription #{pendingPaymentSubscriptionId} as paid to the club?"
            : $"Mark subscription #{pendingPaymentSubscriptionId} as unpaid to the club?");
        ImGui.TextWrapped("This is a privileged manual accounting override and is recorded in the payment audit history.");
        if (ImGui.Button(pendingPaymentSettled ? "Mark settled" : "Mark unpaid"))
        {
            plugin.SetVipSubscriptionPaymentStatus(
                venue,
                pendingPaymentSubscriptionId,
                new SetVipSubscriptionPaymentStatusRequest(pendingPaymentSettled));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private static string GetStatus(VipSubscriptionSummary subscription)
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

        return subscription.Lifetime || subscription.EndsAt > now ? "Active" : "Expired";
    }
}
