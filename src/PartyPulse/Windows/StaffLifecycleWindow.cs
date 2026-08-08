using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class StaffLifecycleWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<long, bool> completionByAssignment = new();
    private readonly HashSet<long> dirtyAssignmentIds = [];
    private Guid profileId;
    private long staffMemberId;

    public StaffLifecycleWindow(Plugin plugin)
        : base("Staff Onboarding / Offboarding###PartyPulseStaffLifecycle")
    {
        this.plugin = plugin;
        IsOpen = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Open(Guid venueProfileId, long selectedStaffMemberId)
    {
        profileId = venueProfileId;
        staffMemberId = selectedStaffMemberId;
        completionByAssignment.Clear();
        dirtyAssignmentIds.Clear();

        var venue = plugin.Configuration.VenueConnections.FirstOrDefault(
            value => value.ProfileId == profileId);
        StaffLifecycleTaskAssignmentSummary[] assignments = venue is null
            ? []
            : plugin.Staff.GetSnapshot(venue).View?.LifecycleTaskAssignments
                .Where(value => value.StaffMemberId == staffMemberId)
                .ToArray() ?? [];
        foreach (var assignment in assignments)
        {
            completionByAssignment[assignment.AssignmentId] =
                assignment.CompletedAt is not null;
        }

        IsOpen = true;
    }

    public void Dispose()
    {
        completionByAssignment.Clear();
        dirtyAssignmentIds.Clear();
    }

    public override void Draw()
    {
        var venue = plugin.Configuration.VenueConnections.FirstOrDefault(
            value => value.ProfileId == profileId);
        if (venue is null)
        {
            ImGui.TextDisabled("The selected venue is no longer configured.");
            return;
        }

        var view = plugin.Staff.GetSnapshot(venue).View;
        var staff = view?.StaffMembers.FirstOrDefault(
            value => value.StaffMemberId == staffMemberId);
        if (view is null || staff is null)
        {
            ImGui.TextDisabled("The Staff listing is no longer available.");
            if (ImGui.Button("Refresh"))
            {
                plugin.RefreshStaff(venue);
            }
            return;
        }

        var lifecycleType = staff.ArchivedAt is null
            ? StaffLifecycleTypes.Onboarding
            : StaffLifecycleTypes.Offboarding;
        var assignments = view.LifecycleTaskAssignments
            .Where(value =>
                value.StaffMemberId == staff.StaffMemberId &&
                value.LifecycleType == lifecycleType)
            .OrderBy(value => value.CreatedAt)
            .ThenBy(value => value.TaskName)
            .ToArray();

        SynchronizeState(assignments);

        var heading = lifecycleType == StaffLifecycleTypes.Onboarding
            ? "Onboarding"
            : "Offboarding";
        ImGui.TextUnformatted($"{heading}: {staff.DisplayName}");
        ImGui.TextDisabled(
            "This checklist is a snapshot of the active templates when the Staff lifecycle changed.");
        ImGui.Separator();

        if (assignments.Length == 0)
        {
            ImGui.TextDisabled($"No {heading.ToLowerInvariant()} tasks are assigned.");
        }
        else
        {
            foreach (var assignment in assignments)
            {
                ImGui.PushID($"staff-lifecycle-{assignment.AssignmentId}");
                var completed = completionByAssignment[assignment.AssignmentId];
                if (ImGui.Checkbox(assignment.TaskName, ref completed))
                {
                    completionByAssignment[assignment.AssignmentId] = completed;
                    if (completed == (assignment.CompletedAt is not null))
                    {
                        dirtyAssignmentIds.Remove(assignment.AssignmentId);
                    }
                    else
                    {
                        dirtyAssignmentIds.Add(assignment.AssignmentId);
                    }
                }

                if (completed && assignment.CompletedAt is not null)
                {
                    ImGui.TextDisabled(
                        $"Completed {VenueTimeZone.Format(venue, assignment.CompletedAt.Value, "g")}");
                }
                ImGui.PopID();
            }

            var completedCount = completionByAssignment.Count(value => value.Value);
            ImGui.Spacing();
            ImGui.TextDisabled($"{completedCount} of {assignments.Length} tasks complete.");

            var hasChanges = assignments.Any(assignment =>
                dirtyAssignmentIds.Contains(assignment.AssignmentId));
            ImGui.BeginDisabled(plugin.Staff.IsBusy(venue.ProfileId) || !hasChanges);
            if (ImGui.Button("Save completed tasks"))
            {
                plugin.SaveStaffLifecycleProgress(
                    venue,
                    staff.StaffMemberId,
                    new SaveStaffLifecycleProgressRequest(
                        assignments
                            .Select(assignment => new StaffLifecycleTaskCompletion(
                                assignment.AssignmentId,
                                completionByAssignment[assignment.AssignmentId]))
                            .ToArray()));
            }
            ImGui.EndDisabled();
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Close", new Vector2(100 * ImGuiHelpers.GlobalScale, 0)))
        {
            IsOpen = false;
        }
    }

    private void SynchronizeState(
        IReadOnlyCollection<StaffLifecycleTaskAssignmentSummary> assignments)
    {
        var currentIds = assignments
            .Select(static value => value.AssignmentId)
            .ToHashSet();
        foreach (var staleId in completionByAssignment.Keys
                     .Where(id => !currentIds.Contains(id))
                     .ToArray())
        {
            completionByAssignment.Remove(staleId);
            dirtyAssignmentIds.Remove(staleId);
        }

        foreach (var assignment in assignments)
        {
            var serverCompleted = assignment.CompletedAt is not null;
            if (!completionByAssignment.TryAdd(
                    assignment.AssignmentId,
                    serverCompleted) &&
                dirtyAssignmentIds.Contains(assignment.AssignmentId))
            {
                if (completionByAssignment[assignment.AssignmentId] == serverCompleted)
                {
                    dirtyAssignmentIds.Remove(assignment.AssignmentId);
                }
            }
            else
            {
                completionByAssignment[assignment.AssignmentId] = serverCompleted;
            }
        }
    }
}
