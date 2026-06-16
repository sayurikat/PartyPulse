using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.VenueUsers;

namespace PartyPulse.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Guid addUserProfileId;
    private string addUserDisplayName = string.Empty;
    private string addUserDiscordHandle = string.Empty;

    public MainWindow(Plugin plugin)
        : base("Party Pulse###PartyPulseMain")
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var selectedVenue = DrawHeader();
        ImGui.Separator();
        DrawFeatureTabs(selectedVenue);
    }

    private VenueConnectionConfiguration? DrawHeader()
    {
        var selectedVenue = DrawVenueSelector();

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
        {
            plugin.ToggleConfigUi();
        }

        if (selectedVenue is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("No venue is saved. Open Settings or use /pulse addvenue PULSE-XXXXXX.");
            return null;
        }

        if (selectedVenue.IsRegistered)
        {
            ImGui.SameLine();
            if (ImGui.Button("Authenticate"))
            {
                plugin.ConnectVenue(selectedVenue);
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(selectedVenue.VenueName.Length > 0 ? selectedVenue.VenueName : selectedVenue.DisplayLabel);
        ImGui.TextWrapped(selectedVenue.AddressDisplay);
        ImGui.TextDisabled(selectedVenue.VenueCode);

        if (selectedVenue.IsRegistered)
        {
            DrawConnectionStatus(plugin.Authentication.GetSnapshot(selectedVenue));
            plugin.EnsureVenueUsersLoaded(selectedVenue);
        }
        else
        {
            ImGui.TextDisabled("Visitor mode — public venue information only.");
        }

        return selectedVenue;
    }

    private VenueConnectionConfiguration? DrawVenueSelector()
    {
        var configuration = plugin.Configuration;
        var selected = configuration.GetSelectedVenue();
        var preview = selected?.DisplayLabel ?? "Select venue";

        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##VenueSelector", preview))
        {
            foreach (var venue in configuration.VenueConnections)
            {
                var isSelected = selected?.ProfileId == venue.ProfileId;
                if (ImGui.Selectable(venue.DisplayLabel, isSelected))
                {
                    configuration.SelectedVenueProfileId = venue.ProfileId;
                    configuration.Save();
                    selected = venue;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        return selected;
    }

    private static void DrawConnectionStatus(AuthenticationSnapshot snapshot)
    {
        ImGui.Spacing();

        var color = snapshot.Status switch
        {
            AuthenticationStatus.Connected => new Vector4(0.35f, 0.85f, 0.45f, 1f),
            AuthenticationStatus.Connecting => new Vector4(0.35f, 0.7f, 1f, 1f),
            AuthenticationStatus.WaitingForPlayer => new Vector4(1f, 0.8f, 0.35f, 1f),
            AuthenticationStatus.Failed => new Vector4(1f, 0.4f, 0.4f, 1f),
            AuthenticationStatus.Expired => new Vector4(1f, 0.65f, 0.3f, 1f),
            _ => new Vector4(0.65f, 0.65f, 0.65f, 1f),
        };

        ImGui.TextColored(color, snapshot.Message);

        if (snapshot.AccessTokenExpiresAt is { } expiresAt)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Token expires {expiresAt.ToLocalTime():t}");
        }
    }

    private void DrawFeatureTabs(VenueConnectionConfiguration? selectedVenue)
    {
        if (!ImGui.BeginTabBar("PartyPulseFeatureTabs"))
        {
            return;
        }

        DrawOverviewTab(selectedVenue);

        if (selectedVenue?.IsRegistered == true)
        {
            var userSnapshot = plugin.UserManagement.GetSnapshot(selectedVenue);
            if (userSnapshot.Status == VenueUserManagementStatus.Ready &&
                userSnapshot.View?.Capabilities.CanView == true)
            {
                DrawUsersTab(selectedVenue, userSnapshot);
            }
        }

        DrawPlaceholderTab("VIP", "VIP purchases, Discord identity, role automation, and payout totals will live here.");
        DrawPlaceholderTab("Staff", "Clock-in state, staff tools, macros, timers, and Party Finder controls will live here.");
        DrawPlaceholderTab("Payout", "Manager payout calculations, adjustments, finalization, and payment actions will live here.");
        DrawPlaceholderTab("Bar", "Bar sales, gambashots, jackpots, and buyout tracking will live here.");
        DrawPlaceholderTab("Games", "Venue-wide game state, rolls, host controls, and timers will live here.");
        DrawPlaceholderTab("Greeter", "Target-aware greeting actions and VIP-specific greeting selection will live here.");

        ImGui.EndTabBar();
    }

    private void DrawOverviewTab(VenueConnectionConfiguration? selectedVenue)
    {
        if (!ImGui.BeginTabItem("Overview"))
        {
            return;
        }

        if (plugin.IdentityProvider.TryGetCurrent(out var identity, out var reason))
        {
            ImGui.TextUnformatted($"Character: {identity!.DisplayName}");
        }
        else
        {
            ImGui.TextDisabled(reason);
        }

        ImGui.TextUnformatted($"Venue: {selectedVenue?.VenueName ?? "Not configured"}");
        ImGui.TextUnformatted($"Address: {selectedVenue?.AddressDisplay ?? "Not configured"}");
        ImGui.TextUnformatted($"Access: {(selectedVenue?.IsRegistered == true ? "Authenticated staff" : "Visitor")}");
        ImGui.TextWrapped("Public venue data is available to visitors. Staff features use the same saved venue after an invite or recovery code registers this device.");

        ImGui.EndTabItem();
    }

    private void DrawUsersTab(
        VenueConnectionConfiguration venue,
        VenueUserManagementSnapshot snapshot)
    {
        if (!ImGui.BeginTabItem("User List"))
        {
            return;
        }

        var view = snapshot.View!;
        var isBusy = plugin.UserManagement.IsBusy(venue.ProfileId);
        if (addUserProfileId != venue.ProfileId)
        {
            addUserProfileId = venue.ProfileId;
            addUserDisplayName = string.Empty;
            addUserDiscordHandle = string.Empty;
        }

        ImGui.BeginDisabled(isBusy);
        if (ImGui.Button("Refresh users"))
        {
            plugin.RefreshVenueUsers(venue);
        }
        ImGui.EndDisabled();

        if (view.Capabilities.CanCreate)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Add venue user");
            ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
            ImGui.InputText("Display name", ref addUserDisplayName, 50);
            ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
            ImGui.InputText("Discord handle (optional)", ref addUserDiscordHandle, 50);

            ImGui.BeginDisabled(isBusy || string.IsNullOrWhiteSpace(addUserDisplayName));
            if (ImGui.Button("Create user"))
            {
                plugin.CreateVenueUser(venue, addUserDisplayName, addUserDiscordHandle);
            }
            ImGui.EndDisabled();

            if (plugin.TargetProvider.TryGetCurrentTarget(out var target, out var targetReason))
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(isBusy);
                if (ImGui.Button($"Add target: {target!.DisplayName}"))
                {
                    addUserDisplayName = target.CharacterName;
                    plugin.CreateVenueUser(venue, target.CharacterName, addUserDiscordHandle);
                }
                ImGui.EndDisabled();
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextDisabled(targetReason);
            }
        }

        if (snapshot.LastInviteCode is { } inviteCode)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Invite code for {inviteCode.DisplayName}: {inviteCode.Code}");
            ImGui.TextDisabled($"Expires: {inviteCode.ExpiresAt.ToLocalTime():g}");
            if (ImGui.Button("Copy latest invite code"))
            {
                ImGui.SetClipboardText(inviteCode.Code);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var tableFlags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("VenueUsers", 5, tableFlags, new Vector2(0, 300 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("Display name");
            ImGui.TableSetupColumn("Discord");
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Access");
            ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var user in view.Users)
            {
                ImGui.PushID(user.UserId);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(user.DisplayName);
                if (user.DisabledAt is not null)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(disabled)");
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(user.DiscordDisplay);
                if (!string.IsNullOrWhiteSpace(user.DiscordName) && !string.IsNullOrWhiteSpace(user.DiscordHandle))
                {
                    ImGui.TextDisabled($"@{user.DiscordHandle}");
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(user.CharacterDisplay);

                ImGui.TableSetColumnIndex(3);
                if (user.IsOwner)
                {
                    ImGui.TextColored(new Vector4(1f, 0.78f, 0.25f, 1f), "Owner");
                }
                else
                {
                    ImGui.TextUnformatted($"{user.Permissions.Count} permission(s)");
                }

                ImGui.TableSetColumnIndex(4);
                var canOpenEditor =
                    view.Capabilities.CanEdit ||
                    view.Capabilities.CanRecover ||
                    view.Capabilities.CanManagePermissions;
                ImGui.BeginDisabled(!canOpenEditor);
                if (ImGui.SmallButton("Edit"))
                {
                    plugin.OpenVenueUserEditor(venue, user);
                }
                ImGui.EndDisabled();

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.EndTabItem();
    }

    private static void DrawPlaceholderTab(string title, string description)
    {
        if (!ImGui.BeginTabItem(title))
        {
            return;
        }

        ImGui.TextWrapped(description);
        ImGui.Spacing();
        ImGui.TextDisabled("Foundation placeholder — no business operation is sent yet.");
        ImGui.EndTabItem();
    }
}
