using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;

namespace PartyPulse.Windows;

public sealed class ShoutrunnerTabRenderer(Plugin plugin)
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
        if (view is not null && !view.Capabilities.CanManageShoutrunnerTemplates)
            return;
        if (!ImGui.BeginTabItem("Shoutrunner"))
            return;

        SyncDrafts(venue, snapshot.ReceivedAt, view, "shoutrunner");
        var busy = plugin.OpeningPublications.IsBusy(venue.ProfileId);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh Shoutrunner templates"))
            plugin.RefreshOpeningPublications(venue);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("Templates support <theme> and <djs>.");

        if (view is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        foreach (var template in view.Templates.Where(value => value.ChannelCode == "shoutrunner"))
            DrawTemplate(venue, template, busy);

        ImGui.EndTabItem();
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
