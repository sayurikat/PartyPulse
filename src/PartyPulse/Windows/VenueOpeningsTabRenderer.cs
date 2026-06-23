using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.VenueOpenings;

namespace PartyPulse.Windows;

public sealed class VenueOpeningsTabRenderer(Plugin plugin)
{
    private const string LocalDateTimeFormat = "yyyy-MM-dd HH:mm";
    private Guid activeProfileId;
    private bool draftInitialized;
    private long? editingOpeningId;
    private string startsAtLocal = string.Empty;
    private int durationMinutes = 480;
    private string addressWorldName = string.Empty;
    private string addressCityName = string.Empty;
    private int addressWard = 1;
    private int addressPlot = 1;
    private string themeName = string.Empty;
    private string openingTitle = string.Empty;
    private long? pendingCancelOpeningId;
    private long? pendingCloseOpeningId;
    private bool requestCancelPopup;
    private bool requestClosePopup;

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenueChange(venue);
        plugin.EnsureVenueOpeningsLoaded(venue);

        var snapshot = plugin.VenueOpenings.GetSnapshot(venue);
        var view = snapshot.View;
        if (view is not null && !view.Capabilities.CanManage)
            return;

        if (!ImGui.BeginTabItem("Openings"))
            return;

        var isBusy = plugin.VenueOpenings.IsBusy(venue.ProfileId);
        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh schedule"))
            plugin.RefreshVenueOpenings(venue);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("Opening times are entered in your local time and stored as UTC.");

        if (view is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        EnsureDraft(view);
        DrawEditor(venue, view, isBusy);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSchedule(venue, view, isBusy);
        DrawConfirmationPopups(venue, isBusy);
        ImGui.EndTabItem();
    }

    private void DrawEditor(
        VenueConnectionConfiguration venue,
        VenueOpeningScheduleResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted(editingOpeningId is null ? "Schedule opening" : $"Edit opening #{editingOpeningId}");

        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Starts (local)", ref startsAtLocal, 17);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Format: {LocalDateTimeFormat}");

        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Duration (minutes)", ref durationMinutes);
        durationMinutes = Math.Clamp(durationMinutes, 30, 2880);

        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Theme", string.IsNullOrWhiteSpace(themeName) ? "Select or enter a theme" : themeName))
        {
            foreach (var theme in view.Themes.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var selected = string.Equals(themeName, theme.Name, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{theme.Name}##OpeningTheme{theme.ThemeId}", selected))
                    themeName = theme.Name;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("New/manual theme", ref themeName, 100);
        ImGui.TextDisabled("A new theme is saved for future opening dropdowns when this opening is saved.");

        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Title (optional)", ref openingTitle, 100);

        ImGui.TextUnformatted("Address");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("World", ref addressWorldName, 50);
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Housing district", ref addressCityName, 50);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Ward", ref addressWard);
        addressWard = Math.Clamp(addressWard, 1, 30);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Plot", ref addressPlot);
        addressPlot = Math.Clamp(addressPlot, 1, 60);

        if (view.DefaultAddress is not null)
        {
            if (ImGui.SmallButton("Use registered venue address"))
                LoadAddress(view.DefaultAddress);
            ImGui.SameLine();
            ImGui.TextDisabled(view.DefaultAddress.DisplayText);
        }

        var validStart = TryParseLocalDateTime(startsAtLocal, out var startsAt, out var dateError);
        var closesAt = validStart ? startsAt.AddMinutes(durationMinutes) : default;
        var valid = validStart &&
                    durationMinutes is >= 30 and <= 2880 &&
                    !string.IsNullOrWhiteSpace(themeName) &&
                    !string.IsNullOrWhiteSpace(addressWorldName) &&
                    !string.IsNullOrWhiteSpace(addressCityName) &&
                    addressWard is >= 1 and <= 30 &&
                    addressPlot is >= 1 and <= 60;

        if (validStart)
            ImGui.TextDisabled($"Ends locally: {closesAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        else
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), dateError);

        ImGui.BeginDisabled(isBusy || !valid);
        if (ImGui.Button(editingOpeningId is null ? "Schedule opening" : "Save changes"))
        {
            plugin.SaveVenueOpening(
                venue,
                editingOpeningId,
                new SaveVenueOpeningRequest(
                    startsAt.ToUniversalTime(),
                    closesAt.ToUniversalTime(),
                    addressWorldName.Trim(),
                    addressCityName.Trim(),
                    addressWard,
                    addressPlot,
                    themeName.Trim(),
                    string.IsNullOrWhiteSpace(openingTitle) ? null : openingTitle.Trim()));
            editingOpeningId = null;
            draftInitialized = false;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(editingOpeningId is null ? "Reset suggestion" : "Cancel edit"))
        {
            editingOpeningId = null;
            LoadSuggestedDraft(view);
        }
    }

    private void DrawSchedule(
        VenueConnectionConfiguration venue,
        VenueOpeningScheduleResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("Opening schedule");
        var now = DateTimeOffset.UtcNow;
        var openings = view.Openings
            .OrderBy(value => value.OpensAt)
            .ThenBy(value => value.OpeningId)
            .ToArray();

        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("VenueOpeningSchedule", 6, flags, new Vector2(0, 270 * ImGuiHelpers.GlobalScale)))
            return;

        ImGui.TableSetupColumn("When");
        ImGui.TableSetupColumn("Theme");
        ImGui.TableSetupColumn("Address");
        ImGui.TableSetupColumn("Title");
        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var opening in openings)
        {
            var isFuture = !opening.IsCancelled && opening.OpensAt > now;
            var isActive = !opening.IsCancelled && opening.OpensAt <= now && opening.ClosesAt > now;
            var state = opening.IsCancelled
                ? "Cancelled"
                : isFuture
                    ? "Scheduled"
                    : isActive
                        ? "Open now"
                        : "Finished";

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted($"{opening.OpensAt.ToLocalTime():ddd yyyy-MM-dd HH:mm}");
            ImGui.TextDisabled($"to {opening.ClosesAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(opening.ThemeName ?? "No theme");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(opening.Address.DisplayText);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(opening.Title ?? string.Empty);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(state);
            ImGui.TableSetColumnIndex(5);
            ImGui.PushID(opening.OpeningId.ToString());
            ImGui.BeginDisabled(isBusy);
            if (isFuture)
            {
                if (ImGui.SmallButton("Edit"))
                    LoadOpeningDraft(opening);
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel"))
                {
                    pendingCancelOpeningId = opening.OpeningId;
                    requestCancelPopup = true;
                }
            }
            else if (isActive)
            {
                if (ImGui.SmallButton("Close now"))
                {
                    pendingCloseOpeningId = opening.OpeningId;
                    requestClosePopup = true;
                }
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawConfirmationPopups(VenueConnectionConfiguration venue, bool isBusy)
    {
        if (requestCancelPopup)
        {
            requestCancelPopup = false;
            ImGui.OpenPopup("Cancel scheduled opening###PartyPulseCancelScheduledOpening");
        }
        if (requestClosePopup)
        {
            requestClosePopup = false;
            ImGui.OpenPopup("Close active opening###PartyPulseCloseActiveOpening");
        }

        if (ImGui.BeginPopupModal("Cancel scheduled opening###PartyPulseCancelScheduledOpening", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Cancel this future opening? Existing historical records are kept, but the opening will no longer become active.");
            ImGui.BeginDisabled(isBusy || pendingCancelOpeningId is null);
            if (ImGui.Button("Cancel opening"))
            {
                plugin.CancelVenueOpening(venue, pendingCancelOpeningId!.Value);
                pendingCancelOpeningId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Keep opening"))
            {
                pendingCancelOpeningId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("Close active opening###PartyPulseCloseActiveOpening", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Close this opening immediately? Arrival tracking for this opening will stop.");
            ImGui.BeginDisabled(isBusy || pendingCloseOpeningId is null);
            if (ImGui.Button("Close now"))
            {
                plugin.CloseScheduledVenueOpening(venue, pendingCloseOpeningId!.Value);
                pendingCloseOpeningId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Keep open"))
            {
                pendingCloseOpeningId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void EnsureDraft(VenueOpeningScheduleResponse view)
    {
        if (!draftInitialized)
            LoadSuggestedDraft(view);
    }

    private void LoadSuggestedDraft(VenueOpeningScheduleResponse view)
    {
        draftInitialized = true;
        startsAtLocal = view.SuggestedOpensAt.ToLocalTime().ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        durationMinutes = Math.Clamp(view.SuggestedDurationMinutes, 30, 2880);
        openingTitle = string.Empty;
        themeName = view.Openings
            .Where(value => !string.IsNullOrWhiteSpace(value.ThemeName))
            .OrderByDescending(value => value.OpensAt)
            .Select(value => value.ThemeName!)
            .FirstOrDefault()
            ?? view.Themes.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Name
            ?? string.Empty;
        if (view.DefaultAddress is not null)
            LoadAddress(view.DefaultAddress);
    }

    private void LoadOpeningDraft(VenueOpeningScheduleItem opening)
    {
        draftInitialized = true;
        editingOpeningId = opening.OpeningId;
        startsAtLocal = opening.OpensAt.ToLocalTime().ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        durationMinutes = Math.Clamp((int)Math.Round(opening.Duration.TotalMinutes), 30, 2880);
        addressWorldName = opening.Address.WorldName;
        addressCityName = opening.Address.CityName;
        addressWard = opening.Address.Ward;
        addressPlot = opening.Address.Plot;
        themeName = opening.ThemeName ?? string.Empty;
        openingTitle = opening.Title ?? string.Empty;
    }

    private void LoadAddress(VenueOpeningAddressSummary address)
    {
        addressWorldName = address.WorldName;
        addressCityName = address.CityName;
        addressWard = address.Ward;
        addressPlot = address.Plot;
    }

    private void ResetForVenueChange(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
            return;
        activeProfileId = venue.ProfileId;
        draftInitialized = false;
        editingOpeningId = null;
        pendingCancelOpeningId = null;
        pendingCloseOpeningId = null;
    }

    private static bool TryParseLocalDateTime(
        string value,
        out DateTimeOffset dateTimeOffset,
        out string error)
    {
        dateTimeOffset = default;
        if (!DateTime.TryParseExact(
                value.Trim(),
                LocalDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDateTime))
        {
            error = $"Start time must use {LocalDateTimeFormat}.";
            return false;
        }

        localDateTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localDateTime))
        {
            error = "That local time does not exist because of a daylight-saving transition.";
            return false;
        }

        var offset = TimeZoneInfo.Local.GetUtcOffset(localDateTime);
        dateTimeOffset = new DateTimeOffset(localDateTime, offset);
        error = string.Empty;
        return true;
    }
}
