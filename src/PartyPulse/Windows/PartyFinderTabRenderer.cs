using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Contracts.PartyFinder;
using PartyPulse.Models;
using PartyPulse.PartyFinder;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class PartyFinderTabRenderer(Plugin plugin)
{
    private readonly Dictionary<string, string> drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dirty = new(StringComparer.OrdinalIgnoreCase);
    private Guid activeProfileId;
    private DateTimeOffset? lastReceivedAt;
    private static readonly (string Code, string Label)[] DevStrategies =
    [
        (PartyFinderDevStrategies.BalanceAllGaps, "Balance all gaps (recommended)"),
        (PartyFinderDevStrategies.ExpiryOnly, "Expiry only"),
        (PartyFinderDevStrategies.PredecessorChain, "Predecessor chain"),
    ];
    private long? devSettingsOpeningId;
    private PartyFinderDevSettings? savedDevSettings;
    private PartyFinderDevSettings? devSettingsDraft;

    public void Draw(VenueConnectionConfiguration venue, MainSubtab subtab)
    {
        plugin.EnsureOpeningPublicationsLoaded(venue);
        var snapshot = plugin.OpeningPublications.GetSnapshot(venue);
        var view = snapshot.View;
        if (view is not null &&
            !view.Capabilities.CanUsePartyFinder &&
            !view.Capabilities.CanManagePartyFinderTemplates)
            return;

        SyncDrafts(venue, snapshot.ReceivedAt, view);
        var busy = plugin.OpeningPublications.IsBusy(venue.ProfileId);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh Party Finder data"))
            plugin.RefreshOpeningPublications(venue);
        ImGui.EndDisabled();

        if (view is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        if (subtab == MainSubtab.PartyFinderRun && view.Capabilities.CanUsePartyFinder)
        {
            DrawActivePublication(venue, view, snapshot.EstimatedServerNow, busy);
        }
        else if (subtab == MainSubtab.PartyFinderTemplates &&
                 view.Capabilities.CanManagePartyFinderTemplates)
        {
            ImGui.TextDisabled("Templates support <theme>, <djs>, <date>, and <time>. Generated opening text can be shortened separately in Openings.");
            foreach (var template in view.Templates.Where(value => value.ChannelCode == "partyfinder"))
                DrawTemplate(venue, template, busy);
            DrawDevSettings(venue, view, snapshot.EstimatedServerNow);
        }

    }

    private void DrawActivePublication(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse view,
        DateTimeOffset serverNow,
        bool busy)
    {
        ImGui.TextUnformatted("Current Party Finder text");
        ImGui.Separator();
        var active = PartyFinderPublicationSelector.Resolve(view, serverNow, VenueTimeZone.Resolve(venue));
        if (active is null)
        {
            ImGui.TextDisabled("No generated Party Finder text currently applies.");
            if (plugin.PartyFinderAutomation.IsRunning && plugin.PartyFinderAutomation.ProfileId == venue.ProfileId)
                plugin.PartyFinderAutomation.Stop("Party Finder refresher stopped because no PartyPulse text currently applies.");
            return;
        }

        ImGui.TextUnformatted($"Opening #{active.OpeningId} — {active.DisplayName}");
        ImGui.TextDisabled($"{VenueTimeZone.Format(venue, active.OpensAt, "ddd yyyy-MM-dd HH:mm")} to {VenueTimeZone.Format(venue, active.ClosesAt, "yyyy-MM-dd HH:mm")}");
        ImGui.BeginChild("ActivePartyFinderText", new Vector2(0, 95 * ImGuiHelpers.GlobalScale), true);
        ImGui.TextWrapped(active.Text);
        ImGui.EndChild();
        var activeTextValid = !active.Text.Contains('\n') && !active.Text.Contains('\r') && active.Text.Length <= 192;
        ImGui.TextDisabled($"{active.Text.Length}/192 characters.");
        if (!activeTextValid)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "Shorten this to one line and at most 192 characters before starting Party Finder.");

        var startBusy = busy;
        plugin.PartyFinderCoordination.EnsureSettingsLoaded(venue, active.OpeningId);
        var devSettings = plugin.PartyFinderCoordination.GetSettings(venue, active.OpeningId);
        var loadingDevSettings = devSettings is null && plugin.PartyFinderCoordination.IsSettingsBusy(venue, active.OpeningId);
        startBusy |= loadingDevSettings;
        if (loadingDevSettings)
            ImGui.TextDisabled("Loading the renewal policy...");
        else if (devSettings is null && !string.IsNullOrWhiteSpace(
                     plugin.PartyFinderCoordination.GetSettingsMessage(venue, active.OpeningId)))
            ImGui.TextDisabled("Renewal settings unavailable. Start uses the regular refresh interval.");

        if (devSettings?.Enabled == true)
        {
            var effectiveStrategy = devSettings.MaximumSpacingMinutes == 0
                ? PartyFinderDevStrategies.ExpiryOnly
                : devSettings.Strategy;
            ImGui.TextDisabled($"Coordinated renewal · {DevStrategyLabel(effectiveStrategy)}");
        }
        else
            DrawRefreshInterval();

        var automation = plugin.PartyFinderAutomation;
        var runningHere = automation.IsRunning && automation.ProfileId == venue.ProfileId;
        ImGui.BeginDisabled(startBusy || automation.IsRunning || !activeTextValid);
        if (ImGui.Button("Start Party Finder"))
        {
            if (!automation.Start(
                    venue,
                    active,
                    plugin.Configuration.PartyFinderRefreshMinutes,
                    out var error))
                Plugin.ChatGui.PrintError(error, "PartyPulse");
            else
                Plugin.ChatGui.Print(automation.StatusMessage, "PartyPulse");
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!automation.IsRunning);
        if (ImGui.Button("Stop"))
        {
            automation.Stop();
            Plugin.ChatGui.Print("Party Finder refresher stopped.", "PartyPulse");
        }
        ImGui.EndDisabled();

        if (automation.IsRunning)
        {
            ImGui.TextWrapped(automation.StatusMessage);
            if (!runningHere)
                ImGui.TextDisabled("The refresher is currently attached to another saved venue.");
        }
        else if (!string.IsNullOrWhiteSpace(automation.StatusMessage))
        {
            ImGui.TextDisabled(automation.StatusMessage);
        }
    }

    private void DrawRefreshInterval()
    {
        var minutes = plugin.Configuration.PartyFinderRefreshMinutes;
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Refresh every (minutes)", ref minutes))
        {
            plugin.Configuration.PartyFinderRefreshMinutes = Math.Clamp(minutes, 1, 1440);
            plugin.Configuration.Save();
        }
    }

    private void DrawDevSettings(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse view,
        DateTimeOffset serverNow)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Party Finder scheduling");

        var openings = view.Openings
            .Where(opening => opening.ClosesAt > serverNow)
            .OrderBy(opening => opening.OpensAt)
            .ThenBy(opening => opening.OpeningId)
            .ToArray();
        if (openings.Length == 0)
        {
            ImGui.TextDisabled("No current or upcoming openings.");
            return;
        }

        var selected = openings.FirstOrDefault(opening => opening.OpeningId == devSettingsOpeningId) ?? openings[0];
        if (ImGui.BeginCombo("Opening##PartyFinderCoordination", OpeningLabel(venue, selected)))
        {
            foreach (var opening in openings)
            {
                if (ImGui.Selectable(OpeningLabel(venue, opening), selected.OpeningId == opening.OpeningId))
                    selected = opening;
            }
            ImGui.EndCombo();
        }

        if (devSettingsOpeningId != selected.OpeningId)
        {
            devSettingsOpeningId = selected.OpeningId;
            savedDevSettings = null;
            devSettingsDraft = null;
        }

        var coordinator = plugin.PartyFinderCoordination;
        var openingId = selected.OpeningId;
        coordinator.EnsureSettingsLoaded(venue, openingId);
        var settings = coordinator.GetSettings(venue, openingId);
        var message = coordinator.GetSettingsMessage(venue, openingId);
        var isBusy = coordinator.IsSettingsBusy(venue, openingId);
        if (!string.IsNullOrWhiteSpace(message))
            ImGui.TextWrapped(message);
        if (settings is null)
        {
            if (isBusy)
                ImGui.TextDisabled("Loading Party Finder scheduling settings...");
            return;
        }

        var draft = devSettingsDraft is null || devSettingsDraft == savedDevSettings
            ? settings
            : devSettingsDraft;
        savedDevSettings = settings;

        ImGui.BeginDisabled(isBusy);
        var enabled = draft.Enabled;
        if (ImGui.Checkbox("Coordinate renewals for this opening", ref enabled))
            draft = draft with { Enabled = enabled };

        if (ImGui.BeginCombo("Renewal strategy", DevStrategyLabel(draft.Strategy)))
        {
            foreach (var strategy in DevStrategies)
            {
                if (ImGui.Selectable(strategy.Label, draft.Strategy == strategy.Code))
                    draft = draft with { Strategy = strategy.Code };
            }
            ImGui.EndCombo();
        }

        ImGui.BeginDisabled(draft.Strategy == PartyFinderDevStrategies.ExpiryOnly);
        var spacing = draft.MaximumSpacingMinutes;
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Maximum spacing", spacing == 0 ? "Disabled (0)" : $"{spacing} min"))
        {
            if (ImGui.Selectable("Disabled (0)", spacing == 0))
                draft = draft with { MaximumSpacingMinutes = 0 };
            for (var minutes = 10; minutes <= 60; minutes++)
            {
                if (ImGui.Selectable($"{minutes} min", spacing == minutes))
                    draft = draft with { MaximumSpacingMinutes = minutes };
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();

        var notify = draft.NotifyAtVenue;
        if (ImGui.Checkbox("Remind Party Finder users to start when at the venue", ref notify))
            draft = draft with { NotifyAtVenue = notify };
        ImGui.TextDisabled("From one hour before opening until closing.");
        devSettingsDraft = draft;

        ImGui.BeginDisabled(draft == settings);
        if (ImGui.Button("Save Party Finder scheduling settings"))
            coordinator.SaveSettings(venue, openingId, draft);
        ImGui.EndDisabled();
        ImGui.EndDisabled();
    }

    private static string OpeningLabel(VenueConnectionConfiguration venue, OpeningPublicationOpeningSummary opening) =>
        $"#{opening.OpeningId} — {VenueTimeZone.Format(venue, opening.OpensAt, "ddd yyyy-MM-dd HH:mm")}";

    private static string DevStrategyLabel(string strategyCode) =>
        DevStrategies.FirstOrDefault(strategy => strategy.Code == strategyCode).Label ?? strategyCode;

    private void DrawTemplate(
        VenueConnectionConfiguration venue,
        OpeningPublicationTemplateSummary template,
        bool busy)
    {
        drafts.TryAdd(template.PublicationCode, template.TemplateText ?? string.Empty);
        var value = drafts[template.PublicationCode];
        ImGui.Spacing();
        ImGui.TextUnformatted(template.DisplayName);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline(
                $"##PartyFinderTemplate{template.PublicationCode}",
                ref value,
                4000,
                new Vector2(0, 70 * ImGuiHelpers.GlobalScale)))
        {
            drafts[template.PublicationCode] = value;
            dirty.Add(template.PublicationCode);
        }
        var valid = ShoutrunnerTabRenderer.Validate(value, template.MaxLines, template.MaxLineLength, out var lines, out var longest);
        ImGui.TextDisabled($"{lines}/{template.MaxLines} lines; {longest}/{template.MaxLineLength} characters.");
        ImGui.BeginDisabled(busy || !valid || !dirty.Contains(template.PublicationCode));
        if (ImGui.Button($"Save {template.DisplayName} template##Save{template.PublicationCode}"))
        {
            plugin.SaveOpeningPublicationTemplate(venue, template.PublicationCode, value);
            dirty.Remove(template.PublicationCode);
        }
        ImGui.EndDisabled();
    }

    private void SyncDrafts(
        VenueConnectionConfiguration venue,
        DateTimeOffset? receivedAt,
        OpeningPublicationContextResponse? view)
    {
        if (activeProfileId != venue.ProfileId)
        {
            activeProfileId = venue.ProfileId;
            drafts.Clear();
            dirty.Clear();
            lastReceivedAt = null;
            devSettingsOpeningId = null;
            savedDevSettings = null;
            devSettingsDraft = null;
        }
        if (view is null || receivedAt == lastReceivedAt) return;
        foreach (var template in view.Templates.Where(value => value.ChannelCode == "partyfinder"))
        {
            if (!dirty.Contains(template.PublicationCode))
                drafts[template.PublicationCode] = template.TemplateText ?? string.Empty;
        }
        lastReceivedAt = receivedAt;
    }
}
