using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Authentication;
using PartyPulse.Models;
using System.Numerics;

namespace PartyPulse.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Party Pulse###PartyPulseMain")
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 440),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();
        DrawFeatureTabs();
    }

    private void DrawHeader()
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
            return;
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
        }
        else
        {
            ImGui.TextDisabled("Visitor mode — public venue information only.");
        }
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

    private void DrawFeatureTabs()
    {
        if (!ImGui.BeginTabBar("PartyPulseFeatureTabs"))
        {
            return;
        }

        DrawOverviewTab();
        DrawPlaceholderTab("VIP", "VIP purchases, Discord identity, role automation, and payout totals will live here.");
        DrawPlaceholderTab("Staff", "Clock-in state, staff tools, macros, timers, and Party Finder controls will live here.");
        DrawPlaceholderTab("Payout", "Manager payout calculations, adjustments, finalization, and payment actions will live here.");
        DrawPlaceholderTab("Bar", "Bar sales, gambashots, jackpots, and buyout tracking will live here.");
        DrawPlaceholderTab("Games", "Venue-wide game state, rolls, host controls, and timers will live here.");
        DrawPlaceholderTab("Greeter", "Target-aware greeting actions and VIP-specific greeting selection will live here.");

        ImGui.EndTabBar();
    }

    private void DrawOverviewTab()
    {
        if (!ImGui.BeginTabItem("Overview"))
        {
            return;
        }

        var selectedVenue = plugin.Configuration.GetSelectedVenue();
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
