using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using System.Numerics;

namespace PartyPulse.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private bool dirty;

    public ConfigWindow(Plugin plugin)
        : base("Party Pulse Settings###PartyPulseConfig")
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
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
        DrawVenueConnections();
        ImGui.Spacing();
        DrawFooter();
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
        if (ImGui.Checkbox("Authenticate configured venues automatically after login", ref autoConnect))
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

    private void DrawVenueConnections()
    {
        ImGui.TextUnformatted("Venue connections");
        ImGui.TextWrapped("Each venue profile keeps its own device ID and refresh token. Access tokens remain in memory only.");

        var removeIndex = -1;
        for (var index = 0; index < configuration.VenueConnections.Count; index++)
        {
            var venue = configuration.VenueConnections[index];
            ImGui.PushID(venue.ProfileId.ToString("N"));

            if (ImGui.CollapsingHeader(venue.DisplayLabel, ImGuiTreeNodeFlags.DefaultOpen))
            {
                var displayName = venue.DisplayName;
                ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputText("Display name", ref displayName, 80))
                {
                    venue.DisplayName = displayName;
                    dirty = true;
                }

                var venueId = venue.VenueId;
                ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("Venue ID", ref venueId))
                {
                    venue.VenueId = Math.Max(0, venueId);
                    dirty = true;
                }

                var deviceId = venue.DeviceId;
                ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("Device ID", ref deviceId))
                {
                    venue.DeviceId = Math.Max(0, deviceId);
                    dirty = true;
                }

                var deviceName = venue.DeviceName;
                ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputText("Device name", ref deviceName, 80))
                {
                    venue.DeviceName = deviceName;
                    dirty = true;
                }

                var refreshToken = venue.RefreshToken;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText(
                        "Refresh token",
                        ref refreshToken,
                        128,
                        ImGuiInputTextFlags.Password))
                {
                    venue.RefreshToken = refreshToken.Trim();
                    dirty = true;
                }

                ImGui.TextDisabled($"Stored token length: {venue.RefreshToken.Length}");

                var snapshot = plugin.Authentication.GetSnapshot(venue);
                DrawStatusText(snapshot.Message, snapshot.Status);
                if (snapshot.AccessTokenExpiresAt is { } expiresAt)
                {
                    ImGui.TextDisabled($"Access token expires: {expiresAt.ToLocalTime():g}");
                }

                if (ImGui.Button("Save and authenticate"))
                {
                    SaveChanges();
                    configuration.SelectedVenueProfileId = venue.ProfileId;
                    configuration.Save();
                    plugin.ConnectVenue(venue);
                }

                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                {
                    removeIndex = index;
                }
            }

            ImGui.PopID();
            ImGui.Spacing();
        }

        if (removeIndex >= 0)
        {
            var removed = configuration.VenueConnections[removeIndex];
            plugin.Authentication.RemoveProfile(removed.ProfileId);
            configuration.VenueConnections.RemoveAt(removeIndex);
            configuration.Normalize();
            configuration.Save();
            dirty = false;
        }

        if (ImGui.Button("Add venue connection"))
        {
            var venue = new VenueConnectionConfiguration();
            configuration.VenueConnections.Add(venue);
            configuration.SelectedVenueProfileId = venue.ProfileId;
            dirty = true;
        }
    }

    private void DrawFooter()
    {
        if (ImGui.Button(dirty ? "Save changes *" : "Save changes"))
        {
            SaveChanges();
        }

        ImGui.SameLine();
        if (ImGui.Button("Authenticate all configured venues"))
        {
            SaveChanges();
            plugin.ConnectAllConfiguredVenues();
        }
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
            AuthenticationStatus.Failed => new Vector4(1f, 0.4f, 0.4f, 1f),
            AuthenticationStatus.Expired => new Vector4(1f, 0.65f, 0.3f, 1f),
            _ => new Vector4(0.65f, 0.65f, 0.65f, 1f),
        };

        ImGui.TextColored(color, message);
    }
}
