using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Api;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class VenueUserEditWindow : Window, IDisposable
{
    private const string ArchivePopupName = "Archive venue user###PartyPulseArchiveVenueUser";
    private readonly Plugin plugin;
    private Guid profileId;
    private int userId;
    private string displayName = string.Empty;
    private string discordHandle = string.Empty;
    private readonly HashSet<string> selectedPermissions = new(StringComparer.Ordinal);
    private bool archiveRequested;

    public VenueUserEditWindow(Plugin plugin)
        : base("Edit Venue User###PartyPulseVenueUserEdit")
    {
        this.plugin = plugin;
        IsOpen = false;
        CollapsedCondition = ImGuiCond.Always;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Open(Guid venueProfileId, VenueUserSummary user)
    {
        if (!IsOpen || profileId != venueProfileId || userId != user.UserId)
        {
            profileId = venueProfileId;
            userId = user.UserId;
            displayName = user.DisplayName;
            discordHandle = user.DiscordHandle ?? string.Empty;
            archiveRequested = false;
            selectedPermissions.Clear();
            foreach (var permission in user.Permissions.Where(static value => value != "venue.owner"))
            {
                selectedPermissions.Add(permission);
            }
        }

        IsOpen = true;
        Collapsed = false;
        BringToFront();
    }

    public override void PostDraw() => Collapsed = null;

    public void Dispose()
    {
        selectedPermissions.Clear();
    }

    public override void Draw()
    {
        var venue = plugin.Configuration.VenueConnections.FirstOrDefault(value => value.ProfileId == profileId);
        if (venue is null)
        {
            ImGui.TextDisabled("The selected venue is no longer configured.");
            return;
        }

        var snapshot = plugin.UserManagement.GetSnapshot(venue);
        var isBusy = plugin.UserManagement.IsBusy(venue.ProfileId);
        var view = snapshot.View;
        var user = view?.Users.FirstOrDefault(value => value.UserId == userId);
        if (archiveRequested && !isBusy)
        {
            archiveRequested = false;
            if (user is null || user.DisabledAt is not null)
            {
                IsOpen = false;
                return;
            }
        }

        if (view is null || user is null)
        {
            ImGui.TextDisabled("The venue user is no longer available.");
            if (ImGui.Button("Refresh"))
            {
                plugin.RefreshVenueUsers(venue);
            }
            return;
        }
        if (user.DisabledAt is not null)
        {
            ImGui.TextWrapped("This user is archived. Restore them from Users > Archived.");
            if (ImGui.Button("Close"))
            {
                IsOpen = false;
            }
            return;
        }

        ImGui.TextUnformatted(user.DisplayName);
        ImGui.TextDisabled(user.CharacterDisplay);
        if (user.IsOwner)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.78f, 0.25f, 1f), "Venue owner");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Profile");
        ImGui.Separator();

        ImGui.BeginDisabled(isBusy || !view.Capabilities.CanEdit);
        ImGui.TextUnformatted("Display name");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##VenueUserDisplayName", ref displayName, 50);
        ImGui.TextUnformatted("Discord handle");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##VenueUserDiscordHandle", ref discordHandle, 50);

        if (!string.IsNullOrWhiteSpace(user.DiscordName))
        {
            ImGui.TextDisabled($"Discord display name: {user.DiscordName}");
        }

        if (ImGui.Button("Save profile"))
        {
            plugin.UpdateVenueUserProfile(
                venue,
                user.UserId,
                displayName,
                discordHandle);
        }
        ImGui.EndDisabled();

        if (!view.Capabilities.CanEdit)
        {
            ImGui.TextDisabled("You do not have permission to edit profile fields.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Permissions");
        ImGui.Separator();

        if (user.IsOwner)
        {
            ImGui.TextWrapped("This user has venue.owner. That permission is controlled outside the normal permission checklist and satisfies every venue permission check.");
        }

        ImGui.BeginDisabled(isBusy || !view.Capabilities.CanManagePermissions);
        foreach (var permission in view.AvailablePermissions)
        {
            var selected = selectedPermissions.Contains(permission.PermissionKey);
            if (ImGui.Checkbox(permission.PermissionKey, ref selected))
            {
                if (selected)
                {
                    selectedPermissions.Add(permission.PermissionKey);
                }
                else
                {
                    selectedPermissions.Remove(permission.PermissionKey);
                }
            }

            if (!string.IsNullOrWhiteSpace(permission.Description) && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(permission.Description);
            }
        }

        if (ImGui.Button("Save permissions"))
        {
            plugin.UpdateVenueUserPermissions(
                venue,
                user.UserId,
                selectedPermissions.OrderBy(static value => value, StringComparer.Ordinal).ToArray());
        }
        ImGui.EndDisabled();

        if (!view.Capabilities.CanManagePermissions)
        {
            ImGui.TextDisabled("You do not have permission to change permission assignments.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Account recovery");
        ImGui.Separator();
        ImGui.TextWrapped("Creating a new recovery code invalidates older unused recovery codes. Devices are revoked only when the user redeems the new code.");

        ImGui.BeginDisabled(isBusy || !view.Capabilities.CanRecover);
        if (ImGui.Button("Create recovery code"))
        {
            plugin.CreateVenueUserRecoveryCode(venue, user);
        }
        ImGui.EndDisabled();

        if (view.Capabilities.CanRecover &&
            plugin.UserManagement.GetLastRecoveryCode(profileId, user.UserId) is { } recoveryCode)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Recovery code: {recoveryCode.Code}");
            ImGui.TextDisabled($"Expires: {VenueTimeZone.Format(venue, recoveryCode.ExpiresAt, "g")}");
            if (ImGui.Button("Copy recovery code"))
            {
                ImGui.SetClipboardText(recoveryCode.Code);
            }
        }

        var latestInviteCode = plugin.UserManagement.GetLastInviteCode(profileId, user.UserId);
        if ((view.Capabilities.CanCreate || view.Capabilities.CanRestore) && latestInviteCode is not null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Invite code: {latestInviteCode.Code}");
            ImGui.TextDisabled($"Expires: {VenueTimeZone.Format(venue, latestInviteCode.ExpiresAt, "g")}");
            if (ImGui.Button("Copy invite code"))
            {
                ImGui.SetClipboardText(latestInviteCode.Code);
            }
        }

        var canArchive = view.Capabilities.CanArchive && !user.IsOwner;
        if (canArchive)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Archive account");
            ImGui.Separator();
            ImGui.BeginDisabled(isBusy);
            if (ImGui.Button("Archive user…"))
            {
                ImGui.OpenPopup(ArchivePopupName);
            }
            ImGui.EndDisabled();
        }

        if (ImGui.BeginPopupModal(ArchivePopupName, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Archive {user.DisplayName}?");
            ImGui.TextWrapped("This revokes their devices and staff access. Their permissions, account, and history are kept.");
            ImGui.BeginDisabled(isBusy || !canArchive);
            if (ImGui.Button("Archive user"))
            {
                archiveRequested = true;
                plugin.ArchiveVenueUser(venue, user);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Close", new Vector2(100 * ImGuiHelpers.GlobalScale, 0)))
        {
            IsOpen = false;
        }
    }
}
