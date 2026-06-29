using System;
using System.Globalization;
using System.Linq;
using Dalamud.Bindings.ImGui;
using PartyPulse.Api;
using PartyPulse.Models;
using PartyPulse.Services;
using PartyPulse.Staff;

namespace PartyPulse.Windows;

public sealed class StaffTabRenderer(Plugin plugin)
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    private Guid activeProfileId;
    private long selectedOpeningId;
    private long selectedStaffId;

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

    private string clockInText = string.Empty;
    private string clockOutText = string.Empty;
    private bool includeClockOut;
    private string timeError = string.Empty;

    private long pendingCancelEntryId;
    private long pendingCancelTransactionId;
    private string cancelReason = string.Empty;

    public void Draw(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("Staff"))
        {
            return;
        }

        ResetForVenue(venue);
        plugin.EnsureStaffLoaded(venue);
        plugin.EnsureCourtLoaded(venue);

        var snapshot = plugin.Staff.GetSnapshot(venue);
        var busy = plugin.Staff.IsBusy(venue.ProfileId) ||
                   plugin.Court.IsBusy(venue.ProfileId);

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
            ImGui.EndTabItem();
            return;
        }

        var view = snapshot.View;
        SelectDefaults(view, venue);
        DrawOpeningSelector(venue, view);

        if (view.Capabilities.CanManage)
        {
            DrawClocking(venue, view, busy);
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
        DrawTimeEntries(venue, view, busy);

        if (view.Capabilities.CanPay)
        {
            ImGui.Separator();
            DrawPayout(venue, view, busy);
        }

        DrawTimeEntryCancellationPopup(venue, busy);
        DrawTransactionCancellationPopup(venue, busy);
        ImGui.EndTabItem();
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
                "Clock-ins / clock-outs",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var staff = view.StaffMembers
            .Where(member => member.ArchivedAt is null)
            .OrderBy(member => member.DisplayName)
            .ToArray();
        var opening = view.Openings.FirstOrDefault(
            candidate => candidate.OpeningId == selectedOpeningId);
        if (staff.Length == 0 || opening is null)
        {
            ImGui.TextDisabled("An active staff listing and opening are required.");
            return;
        }

        EnsureSelectedStaff(staff);
        var selected = staff.First(member => member.StaffMemberId == selectedStaffId);
        if (ImGui.BeginCombo("Staff member##Clock", selected.DisplayName))
        {
            foreach (var member in staff)
            {
                var isSelected = member.StaffMemberId == selectedStaffId;
                if (ImGui.Selectable(member.DisplayName, isSelected))
                {
                    selectedStaffId = member.StaffMemberId;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(
            $"Rate {selected.EffectiveHourlyRateGil:N0}/hour + " +
            $"{selected.CustomFixedAmountGil:N0} fixed");

        ImGui.InputText("Clock in##StaffTime", ref clockInText, 32);
        ImGui.SameLine();
        if (ImGui.SmallButton("Now##ClockIn"))
        {
            clockInText = FormatInputTime(venue, DateTimeOffset.UtcNow);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Opening start"))
        {
            clockInText = FormatInputTime(venue, opening.OpensAt);
        }

        ImGui.Checkbox("Set clock-out now##StaffTime", ref includeClockOut);
        if (includeClockOut)
        {
            ImGui.InputText("Clock out##StaffTime", ref clockOutText, 32);
            ImGui.SameLine();
            if (ImGui.SmallButton("Now##ClockOut"))
            {
                clockOutText = FormatInputTime(venue, DateTimeOffset.UtcNow);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Opening end"))
            {
                clockOutText = FormatInputTime(venue, opening.ClosesAt);
            }
        }

        ImGui.TextDisabled(
            $"Input timezone: {VenueTimeZone.Resolve(venue).DisplayName}; format {TimeFormat}");
        if (timeError.Length > 0)
        {
            ImGui.TextWrapped(timeError);
        }

        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Record clock entry"))
        {
            RecordClockEntry(venue, opening);
        }
        ImGui.EndDisabled();
    }

    private void RecordClockEntry(
        VenueConnectionConfiguration venue,
        StaffOpeningSummary opening)
    {
        if (!VenueTimeZone.TryParseExact(
                venue,
                clockInText,
                TimeFormat,
                CultureInfo.InvariantCulture,
                out var clockIn,
                out timeError))
        {
            return;
        }

        if (!includeClockOut)
        {
            plugin.SaveStaffTimeEntry(
                venue,
                null,
                new SaveStaffTimeEntryRequest(
                    selectedStaffId,
                    opening.OpeningId,
                    clockIn,
                    null));
            timeError = string.Empty;
            return;
        }

        if (!VenueTimeZone.TryParseExact(
                venue,
                clockOutText,
                TimeFormat,
                CultureInfo.InvariantCulture,
                out var clockOut,
                out timeError))
        {
            return;
        }

        plugin.SaveStaffTimeEntry(
            venue,
            null,
            new SaveStaffTimeEntryRequest(
                selectedStaffId,
                opening.OpeningId,
                clockIn,
                clockOut));
        timeError = string.Empty;
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
                     .ThenBy(member => member.DisplayName))
        {
            ImGui.PushID($"staff-member-{member.StaffMemberId}");
            ImGui.TextUnformatted(
                $"{member.DisplayName} — {member.JobName} — " +
                $"{member.EffectiveHourlyRateGil:N0}/h + " +
                $"{member.CustomFixedAmountGil:N0} fixed — " +
                $"unpaid {member.UnpaidSalaryGil:N0}" +
                (member.ArchivedAt is null ? string.Empty : " (archived)"));
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

        EnsureSelectedStaff(staff);
        var selected = staff.First(member => member.StaffMemberId == selectedStaffId);
        if (ImGui.BeginCombo("Staff listing##CharacterLink", selected.DisplayName))
        {
            foreach (var member in staff)
            {
                var isSelected = member.StaffMemberId == selectedStaffId;
                if (ImGui.Selectable(member.DisplayName, isSelected))
                {
                    selectedStaffId = member.StaffMemberId;
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
                    selectedStaffId,
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
        bool busy)
    {
        if (!ImGui.CollapsingHeader("Clock-in history", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
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
            ImGui.TextUnformatted(
                $"{entry.StaffDisplayName}: " +
                $"{VenueTimeZone.Format(venue, entry.ClockInAt, "g")} – {clockOut} | " +
                salary +
                (entry.PaidAt is not null ? " | paid" : string.Empty));

            if (entry.Status == "open")
            {
                DrawClockOutActions(venue, view, entry, busy);
            }

            if (entry.Status != "cancelled" &&
                (entry.FinancialTransactionId is null || entry.PaidAt is not null))
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Cancel entry"))
                {
                    pendingCancelEntryId = entry.TimeEntryId;
                    cancelReason = string.Empty;
                    ImGui.OpenPopup(
                        "Cancel Staff time entry###PartyPulseStaffCancelEntry");
                }
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }
    }

    private void DrawClockOutActions(
        VenueConnectionConfiguration venue,
        StaffManagementViewResponse view,
        StaffTimeEntrySummary entry,
        bool busy)
    {
        var opening = view.Openings.First(
            candidate => candidate.OpeningId == entry.OpeningId);

        ImGui.SameLine();
        ImGui.BeginDisabled(busy);
        if (ImGui.SmallButton("Clock out now"))
        {
            plugin.SaveStaffTimeEntry(
                venue,
                entry.TimeEntryId,
                new SaveStaffTimeEntryRequest(
                    entry.StaffMemberId,
                    entry.OpeningId,
                    entry.ClockInAt,
                    DateTimeOffset.UtcNow));
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(busy);
        if (ImGui.SmallButton("Clock out at opening end"))
        {
            plugin.SaveStaffTimeEntry(
                venue,
                entry.TimeEntryId,
                new SaveStaffTimeEntryRequest(
                    entry.StaffMemberId,
                    entry.OpeningId,
                    entry.ClockInAt,
                    opening.ClosesAt));
        }
        ImGui.EndDisabled();
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
            .Where(member => member.ArchivedAt is null && member.UnpaidSalaryGil > 0)
            .OrderBy(member => member.DisplayName)
            .ToArray();
        if (staff.Length > 0)
        {
            EnsureSelectedStaff(staff);
            var selected = staff.First(member => member.StaffMemberId == selectedStaffId);
            if (ImGui.BeginCombo(
                    "Staff payout",
                    $"{selected.DisplayName} — {selected.UnpaidSalaryGil:N0} gil"))
            {
                foreach (var member in staff)
                {
                    var isSelected = member.StaffMemberId == selectedStaffId;
                    if (ImGui.Selectable(
                            $"{member.DisplayName} — {member.UnpaidSalaryGil:N0} gil",
                            isSelected))
                    {
                        selectedStaffId = member.StaffMemberId;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            if (selected.RequiresCourtSettlement)
            {
                ImGui.TextWrapped(
                    $"{selected.DisplayName} has unsettled Court items " +
                    $"(sales {selected.UnsettledCourtGil:N0} gil, " +
                    $"corrections {selected.UnsettledAdjustmentGil:+#,0;-#,0;0} gil). " +
                    "Use Court Services to create one combined Court/salary settlement instead.");
            }

            if (plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetError))
            {
                ImGui.TextUnformatted($"Verified trade target: {target!.DisplayName}");
                ImGui.BeginDisabled(busy || selected.RequiresCourtSettlement);
                if (ImGui.Button("Create payout and execute Dropbox"))
                {
                    plugin.CreateStaffPayout(
                        venue,
                        new CreateStaffPayoutRequest(
                            selectedStaffId,
                            target.CharacterName,
                            target.WorldName,
                            null));
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
            ImGui.TextDisabled("No unpaid locked Staff salaries.");
        }

        var court = plugin.Court.GetSnapshot(venue).View;
        if (court is null)
        {
            return;
        }

        foreach (var transaction in court.Transactions.Where(transaction =>
                     transaction.TransactionType == "staff_payout" &&
                     transaction.Status == "pending"))
        {
            ImGui.PushID($"staff-payout-{transaction.TransactionId}");
            ImGui.TextUnformatted(
                $"Pending payout #{transaction.TransactionId}: " +
                $"{transaction.SalaryGil:N0} gil to {transaction.StaffDisplayName}");

            if (transaction.CanExecuteTrade)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Execute with Dropbox"))
                {
                    plugin.ExecuteCourtTransactionTrade(venue, transaction);
                }
                ImGui.EndDisabled();
            }

            if (transaction.CanConfirm)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy);
                if (ImGui.SmallButton("Confirm Trade Success"))
                {
                    plugin.ConfirmCourtTransaction(venue, transaction.TransactionId);
                }
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
                    ImGui.OpenPopup(
                        "Cancel Staff payout###PartyPulseStaffCancelPayout");
                }
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }
    }

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
            "If salary was already paid, an audited correction is added to the staff member's next Court settlement, " +
            "allowing a corrected replacement entry without silently rewriting the confirmed payout.");
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

    private void EnsureSelectedStaff(StaffMemberSummary[] staff) =>
        EnsureSelectedStaff(staff, ref selectedStaffId);

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

        var opening = view.Openings.FirstOrDefault(
            candidate => candidate.OpeningId == selectedOpeningId);
        if (clockInText.Length == 0)
        {
            clockInText = FormatInputTime(venue, DateTimeOffset.UtcNow);
        }
        if (clockOutText.Length == 0)
        {
            clockOutText = opening is null
                ? clockInText
                : FormatInputTime(venue, opening.ClosesAt);
        }
    }

    private void ResetTimeSuggestions(
        VenueConnectionConfiguration venue,
        StaffOpeningSummary opening)
    {
        clockInText = FormatInputTime(venue, DateTimeOffset.UtcNow);
        clockOutText = FormatInputTime(venue, opening.ClosesAt);
        timeError = string.Empty;
    }

    private void ResetForVenue(VenueConnectionConfiguration venue)
    {
        if (activeProfileId == venue.ProfileId)
        {
            return;
        }

        activeProfileId = venue.ProfileId;
        selectedOpeningId = 0;
        selectedStaffId = 0;
        editingJobId = 0;
        jobName = string.Empty;
        jobRate = 0;
        jobArchived = false;
        ClearMember();
        clockInText = string.Empty;
        clockOutText = string.Empty;
        includeClockOut = false;
        timeError = string.Empty;
        pendingCancelEntryId = 0;
        pendingCancelTransactionId = 0;
        cancelReason = string.Empty;
    }
}
