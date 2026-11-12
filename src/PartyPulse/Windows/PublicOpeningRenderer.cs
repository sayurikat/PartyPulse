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
    private static readonly Vector4 HeroBackground = new(0.19f, 0.15f, 0.24f, 1f);
    private static readonly Vector4 HeroBorder = new(0.36f, 0.27f, 0.43f, 1f);
    private static readonly Vector4 PanelBackground = new(0.17f, 0.17f, 0.22f, 1f);
    private static readonly Vector4 PanelBorder = new(0.27f, 0.27f, 0.34f, 1f);
    private static readonly Vector4 PanelText = new(0.96f, 0.94f, 0.98f, 1f);

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

    public void Draw(VenueConnectionConfiguration? venue)
    {
        if (venue is null)
        {
            DrawOpeningStatus("No venue configured", "Add a venue from Settings.");
            DrawAccess(false);
            return;
        }

        SelectVenue(venue);
        var now = DateTimeOffset.UtcNow;
        if (baseUri is not null)
        {
            Refresh(now);
        }

        var venueName = string.IsNullOrWhiteSpace(venue.VenueName) ? venue.DisplayLabel : venue.VenueName;
        var opening = result?.Value?.Opening;
        if (opening is null || opening.OpensAtUtc - now > PublicLeadTime || now >= opening.ClosesAtUtc)
        {
            images.Clear();
            travelResult = null;
            var status = selectionError ?? (result switch
            {
                null => "Loading opening status…",
                { Success: false } => "Opening status is unavailable. Party Pulse will retry automatically.",
                _ => "There is no active opening or opening within the next hour.",
            });
            DrawOpeningStatus(venueName, status, venue.AddressDisplay);
            DrawAccess(venue.IsRegistered);
            return;
        }

        var zone = VenueTimeZone.Resolve(venue);
        DrawOpeningHero(venueName, opening, zone, now);
        DrawOpeningBody(opening, zone, now, venue.IsRegistered);
    }

    private static void DrawOpeningStatus(string title, string status, string? address = null)
    {
        if (!BeginPanel("OpeningStatus", featured: true))
        {
            return;
        }

        DrawTitle(title);
        DrawMutedText(status);
        if (address is not null)
        {
            ImGui.Spacing();
            DrawHeading("Venue address");
            DrawWrappedText(address);
        }

        EndPanel();
        ImGui.Spacing();
    }

    private void DrawOpeningHero(string venueName, PublicOpeningSummary opening, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!BeginPanel("OpeningHero", featured: true))
        {
            return;
        }

        if (ImGui.GetContentRegionAvail().X >= 460f * ImGuiHelpers.GlobalScale && BeginColumns("OpeningHeroColumns"))
        {
            ImGui.TableNextColumn();
            DrawOpeningTitle(venueName, opening);
            ImGui.TableNextColumn();
            DrawOpeningTimes(opening, zone, now);
            EndColumns();
        }
        else
        {
            DrawOpeningTitle(venueName, opening);
            ImGui.Spacing();
            DrawOpeningTimes(opening, zone, now);
        }

        DrawDivider();
        DrawLocation(opening.Location);
        EndPanel();
        ImGui.Spacing();
    }

    private static void DrawOpeningTitle(string venueName, PublicOpeningSummary opening)
    {
        var title = opening.Title;
        var theme = opening.ThemeName;
        if (string.IsNullOrWhiteSpace(theme) || string.Equals(theme, title, StringComparison.OrdinalIgnoreCase))
        {
            theme = null;
        }

        if (string.Equals(venueName, title, StringComparison.OrdinalIgnoreCase))
        {
            if (theme is not null)
            {
                DrawMutedText(venueName);
            }

            DrawTitle(theme ?? title);
            return;
        }

        DrawMutedText(venueName);
        DrawTitle(title);
        if (theme is not null)
        {
            DrawMutedText(theme);
        }
    }

    private static void DrawOpeningTimes(PublicOpeningSummary opening, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (now < opening.OpensAtUtc)
        {
            var remaining = TimeSpan.FromSeconds(Math.Ceiling((opening.OpensAtUtc - now).TotalSeconds));
            PartyPulseUi.InlineStatus($"Opens in {remaining:hh\\:mm\\:ss}", PartyPulseUi.Warning);
            DrawMutedText($"Opens at {FormatTime(zone, opening.OpensAtUtc)}");
        }
        else
        {
            PartyPulseUi.InlineStatus("Open now", PartyPulseUi.Success);
        }

        DrawMutedText($"Closes at {FormatTime(zone, opening.ClosesAtUtc)}");
    }

    private void DrawOpeningBody(PublicOpeningSummary opening, TimeZoneInfo zone, DateTimeOffset now, bool registered)
    {
        if (ImGui.GetContentRegionAvail().X >= 560f * ImGuiHelpers.GlobalScale && BeginColumns("OpeningBodyColumns"))
        {
            ImGui.TableNextColumn();
            DrawDjs(opening, zone, now);
            ImGui.TableNextColumn();
            DrawActivities(opening, zone, now);
            DrawAccess(registered);
            EndColumns();
            return;
        }

        DrawDjs(opening, zone, now);
        DrawActivities(opening, zone, now);
        DrawAccess(registered);
    }

    private static void DrawActivities(PublicOpeningSummary opening, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!BeginPanel("OpeningActivities"))
        {
            return;
        }

        DrawHeading("Gamba Shot");
        if (opening.GambaJackpotGil is { } jackpot)
        {
            DrawMutedText("Current jackpot");
            DrawTitle($"{jackpot:N0} gil", 1.24f);
        }
        else
        {
            DrawMutedText("No active Gamba Shot game.");
        }

        DrawDivider();
        DrawGiveaways(opening, zone, now);
        EndPanel();
        ImGui.Spacing();
    }

    private void DrawAccess(bool registered)
    {
        if (!BeginPanel("OverviewAccess"))
        {
            return;
        }

        DrawHeading("Venue access");
        ImGui.PushTextWrapPos(0);
        PartyPulseUi.InlineStatus(
            registered ? "Registered venue account" : "Visitor access",
            registered ? PartyPulseUi.Success : PartyPulseUi.Muted);
        ImGui.PopTextWrapPos();

        DrawDivider();
        DrawHeading("Current character");
        if (plugin.IdentityProvider.TryGetCurrent(out var identity, out var reason))
        {
            DrawWrappedText(identity!.DisplayName);
        }
        else
        {
            DrawMutedText(reason);
        }

        EndPanel();
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
        DrawHeading("Opening address");
        if (!string.IsNullOrWhiteSpace(location.OutdoorLocationName))
        {
            DrawWrappedText($"{location.WorldName}, {location.OutdoorLocationName}");
            DrawOutdoorTravel(location);
            return;
        }

        if (string.IsNullOrWhiteSpace(location.WorldName) || string.IsNullOrWhiteSpace(location.CityName) ||
            location.Ward is not > 0 || location.Plot is not > 0)
        {
            DrawMutedText("Address not published.");
            return;
        }

        var ward = location.Ward.Value;
        var plot = location.Plot.Value;
        DrawWrappedText($"{location.WorldName}, {location.CityName}, Ward {ward}, Plot {plot}");
        if (CanTravel() &&
            !plugin.LocationProvider.IsAtAddress(location.WorldName, location.CityName, ward, plot, out _))
        {
            if (ActionButton("Travel with Lifestream"))
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

        if (ActionButton($"Travel to {location.WorldName} with Lifestream##TravelToOpeningWorld"))
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
        DrawHeading("Giveaways");
        var hasGiveaway = false;
        for (var index = 0; index < opening.Giveaways.Count; index++)
        {
            var giveaway = opening.Giveaways[index];
            if (giveaway.EndsAtUtc <= now)
            {
                continue;
            }

            if (hasGiveaway)
            {
                DrawDivider();
            }

            hasGiveaway = true;
            ImGui.PushID($"OpeningGiveaway{index}");
            DrawWrappedText(giveaway.Title);
            DrawMutedText($"Ends at {FormatTime(zone, giveaway.EndsAtUtc)}");
            if (TryGetHttpsLink(giveaway.DiscordUrl, out var discord) &&
                discord!.IdnHost == "discord.com" &&
                discord.AbsolutePath.StartsWith("/channels/", StringComparison.Ordinal) &&
                ActionButton("Enter on Discord"))
            {
                Util.OpenLink(discord.AbsoluteUri);
            }

            ImGui.PopID();
        }

        if (!hasGiveaway)
        {
            DrawMutedText("No active giveaways.");
        }
    }

    private void DrawDjs(PublicOpeningSummary opening, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!BeginPanel("OpeningDjs"))
        {
            return;
        }

        DrawHeading("DJ lineup");
        ImGui.Spacing();
        var hasDj = false;
        for (var index = 0; index < opening.Djs.Count; index++)
        {
            var dj = opening.Djs[index];
            if (dj.EndsAtUtc <= now)
            {
                continue;
            }

            if (hasDj)
            {
                DrawDivider();
            }

            hasDj = true;
            ImGui.PushID($"OpeningDj{index}");
            DrawDj(dj, zone, now);
            ImGui.PopID();
        }

        if (!hasDj)
        {
            DrawMutedText("No current or upcoming DJ sets.");
        }

        EndPanel();
        ImGui.Spacing();
    }

    private void DrawDj(PublicOpeningDj dj, TimeZoneInfo zone, DateTimeOffset now)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var avatar = images.Get(dj.TwitchProfileImageUrl);
        if (avatar is not null)
        {
            ImGui.Image(avatar.Handle, new Vector2(40f * scale));
            ImGui.SameLine(0f, 10f * scale);
        }

        ImGui.BeginGroup();
        var current = dj.StartsAtUtc <= now;
        var status = current ? "Now playing" : "Upcoming";
        var gap = 10f * scale;
        var statusWidth = ImGui.CalcTextSize(status).X + (13f * scale);
        var fitsOnOneLine = ImGui.CalcTextSize(dj.Name).X + gap + statusWidth <= ImGui.GetContentRegionAvail().X;
        DrawWrappedText(dj.Name);
        if (fitsOnOneLine)
        {
            ImGui.SameLine(0f, gap);
        }

        PartyPulseUi.InlineStatus(status, current ? PartyPulseUi.Success : PartyPulseUi.Info);
        DrawMutedText($"{FormatTime(zone, dj.StartsAtUtc)} – {FormatTime(zone, dj.EndsAtUtc)}");
        if (TryGetHttpsLink(dj.TwitchUrl, out var twitch) &&
            twitch!.IdnHost is "twitch.tv" or "www.twitch.tv" or "m.twitch.tv" &&
            ActionButton("Watch on Twitch"))
        {
            Util.OpenLink(twitch.AbsoluteUri);
        }

        ImGui.EndGroup();

        var preview = images.Get(dj.TwitchStreamPreviewUrl);
        if (preview is { Width: > 0, Height: > 0 })
        {
            var width = Math.Min(480f * scale, ImGui.GetContentRegionAvail().X);
            if (width > 0)
            {
                ImGui.Spacing();
                ImGui.Image(preview.Handle, new Vector2(width, width * preview.Height / preview.Width));
            }
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

    private static void DrawMutedText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, PartyPulseUi.Muted);
        DrawWrappedText(text);
        ImGui.PopStyleColor();
    }

    private static void DrawHeading(string text) => ImGui.TextColored(PartyPulseUi.Accent, text);

    private static void DrawTitle(string text, float scale = 1.55f)
    {
        ImGui.SetWindowFontScale(scale);
        DrawWrappedText(text);
        ImGui.SetWindowFontScale(1f);
    }

    private static void DrawDivider()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static bool ActionButton(string label)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 4f) * scale);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleVar(2);
        return clicked;
    }

    private static bool BeginPanel(string id, bool featured = false)
    {
        var border = featured ? HeroBorder : PanelBorder;
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(14f, 12f) * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, border);
        ImGui.PushStyleColor(ImGuiCol.Separator, border);
        ImGui.PushStyleColor(ImGuiCol.Text, PanelText);
        if (ImGui.BeginTable(id, 1, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersOuter))
        {
            ImGui.TableNextColumn();
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(featured ? HeroBackground : PanelBackground));
            return true;
        }

        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        return false;
    }

    private static void EndPanel()
    {
        ImGui.EndTable();
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
    }

    private static bool BeginColumns(string id)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(6f * ImGuiHelpers.GlobalScale, 0f));
        if (ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX))
        {
            ImGui.TableSetupColumn("Main", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("Aside", ImGuiTableColumnFlags.WidthStretch, 2f);
            return true;
        }

        ImGui.PopStyleVar();
        return false;
    }

    private static void EndColumns()
    {
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    private static string FormatTime(TimeZoneInfo zone, DateTimeOffset time) =>
        VenueTimeZone.Convert(zone, time).ToString("ddd HH:mm", CultureInfo.CurrentCulture);
}
