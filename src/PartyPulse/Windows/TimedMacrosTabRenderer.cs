using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.TimedMacros;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class TimedMacrosTabRenderer(Plugin plugin)
{
    private Guid activeProfileId;
    private readonly Dictionary<long, TimedMacroDraft> drafts = new();
    private string newMacroName = string.Empty;
    private string newMacroText = string.Empty;
    private int newMacroIntervalMinutes = 30;
    private bool newMacroEnabled = true;
    private long? pendingArchiveId;
    private bool requestArchivePopup;

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenueChange(venue);


        plugin.EnsureTimedMacrosLoaded(venue);
        var snapshot = plugin.TimedMacros.GetSnapshot(venue);
        var view = snapshot.View;
        var isBusy = plugin.TimedMacros.IsBusy(venue.ProfileId) || plugin.IsGameMacroBusy;

        PartyPulseUi.PageHeader("Timed Macros", "Run shared venue timers and manage opening-bound or global macro definitions.");
        ImGui.BeginDisabled(plugin.TimedMacros.IsBusy(venue.ProfileId));
        if (ImGui.Button("Refresh timed macros"))
            plugin.RefreshTimedMacros(venue);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("Execution resets the shared timer for every user. Running before the timer is due is allowed.");

        if (view is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        var opening = view.CurrentOpening;
        var locationMessage = string.Empty;
        var atAddress = opening is not null && plugin.LocationProvider.IsAtOpeningLocation(
            opening.AddressWorldName,
            opening.AddressCityName,
            opening.AddressWard,
            opening.AddressPlot,
            opening.LocationType,
            opening.OutdoorLocationName,
            out locationMessage);

        DrawOpeningStatus(venue, opening, atAddress, locationMessage);
        ImGui.Spacing();
        DrawExecutionTable(venue, snapshot, view, opening, atAddress, isBusy);

        if (view.Macros.Any(value => value.CanManage) || view.Capabilities.CanManageAny)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawSetup(venue, view, isBusy);
        }

        DrawArchivePopup(venue, isBusy);
    }

    private static void DrawOpeningStatus(
        VenueConnectionConfiguration venue,
        TimedMacroOpeningSummary? opening,
        bool atAddress,
        string locationMessage)
    {
        if (opening is null)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.72f, 0.25f, 1f),
                "Opening-bound timers are paused because there is no active opening. Global timers remain available.");
            return;
        }

        ImGui.TextUnformatted(opening.Title ?? $"Opening #{opening.OpeningId}");
        ImGui.TextDisabled($"{VenueTimeZone.Format(venue, opening.OpensAt, "g")} – {VenueTimeZone.Format(venue, opening.ClosesAt, "g")}");
        ImGui.TextDisabled(opening.AddressDisplay);
        if (!atAddress)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.72f, 0.25f, 1f),
                "Opening-bound timers are paused because you are not at this opening's location. Global timers remain available.");
            ImGui.TextWrapped(locationMessage);
        }
    }

    private void DrawExecutionTable(
        VenueConnectionConfiguration venue,
        TimedMacroManagementSnapshot snapshot,
        TimedMacroViewResponse view,
        TimedMacroOpeningSummary? opening,
        bool atAddress,
        bool isBusy)
    {
        ImGui.TextUnformatted("Timed macros");
        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("TimedMacroExecution", 6, flags))
            return;

        ImGui.TableSetupColumn("Macro");
        ImGui.TableSetupColumn("Interval", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Last executed");
        ImGui.TableSetupColumn("Next");
        ImGui.TableSetupColumn("Runs", ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        var now = snapshot.EstimatedServerNow;
        foreach (var macro in view.Macros
                     .Where(value => !value.IsTemplate && (value.CanExecute || value.CanManage))
                     .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.TimedMacroId))
        {
            ImGui.PushID(macro.TimedMacroId.ToString());
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(macro.DisplayName);
            if (!macro.Enabled)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(disabled)");
            }
            if (!macro.IsConfigured)
                ImGui.TextDisabled("Not configured");

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(FormatInterval(macro.IntervalMinutes));

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(macro.LastExecutedAt is { } lastExecuted
                ? VenueTimeZone.Format(venue, lastExecuted, "t")
                : macro.RequiresActiveOpening ? "Not this opening" : "Never");

            ImGui.TableSetColumnIndex(3);
            DrawCountdown(macro, opening, atAddress, now);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(macro.ExecutionCount.ToString("N0"));

            ImGui.TableSetColumnIndex(5);
            var scopeAvailable = !macro.RequiresActiveOpening || (opening is not null && atAddress);
            var canExecute =
                macro.CanExecute &&
                macro.Enabled &&
                macro.IsConfigured &&
                scopeAvailable;
            ImGui.BeginDisabled(isBusy || !canExecute);
            if (ImGui.SmallButton("Execute"))
                plugin.RunTimedMacro(venue, macro, macro.RequiresActiveOpening ? opening : null);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canExecute)
            {
                var reason = !macro.CanExecute
                    ? "You do not have permission to execute this macro."
                    : !macro.Enabled
                        ? "This timed macro is disabled."
                        : !macro.IsConfigured
                            ? "This timed macro has not been configured."
                            : macro.RequiresActiveOpening && opening is null
                                ? "There is no active opening."
                                : macro.RequiresActiveOpening
                                    ? "You must be at the opening location."
                                    : "This macro is not currently available.";
                ImGui.SetTooltip(reason);
            }
            else if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("May be executed before it is due. A successful execution resets the shared timer.");
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawSetup(
        VenueConnectionConfiguration venue,
        TimedMacroViewResponse view,
        bool isBusy)
    {
        ImGui.TextUnformatted("Timed macro setup");
        ImGui.TextDisabled("Macros support normal in-game macro lines and <wait.X>. Maximum 15 lines.");

        foreach (var macro in view.Macros
                     .Where(value => value.CanManage && !value.IsScheduleInstance)
                     .OrderBy(value => value.TypeCode == TimedMacroTypeCodes.VipAdvertisement ? 0
                         : value.TypeCode == TimedMacroTypeCodes.PhotoshootAdvertisement ? 1
                         : value.TypeCode == TimedMacroTypeCodes.CourtAdvertisement ? 2
                         : value.TypeCode == TimedMacroTypeCodes.BarAdvertisement ? 3
                         : value.TypeCode == TimedMacroTypeCodes.BarBuyout ? 4
                         : value.TypeCode == TimedMacroTypeCodes.BarGamba ? 5
                         : 6)
                     .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var draft = GetDraft(macro);
            ImGui.PushID($"setup-{macro.TimedMacroId}");
            ImGui.Spacing();
            var headerLabel = $"{macro.DisplayName}##TimedMacroSetup{macro.TimedMacroId}";
            if (!ImGui.CollapsingHeader(headerLabel))
            {
                ImGui.PopID();
                continue;
            }
            ImGui.Spacing();

            if (macro.IsCustom)
            {
                ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputText("Name", ref draft.DisplayName, 100))
                    draft.Dirty = true;
            }
            else
            {
                ImGui.TextUnformatted(macro.DisplayName);
            }

            if (!string.IsNullOrWhiteSpace(macro.Description))
                ImGui.TextDisabled(macro.Description);

            ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Interval (minutes)", ref draft.IntervalMinutes))
                draft.Dirty = true;
            draft.IntervalMinutes = Math.Clamp(draft.IntervalMinutes, 1, 10080);

            if (ImGui.Checkbox("Enabled", ref draft.Enabled))
                draft.Dirty = true;

            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextMultiline(
                    "##TimedMacroText",
                    ref draft.MacroText,
                    4000,
                    new Vector2(0, 105 * ImGuiHelpers.GlobalScale)))
            {
                draft.Dirty = true;
            }

            var valid = ValidateMacroText(draft.MacroText, macro.MaxLines, macro.MaxLineLength, out var lineCount, out var longestLine);
            ImGui.TextDisabled(
                $"{lineCount}/{macro.MaxLines} lines; longest line {longestLine}/{macro.MaxLineLength} characters");

            var validName = !macro.IsCustom || !string.IsNullOrWhiteSpace(draft.DisplayName);
            ImGui.BeginDisabled(isBusy || !valid || !validName);
            if (ImGui.SmallButton("Save"))
            {
                plugin.UpdateTimedMacro(
                    venue,
                    macro.TimedMacroId,
                    new UpdateTimedMacroRequest(
                        macro.IsCustom ? draft.DisplayName.Trim() : macro.DisplayName,
                        string.IsNullOrWhiteSpace(draft.MacroText) ? null : draft.MacroText,
                        draft.IntervalMinutes,
                        draft.Enabled));
                draft.Dirty = false;
            }
            ImGui.EndDisabled();

            if (macro.IsCustom)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(isBusy);
                if (ImGui.SmallButton("Archive"))
                {
                    pendingArchiveId = macro.TimedMacroId;
                    requestArchivePopup = true;
                }
                ImGui.EndDisabled();
            }

            if (!valid)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Macro exceeds the configured game limits.");
            }

            ImGui.PopID();
        }

        if (!view.Capabilities.CanManageAny)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Add custom timed macro");
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Name##NewTimedMacro", ref newMacroName, 100);
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Interval (minutes)##NewTimedMacro", ref newMacroIntervalMinutes);
        newMacroIntervalMinutes = Math.Clamp(newMacroIntervalMinutes, 1, 10080);
        ImGui.Checkbox("Enabled##NewTimedMacro", ref newMacroEnabled);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline(
            "##NewTimedMacroText",
            ref newMacroText,
            4000,
            new Vector2(0, 105 * ImGuiHelpers.GlobalScale));

        var newValid = ValidateMacroText(newMacroText, 15, 180, out var newLineCount, out var newLongestLine);
        ImGui.TextDisabled($"{newLineCount}/15 lines; longest line {newLongestLine}/180 characters");
        ImGui.BeginDisabled(isBusy || !newValid || string.IsNullOrWhiteSpace(newMacroName));
        if (ImGui.Button("Create custom timed macro"))
        {
            plugin.CreateTimedMacro(
                venue,
                new CreateTimedMacroRequest(
                    newMacroName.Trim(),
                    string.IsNullOrWhiteSpace(newMacroText) ? null : newMacroText,
                    newMacroIntervalMinutes,
                    newMacroEnabled));
            newMacroName = string.Empty;
            newMacroText = string.Empty;
            newMacroIntervalMinutes = 30;
            newMacroEnabled = true;
        }
        ImGui.EndDisabled();
    }

    private void DrawArchivePopup(VenueConnectionConfiguration venue, bool isBusy)
    {
        if (requestArchivePopup)
        {
            requestArchivePopup = false;
            ImGui.OpenPopup("Archive timed macro###PartyPulseArchiveTimedMacro");
        }

        if (!ImGui.BeginPopupModal(
                "Archive timed macro###PartyPulseArchiveTimedMacro",
                ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped("Archive this custom timed macro? Historical execution records are kept, but it will no longer appear or run.");
        ImGui.BeginDisabled(isBusy || pendingArchiveId is null);
        if (ImGui.Button("Archive"))
        {
            plugin.ArchiveTimedMacro(venue, pendingArchiveId!.Value);
            drafts.Remove(pendingArchiveId.Value);
            pendingArchiveId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Keep"))
        {
            pendingArchiveId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private TimedMacroDraft GetDraft(TimedMacroSummary macro)
    {
        if (!drafts.TryGetValue(macro.TimedMacroId, out var draft))
        {
            draft = TimedMacroDraft.From(macro);
            drafts[macro.TimedMacroId] = draft;
            return draft;
        }

        if (!draft.Dirty && draft.SourceUpdatedAt != macro.UpdatedAt)
            draft.Load(macro);

        return draft;
    }

    private void ResetForVenueChange(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
            return;

        activeProfileId = venue.ProfileId;
        drafts.Clear();
        newMacroName = string.Empty;
        newMacroText = string.Empty;
        newMacroIntervalMinutes = 30;
        newMacroEnabled = true;
        pendingArchiveId = null;
        requestArchivePopup = false;
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

    private static void DrawCountdown(
        TimedMacroSummary macro,
        TimedMacroOpeningSummary? opening,
        bool atAddress,
        DateTimeOffset now)
    {
        if (macro.RequiresActiveOpening && (opening is null || !atAddress))
        {
            ImGui.TextDisabled("Paused");
            return;
        }

        if (macro.NextDueAt is not { } dueAt)
        {
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.25f, 1f), "Ready");
            return;
        }

        var remaining = dueAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.25f, 1f), "Due now");
            return;
        }

        ImGui.TextUnformatted(FormatRemaining(remaining));
    }

    private static string FormatInterval(int minutes) =>
        minutes % 60 == 0
            ? $"{minutes / 60}h"
            : $"{minutes}m";

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        return $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private sealed class TimedMacroDraft
    {
        public string DisplayName = string.Empty;
        public string MacroText = string.Empty;
        public int IntervalMinutes;
        public bool Enabled;
        public bool Dirty;
        public DateTimeOffset? SourceUpdatedAt;

        public static TimedMacroDraft From(TimedMacroSummary macro)
        {
            var draft = new TimedMacroDraft();
            draft.Load(macro);
            return draft;
        }

        public void Load(TimedMacroSummary macro)
        {
            DisplayName = macro.DisplayName;
            MacroText = macro.MacroText ?? string.Empty;
            IntervalMinutes = macro.IntervalMinutes;
            Enabled = macro.Enabled;
            SourceUpdatedAt = macro.UpdatedAt;
            Dirty = false;
        }
    }
}
