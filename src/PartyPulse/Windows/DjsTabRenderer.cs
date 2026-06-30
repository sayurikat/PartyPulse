using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;

namespace PartyPulse.Windows;

public sealed class DjsTabRenderer(Plugin plugin)
{
    private Guid activeProfileId;
    private long? editingDjId;
    private string name = string.Empty;
    private string twitchUrl = string.Empty;
    private bool resident;
    private string note = string.Empty;
    private long? pendingArchiveDjId;
    private bool requestArchivePopup;
    private bool settingsInitialized;
    private string defaultHourlyRateGil = "0";
    private long selectedLinkDjId;

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenueChange(venue);
        plugin.EnsureDjsLoaded(venue);

        var snapshot = plugin.Djs.GetSnapshot(venue);
        var view = snapshot.View;
        if (view is not null && !view.Capabilities.CanManageDirectory)
            return;

        PartyPulseUi.PageHeader("DJs", "Manage the venue DJ directory, linked characters, notes, and default hourly pricing.");

        var isBusy = plugin.Djs.IsBusy(venue.ProfileId);
        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh DJs"))
            plugin.RefreshDjs(venue);
        ImGui.EndDisabled();

        if (view is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        EnsureSettingsDraft(view);
        DrawVenueSettings(venue, view, isBusy);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawEditor(venue, isBusy);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawCharacterLinks(venue, view, isBusy);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawDirectory(venue, view, isBusy);
        DrawArchivePopup(venue, isBusy);
    }

    private void DrawVenueSettings(
        VenueConnectionConfiguration venue,
        DjViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("Venue DJ pricing");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Default price per hour (gil)", ref defaultHourlyRateGil, 16);

        var valid = long.TryParse(
                        defaultHourlyRateGil,
                        NumberStyles.Integer | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture,
                        out var parsedRate) &&
                    parsedRate is >= 0 and <= int.MaxValue;
        if (valid)
            ImGui.TextDisabled($"Saved value: {view.DefaultHourlyRateGil:N0} gil/hour. New DJ slots suggest a total from this rate and their duration.");
        else
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), $"Enter a whole amount from 0 to {int.MaxValue:N0} gil.");

        ImGui.BeginDisabled(isBusy || !valid || parsedRate == view.DefaultHourlyRateGil);
        if (ImGui.Button("Save default DJ rate"))
            plugin.UpdateDjSettings(venue, new UpdateDjSettingsRequest(parsedRate));
        ImGui.EndDisabled();
    }

    private void DrawEditor(VenueConnectionConfiguration venue, bool isBusy)
    {
        ImGui.TextUnformatted(editingDjId is null ? "Register DJ" : $"Edit DJ #{editingDjId}");

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Name", ref name, 100);
        ImGui.SetNextItemWidth(420 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Twitch link", ref twitchUrl, 500);
        ImGui.Checkbox("Resident DJ", ref resident);
        ImGui.TextUnformatted("Notes");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##DjNotes", ref note, 2000, new Vector2(0, 85 * ImGuiHelpers.GlobalScale));

        var valid = !string.IsNullOrWhiteSpace(name) &&
                    (string.IsNullOrWhiteSpace(twitchUrl) ||
                     Uri.TryCreate(twitchUrl.Trim(), UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                     (string.Equals(uri.Host, "twitch.tv", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(uri.Host, "www.twitch.tv", StringComparison.OrdinalIgnoreCase)));

        ImGui.BeginDisabled(isBusy || !valid);
        if (ImGui.Button(editingDjId is null ? "Register DJ" : "Save DJ"))
        {
            plugin.SaveDj(
                venue,
                editingDjId,
                new SaveDjRequest(
                    name.Trim(),
                    string.IsNullOrWhiteSpace(twitchUrl) ? null : twitchUrl.Trim(),
                    resident,
                    string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
            ClearDraft();
        }
        ImGui.EndDisabled();

        if (editingDjId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel edit"))
                ClearDraft();
        }
    }

    private void DrawCharacterLinks(
        VenueConnectionConfiguration venue,
        DjViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("DJ characters");
        ImGui.TextDisabled("Target a player character, choose the DJ, then link it. A DJ may have multiple characters.");

        if (view.Djs.Count == 0)
        {
            ImGui.TextDisabled("Register a DJ before assigning characters.");
            return;
        }

        var selectedDj = view.Djs.FirstOrDefault(value => value.DjId == selectedLinkDjId) ?? view.Djs[0];
        if (selectedLinkDjId <= 0)
            selectedLinkDjId = selectedDj.DjId;

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Assign target to DJ", selectedDj.Name))
        {
            foreach (var dj in view.Djs.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var selected = dj.DjId == selectedLinkDjId;
                if (ImGui.Selectable($"{dj.Name}##DjCharacterLink{dj.DjId}", selected))
                    selectedLinkDjId = dj.DjId;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var hasTarget = plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetReason);
        ImGui.SameLine();
        ImGui.BeginDisabled(isBusy || !hasTarget);
        if (ImGui.Button("Link current target") && target is not null)
        {
            plugin.LinkDjCharacter(
                venue,
                new LinkDjCharacterRequest(
                    selectedLinkDjId,
                    target.CharacterName,
                    target.WorldName));
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(hasTarget && target is not null ? target.DisplayName : targetReason);

        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("DjCharacterLinkTable", 4, flags))
            return;

        ImGui.TableSetupColumn("DJ");
        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("World");
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var character in view.Characters
                     .OrderBy(
                         value => view.Djs.FirstOrDefault(dj => dj.DjId == value.DjId)?.Name ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.CharacterName, StringComparer.OrdinalIgnoreCase))
        {
            var djName = view.Djs.FirstOrDefault(value => value.DjId == character.DjId)?.Name ?? $"DJ #{character.DjId}";
            ImGui.PushID($"dj-character-{character.CharacterId}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(djName);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(character.CharacterName);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(character.WorldName);
            ImGui.TableSetColumnIndex(3);
            ImGui.BeginDisabled(isBusy);
            if (ImGui.SmallButton("Unlink"))
            {
                plugin.LinkDjCharacter(
                    venue,
                    new LinkDjCharacterRequest(
                        null,
                        character.CharacterName,
                        character.WorldName));
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawDirectory(
        VenueConnectionConfiguration venue,
        DjViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("DJ directory");
        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("DjDirectoryTable", 5, flags, new Vector2(0, 300 * ImGuiHelpers.GlobalScale)))
            return;

        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Resident", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Twitch");
        ImGui.TableSetupColumn("Note");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var dj in view.Djs
                     .OrderByDescending(value => value.Resident)
                     .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            ImGui.PushID($"dj-{dj.DjId}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(dj.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(dj.Resident ? "Yes" : "No");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(dj.TwitchUrl ?? "Not recorded");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(dj.Note ?? string.Empty);
            ImGui.TableSetColumnIndex(4);
            ImGui.BeginDisabled(isBusy);
            if (ImGui.SmallButton("Edit"))
                LoadDraft(dj);
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
            {
                pendingArchiveDjId = dj.DjId;
                requestArchivePopup = true;
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawArchivePopup(VenueConnectionConfiguration venue, bool isBusy)
    {
        if (requestArchivePopup)
        {
            requestArchivePopup = false;
            ImGui.OpenPopup("Delete DJ###PartyPulseArchiveDj");
        }

        if (!ImGui.BeginPopupModal("Delete DJ###PartyPulseArchiveDj", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped("Delete this DJ from the active directory? Existing booking and status history is preserved for statistics.");
        ImGui.BeginDisabled(isBusy || pendingArchiveDjId is null);
        if (ImGui.Button("Delete DJ"))
        {
            plugin.ArchiveDj(venue, pendingArchiveDjId!.Value);
            if (editingDjId == pendingArchiveDjId)
                ClearDraft();
            pendingArchiveDjId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Keep"))
        {
            pendingArchiveDjId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void EnsureSettingsDraft(DjViewResponse view)
    {
        if (settingsInitialized)
            return;

        defaultHourlyRateGil = view.DefaultHourlyRateGil.ToString(CultureInfo.InvariantCulture);
        selectedLinkDjId = view.Djs.FirstOrDefault()?.DjId ?? 0;
        settingsInitialized = true;
    }

    private void LoadDraft(DjSummary dj)
    {
        editingDjId = dj.DjId;
        name = dj.Name;
        twitchUrl = dj.TwitchUrl ?? string.Empty;
        resident = dj.Resident;
        note = dj.Note ?? string.Empty;
    }

    private void ClearDraft()
    {
        editingDjId = null;
        name = string.Empty;
        twitchUrl = string.Empty;
        resident = false;
        note = string.Empty;
    }

    private void ResetForVenueChange(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
            return;

        activeProfileId = venue.ProfileId;
        ClearDraft();
        pendingArchiveDjId = null;
        requestArchivePopup = false;
        settingsInitialized = false;
        defaultHourlyRateGil = "0";
        selectedLinkDjId = 0;
    }
}
