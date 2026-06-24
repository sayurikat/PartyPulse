using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Greeter;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class GreeterTabRenderer(Plugin plugin)
{
    private readonly Dictionary<string, string> macroDrafts = new(StringComparer.OrdinalIgnoreCase);
    private Guid activeProfileId;

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenueChange(venue);
        var snapshot = plugin.Greeter.GetSnapshot(venue);
        if (snapshot.Status == GreeterManagementStatus.Denied)
            return;

        if (!ImGui.BeginTabItem("Greeter"))
            return;

        plugin.EnsureGreeterLoaded(venue);
        snapshot = plugin.Greeter.GetSnapshot(venue);
        var busy = plugin.Greeter.IsBusy(venue.ProfileId) || plugin.IsGameMacroBusy;
        ImGui.BeginDisabled(plugin.Greeter.IsBusy(venue.ProfileId));
        if (ImGui.SmallButton("Refresh greeter"))
            plugin.RefreshGreeter(venue);
        ImGui.EndDisabled();

        var context = snapshot.Context;
        if (context is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        SynchronizeMacroDrafts(context);
        DrawOpeningAndDj(venue, context);

        var opening = context.CurrentOpening;
        var locationMessage = string.Empty;
        var atAddress = opening is not null &&
                        plugin.LocationProvider.IsAtAddress(
                            opening.AddressWorldName,
                            opening.AddressCityName,
                            opening.AddressWard,
                            opening.AddressPlot,
                            out locationMessage);

        if (context.Capabilities.CanUse)
        {
            if (opening is null)
            {
                plugin.GreeterNearby.Clear();
                ImGui.Spacing();
                ImGui.TextWrapped("There is no active venue opening. Greeter arrival tracking starts automatically during an opening.");
            }
            else if (!atAddress)
            {
                plugin.GreeterNearby.Clear();
                ImGui.Spacing();
                ImGui.TextColored(
                    new Vector4(1f, 0.72f, 0.25f, 1f),
                    "Greeter tracking is paused because you are not at this opening's address.");
                ImGui.TextWrapped(locationMessage);
            }
            else
            {
                TrackNearbyPlayers(venue, opening);
                DrawTargetStatus(context, venue);
                DrawArrivalTracker(venue, context, busy);
            }
        }
        else
        {
            plugin.GreeterNearby.Clear();
            ImGui.TextDisabled("You can manage greeter macros, but you do not have permission to use the arrival tracker.");
        }

        if (context.Capabilities.CanManageMacros)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawMacroSetup(venue, context, busy);
        }

        ImGui.EndTabItem();
    }

    private static void DrawOpeningAndDj(VenueConnectionConfiguration venue, GreeterContextResponse context)
    {
        if (context.CurrentOpening is { } opening)
        {
            ImGui.TextUnformatted(opening.Title ?? $"Opening #{opening.OpeningId}");
            ImGui.TextDisabled($"{VenueTimeZone.Format(venue, opening.OpensAt, "g")} – {VenueTimeZone.Format(venue, opening.ClosesAt, "g")}");
            ImGui.TextDisabled(opening.AddressDisplay);
        }

        ImGui.Spacing();
        if (context.CurrentDj is { } dj)
        {
            ImGui.TextUnformatted($"Current DJ: {dj.Name}{(dj.Resident ? " (Resident)" : string.Empty)}");
            ImGui.TextDisabled($"{VenueTimeZone.Format(venue, dj.StartsAt, "t")} – {VenueTimeZone.Format(venue, dj.EndsAt, "t")}");
            if (!string.IsNullOrWhiteSpace(dj.TwitchUrl))
                ImGui.TextWrapped(dj.TwitchUrl);
        }
        else
        {
            ImGui.TextDisabled("Current DJ: none. The no-DJ greeting macros will be used.");
        }
    }

    private void TrackNearbyPlayers(
        VenueConnectionConfiguration venue,
        GreeterOpeningSummary opening)
    {
        plugin.GreeterNearby.Prepare(venue.ProfileId, opening.OpeningId);
        plugin.GreeterNearby.ScanIfDue();
        var observations = plugin.GreeterNearby.TakeUnsubmittedObservations();
        if (observations.Count > 0)
            plugin.SubmitGreeterObservations(venue, opening.OpeningId, observations);

        ImGui.Spacing();
        ImGui.TextDisabled(
            $"Nearby players: {plugin.GreeterNearby.NearbyCount:N0}. " +
            "Greeting and dismissal state is shared by all greeters for this opening.");
    }

    private void DrawTargetStatus(GreeterContextResponse context, VenueConnectionConfiguration venue)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Current target");
        if (!plugin.TargetProvider.TryGetCurrentTarget(out var identity, out var reason))
        {
            ImGui.TextDisabled(reason);
            return;
        }

        var arrival = context.Arrivals.FirstOrDefault(value =>
            string.Equals(value.CharacterName, identity!.CharacterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.WorldName, identity.WorldName, StringComparison.OrdinalIgnoreCase));

        ImGui.TextUnformatted(identity!.DisplayName);
        if (arrival is null)
        {
            ImGui.TextDisabled("Not observed for this opening yet.");
            return;
        }

        ImGui.SameLine();
        ImGui.TextColored(
            arrival.IsVip
                ? new Vector4(0.82f, 0.55f, 1f, 1f)
                : new Vector4(0.72f, 0.72f, 0.72f, 1f),
            arrival.IsVip ? "VIP" : "Regular guest");
        ImGui.TextUnformatted(GetArrivalState(venue, arrival));
    }

    private void DrawArrivalTracker(
        VenueConnectionConfiguration venue,
        GreeterContextResponse context,
        bool busy)
    {
        var opening = context.CurrentOpening!;
        var arrivals = context.Arrivals
            .Where(value => value.OpeningId == opening.OpeningId)
            .OrderBy(value => value.CompletedAt is not null)
            .ThenBy(value => value.FirstSeenAt)
            .ThenBy(value => value.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ImGui.Spacing();
        ImGui.TextUnformatted("Player arrivals");
        if (arrivals.Length == 0)
        {
            ImGui.TextDisabled("No players have been observed during this opening yet.");
            return;
        }

        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(
                "GreeterArrivalTable",
                5,
                flags,
                new Vector2(0, 280 * ImGuiHelpers.GlobalScale)))
            return;

        ImGui.TableSetupColumn("Player");
        ImGui.TableSetupColumn("VIP", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Arrived", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 175 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var arrival in arrivals)
        {
            var nearby = plugin.GreeterNearby.TryGetNearby(
                arrival.CharacterName,
                arrival.WorldName,
                out var nearbyPlayer);

            ImGui.PushID($"{arrival.WorldId}:{arrival.CharacterName}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(arrival.DisplayName);
            ImGui.TextDisabled(nearby ? "Nearby" : "Not currently nearby");

            ImGui.TableSetColumnIndex(1);
            ImGui.TextColored(
                arrival.IsVip
                    ? new Vector4(0.82f, 0.55f, 1f, 1f)
                    : new Vector4(0.72f, 0.72f, 0.72f, 1f),
                arrival.IsVip ? "Yes" : "No");

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(VenueTimeZone.Format(venue, arrival.FirstSeenAt, "t"));

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(GetArrivalState(venue, arrival));

            ImGui.TableSetColumnIndex(4);
            if (arrival.CompletedAt is null)
            {
                var macro = ResolveGreetingMacro(context, arrival.IsVip);
                var canGreet = nearby && macro?.IsConfigured == true;
                ImGui.BeginDisabled(busy || !canGreet);
                if (ImGui.SmallButton(arrival.IsVip ? "VIP Greet" : "Greet"))
                {
                    plugin.RunGreeterMacro(
                        venue,
                        arrival,
                        nearbyPlayer!,
                        macro!,
                        context.CurrentDj);
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canGreet)
                {
                    ImGui.SetTooltip(nearby
                        ? "The required greeting macro has not been configured."
                        : "The player must be nearby before the greeting can run.");
                }

                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Dismiss"))
                    plugin.DismissGreeterArrival(venue, arrival);
                ImGui.EndDisabled();
            }
            else
            {
                ImGui.TextDisabled("Complete");
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawMacroSetup(
        VenueConnectionConfiguration venue,
        GreeterContextResponse context,
        bool busy)
    {
        if (!ImGui.CollapsingHeader("Greeter macro setup"))
            return;

        ImGui.TextWrapped(
            "The two with-DJ macros support <name> and <twitch>, replaced with the currently playing confirmed DJ. " +
            "The no-DJ macros are selected automatically when no DJ is playing.");

        foreach (var macro in context.Macros.OrderBy(value => MacroOrder(value.MacroCode)))
        {
            ImGui.PushID(macro.MacroCode);
            ImGui.TextUnformatted(macro.DisplayName);
            if (!string.IsNullOrWhiteSpace(macro.Description))
                ImGui.TextDisabled(macro.Description);

            var draft = macroDrafts.TryGetValue(macro.MacroCode, out var value)
                ? value
                : macro.MacroText ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextMultiline(
                "##GreeterMacroText",
                ref draft,
                4000,
                new Vector2(0, 85 * ImGuiHelpers.GlobalScale));
            macroDrafts[macro.MacroCode] = draft;

            var valid = ValidateMacroText(draft, macro.MaxLines, macro.MaxLineLength, out var lines, out var longest);
            ImGui.TextDisabled($"{lines}/{macro.MaxLines} lines; longest line {longest}/{macro.MaxLineLength} characters.");
            ImGui.BeginDisabled(busy || !valid || !macro.CanManage);
            if (ImGui.SmallButton("Save macro"))
            {
                plugin.UpdateGreeterMacro(
                    venue,
                    macro.MacroCode,
                    string.IsNullOrWhiteSpace(draft) ? null : draft);
            }
            ImGui.EndDisabled();
            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void SynchronizeMacroDrafts(GreeterContextResponse context)
    {
        foreach (var macro in context.Macros)
        {
            if (!macroDrafts.ContainsKey(macro.MacroCode))
                macroDrafts[macro.MacroCode] = macro.MacroText ?? string.Empty;
        }
    }

    private void ResetForVenueChange(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
            return;
        activeProfileId = venue.ProfileId;
        macroDrafts.Clear();
        plugin.GreeterNearby.Clear();
    }

    private static GreeterMacroSummary? ResolveGreetingMacro(
        GreeterContextResponse context,
        bool isVip)
    {
        var code = context.CurrentDj is null
            ? isVip ? GreeterMacroCodes.VipGreetWithoutDj : GreeterMacroCodes.GreetWithoutDj
            : isVip ? GreeterMacroCodes.VipGreetWithDj : GreeterMacroCodes.GreetWithDj;
        return context.Macros.FirstOrDefault(value =>
            string.Equals(value.MacroCode, code, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetArrivalState(VenueConnectionConfiguration venue, GreeterArrivalSummary arrival)
    {
        if (arrival.GreetedAt is { } greeted)
            return $"Greeted {VenueTimeZone.Format(venue, greeted, "t")}";
        if (arrival.DismissedAt is { } dismissed)
            return $"Dismissed {VenueTimeZone.Format(venue, dismissed, "t")}";
        return "Waiting";
    }

    private static int MacroOrder(string macroCode) => macroCode switch
    {
        GreeterMacroCodes.GreetWithDj => 0,
        GreeterMacroCodes.VipGreetWithDj => 1,
        GreeterMacroCodes.GreetWithoutDj => 2,
        GreeterMacroCodes.VipGreetWithoutDj => 3,
        _ => 10
    };

    private static bool ValidateMacroText(
        string text,
        int maxLines,
        int maxLineLength,
        out int lineCount,
        out int longestLine)
    {
        var lines = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        lineCount = lines.Length == 1 && lines[0].Length == 0 ? 0 : lines.Length;
        longestLine = lines.Length == 0 ? 0 : lines.Max(value => value.Length);
        return lineCount <= maxLines && longestLine <= maxLineLength;
    }
}
