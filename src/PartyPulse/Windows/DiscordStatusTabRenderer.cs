using System;
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
    private string openMessage = DiscordVenueStatusDefaults.OpenMessage;
    private string closedMessage = DiscordVenueStatusDefaults.ClosedMessage;

    public void Draw(VenueConnectionConfiguration venue)
    {
        plugin.EnsureDiscordStatusLoaded(venue);
        var snapshot = plugin.DiscordStatus.GetSnapshot(venue);
        var view = snapshot.View;
        var isBusy = plugin.DiscordStatus.IsBusy(venue.ProfileId);

        PartyPulseUi.PageHeader(
            "Discord Venue Status",
            "Post the current opening to one Discord channel and keep that same message updated until the venue closes.");

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

        ImGui.TextDisabled("Placeholders: <theme>, <title>, and <address>. Matching is case-insensitive.");

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
                OpenMessage = openMessage,
                ClosedMessage = closedMessage,
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
            openMessage = DiscordVenueStatusDefaults.OpenMessage;
            closedMessage = DiscordVenueStatusDefaults.ClosedMessage;
            dirty = true;
        }

        DrawCurrentPublication(venue, view.VenueStatus.CurrentPublication);
    }

    private void DrawChannelCombo(DiscordChannelSummary[] postableChannels)
    {
        var selected = postableChannels.FirstOrDefault(channel => channel.ChannelId == channelId);
        var preview = selected is not null
            ? $"#{selected.Name}"
            : channelId > 0
                ? $"Unavailable channel ({channelId})"
                : "Select a channel";

        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo("Channel", preview))
        {
            return;
        }

        foreach (var channel in postableChannels)
        {
            var isSelected = channel.ChannelId == channelId;
            if (ImGui.Selectable($"#{channel.Name}", isSelected))
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
            "Current opening",
            "The bot refreshes PartyPulse details about once per minute while the opening is active.");

        if (publication is null)
        {
            ImGui.TextDisabled("No venue-status publication exists for an active opening.");
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
        openMessage = settings.OpenMessage;
        closedMessage = settings.ClosedMessage;
        dirty = false;
    }
}
