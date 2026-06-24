using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.OpeningPublications;
using PartyPulse.Services;
using PartyPulse.VenueOpenings;

namespace PartyPulse.Windows;

public sealed class VenueOpeningsTabRenderer(Plugin plugin)
{
    private const string LocalDateTimeFormat = "yyyy-MM-dd HH:mm";
    private Guid activeProfileId;
    private string activeDisplayTimeZoneId = string.Empty;
    private bool draftInitialized;
    private long? editingOpeningId;
    private string startsAtLocal = string.Empty;
    private string endsAtLocal = string.Empty;
    private int durationMinutes = 480;
    private DateTimeOffset? pendingOpeningSuggestionAfter;
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
    private int bookingDurationMinutes = 60;
    private string bookingStatusCode = DjBookingStatusCodes.Pending;
    private string bookingNote = string.Empty;
    private string bookingMacroOverride = string.Empty;
    private long? pendingDeleteBookingId;
    private long? pendingDeleteBookingOpeningId;
    private bool requestDeleteBookingPopup;
    private DateTimeOffset? pendingBookingSuggestionAfter;

    private long? selectedPublicationOpeningId;
    private readonly Dictionary<string, string> publicationDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dirtyPublicationDrafts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? publicationDraftReceivedAt;

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenueChange(venue);
        plugin.EnsureVenueOpeningsLoaded(venue);
        plugin.EnsureDjsLoaded(venue);
        plugin.EnsureOpeningPublicationsLoaded(venue);

        var snapshot = plugin.VenueOpenings.GetSnapshot(venue);
        var historySnapshot = plugin.VenueOpenings.GetHistorySnapshot(venue);
        var view = snapshot.View;
        if (view is not null && !view.Capabilities.CanManage)
            return;

        if (!ImGui.BeginTabItem("Openings"))
            return;

        var isBusy = plugin.VenueOpenings.IsBusy(venue.ProfileId);
        var djsBusy = plugin.Djs.IsBusy(venue.ProfileId);
        var djSnapshot = plugin.Djs.GetSnapshot(venue);
        var djView = djSnapshot.View;
        var publicationSnapshot = plugin.OpeningPublications.GetSnapshot(venue);

        ImGui.BeginDisabled(isBusy || djsBusy);
        if (ImGui.Button("Refresh schedule"))
        {
            plugin.RefreshVenueOpenings(venue);
            plugin.RefreshDjs(venue);
            plugin.RefreshOpeningPublications(venue);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled($"Opening and DJ times use {VenueTimeZone.Resolve(venue).DisplayName} and are stored as UTC.");

        if (view is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        ProcessPendingSuggestions(venue, view, djSnapshot, isBusy, djsBusy);
        EnsureDraft(venue, view);
        DrawEditor(venue, view, isBusy);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSchedule(venue, view, djView, isBusy || djsBusy);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawPreviousOpenings(venue, historySnapshot, isBusy || djsBusy);

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

        if (selectedPublicationOpeningId is { } publicationOpeningId)
        {
            var opening = view.Openings.FirstOrDefault(value => value.OpeningId == publicationOpeningId);
            if (opening is not null)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawPublicationEditor(
                    venue,
                    opening,
                    publicationSnapshot,
                    isBusy || djsBusy || plugin.OpeningPublications.IsBusy(venue.ProfileId));
            }
            else
            {
                selectedPublicationOpeningId = null;
                publicationDrafts.Clear();
                dirtyPublicationDrafts.Clear();
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
        var openingStartChanged = ImGui.InputText("Starts (venue time)", ref startsAtLocal, 17);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Format: {LocalDateTimeFormat}");

        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        var openingDurationChanged = ImGui.InputInt("Duration (minutes)", ref durationMinutes);
        if (openingDurationChanged)
            durationMinutes = Math.Clamp(durationMinutes, 30, 2880);
        if ((openingStartChanged || openingDurationChanged) &&
            TryParseLocalDateTime(venue, startsAtLocal, out var calculatedOpeningStart, out _))
        {
            var synchronizedDuration = openingDurationChanged
                ? durationMinutes
                : Math.Clamp(durationMinutes, 30, 2880);
            endsAtLocal = VenueTimeZone.Format(
                venue,
                calculatedOpeningStart.AddMinutes(synchronizedDuration),
                LocalDateTimeFormat,
                CultureInfo.InvariantCulture);
        }

        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        var openingEndChanged = ImGui.InputText("Ends (venue time)", ref endsAtLocal, 17);
        if (openingEndChanged &&
            TryParseLocalDateTime(venue, startsAtLocal, out var durationOpeningStart, out _) &&
            TryParseLocalDateTime(venue, endsAtLocal, out var durationOpeningEnd, out _) &&
            durationOpeningEnd > durationOpeningStart)
        {
            durationMinutes = (int)Math.Round(
                (durationOpeningEnd - durationOpeningStart).TotalMinutes);
        }

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

        var validStart = TryParseLocalDateTime(venue, startsAtLocal, out var startsAt, out var dateError);
        var validEnd = TryParseLocalDateTime(venue, endsAtLocal, out var closesAt, out var endDateError);
        var actualDurationMinutes = validStart && validEnd
            ? (closesAt - startsAt).TotalMinutes
            : 0;
        var valid = validStart &&
                    validEnd &&
                    closesAt > startsAt &&
                    actualDurationMinutes is >= 30 and <= 2880 &&
                    !string.IsNullOrWhiteSpace(themeName) &&
                    !string.IsNullOrWhiteSpace(addressWorldName) &&
                    !string.IsNullOrWhiteSpace(addressCityName) &&
                    addressWard is >= 1 and <= 30 &&
                    addressPlot is >= 1 and <= 60;

        if (!validStart)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), dateError);
        else if (!validEnd)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), endDateError);
        else if (closesAt <= startsAt)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "Opening must end after it starts.");
        else if (actualDurationMinutes is < 30 or > 2880)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "Opening duration must be between 30 and 2880 minutes.");
        else
            ImGui.TextDisabled($"Duration: {actualDurationMinutes:N0} minutes.");

        ImGui.BeginDisabled(isBusy || !valid);
        if (ImGui.Button(editingOpeningId is null ? "Schedule opening" : "Save changes"))
        {
            pendingOpeningSuggestionAfter = DateTimeOffset.UtcNow;
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
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(editingOpeningId is null ? "Reset suggestion" : "Cancel edit"))
        {
            pendingOpeningSuggestionAfter = null;
            editingOpeningId = null;
            LoadSuggestedDraft(venue, view);
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
            .Where(value => !value.IsCancelled && value.ClosesAt > now)
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
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 300 * ImGuiHelpers.GlobalScale);
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
            ImGui.TextUnformatted($"{VenueTimeZone.Format(venue, opening.OpensAt, "ddd yyyy-MM-dd HH:mm")}");
            ImGui.TextDisabled($"to {VenueTimeZone.Format(venue, opening.ClosesAt, "yyyy-MM-dd HH:mm")}");
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
                    InitializeBookingDraft(venue, opening, bookings, djView);
                }
                ImGui.SameLine();
            }
            if (ImGui.SmallButton("Publicity"))
            {
                selectedPublicationOpeningId = opening.OpeningId;
                publicationDraftReceivedAt = null;
                dirtyPublicationDrafts.Clear();
            }
            ImGui.SameLine();
            if (isFuture)
            {
                if (ImGui.SmallButton("Edit"))
                    LoadOpeningDraft(venue, opening);
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

    private void DrawPreviousOpenings(
        VenueConnectionConfiguration venue,
        VenueOpeningHistorySnapshot snapshot,
        bool isBusy)
    {
        ImGui.TextUnformatted("Previous openings");
        ImGui.TextDisabled("Loaded separately so the normal opening schedule stays fast as history grows.");

        if (snapshot.Status == VenueOpeningHistoryStatus.NotLoaded)
        {
            ImGui.BeginDisabled(isBusy);
            if (ImGui.Button("Load previous openings"))
                plugin.RefreshVenueOpeningHistory(venue);
            ImGui.EndDisabled();
            return;
        }

        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh previous openings"))
            plugin.RefreshVenueOpeningHistory(venue);
        ImGui.EndDisabled();
        if (snapshot.Status == VenueOpeningHistoryStatus.Loading)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(snapshot.Message);
        }
        else if (snapshot.Status == VenueOpeningHistoryStatus.Failed)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), snapshot.Message);
        }

        if (snapshot.Openings.Count == 0)
        {
            ImGui.TextDisabled("No previous openings were found.");
            return;
        }

        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable(
                "VenueOpeningHistory",
                5,
                flags,
                new Vector2(0, 240 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("When");
            ImGui.TableSetupColumn("Theme");
            ImGui.TableSetupColumn("Address");
            ImGui.TableSetupColumn("Title");
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 85 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var opening in snapshot.Openings
                         .OrderByDescending(value => value.OpensAt)
                         .ThenByDescending(value => value.OpeningId))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"{VenueTimeZone.Format(venue, opening.OpensAt, "ddd yyyy-MM-dd HH:mm")}");
                ImGui.TextDisabled($"to {VenueTimeZone.Format(venue, opening.ClosesAt, "yyyy-MM-dd HH:mm")}");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(opening.ThemeName ?? "No theme");
                ImGui.TableSetColumnIndex(2);
                ImGui.TextWrapped(opening.Address.DisplayText);
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(opening.Title ?? string.Empty);
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(opening.IsCancelled ? "Cancelled" : "Finished");
            }

            ImGui.EndTable();
        }

        if (snapshot.HasMore)
        {
            ImGui.BeginDisabled(isBusy || snapshot.Status == VenueOpeningHistoryStatus.Loading);
            if (ImGui.Button("Load more previous openings"))
                plugin.LoadMoreVenueOpeningHistory(venue);
            ImGui.EndDisabled();
        }
    }

    private void DrawDjScheduleEditor(
        VenueConnectionConfiguration venue,
        VenueOpeningScheduleItem opening,
        PartyPulse.Djs.DjManagementSnapshot snapshot,
        bool isBusy)
    {
        ImGui.TextUnformatted($"DJ schedule — opening #{opening.OpeningId}");
        ImGui.TextDisabled($"{VenueTimeZone.Format(venue, opening.OpensAt, "g")} to {VenueTimeZone.Format(venue, opening.ClosesAt, "g")}");

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
        var bookingStartChanged = ImGui.InputText("Starts (venue time)##DjBooking", ref bookingStartsAtLocal, 17);
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        var bookingDurationChanged = ImGui.InputInt("Duration (minutes)##DjBooking", ref bookingDurationMinutes);
        if (bookingDurationChanged)
            bookingDurationMinutes = Math.Clamp(bookingDurationMinutes, 15, 2880);
        if ((bookingStartChanged || bookingDurationChanged) &&
            TryParseLocalDateTime(venue, bookingStartsAtLocal, out var calculatedStart, out _))
        {
            var synchronizedDuration = bookingDurationChanged
                ? bookingDurationMinutes
                : Math.Clamp(bookingDurationMinutes, 15, 2880);
            bookingEndsAtLocal = VenueTimeZone.Format(
                venue,
                calculatedStart.AddMinutes(synchronizedDuration),
                LocalDateTimeFormat,
                CultureInfo.InvariantCulture);
        }

        ImGui.SetNextItemWidth(210 * ImGuiHelpers.GlobalScale);
        var bookingEndChanged = ImGui.InputText("Ends (venue time)##DjBooking", ref bookingEndsAtLocal, 17);
        if (bookingEndChanged &&
            TryParseLocalDateTime(venue, bookingStartsAtLocal, out var durationStart, out _) &&
            TryParseLocalDateTime(venue, bookingEndsAtLocal, out var durationEnd, out _) &&
            durationEnd > durationStart)
        {
            bookingDurationMinutes = (int)Math.Round(
                (durationEnd - durationStart).TotalMinutes);
        }

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

        ImGui.TextUnformatted("Note");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##DjBookingNote", ref bookingNote, 1000, new Vector2(0, 65 * ImGuiHelpers.GlobalScale));
        ImGui.TextUnformatted("Custom advertisement override");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline(
            "##DjBookingMacroOverride",
            ref bookingMacroOverride,
            4000,
            new Vector2(0, 90 * ImGuiHelpers.GlobalScale));
        ImGui.TextDisabled("Leave blank to use the regular/resident generic timed macro. Both generic and override macros support <name> and <twitch>.");

        var startValid = TryParseLocalDateTime(venue, bookingStartsAtLocal, out var startsAt, out var startError);
        var endValid = TryParseLocalDateTime(venue, bookingEndsAtLocal, out var endsAt, out var endError);
        var macroValid = ValidateMacroText(bookingMacroOverride, 15, 180, out var lines, out var longestLine);
        var actualBookingDurationMinutes = startValid && endValid
            ? (endsAt - startsAt).TotalMinutes
            : 0;
        var bookingValid = startValid && endValid && endsAt > startsAt &&
                           actualBookingDurationMinutes is >= 15 and <= 2880 &&
                           startsAt.ToUniversalTime() >= opening.OpensAt &&
                           endsAt.ToUniversalTime() <= opening.ClosesAt &&
                           macroValid;

        if (!startValid)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), startError);
        else if (!endValid)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), endError);
        else if (endsAt <= startsAt)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "DJ slot must end after it starts.");
        else if (actualBookingDurationMinutes is < 15 or > 2880)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "DJ slot duration must be between 15 and 2880 minutes.");
        else if (startsAt.ToUniversalTime() < opening.OpensAt || endsAt.ToUniversalTime() > opening.ClosesAt)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "DJ slot must remain entirely inside the opening.");
        ImGui.TextDisabled($"Macro: {lines}/15 lines; longest line {longestLine}/180 characters.");

        ImGui.BeginDisabled(isBusy || !bookingValid);
        if (ImGui.Button(editingBookingId is null ? "Add DJ slot" : "Save DJ slot"))
        {
            pendingBookingSuggestionAfter = DateTimeOffset.UtcNow;
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
        }
        ImGui.EndDisabled();

        if (editingBookingId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel slot edit"))
            {
                pendingBookingSuggestionAfter = null;
                InitializeBookingDraft(venue, opening, bookings, view);
            }
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
            ImGui.TextUnformatted($"{VenueTimeZone.Format(venue, booking.StartsAt, "HH:mm")}–{VenueTimeZone.Format(venue, booking.EndsAt, "HH:mm")}");
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
                LoadBookingDraft(venue, booking);
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

    private void ProcessPendingSuggestions(
        VenueConnectionConfiguration venue,
        VenueOpeningScheduleResponse view,
        PartyPulse.Djs.DjManagementSnapshot djSnapshot,
        bool openingsBusy,
        bool djsBusy)
    {
        if (pendingOpeningSuggestionAfter is { } openingRequestedAt &&
            !openingsBusy &&
            plugin.VenueOpenings.GetLastSuccessfulOpeningSaveAt(venue.ProfileId) is { } openingLoadedAt &&
            openingLoadedAt >= openingRequestedAt)
        {
            pendingOpeningSuggestionAfter = null;
            LoadSuggestedDraft(venue, view);
        }

        if (pendingBookingSuggestionAfter is { } bookingRequestedAt &&
            selectedDjOpeningId is { } openingId &&
            !djsBusy &&
            djSnapshot.Status == PartyPulse.Djs.DjManagementStatus.Ready &&
            plugin.Djs.GetLastSuccessfulBookingSaveAt(venue.ProfileId) is { } bookingLoadedAt &&
            bookingLoadedAt >= bookingRequestedAt &&
            djSnapshot.View is { } djView)
        {
            var opening = view.Openings.FirstOrDefault(value => value.OpeningId == openingId);
            if (opening is not null)
            {
                var bookings = djView.Bookings
                    .Where(value => value.OpeningId == openingId)
                    .OrderBy(value => value.StartsAt)
                    .ThenBy(value => value.BookingId)
                    .ToArray();
                InitializeBookingDraft(venue, opening, bookings, djView);
            }
            pendingBookingSuggestionAfter = null;
        }
    }

    private void DrawPublicationEditor(
        VenueConnectionConfiguration venue,
        VenueOpeningScheduleItem opening,
        OpeningPublicationManagementSnapshot snapshot,
        bool isBusy)
    {
        ImGui.TextUnformatted($"Opening publicity — opening #{opening.OpeningId}");
        ImGui.TextDisabled($"{VenueTimeZone.Format(venue, opening.OpensAt, "g")} to {VenueTimeZone.Format(venue, opening.ClosesAt, "g")} — {opening.ThemeName ?? "No theme"}");

        var view = snapshot.View;
        if (view is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        var publicationOpening = view.Openings.FirstOrDefault(value => value.OpeningId == opening.OpeningId);
        if (publicationOpening is null)
        {
            ImGui.TextDisabled("Opening-publication data for this opening is not available yet.");
            ImGui.BeginDisabled(isBusy);
            if (ImGui.Button("Refresh publication data"))
                plugin.RefreshOpeningPublications(venue);
            ImGui.EndDisabled();
            return;
        }

        SyncPublicationDrafts(publicationOpening, view, snapshot.ReceivedAt);
        var displayDate = VenueTimeZone.Format(venue, publicationOpening.OpensAt, "MMM d", CultureInfo.GetCultureInfo("en-US"));
        var displayTime = VenueTimeZone.Format(venue, publicationOpening.OpensAt, "h tt", CultureInfo.GetCultureInfo("en-US"));
        ImGui.TextDisabled($"Placeholder values: <theme> = {publicationOpening.ThemeName ?? string.Empty}; <djs> = {publicationOpening.Djs}; <date> = {displayDate}; <time> = {displayTime}");

        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Create Shoutrunner Macros"))
        {
            ClearPublicationDirtyState("shoutrunner", view);
            plugin.GenerateOpeningPublications(venue, publicationOpening, "shoutrunner");
        }
        ImGui.SameLine();
        if (ImGui.Button("Create Party Finder Text"))
        {
            ClearPublicationDirtyState("partyfinder", view);
            plugin.GenerateOpeningPublications(venue, publicationOpening, "partyfinder");
        }
        ImGui.SameLine();
        if (ImGui.Button("Reload saved text"))
        {
            dirtyPublicationDrafts.Clear();
            publicationDraftReceivedAt = null;
            SyncPublicationDrafts(publicationOpening, view, snapshot.ReceivedAt);
        }
        ImGui.EndDisabled();

        foreach (var template in view.Templates)
        {
            publicationDrafts.TryAdd(template.PublicationCode, string.Empty);
            var value = publicationDrafts[template.PublicationCode];
            ImGui.Spacing();
            //ImGui.SeparatorText($"{(template.ChannelCode == "shoutrunner" ? "Shoutrunner" : "Party Finder")} — {template.DisplayName}");
            ImGui.TextUnformatted($"{(template.ChannelCode == "shoutrunner" ? "Shoutrunner" : "Party Finder")} — {template.DisplayName}");
            ImGui.Separator();
            ImGui.SetNextItemWidth(-1);
            var height = template.ChannelCode == "partyfinder" ? 65 : 105;
            if (ImGui.InputTextMultiline(
                    $"##OpeningPublication{opening.OpeningId}-{template.PublicationCode}",
                    ref value,
                    4000,
                    new Vector2(0, height * ImGuiHelpers.GlobalScale)))
            {
                publicationDrafts[template.PublicationCode] = value;
                dirtyPublicationDrafts.Add(template.PublicationCode);
            }

            var valid = ShoutrunnerTabRenderer.Validate(
                value,
                template.MaxLines,
                template.MaxLineLength,
                out var lineCount,
                out var longestLine);
            ImGui.TextDisabled($"{lineCount}/{template.MaxLines} lines; longest line {longestLine}/{template.MaxLineLength} characters.");
            ImGui.BeginDisabled(isBusy || !valid || !dirtyPublicationDrafts.Contains(template.PublicationCode));
            if (ImGui.Button($"Save {template.DisplayName}##OpeningPublicationSave{template.PublicationCode}"))
            {
                plugin.SaveOpeningPublicationText(
                    venue,
                    opening.OpeningId,
                    template.PublicationCode,
                    value);
                dirtyPublicationDrafts.Remove(template.PublicationCode);
            }
            ImGui.EndDisabled();
            if (!valid)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), "Text exceeds its line limit.");
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Close publicity editor"))
        {
            selectedPublicationOpeningId = null;
            publicationDrafts.Clear();
            dirtyPublicationDrafts.Clear();
            publicationDraftReceivedAt = null;
        }
    }

    private void SyncPublicationDrafts(
        OpeningPublicationOpeningSummary opening,
        OpeningPublicationContextResponse view,
        DateTimeOffset? receivedAt)
    {
        if (publicationDraftReceivedAt == receivedAt) return;
        foreach (var template in view.Templates)
        {
            if (dirtyPublicationDrafts.Contains(template.PublicationCode)) continue;
            publicationDrafts[template.PublicationCode] = opening.Texts
                .FirstOrDefault(value => string.Equals(
                    value.PublicationCode,
                    template.PublicationCode,
                    StringComparison.OrdinalIgnoreCase))
                ?.PublicationText ?? string.Empty;
        }
        publicationDraftReceivedAt = receivedAt;
    }

    private void ClearPublicationDirtyState(string channelCode, OpeningPublicationContextResponse view)
    {
        foreach (var template in view.Templates.Where(value =>
                     string.Equals(value.ChannelCode, channelCode, StringComparison.OrdinalIgnoreCase)))
            dirtyPublicationDrafts.Remove(template.PublicationCode);
        publicationDraftReceivedAt = null;
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
        VenueConnectionConfiguration venue,
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
        bookingStartsAtLocal = VenueTimeZone.Format(venue, start, LocalDateTimeFormat, CultureInfo.InvariantCulture);
        bookingEndsAtLocal = VenueTimeZone.Format(venue, end, LocalDateTimeFormat, CultureInfo.InvariantCulture);
        bookingDurationMinutes = Math.Clamp((int)Math.Round((end - start).TotalMinutes), 15, 2880);
        bookingStatusCode = DjBookingStatusCodes.Pending;
        bookingNote = string.Empty;
        bookingMacroOverride = string.Empty;
    }

    private void LoadBookingDraft(VenueConnectionConfiguration venue, DjBookingSummary booking)
    {
        editingBookingId = booking.BookingId;
        selectedDjId = booking.DjId;
        bookingStartsAtLocal = VenueTimeZone.Format(venue, booking.StartsAt, LocalDateTimeFormat, CultureInfo.InvariantCulture);
        bookingEndsAtLocal = VenueTimeZone.Format(venue, booking.EndsAt, LocalDateTimeFormat, CultureInfo.InvariantCulture);
        bookingDurationMinutes = Math.Clamp((int)Math.Round((booking.EndsAt - booking.StartsAt).TotalMinutes), 15, 2880);
        bookingStatusCode = booking.StatusCode;
        bookingNote = booking.Note ?? string.Empty;
        bookingMacroOverride = booking.CustomMacroText ?? string.Empty;
    }

    private void ClearBookingDraft()
    {
        pendingBookingSuggestionAfter = null;
        editingBookingId = null;
        selectedDjId = 0;
        bookingStartsAtLocal = string.Empty;
        bookingEndsAtLocal = string.Empty;
        bookingDurationMinutes = 60;
        bookingStatusCode = DjBookingStatusCodes.Pending;
        bookingNote = string.Empty;
        bookingMacroOverride = string.Empty;
    }

    private void EnsureDraft(VenueConnectionConfiguration venue, VenueOpeningScheduleResponse view)
    {
        if (!draftInitialized)
            LoadSuggestedDraft(venue, view);
    }

    private void LoadSuggestedDraft(VenueConnectionConfiguration venue, VenueOpeningScheduleResponse view)
    {
        draftInitialized = true;
        startsAtLocal = VenueTimeZone.Format(venue, view.SuggestedOpensAt, LocalDateTimeFormat, CultureInfo.InvariantCulture);
        durationMinutes = Math.Clamp(view.SuggestedDurationMinutes, 30, 2880);
        endsAtLocal = VenueTimeZone.Format(venue, view.SuggestedOpensAt.AddMinutes(durationMinutes), LocalDateTimeFormat, CultureInfo.InvariantCulture);
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

    private void LoadOpeningDraft(VenueConnectionConfiguration venue, VenueOpeningScheduleItem opening)
    {
        draftInitialized = true;
        editingOpeningId = opening.OpeningId;
        startsAtLocal = VenueTimeZone.Format(venue, opening.OpensAt, LocalDateTimeFormat, CultureInfo.InvariantCulture);
        durationMinutes = Math.Clamp((int)Math.Round(opening.Duration.TotalMinutes), 30, 2880);
        endsAtLocal = VenueTimeZone.Format(venue, opening.ClosesAt, LocalDateTimeFormat, CultureInfo.InvariantCulture);
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
        var displayTimeZoneId = VenueTimeZone.Resolve(venue).Id;
        if (activeProfileId == venue.ProfileId &&
            string.Equals(activeDisplayTimeZoneId, displayTimeZoneId, StringComparison.Ordinal))
        {
            return;
        }

        activeProfileId = venue.ProfileId;
        activeDisplayTimeZoneId = displayTimeZoneId;
        draftInitialized = false;
        editingOpeningId = null;
        pendingCancelOpeningId = null;
        pendingCloseOpeningId = null;
        selectedDjOpeningId = null;
        pendingDeleteBookingId = null;
        pendingDeleteBookingOpeningId = null;
        pendingOpeningSuggestionAfter = null;
        pendingBookingSuggestionAfter = null;
        selectedPublicationOpeningId = null;
        publicationDrafts.Clear();
        dirtyPublicationDrafts.Clear();
        publicationDraftReceivedAt = null;
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
        VenueConnectionConfiguration venue,
        string value,
        out DateTimeOffset dateTimeOffset,
        out string error)
    {
        return VenueTimeZone.TryParseExact(
            venue,
            value,
            LocalDateTimeFormat,
            CultureInfo.InvariantCulture,
            out dateTimeOffset,
            out error);
    }

    private sealed record DjCoverage(
        int TotalMinutes,
        int ConfirmedMinutes,
        int PendingMinutes,
        int GapMinutes);
}
