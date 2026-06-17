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
    private VenueConnectionConfiguration? pendingLocalRemoval;
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
        DrawRemoveVenueConfirmation();
    }

    private void DrawApiSettings()
    {
        ImGui.TextUnformatted("API");

        var apiBaseUrl = configuration.ApiBaseUrl;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##ApiBaseUrl", ref apiBaseUrl, 512))
        {
            configuration.ApiBaseUrl = apiBaseUrl;
            dirty = true;
        }

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
                }
                else
                {
                    DrawInviteRegistration(venue);
                    ImGui.Spacing();
                    DrawPairingRegistration(venue);
                }

                ImGui.Spacing();
                DrawRecovery(venue);
                ImGui.Spacing();

                if (venue.IsRegistered)
                {
                    var snapshot = plugin.Authentication.GetSnapshot(venue);
                    DrawStatusText(snapshot.Message, snapshot.Status);
                    if (snapshot.AccessTokenExpiresAt is { } expiresAt)
                    {
                        ImGui.TextDisabled($"Access token expires: {expiresAt.ToLocalTime():g}");
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

                if (ImGui.Button("Remove venue from this plugin"))
                {
                    pendingLocalRemoval = venue;
                    ImGui.OpenPopup("Remove saved venue###PartyPulseRemoveSavedVenue");
                }
            }

            ImGui.PopID();
            ImGui.Spacing();
        }

    }

    private void DrawRemoveVenueConfirmation()
    {
        if (!ImGui.BeginPopupModal(
                "Remove saved venue###PartyPulseRemoveSavedVenue",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped($"Remove {pendingLocalRemoval?.DisplayLabel ?? "this venue"} from this plugin?");
        if (pendingLocalRemoval?.IsRegistered == true)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.65f, 0.3f, 1f),
                "This deletes the locally stored device credential but does not leave the venue or revoke the server-side device.");
        }
        else
        {
            ImGui.TextDisabled("This only removes the saved public venue from your local list.");
        }

        if (ImGui.Button("Remove") && pendingLocalRemoval is not null)
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
        ImGui.TextUnformatted($"Registered staff device ID: {venue.DeviceId}");
        if (venue.RefreshTokenUpdatedAt is { } updatedAt)
        {
            ImGui.TextDisabled($"Refresh token last updated: {updatedAt.ToLocalTime():g}");
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
        ImGui.TextUnformatted("Register an additional device");
        ImGui.TextWrapped("On an already registered device, create a pairing code under My Account. Enter that code here on the new computer.");

        var code = pairingCodes.GetValueOrDefault(venue.ProfileId, string.Empty);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Device pairing code", ref code, 80, ImGuiInputTextFlags.Password))
        {
            pairingCodes[venue.ProfileId] = code;
        }

        if (ImGui.Button("Register with device code"))
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
