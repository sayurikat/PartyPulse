using System;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using PartyPulse.Api;
using PartyPulse.Integrations;
using PartyPulse.Integrations.Lifestream;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Windows;

internal sealed class PublicOpeningRenderer(Plugin plugin, PartyPulseApiClient apiClient) : IDisposable
{
    private static readonly TimeSpan PublicLeadTime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly LifestreamApi lifestream = new(Plugin.PluginInterface, Plugin.Log);
    private readonly PublicOpeningImages images = new(Plugin.TextureProvider, Plugin.Log);

    private (Guid ProfileId, string Code, string ApiUrl)? venueKey;
    private Uri? baseUri;
    private string? selectionError;
    private CancellationTokenSource? venueCancellation;
    private Task<ApiResult<PublicVenueOpeningResponse>?>? pendingRequest;
    private ApiResult<PublicVenueOpeningResponse>? result;
    private DateTimeOffset nextRefreshUtc;
    private PluginIntegrationResult? travelResult;

    public void Draw(VenueConnectionConfiguration venue)
    {
        SelectVenue(venue);
        var now = DateTimeOffset.UtcNow;
        if (baseUri is not null)
        {
            Refresh(now);
        }

        var opening = result?.Value?.Opening;
        if (opening is null || opening.OpensAtUtc - now > PublicLeadTime || now >= opening.ClosesAtUtc)
        {
            images.Clear();
            travelResult = null;
            ImGui.PushStyleColor(ImGuiCol.Text, PartyPulseUi.Muted);
            DrawWrappedText(venue.AddressDisplay);
            ImGui.PopStyleColor();
            PartyPulseUi.SectionHeader("Opening status");
            DrawWrappedText(selectionError ?? (result switch
            {
                null => "Loading opening status…",
                { Success: false } => "Opening status is unavailable. Party Pulse will retry automatically.",
                _ => "There is no active opening or opening within the next hour.",
            }));
            return;
        }

        var zone = VenueTimeZone.Resolve(venue);
        PartyPulseUi.SectionHeader(opening.Title);
        if (!string.IsNullOrWhiteSpace(opening.ThemeName) &&
            !string.Equals(opening.ThemeName, opening.Title, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.TextUnformatted(opening.ThemeName);
        }

        if (now < opening.OpensAtUtc)
        {
            var remaining = TimeSpan.FromSeconds(Math.Ceiling((opening.OpensAtUtc - now).TotalSeconds));
            PartyPulseUi.InlineStatus($"Opens in {remaining:hh\\:mm\\:ss}", PartyPulseUi.Warning);
            ImGui.TextUnformatted($"Opens at {FormatTime(zone, opening.OpensAtUtc)}");
        }
        else
        {
            PartyPulseUi.InlineStatus("Open now", PartyPulseUi.Success);
        }

        ImGui.TextUnformatted($"Closes at {FormatTime(zone, opening.ClosesAtUtc)}");
        DrawLocation(opening.Location);

        PartyPulseUi.SectionHeader("Gamba Shot");
        if (opening.GambaJackpotGil is { } jackpot)
        {
            ImGui.TextUnformatted($"Current jackpot · {jackpot:N0} gil");
        }
        else
        {
            ImGui.TextDisabled("No active Gamba Shot game.");
        }

        DrawGiveaways(opening, zone, now);
        DrawDjs(opening, zone, now);
    }

    public void Clear()
    {
        if (venueKey is null)
        {
            return;
        }

        venueCancellation?.Cancel();
        venueCancellation?.Dispose();
        venueCancellation = null;
        venueKey = null;
        baseUri = null;
        selectionError = null;
        pendingRequest = null;
        result = null;
        nextRefreshUtc = default;
        travelResult = null;
        images.Clear();
    }

    public void Dispose()
    {
        Clear();
        images.Dispose();
    }

    private void SelectVenue(VenueConnectionConfiguration venue)
    {
        var key = (venue.ProfileId, venue.VenueCode, plugin.Configuration.ApiBaseUrl);
        if (venueKey == key)
        {
            return;
        }

        Clear();
        venueKey = key;
        if (!VenueConnectionConfiguration.TryNormalizeVenueCode(venue.VenueCode, out _))
        {
            selectionError = "Add the venue using its public code to see opening details.";
            return;
        }

        if (!PartyPulseApiClient.TryCreateBaseUri(key.ApiBaseUrl, out baseUri, out var error))
        {
            selectionError = error;
            return;
        }

        venueCancellation = CancellationTokenSource.CreateLinkedTokenSource(plugin.LifetimeToken);
    }

    private void Refresh(DateTimeOffset now)
    {
        if (pendingRequest is { IsCompleted: true })
        {
            var refreshed = pendingRequest.GetAwaiter().GetResult();
            pendingRequest = null;
            if (refreshed?.Value?.Opening?.Location != result?.Value?.Opening?.Location)
            {
                travelResult = null;
            }

            result = refreshed;
            images.Retain(result?.Value?.Opening?.Djs);
            var retryAfter = result?.Failure?.RetryAfter;
            nextRefreshUtc = now + (retryAfter > RefreshInterval ? retryAfter.Value : RefreshInterval);
        }

        if (pendingRequest is null && now >= nextRefreshUtc)
        {
            pendingRequest = LoadAsync(baseUri!, venueKey!.Value.Code, venueCancellation!.Token);
        }
    }

    private async Task<ApiResult<PublicVenueOpeningResponse>?> LoadAsync(
        Uri uri,
        string venueCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await apiClient.GetPublicVenueOpeningAsync(uri, venueCode, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "Could not load public opening status.");
            return ApiResult<PublicVenueOpeningResponse>.Failed(new ApiFailure(
                ApiFailureKind.Unknown,
                "PUBLIC_OPENING_FAILED",
                "Opening status is unavailable."));
        }
    }

    private void DrawLocation(PublicOpeningLocation location)
    {
        PartyPulseUi.SectionHeader("Opening address");
        if (!string.IsNullOrWhiteSpace(location.OutdoorLocationName))
        {
            DrawWrappedText($"{location.WorldName}, {location.OutdoorLocationName}");
            DrawOutdoorTravel(location);
            return;
        }

        if (string.IsNullOrWhiteSpace(location.WorldName) || string.IsNullOrWhiteSpace(location.CityName) ||
            location.Ward is not > 0 || location.Plot is not > 0)
        {
            ImGui.TextDisabled("Address not published.");
            return;
        }

        var ward = location.Ward.Value;
        var plot = location.Plot.Value;
        DrawWrappedText($"{location.WorldName}, {location.CityName}, Ward {ward}, Plot {plot}");
        if (CanTravel() &&
            !plugin.LocationProvider.IsAtAddress(location.WorldName, location.CityName, ward, plot, out _))
        {
            if (ImGui.Button("Travel with Lifestream"))
            {
                travelResult = lifestream.TravelToHousingAddress(location.WorldName, location.CityName, ward, plot);
            }

            DrawTravelResult();
        }
    }

    private void DrawOutdoorTravel(PublicOpeningLocation location)
    {
        if (!CanTravel() || string.IsNullOrWhiteSpace(location.WorldName) ||
            string.Equals(Plugin.PlayerState.CurrentWorld.Value.Name.ToString(), location.WorldName,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (ImGui.Button($"Travel to {location.WorldName} with Lifestream##TravelToOpeningWorld"))
        {
            travelResult = lifestream.TravelToWorld(location.WorldName);
        }

        DrawTravelResult();
    }

    private static bool CanTravel() =>
        Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded && Plugin.PlayerState.CurrentWorld.IsValid;

    private void DrawTravelResult()
    {
        if (travelResult is { } travel)
        {
            DrawWrappedText(travel.Success
                ? "Travel request sent to Lifestream."
                : travel.Failure?.Message ?? "Lifestream could not start travel.");
        }
    }

    private static void DrawGiveaways(PublicOpeningSummary opening, TimeZoneInfo zone, DateTimeOffset now)
    {
        PartyPulseUi.SectionHeader("Giveaways");
        var hasGiveaway = false;
        for (var index = 0; index < opening.Giveaways.Count; index++)
        {
            var giveaway = opening.Giveaways[index];
            if (giveaway.EndsAtUtc <= now)
            {
                continue;
            }

            hasGiveaway = true;
            ImGui.PushID($"OpeningGiveaway{index}");
            DrawWrappedText(giveaway.Title);
            ImGui.TextDisabled($"Ends at {FormatTime(zone, giveaway.EndsAtUtc)}");
            if (TryGetHttpsLink(giveaway.DiscordUrl, out var discord) &&
                discord!.IdnHost == "discord.com" &&
                discord.AbsolutePath.StartsWith("/channels/", StringComparison.Ordinal) &&
                ImGui.SmallButton("Enter on Discord"))
            {
                Util.OpenLink(discord.AbsoluteUri);
            }

            ImGui.PopID();
        }

        if (!hasGiveaway)
        {
            ImGui.TextDisabled("No active giveaways.");
        }
    }

    private void DrawDjs(PublicOpeningSummary opening, TimeZoneInfo zone, DateTimeOffset now)
    {
        PartyPulseUi.SectionHeader("DJ lineup");
        var hasDj = false;
        for (var index = 0; index < opening.Djs.Count; index++)
        {
            var dj = opening.Djs[index];
            if (dj.EndsAtUtc <= now)
            {
                continue;
            }

            hasDj = true;
            ImGui.PushID($"OpeningDj{index}");
            var avatar = images.Get(dj.TwitchProfileImageUrl);
            if (avatar is not null)
            {
                ImGui.Image(avatar.Handle, new Vector2(36f * ImGuiHelpers.GlobalScale));
                ImGui.SameLine();
            }

            ImGui.BeginGroup();
            var current = dj.StartsAtUtc <= now;
            PartyPulseUi.InlineStatus(current ? "Now" : "Upcoming", current ? PartyPulseUi.Success : PartyPulseUi.Info);
            ImGui.SameLine();
            DrawWrappedText(dj.Name);
            ImGui.TextDisabled($"{FormatTime(zone, dj.StartsAtUtc)} – {FormatTime(zone, dj.EndsAtUtc)}");
            if (TryGetHttpsLink(dj.TwitchUrl, out var twitch) &&
                twitch!.IdnHost is "twitch.tv" or "www.twitch.tv" or "m.twitch.tv" &&
                ImGui.SmallButton("Watch on Twitch"))
            {
                Util.OpenLink(twitch.AbsoluteUri);
            }

            ImGui.EndGroup();

            var preview = images.Get(dj.TwitchStreamPreviewUrl);
            if (preview is { Width: > 0, Height: > 0 })
            {
                var width = Math.Min(320f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X);
                if (width > 0)
                {
                    ImGui.Image(preview.Handle, new Vector2(width, width * preview.Height / preview.Width));
                }
            }

            ImGui.PopID();
            ImGui.Spacing();
        }

        if (!hasDj)
        {
            ImGui.TextDisabled("No current or upcoming DJ sets.");
        }
    }

    internal static bool TryGetHttpsLink(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    private static void DrawWrappedText(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static string FormatTime(TimeZoneInfo zone, DateTimeOffset time) =>
        VenueTimeZone.Convert(zone, time).ToString("ddd HH:mm zzz", CultureInfo.CurrentCulture);
}
