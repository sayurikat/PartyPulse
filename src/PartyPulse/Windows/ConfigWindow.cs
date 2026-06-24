using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly Dictionary<Guid, string> inviteCodes = [];
    private readonly Dictionary<Guid, string> recoveryCodes = [];
    private readonly Dictionary<Guid, string> pairingCodes = [];
    private string venueCodeInput = string.Empty;
    private VenueConnectionConfiguration? pendingUnauthorize;
    private bool requestOpenUnauthorizePopup;
    private VenueConnectionConfiguration? pendingLocalRemoval;
    private bool requestOpenLocalRemovalPopup;
    private bool dirty;

    public ConfigWindow(Plugin plugin)
        : base("Party Pulse Settings###PartyPulseConfig")
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
        inviteCodes.Clear();
        recoveryCodes.Clear();
        pairingCodes.Clear();
    }

    public override void PreDraw()
    {
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        DrawApiSettings();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawPlayerStatus();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawAddVenue();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawVenueConnections();
        ImGui.Spacing();
        DrawFooter();
        DrawUnauthorizeVenueConfirmation();
        DrawRemoveVenueConfirmation();
    }

    private void DrawApiSettings()
    {
        ImGui.TextUnformatted("API");

        var apiBaseUrl = configuration.ApiBaseUrl;
        ImGui.SetNextItemWidth(-135 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##ApiBaseUrl", ref apiBaseUrl, 512))
        {
            configuration.ApiBaseUrl = apiBaseUrl;
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(string.Equals(
            configuration.ApiBaseUrl,
            Configuration.DefaultApiBaseUrl,
            StringComparison.Ordinal));
        if (ImGui.Button("Reset to default"))
        {
            configuration.ApiBaseUrl = Configuration.DefaultApiBaseUrl;
            dirty = true;
        }
        ImGui.EndDisabled();

        if (!PartyPulseApiClient.TryCreateBaseUri(configuration.ApiBaseUrl, out _, out var error))
        {
            DrawStatusText(error, AuthenticationStatus.Failed);
        }

        var autoConnect = configuration.AutoConnect;
        if (ImGui.Checkbox("Authenticate registered staff venues automatically after login", ref autoConnect))
        {
            configuration.AutoConnect = autoConnect;
            dirty = true;
        }

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable settings window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            dirty = true;
        }
    }

    private void DrawPlayerStatus()
    {
        ImGui.TextUnformatted("Current character");
        if (plugin.IdentityProvider.TryGetCurrent(out var identity, out var reason))
        {
            DrawStatusText(identity!.DisplayName, AuthenticationStatus.Connected);
        }
        else
        {
            DrawStatusText(reason, AuthenticationStatus.WaitingForPlayer);
        }
    }

    private void DrawAddVenue()
    {
        ImGui.TextUnformatted("Add public venue");
        ImGui.TextWrapped("Visitors only need the public venue code or the venue's in-game housing location. Staff can redeem an invite on the same saved venue afterwards.");

        ImGui.TextUnformatted("Venue code");
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##VenueCode", ref venueCodeInput, 32);
        ImGui.SameLine();
        if (ImGui.Button("Add by code"))
        {
            plugin.AddVenueByCode(venueCodeInput);
        }

        if (plugin.LocationProvider.TryGetCurrentHousingAddress(out var address, out var reason))
        {
            if (ImGui.Button($"Add venue for {address!.DisplayText}"))
            {
                plugin.AddVenueAtCurrentLocation();
            }
        }
        else
        {
            ImGui.TextDisabled(reason);
        }

        var lookup = plugin.VenueDirectory.GetSnapshot();
        var lookupStatus = lookup.Status switch
        {
            VenueDirectoryStatus.LookingUp => AuthenticationStatus.Connecting,
            VenueDirectoryStatus.Added => AuthenticationStatus.Connected,
            VenueDirectoryStatus.Failed => AuthenticationStatus.Failed,
            _ => AuthenticationStatus.Disconnected,
        };
        DrawStatusText(lookup.Message, lookupStatus);
    }

    private void DrawVenueConnections()
    {
        ImGui.TextUnformatted("Saved venues");

        if (configuration.VenueConnections.Count == 0)
        {
            ImGui.TextDisabled("No venues have been added yet.");
            return;
        }

        for (var index = 0; index < configuration.VenueConnections.Count; index++)
        {
            var venue = configuration.VenueConnections[index];
            ImGui.PushID(venue.ProfileId.ToString("N"));

            if (ImGui.CollapsingHeader(venue.DisplayLabel, ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawPublicVenueDetails(venue);
                ImGui.Spacing();
                DrawLocalVenueFields(venue);
                ImGui.Spacing();

                if (venue.IsRegistered)
                {
                    DrawRegisteredDevice(venue);
                    ImGui.Spacing();
                    DrawAuthorizedDeviceRegistration(venue);
                }
                else
                {
                    DrawInviteRegistration(venue);
                    ImGui.Spacing();
                    DrawPairingRegistration(venue);
                    ImGui.Spacing();
                    DrawRecovery(venue);
                }

                ImGui.Spacing();

                if (venue.IsRegistered)
                {
                    var snapshot = plugin.Authentication.GetSnapshot(venue);
                    DrawStatusText(snapshot.Message, snapshot.Status);
                    if (snapshot.AccessTokenExpiresAt is { } expiresAt)
                    {
                        ImGui.TextDisabled($"Access token expires: {VenueTimeZone.Format(venue, expiresAt, "g")}");
                    }

                    if (ImGui.Button("Save and authenticate"))
                    {
                        SaveAndSelect(venue);
                        plugin.ConnectVenue(venue);
                    }
                    ImGui.SameLine();
                }
                else
                {
                    DrawStatusText("Visitor mode — public venue information only.", AuthenticationStatus.Disconnected);
                }

                if (venue.IsRegistered)
                {
                    var selfService = plugin.SelfService.GetSnapshot(venue);
                    var isLastOwner = selfService.View?.IsLastOwner == true;

                    ImGui.BeginDisabled(isLastOwner);
                    if (ImGui.Button("Unauthorize from venue"))
                    {
                        pendingUnauthorize = venue;
                        requestOpenUnauthorizePopup = true;
                    }
                    ImGui.EndDisabled();

                    if (isLastOwner)
                    {
                        ImGui.TextColored(
                            new Vector4(1f, 0.65f, 0.3f, 1f),
                            "You are the venue's last active owner and cannot unauthorize until another owner exists.");
                    }
                    else
                    {
                        ImGui.TextDisabled(
                            "Disables your venue user and revokes all of its registered devices. The venue stays saved here in visitor mode.");
                    }
                }

                if (ImGui.Button("Remove venue from this device"))
                {
                    pendingLocalRemoval = venue;
                    requestOpenLocalRemovalPopup = true;
                }
            }

            ImGui.PopID();
            ImGui.Spacing();
        }

    }

    private void DrawUnauthorizeVenueConfirmation()
    {
        // Like the local-removal popup, this is opened outside the per-venue
        // ImGui ID scope so OpenPopup and BeginPopupModal use the same ID.
        if (requestOpenUnauthorizePopup)
        {
            ImGui.OpenPopup("Unauthorize from venue###PartyPulseConfigUnauthorizeVenue");
            requestOpenUnauthorizePopup = false;
        }

        if (!ImGui.BeginPopupModal(
                "Unauthorize from venue###PartyPulseConfigUnauthorizeVenue",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"Unauthorize your venue user from {pendingUnauthorize?.DisplayLabel ?? "this venue"}?");
        ImGui.TextWrapped(
            "This disables the server-side venue user, removes its permissions, revokes all registered devices, and unlinks its characters.");
        ImGui.TextDisabled(
            "The public venue remains saved on this computer in visitor mode.");

        if (ImGui.Button("Unauthorize") && pendingUnauthorize is not null)
        {
            var venue = pendingUnauthorize;
            pendingUnauthorize = null;
            plugin.UnauthorizeFromVenue(venue);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            pendingUnauthorize = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawRemoveVenueConfirmation()
    {
        // The remove button is drawn under a per-venue ImGui ID. Opening the
        // popup here, outside that ID scope, keeps OpenPopup and BeginPopupModal
        // on the same ID and makes the confirmation reliably appear.
        if (requestOpenLocalRemovalPopup)
        {
            ImGui.OpenPopup("Remove saved venue###PartyPulseRemoveSavedVenue");
            requestOpenLocalRemovalPopup = false;
        }

        if (!ImGui.BeginPopupModal(
                "Remove saved venue###PartyPulseRemoveSavedVenue",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped($"Remove {pendingLocalRemoval?.DisplayLabel ?? "this venue"} from this device?");
        if (pendingLocalRemoval?.IsRegistered == true)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.65f, 0.3f, 1f),
                "This deletes the locally stored venue and device credential. It does not unauthorize your venue account or revoke the server-side device.");
        }
        else
        {
            ImGui.TextDisabled("This only removes the saved public venue from this device.");
        }

        if (ImGui.Button("Remove from this device") && pendingLocalRemoval is not null)
        {
            var removed = pendingLocalRemoval;
            inviteCodes.Remove(removed.ProfileId);
            recoveryCodes.Remove(removed.ProfileId);
            pairingCodes.Remove(removed.ProfileId);
            plugin.RemoveVenueLocally(removed);
            pendingLocalRemoval = null;
            dirty = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            pendingLocalRemoval = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void DrawPublicVenueDetails(VenueConnectionConfiguration venue)
    {
        ImGui.TextUnformatted(venue.VenueName.Length > 0 ? venue.VenueName : "Unknown venue");
        ImGui.TextDisabled(venue.VenueCode.Length > 0 ? venue.VenueCode : "Public code not loaded");
        ImGui.TextWrapped(venue.AddressDisplay);
    }

    private void DrawLocalVenueFields(VenueConnectionConfiguration venue)
    {
        var displayName = venue.DisplayName;
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Local alias", ref displayName, 80))
        {
            venue.DisplayName = displayName;
            dirty = true;
        }

        var timeZone = VenueTimeZone.Resolve(venue);
        ImGui.SetNextItemWidth(460 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Display/input timezone", timeZone.DisplayName))
        {
            foreach (var option in VenueTimeZone.Available)
            {
                var selected = string.Equals(option.Id, timeZone.Id, StringComparison.Ordinal);
                if (ImGui.Selectable(option.DisplayName, selected))
                {
                    venue.DisplayTimeZoneId = option.Id;
                    timeZone = option;
                    dirty = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled($"Venue time now: {VenueTimeZone.Convert(timeZone, DateTimeOffset.UtcNow):ddd yyyy-MM-dd HH:mm zzz} ({timeZone.Id})");

        var deviceName = venue.DeviceName;
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Device name", ref deviceName, 50))
        {
            venue.DeviceName = deviceName;
            dirty = true;
        }
    }

    private static void DrawRegisteredDevice(VenueConnectionConfiguration venue)
    {
        ImGui.TextUnformatted($"Authorized device ID: {venue.DeviceId}");
        if (venue.RefreshTokenUpdatedAt is { } updatedAt)
        {
            ImGui.TextDisabled($"Refresh token last updated: {VenueTimeZone.Format(venue, updatedAt, "g")}");
        }
    }

    private void DrawAuthorizedDeviceRegistration(VenueConnectionConfiguration venue)
    {
        ImGui.TextUnformatted("Authorize another device");
        ImGui.TextWrapped("Create a short-lived code here, then enter it on the second computer after adding this venue there.");

        if (ImGui.Button("Create new device code"))
        {
            SaveAndSelect(venue);
            plugin.CreateDevicePairingCode(venue);
        }

        var snapshot = plugin.SelfService.GetSnapshot(venue);
        if (snapshot.LatestPairingCode is not { } pairing)
        {
            return;
        }

        ImGui.TextWrapped($"Device code: {pairing.PairingCode}");
        ImGui.TextDisabled($"Expires: {VenueTimeZone.Format(venue, pairing.ExpiresAt, "g")}");
        if (ImGui.Button("Copy device code"))
        {
            ImGui.SetClipboardText(pairing.PairingCode);
        }
    }

    private void DrawInviteRegistration(VenueConnectionConfiguration venue)
    {
        ImGui.TextUnformatted("Staff registration (optional)");
        ImGui.TextWrapped("Enter a one-time invite code to upgrade this saved visitor venue into an authenticated staff venue on this device.");

        var code = inviteCodes.GetValueOrDefault(venue.ProfileId, string.Empty);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Invite code", ref code, 80, ImGuiInputTextFlags.Password))
        {
            inviteCodes[venue.ProfileId] = code;
        }

        if (ImGui.Button("Register staff device with invite"))
        {
            SaveAndSelect(venue);
            plugin.RedeemInvite(venue, code);
        }
    }

    private void DrawPairingRegistration(VenueConnectionConfiguration venue)
    {
        ImGui.TextUnformatted("Authorize this device with a device code");
        ImGui.TextWrapped("Enter a code created on another already-authorized device. This field redeems a code; it does not create one.");

        var code = pairingCodes.GetValueOrDefault(venue.ProfileId, string.Empty);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Device pairing code", ref code, 80, ImGuiInputTextFlags.Password))
        {
            pairingCodes[venue.ProfileId] = code;
        }

        if (ImGui.Button("Authorize this device"))
        {
            SaveAndSelect(venue);
            plugin.RedeemDevicePairingCode(venue, code);
        }
    }

    private void DrawRecovery(VenueConnectionConfiguration venue)
    {
        if (!ImGui.TreeNode("Account recovery"))
        {
            return;
        }

        ImGui.TextWrapped("Recovery revokes every other device for this venue user and registers this machine as the replacement device.");
        var code = recoveryCodes.GetValueOrDefault(venue.ProfileId, string.Empty);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Recovery code", ref code, 80, ImGuiInputTextFlags.Password))
        {
            recoveryCodes[venue.ProfileId] = code;
        }

        if (ImGui.Button("Recover and replace devices"))
        {
            SaveAndSelect(venue);
            plugin.RecoverVenue(venue, code);
        }

        ImGui.TreePop();
    }

    private void DrawFooter()
    {
        if (ImGui.Button(dirty ? "Save changes *" : "Save changes"))
        {
            SaveChanges();
        }

        ImGui.SameLine();
        if (ImGui.Button("Authenticate all registered venues"))
        {
            SaveChanges();
            plugin.ConnectAllConfiguredVenues();
        }
    }

    private void SaveAndSelect(VenueConnectionConfiguration venue)
    {
        SaveChanges();
        configuration.SelectedVenueProfileId = venue.ProfileId;
        configuration.Save();
    }

    private void SaveChanges()
    {
        configuration.Normalize();
        configuration.Save();
        dirty = false;
    }

    private static void DrawStatusText(string message, AuthenticationStatus status)
    {
        var color = status switch
        {
            AuthenticationStatus.Connected => new Vector4(0.35f, 0.85f, 0.45f, 1f),
            AuthenticationStatus.Connecting => new Vector4(0.35f, 0.7f, 1f, 1f),
            AuthenticationStatus.WaitingForPlayer => new Vector4(1f, 0.8f, 0.35f, 1f),
            AuthenticationStatus.CharacterNotLinked => new Vector4(1f, 0.8f, 0.35f, 1f),
            AuthenticationStatus.Failed => new Vector4(1f, 0.4f, 0.4f, 1f),
            AuthenticationStatus.Expired => new Vector4(1f, 0.65f, 0.3f, 1f),
            _ => new Vector4(0.65f, 0.65f, 0.65f, 1f),
        };

        ImGui.TextColored(color, message);
    }
}
