using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Djs;
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

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenueChange(venue);
        plugin.EnsureDjsLoaded(venue);

        var snapshot = plugin.Djs.GetSnapshot(venue);
        var view = snapshot.View;
        if (view is not null && !view.Capabilities.CanManageDirectory)
            return;

        if (!ImGui.BeginTabItem("DJs"))
            return;

        var isBusy = plugin.Djs.IsBusy(venue.ProfileId);
        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh DJs"))
            plugin.RefreshDjs(venue);
        ImGui.EndDisabled();

        if (view is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        DrawEditor(venue, isBusy);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawDirectory(venue, view, isBusy);
        DrawArchivePopup(venue, isBusy);
        ImGui.EndTabItem();
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
    }
}
