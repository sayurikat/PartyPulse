using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Services;
using PartyPulse.Staff;

namespace PartyPulse.Windows;

public sealed class StaffTabRenderer(Plugin plugin)
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm";
    private const string ProxyPayoutPopupName = "Confirm proxy Staff payout###PartyPulseStaffProxyPayout";
    private const string RepaymentPopupName = "Confirm Staff repayment###PartyPulseStaffRepayment";
    private const string OvertimePopupName = "Confirm Staff overtime###PartyPulseStaffOvertime";
    private const string CancelAbsencePopupName = "Cancel Staff absence###PartyPulseStaffCancelAbsence";

    private static readonly Vector4 PositiveBalanceColor = new(1f, 0.72f, 0.25f, 1f);
    private static readonly Vector4 NegativeBalanceColor = new(0.95f, 0.25f, 0.25f, 1f);
    private static readonly Vector4 ZeroBalanceColor = new(0.35f, 0.85f, 0.45f, 1f);

    private Guid activeProfileId;
    private long selectedOpeningId;
    private long selectedCharacterLinkStaffId;
    private long selectedPayoutStaffId;

    private long editingJobId;
    private string jobName = string.Empty;
    private int jobRate;
    private bool jobArchived;

    private long editingStaffId;
    private string staffName = string.Empty;
    private long staffJobId;
    private int staffVenueUserId;
    private bool customRateEnabled;
    private int customRate;
    private int fixedAmount;
    private string staffNote = string.Empty;
    private bool staffArchived;

    private readonly Dictionary<long, AttendanceRowState> attendanceRows = new();
    private readonly Dictionary<long, string> clockOutTextByEntry = new();
    private readonly Dictionary<long, string> clockOutErrorByEntry = new();
    private readonly HashSet<long> additionalEntryStaffIds = new();
    private PendingStaffTimeEntry? pendingOvertimeEntry;
    private bool openOvertimePopup;
    private long pendingCancelAbsenceId;
    private bool openCancelAbsencePopup;
    private string absenceCancelReason = string.Empty;

    private long pendingCancelEntryId;
    private long pendingCancelTransactionId;
    private bool openCancelEntryPopup;
    private bool openCancelTransactionPopup;
    private bool openProxyPayoutPopup;
    private bool openRepaymentPopup;
    private bool proxyTrustConfirmed;
    private bool proxyDeliveryConfirmed;
    private bool repaymentAmountConfirmed;
    private bool repaymentReceivedConfirmed;
    private string pendingPayoutTargetName = string.Empty;
    private string pendingPayoutTargetWorld = string.Empty;
    private string cancelReason = string.Empty;

    public void Draw(VenueConnectionConfiguration venue)
    {
        ResetForVenue(venue);
        plugin.EnsureStaffLoaded(venue);
        plugin.EnsureCourtLoaded(venue);

        var snapshot = plugin.Staff.GetSnapshot(venue);
        var busy = plugin.Staff.IsBusy(venue.ProfileId) ||
                   plugin.Court.IsBusy(venue.ProfileId);

        PartyPulseUi.PageHeader("Staff", "Account for attendance, manage staff and jobs, review time entries, and process payouts.");

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Refresh Staff"))
        {
            plugin.RefreshStaff(venue);
            plugin.RefreshCourt(venue);
        }
        ImGui.EndDisabled();

        if (snapshot.Status != StaffManagementStatus.Ready || snapshot.View is null)
        {
            ImGui.TextWrapped(snapshot.Message);
            return;
        }

        var view = snapshot.View;
        var canManageAttendance =
            view.Capabilities.CanManage || view.Capabilities.CanManageCourtAttendance;
        SelectDefaults(view, venue);
        DrawOpeningSelector(venue, view);

        if (canManageAttendance)
        {
            DrawClocking(venue, view, busy);
        }

        if (view.Capabilities.CanManage)
        {
            ImGui.Separator();
            DrawStaffListings(venue, view, busy);
            ImGui.Separator();
            DrawCharacterLinks(venue, view, busy);
        }

        if (view.Capabilities.CanManageJobs)
        {
            ImGui.Separator();
            DrawJobs(venue, view, busy);
        }

        ImGui.Separator();
        DrawTimeEntries(venue, view, busy, canManageAttendance);

        if (view.Capabilities.CanPay)
        {
            ImGui.Separator();
            DrawPayout(venue, view, busy);
        }

        DrawTimeEntryCancellationPopup(venue, busy);
        DrawAbsenceCancellationPopup(venue, busy);
        DrawOvertimePopup(venue, busy);
        DrawTransactionCancellationPopup(venue, busy);
        DrawProxyPayoutPopup(venue, view, busy);
        DrawRepaymentPopup(venue, view, busy);
    }

    private void DrawOpeningSelector(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view)
    {
        var openings = view.Openings.ToArray();
        if (openings.Length == 0)
        {
            ImGui.TextDisabled("No venue openings are available for clock-ins.");
            return;
        }

        var selected = openings.FirstOrDefault(
                           opening => opening.OpeningId == selectedOpeningId) ?? openings[0];
        selectedOpeningId = selected.OpeningId;

        if (!ImGui.BeginCombo("Opening", OpeningLabel(venue, selected)))
        {
            return;
        }

        foreach (var opening in openings)
        {
            var isSelected = opening.OpeningId == selectedOpeningId;
            if (ImGui.Selectable(OpeningLabel(venue, opening), isSelected))
            {
                selectedOpeningId = opening.OpeningId;
                ResetTimeSuggestions(venue, opening);
            }

            if (isSelected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private void DrawClocking(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.CollapsingHeader(
                "Clock-ins / absences",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var opening = view.Openings.FirstOrDefault(
            candidate => candidate.OpeningId == selectedOpeningId);
        var activeStaff = view.StaffMembers
            .Where(static member => member.ArchivedAt is null)
            .OrderBy(static member => member.DisplayName)
            .ToArray();
        if (opening is null || activeStaff.Length == 0)
        {
            ImGui.TextDisabled("An active staff listing and opening are required.");
            return;
        }

        var activeAbsenceStaffIds = view.Absences
            .Where(item => item.OpeningId == opening.OpeningId && item.CancelledAt is null)
            .Select(static item => item.StaffMemberId)
            .ToHashSet();
        var activeStaffIds = activeStaff
            .Select(static member => member.StaffMemberId)
            .ToHashSet();
        var accountedStaffIds = view.TimeEntries
            .Where(item => item.OpeningId == opening.OpeningId && item.Status != "cancelled")
            .Select(static item => item.StaffMemberId)
            .Concat(activeAbsenceStaffIds)
            .Where(activeStaffIds.Contains)
            .ToHashSet();
        var pendingStaff = activeStaff
            .Where(member =>
                !accountedStaffIds.Contains(member.StaffMemberId) ||
                additionalEntryStaffIds.Contains(member.StaffMemberId))
            .ToArray();

        ImGui.TextDisabled(
            $"{accountedStaffIds.Count} accounted for; {pendingStaff.Length} awaiting a clock-in or absence.");
        ImGui.SameLine();
        ImGui.TextDisabled(
            $"Times use {VenueTimeZone.Resolve(venue).DisplayName} ({TimeFormat}).");

        if (pendingStaff.Length == 0)
        {
            ImGui.TextColored(PartyPulseUi.Success, "All listed staff are accounted for this opening.");
            return;
        }

        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable(
                "StaffAttendanceTable",
                6,
                flags,
                new Vector2(0, 0),
                1040f * ImGuiHelpers.GlobalScale))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Staff", ImGuiTableColumnFlags.WidthFixed, 225f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Clock-in time", ImGuiTableColumnFlags.WidthFixed, 190f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Quick time", ImGuiTableColumnFlags.WidthFixed, 225f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Clock in", ImGuiTableColumnFlags.WidthFixed, 92f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Absence", ImGuiTableColumnFlags.WidthFixed, 155f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Confirm", ImGuiTableColumnFlags.WidthFixed, 125f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var member in pendingStaff)
        {
            ImGui.PushID($"staff-attendance-{member.StaffMemberId}");
            var state = GetAttendanceRowState(venue, member.StaffMemberId);
            var firstSeen = view.FirstSeen
                .Where(item =>
                    item.OpeningId == opening.OpeningId &&
                    item.StaffMemberId == member.StaffMemberId)
                .OrderBy(static item => item.FirstSeenAt)
                .FirstOrDefault();

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(member.DisplayName);
            ImGui.TextDisabled(
                $"{member.JobName} · {member.EffectiveHourlyRateGil:N0}/h + " +
                $"{member.CustomFixedAmountGil:N0} fixed");
            if (firstSeen is not null)
            {
                ImGui.TextColored(
                    PartyPulseUi.Info,
                    $"First seen {VenueTimeZone.Format(venue, firstSeen.FirstSeenAt, "t")}");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{firstSeen.CharacterName} @ {firstSeen.WorldName}");
                }
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##clock-in", ref state.ClockInText, 32))
            {
                state.ClockInSource = "manual";
                state.ExactFirstSeenAt = null;
                state.Error = string.Empty;
            }
            if (state.Error.Length > 0)
            {
                ImGui.TextColored(PartyPulseUi.Danger, state.Error);
            }

            ImGui.TableSetColumnIndex(2);
            if (ImGui.SmallButton("Now"))
            {
                state.ClockInText = FormatInputTime(venue, DateTimeOffset.UtcNow);
                state.ClockInSource = "now";
                state.ExactFirstSeenAt = null;
                state.Error = string.Empty;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Start"))
            {
                state.ClockInText = FormatInputTime(venue, opening.OpensAt);
                state.ClockInSource = "opening_start";
                state.ExactFirstSeenAt = null;
                state.Error = string.Empty;
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(firstSeen is null);
            if (ImGui.SmallButton("Seen") && firstSeen is not null)
            {
                state.ClockInText = FormatInputTime(venue, firstSeen.FirstSeenAt);
                state.ClockInSource = "first_seen";
                state.ExactFirstSeenAt = firstSeen.FirstSeenAt;
                state.Error = string.Empty;
            }
            ImGui.EndDisabled();
            if (firstSeen is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("No first-seen record is available for this opening.");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("-15m"))
            {
                if (TryParseTime(venue, state.ClockInText, out var parsed, out var error))
                {
                    state.ClockInText = FormatInputTime(venue, RoundDownQuarterHour(parsed));
                    state.ClockInSource = "manual";
                    state.ExactFirstSeenAt = null;
                    state.Error = string.Empty;
                }
                else
                {
                    state.Error = error;
                }
            }

            ImGui.TableSetColumnIndex(3);
            ImGui.BeginDisabled(busy);
            if (ImGui.Button("Check in", new Vector2(-1, 0)))
            {
                SubmitClockIn(venue, opening, member, state);
            }
            ImGui.EndDisabled();

            ImGui.TableSetColumnIndex(4);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo(
                    "##absence-reason",
                    state.AbsenceReasonCode == "unplanned"
                        ? "Unplanned absent"
                        : "Planned absent"))
            {
                if (ImGui.Selectable("Planned absent", state.AbsenceReasonCode == "planned"))
                {
                    state.AbsenceReasonCode = "planned";
                }

                if (ImGui.Selectable("Unplanned absent", state.AbsenceReasonCode == "unplanned"))
                {
                    state.AbsenceReasonCode = "unplanned";
                }

                ImGui.EndCombo();
            }

            ImGui.TableSetColumnIndex(5);
            ImGui.BeginDisabled(busy);
            if (ImGui.Button("Confirm absent", new Vector2(-1, 0)))
            {
                plugin.SetStaffAbsence(
                    venue,
                    opening.OpeningId,
                    new SetStaffAbsenceRequest(member.StaffMemberId, state.AbsenceReasonCode));
                additionalEntryStaffIds.Remove(member.StaffMemberId);
            }
            ImGui.EndDisabled();

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private AttendanceRowState GetAttendanceRowState(
        VenueConnectionConfiguration venue,
        long staffMemberId)
    {
        if (!attendanceRows.TryGetValue(staffMemberId, out var state))
        {
            state = new AttendanceRowState
            {
                ClockInText = FormatInputTime(venue, DateTimeOffset.UtcNow)
            };
            attendanceRows[staffMemberId] = state;
        }

        return state;
    }

    private void SubmitClockIn(
        VenueConnectionConfiguration venue,
        StaffOpeningSummary opening,
        StaffMemberSummary member,
        AttendanceRowState state)
    {
        if (!TryParseTime(venue, state.ClockInText, out var clockIn, out var error))
        {
            state.Error = error;
            return;
        }

        state.Error = string.Empty;
        var effectiveClockIn = state.ClockInSource == "first_seen" &&
                               state.ExactFirstSeenAt is { } exactFirstSeenAt
            ? exactFirstSeenAt
            : clockIn;
        var pending = new PendingStaffTimeEntry(
            null,
            member.StaffMemberId,
            opening.OpeningId,
            effectiveClockIn,
            null,
            state.ClockInSource);
        if (IsOvertime(opening, effectiveClockIn, null))
        {
            QueueOvertimeConfirmation(pending);
            return;
        }

        SubmitTimeEntry(venue, pending, false);
        additionalEntryStaffIds.Remove(member.StaffMemberId);
    }

    private void DrawStaffListings(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.CollapsingHeader("Staff listings"))
        {
            return;
        }

        foreach (var member in view.StaffMembers
                     .OrderBy(member => member.ArchivedAt is not null)
                     .ThenBy(member => member.JobName)
                     .ThenBy(member => member.DisplayName))
        {
            ImGui.PushID($"staff-member-{member.StaffMemberId}");
            ImGui.TextUnformatted(
                $"{member.JobName} — {member.DisplayName} — " +
                $"{member.EffectiveHourlyRateGil:N0}/h + " +
                $"{member.CustomFixedAmountGil:N0} fixed" +
                (member.ArchivedAt is null ? string.Empty : " (archived)"));
            ImGui.SameLine();
            ImGui.TextColored(
                BalanceColor(member.StandingBalanceGil),
                $"balance {member.StandingBalanceGil:+#,0;-#,0;0} gil");
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit"))
            {
                LoadMember(member);
            }
            ImGui.PopID();
        }

        var jobs = view.Jobs
            .Where(job => job.ArchivedAt is null)
            .OrderBy(job => job.Name)
            .ToArray();
        if (jobs.Length == 0)
        {
            ImGui.TextDisabled("Create a job definition before adding staff.");
            return;
        }

        if (staffJobId == 0 || jobs.All(job => job.JobDefinitionId != staffJobId))
        {
            staffJobId = jobs[0].JobDefinitionId;
        }

        ImGui.InputText("Display name##StaffMember", ref staffName, 100);
        DrawJobSelector(jobs);
        DrawVenueUserSelector(view);

        ImGui.Checkbox("Custom hourly rate##StaffMember", ref customRateEnabled);
        if (customRateEnabled)
        {
            ImGui.InputInt("Custom gil/hour##StaffMember", ref customRate);
            customRate = Math.Max(0, customRate);
            ImGui.TextDisabled($"Custom hourly salary: {customRate:N0} gil.");
        }

        ImGui.InputInt("Fixed amount per completed entry##StaffMember", ref fixedAmount);
        fixedAmount = Math.Max(0, fixedAmount);
        ImGui.TextDisabled($"Fixed salary component: {fixedAmount:N0} gil.");
        ImGui.InputText("Note##StaffMember", ref staffNote, 500);
        ImGui.Checkbox("Archived##StaffMember", ref staffArchived);

        ImGui.BeginDisabled(busy || string.IsNullOrWhiteSpace(staffName));
        if (ImGui.Button(editingStaffId == 0
                ? "Create staff listing"
                : "Save staff listing"))
        {
            plugin.SaveStaffMember(
                venue,
                editingStaffId == 0 ? null : editingStaffId,
                new SaveStaffMemberRequest(
                    staffName.Trim(),
                    staffJobId,
                    staffVenueUserId == 0 ? null : staffVenueUserId,
                    customRateEnabled ? customRate : null,
                    fixedAmount,
                    string.IsNullOrWhiteSpace(staffNote) ? null : staffNote.Trim(),
                    staffArchived));
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear##StaffMember"))
        {
            ClearMember();
        }
    }

    private void DrawJobSelector(StaffJobSummary[] jobs)
    {
        var selectedJob = jobs.First(job => job.JobDefinitionId == staffJobId);
        if (!ImGui.BeginCombo("Job##StaffMember", selectedJob.Name))
        {
            return;
        }

        foreach (var job in jobs)
        {
            var isSelected = job.JobDefinitionId == staffJobId;
            if (ImGui.Selectable(
                    $"{job.Name} — {job.HourlyRateGil:N0}/h",
                    isSelected))
            {
                staffJobId = job.JobDefinitionId;
            }

            if (isSelected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private void DrawVenueUserSelector(StaffManagementViewResponse view)
    {
        var users = view.VenueUsers.OrderBy(user => user.DisplayName).ToArray();
        var preview = staffVenueUserId == 0
            ? "No venue user"
            : users.FirstOrDefault(user => user.VenueUserId == staffVenueUserId)?.DisplayName ??
              "No venue user";

        if (!ImGui.BeginCombo("Venue user##StaffMember", preview))
        {
            return;
        }

        if (ImGui.Selectable("No venue user", staffVenueUserId == 0))
        {
            staffVenueUserId = 0;
        }

        foreach (var user in users)
        {
            var isSelected = user.VenueUserId == staffVenueUserId;
            var unavailable = user.AssignedStaffMemberId is not null &&
                              user.AssignedStaffMemberId != editingStaffId;
            ImGui.BeginDisabled(unavailable);
            if (ImGui.Selectable(user.DisplayName, isSelected))
            {
                staffVenueUserId = user.VenueUserId;
            }
            ImGui.EndDisabled();

            if (isSelected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private void DrawCharacterLinks(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.CollapsingHeader("Link target character to Staff"))
        {
            return;
        }

        var staff = view.StaffMembers
            .Where(member => member.ArchivedAt is null)
            .OrderBy(member => member.DisplayName)
            .ToArray();
        if (staff.Length == 0)
        {
            return;
        }

        EnsureSelectedStaff(staff, ref selectedCharacterLinkStaffId);
        var selected = staff.First(
            member => member.StaffMemberId == selectedCharacterLinkStaffId);
        if (ImGui.BeginCombo("Staff listing##CharacterLink", selected.DisplayName))
        {
            foreach (var member in staff)
            {
                var isSelected = member.StaffMemberId == selectedCharacterLinkStaffId;
                if (ImGui.Selectable(member.DisplayName, isSelected))
                {
                    selectedCharacterLinkStaffId = member.StaffMemberId;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (!plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetError))
        {
            ImGui.TextDisabled(targetError);
            return;
        }

        var link = view.Characters.FirstOrDefault(character =>
            character.CharacterName.Equals(
                target!.CharacterName,
                StringComparison.OrdinalIgnoreCase) &&
            character.WorldName.Equals(
                target.WorldName,
                StringComparison.OrdinalIgnoreCase));
        var linkedName = link?.StaffMemberId is { } linkedStaffId
            ? view.StaffMembers.FirstOrDefault(
                  member => member.StaffMemberId == linkedStaffId)?.DisplayName ??
              $"#{linkedStaffId}"
            : "none";
        ImGui.TextUnformatted(
            $"Target: {target.DisplayName} — current link: {linkedName}");

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Link target"))
        {
            plugin.LinkStaffCharacter(
                venue,
                new LinkStaffCharacterRequest(
                    selectedCharacterLinkStaffId,
                    target.CharacterName,
                    target.WorldName));
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(busy || link?.StaffMemberId is null);
        if (ImGui.Button("Unlink target"))
        {
            plugin.LinkStaffCharacter(
                venue,
                new LinkStaffCharacterRequest(
                    null,
                    target.CharacterName,
                    target.WorldName));
        }
        ImGui.EndDisabled();
    }

    private void DrawJobs(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.CollapsingHeader("Job definitions (owner)"))
        {
            return;
        }

        foreach (var job in view.Jobs
                     .OrderBy(job => job.ArchivedAt is not null)
                     .ThenBy(job => job.Name))
        {
            ImGui.PushID($"staff-job-{job.JobDefinitionId}");
            ImGui.TextUnformatted(
                $"{job.Name} — {job.HourlyRateGil:N0} gil/hour" +
                (job.ArchivedAt is null ? string.Empty : " (archived)"));
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit"))
            {
                editingJobId = job.JobDefinitionId;
                jobName = job.Name;
                jobRate = (int)Math.Min(int.MaxValue, job.HourlyRateGil);
                jobArchived = job.ArchivedAt is not null;
            }
            ImGui.PopID();
        }

        ImGui.InputText("Job name", ref jobName, 100);
        ImGui.InputInt("Salary per hour", ref jobRate);
        jobRate = Math.Max(0, jobRate);
        ImGui.TextDisabled($"Saved hourly salary: {jobRate:N0} gil.");
        ImGui.Checkbox("Archived##StaffJob", ref jobArchived);

        ImGui.BeginDisabled(busy || string.IsNullOrWhiteSpace(jobName));
        if (ImGui.Button(editingJobId == 0 ? "Create job" : "Save job"))
        {
            plugin.SaveStaffJob(
                venue,
                editingJobId == 0 ? null : editingJobId,
                new SaveStaffJobRequest(jobName.Trim(), jobRate, jobArchived));
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear##StaffJob"))
        {
            editingJobId = 0;
            jobName = string.Empty;
            jobRate = 0;
            jobArchived = false;
        }
    }

    private void DrawTimeEntries(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view,
        bool busy,
        bool canManageAttendance)
    {
        if (!ImGui.CollapsingHeader("Clock-in history", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var opening = view.Openings.FirstOrDefault(
            candidate => candidate.OpeningId == selectedOpeningId);
        if (opening is null)
        {
            ImGui.TextDisabled("Select an opening to view its attendance history.");
            return;
        }

        foreach (var absence in view.Absences
                     .Where(item => item.OpeningId == selectedOpeningId)
                     .OrderBy(static item => item.StaffDisplayName)
                     .ThenBy(static item => item.RecordedAt))
        {
            ImGui.PushID($"staff-absence-{absence.AbsenceId}");
            var reason = absence.ReasonCode == "unplanned"
                ? "Unplanned absent"
                : "Planned absent";
            var status = absence.CancelledAt is null
                ? "confirmed"
                : $"cancelled {VenueTimeZone.Format(venue, absence.CancelledAt.Value, "g")}";
            ImGui.TextUnformatted(
                $"{absence.StaffDisplayName}: {reason} | {status}");
            if (absence.CancelledAt is not null && !string.IsNullOrWhiteSpace(absence.CancelReason))
            {
                ImGui.TextDisabled($"Cancellation: {absence.CancelReason}");
            }

            if (absence.CancelledAt is null && canManageAttendance)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Cancel absence"))
                {
                    pendingCancelAbsenceId = absence.AbsenceId;
                    absenceCancelReason = string.Empty;
                    openCancelAbsencePopup = true;
                }
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }

        foreach (var entry in view.TimeEntries
                     .Where(entry => entry.OpeningId == selectedOpeningId)
                     .OrderBy(entry => entry.StaffDisplayName)
                     .ThenBy(entry => entry.ClockInAt))
        {
            ImGui.PushID($"time-entry-{entry.TimeEntryId}");
            var clockOut = entry.ClockOutAt is { } clockOutAt
                ? VenueTimeZone.Format(venue, clockOutAt, "g")
                : "open";
            var salary = entry.SalaryGil is { } salaryGil
                ? $"{salaryGil:N0} gil"
                : entry.Status;
            var status = entry.Status == "cancelled"
                ? entry.PaidAt is not null ? "cancelled (was paid)" : "cancelled"
                : entry.PaidAt is not null ? "paid" : entry.Status;
            var source = entry.ClockInSource switch
            {
                "first_seen" => "first seen",
                "opening_start" => "opening start",
                _ => entry.ClockInSource
            };
            ImGui.TextUnformatted(
                $"{entry.StaffDisplayName}: " +
                $"{VenueTimeZone.Format(venue, entry.ClockInAt, "g")} – {clockOut} | " +
                $"{salary} | {status} | source: {source}");
            if (entry.IsOvertime)
            {
                ImGui.TextDisabled("Overtime explicitly confirmed; standard pay rate applies.");
            }
            if (entry.PaidAt is not null && entry.PaidToCharacterName is not null)
            {
                ImGui.TextDisabled(
                    entry.PaidViaProxy
                        ? $"Paid by proxy to {entry.PaidToCharacterName} @ {entry.PaidToWorldName}"
                        : $"Paid to {entry.PaidToCharacterName} @ {entry.PaidToWorldName}");
            }

            if (entry.Status == "open" && canManageAttendance)
            {
                DrawClockOutActions(venue, opening, entry, busy);
            }
            else if (entry.Status != "cancelled" && canManageAttendance)
            {
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Add another work entry"))
                {
                    additionalEntryStaffIds.Add(entry.StaffMemberId);
                    var row = GetAttendanceRowState(venue, entry.StaffMemberId);
                    row.ClockInText = FormatInputTime(venue, DateTimeOffset.UtcNow);
                    row.ClockInSource = "manual";
                    row.ExactFirstSeenAt = null;
                    row.Error = string.Empty;
                }
                ImGui.EndDisabled();
            }

            if (canManageAttendance &&
                entry.Status != "cancelled" &&
                (entry.FinancialTransactionId is null || entry.PaidAt is not null))
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Cancel entry"))
                {
                    pendingCancelEntryId = entry.TimeEntryId;
                    cancelReason = string.Empty;
                    openCancelEntryPopup = true;
                }
                ImGui.EndDisabled();
            }

            if (clockOutErrorByEntry.TryGetValue(entry.TimeEntryId, out var entryError) &&
                entryError.Length > 0)
            {
                ImGui.TextWrapped(entryError);
            }

            ImGui.PopID();
        }

        if (openCancelEntryPopup)
        {
            ImGui.OpenPopup(
                "Cancel Staff time entry###PartyPulseStaffCancelEntry");
            openCancelEntryPopup = false;
        }

        if (openCancelAbsencePopup)
        {
            ImGui.OpenPopup(CancelAbsencePopupName);
            openCancelAbsencePopup = false;
        }
    }

    private void DrawClockOutActions(
        VenueConnectionConfiguration venue,
        StaffOpeningSummary opening,
        StaffTimeEntrySummary entry,
        bool busy)
    {
        if (!clockOutTextByEntry.TryGetValue(entry.TimeEntryId, out var clockOutText))
        {
            clockOutText = FormatInputTime(venue, DateTimeOffset.UtcNow);
        }

        ImGui.SetNextItemWidth(180f);
        if (ImGui.InputText("Clock out##Time", ref clockOutText, 32))
        {
            clockOutErrorByEntry.Remove(entry.TimeEntryId);
        }
        clockOutTextByEntry[entry.TimeEntryId] = clockOutText;

        ImGui.SameLine();
        if (ImGui.SmallButton("Now"))
        {
            clockOutTextByEntry[entry.TimeEntryId] =
                FormatInputTime(venue, DateTimeOffset.UtcNow);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Opening end"))
        {
            clockOutTextByEntry[entry.TimeEntryId] =
                FormatInputTime(venue, opening.ClosesAt);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Round up 15 min"))
        {
            if (TryParseTime(
                    venue,
                    clockOutTextByEntry[entry.TimeEntryId],
                    out var parsed,
                    out var error))
            {
                clockOutTextByEntry[entry.TimeEntryId] =
                    FormatInputTime(venue, RoundUpQuarterHour(parsed));
                clockOutErrorByEntry.Remove(entry.TimeEntryId);
            }
            else
            {
                clockOutErrorByEntry[entry.TimeEntryId] = error;
            }
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Clock out##Submit"))
        {
            SubmitClockOut(venue, opening, entry);
        }
        ImGui.EndDisabled();
    }

    private void SubmitClockOut(
        VenueConnectionConfiguration venue,
        StaffOpeningSummary opening,
        StaffTimeEntrySummary entry)
    {
        if (!TryParseTime(
                venue,
                clockOutTextByEntry[entry.TimeEntryId],
                out var clockOut,
                out var error))
        {
            clockOutErrorByEntry[entry.TimeEntryId] = error;
            return;
        }

        if (clockOut <= entry.ClockInAt)
        {
            clockOutErrorByEntry[entry.TimeEntryId] =
                "Clock-out must be after clock-in.";
            return;
        }

        clockOutErrorByEntry.Remove(entry.TimeEntryId);
        var pending = new PendingStaffTimeEntry(
            entry.TimeEntryId,
            entry.StaffMemberId,
            entry.OpeningId,
            entry.ClockInAt,
            clockOut,
            entry.ClockInSource);
        if (IsOvertime(opening, entry.ClockInAt, clockOut))
        {
            QueueOvertimeConfirmation(pending);
            return;
        }

        SubmitTimeEntry(venue, pending, false);
    }

    private void QueueOvertimeConfirmation(PendingStaffTimeEntry pending)
    {
        pendingOvertimeEntry = pending;
        openOvertimePopup = true;
    }

    private void SubmitTimeEntry(
        VenueConnectionConfiguration venue,
        PendingStaffTimeEntry pending,
        bool overtimeConfirmed)
    {
        plugin.SaveStaffTimeEntry(
            venue,
            pending.TimeEntryId,
            new SaveStaffTimeEntryRequest(
                pending.StaffMemberId,
                pending.OpeningId,
                pending.ClockInAt,
                pending.ClockOutAt,
                pending.ClockInSource,
                overtimeConfirmed));
    }

    private void DrawOvertimePopup(
        VenueConnectionConfiguration venue,
        bool busy)
    {
        if (openOvertimePopup)
        {
            ImGui.OpenPopup(OvertimePopupName);
            openOvertimePopup = false;
        }

        if (!ImGui.BeginPopupModal(
                OvertimePopupName,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        if (pendingOvertimeEntry is not { } pending)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextWrapped(
            "This work interval is before or after the selected opening hours. " +
            "Overtime uses the normal Staff pay rate and requires explicit confirmation.");
        ImGui.TextUnformatted(
            $"Clock in: {VenueTimeZone.Format(venue, pending.ClockInAt, "g")}");
        if (pending.ClockOutAt is { } clockOut)
        {
            ImGui.TextUnformatted(
                $"Clock out: {VenueTimeZone.Format(venue, clockOut, "g")}");
        }

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Confirm overtime"))
        {
            SubmitTimeEntry(venue, pending, true);
            additionalEntryStaffIds.Remove(pending.StaffMemberId);
            pendingOvertimeEntry = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Go back"))
        {
            pendingOvertimeEntry = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawAbsenceCancellationPopup(
        VenueConnectionConfiguration venue,
        bool busy)
    {
        if (!ImGui.BeginPopupModal(
                CancelAbsencePopupName,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            "Cancel this absence record? The cancellation remains visible in Clock-in history.");
        ImGui.InputText("Reason (optional)", ref absenceCancelReason, 255);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Cancel absence record"))
        {
            plugin.CancelStaffAbsence(
                venue,
                pendingCancelAbsenceId,
                absenceCancelReason);
            pendingCancelAbsenceId = 0;
            absenceCancelReason = string.Empty;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Keep absence"))
        {
            pendingCancelAbsenceId = 0;
            absenceCancelReason = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static bool TryParseTime(
        VenueConnectionConfiguration venue,
        string text,
        out DateTimeOffset value,
        out string error) =>
        VenueTimeZone.TryParseExact(
            venue,
            text,
            TimeFormat,
            CultureInfo.InvariantCulture,
            out value,
            out error);

    private static bool IsOvertime(
        StaffOpeningSummary opening,
        DateTimeOffset clockIn,
        DateTimeOffset? clockOut) =>
        clockIn < opening.OpensAt ||
        clockIn >= opening.ClosesAt ||
        (clockOut is { } value && value > opening.ClosesAt);

    private static DateTimeOffset RoundDownQuarterHour(DateTimeOffset value)
    {
        var minute = value.Minute - value.Minute % 15;
        return new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            minute,
            0,
            value.Offset);
    }

    private static DateTimeOffset RoundUpQuarterHour(DateTimeOffset value)
    {
        var rounded = RoundDownQuarterHour(value);
        return rounded == value ? rounded : rounded.AddMinutes(15);
    }

    private void DrawPayout(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.CollapsingHeader(
                "Pay Staff with Dropbox",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var staff = view.StaffMembers
            .Where(member => member.ArchivedAt is null && member.StandingBalanceGil != 0)
            .OrderBy(member => member.JobName)
            .ThenBy(member => member.DisplayName)
            .ToArray();
        if (staff.Length > 0)
        {
            EnsureSelectedStaff(staff, ref selectedPayoutStaffId);
            var selected = staff.First(member => member.StaffMemberId == selectedPayoutStaffId);
            if (ImGui.BeginCombo(
                    "Staff balance",
                    $"{selected.JobName} — {selected.DisplayName} — {selected.StandingBalanceGil:+#,0;-#,0;0} gil"))
            {
                foreach (var member in staff)
                {
                    var isSelected = member.StaffMemberId == selectedPayoutStaffId;
                    if (ImGui.Selectable(
                            $"{member.JobName} — {member.DisplayName} — {member.StandingBalanceGil:+#,0;-#,0;0} gil",
                            isSelected))
                    {
                        selectedPayoutStaffId = member.StaffMemberId;
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            ImGui.TextColored(
                BalanceColor(selected.StandingBalanceGil),
                selected.StandingBalanceGil > 0
                    ? $"Venue owes {selected.DisplayName}: {selected.StandingBalanceGil:N0} gil"
                    : $"{selected.DisplayName} owes venue: {-selected.StandingBalanceGil:N0} gil");
            ImGui.TextDisabled(
                $"Unpaid salary {selected.UnpaidSalaryGil:N0} − prior paid-entry deductions " +
                $"{selected.SalaryDeductionGil:N0} = {selected.StandingBalanceGil:+#,0;-#,0;0} gil.");

            if (selected.RequiresCourtSettlement)
            {
                ImGui.TextWrapped(
                    $"{selected.DisplayName} has unsettled Court items " +
                    $"(sales {selected.UnsettledCourtGil:N0} gil, " +
                    $"corrections {selected.UnsettledAdjustmentGil:+#,0;-#,0;0} gil). " +
                    "Use Court Services to create one combined Court/salary settlement instead.");
            }

            if (selected.StandingBalanceGil < 0)
            {
                ImGui.TextDisabled(
                    "No Dropbox trade is started. Record this only after the staff member has paid the finance person.");
                ImGui.BeginDisabled(busy || selected.RequiresCourtSettlement);
                if (ImGui.Button("Record repayment received"))
                {
                    repaymentAmountConfirmed = false;
                    repaymentReceivedConfirmed = false;
                    openRepaymentPopup = true;
                }
                ImGui.EndDisabled();
            }
            else if (plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetError))
            {
                ImGui.TextUnformatted($"Trade target: {target!.DisplayName}");
                var character = view.Characters.FirstOrDefault(value =>
                    value.CharacterName.Equals(target.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                    value.WorldName.Equals(target.WorldName, StringComparison.OrdinalIgnoreCase));
                var linkedToSelected =
                    character?.StaffMemberId == selected.StaffMemberId ||
                    (selected.VenueUserId is not null && character?.VenueUserId == selected.VenueUserId);
                if (!linkedToSelected)
                {
                    ImGui.TextColored(
                        NegativeBalanceColor,
                        "Target is not linked to the selected Staff listing. This will be a proxy payout.");
                }

                ImGui.BeginDisabled(busy || selected.RequiresCourtSettlement);
                if (ImGui.Button("Create payout and execute Dropbox"))
                {
                    if (linkedToSelected)
                    {
                        plugin.CreateStaffPayout(
                            venue,
                            new CreateStaffPayoutRequest(
                                selected.StaffMemberId,
                                target.CharacterName,
                                target.WorldName,
                                false,
                                false,
                                null));
                    }
                    else
                    {
                        pendingPayoutTargetName = target.CharacterName;
                        pendingPayoutTargetWorld = target.WorldName;
                        proxyTrustConfirmed = false;
                        proxyDeliveryConfirmed = false;
                        openProxyPayoutPopup = true;
                    }
                }
                ImGui.EndDisabled();
            }
            else
            {
                ImGui.TextDisabled(targetError);
            }
        }
        else
        {
            ImGui.TextDisabled("All Staff salary balances are zero.");
        }

        var court = plugin.Court.GetSnapshot(venue).View;
        if (court is null)
            return;

        foreach (var transaction in court.Transactions.Where(transaction =>
                     transaction.TransactionType == "staff_payout" &&
                     transaction.Status == "pending"))
        {
            ImGui.PushID($"staff-payout-{transaction.TransactionId}");
            ImGui.TextUnformatted(
                $"Pending payout #{transaction.TransactionId}: " +
                $"salary {transaction.SalaryGil:N0}, deductions {transaction.AdjustmentGil:N0}, " +
                $"trade {transaction.TradeAmountGil:N0} gil to {transaction.StaffDisplayName}" +
                (transaction.PayoutViaProxy ? " (proxy)" : string.Empty));

            if (transaction.CanExecuteTrade)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Execute with Dropbox"))
                    plugin.ExecuteCourtTransactionTrade(venue, transaction);
                ImGui.EndDisabled();
            }

            if (transaction.CanConfirm)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Confirm Trade Success"))
                    plugin.ConfirmCourtTransaction(venue, transaction.TransactionId);
                ImGui.EndDisabled();
            }

            if (transaction.CanCancel)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Cancel pending"))
                {
                    pendingCancelTransactionId = transaction.TransactionId;
                    cancelReason = string.Empty;
                    openCancelTransactionPopup = true;
                }
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }

        if (openCancelTransactionPopup)
        {
            ImGui.OpenPopup("Cancel Staff payout###PartyPulseStaffCancelPayout");
            openCancelTransactionPopup = false;
        }
        if (openProxyPayoutPopup)
        {
            ImGui.OpenPopup(ProxyPayoutPopupName);
            openProxyPayoutPopup = false;
        }
        if (openRepaymentPopup)
        {
            ImGui.OpenPopup(RepaymentPopupName);
            openRepaymentPopup = false;
        }
    }

    private void DrawProxyPayoutPopup(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.BeginPopupModal(ProxyPayoutPopupName, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var selected = view.StaffMembers.FirstOrDefault(value =>
            value.StaffMemberId == selectedPayoutStaffId);
        if (selected is null)
        {
            ImGui.TextDisabled("The selected Staff listing is no longer available.");
        }
        else
        {
            ImGui.TextWrapped(
                $"Pay {selected.StandingBalanceGil:N0} gil for {selected.DisplayName} to " +
                $"{pendingPayoutTargetName} @ {pendingPayoutTargetWorld} as a proxy?");
            ImGui.Checkbox(
                "I trust this character to receive the gil for the selected staff member",
                ref proxyTrustConfirmed);
            ImGui.Checkbox(
                "I understand the staff record will identify this character as the payment proxy",
                ref proxyDeliveryConfirmed);

            ImGui.BeginDisabled(
                busy ||
                selected.RequiresCourtSettlement ||
                !proxyTrustConfirmed ||
                !proxyDeliveryConfirmed);
            if (ImGui.Button("Confirm proxy payout"))
            {
                plugin.CreateStaffPayout(
                    venue,
                    new CreateStaffPayoutRequest(
                        selected.StaffMemberId,
                        pendingPayoutTargetName,
                        pendingPayoutTargetWorld,
                        true,
                        false,
                        "Trusted proxy payout."));
                ClearPayoutConfirmation();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel proxy payout"))
        {
            ClearPayoutConfirmation();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawRepaymentPopup(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view,
        bool busy)
    {
        if (!ImGui.BeginPopupModal(RepaymentPopupName, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var selected = view.StaffMembers.FirstOrDefault(value =>
            value.StaffMemberId == selectedPayoutStaffId);
        if (selected is null || selected.StandingBalanceGil >= 0)
        {
            ImGui.TextDisabled("The selected negative Staff balance is no longer available.");
        }
        else
        {
            var repayment = -selected.StandingBalanceGil;
            ImGui.TextColored(
                NegativeBalanceColor,
                $"Record {repayment:N0} gil received from {selected.DisplayName}?");
            ImGui.Checkbox(
                $"I verified the repayment amount is exactly {repayment:N0} gil",
                ref repaymentAmountConfirmed);
            ImGui.Checkbox(
                "I confirm a finance person has received the gil",
                ref repaymentReceivedConfirmed);

            ImGui.BeginDisabled(
                busy ||
                selected.RequiresCourtSettlement ||
                !repaymentAmountConfirmed ||
                !repaymentReceivedConfirmed);
            if (ImGui.Button("Confirm repayment received"))
            {
                plugin.CreateStaffPayout(
                    venue,
                    new CreateStaffPayoutRequest(
                        selected.StaffMemberId,
                        null,
                        null,
                        false,
                        true,
                        "Negative Staff balance received by finance."));
                ClearPayoutConfirmation();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("Do not record repayment"))
        {
            ClearPayoutConfirmation();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void ClearPayoutConfirmation()
    {
        openProxyPayoutPopup = false;
        openRepaymentPopup = false;
        proxyTrustConfirmed = false;
        proxyDeliveryConfirmed = false;
        repaymentAmountConfirmed = false;
        repaymentReceivedConfirmed = false;
        pendingPayoutTargetName = string.Empty;
        pendingPayoutTargetWorld = string.Empty;
    }

    private static Vector4 BalanceColor(long balance) =>
        balance > 0 ? PositiveBalanceColor : balance < 0 ? NegativeBalanceColor : ZeroBalanceColor;

    private void DrawTimeEntryCancellationPopup(
        VenueConnectionConfiguration venue,
        bool busy)
    {
        if (!ImGui.BeginPopupModal(
                "Cancel Staff time entry###PartyPulseStaffCancelEntry",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Cancel time entry #{pendingCancelEntryId}? Locked and paid entries remain in the audit history. " +
            "If salary was already paid, that amount becomes a deduction from the staff member's next salary settlement. " +
            "A corrected replacement entry can then be recorded without rewriting the confirmed payout.");
        ImGui.InputText("Reason", ref cancelReason, 255);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Confirm cancellation"))
        {
            plugin.CancelStaffTimeEntry(venue, pendingCancelEntryId, cancelReason);
            pendingCancelEntryId = 0;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Keep entry"))
        {
            pendingCancelEntryId = 0;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawTransactionCancellationPopup(
        VenueConnectionConfiguration venue,
        bool busy)
    {
        if (!ImGui.BeginPopupModal(
                "Cancel Staff payout###PartyPulseStaffCancelPayout",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Cancel pending Staff payout #{pendingCancelTransactionId}? " +
            "Its salary entries will be released for a new payout.");
        ImGui.InputText("Reason##StaffPayout", ref cancelReason, 255);
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Confirm payout cancellation"))
        {
            plugin.CancelCourtTransaction(
                venue,
                pendingCancelTransactionId,
                cancelReason);
            pendingCancelTransactionId = 0;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Keep payout"))
        {
            pendingCancelTransactionId = 0;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static string OpeningLabel(
        VenueConnectionConfiguration venue,
        StaffOpeningSummary opening) =>
        $"{(opening.IsActive ? "Active — " : string.Empty)}" +
        $"{opening.Title ?? $"Opening #{opening.OpeningId}"} — " +
        VenueTimeZone.Format(venue, opening.OpensAt, "g");

    private static string FormatInputTime(
        VenueConnectionConfiguration venue,
        DateTimeOffset value) =>
        VenueTimeZone.Format(
            venue,
            value,
            TimeFormat,
            CultureInfo.InvariantCulture);

    private static void EnsureSelectedStaff(
    StaffMemberSummary[] staff,
    ref long selectedStaffId)
    {
        if (staff.Length == 0)
        {
            selectedStaffId = 0;
            return;
        }

        long currentSelectedStaffId = selectedStaffId;

        if (currentSelectedStaffId == 0 ||
            staff.All(member => member.StaffMemberId != currentSelectedStaffId))
        {
            selectedStaffId = staff[0].StaffMemberId;
        }
    }

    private void LoadMember(StaffMemberSummary member)
    {
        editingStaffId = member.StaffMemberId;
        staffName = member.DisplayName;
        staffJobId = member.JobDefinitionId;
        staffVenueUserId = member.VenueUserId.GetValueOrDefault();
        customRateEnabled = member.CustomHourlyRateGil is not null;
        customRate = (int)Math.Min(
            int.MaxValue,
            member.CustomHourlyRateGil.GetValueOrDefault());
        fixedAmount = (int)Math.Min(int.MaxValue, member.CustomFixedAmountGil);
        staffNote = member.Note ?? string.Empty;
        staffArchived = member.ArchivedAt is not null;
    }

    private void ClearMember()
    {
        editingStaffId = 0;
        staffName = string.Empty;
        staffJobId = 0;
        staffVenueUserId = 0;
        customRateEnabled = false;
        customRate = 0;
        fixedAmount = 0;
        staffNote = string.Empty;
        staffArchived = false;
    }

    private void SelectDefaults(
        StaffManagementViewResponse view,
        VenueConnectionConfiguration venue)
    {
        if (selectedOpeningId == 0)
        {
            selectedOpeningId = view.DefaultOpeningId ??
                                view.Openings.FirstOrDefault()?.OpeningId ??
                                0;
        }

    }

    private void ResetTimeSuggestions(
        VenueConnectionConfiguration venue,
        StaffOpeningSummary opening)
    {
        attendanceRows.Clear();
        clockOutTextByEntry.Clear();
        clockOutErrorByEntry.Clear();
        additionalEntryStaffIds.Clear();
        pendingOvertimeEntry = null;
    }

    private void ResetForVenue(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
        {
            return;
        }

        activeProfileId = venue.ProfileId;
        selectedOpeningId = 0;
        selectedCharacterLinkStaffId = 0;
        selectedPayoutStaffId = 0;
        editingJobId = 0;
        jobName = string.Empty;
        jobRate = 0;
        jobArchived = false;
        ClearMember();
        attendanceRows.Clear();
        clockOutTextByEntry.Clear();
        clockOutErrorByEntry.Clear();
        additionalEntryStaffIds.Clear();
        pendingOvertimeEntry = null;
        openOvertimePopup = false;
        pendingCancelAbsenceId = 0;
        openCancelAbsencePopup = false;
        absenceCancelReason = string.Empty;
        pendingCancelEntryId = 0;
        pendingCancelTransactionId = 0;
        openCancelEntryPopup = false;
        openCancelTransactionPopup = false;
        ClearPayoutConfirmation();
        cancelReason = string.Empty;
    }

    private sealed class AttendanceRowState
    {
        public string ClockInText = string.Empty;
        public string ClockInSource = "manual";
        public DateTimeOffset? ExactFirstSeenAt;
        public string AbsenceReasonCode = "planned";
        public string Error = string.Empty;
    }

    private sealed record PendingStaffTimeEntry(
        long? TimeEntryId,
        long StaffMemberId,
        long OpeningId,
        DateTimeOffset ClockInAt,
        DateTimeOffset? ClockOutAt,
        string ClockInSource);
}
