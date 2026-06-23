using System;
using System.Collections.Generic;
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

    private long? selectedDjOpeningId;
    private long? editingBookingId;
    private long selectedDjId;
    private string bookingStartsAtLocal = string.Empty;
    private string bookingEndsAtLocal = string.Empty;
    private string bookingStatusCode = DjBookingStatusCodes.Pending;
    private string bookingNote = string.Empty;
    private string bookingMacroOverride = string.Empty;
    private long? pendingDeleteBookingId;
    private long? pendingDeleteBookingOpeningId;
    private bool requestDeleteBookingPopup;

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenueChange(venue);
        plugin.EnsureVenueOpeningsLoaded(venue);
        plugin.EnsureDjsLoaded(venue);

        var snapshot = plugin.VenueOpenings.GetSnapshot(venue);
        var view = snapshot.View;
        if (view is not null && !view.Capabilities.CanManage)
            return;

        if (!ImGui.BeginTabItem("Openings"))
            return;

        var isBusy = plugin.VenueOpenings.IsBusy(venue.ProfileId);
        var djsBusy = plugin.Djs.IsBusy(venue.ProfileId);
        var djSnapshot = plugin.Djs.GetSnapshot(venue);
        var djView = djSnapshot.View;

        ImGui.BeginDisabled(isBusy || djsBusy);
        if (ImGui.Button("Refresh schedule"))
        {
            plugin.RefreshVenueOpenings(venue);
            plugin.RefreshDjs(venue);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("Opening and DJ times are entered in your local time and stored as UTC.");

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
        DrawSchedule(venue, view, djView, isBusy || djsBusy);

        if (selectedDjOpeningId is { } openingId)
        {
            var opening = view.Openings.FirstOrDefault(value => value.OpeningId == openingId);
            if (opening is not null)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawDjScheduleEditor(venue, opening, djSnapshot, isBusy || djsBusy);
            }
            else
            {
                selectedDjOpeningId = null;
                ClearBookingDraft();
            }
        }

        DrawConfirmationPopups(venue, isBusy || djsBusy);
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
        DjViewResponse? djView,
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
        if (!ImGui.BeginTable("VenueOpeningSchedule", 7, flags, new Vector2(0, 300 * ImGuiHelpers.GlobalScale)))
            return;

        ImGui.TableSetupColumn("When");
        ImGui.TableSetupColumn("Theme");
        ImGui.TableSetupColumn("Address");
        ImGui.TableSetupColumn("Title");
        ImGui.TableSetupColumn("DJs");
        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 215 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var opening in openings)
        {
            var isFuture = !opening.IsCancelled && opening.OpensAt > now;
            var isActive = !opening.IsCancelled && opening.OpensAt <= now && opening.ClosesAt > now;
            var canManageDjs = !opening.IsCancelled && opening.ClosesAt > now;
            var state = opening.IsCancelled
                ? "Cancelled"
                : isFuture
                    ? "Scheduled"
                    : isActive
                        ? "Open now"
                        : "Finished";
            var bookings = djView?.Bookings
                .Where(value => value.OpeningId == opening.OpeningId)
                .ToArray() ?? Array.Empty<DjBookingSummary>();
            var coverage = CalculateCoverage(opening, bookings);

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
            DrawCoverage(coverage);
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(state);
            ImGui.TableSetColumnIndex(6);
            ImGui.PushID(opening.OpeningId.ToString(CultureInfo.InvariantCulture));
            ImGui.BeginDisabled(isBusy);
            if (canManageDjs)
            {
                if (ImGui.SmallButton("DJs"))
                {
                    selectedDjOpeningId = opening.OpeningId;
                    InitializeBookingDraft(opening, bookings, djView);
                }
                ImGui.SameLine();
            }
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

    private void DrawDjScheduleEditor(
        VenueConnectionConfiguration venue,
        VenueOpeningScheduleItem opening,
        PartyPulse.Djs.DjManagementSnapshot snapshot,
        bool isBusy)
    {
        ImGui.TextUnformatted($"DJ schedule — opening #{opening.OpeningId}");
        ImGui.TextDisabled($"{opening.OpensAt.ToLocalTime():g} to {opening.ClosesAt.ToLocalTime():g}");

        var view = snapshot.View;
        if (view is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        var bookings = view.Bookings
            .Where(value => value.OpeningId == opening.OpeningId)
            .OrderBy(value => value.StartsAt)
            .ThenBy(value => value.BookingId)
            .ToArray();
        var coverage = CalculateCoverage(opening, bookings);
        DrawCoverage(coverage);
        ImGui.SameLine();
        ImGui.TextDisabled($"Confirmed {coverage.ConfirmedMinutes:N0}/{coverage.TotalMinutes:N0} minutes; pending {coverage.PendingMinutes:N0}; gap {coverage.GapMinutes:N0}.");

        if (!view.Capabilities.CanManageSchedule)
        {
            ImGui.TextDisabled("You do not have permission to manage opening DJ schedules.");
            return;
        }

        if (view.Djs.Count == 0)
        {
            ImGui.TextWrapped("Register at least one DJ in the DJs tab before adding an opening schedule.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(editingBookingId is null ? "Add DJ slot" : $"Edit DJ slot #{editingBookingId}");

        var selectedDj = view.Djs.FirstOrDefault(value => value.DjId == selectedDjId) ?? view.Djs.First();
        if (selectedDjId <= 0)
            selectedDjId = selectedDj.DjId;
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("DJ", $"{selectedDj.Name}{(selectedDj.Resident ? " (Resident)" : string.Empty)}"))
        {
            foreach (var dj in view.Djs.OrderByDescending(value => value.Resident).ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var selected = dj.DjId == selectedDjId;
                if (ImGui.Selectable($"{dj.Name}{(dj.Resident ? " (Resident)" : string.Empty)}##DjOption{dj.DjId}", selected))
                    selectedDjId = dj.DjId;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Starts (local)##DjBooking", ref bookingStartsAtLocal, 17);
        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Ends (local)##DjBooking", ref bookingEndsAtLocal, 17);

        var selectedStatus = view.Statuses.FirstOrDefault(value =>
            string.Equals(value.StatusCode, bookingStatusCode, StringComparison.OrdinalIgnoreCase)) ?? view.Statuses.First();
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Status", selectedStatus.DisplayName))
        {
            foreach (var status in view.Statuses.OrderBy(value => value.SortOrder))
            {
                var selected = string.Equals(status.StatusCode, bookingStatusCode, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{status.DisplayName}##DjStatus{status.StatusCode}", selected))
                    bookingStatusCode = status.StatusCode;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("Note##DjBooking", ref bookingNote, 1000, new Vector2(0, 65 * ImGuiHelpers.GlobalScale));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline(
            "Custom advertisement override##DjBooking",
            ref bookingMacroOverride,
            4000,
            new Vector2(0, 90 * ImGuiHelpers.GlobalScale));
        ImGui.TextDisabled("Leave blank to use the regular/resident generic timed macro. Both generic and override macros support <name> and <twitch>.");

        var startValid = TryParseLocalDateTime(bookingStartsAtLocal, out var startsAt, out var startError);
        var endValid = TryParseLocalDateTime(bookingEndsAtLocal, out var endsAt, out var endError);
        var macroValid = ValidateMacroText(bookingMacroOverride, 15, 180, out var lines, out var longestLine);
        var bookingValid = startValid && endValid && endsAt > startsAt &&
                           startsAt.ToUniversalTime() >= opening.OpensAt &&
                           endsAt.ToUniversalTime() <= opening.ClosesAt &&
                           macroValid;

        if (!startValid)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), startError);
        else if (!endValid)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), endError);
        else if (endsAt <= startsAt)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "DJ slot must end after it starts.");
        else if (startsAt.ToUniversalTime() < opening.OpensAt || endsAt.ToUniversalTime() > opening.ClosesAt)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "DJ slot must remain entirely inside the opening.");
        ImGui.TextDisabled($"Macro: {lines}/15 lines; longest line {longestLine}/180 characters.");

        ImGui.BeginDisabled(isBusy || !bookingValid);
        if (ImGui.Button(editingBookingId is null ? "Add DJ slot" : "Save DJ slot"))
        {
            plugin.SaveDjBooking(
                venue,
                editingBookingId,
                new SaveDjBookingRequest(
                    opening.OpeningId,
                    selectedDjId,
                    startsAt.ToUniversalTime(),
                    endsAt.ToUniversalTime(),
                    bookingStatusCode,
                    string.IsNullOrWhiteSpace(bookingNote) ? null : bookingNote.Trim(),
                    string.IsNullOrWhiteSpace(bookingMacroOverride) ? null : bookingMacroOverride));
            InitializeBookingDraft(opening, bookings, view);
        }
        ImGui.EndDisabled();

        if (editingBookingId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel slot edit"))
                InitializeBookingDraft(opening, bookings, view);
        }

        ImGui.SameLine();
        if (ImGui.Button("Close DJ scheduler"))
        {
            selectedDjOpeningId = null;
            ClearBookingDraft();
        }

        ImGui.Spacing();
        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("OpeningDjScheduleTable", 7, flags))
            return;

        ImGui.TableSetupColumn("Time");
        ImGui.TableSetupColumn("DJ");
        ImGui.TableSetupColumn("Resident", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Twitch");
        ImGui.TableSetupColumn("Macro");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var booking in bookings)
        {
            ImGui.PushID($"booking-{booking.BookingId}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted($"{booking.StartsAt.ToLocalTime():HH:mm}–{booking.EndsAt.ToLocalTime():HH:mm}");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(booking.DjName);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(booking.Resident ? "Yes" : "No");
            ImGui.TableSetColumnIndex(3);
            DrawStatus(booking.StatusCode, booking.StatusName);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(booking.TwitchUrl ?? "Not recorded");
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(booking.CustomMacroText) ? "Generic" : "Custom override");
            if (!string.IsNullOrWhiteSpace(booking.Note) && ImGui.IsItemHovered())
                ImGui.SetTooltip(booking.Note);
            ImGui.TableSetColumnIndex(6);
            ImGui.BeginDisabled(isBusy);
            if (ImGui.SmallButton("Edit"))
                LoadBookingDraft(booking);
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
            {
                pendingDeleteBookingId = booking.BookingId;
                pendingDeleteBookingOpeningId = opening.OpeningId;
                requestDeleteBookingPopup = true;
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
        if (requestDeleteBookingPopup)
        {
            requestDeleteBookingPopup = false;
            ImGui.OpenPopup("Delete DJ booking###PartyPulseDeleteDjBooking");
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
            ImGui.TextWrapped("Close this opening immediately? Arrival tracking and timed macros for this opening will stop.");
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

        if (ImGui.BeginPopupModal("Delete DJ booking###PartyPulseDeleteDjBooking", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Delete this DJ slot from the opening? The booking and its status history remain in SQL for later statistics, but it disappears from the active schedule.");
            ImGui.BeginDisabled(isBusy || pendingDeleteBookingId is null || pendingDeleteBookingOpeningId is null);
            if (ImGui.Button("Delete booking"))
            {
                plugin.DeleteDjBooking(
                    venue,
                    pendingDeleteBookingOpeningId!.Value,
                    pendingDeleteBookingId!.Value);
                pendingDeleteBookingId = null;
                pendingDeleteBookingOpeningId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Keep booking"))
            {
                pendingDeleteBookingId = null;
                pendingDeleteBookingOpeningId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private static DjCoverage CalculateCoverage(
        VenueOpeningScheduleItem opening,
        IReadOnlyCollection<DjBookingSummary> bookings)
    {
        var totalMinutes = Math.Max(0, (int)Math.Round(opening.Duration.TotalMinutes));
        var confirmed = MergeCoverageMinutes(
            opening,
            bookings.Where(value => string.Equals(value.StatusCode, DjBookingStatusCodes.Confirmed, StringComparison.OrdinalIgnoreCase)));
        var pending = MergeCoverageMinutes(
            opening,
            bookings.Where(value => string.Equals(value.StatusCode, DjBookingStatusCodes.Pending, StringComparison.OrdinalIgnoreCase)));
        return new DjCoverage(totalMinutes, confirmed, pending, Math.Max(0, totalMinutes - confirmed));
    }

    private static int MergeCoverageMinutes(
        VenueOpeningScheduleItem opening,
        IEnumerable<DjBookingSummary> source)
    {
        var intervals = source
            .Select(value => (
                Start: value.StartsAt < opening.OpensAt ? opening.OpensAt : value.StartsAt,
                End: value.EndsAt > opening.ClosesAt ? opening.ClosesAt : value.EndsAt))
            .Where(value => value.End > value.Start)
            .OrderBy(value => value.Start)
            .ToArray();
        if (intervals.Length == 0)
            return 0;

        var total = TimeSpan.Zero;
        var currentStart = intervals[0].Start;
        var currentEnd = intervals[0].End;
        foreach (var interval in intervals.Skip(1))
        {
            if (interval.Start <= currentEnd)
            {
                if (interval.End > currentEnd)
                    currentEnd = interval.End;
                continue;
            }
            total += currentEnd - currentStart;
            currentStart = interval.Start;
            currentEnd = interval.End;
        }
        total += currentEnd - currentStart;
        return Math.Max(0, (int)Math.Round(total.TotalMinutes));
    }

    private static void DrawCoverage(DjCoverage coverage)
    {
        if (coverage.TotalMinutes <= 0)
        {
            ImGui.TextDisabled("No duration");
            return;
        }

        var fraction = Math.Clamp(coverage.ConfirmedMinutes / (float)coverage.TotalMinutes, 0f, 1f);
        var isFilled = coverage.ConfirmedMinutes >= coverage.TotalMinutes;
        var label = isFilled
            ? "Filled"
            : coverage.ConfirmedMinutes > 0
                ? $"{coverage.ConfirmedMinutes}/{coverage.TotalMinutes} min"
                : coverage.PendingMinutes > 0
                    ? $"Pending {coverage.PendingMinutes} min"
                    : "No DJ";
        var color = isFilled
            ? new Vector4(0.25f, 0.75f, 0.35f, 1f)
            : coverage.ConfirmedMinutes > 0
                ? new Vector4(0.95f, 0.7f, 0.2f, 1f)
                : coverage.PendingMinutes > 0
                    ? new Vector4(0.85f, 0.55f, 0.2f, 1f)
                    : new Vector4(0.85f, 0.25f, 0.25f, 1f);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
        ImGui.ProgressBar(fraction, new Vector2(-1, 0), label);
        ImGui.PopStyleColor();
    }

    private static void DrawStatus(string statusCode, string statusName)
    {
        var color = statusCode.ToLowerInvariant() switch
        {
            DjBookingStatusCodes.Confirmed => new Vector4(0.35f, 0.85f, 0.45f, 1f),
            DjBookingStatusCodes.Pending => new Vector4(1f, 0.75f, 0.25f, 1f),
            DjBookingStatusCodes.Unavailable => new Vector4(1f, 0.45f, 0.4f, 1f),
            DjBookingStatusCodes.Cancelled => new Vector4(0.7f, 0.7f, 0.7f, 1f),
            _ => Vector4.One
        };
        ImGui.TextColored(color, statusName);
    }

    private void InitializeBookingDraft(
        VenueOpeningScheduleItem opening,
        IReadOnlyCollection<DjBookingSummary> bookings,
        DjViewResponse? view)
    {
        editingBookingId = null;
        selectedDjId = view?.Djs.OrderByDescending(value => value.Resident).ThenBy(value => value.Name).FirstOrDefault()?.DjId ?? 0;
        var reserved = bookings
            .Where(value => value.ReservesTime)
            .OrderBy(value => value.StartsAt)
            .ToArray();
        var start = opening.OpensAt;
        foreach (var booking in reserved)
        {
            if (booking.StartsAt > start)
                break;
            if (booking.EndsAt > start)
                start = booking.EndsAt;
        }
        if (start >= opening.ClosesAt)
            start = opening.OpensAt;
        var end = start.AddHours(1);
        if (end > opening.ClosesAt)
            end = opening.ClosesAt;
        bookingStartsAtLocal = start.ToLocalTime().ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        bookingEndsAtLocal = end.ToLocalTime().ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        bookingStatusCode = DjBookingStatusCodes.Pending;
        bookingNote = string.Empty;
        bookingMacroOverride = string.Empty;
    }

    private void LoadBookingDraft(DjBookingSummary booking)
    {
        editingBookingId = booking.BookingId;
        selectedDjId = booking.DjId;
        bookingStartsAtLocal = booking.StartsAt.ToLocalTime().ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        bookingEndsAtLocal = booking.EndsAt.ToLocalTime().ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        bookingStatusCode = booking.StatusCode;
        bookingNote = booking.Note ?? string.Empty;
        bookingMacroOverride = booking.CustomMacroText ?? string.Empty;
    }

    private void ClearBookingDraft()
    {
        editingBookingId = null;
        selectedDjId = 0;
        bookingStartsAtLocal = string.Empty;
        bookingEndsAtLocal = string.Empty;
        bookingStatusCode = DjBookingStatusCodes.Pending;
        bookingNote = string.Empty;
        bookingMacroOverride = string.Empty;
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
        selectedDjOpeningId = null;
        pendingDeleteBookingId = null;
        pendingDeleteBookingOpeningId = null;
        ClearBookingDraft();
    }

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
            error = $"Time must use {LocalDateTimeFormat}.";
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

    private sealed record DjCoverage(
        int TotalMinutes,
        int ConfirmedMinutes,
        int PendingMinutes,
        int GapMinutes);
}
