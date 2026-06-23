using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.SelfService;
using PartyPulse.VenueUsers;

namespace PartyPulse.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly VipTabRenderer vipTab;
    private readonly VenueOpeningsTabRenderer venueOpeningsTab;
    private readonly DjsTabRenderer djsTab;
    private readonly TimedMacrosTabRenderer timedMacrosTab;
    private readonly FinanceTabRenderer financeTab;
    private bool requestSelectFinanceTab;
    private long? requestedFinanceSettlementId;
    private Guid addUserProfileId;
    private string addUserDisplayName = string.Empty;
    private string addUserDiscordHandle = string.Empty;
    private VenueConnectionConfiguration? pendingLinkVenue;
    private VenueConnectionConfiguration? pendingUnauthorizeVenue;
    private bool requestOpenUnauthorizePopup;
    private VenueConnectionConfiguration? pendingLocalRemovalVenue;
    private bool requestOpenLocalRemovalPopup;
    private VenueConnectionConfiguration? pendingUnlinkVenue;
    private SelfCharacterSummary? pendingUnlinkCharacter;
    private bool requestOpenUnlinkPopup;

    public MainWindow(Plugin plugin)
        : base("Party Pulse###PartyPulseMain")
    {
        this.plugin = plugin;
        vipTab = new VipTabRenderer(plugin);
        venueOpeningsTab = new VenueOpeningsTabRenderer(plugin);
        djsTab = new DjsTabRenderer(plugin);
        timedMacrosTab = new TimedMacrosTabRenderer(plugin);
        financeTab = new FinanceTabRenderer(plugin);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void OpenFinance(Guid venueProfileId, long? settlementId)
    {
        var venue = plugin.Configuration.VenueConnections.FirstOrDefault(
            value => value.ProfileId == venueProfileId);
        if (venue is null)
        {
            return;
        }

        plugin.Configuration.SelectedVenueProfileId = venue.ProfileId;
        plugin.Configuration.Save();
        requestedFinanceSettlementId = settlementId;
        requestSelectFinanceTab = true;
        IsOpen = true;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var selectedVenue = DrawHeader();
        ImGui.Separator();
        DrawFeatureTabs(selectedVenue);
        DrawConfirmationPopups();
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
            var auth = plugin.Authentication.GetSnapshot(selectedVenue);
            DrawConnectionStatus(auth);
            if (auth.Status == AuthenticationStatus.Connected)
            {
                plugin.EnsureVenueUsersLoaded(selectedVenue);
                plugin.EnsureSelfServiceLoaded(selectedVenue);
            }
            else if (auth.Status == AuthenticationStatus.CharacterNotLinked &&
                     plugin.IdentityProvider.TryGetCurrent(out var identity, out _))
            {
                ImGui.TextWrapped($"{identity!.DisplayName} is not linked to this venue user.");
                if (ImGui.Button("Link current character"))
                {
                    pendingLinkVenue = selectedVenue;
                    ImGui.OpenPopup("Link current character###PartyPulseLinkCurrentCharacter");
                }
            }
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
            AuthenticationStatus.CharacterNotLinked => new Vector4(1f, 0.8f, 0.35f, 1f),
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

        if (selectedVenue?.IsRegistered == true &&
            CanDrawAuthenticatedFeatures(plugin.Authentication.GetSnapshot(selectedVenue)))
        {
            DrawMyAccountTab(selectedVenue);

            var userSnapshot = plugin.UserManagement.GetSnapshot(selectedVenue);
            if (userSnapshot.Status == VenueUserManagementStatus.Ready &&
                userSnapshot.View?.Capabilities.CanView == true)
            {
                DrawUsersTab(selectedVenue, userSnapshot);
            }

            djsTab.Draw(selectedVenue);
            venueOpeningsTab.Draw(selectedVenue);
            timedMacrosTab.Draw(selectedVenue);
            vipTab.Draw(selectedVenue);
            if (financeTab.Draw(
                    selectedVenue,
                    requestSelectFinanceTab,
                    requestedFinanceSettlementId))
            {
                requestSelectFinanceTab = false;
                requestedFinanceSettlementId = null;
            }
        }

        DrawPlaceholderTab("Staff", "Clock-in state, staff tools, macros, timers, and Party Finder controls will live here.");
        DrawPlaceholderTab("Bar", "Bar sales, gambashots, jackpots, and buyout tracking will live here.");
        DrawPlaceholderTab("Games", "Venue-wide game state, rolls, host controls, and timers will live here.");
        DrawPlaceholderTab("Greeter", "Target-aware greeting actions and VIP-specific greeting selection will live here.");

        ImGui.EndTabBar();
    }

    private static bool CanDrawAuthenticatedFeatures(AuthenticationSnapshot snapshot)
    {
        if (snapshot.Status == AuthenticationStatus.Connected)
        {
            return true;
        }

        return snapshot.LastSuccessAt is not null &&
               (snapshot.Status == AuthenticationStatus.Connecting ||
                snapshot.Status == AuthenticationStatus.Expired ||
                snapshot.Status == AuthenticationStatus.Failed);
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
        ImGui.TextUnformatted($"Access: {(selectedVenue?.IsRegistered == true ? "Registered venue account" : "Visitor")}");
        ImGui.TextWrapped("Public venue data is available to visitors. Registered venue accounts use self-service for characters, devices, and membership.");

        ImGui.EndTabItem();
    }

    private void DrawMyAccountTab(VenueConnectionConfiguration venue)
    {
        if (!ImGui.BeginTabItem("My Account"))
        {
            return;
        }

        var snapshot = plugin.SelfService.GetSnapshot(venue);
        if (snapshot.Status is SelfServiceStatus.NotLoaded or SelfServiceStatus.Loading)
        {
            ImGui.TextDisabled(snapshot.Message);
            ImGui.EndTabItem();
            return;
        }

        if (snapshot.Status == SelfServiceStatus.Failed || snapshot.View is null)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), snapshot.Message);
            if (ImGui.Button("Retry"))
            {
                plugin.RefreshSelfService(venue);
            }

            ImGui.EndTabItem();
            return;
        }

        var view = snapshot.View;
        ImGui.TextUnformatted(view.IsOwner ? "Venue owner account" : "Venue staff account");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
        {
            plugin.RefreshSelfService(venue);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Registered characters");
        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("SelfCharacters", 3, tableFlags))
        {
            ImGui.TableSetupColumn("Character");
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var character in view.Characters)
            {
                ImGui.PushID(character.CharacterId);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(character.DisplayName);

                ImGui.TableSetColumnIndex(1);
                if (character.IsCurrent)
                {
                    ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.45f, 1f), "Current");
                }
                else if (character.IsMain)
                {
                    ImGui.TextUnformatted("Main");
                }
                else
                {
                    ImGui.TextDisabled("Linked");
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.BeginDisabled(character.IsCurrent);
                if (ImGui.SmallButton("Unlink"))
                {
                    pendingUnlinkVenue = venue;
                    pendingUnlinkCharacter = character;
                    requestOpenUnlinkPopup = true;
                }
                ImGui.EndDisabled();
                if (character.IsCurrent && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("The currently logged-in character cannot be unlinked.");
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Register another device");
        ImGui.TextWrapped("Create a short-lived code, then add this same venue on the second computer and choose 'Register with device code'.");
        if (ImGui.Button("Create device pairing code"))
        {
            plugin.CreateDevicePairingCode(venue);
        }

        if (snapshot.LatestPairingCode is { } pairing)
        {
            ImGui.TextWrapped($"Pairing code: {pairing.PairingCode}");
            ImGui.TextDisabled($"Expires: {pairing.ExpiresAt.ToLocalTime():g}");
            if (ImGui.Button("Copy pairing code"))
            {
                ImGui.SetClipboardText(pairing.PairingCode);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Venue authorization");
        if (view.IsLastOwner)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.65f, 0.3f, 1f),
                "You are the venue's last active owner and cannot unauthorize until another owner exists.");
        }

        ImGui.BeginDisabled(view.IsLastOwner);
        if (ImGui.Button("Unauthorize from venue"))
        {
            pendingUnauthorizeVenue = venue;
            requestOpenUnauthorizePopup = true;
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled("This disables your venue user and revokes every registered device for that user. The public venue remains saved locally in visitor mode.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Local venue data");
        if (ImGui.Button("Remove venue from this device"))
        {
            pendingLocalRemovalVenue = venue;
            requestOpenLocalRemovalPopup = true;
        }
        ImGui.TextDisabled("This only removes the venue and credential stored by this plugin on this computer. It does not change the server-side venue user.");

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
                    view.Capabilities.CanRestore ||
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

    private void DrawConfirmationPopups()
    {
        if (requestOpenUnauthorizePopup)
        {
            ImGui.OpenPopup("Unauthorize from venue###PartyPulseUnauthorizeVenue");
            requestOpenUnauthorizePopup = false;
        }

        if (requestOpenUnlinkPopup)
        {
            ImGui.OpenPopup("Unlink character###PartyPulseUnlinkCharacter");
            requestOpenUnlinkPopup = false;
        }

        if (requestOpenLocalRemovalPopup)
        {
            ImGui.OpenPopup("Remove venue from device###PartyPulseRemoveVenueFromDevice");
            requestOpenLocalRemovalPopup = false;
        }

        if (ImGui.BeginPopupModal(
                "Link current character###PartyPulseLinkCurrentCharacter",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (pendingLinkVenue is not null &&
                plugin.IdentityProvider.TryGetCurrent(out var identity, out var reason))
            {
                ImGui.TextWrapped("Link the currently logged-in character to this venue account?");
                ImGui.Spacing();
                ImGui.TextUnformatted($"Venue: {pendingLinkVenue.DisplayLabel}");
                ImGui.TextUnformatted($"Character: {identity!.CharacterName}");
                ImGui.TextUnformatted($"Home world: {identity.WorldName}");
                ImGui.Spacing();
                if (ImGui.Button("Link character"))
                {
                    plugin.LinkCurrentCharacter(pendingLinkVenue);
                    pendingLinkVenue = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            else
            {
                ImGui.TextDisabled("The current character is not available.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingLinkVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal(
                "Unlink character###PartyPulseUnlinkCharacter",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(
                pendingUnlinkCharacter is null
                    ? "Unlink this character?"
                    : $"Unlink {pendingUnlinkCharacter.DisplayName} from this venue account?");
            ImGui.TextDisabled("The character can be linked again later while logged into it on a registered device.");
            if (ImGui.Button("Unlink") && pendingUnlinkVenue is not null && pendingUnlinkCharacter is not null)
            {
                plugin.UnlinkCharacter(pendingUnlinkVenue, pendingUnlinkCharacter.CharacterId);
                pendingUnlinkVenue = null;
                pendingUnlinkCharacter = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingUnlinkVenue = null;
                pendingUnlinkCharacter = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal(
                "Unauthorize from venue###PartyPulseUnauthorizeVenue",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Unauthorize your user from {pendingUnauthorizeVenue?.DisplayLabel ?? "this venue"}?");
            ImGui.TextWrapped("Your venue user will be disabled and all of its devices will be revoked. The public venue remains in your local list as a visitor venue.");
            if (ImGui.Button("Unauthorize") && pendingUnauthorizeVenue is not null)
            {
                plugin.UnauthorizeFromVenue(pendingUnauthorizeVenue);
                pendingUnauthorizeVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingUnauthorizeVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal(
                "Remove venue from device###PartyPulseRemoveVenueFromDevice",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Remove {pendingLocalRemovalVenue?.DisplayLabel ?? "this venue"} from this device?");
            ImGui.TextWrapped("This removes the saved venue and local device credential only. Your server-side venue user and other devices are not changed.");
            if (ImGui.Button("Remove from this device") && pendingLocalRemovalVenue is not null)
            {
                plugin.RemoveVenueLocally(pendingLocalRemovalVenue);
                pendingLocalRemovalVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingLocalRemovalVenue = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
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
