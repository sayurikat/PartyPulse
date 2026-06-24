using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Services;
using PartyPulse.Vip;

namespace PartyPulse.Windows;

public sealed class VipArrivalWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Guid venueProfileId;

    public VipArrivalWindow(Plugin plugin)
        : base("VIP arrival tracker###PartyPulseVipArrivalTracker")
    {
        this.plugin = plugin;
        IsOpen = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Open(Guid profileId)
    {
        venueProfileId = profileId;
        IsOpen = true;
    }

    public override void Draw()
    {
        var venue = plugin.Configuration.VenueConnections.FirstOrDefault(
            value => value.ProfileId == venueProfileId);
        if (venue is null)
        {
            ImGui.TextWrapped("This venue is no longer configured on this device.");
            return;
        }

        plugin.EnsureVipLoaded(venue);
        plugin.EnsureVipArrivalsLoaded(venue);

        var vipSnapshot = plugin.Vip.GetSnapshot(venue);
        var arrivalSnapshot = plugin.VipArrivals.GetSnapshot(venue);

        DrawHeader(venue, arrivalSnapshot);

        if (vipSnapshot.Status != VipManagementStatus.Ready || vipSnapshot.View is null)
        {
            ImGui.TextWrapped(vipSnapshot.Message);
            return;
        }

        if (arrivalSnapshot.Status != VipArrivalManagementStatus.Ready || arrivalSnapshot.Context is null)
        {
            ImGui.TextWrapped(arrivalSnapshot.Message);
            return;
        }

        var context = arrivalSnapshot.Context;
        if (!context.Capabilities.CanUseArrival)
        {
            ImGui.TextWrapped("You do not have permission to use the VIP arrival tracker.");
            return;
        }

        if (context.CurrentOpening is not { } opening)
        {
            plugin.VipArrivalNearby.Clear();
            ImGui.TextWrapped("There is no active venue opening. Start a temporary opening from the VIP tab, or wait for a future scheduled opening.");
            return;
        }

        ImGui.TextUnformatted(opening.Title ?? $"Opening #{opening.OpeningId}");
        ImGui.TextDisabled($"{VenueTimeZone.Format(venue, opening.OpensAt, "g")} – {VenueTimeZone.Format(venue, opening.ClosesAt, "g")}");
        ImGui.TextDisabled(opening.AddressDisplay);

        if (!IsAtOpeningAddress(opening, out var locationMessage))
        {
            plugin.VipArrivalNearby.Clear();
            ImGui.Spacing();
            ImGui.TextColored(
                new Vector4(1f, 0.72f, 0.25f, 1f),
                "Arrival tracking is paused because you are not at this opening's venue address.");
            ImGui.TextWrapped(locationMessage);
            return;
        }

        var view = vipSnapshot.View;
        plugin.VipArrivalNearby.Prepare(venue.ProfileId, opening.OpeningId, view);
        plugin.VipArrivalNearby.ScanIfDue();
        var observations = plugin.VipArrivalNearby.TakeUnsubmittedObservations();
        if (observations.Count > 0)
        {
            plugin.SubmitVipArrivalObservations(venue, opening.OpeningId, observations);
        }

        ImGui.Spacing();
        ImGui.TextDisabled(
            $"Nearby linked VIP players: {plugin.VipArrivalNearby.NearbyCount:N0}. " +
            "The list is shared by all sellers for this opening.");

        var arrivals = context.Arrivals
            .Where(value =>
                value.OpeningId == opening.OpeningId &&
                value.CompletedAt is null)
            .OrderBy(value => value.FirstSeenAt)
            .ThenBy(value => value.VipPlayerId)
            .ToArray();

        if (arrivals.Length == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No VIP arrivals are waiting for action.");
            return;
        }

        var welcomeMacro = context.Macros.FirstOrDefault(
            value => string.Equals(value.MacroCode, VipArrivalMacroCodes.Welcome, StringComparison.OrdinalIgnoreCase));
        var renewalMacro = context.Macros.FirstOrDefault(
            value => string.Equals(value.MacroCode, VipArrivalMacroCodes.Renewal, StringComparison.OrdinalIgnoreCase));
        var charactersById = view.Characters.ToDictionary(value => value.CharacterId);
        var playersById = view.Players.ToDictionary(value => value.VipPlayerId);
        var isBusy = plugin.VipArrivals.IsBusy(venue.ProfileId) || plugin.IsGameMacroBusy;

        var flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable(
                "VipArrivalTasks",
                5,
                flags,
                new Vector2(0, 250 * ImGuiHelpers.GlobalScale)))
        {
            return;
        }

        ImGui.TableSetupColumn("VIP player");
        ImGui.TableSetupColumn("Arrived");
        ImGui.TableSetupColumn("Welcome");
        ImGui.TableSetupColumn("Renewal");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 245 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var arrival in arrivals)
        {
            playersById.TryGetValue(arrival.VipPlayerId, out var player);
            charactersById.TryGetValue(arrival.LastSeenCharacterId, out var seenCharacter);
            var displayName = seenCharacter?.DisplayName ?? player?.CharacterDisplay ?? $"VIP player #{arrival.VipPlayerId}";
            var isNearby = plugin.VipArrivalNearby.TryGetNearby(arrival.VipPlayerId, out var nearby);

            ImGui.PushID(arrival.VipPlayerId);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(displayName);
            ImGui.TextDisabled(isNearby ? $"Nearby: {nearby!.DisplayName}" : "Not currently nearby");

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, arrival.FirstSeenAt, "t"));

            ImGui.TableSetColumnIndex(2);
            DrawActionState(venue, arrival.WelcomedAt, "Pending");

            ImGui.TableSetColumnIndex(3);
            if (!arrival.RenewalRequired)
            {
                ImGui.TextDisabled("Not applicable");
            }
            else
            {
                DrawActionState(venue, arrival.RenewalRemindedAt, "Pending");
            }

            ImGui.TableSetColumnIndex(4);
            var drewButton = false;
            if (arrival.WelcomedAt is null)
            {
                var canRun = isNearby && welcomeMacro?.IsConfigured == true;
                ImGui.BeginDisabled(isBusy || !canRun);
                if (ImGui.SmallButton("Welcome"))
                {
                    plugin.RunVipArrivalMacro(
                        venue,
                        arrival,
                        nearby!,
                        welcomeMacro!,
                        "welcome");
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canRun)
                {
                    ImGui.SetTooltip(isNearby
                        ? "The welcome macro has not been configured."
                        : "The player must be nearby before the macro can run.");
                }
                drewButton = true;
            }

            if (arrival.RenewalRequired && arrival.RenewalRemindedAt is null)
            {
                if (drewButton) ImGui.SameLine();
                var canRun = isNearby && renewalMacro?.IsConfigured == true;
                ImGui.BeginDisabled(isBusy || !canRun);
                if (ImGui.SmallButton("Remind"))
                {
                    plugin.RunVipArrivalMacro(
                        venue,
                        arrival,
                        nearby!,
                        renewalMacro!,
                        "renewal");
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canRun)
                {
                    ImGui.SetTooltip(isNearby
                        ? "The renewal macro has not been configured."
                        : "The player must be nearby before the macro can run.");
                }
                drewButton = true;
            }

            if (drewButton) ImGui.SameLine();
            ImGui.BeginDisabled(isBusy);
            if (ImGui.SmallButton("Dismiss"))
            {
                plugin.DismissVipArrival(venue, arrival);
            }
            ImGui.EndDisabled();

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    public void Dispose()
    {
    }

    private void DrawHeader(
        VenueConnectionConfiguration venue,
        VipArrivalManagementSnapshot snapshot)
    {
        ImGui.TextUnformatted(venue.DisplayLabel);
        ImGui.SameLine();
        ImGui.BeginDisabled(plugin.VipArrivals.IsBusy(venue.ProfileId));
        if (ImGui.SmallButton("Refresh"))
        {
            plugin.RefreshVipArrivals(venue);
        }
        ImGui.EndDisabled();
        ImGui.Separator();
    }

    private bool IsAtOpeningAddress(VenueOpeningSummary opening, out string message)
    {
        if (!plugin.LocationProvider.TryGetCurrentHousingAddress(out var current, out var reason) || current is null)
        {
            message = reason;
            return false;
        }

        var matches =
            string.Equals(current.WorldName, opening.AddressWorldName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CityName, opening.AddressCityName, StringComparison.OrdinalIgnoreCase) &&
            current.Ward == opening.AddressWard &&
            current.Plot == opening.AddressPlot;
        message = matches
            ? string.Empty
            : $"Current: {current.DisplayText}\nRequired: {opening.AddressDisplay}";
        return matches;
    }

    private static void DrawActionState(VenueConnectionConfiguration venue, DateTimeOffset? completedAt, string pendingText)
    {
        if (completedAt is { } value)
        {
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, value, "t"));
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.82f, 0.2f, 1f), pendingText);
        }
    }
}
