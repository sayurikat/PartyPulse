using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.DiscordStatus;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class DiscordStatusTabRenderer(Plugin plugin)
{
    private Guid draftProfileId;
    private DateTimeOffset? draftReceivedAt;
    private DateTimeOffset? saveRequestedAt;
    private bool dirty;
    private bool enabled;
    private long channelId;
    private int preOpenMinutes = DiscordVenueStatusDefaults.PreOpenMinutes;
    private string preOpenMessage = DiscordVenueStatusDefaults.PreOpenMessage;
    private string openMessage = DiscordVenueStatusDefaults.OpenMessage;
    private string closedMessage = DiscordVenueStatusDefaults.ClosedMessage;
    private bool autoPublishAnnouncement;
    private bool mentionEveryone;
    private readonly HashSet<long> mentionRoleIds = [];

    public void Draw(VenueConnectionConfiguration venue)
    {
        plugin.EnsureDiscordStatusLoaded(venue);
        var snapshot = plugin.DiscordStatus.GetSnapshot(venue);
        var view = snapshot.View;
        var isBusy = plugin.DiscordStatus.IsBusy(venue.ProfileId);

        PartyPulseUi.PageHeader(
            "Discord Venue Status",
            "Post an upcoming or current opening to one Discord channel and keep that same message updated until the venue closes.");

        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh status"))
        {
            plugin.RefreshDiscordStatus(venue);
        }
        ImGui.EndDisabled();

        if (view?.VenueStatus is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        if (!view.Capabilities.CanManage)
        {
            ImGui.TextWrapped("You do not have permission to manage Discord venue status.");
            return;
        }

        ResolveSaveAttempt(snapshot);
        ResetDraftWhenAppropriate(venue, snapshot, view.VenueStatus);

        if (snapshot.Status == DiscordStatusManagementStatus.Failed)
        {
            ImGui.TextColored(PartyPulseUi.Warning, snapshot.Message);
        }

        if (view.Guild is null)
        {
            ImGui.TextColored(
                PartyPulseUi.Warning,
                "Link a Discord server before enabling venue-status messages.");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Linked to {view.Guild.GuildName}");
        }

        var postableChannels = view.Channels
            .Where(static channel => channel.CanPost)
            .OrderBy(static channel => channel.Position)
            .ThenBy(static channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (view.Guild is not null && postableChannels.Length == 0)
        {
            ImGui.TextColored(
                PartyPulseUi.Warning,
                "No postable channel is available. Check the bot's channel permissions and wait for metadata synchronization.");
        }

        PartyPulseUi.SectionHeader(
            "Publication settings",
            "Channel changes apply to the next opening. A message already published for the current opening remains in its original channel.");

        if (ImGui.Checkbox("Enable Discord venue status", ref enabled))
        {
            dirty = true;
        }

        DrawChannelCombo(postableChannels);

        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Post before opening (minutes)", ref preOpenMinutes))
        {
            dirty = true;
        }
        ImGui.TextDisabled("Use 0 to post when the opening starts; the maximum is 1440 minutes.");

        ImGui.TextUnformatted("Pre-opening message");
        if (ImGui.InputTextMultiline(
                "##DiscordVenueStatusPreOpenMessage",
                ref preOpenMessage,
                2000,
                new Vector2(0, 90 * ImGuiHelpers.GlobalScale)))
        {
            dirty = true;
        }

        ImGui.TextUnformatted("Open message");
        if (ImGui.InputTextMultiline(
                "##DiscordVenueStatusOpenMessage",
                ref openMessage,
                2000,
                new Vector2(0, 90 * ImGuiHelpers.GlobalScale)))
        {
            dirty = true;
        }

        ImGui.TextUnformatted("Closed message");
        if (ImGui.InputTextMultiline(
                "##DiscordVenueStatusClosedMessage",
                ref closedMessage,
                2000,
                new Vector2(0, 75 * ImGuiHelpers.GlobalScale)))
        {
            dirty = true;
        }



        if (ImGui.Checkbox(
                "Automatically publish posts in announcement channels",
                ref autoPublishAnnouncement))
        {
            dirty = true;
        }
        ImGui.TextDisabled("This has no effect for ordinary text channels.");

        PartyPulseUi.SectionHeader(
            "Notifications",
            "Optionally notify @everyone and one or more Discord roles when the status post is first created.");

        if (ImGui.Checkbox("Notify @everyone", ref mentionEveryone))
        {
            dirty = true;
        }

        DrawMentionRoles(view.Roles, view.Guild?.GuildId);

        ImGui.Spacing();

        var validationError = Validate(view, postableChannels);
        if (validationError is not null)
        {
            ImGui.TextColored(PartyPulseUi.Warning, validationError);
        }

        ImGui.BeginDisabled(isBusy || !dirty || validationError is not null);
        if (ImGui.Button("Save settings"))
        {
            saveRequestedAt = DateTimeOffset.UtcNow;
            plugin.SaveDiscordStatus(venue, new SaveDiscordVenueStatusRequest
            {
                Enabled = enabled,
                ChannelId = channelId > 0 ? channelId : null,
                PreOpenMinutes = preOpenMinutes,
                PreOpenMessage = preOpenMessage,
                OpenMessage = openMessage,
                ClosedMessage = closedMessage,
                AutoPublishAnnouncement = autoPublishAnnouncement,
                MentionEveryone = mentionEveryone,
                MentionRoleIds = mentionRoleIds.OrderBy(static roleId => roleId).ToArray()
            });
            dirty = false;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(isBusy || !dirty);
        if (ImGui.Button("Reset saved values"))
        {
            ResetDraft(venue, snapshot.ReceivedAt, view.VenueStatus);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Use default messages"))
        {
            preOpenMessage = DiscordVenueStatusDefaults.PreOpenMessage;
            openMessage = DiscordVenueStatusDefaults.OpenMessage;
            closedMessage = DiscordVenueStatusDefaults.ClosedMessage;
            dirty = true;
        }

        DrawCurrentPublication(venue, view.VenueStatus.CurrentPublication);
    }

    private void DrawMentionRoles(IReadOnlyList<DiscordRoleSummary> roles, long? guildId)
    {
        if (!ImGui.CollapsingHeader("Mention Roles"))
            return;

        var availableRoles = roles
            .Where(role => !role.Managed && role.RoleId != guildId)
            .OrderByDescending(static role => role.Position)
            .ThenBy(static role => role.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ImGui.TextUnformatted("Roles to notify");
        var availableRoleIds = availableRoles.Select(static role => role.RoleId).ToHashSet();
        var unavailableSelectionCount = mentionRoleIds.Count(roleId => !availableRoleIds.Contains(roleId));
        if (unavailableSelectionCount > 0)
        {
            ImGui.TextColored(
                PartyPulseUi.Warning,
                $"{unavailableSelectionCount} saved role selection(s) are no longer available.");
        }

        ImGui.BeginDisabled(mentionRoleIds.Count == 0);
        if (ImGui.Button("Remove all role notifications"))
        {
            mentionRoleIds.Clear();
            dirty = true;
        }
        ImGui.EndDisabled();

        if (availableRoles.Length == 0)
        {
            ImGui.TextDisabled("No selectable Discord roles are available.");
            return;
        }

        foreach (var role in availableRoles)
        {
            var selected = mentionRoleIds.Contains(role.RoleId);
            var displayName = DiscordChannelDisplayName.ToAsciiLetters(role.Name);
            if (ImGui.Checkbox($"{displayName}##DiscordStatusMentionRole{role.RoleId}", ref selected))
            {
                if (selected)
                {
                    mentionRoleIds.Add(role.RoleId);
                }
                else
                {
                    mentionRoleIds.Remove(role.RoleId);
                }

                dirty = true;
            }
        }

    }

    private void DrawChannelCombo(DiscordChannelSummary[] postableChannels)
    {
        var selected = postableChannels.FirstOrDefault(channel => channel.ChannelId == channelId);
        var preview = selected is not null
            ? DiscordChannelDisplayName.ToAsciiLetters(selected.Name)
            : channelId > 0
                ? "Unavailable channel"
                : "Select a channel";

        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo("Channel", preview))
        {
            return;
        }

        foreach (var channel in postableChannels)
        {
            var isSelected = channel.ChannelId == channelId;
            var displayName = DiscordChannelDisplayName.ToAsciiLetters(channel.Name);
            if (ImGui.Selectable($"{displayName}##DiscordStatusChannel{channel.ChannelId}", isSelected))
            {
                channelId = channel.ChannelId;
                dirty = true;
            }

            if (isSelected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private string? Validate(
        DiscordManagementViewResponse view,
        System.Collections.Generic.IReadOnlyCollection<DiscordChannelSummary> postableChannels)
    {
        if (preOpenMinutes is < 0 or > 1440)
        {
            return "Pre-opening minutes must be between 0 and 1440.";
        }

        if (string.IsNullOrWhiteSpace(preOpenMessage))
        {
            return "Enter a pre-opening message.";
        }

        if (string.IsNullOrWhiteSpace(openMessage))
        {
            return "Enter an open message.";
        }

        if (string.IsNullOrWhiteSpace(closedMessage))
        {
            return "Enter a closed message.";
        }

        if (enabled && view.Guild is null)
        {
            return "Link a Discord server before enabling venue status.";
        }

        if (enabled && !postableChannels.Any(channel => channel.ChannelId == channelId))
        {
            return "Choose a channel where the bot can post messages.";
        }

        return null;
    }

    private static void DrawCurrentPublication(
        VenueConnectionConfiguration venue,
        DiscordVenueStatusPublicationSummary? publication)
    {
        PartyPulseUi.SectionHeader(
            "Current publication",
            "The bot refreshes PartyPulse details about once per minute before and during the opening.");

        if (publication is null)
        {
            ImGui.TextDisabled("No venue-status publication exists for an upcoming or active opening.");
            return;
        }

        ImGui.TextUnformatted($"State: {publication.PublicationState}");
        ImGui.TextUnformatted($"Channel: #{publication.ChannelName}");
        if (publication.LastPublishedAt is { } lastPublishedAt)
        {
            ImGui.TextDisabled($"Last published {VenueTimeZone.Format(venue, lastPublishedAt, "g")}");
        }
        else
        {
            ImGui.TextDisabled("Waiting for the Discord bot to publish the message.");
        }

        if (!string.IsNullOrWhiteSpace(publication.LastError))
        {
            ImGui.TextColored(PartyPulseUi.Warning, $"Last Discord error: {publication.LastError}");
        }
    }

    private void ResetDraftWhenAppropriate(
        VenueConnectionConfiguration venue,
        DiscordStatusManagementSnapshot snapshot,
        DiscordVenueStatusSettingsSummary settings)
    {
        if (draftProfileId != venue.ProfileId || (!dirty && draftReceivedAt != snapshot.ReceivedAt))
        {
            ResetDraft(venue, snapshot.ReceivedAt, settings);
        }
    }

    private void ResolveSaveAttempt(DiscordStatusManagementSnapshot snapshot)
    {
        if (saveRequestedAt is not { } requestedAt)
        {
            return;
        }

        if (snapshot.ReceivedAt >= requestedAt &&
            snapshot.Status == DiscordStatusManagementStatus.Ready)
        {
            saveRequestedAt = null;
            return;
        }

        if (snapshot.LastAttemptAt >= requestedAt &&
            snapshot.Status == DiscordStatusManagementStatus.Failed)
        {
            saveRequestedAt = null;
            dirty = true;
        }
    }

    private void ResetDraft(
        VenueConnectionConfiguration venue,
        DateTimeOffset? receivedAt,
        DiscordVenueStatusSettingsSummary settings)
    {
        draftProfileId = venue.ProfileId;
        draftReceivedAt = receivedAt;
        enabled = settings.Enabled;
        channelId = settings.ChannelId ?? 0;
        preOpenMinutes = settings.PreOpenMinutes;
        preOpenMessage = settings.PreOpenMessage;
        openMessage = settings.OpenMessage;
        closedMessage = settings.ClosedMessage;
        autoPublishAnnouncement = settings.AutoPublishAnnouncement;
        mentionEveryone = settings.MentionEveryone;
        mentionRoleIds.Clear();
        mentionRoleIds.UnionWith(settings.MentionRoleIds);
        dirty = false;
    }
}
