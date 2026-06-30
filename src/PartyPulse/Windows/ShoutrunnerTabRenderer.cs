using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Shoutrunner;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class ShoutrunnerTabRenderer(Plugin plugin)
{
    private readonly Dictionary<string, string> drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dirty = new(StringComparer.OrdinalIgnoreCase);
    private Guid activeProfileId;
    private DateTimeOffset? lastReceivedAt;
    private string resetReason = string.Empty;
    private bool requestResetPopup;
    private bool requestReportPopup;

    public void Draw(VenueConnectionConfiguration venue)
    {
        plugin.EnsureOpeningPublicationsLoaded(venue);
        var snapshot = plugin.OpeningPublications.GetSnapshot(venue);
        var view = snapshot.View;
        if (view is not null && !view.Capabilities.CanUseShoutrunner)
            return;

        PartyPulseUi.PageHeader("Shoutrunner", "Plan world routes, execute opening advertisements, and track completed destinations.");

        SyncDrafts(venue, snapshot.ReceivedAt, view, "shoutrunner");
        var busy = plugin.OpeningPublications.IsBusy(venue.ProfileId);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh Shoutrunner data"))
            plugin.RefreshOpeningPublications(venue);
        ImGui.EndDisabled();

        if (view is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            DrawConfirmations(venue, null, null);
            return;
        }

        DrawTools(venue, view, snapshot.EstimatedServerNow, busy);
        DrawSetup(venue, view.Worlds);

        if (view.Capabilities.CanManageShoutrunnerTemplates &&
            ImGui.CollapsingHeader("Shoutrunner template editor"))
        {
            ImGui.TextDisabled("Templates support <theme>, <djs>, <date>, and <time>.");
            foreach (var template in view.Templates.Where(value => value.ChannelCode == "shoutrunner"))
                DrawTemplate(venue, template, busy);
        }

        DrawConfirmations(
            venue,
            view,
            ShoutrunnerPublicationSelector.Resolve(view, snapshot.EstimatedServerNow, VenueTimeZone.Resolve(venue)));
    }

    private void DrawTools(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse view,
        DateTimeOffset serverNow,
        bool busy)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Shoutrunner tools");
        ImGui.Separator();

        var publication = ShoutrunnerPublicationSelector.Resolve(view, serverNow, VenueTimeZone.Resolve(venue));
        var returnOpening = ResolveReturnOpening(view, serverNow);
        if (ImGui.Button("Return to venue"))
            plugin.ReturnShoutrunnerToVenue(venue, returnOpening);
        if (ImGui.IsItemHovered())
        {
            var worldName = returnOpening?.AddressWorldName ?? venue.AddressWorldName;
            var cityName = returnOpening?.AddressCityName ?? venue.AddressCityName;
            var ward = returnOpening?.AddressWard ?? venue.AddressWard;
            var plot = returnOpening?.AddressPlot ?? venue.AddressPlot;
            ImGui.SetTooltip($"/li {worldName} {cityName} {ward} {plot}");
        }
        var profile = plugin.ShoutrunnerDuty.GetProfile(venue);
        if (publication is null)
        {
            ImGui.TextDisabled("No generated Shoutrunner macro currently applies to the current or next opening.");
            DrawPendingReportControls(venue, profile, busy);
            return;
        }

        var route = plugin.ShoutrunnerDuty.GetRouteSnapshot(venue, view, publication);
        ImGui.TextUnformatted($"Opening #{publication.OpeningId} — {publication.DisplayName}");
        ImGui.TextDisabled($"{VenueTimeZone.Format(venue, publication.OpensAt, "ddd yyyy-MM-dd HH:mm")} to {VenueTimeZone.Format(venue, publication.ClosesAt, "yyyy-MM-dd HH:mm")}");

        if (route.CurrentLocation is null)
            ImGui.TextDisabled("Current game location is not available.");
        else
            ImGui.TextDisabled($"Current: {route.CurrentLocation.WorldName} / {route.CurrentLocation.CityName ?? "outside a supported shout city"}");

        if (route.NextDestination is null)
            ImGui.TextUnformatted(route.TotalLocations == 0 ? "Next: select worlds below" : "Next: all selected destinations complete");
        else
            ImGui.TextUnformatted($"Next: {route.NextDestination.WorldName} ({route.NextDestination.DatacenterName}) / {route.NextDestination.CityName}");

        var fraction = route.TotalLocations == 0
            ? 0f
            : Math.Clamp(route.CompletedLocations / (float)route.TotalLocations, 0f, 1f);
        ImGui.ProgressBar(
            fraction,
            new Vector2(-1, 0),
            $"{route.CompletedLocations}/{route.TotalLocations} locations");

        var roundComplete = route.TotalLocations > 0 && route.CompletedLocations == route.TotalLocations;
        if (roundComplete)
        {
            ImGui.BeginDisabled(busy);
            if (ImGui.Button("Complete"))
                plugin.CompleteShoutrunnerRound(venue, view, publication);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Finish this round and clear progress without creating a reset log.");
        }
        else
        {
            ImGui.BeginDisabled(
                busy ||
                route.NextDestination is null ||
                route.IsAtNextDestination ||
                route.TravelCooldownActive);
            if (ImGui.Button(route.CompletedLocations == 0 ? "Start" : "Next"))
                plugin.TravelShoutrunner(venue, view, publication);
            ImGui.EndDisabled();
        }
        if (route.TravelCooldownActive)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Next available in {Math.Ceiling(route.TravelCooldownRemaining.TotalSeconds)} seconds.");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(busy || plugin.IsGameMacroBusy || route.TravelCooldownActive || !route.IsAtNextDestination);
        if (ImGui.Button("Shout"))
            plugin.RunShoutrunnerShout(venue, view, publication);
        ImGui.EndDisabled();
        if (!route.IsAtNextDestination && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Travel to the displayed world and city before running the macro.");

        ImGui.SameLine();
        ImGui.BeginDisabled(route.CompletedLocations == 0);
        if (ImGui.Button("Reset"))
            requestResetPopup = true;
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.BeginChild("CurrentShoutrunnerMacro", new Vector2(0, 95 * ImGuiHelpers.GlobalScale), true);
        ImGui.TextWrapped(publication.Text);
        ImGui.EndChild();

        DrawPendingReportControls(venue, profile, busy);
    }

    private static OpeningPublicationOpeningSummary? ResolveReturnOpening(
        OpeningPublicationContextResponse view,
        DateTimeOffset serverNow)
    {
        var available = view.Openings
            .Where(opening => opening.ClosesAt > serverNow)
            .OrderBy(opening => opening.OpensAt)
            .ThenBy(opening => opening.OpeningId)
            .ToArray();
        return available.FirstOrDefault(opening => opening.OpensAt <= serverNow)
               ?? available.FirstOrDefault(opening => opening.OpensAt > serverNow);
    }

    private void DrawPendingReportControls(
        VenueConnectionConfiguration venue,
        ShoutrunnerProfileConfiguration profile,
        bool busy)
    {
        var pending = profile.PendingLogs.Count;
        ImGui.TextDisabled($"Pending duty log entries: {pending}");
        ImGui.SameLine();
        ImGui.BeginDisabled(busy || pending == 0);
        if (ImGui.Button("Report end of duty"))
            requestReportPopup = true;
        ImGui.EndDisabled();
    }

    private void DrawSetup(
        VenueConnectionConfiguration venue,
        IReadOnlyList<ShoutrunnerWorldSummary> worlds)
    {
        if (!ImGui.CollapsingHeader("Shoutrunner setup"))
            return;

        ImGui.TextWrapped("Select entire data centers, then expand them to add or remove individual worlds. This selection is stored only on this device.");
        var profile = plugin.ShoutrunnerDuty.GetProfile(venue);
        foreach (var datacenter in worlds
                     .GroupBy(world => world.DatacenterName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var dcWorlds = datacenter.OrderBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase).ToArray();
            var selectedCount = dcWorlds.Count(world =>
                profile.SelectedWorldNames.Contains(world.WorldName, StringComparer.OrdinalIgnoreCase));
            var allSelected = selectedCount == dcWorlds.Length && dcWorlds.Length > 0;

            ImGui.PushID($"ShoutrunnerDc{datacenter.Key}");
            if (ImGui.Checkbox("##Selected", ref allSelected))
                plugin.ShoutrunnerDuty.SetDatacenterSelected(venue, dcWorlds, allSelected);
            ImGui.SameLine();
            if (ImGui.TreeNode($"{datacenter.Key} ({selectedCount}/{dcWorlds.Length})###ShoutrunnerDcTree{datacenter.Key}"))
            {
                foreach (var world in dcWorlds)
                {
                    var selected = profile.SelectedWorldNames.Contains(world.WorldName, StringComparer.OrdinalIgnoreCase);
                    if (ImGui.Checkbox($"{world.WorldName}##World{world.WorldId}", ref selected))
                        plugin.ShoutrunnerDuty.SetWorldSelected(venue, world, selected);
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
    }

    private void DrawTemplate(
        VenueConnectionConfiguration venue,
        OpeningPublicationTemplateSummary template,
        bool busy)
    {
        drafts.TryAdd(template.PublicationCode, template.TemplateText ?? string.Empty);
        var value = drafts[template.PublicationCode];
        ImGui.Spacing();
        ImGui.TextUnformatted(template.DisplayName);
        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(template.Description))
            ImGui.TextWrapped(template.Description);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline(
                $"##ShoutrunnerTemplate{template.PublicationCode}",
                ref value,
                4000,
                new Vector2(0, 115 * ImGuiHelpers.GlobalScale)))
        {
            drafts[template.PublicationCode] = value;
            dirty.Add(template.PublicationCode);
        }

        var valid = Validate(value, template.MaxLines, template.MaxLineLength, out var lines, out var longest);
        ImGui.TextDisabled($"{lines}/{template.MaxLines} lines; longest line {longest}/{template.MaxLineLength} characters.");
        if (!valid)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "Template exceeds its line limit.");
        ImGui.BeginDisabled(busy || !template.CanManage || !valid || !dirty.Contains(template.PublicationCode));
        if (ImGui.Button($"Save {template.DisplayName}##Save{template.PublicationCode}"))
        {
            plugin.SaveOpeningPublicationTemplate(venue, template.PublicationCode, value);
            dirty.Remove(template.PublicationCode);
        }
        ImGui.EndDisabled();
    }

    private void DrawConfirmations(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse? view,
        ActiveShoutrunnerPublication? publication)
    {
        if (requestResetPopup)
        {
            ImGui.OpenPopup("Reset Shoutrunner progress###PartyPulseResetShoutrunner");
            requestResetPopup = false;
        }
        if (ImGui.BeginPopupModal("Reset Shoutrunner progress###PartyPulseResetShoutrunner", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Reset all completed destinations for this opening?");
            ImGui.SetNextItemWidth(420 * ImGuiHelpers.GlobalScale);
            ImGui.InputText("Reason", ref resetReason, 500);
            ImGui.BeginDisabled(view is null || publication is null || string.IsNullOrWhiteSpace(resetReason));
            if (ImGui.Button("Reset and log"))
            {
                plugin.ResetShoutrunner(venue, view!, publication!, resetReason);
                resetReason = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                resetReason = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (requestReportPopup)
        {
            ImGui.OpenPopup("Report Shoutrunner duty###PartyPulseReportShoutrunnerDuty");
            requestReportPopup = false;
        }
        if (ImGui.BeginPopupModal("Report Shoutrunner duty###PartyPulseReportShoutrunnerDuty", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var pending = plugin.ShoutrunnerDuty.GetProfile(venue).PendingLogs.Count;
            ImGui.TextWrapped($"Send {pending} locally saved Shoutrunner log entries to the venue database?");
            ImGui.TextDisabled("Entries are cleared locally only after the server confirms the report.");
            ImGui.BeginDisabled(pending == 0);
            if (ImGui.Button("Report end of duty"))
            {
                plugin.ReportShoutrunnerDuty(venue);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void SyncDrafts(
        VenueConnectionConfiguration venue,
        DateTimeOffset? receivedAt,
        OpeningPublicationContextResponse? view,
        string channel)
    {
        if (activeProfileId != venue.ProfileId)
        {
            activeProfileId = venue.ProfileId;
            drafts.Clear();
            dirty.Clear();
            lastReceivedAt = null;
        }
        if (view is null || receivedAt == lastReceivedAt) return;
        foreach (var template in view.Templates.Where(value => value.ChannelCode == channel))
        {
            if (!dirty.Contains(template.PublicationCode))
                drafts[template.PublicationCode] = template.TemplateText ?? string.Empty;
        }
        lastReceivedAt = receivedAt;
    }

    internal static bool Validate(string value, int maxLines, int maxLineLength, out int lines, out int longest)
    {
        var parts = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        lines = parts.Length == 1 && parts[0].Length == 0 ? 0 : parts.Length;
        longest = parts.Length == 0 ? 0 : parts.Max(part => part.Length);
        return lines <= maxLines && longest <= maxLineLength;
    }
}
