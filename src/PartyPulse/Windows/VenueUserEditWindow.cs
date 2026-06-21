using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Api;

namespace PartyPulse.Windows;

public sealed class VenueUserEditWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Guid profileId;
    private int userId;
    private string displayName = string.Empty;
    private string discordHandle = string.Empty;
    private readonly HashSet<string> selectedPermissions = new(StringComparer.Ordinal);

    public VenueUserEditWindow(Plugin plugin)
        : base("Edit Venue User###PartyPulseVenueUserEdit")
    {
        this.plugin = plugin;
        IsOpen = false;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Open(Guid venueProfileId, int venueUserId)
    {
        profileId = venueProfileId;
        userId = venueUserId;
        displayName = string.Empty;
        discordHandle = string.Empty;
        selectedPermissions.Clear();

        var venue = plugin.Configuration.VenueConnections.FirstOrDefault(value => value.ProfileId == profileId);
        var user = venue is null
            ? null
            : plugin.UserManagement.GetSnapshot(venue).View?.Users.FirstOrDefault(value => value.UserId == userId);

        if (user is not null)
        {
            displayName = user.DisplayName;
            discordHandle = user.DiscordHandle ?? string.Empty;
            foreach (var permission in user.Permissions.Where(static value => value != "venue.owner"))
            {
                selectedPermissions.Add(permission);
            }
        }

        IsOpen = true;
    }

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
        if (view is null || user is null)
        {
            ImGui.TextDisabled("The venue user is no longer available.");
            if (ImGui.Button("Refresh"))
            {
                plugin.RefreshVenueUsers(venue);
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
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Display name", ref displayName, 50);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Discord handle", ref discordHandle, 50);

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

        ImGui.BeginDisabled(
            isBusy ||
            !view.Capabilities.CanManagePermissions ||
            user.DisabledAt is not null);
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
        else if (user.DisabledAt is not null)
        {
            ImGui.TextDisabled("Restore the user before assigning permissions.");
        }

        ImGui.Spacing();

        if (user.DisabledAt is not null)
        {
            ImGui.TextUnformatted("Account restoration");
            ImGui.Separator();
            ImGui.TextWrapped(
                "Restoring this venue user keeps the existing account identity and history, but does not restore old devices or permissions. A fresh one-time invite code will be created.");

            ImGui.BeginDisabled(isBusy || !view.Capabilities.CanRestore);
            if (ImGui.Button("Restore user and create invite"))
            {
                plugin.RestoreVenueUser(venue, user);
            }
            ImGui.EndDisabled();

            if (!view.Capabilities.CanRestore)
            {
                ImGui.TextDisabled("You do not have permission to restore disabled venue users.");
            }
        }
        else
        {
            ImGui.TextUnformatted("Account recovery");
            ImGui.Separator();
            ImGui.TextWrapped("Creating a new recovery code invalidates older unused recovery codes. Devices are revoked only when the user redeems the new code.");

            ImGui.BeginDisabled(isBusy || !view.Capabilities.CanRecover);
            if (ImGui.Button("Create recovery code"))
            {
                plugin.CreateVenueUserRecoveryCode(venue, user);
            }
            ImGui.EndDisabled();

            var recoveryCode = plugin.UserManagement.GetLastRecoveryCode(profileId, user.UserId);
            if (recoveryCode is not null)
            {
                ImGui.Spacing();
                ImGui.TextWrapped($"Recovery code: {recoveryCode.Code}");
                ImGui.TextDisabled($"Expires: {recoveryCode.ExpiresAt.ToLocalTime():g}");
                if (ImGui.Button("Copy recovery code"))
                {
                    ImGui.SetClipboardText(recoveryCode.Code);
                }
            }
        }

        var latestInviteCode = plugin.UserManagement.GetLastInviteCode(profileId, user.UserId);
        if (latestInviteCode is not null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Invite code: {latestInviteCode.Code}");
            ImGui.TextDisabled($"Expires: {latestInviteCode.ExpiresAt.ToLocalTime():g}");
            if (ImGui.Button("Copy invite code"))
            {
                ImGui.SetClipboardText(latestInviteCode.Code);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Close", new Vector2(100 * ImGuiHelpers.GlobalScale, 0)))
        {
            IsOpen = false;
        }
    }
}
