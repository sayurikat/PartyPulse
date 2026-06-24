using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.PartyFinder;

namespace PartyPulse.Windows;

public sealed class PartyFinderTabRenderer(Plugin plugin)
{
    private readonly Dictionary<string, string> drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dirty = new(StringComparer.OrdinalIgnoreCase);
    private Guid activeProfileId;
    private DateTimeOffset? lastReceivedAt;

    public void Draw(VenueConnectionConfiguration venue)
    {
        plugin.EnsureOpeningPublicationsLoaded(venue);
        var snapshot = plugin.OpeningPublications.GetSnapshot(venue);
        var view = snapshot.View;
        if (view is not null && !view.Capabilities.CanUsePartyFinder)
            return;
        if (!ImGui.BeginTabItem("Party Finder"))
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
            ImGui.EndTabItem();
            return;
        }

        DrawActivePublication(venue, view, snapshot.EstimatedServerNow, busy);

        if (view.Capabilities.CanManagePartyFinderTemplates &&
            ImGui.CollapsingHeader("Party Finder template editor"))
        {
            ImGui.TextDisabled("Templates support <theme> and <djs>. Generated opening text can be shortened separately in Openings.");
            foreach (var template in view.Templates.Where(value => value.ChannelCode == "partyfinder"))
                DrawTemplate(venue, template, busy);
        }

        ImGui.EndTabItem();
    }

    private void DrawActivePublication(
        VenueConnectionConfiguration venue,
        OpeningPublicationContextResponse view,
        DateTimeOffset serverNow,
        bool busy)
    {
        ImGui.TextUnformatted("Current Party Finder text");
        ImGui.Separator();
        var active = PartyFinderPublicationSelector.Resolve(view, serverNow);
        if (active is null)
        {
            ImGui.TextDisabled("No generated Party Finder text currently applies.");
            if (plugin.PartyFinderAutomation.IsRunning && plugin.PartyFinderAutomation.ProfileId == venue.ProfileId)
                plugin.PartyFinderAutomation.Stop("Party Finder refresher stopped because no PartyPulse text currently applies.");
            return;
        }

        ImGui.TextUnformatted($"Opening #{active.OpeningId} — {active.DisplayName}");
        ImGui.TextDisabled($"{active.OpensAt.ToLocalTime():ddd yyyy-MM-dd HH:mm} to {active.ClosesAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        ImGui.BeginChild("ActivePartyFinderText", new Vector2(0, 95 * ImGuiHelpers.GlobalScale), true);
        ImGui.TextWrapped(active.Text);
        ImGui.EndChild();
        var activeTextValid = !active.Text.Contains('\n') && !active.Text.Contains('\r') && active.Text.Length <= 192;
        ImGui.TextDisabled($"{active.Text.Length}/192 characters.");
        if (!activeTextValid)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "Shorten this to one line and at most 192 characters before starting Party Finder.");

        var minutes = plugin.Configuration.PartyFinderRefreshMinutes;
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Refresh every (minutes)", ref minutes))
        {
            plugin.Configuration.PartyFinderRefreshMinutes = Math.Clamp(minutes, 1, 1440);
            plugin.Configuration.Save();
        }

        var automation = plugin.PartyFinderAutomation;
        var runningHere = automation.IsRunning && automation.ProfileId == venue.ProfileId;
        ImGui.BeginDisabled(busy || automation.IsRunning || !activeTextValid);
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
