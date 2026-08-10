using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Globalization;
using PartyPulse.Models;

namespace PartyPulse.Api;

public sealed class PartyPulseApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient;

    public PartyPulseApiClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static bool TryCreateBaseUri(string rawValue, out Uri? baseUri, out string error)
    {
        baseUri = null;
        var value = rawValue?.Trim() ?? string.Empty;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            error = "API base URL must be an absolute URL.";
            return false;
        }

        var isHttps = parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLocalHttp = parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && parsed.IsLoopback;
        if (!isHttps && !isLocalHttp)
        {
            error = "Use HTTPS. Plain HTTP is accepted only for localhost development.";
            return false;
        }

        baseUri = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        error = string.Empty;
        return true;
    }

    public Task<ApiResult<PublicVenueResponse>> GetPublicVenueByCodeAsync(
        Uri baseUri,
        string venueCode,
        CancellationToken cancellationToken)
    {
        var normalizedCode = VenueConnectionConfiguration.NormalizeVenueCode(venueCode);
        var path = $"api/v1/venues/code/{Uri.EscapeDataString(normalizedCode)}";
        return SendJsonAsync<PublicVenueResponse>(
            new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path)),
            ValidatePublicVenueResponse,
            cancellationToken);
    }

    public Task<ApiResult<PublicVenueResponse>> GetPublicVenueByAddressAsync(
        Uri baseUri,
        VenueAddress address,
        CancellationToken cancellationToken)
    {
        var path = "api/v1/venues/address" +
            $"?worldName={Uri.EscapeDataString(address.WorldName)}" +
            $"&cityName={Uri.EscapeDataString(address.CityName)}" +
            $"&ward={address.Ward}" +
            $"&plot={address.Plot}";
        return SendJsonAsync<PublicVenueResponse>(
            new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path)),
            ValidatePublicVenueResponse,
            cancellationToken);
    }

    public Task<ApiResult<RefreshTokenResponse>> RefreshAsync(
        Uri baseUri,
        RefreshTokenRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<RefreshTokenResponse>(
            new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/auth/refresh"))
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            },
            ValidateRefreshResponse,
            cancellationToken);

    public Task<ApiResult<DeviceAuthenticationResponse>> RedeemInviteAsync(
        Uri baseUri,
        RedeemInviteRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<DeviceAuthenticationResponse>(
            new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/auth/invite/redeem"))
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            },
            ValidateDeviceAuthenticationResponse,
            cancellationToken);

    public Task<ApiResult<DeviceAuthenticationResponse>> RecoverAsync(
        Uri baseUri,
        RecoverAccountRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<DeviceAuthenticationResponse>(
            new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/auth/recover"))
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            },
            ValidateDeviceAuthenticationResponse,
            cancellationToken);

    public Task<ApiResult<DeviceAuthenticationResponse>> RedeemDevicePairingCodeAsync(
        Uri baseUri,
        RedeemDevicePairingCodeRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<DeviceAuthenticationResponse>(
            new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/auth/device/redeem"))
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            },
            ValidateDeviceAuthenticationResponse,
            cancellationToken);

    public Task<ApiResult<LinkCurrentCharacterResponse>> LinkCurrentCharacterAsync(
        Uri baseUri,
        LinkCurrentCharacterRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<LinkCurrentCharacterResponse>(
            new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/auth/character/link"))
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            },
            static payload => payload.CharacterId > 0,
            cancellationToken);

    public Task<ApiResult<AuthenticationConfirmationResponse>> ConfirmAuthenticationAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/auth/confirm"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return SendJsonAsync<AuthenticationConfirmationResponse>(
            message,
            ValidateConfirmationResponse,
            cancellationToken);
    }

    public async Task<ApiResult<TResponse>> SendAuthorizedAsync<TResponse>(
        Uri baseUri,
        HttpMethod method,
        string relativePath,
        string accessToken,
        object? body,
        Func<TResponse, bool> validator,
        CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(method, new Uri(baseUri, relativePath.TrimStart('/')));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await SendJsonAsync(message, validator, cancellationToken);
    }


    public Task<ApiResult<VenueUserManagementViewResponse>> GetVenueUsersAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VenueUserManagementViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/venue-users",
            accessToken,
            null,
            ValidateVenueUserManagementViewResponse,
            cancellationToken);

    public Task<ApiResult<CreateVenueUserResponse>> CreateVenueUserAsync(
        Uri baseUri,
        string accessToken,
        CreateVenueUserRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreateVenueUserResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/venue-users",
            accessToken,
            request,
            ValidateCreateVenueUserResponse,
            cancellationToken);

    public Task<ApiResult<VenueUserOperationResponse>> UpdateVenueUserProfileAsync(
        Uri baseUri,
        string accessToken,
        int userId,
        UpdateVenueUserProfileRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VenueUserOperationResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/venue-users/{userId}",
            accessToken,
            request,
            ValidateVenueUserOperationResponse,
            cancellationToken);

    public Task<ApiResult<SetVenueUserPermissionsResponse>> SetVenueUserPermissionsAsync(
        Uri baseUri,
        string accessToken,
        int userId,
        SetVenueUserPermissionsRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SetVenueUserPermissionsResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/venue-users/{userId}/permissions",
            accessToken,
            request,
            ValidateSetVenueUserPermissionsResponse,
            cancellationToken);

    public Task<ApiResult<SelfServiceViewResponse>> GetSelfServiceAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SelfServiceViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/self",
            accessToken,
            null,
            ValidateSelfServiceViewResponse,
            cancellationToken);

    public Task<ApiResult<SelfServiceOperationResponse>> UnlinkSelfCharacterAsync(
        Uri baseUri,
        string accessToken,
        int characterId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SelfServiceOperationResponse>(
            baseUri,
            HttpMethod.Delete,
            $"api/v1/self/characters/{characterId}",
            accessToken,
            null,
            static payload => payload.Success,
            cancellationToken);

    public Task<ApiResult<DevicePairingCodeResponse>> CreateDevicePairingCodeAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DevicePairingCodeResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/self/device-pairing-code",
            accessToken,
            null,
            static payload => !string.IsNullOrWhiteSpace(payload.PairingCode) && payload.ExpiresAt > DateTimeOffset.UtcNow,
            cancellationToken);

    public Task<ApiResult<SelfServiceOperationResponse>> UnauthorizeFromVenueAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SelfServiceOperationResponse>(
            baseUri,
            HttpMethod.Delete,
            "api/v1/self/authorization",
            accessToken,
            null,
            static payload => payload.Success,
            cancellationToken);

    public Task<ApiResult<CreateRecoveryCodeResponse>> CreateVenueUserRecoveryCodeAsync(
        Uri baseUri,
        string accessToken,
        int userId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreateRecoveryCodeResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/venue-users/{userId}/recovery-code",
            accessToken,
            null,
            ValidateCreateRecoveryCodeResponse,
            cancellationToken);

    public Task<ApiResult<RestoreVenueUserResponse>> RestoreVenueUserAsync(
        Uri baseUri,
        string accessToken,
        int userId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<RestoreVenueUserResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/venue-users/{userId}/restore",
            accessToken,
            null,
            ValidateRestoreVenueUserResponse,
            cancellationToken);

    public Task<ApiResult<VipManagementViewResponse>> GetVipAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipManagementViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/vip",
            accessToken,
            null,
            ValidateVipManagementViewResponse,
            cancellationToken);

    public Task<ApiResult<VipPackageOperationResponse>> CreateVipPackageAsync(
        Uri baseUri,
        string accessToken,
        CreateVipPackageRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipPackageOperationResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/vip/packages",
            accessToken,
            request,
            static payload => payload.PackageId > 0,
            cancellationToken);

    public Task<ApiResult<VipPackageOperationResponse>> UpdateVipPackageAsync(
        Uri baseUri,
        string accessToken,
        int packageId,
        UpdateVipPackageRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipPackageOperationResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/vip/packages/{packageId}",
            accessToken,
            request,
            static payload => payload.PackageId > 0,
            cancellationToken);

    public Task<ApiResult<SellVipSubscriptionResponse>> SellVipSubscriptionAsync(
        Uri baseUri,
        string accessToken,
        SellVipSubscriptionRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SellVipSubscriptionResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/vip/subscriptions",
            accessToken,
            request,
            ValidateVipSaleResponse,
            cancellationToken);

    public Task<ApiResult<VipCharacterOperationResponse>> LinkVipCharacterAsync(
        Uri baseUri,
        string accessToken,
        int vipPlayerId,
        LinkVipCharacterRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipCharacterOperationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/vip/players/{vipPlayerId}/characters",
            accessToken,
            request,
            static payload => payload.VipPlayerId > 0 &&
                              payload.CharacterId > 0 &&
                              payload.PreferredCharacterId > 0,
            cancellationToken);

    public Task<ApiResult<VipPreferredCharacterResponse>> SetVipPreferredCharacterAsync(
        Uri baseUri,
        string accessToken,
        int vipPlayerId,
        int characterId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipPreferredCharacterResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/vip/players/{vipPlayerId}/preferred-character/{characterId}",
            accessToken,
            null,
            static payload => payload.VipPlayerId > 0 && payload.PreferredCharacterId > 0,
            cancellationToken);

    public Task<ApiResult<VipPlayerOperationResponse>> UpdateVipPlayerAsync(
        Uri baseUri,
        string accessToken,
        int vipPlayerId,
        UpdateVipPlayerRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipPlayerOperationResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/vip/players/{vipPlayerId}",
            accessToken,
            request,
            static payload => payload.VipPlayerId > 0,
            cancellationToken);

    public Task<ApiResult<VipCharacterOperationResponse>> UnlinkVipCharacterAsync(
        Uri baseUri,
        string accessToken,
        int vipPlayerId,
        int characterId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipCharacterOperationResponse>(
            baseUri,
            HttpMethod.Delete,
            $"api/v1/vip/players/{vipPlayerId}/characters/{characterId}",
            accessToken,
            null,
            static payload => payload.VipPlayerId > 0 &&
                              payload.CharacterId > 0 &&
                              payload.PreferredCharacterId > 0,
            cancellationToken);

    public Task<ApiResult<VipSubscriptionCancellationResponse>> CancelVipSubscriptionAsync(
        Uri baseUri,
        string accessToken,
        long subscriptionId,
        CancelVipSubscriptionRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipSubscriptionCancellationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/vip/subscriptions/{subscriptionId}/cancel",
            accessToken,
            request,
            static payload => payload.SubscriptionId > 0 &&
                              payload.VipPlayerId > 0 &&
                              payload.CancelledAt != default,
            cancellationToken);

    public Task<ApiResult<VipSubscriptionPaymentStatusResponse>> SetVipSubscriptionPaymentStatusAsync(
        Uri baseUri,
        string accessToken,
        long subscriptionId,
        SetVipSubscriptionPaymentStatusRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipSubscriptionPaymentStatusResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/vip/subscriptions/{subscriptionId}/payment-status",
            accessToken,
            request,
            static payload => payload.SubscriptionId > 0 &&
                              (!payload.Settled || payload.PaidToVenueAt is not null),
            cancellationToken);

    public Task<ApiResult<DiscordManagementViewResponse>> GetDiscordManagementAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DiscordManagementViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/discord",
            accessToken,
            null,
            ValidateDiscordManagementViewResponse,
            cancellationToken);

    public Task<ApiResult<SaveDiscordVenueStatusResponse>> SaveDiscordVenueStatusAsync(
        Uri baseUri,
        string accessToken,
        SaveDiscordVenueStatusRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SaveDiscordVenueStatusResponse>(
            baseUri,
            HttpMethod.Put,
            "api/v1/discord/venue-status",
            accessToken,
            request,
            static payload =>
                payload.UpdatedAt != default &&
                !string.IsNullOrWhiteSpace(payload.OpenMessage) &&
                !string.IsNullOrWhiteSpace(payload.ClosedMessage) &&
                (!payload.Enabled || payload.ChannelId is > 0),
            cancellationToken);

    public Task<ApiResult<CreateDiscordGuildLinkCodeResponse>> CreateDiscordGuildLinkCodeAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreateDiscordGuildLinkCodeResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/discord/link-codes",
            accessToken,
            null,
            static payload => !string.IsNullOrWhiteSpace(payload.LinkCode) && payload.ExpiresAt > DateTimeOffset.UtcNow,
            cancellationToken);

    public Task<ApiResult<UnlinkDiscordGuildResponse>> UnlinkDiscordGuildAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UnlinkDiscordGuildResponse>(
            baseUri,
            HttpMethod.Delete,
            "api/v1/discord/guild",
            accessToken,
            null,
            static payload => payload.GuildId > 0 && payload.UnlinkedAt != default,
            cancellationToken);

    public Task<ApiResult<GiveawayManagementViewResponse>> GetGiveawaysAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<GiveawayManagementViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/giveaways",
            accessToken,
            null,
            ValidateGiveawayManagementViewResponse,
            cancellationToken);

    public Task<ApiResult<SaveGiveawayResponse>> CreateGiveawayAsync(
        Uri baseUri,
        string accessToken,
        SaveGiveawayRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SaveGiveawayResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/giveaways",
            accessToken,
            request,
            static payload => payload.GiveawayId > 0 && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<SaveGiveawayResponse>> UpdateGiveawayAsync(
        Uri baseUri,
        string accessToken,
        long giveawayId,
        SaveGiveawayRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SaveGiveawayResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/giveaways/{giveawayId}",
            accessToken,
            request,
            static payload => payload.GiveawayId > 0 && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<SaveGiveawaySchedulerResponse>> CreateGiveawaySchedulerAsync(
        Uri baseUri,
        string accessToken,
        SaveGiveawaySchedulerRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SaveGiveawaySchedulerResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/giveaways/schedulers",
            accessToken,
            request,
            static payload => payload.SchedulerId > 0 && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<SaveGiveawaySchedulerResponse>> UpdateGiveawaySchedulerAsync(
        Uri baseUri,
        string accessToken,
        long schedulerId,
        SaveGiveawaySchedulerRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SaveGiveawaySchedulerResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/giveaways/schedulers/{schedulerId}",
            accessToken,
            request,
            static payload => payload.SchedulerId > 0 && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<VenueOpeningScheduleResponse>> GetVenueOpeningScheduleAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VenueOpeningScheduleResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/venue-openings",
            accessToken,
            null,
            ValidateVenueOpeningScheduleResponse,
            cancellationToken);

    public Task<ApiResult<VenueOpeningHistoryResponse>> GetVenueOpeningHistoryAsync(
        Uri baseUri,
        string accessToken,
        int pageSize,
        DateTimeOffset? beforeOpensAt,
        long? beforeOpeningId,
        CancellationToken cancellationToken)
    {
        var path = $"api/v1/venue-openings/history?pageSize={pageSize}";
        if (beforeOpensAt is { } cursorTime && beforeOpeningId is { } cursorId)
        {
            path += $"&beforeOpensAt={Uri.EscapeDataString(cursorTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}" +
                    $"&beforeOpeningId={cursorId}";
        }

        return SendAuthorizedAsync<VenueOpeningHistoryResponse>(
            baseUri,
            HttpMethod.Get,
            path,
            accessToken,
            null,
            ValidateVenueOpeningHistoryResponse,
            cancellationToken);
    }

    public Task<ApiResult<VenueOpeningScheduleItem>> SaveVenueOpeningAsync(
        Uri baseUri,
        string accessToken,
        long? openingId,
        SaveVenueOpeningRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VenueOpeningScheduleItem>(
            baseUri,
            openingId is null ? HttpMethod.Post : HttpMethod.Put,
            openingId is null ? "api/v1/venue-openings" : $"api/v1/venue-openings/{openingId.Value}",
            accessToken,
            request,
            ValidateVenueOpeningScheduleItem,
            cancellationToken);

    public Task<ApiResult<CancelVenueOpeningResponse>> CancelVenueOpeningAsync(
        Uri baseUri,
        string accessToken,
        long openingId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CancelVenueOpeningResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/venue-openings/{openingId}/cancel",
            accessToken,
            null,
            static payload => payload.OpeningId > 0 && payload.CancelledAt != default,
            cancellationToken);


    public Task<ApiResult<OpeningPublicationContextResponse>> GetOpeningPublicationsAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OpeningPublicationContextResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/opening-publications",
            accessToken,
            null,
            ValidateOpeningPublicationContextResponse,
            cancellationToken);

    public Task<ApiResult<SaveOpeningPublicationTemplateResponse>> SaveOpeningPublicationTemplateAsync(
        Uri baseUri,
        string accessToken,
        string publicationCode,
        SaveOpeningPublicationTemplateRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SaveOpeningPublicationTemplateResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/opening-publications/templates/{Uri.EscapeDataString(publicationCode)}",
            accessToken,
            request,
            static payload => !string.IsNullOrWhiteSpace(payload.PublicationCode) && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<GenerateOpeningPublicationsResponse>> GenerateOpeningPublicationsAsync(
        Uri baseUri,
        string accessToken,
        long openingId,
        GenerateOpeningPublicationsRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<GenerateOpeningPublicationsResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/opening-publications/openings/{openingId}/generate",
            accessToken,
            request,
            static payload => payload.OpeningId > 0 &&
                              !string.IsNullOrWhiteSpace(payload.ChannelCode) &&
                              payload.Texts is not null,
            cancellationToken);

    public Task<ApiResult<SaveOpeningPublicationTextResponse>> SaveOpeningPublicationTextAsync(
        Uri baseUri,
        string accessToken,
        long openingId,
        string publicationCode,
        SaveOpeningPublicationTextRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SaveOpeningPublicationTextResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/opening-publications/openings/{openingId}/texts/{Uri.EscapeDataString(publicationCode)}",
            accessToken,
            request,
            static payload => payload.OpeningId > 0 &&
                              !string.IsNullOrWhiteSpace(payload.PublicationCode) &&
                              payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<ReportShoutrunnerDutyResponse>> ReportShoutrunnerDutyAsync(
        Uri baseUri,
        string accessToken,
        ReportShoutrunnerDutyRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<ReportShoutrunnerDutyResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/opening-publications/shoutrunner/duty-reports",
            accessToken,
            request,
            static payload => payload.AcceptedCount >= 0 &&
                              payload.DuplicateCount >= 0 &&
                              payload.ReportedAt != default,
            cancellationToken);

    public Task<ApiResult<DjViewResponse>> GetDjsAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DjViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/djs",
            accessToken,
            null,
            ValidateDjViewResponse,
            cancellationToken);

    public Task<ApiResult<DjSummary>> SaveDjAsync(
        Uri baseUri,
        string accessToken,
        long? djId,
        SaveDjRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DjSummary>(
            baseUri,
            djId is null ? HttpMethod.Post : HttpMethod.Put,
            djId is null ? "api/v1/djs" : $"api/v1/djs/{djId.Value}",
            accessToken,
            request,
            ValidateDjSummary,
            cancellationToken);

    public Task<ApiResult<ArchiveDjResponse>> ArchiveDjAsync(
        Uri baseUri,
        string accessToken,
        long djId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<ArchiveDjResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/djs/{djId}/archive",
            accessToken,
            null,
            static payload => payload.DjId > 0 && payload.ArchivedAt != default,
            cancellationToken);

    public Task<ApiResult<UpdateDjSettingsResponse>> UpdateDjSettingsAsync(
        Uri baseUri,
        string accessToken,
        UpdateDjSettingsRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UpdateDjSettingsResponse>(
            baseUri,
            HttpMethod.Put,
            "api/v1/djs/settings",
            accessToken,
            request,
            static payload => payload.DefaultHourlyRateGil >= 0 && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<DjCharacterLinkResponse>> LinkDjCharacterAsync(
        Uri baseUri,
        string accessToken,
        LinkDjCharacterRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DjCharacterLinkResponse>(
            baseUri,
            HttpMethod.Put,
            "api/v1/djs/character-link",
            accessToken,
            request,
            static payload => payload.CharacterId > 0,
            cancellationToken);

    public Task<ApiResult<DjBookingSummary>> SaveDjBookingAsync(
        Uri baseUri,
        string accessToken,
        long? bookingId,
        SaveDjBookingRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DjBookingSummary>(
            baseUri,
            bookingId is null ? HttpMethod.Post : HttpMethod.Put,
            bookingId is null ? "api/v1/djs/bookings" : $"api/v1/djs/bookings/{bookingId.Value}",
            accessToken,
            request,
            ValidateDjBookingSummary,
            cancellationToken);

    public Task<ApiResult<DeleteDjBookingResponse>> DeleteDjBookingAsync(
        Uri baseUri,
        string accessToken,
        long openingId,
        long bookingId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DeleteDjBookingResponse>(
            baseUri,
            HttpMethod.Delete,
            $"api/v1/djs/bookings/{bookingId}?openingId={openingId}",
            accessToken,
            null,
            static payload => payload.BookingId > 0 && payload.DeletedAt != default,
            cancellationToken);

    public Task<ApiResult<DjPaymentOperationResponse>> StartDjPaymentAsync(
        Uri baseUri,
        string accessToken,
        long bookingId,
        StartDjPaymentRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DjPaymentOperationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/djs/bookings/{bookingId}/payments/start",
            accessToken,
            request,
            ValidateDjPaymentResponse,
            cancellationToken);

    public Task<ApiResult<DjBalancePaymentResponse>> StartDjBalancePaymentAsync(
        Uri baseUri,
        string accessToken,
        StartDjBalancePaymentRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DjBalancePaymentResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/djs/payments/balance/start",
            accessToken,
            request,
            ValidateDjBalancePaymentResponse,
            cancellationToken);

    public Task<ApiResult<DjPaymentOperationResponse>> ConfirmDjPaymentAsync(
        Uri baseUri,
        string accessToken,
        long paymentId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DjPaymentOperationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/djs/payments/{paymentId}/confirm",
            accessToken,
            null,
            ValidateDjPaymentResponse,
            cancellationToken);

    public Task<ApiResult<DjPaymentOperationResponse>> CancelDjPaymentAsync(
        Uri baseUri,
        string accessToken,
        long paymentId,
        CancelDjPaymentRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<DjPaymentOperationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/djs/payments/{paymentId}/cancel",
            accessToken,
            request,
            ValidateDjPaymentResponse,
            cancellationToken);

    public Task<ApiResult<TimedMacroViewResponse>> GetTimedMacrosAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<TimedMacroViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/timed-macros",
            accessToken,
            null,
            ValidateTimedMacroViewResponse,
            cancellationToken);

    public Task<ApiResult<SaveTimedMacroResponse>> CreateTimedMacroAsync(
        Uri baseUri,
        string accessToken,
        CreateTimedMacroRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SaveTimedMacroResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/timed-macros",
            accessToken,
            request,
            static payload => payload.TimedMacroId > 0 && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<SaveTimedMacroResponse>> UpdateTimedMacroAsync(
        Uri baseUri,
        string accessToken,
        long timedMacroId,
        UpdateTimedMacroRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SaveTimedMacroResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/timed-macros/{timedMacroId}",
            accessToken,
            request,
            static payload => payload.TimedMacroId > 0 && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<ArchiveTimedMacroResponse>> ArchiveTimedMacroAsync(
        Uri baseUri,
        string accessToken,
        long timedMacroId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<ArchiveTimedMacroResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/timed-macros/{timedMacroId}/archive",
            accessToken,
            null,
            static payload => payload.TimedMacroId > 0 && payload.ArchivedAt != default,
            cancellationToken);

    public Task<ApiResult<RecordTimedMacroExecutionResponse>> RecordTimedMacroExecutionAsync(
        Uri baseUri,
        string accessToken,
        long timedMacroId,
        RecordTimedMacroExecutionRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<RecordTimedMacroExecutionResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/timed-macros/{timedMacroId}/executions",
            accessToken,
            request,
            static payload =>
                (payload.OpeningId is null || payload.OpeningId > 0) &&
                payload.TimedMacroId > 0 &&
                payload.LastExecutedAt != default &&
                payload.LastExecutedByUserId > 0 &&
                payload.ExecutionCount > 0 &&
                payload.NextDueAt > payload.LastExecutedAt,
            cancellationToken);

    public Task<ApiResult<VipArrivalContextResponse>> GetVipArrivalContextAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipArrivalContextResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/vip-arrivals",
            accessToken,
            null,
            ValidateVipArrivalContextResponse,
            cancellationToken);

    public Task<ApiResult<ObserveVipArrivalsResponse>> ObserveVipArrivalsAsync(
        Uri baseUri,
        string accessToken,
        ObserveVipArrivalsRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<ObserveVipArrivalsResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/vip-arrivals/observations",
            accessToken,
            request,
            static payload => payload.OpeningId > 0 && payload.ObservedCount >= 0 && payload.PendingCount >= 0,
            cancellationToken);

    public Task<ApiResult<RecordVipArrivalActionResponse>> RecordVipArrivalActionAsync(
        Uri baseUri,
        string accessToken,
        int vipPlayerId,
        RecordVipArrivalActionRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<RecordVipArrivalActionResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/vip-arrivals/players/{vipPlayerId}/actions",
            accessToken,
            request,
            static payload => payload.OpeningId > 0 && payload.VipPlayerId > 0 && !string.IsNullOrWhiteSpace(payload.ActionKey),
            cancellationToken);

    public Task<ApiResult<UpdateVenueMacroResponse>> UpdateVenueMacroAsync(
        Uri baseUri,
        string accessToken,
        string macroCode,
        UpdateVenueMacroRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UpdateVenueMacroResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/vip-arrivals/macros/{Uri.EscapeDataString(macroCode)}",
            accessToken,
            request,
            static payload => !string.IsNullOrWhiteSpace(payload.MacroCode) && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<VenueOpeningSummary>> StartTemporaryVenueOpeningAsync(
        Uri baseUri,
        string accessToken,
        StartTemporaryOpeningRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VenueOpeningSummary>(
            baseUri,
            HttpMethod.Post,
            "api/v1/vip-arrivals/openings/temporary",
            accessToken,
            request,
            ValidateVenueOpening,
            cancellationToken);

    public Task<ApiResult<CloseVenueOpeningResponse>> CloseVenueOpeningAsync(
        Uri baseUri,
        string accessToken,
        long openingId,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CloseVenueOpeningResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/vip-arrivals/openings/{openingId}/close",
            accessToken,
            null,
            static payload => payload.OpeningId > 0 && payload.ClosedAt != default,
            cancellationToken);

    public Task<ApiResult<GreeterContextResponse>> GetGreeterContextAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<GreeterContextResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/greeter",
            accessToken,
            null,
            ValidateGreeterContextResponse,
            cancellationToken);

    public Task<ApiResult<ObserveGreeterArrivalsResponse>> ObserveGreeterArrivalsAsync(
        Uri baseUri,
        string accessToken,
        ObserveGreeterArrivalsRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<ObserveGreeterArrivalsResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/greeter/observations",
            accessToken,
            request,
            static payload => payload.OpeningId > 0 && payload.ObservedCount >= 0 && payload.PendingCount >= 0,
            cancellationToken);

    public Task<ApiResult<RecordGreeterActionResponse>> RecordGreeterActionAsync(
        Uri baseUri,
        string accessToken,
        RecordGreeterActionRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<RecordGreeterActionResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/greeter/actions",
            accessToken,
            request,
            static payload =>
                payload.OpeningId > 0 &&
                !string.IsNullOrWhiteSpace(payload.CharacterName) &&
                !string.IsNullOrWhiteSpace(payload.WorldName) &&
                !string.IsNullOrWhiteSpace(payload.ActionKey),
            cancellationToken);

    public Task<ApiResult<UpdateGreeterMacroResponse>> UpdateGreeterMacroAsync(
        Uri baseUri,
        string accessToken,
        string macroCode,
        UpdateGreeterMacroRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UpdateGreeterMacroResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/greeter/macros/{Uri.EscapeDataString(macroCode)}",
            accessToken,
            request,
            static payload => !string.IsNullOrWhiteSpace(payload.MacroCode) && payload.UpdatedAt != default,
            cancellationToken);

    public Task<ApiResult<VipPerkManagementViewResponse>> GetVipPerksAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipPerkManagementViewResponse>(
            baseUri, HttpMethod.Get, "api/v1/vip-perks", accessToken, null,
            ValidateVipPerkViewResponse, cancellationToken);

    public Task<ApiResult<VipPerkOperationResponse>> CreateVipPerkAsync(
        Uri baseUri, string accessToken, CreateVipPerkRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipPerkOperationResponse>(
            baseUri, HttpMethod.Post, "api/v1/vip-perks", accessToken, request,
            static payload => payload.PerkId > 0, cancellationToken);

    public Task<ApiResult<VipPerkOperationResponse>> UpdateVipPerkAsync(
        Uri baseUri, string accessToken, int perkId, UpdateVipPerkRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipPerkOperationResponse>(
            baseUri, HttpMethod.Put, $"api/v1/vip-perks/{perkId}", accessToken, request,
            static payload => payload.PerkId > 0, cancellationToken);

    public Task<ApiResult<VipPackagePerkOperationResponse>> SetVipPackagePerkAsync(
        Uri baseUri, string accessToken, int packageId, int perkId, SetVipPackagePerkRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<VipPackagePerkOperationResponse>(
            baseUri, HttpMethod.Put, $"api/v1/vip-perks/packages/{packageId}/perks/{perkId}", accessToken, request,
            static _ => true, cancellationToken);

    public Task<ApiResult<RedeemVipPerkResponse>> RedeemVipPerkAsync(
        Uri baseUri, string accessToken, RedeemVipPerkRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<RedeemVipPerkResponse>(
            baseUri, HttpMethod.Post, "api/v1/vip-perks/redemptions", accessToken, request,
            static payload => payload.RedemptionId > 0 && payload.PerkId > 0 && payload.RedeemedAt != default,
            cancellationToken);

    public Task<ApiResult<UndoVipPerkRedemptionResponse>> UndoVipPerkRedemptionAsync(
        Uri baseUri, string accessToken, long redemptionId, UndoVipPerkRedemptionRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UndoVipPerkRedemptionResponse>(
            baseUri, HttpMethod.Post, $"api/v1/vip-perks/redemptions/{redemptionId}/undo", accessToken, request,
            static payload => payload.RedemptionId > 0 && payload.UndoneAt != default, cancellationToken);

    public Task<ApiResult<PhotoshootManagementViewResponse>> GetPhotoshootsAsync(
        Uri baseUri, string accessToken, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<PhotoshootManagementViewResponse>(
            baseUri, HttpMethod.Get, "api/v1/photoshoots", accessToken, null,
            ValidatePhotoshootViewResponse, cancellationToken);

    public Task<ApiResult<PhotoshootPackageOperationResponse>> CreatePhotoshootPackageAsync(
        Uri baseUri, string accessToken, CreatePhotoshootPackageRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<PhotoshootPackageOperationResponse>(
            baseUri, HttpMethod.Post, "api/v1/photoshoots/packages", accessToken, request,
            static payload => payload.PackageId > 0, cancellationToken);

    public Task<ApiResult<PhotoshootPackageOperationResponse>> UpdatePhotoshootPackageAsync(
        Uri baseUri, string accessToken, int packageId, UpdatePhotoshootPackageRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<PhotoshootPackageOperationResponse>(
            baseUri, HttpMethod.Put, $"api/v1/photoshoots/packages/{packageId}", accessToken, request,
            static payload => payload.PackageId > 0, cancellationToken);

    public Task<ApiResult<UpdatePhotoshootSettingsResponse>> UpdatePhotoshootSettingsAsync(
        Uri baseUri, string accessToken, UpdatePhotoshootSettingsRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UpdatePhotoshootSettingsResponse>(
            baseUri, HttpMethod.Put, "api/v1/photoshoots/settings", accessToken, request,
            static payload => payload.SellerPercentage is >= 0m and <= 100m, cancellationToken);

    public Task<ApiResult<SellPhotoshootResponse>> SellPhotoshootAsync(
        Uri baseUri, string accessToken, SellPhotoshootRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SellPhotoshootResponse>(
            baseUri, HttpMethod.Post, "api/v1/photoshoots/sales", accessToken, request,
            static payload => payload.SaleId > 0 &&
                              payload.PackageId > 0 &&
                              payload.TotalGil >= 0 &&
                              payload.SellerPercentage is >= 0m and <= 100m &&
                              payload.SellerShareGil >= 0 &&
                              payload.VenueShareGil >= 0 &&
                              payload.SellerShareGil + payload.VenueShareGil == payload.TotalGil &&
                              payload.SoldAt != default,
            cancellationToken);

    public Task<ApiResult<PhotoshootSalePaymentStatusResponse>> SetPhotoshootSalePaymentStatusAsync(
        Uri baseUri,
        string accessToken,
        long saleId,
        SetPhotoshootSalePaymentStatusRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<PhotoshootSalePaymentStatusResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/photoshoots/sales/{saleId}/payment-status",
            accessToken,
            request,
            static payload => payload.SaleId > 0,
            cancellationToken);

    public Task<ApiResult<PhotoshootSaleCancellationResponse>> CancelPhotoshootSaleAsync(
        Uri baseUri,
        string accessToken,
        long saleId,
        CancelPhotoshootSaleRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<PhotoshootSaleCancellationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/photoshoots/sales/{saleId}/cancel",
            accessToken,
            request,
            static payload => payload.SaleId > 0 && payload.VoidedAt != default,
            cancellationToken);

    public Task<ApiResult<OtherSalesManagementViewResponse>> GetOtherSalesAsync(
        Uri baseUri, string accessToken, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherSalesManagementViewResponse>(
            baseUri, HttpMethod.Get, "api/v1/other-sales", accessToken, null,
            ValidateOtherSalesViewResponse, cancellationToken);

    public Task<ApiResult<OtherSaleItemOperationResponse>> CreateOtherSaleItemAsync(
        Uri baseUri, string accessToken, CreateOtherSaleItemRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherSaleItemOperationResponse>(
            baseUri, HttpMethod.Post, "api/v1/other-sales/items", accessToken, request,
            static payload => payload.ItemId > 0, cancellationToken);

    public Task<ApiResult<OtherSaleItemOperationResponse>> UpdateOtherSaleItemAsync(
        Uri baseUri, string accessToken, int itemId, UpdateOtherSaleItemRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherSaleItemOperationResponse>(
            baseUri, HttpMethod.Put, $"api/v1/other-sales/items/{itemId}", accessToken, request,
            static payload => payload.ItemId > 0, cancellationToken);

    public Task<ApiResult<UpdateOtherSaleSellerPercentageResponse>> UpdateOtherSaleSellerPercentageAsync(
        Uri baseUri, string accessToken, int itemId, UpdateOtherSaleSellerPercentageRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UpdateOtherSaleSellerPercentageResponse>(
            baseUri, HttpMethod.Put, $"api/v1/other-sales/items/{itemId}/seller-percentage", accessToken, request,
            static payload => payload.ItemId > 0 && payload.SellerPercentage is >= 0m and <= 100m,
            cancellationToken);

    public Task<ApiResult<SellOtherSaleResponse>> SellOtherSaleAsync(
        Uri baseUri, string accessToken, SellOtherSaleRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SellOtherSaleResponse>(
            baseUri, HttpMethod.Post, "api/v1/other-sales/sales", accessToken, request,
            static payload => payload.SaleId > 0 && payload.ItemId > 0 && payload.Quantity > 0 &&
                              payload.TotalGil >= 0 && payload.SellerPercentage is >= 0m and <= 100m &&
                              payload.SellerShareGil >= 0 && payload.VenueShareGil >= 0 &&
                              payload.SellerShareGil + payload.VenueShareGil == payload.TotalGil &&
                              payload.SoldAt != default,
            cancellationToken);

    public Task<ApiResult<OtherSalePaymentStatusResponse>> SetOtherSalePaymentStatusAsync(
        Uri baseUri, string accessToken, long saleId, SetOtherSalePaymentStatusRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherSalePaymentStatusResponse>(
            baseUri, HttpMethod.Put, $"api/v1/other-sales/sales/{saleId}/payment-status", accessToken, request,
            static payload => payload.SaleId > 0, cancellationToken);

    public Task<ApiResult<OtherSaleCancellationResponse>> CancelOtherSaleAsync(
        Uri baseUri, string accessToken, long saleId, CancelOtherSaleRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherSaleCancellationResponse>(
            baseUri, HttpMethod.Post, $"api/v1/other-sales/sales/{saleId}/cancel", accessToken, request,
            static payload => payload.SaleId > 0 && payload.VoidedAt != default, cancellationToken);

    public Task<ApiResult<OtherGamesManagementViewResponse>> GetOtherGamesAsync(
        Uri baseUri, string accessToken, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherGamesManagementViewResponse>(
            baseUri, HttpMethod.Get, "api/v1/other-games", accessToken, null,
            ValidateOtherGamesViewResponse, cancellationToken);

    public Task<ApiResult<OtherGameItemOperationResponse>> CreateOtherGameItemAsync(
        Uri baseUri, string accessToken, CreateOtherGameItemRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherGameItemOperationResponse>(
            baseUri, HttpMethod.Post, "api/v1/other-games/items", accessToken, request,
            static payload => payload.ItemId > 0, cancellationToken);

    public Task<ApiResult<OtherGameItemOperationResponse>> UpdateOtherGameItemAsync(
        Uri baseUri, string accessToken, int itemId, UpdateOtherGameItemRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherGameItemOperationResponse>(
            baseUri, HttpMethod.Put, $"api/v1/other-games/items/{itemId}", accessToken, request,
            static payload => payload.ItemId > 0, cancellationToken);

    public Task<ApiResult<UpdateOtherGameSellerPercentageResponse>> UpdateOtherGameSellerPercentageAsync(
        Uri baseUri, string accessToken, int itemId, UpdateOtherGameSellerPercentageRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UpdateOtherGameSellerPercentageResponse>(
            baseUri, HttpMethod.Put, $"api/v1/other-games/items/{itemId}/seller-percentage", accessToken, request,
            static payload => payload.ItemId > 0 && payload.SellerPercentage is >= 0m and <= 100m, cancellationToken);

    public Task<ApiResult<SellOtherGameResponse>> SellOtherGameAsync(
        Uri baseUri, string accessToken, SellOtherGameRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SellOtherGameResponse>(
            baseUri, HttpMethod.Post, "api/v1/other-games/sales", accessToken, request,
            static payload => payload.SaleId > 0 && payload.ItemId > 0 && payload.Quantity > 0 &&
                              payload.TotalGil >= 0 && payload.SellerPercentage is >= 0m and <= 100m &&
                              payload.SellerShareGil >= 0 && payload.VenueShareGil >= 0 &&
                              payload.SellerShareGil + payload.VenueShareGil == payload.TotalGil &&
                              payload.SoldAt != default, cancellationToken);

    public Task<ApiResult<OtherGameOutcomeResponse>> SetOtherGameOutcomeAsync(
        Uri baseUri, string accessToken, long saleId, SetOtherGameOutcomeRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherGameOutcomeResponse>(
            baseUri, HttpMethod.Put, $"api/v1/other-games/sales/{saleId}/outcome", accessToken, request,
            static payload => payload.SaleId > 0 &&
                              ((payload.OutcomeStatus == "no_win" && payload.WinAmountGil == 0) ||
                               (payload.OutcomeStatus == "win" && payload.WinAmountGil > 0)) &&
                              payload.OutcomeRecordedAt != default, cancellationToken);

    public Task<ApiResult<OtherGameSettlementStatusResponse>> SetOtherGameSettlementStatusAsync(
        Uri baseUri, string accessToken, long saleId, SetOtherGameSettlementStatusRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherGameSettlementStatusResponse>(
            baseUri, HttpMethod.Put, $"api/v1/other-games/sales/{saleId}/settlement-status", accessToken, request,
            static payload => payload.SaleId > 0, cancellationToken);

    public Task<ApiResult<OtherGameSaleCancellationResponse>> CancelOtherGameSaleAsync(
        Uri baseUri, string accessToken, long saleId, CancelOtherGameSaleRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<OtherGameSaleCancellationResponse>(
            baseUri, HttpMethod.Post, $"api/v1/other-games/sales/{saleId}/cancel", accessToken, request,
            static payload => payload.SaleId > 0 && payload.VoidedAt != default, cancellationToken);

    public Task<ApiResult<BarManagementViewResponse>> GetBarAsync(
        Uri baseUri, string accessToken, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<BarManagementViewResponse>(
            baseUri, HttpMethod.Get, "api/v1/bar", accessToken, null,
            ValidateBarViewResponse, cancellationToken);

    public Task<ApiResult<BarBuyoutPackageOperationResponse>> CreateBarBuyoutPackageAsync(
        Uri baseUri, string accessToken, CreateBarBuyoutPackageRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<BarBuyoutPackageOperationResponse>(
            baseUri, HttpMethod.Post, "api/v1/bar/buyout-packages", accessToken, request,
            static payload => payload.PackageId > 0, cancellationToken);

    public Task<ApiResult<BarBuyoutPackageOperationResponse>> UpdateBarBuyoutPackageAsync(
        Uri baseUri, string accessToken, long packageId, UpdateBarBuyoutPackageRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<BarBuyoutPackageOperationResponse>(
            baseUri, HttpMethod.Put, $"api/v1/bar/buyout-packages/{packageId}", accessToken, request,
            static payload => payload.PackageId > 0, cancellationToken);

    public Task<ApiResult<UpdateBarSettingsResponse>> UpdateBarSettingsAsync(
        Uri baseUri, string accessToken, UpdateBarSettingsRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UpdateBarSettingsResponse>(
            baseUri, HttpMethod.Put, "api/v1/bar/settings", accessToken, request,
            static payload => payload.BuyoutSellerPercentage is >= 0m and <= 100m &&
                              payload.GambaTicketPriceGil > 0 &&
                              payload.GambaHousePercentage is >= 0m and <= 100m,
            cancellationToken);

    public Task<ApiResult<SellBarBuyoutResponse>> SellBarBuyoutAsync(
        Uri baseUri, string accessToken, SellBarBuyoutRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SellBarBuyoutResponse>(
            baseUri, HttpMethod.Post, "api/v1/bar/buyouts", accessToken, request,
            static payload => payload.SaleId > 0 && payload.EndsAt > payload.StartsAt, cancellationToken);

    public Task<ApiResult<BarSalePaymentStatusResponse>> SetBarBuyoutPaymentStatusAsync(
        Uri baseUri, string accessToken, long saleId, SetBarSalePaymentStatusRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<BarSalePaymentStatusResponse>(
            baseUri, HttpMethod.Put, $"api/v1/bar/buyouts/{saleId}/payment-status", accessToken, request,
            static payload => payload.SaleId > 0, cancellationToken);

    public Task<ApiResult<BarSaleCancellationResponse>> CancelBarBuyoutAsync(
        Uri baseUri, string accessToken, long saleId, CancelBarSaleRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<BarSaleCancellationResponse>(
            baseUri, HttpMethod.Post, $"api/v1/bar/buyouts/{saleId}/cancel", accessToken, request,
            static payload => payload.SaleId > 0 && payload.VoidedAt != default, cancellationToken);

    public Task<ApiResult<StartGambaGameResponse>> StartGambaGameAsync(
        Uri baseUri, string accessToken, StartGambaGameRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StartGambaGameResponse>(
            baseUri, HttpMethod.Post, "api/v1/bar/gamba-games", accessToken, request,
            static payload => payload.GameId > 0 && payload.CurrentJackpotGil >= 0 && payload.StartedAt != default, cancellationToken);

    public Task<ApiResult<SellGambaTicketsResponse>> SellGambaTicketsAsync(
        Uri baseUri, string accessToken, SellGambaTicketsRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SellGambaTicketsResponse>(
            baseUri, HttpMethod.Post, "api/v1/bar/gamba-tickets", accessToken, request,
            static payload => payload.SaleId > 0 && payload.GameId > 0 && payload.Quantity > 0 &&
                              payload.GrossGil >= 0 && payload.HouseShareGil >= 0 &&
                              payload.JackpotContributionGil >= 0 && payload.SoldAt != default, cancellationToken);

    public Task<ApiResult<BarSalePaymentStatusResponse>> SetGambaTicketPaymentStatusAsync(
        Uri baseUri, string accessToken, long saleId, SetBarSalePaymentStatusRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<BarSalePaymentStatusResponse>(
            baseUri, HttpMethod.Put, $"api/v1/bar/gamba-tickets/{saleId}/payment-status", accessToken, request,
            static payload => payload.SaleId > 0, cancellationToken);

    public Task<ApiResult<BarSaleCancellationResponse>> CancelGambaTicketSaleAsync(
        Uri baseUri, string accessToken, long saleId, CancelBarSaleRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<BarSaleCancellationResponse>(
            baseUri, HttpMethod.Post, $"api/v1/bar/gamba-tickets/{saleId}/cancel", accessToken, request,
            static payload => payload.SaleId > 0 && payload.VoidedAt != default, cancellationToken);

    public Task<ApiResult<CompleteGambaGameResponse>> CompleteGambaGameAsync(
        Uri baseUri, string accessToken, long gameId, CompleteGambaGameRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CompleteGambaGameResponse>(
            baseUri, HttpMethod.Post, $"api/v1/bar/gamba-games/{gameId}/complete", accessToken, request,
            static payload => payload.GameId > 0 && payload.FinalJackpotGil >= 0 &&
                              !string.IsNullOrWhiteSpace(payload.WinnerCharacterName) &&
                              !string.IsNullOrWhiteSpace(payload.WinnerWorldName) && payload.WonAt != default,
            cancellationToken);

    public Task<ApiResult<CancelGambaGameResponse>> CancelGambaGameAsync(
        Uri baseUri, string accessToken, long gameId, CancelGambaGameRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CancelGambaGameResponse>(
            baseUri, HttpMethod.Post, $"api/v1/bar/gamba-games/{gameId}/cancel", accessToken, request,
            static payload => payload.GameId > 0 && payload.CancelledAt != default && payload.CancelledTicketSaleCount >= 0,
            cancellationToken);

    public Task<ApiResult<FinanceViewResponse>> GetFinanceAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<FinanceViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/finance",
            accessToken,
            null,
            ValidateFinanceViewResponse,
            cancellationToken);

    public Task<ApiResult<CreateVipSettlementResponse>> CreateVipSettlementAsync(
        Uri baseUri,
        string accessToken,
        CreateVipSettlementRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreateVipSettlementResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/finance/settlements/vip",
            accessToken,
            request,
            static payload => payload.SettlementId > 0 &&
                              payload.AmountGil > 0 &&
                              payload.TargetUserId > 0 &&
                              payload.TargetCharacterId > 0 &&
                              !string.IsNullOrWhiteSpace(payload.TargetCharacterName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetWorldName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetUserDisplayName) &&
                              payload.CreatedAt != default,
            cancellationToken);

    public Task<ApiResult<CreatePhotoshootSettlementResponse>> CreatePhotoshootSettlementAsync(
        Uri baseUri,
        string accessToken,
        CreatePhotoshootSettlementRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreatePhotoshootSettlementResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/finance/settlements/photoshoots",
            accessToken,
            request,
            static payload => payload.SettlementId > 0 &&
                              payload.AmountGil > 0 &&
                              payload.TargetUserId > 0 &&
                              payload.TargetCharacterId > 0 &&
                              !string.IsNullOrWhiteSpace(payload.TargetCharacterName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetWorldName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetUserDisplayName) &&
                              payload.CreatedAt != default,
            cancellationToken);

    public Task<ApiResult<CreateOtherSalesSettlementResponse>> CreateOtherSalesSettlementAsync(
        Uri baseUri,
        string accessToken,
        CreateOtherSalesSettlementRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreateOtherSalesSettlementResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/finance/settlements/other-sales",
            accessToken,
            request,
            static payload => payload.SettlementId > 0 &&
                              payload.AmountGil > 0 &&
                              payload.TargetUserId > 0 &&
                              payload.TargetCharacterId > 0 &&
                              !string.IsNullOrWhiteSpace(payload.TargetCharacterName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetWorldName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetUserDisplayName) &&
                              payload.CreatedAt != default,
            cancellationToken);

    public Task<ApiResult<CreateOtherGamesSettlementResponse>> CreateOtherGamesSettlementAsync(
        Uri baseUri,
        string accessToken,
        CreateOtherGamesSettlementRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreateOtherGamesSettlementResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/finance/settlements/other-games",
            accessToken,
            request,
            static payload => payload.SettlementId > 0 &&
                              ((payload.AmountGil > 0 && payload.TradeDirection == "seller_to_venue") ||
                               (payload.AmountGil == 0 && payload.TradeDirection == "none")) &&
                              payload.TargetUserId > 0 &&
                              payload.TargetCharacterId > 0 &&
                              !string.IsNullOrWhiteSpace(payload.TargetCharacterName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetWorldName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetUserDisplayName) &&
                              payload.CreatedAt != default,
            cancellationToken);

    public Task<ApiResult<CreateOtherGamesSettlementResponse>> CreateOtherGamesPayoutAsync(
        Uri baseUri,
        string accessToken,
        CreateOtherGamesPayoutRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreateOtherGamesSettlementResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/finance/settlements/other-games/payout",
            accessToken,
            request,
            static payload => payload.SettlementId > 0 &&
                              payload.AmountGil < 0 &&
                              payload.TradeDirection == "venue_to_seller" &&
                              payload.TargetUserId > 0 &&
                              payload.TargetCharacterId > 0 &&
                              !string.IsNullOrWhiteSpace(payload.TargetCharacterName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetWorldName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetUserDisplayName) &&
                              payload.CreatedAt != default,
            cancellationToken);

    public Task<ApiResult<CreateBarSettlementResponse>> CreateBarSettlementAsync(
        Uri baseUri,
        string accessToken,
        CreateBarSettlementRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreateBarSettlementResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/finance/settlements/bar",
            accessToken,
            request,
            static payload => payload.SettlementId > 0 &&
                              payload.AmountGil > 0 &&
                              payload.TargetUserId > 0 &&
                              payload.TargetCharacterId > 0 &&
                              !string.IsNullOrWhiteSpace(payload.TargetCharacterName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetWorldName) &&
                              !string.IsNullOrWhiteSpace(payload.TargetUserDisplayName) &&
                              payload.CreatedAt != default,
            cancellationToken);

    public Task<ApiResult<RespondSettlementResponse>> RespondSettlementAsync(
        Uri baseUri,
        string accessToken,
        long settlementId,
        RespondSettlementRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<RespondSettlementResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/finance/settlements/{settlementId}/response",
            accessToken,
            request,
            static payload => payload.SettlementId > 0 &&
                              !string.IsNullOrWhiteSpace(payload.Status) &&
                              payload.RespondedAt != default,
            cancellationToken);

    public Task<ApiResult<NotificationPollResponse>> PollNotificationsAsync(
        Uri baseUri,
        string accessToken,
        int maxResults,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<NotificationPollResponse>(
            baseUri,
            HttpMethod.Get,
            $"api/v1/notifications?maxResults={maxResults}",
            accessToken,
            null,
            ValidateNotificationPollResponse,
            cancellationToken);

    public Task<ApiResult<MarkNotificationSeenResponse>> MarkNotificationSeenAsync(
        Uri baseUri,
        string accessToken,
        long notificationId,
        bool dismissed,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<MarkNotificationSeenResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/notifications/{notificationId}/seen",
            accessToken,
            new MarkNotificationSeenRequest(dismissed),
            static payload => payload.NotificationId > 0 && payload.SeenAt != default,
            cancellationToken);

    private static bool ValidateVipManagementViewResponse(VipManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.PersonalUnpaidGil >= 0 &&
        payload.PersonalPendingSettlementGil >= 0 &&
        payload.Packages is not null &&
        payload.Players is not null &&
        payload.Characters is not null &&
        payload.Subscriptions is not null &&
        payload.DiscordRoles is not null &&
        payload.Packages.All(static package =>
            package.PackageId > 0 &&
            !string.IsNullOrWhiteSpace(package.Name) &&
            package.Tier > 0 &&
            package.PriceGil >= 0) &&
        payload.Players.All(static player =>
            player.VipPlayerId > 0 &&
            ((string.IsNullOrWhiteSpace(player.DisplayCharacterName) &&
              string.IsNullOrWhiteSpace(player.DisplayWorldName) &&
              (!string.IsNullOrWhiteSpace(player.DiscordUsername) || player.DiscordId is not null)) ||
             (!string.IsNullOrWhiteSpace(player.DisplayCharacterName) &&
              !string.IsNullOrWhiteSpace(player.DisplayWorldName)))) &&
        payload.Characters.All(static character =>
            character.CharacterId > 0 &&
            character.VipPlayerId > 0 &&
            !string.IsNullOrWhiteSpace(character.CharacterName) &&
            !string.IsNullOrWhiteSpace(character.WorldName)) &&
        payload.Subscriptions.All(static subscription =>
            subscription.SubscriptionId > 0 &&
            subscription.VipPlayerId > 0 &&
            subscription.PackageId > 0 &&
            subscription.PurchasePriceGil >= 0 &&
            !string.IsNullOrWhiteSpace(subscription.PackageName) &&
            !string.IsNullOrWhiteSpace(subscription.SellerDisplayName) &&
            !string.IsNullOrWhiteSpace(subscription.SourceType)) &&
        payload.DiscordRoles.All(static role =>
            role.RoleId > 0 &&
            !string.IsNullOrWhiteSpace(role.Name) &&
            role.Position >= 0);

    private static bool ValidateVipPerkViewResponse(VipPerkManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Perks is not null && payload.PackageAssignments is not null &&
        payload.Availability is not null && payload.Redemptions is not null &&
        payload.Perks.All(static value => value.PerkId > 0 && !string.IsNullOrWhiteSpace(value.Name)) &&
        payload.PackageAssignments.All(static value => value.PackagePerkId > 0 && value.PackageId > 0 && value.PerkId > 0) &&
        payload.Availability.All(static value => value.CharacterId > 0 && value.SubscriptionId > 0 && value.PerkId > 0) &&
        payload.Redemptions.All(static value => value.RedemptionId > 0 && value.PerkId > 0);

    private static bool ValidateBarViewResponse(BarManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Settings is not null &&
        payload.Settings.BuyoutSellerPercentage is >= 0m and <= 100m &&
        payload.Settings.GambaTicketPriceGil > 0 &&
        payload.Settings.GambaHousePercentage is >= 0m and <= 100m &&
        payload.PersonalUnpaidGil >= 0 &&
        payload.PersonalPendingGil >= 0 &&
        payload.PersonalAvailableGil >= 0 &&
        payload.PersonalAvailableGil <= payload.PersonalUnpaidGil &&
        payload.SuggestedStartingJackpotGil >= 0 &&
        payload.BuyoutPackages is not null &&
        payload.BuyoutSales is not null &&
        payload.GambaTicketSales is not null &&
        payload.GambaGameHistory is not null;

    public Task<ApiResult<StaffManagementViewResponse>> GetStaffAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffManagementViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/staff",
            accessToken,
            null,
            ValidateStaffViewResponse,
            cancellationToken);

    public Task<ApiResult<StaffJobOperationResponse>> CreateStaffJobAsync(
        Uri baseUri,
        string accessToken,
        SaveStaffJobRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffJobOperationResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/staff/jobs",
            accessToken,
            request,
            static payload => payload.JobDefinitionId > 0,
            cancellationToken);

    public Task<ApiResult<StaffJobOperationResponse>> UpdateStaffJobAsync(
        Uri baseUri,
        string accessToken,
        long jobDefinitionId,
        SaveStaffJobRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffJobOperationResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/staff/jobs/{jobDefinitionId}",
            accessToken,
            request,
            static payload => payload.JobDefinitionId > 0,
            cancellationToken);

    public Task<ApiResult<StaffMemberOperationResponse>> CreateStaffMemberAsync(
        Uri baseUri,
        string accessToken,
        SaveStaffMemberRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffMemberOperationResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/staff/members",
            accessToken,
            request,
            static payload => payload.StaffMemberId > 0,
            cancellationToken);

    public Task<ApiResult<StaffMemberOperationResponse>> UpdateStaffMemberAsync(
        Uri baseUri,
        string accessToken,
        long staffMemberId,
        SaveStaffMemberRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffMemberOperationResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/staff/members/{staffMemberId}",
            accessToken,
            request,
            static payload => payload.StaffMemberId > 0,
            cancellationToken);

    public Task<ApiResult<StaffLifecycleTaskOperationResponse>> CreateStaffLifecycleTaskAsync(
        Uri baseUri,
        string accessToken,
        SaveStaffLifecycleTaskRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffLifecycleTaskOperationResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/staff/lifecycle-tasks",
            accessToken,
            request,
            static payload => payload.TaskDefinitionId > 0,
            cancellationToken);

    public Task<ApiResult<StaffLifecycleTaskOperationResponse>> UpdateStaffLifecycleTaskAsync(
        Uri baseUri,
        string accessToken,
        long taskDefinitionId,
        SaveStaffLifecycleTaskRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffLifecycleTaskOperationResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/staff/lifecycle-tasks/{taskDefinitionId}",
            accessToken,
            request,
            static payload => payload.TaskDefinitionId > 0,
            cancellationToken);

    public Task<ApiResult<StaffLifecycleProgressResponse>> SaveStaffLifecycleProgressAsync(
        Uri baseUri,
        string accessToken,
        long staffMemberId,
        SaveStaffLifecycleProgressRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffLifecycleProgressResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/staff/members/{staffMemberId}/lifecycle-progress",
            accessToken,
            request,
            static payload =>
                payload.StaffMemberId > 0 &&
                payload.UpdatedCount >= 0,
            cancellationToken);

    public Task<ApiResult<StaffCharacterLinkResponse>> LinkStaffCharacterAsync(
        Uri baseUri,
        string accessToken,
        LinkStaffCharacterRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffCharacterLinkResponse>(
            baseUri,
            HttpMethod.Put,
            "api/v1/staff/character-link",
            accessToken,
            request,
            static payload => payload.CharacterId > 0,
            cancellationToken);

    public Task<ApiResult<StaffTimeEntryOperationResponse>> CreateStaffTimeEntryAsync(
        Uri baseUri,
        string accessToken,
        SaveStaffTimeEntryRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffTimeEntryOperationResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/staff/time-entries",
            accessToken,
            request,
            static payload =>
                payload.TimeEntryId > 0 &&
                !string.IsNullOrWhiteSpace(payload.Status),
            cancellationToken);

    public Task<ApiResult<StaffTimeEntryOperationResponse>> UpdateStaffTimeEntryAsync(
        Uri baseUri,
        string accessToken,
        long timeEntryId,
        SaveStaffTimeEntryRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffTimeEntryOperationResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/staff/time-entries/{timeEntryId}",
            accessToken,
            request,
            static payload =>
                payload.TimeEntryId > 0 &&
                !string.IsNullOrWhiteSpace(payload.Status),
            cancellationToken);

    public Task<ApiResult<StaffTimeEntryCancellationResponse>> CancelStaffTimeEntryAsync(
        Uri baseUri,
        string accessToken,
        long timeEntryId,
        CancelStaffTimeEntryRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffTimeEntryCancellationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/staff/time-entries/{timeEntryId}/cancel",
            accessToken,
            request,
            static payload =>
                payload.TimeEntryId > 0 &&
                payload.CancelledAt != default,
            cancellationToken);

    public Task<ApiResult<ObserveStaffFirstSeenResponse>> ObserveStaffFirstSeenAsync(
        Uri baseUri,
        string accessToken,
        ObserveStaffFirstSeenRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<ObserveStaffFirstSeenResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/staff/first-seen",
            accessToken,
            request,
            static payload =>
                payload.OpeningId > 0 &&
                payload.MatchedCount >= 0 &&
                payload.InsertedCount >= 0 &&
                payload.InsertedCount <= payload.MatchedCount,
            cancellationToken);

    public Task<ApiResult<StaffAbsenceOperationResponse>> SetStaffAbsenceAsync(
        Uri baseUri,
        string accessToken,
        long openingId,
        SetStaffAbsenceRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffAbsenceOperationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/staff/openings/{openingId}/absences",
            accessToken,
            request,
            static payload =>
                payload.AbsenceId > 0 &&
                payload.OpeningId > 0 &&
                payload.StaffMemberId > 0 &&
                payload.RecordedAt != default &&
                payload.ReasonCode is "planned" or "unplanned",
            cancellationToken);

    public Task<ApiResult<StaffAbsenceCancellationResponse>> CancelStaffAbsenceAsync(
        Uri baseUri,
        string accessToken,
        long absenceId,
        CancelStaffAbsenceRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffAbsenceCancellationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/staff/absences/{absenceId}/cancel",
            accessToken,
            request,
            static payload => payload.AbsenceId > 0 && payload.CancelledAt != default,
            cancellationToken);

    public Task<ApiResult<StaffPayoutResponse>> CreateStaffPayoutAsync(
        Uri baseUri,
        string accessToken,
        CreateStaffPayoutRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<StaffPayoutResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/staff/payouts",
            accessToken,
            request,
            ValidateFinancialTransactionResponse,
            cancellationToken);

    public Task<ApiResult<CourtManagementViewResponse>> GetCourtAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtManagementViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/court",
            accessToken,
            null,
            ValidateCourtViewResponse,
            cancellationToken);

    public Task<ApiResult<UpdateCourtSettingsResponse>> UpdateCourtSettingsAsync(
        Uri baseUri,
        string accessToken,
        UpdateCourtSettingsRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<UpdateCourtSettingsResponse>(
            baseUri,
            HttpMethod.Put,
            "api/v1/court/settings",
            accessToken,
            request,
            static payload => payload.CourtKeepPercentage is >= 0m and <= 100m,
            cancellationToken);

    public Task<ApiResult<CourtStaffSettlementPreviewResponse>> PreviewCourtStaffSettlementAsync(
        Uri baseUri,
        string accessToken,
        CreateCourtStaffSettlementRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtStaffSettlementPreviewResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/court/settlements/staff/preview",
            accessToken,
            request,
            static payload =>
                !string.IsNullOrWhiteSpace(payload.StaffDisplayName) &&
                !string.IsNullOrWhiteSpace(payload.StaffCharacterName) &&
                !string.IsNullOrWhiteSpace(payload.StaffWorldName) &&
                payload.GrossSalesGil >= 0 &&
                payload.CourtRetainedGil >= 0 &&
                payload.VenueShareGil >= 0 &&
                payload.SalaryGil >= 0 &&
                payload.SalaryDeductionGil >= 0 &&
                payload.TradeAmountGil >= 0 &&
                payload.SaleCount >= 0 &&
                payload.TimeEntryCount >= 0 &&
                payload.AdjustmentCount >= 0,
            cancellationToken);

    public Task<ApiResult<CourtOfferOperationResponse>> CreateCourtOfferAsync(
        Uri baseUri,
        string accessToken,
        SaveCourtOfferRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtOfferOperationResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/court/offers",
            accessToken,
            request,
            static payload => payload.OfferId > 0,
            cancellationToken);

    public Task<ApiResult<CourtOfferOperationResponse>> UpdateCourtOfferAsync(
        Uri baseUri,
        string accessToken,
        long offerId,
        SaveCourtOfferRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtOfferOperationResponse>(
            baseUri,
            HttpMethod.Put,
            $"api/v1/court/offers/{offerId}",
            accessToken,
            request,
            static payload => payload.OfferId > 0,
            cancellationToken);

    public Task<ApiResult<SellCourtServiceResponse>> SellCourtServiceAsync(
        Uri baseUri,
        string accessToken,
        SellCourtServiceRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SellCourtServiceResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/court/sales",
            accessToken,
            request,
            static payload =>
                payload.SaleId > 0 &&
                payload.OfferId > 0 &&
                payload.Quantity is >= 1 and <= 100 &&
                payload.UnitDurationMinutes > 0 &&
                payload.TotalDurationMinutes >= payload.UnitDurationMinutes &&
                payload.UnitPriceGil >= 0 &&
                payload.TotalPriceGil >= 0 &&
                !string.IsNullOrWhiteSpace(payload.OfferName),
            cancellationToken);

    public Task<ApiResult<CourtSaleCancellationResponse>> CancelCourtSaleAsync(
        Uri baseUri,
        string accessToken,
        long saleId,
        CancelCourtSaleRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtSaleCancellationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/court/sales/{saleId}/cancel",
            accessToken,
            request,
            static payload => payload.SaleId > 0 && payload.VoidedAt != default,
            cancellationToken);

    public Task<ApiResult<CourtFinancialTransactionResponse>> CreateCourtStaffSettlementAsync(
        Uri baseUri,
        string accessToken,
        CreateCourtStaffSettlementRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtFinancialTransactionResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/court/settlements/staff",
            accessToken,
            request,
            ValidateFinancialTransactionResponse,
            cancellationToken);

    public Task<ApiResult<CourtFinancialTransactionResponse>> CreateCourtAccountantPrepayAsync(
        Uri baseUri,
        string accessToken,
        CreateCourtAccountantPrepayRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtFinancialTransactionResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/court/accountants/prepay",
            accessToken,
            request,
            ValidateFinancialTransactionResponse,
            cancellationToken);

    public Task<ApiResult<CourtFinancialTransactionResponse>> CreateCourtAccountantFinalizationAsync(
        Uri baseUri,
        string accessToken,
        CreateCourtAccountantFinalizationRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtFinancialTransactionResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/court/accountants/finalize",
            accessToken,
            request,
            ValidateFinancialTransactionResponse,
            cancellationToken);

    public Task<ApiResult<CourtTransactionConfirmationResponse>> ConfirmCourtTransactionAsync(
        Uri baseUri,
        string accessToken,
        long transactionId,
        ConfirmCourtTransactionRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtTransactionConfirmationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/court/transactions/{transactionId}/confirm",
            accessToken,
            request,
            static payload =>
                payload.TransactionId > 0 &&
                payload.ConfirmedAt != default,
            cancellationToken);

    public Task<ApiResult<CourtTransactionCancellationResponse>> CancelCourtTransactionAsync(
        Uri baseUri,
        string accessToken,
        long transactionId,
        CancelCourtTransactionRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CourtTransactionCancellationResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/court/transactions/{transactionId}/cancel",
            accessToken,
            request,
            static payload =>
                payload.TransactionId > 0 &&
                payload.CancelledAt != default,
            cancellationToken);

    private static bool ValidateStaffViewResponse(StaffManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Openings is not null &&
        payload.Jobs is not null &&
        payload.VenueUsers is not null &&
        payload.StaffMembers is not null &&
        payload.Characters is not null &&
        payload.FirstSeen is not null &&
        payload.Absences is not null &&
        payload.TimeEntries is not null &&
        payload.LifecycleTaskDefinitions is not null &&
        payload.LifecycleTaskAssignments is not null &&
        payload.Openings.All(static opening =>
            opening.OpeningId > 0 &&
            opening.ClosesAt > opening.OpensAt &&
            ValidateOpeningLocation(
                opening.LocationType,
                opening.AddressWorldId,
                opening.AddressWorldName,
                opening.AddressCityId,
                opening.AddressCityName,
                opening.AddressWard,
                opening.AddressPlot,
                opening.OutdoorLocationName)) &&
        payload.Jobs.All(static job =>
            job.JobDefinitionId > 0 &&
            !string.IsNullOrWhiteSpace(job.Name) &&
            job.HourlyRateGil >= 0) &&
        payload.StaffMembers.All(static staff =>
            staff.StaffMemberId > 0 &&
            !string.IsNullOrWhiteSpace(staff.DisplayName) &&
            staff.JobDefinitionId > 0 &&
            staff.EffectiveHourlyRateGil >= 0 &&
            staff.CustomFixedAmountGil >= 0 &&
            staff.UnpaidSalaryGil >= 0 &&
            staff.SalaryDeductionGil >= 0 &&
            staff.UnsettledCourtGil >= 0) &&
        payload.FirstSeen.All(static item =>
            item.FirstSeenId > 0 &&
            item.OpeningId > 0 &&
            item.StaffMemberId > 0 &&
            item.CharacterId > 0 &&
            item.FirstSeenAt != default) &&
        payload.Absences.All(static item =>
            item.AbsenceId > 0 &&
            item.OpeningId > 0 &&
            item.StaffMemberId > 0 &&
            item.ReasonCode is "planned" or "unplanned" &&
            item.RecordedAt != default) &&
        payload.TimeEntries.All(static entry =>
            entry.TimeEntryId > 0 &&
            entry.StaffMemberId > 0 &&
            entry.OpeningId > 0 &&
            !string.IsNullOrWhiteSpace(entry.Status) &&
            !string.IsNullOrWhiteSpace(entry.ClockInSource)) &&
        payload.LifecycleTaskDefinitions.All(static task =>
            task.TaskDefinitionId > 0 &&
            StaffLifecycleTypes.IsValid(task.LifecycleType) &&
            !string.IsNullOrWhiteSpace(task.Name) &&
            task.CreatedAt != default &&
            task.UpdatedAt != default) &&
        payload.LifecycleTaskAssignments.All(static task =>
            task.AssignmentId > 0 &&
            task.StaffMemberId > 0 &&
            task.TaskDefinitionId > 0 &&
            StaffLifecycleTypes.IsValid(task.LifecycleType) &&
            !string.IsNullOrWhiteSpace(task.TaskName) &&
            task.CreatedAt != default &&
            (task.CompletedAt is null) == (task.CompletedByUserId is null));

    private static bool ValidateCourtViewResponse(CourtManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Offers is not null &&
        payload.Sales is not null &&
        payload.AccountantAccounts is not null &&
        payload.UnsettledStaff is not null &&
        payload.Transactions is not null &&
        payload.VipStatuses is not null &&
        payload.VipPerkAvailability is not null &&
        payload.CourtKeepPercentage is >= 0m and <= 100m &&
        payload.PersonalUnsettledCourtGil >= 0 &&
        payload.PersonalUnpaidSalaryGil >= 0 &&
        payload.Offers.All(static offer =>
            offer.OfferId > 0 &&
            !string.IsNullOrWhiteSpace(offer.Name) &&
            offer.DurationMinutes > 0) &&
        payload.Sales.All(static sale =>
            sale.SaleId > 0 &&
            sale.OfferId > 0 &&
            !string.IsNullOrWhiteSpace(sale.OfferName) &&
            sale.Quantity is >= 1 and <= 100 &&
            sale.UnitDurationMinutes > 0 &&
            sale.TotalDurationMinutes >= sale.UnitDurationMinutes &&
            sale.UnitPriceGil >= 0 &&
            sale.TotalPriceGil >= 0 &&
            sale.SellerPercentage is >= 0m and <= 100m &&
            sale.SellerShareGil >= 0 &&
            sale.VenueShareGil >= 0 &&
            sale.SellerShareGil + sale.VenueShareGil == sale.TotalPriceGil) &&
        payload.UnsettledStaff.All(static staff =>
            !string.IsNullOrWhiteSpace(staff.StaffDisplayName) &&
            staff.UnsettledCourtGil >= 0 &&
            staff.UnsettledSaleCount >= 0 &&
            staff.UnpaidSalaryGil >= 0 &&
            staff.UnpaidSalaryEntryCount >= 0 &&
            staff.OpenTimeEntryCount >= 0) &&
        payload.Transactions.All(static transaction =>
            transaction.TransactionId > 0 &&
            !string.IsNullOrWhiteSpace(transaction.TransactionType) &&
            !string.IsNullOrWhiteSpace(transaction.Status) &&
            transaction.TradeAmountGil >= 0 &&
            transaction.Items is not null);

    private static bool ValidateFinancialTransactionResponse(StaffPayoutResponse payload) =>
        payload.TransactionId > 0 &&
        payload.GrossSalesGil >= 0 &&
        payload.CourtRetainedGil >= 0 &&
        payload.GrossCourtGil >= 0 &&
        payload.SalaryGil >= 0 &&
        payload.TradeAmountGil >= 0 &&
        !string.IsNullOrWhiteSpace(payload.TradeDirection) &&
        payload.CreatedAt != default;

    private static bool ValidateFinancialTransactionResponse(
        CourtFinancialTransactionResponse payload) =>
        payload.TransactionId > 0 &&
        payload.GrossSalesGil >= 0 &&
        payload.CourtRetainedGil >= 0 &&
        payload.GrossCourtGil >= 0 &&
        payload.SalaryGil >= 0 &&
        payload.TradeAmountGil >= 0 &&
        !string.IsNullOrWhiteSpace(payload.TradeDirection) &&
        payload.CreatedAt != default;

    private static bool ValidatePhotoshootViewResponse(PhotoshootManagementViewResponse payload) =>
        payload.Capabilities is not null && payload.SellerPercentage is >= 0m and <= 100m &&
        payload.PersonalGrossGil >= 0 && payload.PersonalSellerShareGil >= 0 &&
        payload.PersonalUnpaidGil >= 0 && payload.PersonalPendingGil >= 0 && payload.PersonalAvailableGil >= 0 &&
        payload.Packages is not null && payload.Sales is not null && payload.VipStatuses is not null && payload.VipPerkAvailability is not null &&
        payload.Packages.All(static value => value.PackageId > 0 && value.IncludedCharacters > 0 && !string.IsNullOrWhiteSpace(value.Name)) &&
        payload.Sales.All(static value =>
            value.SaleId > 0 && value.PackageId > 0 && value.TotalGil >= 0 &&
            value.SellerPercentage is >= 0m and <= 100m && value.SellerShareGil >= 0 &&
            value.VenueShareGil >= 0 && value.SellerShareGil + value.VenueShareGil == value.TotalGil) &&
        payload.VipStatuses.All(static value => value.CharacterId > 0 && value.SubscriptionId > 0 && value.VipPackageId > 0) &&
        payload.VipPerkAvailability.All(static value => value.CharacterId > 0 && value.PerkId > 0);

    private static bool ValidateOtherSalesViewResponse(OtherSalesManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.PersonalGrossGil >= 0 && payload.PersonalSellerShareGil >= 0 &&
        payload.PersonalUnpaidGil >= 0 && payload.PersonalPendingGil >= 0 &&
        payload.PersonalAvailableGil >= 0 &&
        payload.Items is not null && payload.Perks is not null && payload.Sales is not null &&
        payload.VipStatuses is not null && payload.VipPerkAvailability is not null &&
        payload.Items.All(static item =>
            item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.Name) &&
            item.SellerPercentage is >= 0m and <= 100m &&
            ((item.PricePerUnitGil is >= 0 && item.PricePerkId is null) ||
             (item.PricePerUnitGil is null && item.PricePerkId is > 0))) &&
        payload.Sales.All(static sale =>
            sale.SaleId > 0 && sale.ItemId > 0 && sale.Quantity > 0 &&
            !string.IsNullOrWhiteSpace(sale.PriceType) &&
            !string.IsNullOrWhiteSpace(sale.ItemName) &&
            !string.IsNullOrWhiteSpace(sale.SellerDisplayName) &&
            !string.IsNullOrWhiteSpace(sale.BuyerCharacterName) &&
            !string.IsNullOrWhiteSpace(sale.BuyerWorldName) &&
            sale.UnitPriceGil >= 0 && sale.TotalGil >= 0 &&
            sale.SellerPercentage is >= 0m and <= 100m &&
            sale.SellerShareGil >= 0 && sale.VenueShareGil >= 0 &&
            sale.SellerShareGil + sale.VenueShareGil == sale.TotalGil &&
            ((sale.PriceType == "gil" && sale.PricePerkId is null) ||
             (sale.PriceType == "vip_perk" && sale.PricePerkId is > 0 && sale.Quantity == 1))) &&
        payload.VipStatuses.All(static value => value.CharacterId > 0 && value.SubscriptionId > 0 && value.VipPackageId > 0) &&
        payload.VipPerkAvailability.All(static value => value.CharacterId > 0 && value.PerkId > 0);

    private static bool ValidateOtherGamesViewResponse(OtherGamesManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.PersonalGrossGil >= 0 && payload.PersonalSellerShareGil >= 0 && payload.PersonalWinGil >= 0 &&
        payload.PersonalAwaitingOutcomeCount >= 0 && payload.PersonalAvailableSaleCount >= 0 &&
        payload.Items is not null && payload.Perks is not null && payload.Sales is not null &&
        payload.SellerBalances is not null && payload.VipStatuses is not null && payload.VipPerkAvailability is not null &&
        payload.Items.All(static item =>
            item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.Name) &&
            item.SellerPercentage is >= 0m and <= 100m &&
            ((item.PricePerUnitGil is >= 0 && item.PricePerkId is null) ||
             (item.PricePerUnitGil is null && item.PricePerkId is > 0))) &&
        payload.Sales.All(static sale =>
            sale.SaleId > 0 && sale.ItemId > 0 && sale.Quantity > 0 &&
            !string.IsNullOrWhiteSpace(sale.PriceType) &&
            !string.IsNullOrWhiteSpace(sale.ItemName) &&
            !string.IsNullOrWhiteSpace(sale.SellerDisplayName) &&
            !string.IsNullOrWhiteSpace(sale.BuyerCharacterName) &&
            !string.IsNullOrWhiteSpace(sale.BuyerWorldName) &&
            sale.UnitPriceGil >= 0 && sale.TotalGil >= 0 &&
            sale.SellerPercentage is >= 0m and <= 100m &&
            sale.SellerShareGil >= 0 && sale.VenueShareGil >= 0 &&
            sale.SellerShareGil + sale.VenueShareGil == sale.TotalGil &&
            (sale.OutcomeStatus == "pending" || sale.OutcomeStatus == "no_win" || sale.OutcomeStatus == "win") &&
            (sale.OutcomeStatus switch
            {
                "pending" => sale.WinAmountGil is null && sale.NetVenueGil is null,
                "no_win" => sale.WinAmountGil == 0 && sale.NetVenueGil == sale.VenueShareGil,
                "win" => sale.WinAmountGil.HasValue && sale.WinAmountGil.Value > 0 &&
                         sale.NetVenueGil == sale.VenueShareGil - sale.WinAmountGil.Value,
                _ => false
            }) &&
            ((sale.PriceType == "gil" && sale.PricePerkId is null) ||
             (sale.PriceType == "vip_perk" && sale.PricePerkId is > 0 && sale.Quantity == 1))) &&
        payload.SellerBalances.All(static balance => balance.SellerUserId > 0 && balance.AwaitingOutcomeCount >= 0) &&
        payload.VipStatuses.All(static value => value.CharacterId > 0 && value.SubscriptionId > 0 && value.VipPackageId > 0) &&
        payload.VipPerkAvailability.All(static value => value.CharacterId > 0 && value.PerkId > 0);

    private static bool ValidateFinanceViewResponse(FinanceViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.PersonalUnpaidVipGil >= 0 &&
        payload.PersonalPendingVipGil >= 0 &&
        payload.PersonalAvailableVipGil >= 0 &&
        payload.PersonalUnpaidPhotoshootGil >= 0 &&
        payload.PersonalPendingPhotoshootGil >= 0 &&
        payload.PersonalAvailablePhotoshootGil >= 0 &&
        payload.PersonalUnpaidOtherSalesGil >= 0 &&
        payload.PersonalPendingOtherSalesGil >= 0 &&
        payload.PersonalAvailableOtherSalesGil >= 0 &&
        payload.VenuePendingCount >= 0 &&
        payload.Settlements is not null &&
        payload.Items is not null &&
        payload.Settlements.All(static settlement =>
            settlement.SettlementId > 0 &&
            (settlement.SettlementType == "other_games" || settlement.AmountGil > 0) &&
            !string.IsNullOrWhiteSpace(settlement.SettlementType) &&
            !string.IsNullOrWhiteSpace(settlement.Status) &&
            !string.IsNullOrWhiteSpace(settlement.InitiatedByDisplayName) &&
            !string.IsNullOrWhiteSpace(settlement.TargetUserDisplayName)) &&
        payload.Items.All(static item =>
            item.SettlementItemId > 0 &&
            item.SettlementId > 0 &&
            item.SourceId > 0 &&
            (item.SourceType == "other_game_sale" || item.AmountGil > 0) &&
            !string.IsNullOrWhiteSpace(item.SourceType));

    private static bool ValidateVenueOpeningScheduleResponse(VenueOpeningScheduleResponse payload) =>
        payload.Capabilities is not null &&
        payload.SuggestedOpensAt != default &&
        payload.SuggestedDurationMinutes is >= 30 and <= 2880 &&
        payload.Themes is not null &&
        payload.Openings is not null &&
        (payload.DefaultAddress is null || ValidateVenueOpeningAddress(payload.DefaultAddress)) &&
        payload.Themes.All(static theme =>
            theme.ThemeId > 0 && !string.IsNullOrWhiteSpace(theme.Name)) &&
        payload.Openings.All(ValidateVenueOpeningScheduleItem);

    private static bool ValidateVenueOpeningHistoryResponse(VenueOpeningHistoryResponse payload) =>
        payload.Openings is not null &&
        payload.Openings.All(ValidateVenueOpeningScheduleItem) &&
        payload.DjBookings is not null &&
        payload.DjBookings.All(ValidateDjBookingSummary) &&
        (!payload.HasMore || (payload.NextBeforeOpensAt is not null && payload.NextBeforeOpeningId is > 0));

    private static bool ValidateVenueOpeningScheduleItem(VenueOpeningScheduleItem opening) =>
        opening.OpeningId > 0 &&
        opening.ClosesAt > opening.OpensAt &&
        ValidateVenueOpeningAddress(opening.Address) &&
        !string.IsNullOrWhiteSpace(opening.SourceType) &&
        opening.CreatedAt != default;

    private static bool ValidateVenueOpeningAddress(VenueOpeningAddressSummary address) =>
        ValidateOpeningLocation(
            address.LocationType,
            address.WorldId,
            address.WorldName,
            address.CityId,
            address.CityName,
            address.Ward,
            address.Plot,
            address.OutdoorLocationName);

    private static bool ValidateOpeningLocation(
        string locationType,
        int worldId,
        string worldName,
        int cityId,
        string cityName,
        int ward,
        int plot,
        string? outdoorLocationName)
    {
        if (worldId <= 0 || string.IsNullOrWhiteSpace(worldName))
            return false;

        if (string.Equals(locationType, VenueOpeningLocationTypes.Outdoor, StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(outdoorLocationName);

        return cityId > 0 &&
               !string.IsNullOrWhiteSpace(cityName) &&
               ward is >= 1 and <= 30 &&
               plot is >= 1 and <= 60;
    }

    private static bool ValidateDjViewResponse(DjViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.DefaultHourlyRateGil >= 0 &&
        payload.Djs is not null &&
        payload.Characters is not null &&
        payload.Bookings is not null &&
        payload.Statuses is not null &&
        payload.Djs.All(ValidateDjSummary) &&
        payload.Characters.All(static character =>
            character.CharacterId > 0 &&
            character.DjId > 0 &&
            !string.IsNullOrWhiteSpace(character.CharacterName) &&
            !string.IsNullOrWhiteSpace(character.WorldName)) &&
        payload.Bookings.All(ValidateDjBookingSummary) &&
        payload.Statuses.All(static status =>
            !string.IsNullOrWhiteSpace(status.StatusCode) &&
            !string.IsNullOrWhiteSpace(status.DisplayName) &&
            status.SortOrder > 0);

    private static bool ValidateDjSummary(DjSummary dj) =>
        dj.DjId > 0 &&
        !string.IsNullOrWhiteSpace(dj.Name) &&
        dj.CreatedAt != default;

    private static bool ValidateDjBookingSummary(DjBookingSummary booking) =>
        booking.BookingId > 0 &&
        booking.OpeningId > 0 &&
        booking.DjId > 0 &&
        booking.EndsAt > booking.StartsAt &&
        !string.IsNullOrWhiteSpace(booking.StatusCode) &&
        !string.IsNullOrWhiteSpace(booking.StatusName) &&
        !string.IsNullOrWhiteSpace(booking.DjName) &&
        booking.TimedMacroId > 0 &&
        booking.PriceGil >= 0 &&
        (booking.PaymentId is null ||
         (!string.IsNullOrWhiteSpace(booking.PaymentStatus) &&
          !string.IsNullOrWhiteSpace(booking.PaymentTargetCharacterName) &&
          !string.IsNullOrWhiteSpace(booking.PaymentTargetWorldName) &&
          booking.PaymentStartedAt is not null)) &&
        booking.CreatedAt != default;

    private static bool ValidateDjPaymentResponse(DjPaymentOperationResponse payment) =>
        payment.PaymentId > 0 &&
        payment.BookingId > 0 &&
        payment.AmountGil > 0 &&
        !string.IsNullOrWhiteSpace(payment.Status) &&
        !string.IsNullOrWhiteSpace(payment.TargetCharacterName) &&
        !string.IsNullOrWhiteSpace(payment.TargetWorldName) &&
        payment.StartedAt != default;

    private static bool ValidateDjBalancePaymentResponse(DjBalancePaymentResponse payment) =>
        payment.DjId > 0 &&
        !string.IsNullOrWhiteSpace(payment.DjName) &&
        payment.AmountGil > 0 &&
        !string.IsNullOrWhiteSpace(payment.TargetCharacterName) &&
        !string.IsNullOrWhiteSpace(payment.TargetWorldName) &&
        payment.StartedAt != default &&
        payment.Payments is not null &&
        payment.Payments.Count > 0 &&
        payment.Payments.All(ValidateDjPaymentResponse) &&
        payment.Payments.Sum(item => item.AmountGil) == payment.AmountGil;

    private static bool ValidateTimedMacroViewResponse(TimedMacroViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Macros is not null &&
        (payload.CurrentOpening is null ||
         (payload.CurrentOpening.OpeningId > 0 &&
          payload.CurrentOpening.ClosesAt > payload.CurrentOpening.OpensAt &&
          ValidateOpeningLocation(
              payload.CurrentOpening.LocationType,
              payload.CurrentOpening.AddressWorldId,
              payload.CurrentOpening.AddressWorldName,
              payload.CurrentOpening.AddressCityId,
              payload.CurrentOpening.AddressCityName,
              payload.CurrentOpening.AddressWard,
              payload.CurrentOpening.AddressPlot,
              payload.CurrentOpening.OutdoorLocationName))) &&
        (payload.CurrentOpening is not null ||
         payload.Macros.All(static macro => !macro.RequiresActiveOpening || macro.NextDueAt is null)) &&
        payload.Macros.All(static macro =>
            macro.TimedMacroId > 0 &&
            !string.IsNullOrWhiteSpace(macro.InstanceCode) &&
            !string.IsNullOrWhiteSpace(macro.TypeCode) &&
            !string.IsNullOrWhiteSpace(macro.DisplayName) &&
            macro.MaxLines is > 0 and <= 15 &&
            macro.MaxLineLength > 0 &&
            macro.IntervalMinutes is >= 1 and <= 10080 &&
            !string.IsNullOrWhiteSpace(macro.SourceType) &&
            macro.ExecutionCount >= 0);

    private static bool ValidateVipArrivalContextResponse(VipArrivalContextResponse payload) =>
        payload.Capabilities is not null &&
        payload.Macros is not null &&
        payload.Arrivals is not null &&
        (payload.CurrentOpening is null || ValidateVenueOpening(payload.CurrentOpening)) &&
        payload.Macros.All(static macro =>
            !string.IsNullOrWhiteSpace(macro.MacroCode) &&
            !string.IsNullOrWhiteSpace(macro.DisplayName) &&
            macro.MaxLines is > 0 and <= 15 &&
            macro.MaxLineLength > 0) &&
        payload.Arrivals.All(static arrival =>
            arrival.OpeningId > 0 &&
            arrival.VipPlayerId > 0 &&
            arrival.LastSeenCharacterId > 0 &&
            arrival.LastSeenAt >= arrival.FirstSeenAt);

    private static bool ValidateVenueOpening(VenueOpeningSummary opening) =>
        opening.OpeningId > 0 &&
        opening.ClosesAt > opening.OpensAt &&
        ValidateOpeningLocation(
            opening.LocationType,
            opening.AddressWorldId,
            opening.AddressWorldName,
            opening.AddressCityId,
            opening.AddressCityName,
            opening.AddressWard,
            opening.AddressPlot,
            opening.OutdoorLocationName) &&
        !string.IsNullOrWhiteSpace(opening.SourceType);

    private static bool ValidateOpeningPublicationContextResponse(OpeningPublicationContextResponse payload) =>
        payload.Capabilities is not null &&
        payload.Templates is not null &&
        payload.Openings is not null &&
        payload.Worlds is not null &&
        payload.Templates.All(static template =>
            !string.IsNullOrWhiteSpace(template.PublicationCode) &&
            !string.IsNullOrWhiteSpace(template.ChannelCode) &&
            !string.IsNullOrWhiteSpace(template.DisplayName) &&
            template.MaxLines > 0 &&
            template.MaxLineLength > 0) &&
        payload.Openings.All(static opening =>
            opening.OpeningId > 0 &&
            opening.ClosesAt > opening.OpensAt &&
            opening.Texts is not null &&
            opening.Texts.All(text =>
                text.OpeningId == opening.OpeningId &&
                !string.IsNullOrWhiteSpace(text.PublicationCode) &&
                !string.IsNullOrWhiteSpace(text.ChannelCode) &&
                text.MaxLines > 0 &&
                text.MaxLineLength > 0)) &&
        payload.Worlds.All(static world =>
            world.WorldId > 0 &&
            !string.IsNullOrWhiteSpace(world.WorldName) &&
            !string.IsNullOrWhiteSpace(world.DatacenterName) &&
            !string.IsNullOrWhiteSpace(world.RegionName));

    private static bool ValidateGreeterContextResponse(GreeterContextResponse payload) =>
        payload.Capabilities is not null &&
        payload.Macros is not null &&
        payload.Arrivals is not null &&
        (payload.CurrentOpening is null ||
         (payload.CurrentOpening.OpeningId > 0 &&
          payload.CurrentOpening.ClosesAt > payload.CurrentOpening.OpensAt &&
          ValidateOpeningLocation(
              payload.CurrentOpening.LocationType,
              payload.CurrentOpening.AddressWorldId,
              payload.CurrentOpening.AddressWorldName,
              payload.CurrentOpening.AddressCityId,
              payload.CurrentOpening.AddressCityName,
              payload.CurrentOpening.AddressWard,
              payload.CurrentOpening.AddressPlot,
              payload.CurrentOpening.OutdoorLocationName))) &&
        (payload.CurrentDj is null ||
         (payload.CurrentDj.BookingId > 0 &&
          !string.IsNullOrWhiteSpace(payload.CurrentDj.Name) &&
          payload.CurrentDj.EndsAt > payload.CurrentDj.StartsAt)) &&
        payload.Macros.All(static macro =>
            !string.IsNullOrWhiteSpace(macro.MacroCode) &&
            !string.IsNullOrWhiteSpace(macro.DisplayName) &&
            macro.MaxLines is > 0 and <= 15 &&
            macro.MaxLineLength > 0) &&
        payload.Arrivals.All(static arrival =>
            arrival.OpeningId > 0 &&
            arrival.WorldId > 0 &&
            !string.IsNullOrWhiteSpace(arrival.WorldName) &&
            !string.IsNullOrWhiteSpace(arrival.CharacterName) &&
            arrival.LastSeenAt >= arrival.FirstSeenAt);

    private static bool ValidateNotificationPollResponse(NotificationPollResponse payload) =>
        payload.UnseenNotificationCount >= 0 &&
        payload.PendingSettlementCount >= 0 &&
        payload.Notifications is not null &&
        payload.Notifications.All(static notification =>
            notification.NotificationId > 0 &&
            !string.IsNullOrWhiteSpace(notification.NotificationType) &&
            !string.IsNullOrWhiteSpace(notification.Title) &&
            !string.IsNullOrWhiteSpace(notification.Message) &&
            notification.CreatedAt != default);

    private static bool ValidateVipSaleResponse(SellVipSubscriptionResponse payload) =>
        payload.SubscriptionId > 0 &&
        payload.VipPlayerId > 0 &&
        payload.CharacterId > 0 &&
        payload.PackageId > 0 &&
        payload.PurchasePriceGil >= 0 &&
        payload.StartsAt != default &&
        (payload.Lifetime ? payload.EndsAt is null : payload.EndsAt > payload.StartsAt) &&
        payload.PersonalUnpaidGil >= 0 &&
        (!payload.WasNewVip || payload.OpeningId is null or > 0);

    private static bool ValidateSelfServiceViewResponse(SelfServiceViewResponse payload) =>
        payload.Characters is not null &&
        payload.Characters.All(static character =>
            character is not null &&
            character.CharacterId > 0 &&
            !string.IsNullOrWhiteSpace(character.CharacterName) &&
            !string.IsNullOrWhiteSpace(character.WorldName));

    private static bool ValidateVenueUserManagementViewResponse(VenueUserManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.AvailablePermissions is not null &&
        payload.Users is not null &&
        payload.AvailablePermissions.All(static permission =>
            permission is not null && !string.IsNullOrWhiteSpace(permission.PermissionKey)) &&
        payload.Users.All(static user =>
            user is not null &&
            user.UserId > 0 &&
            !string.IsNullOrWhiteSpace(user.DisplayName) &&
            user.Permissions is not null);

    private static bool ValidateCreateVenueUserResponse(CreateVenueUserResponse payload) =>
        payload.UserId > 0 &&
        !string.IsNullOrWhiteSpace(payload.InviteCode) &&
        payload.InviteExpiresAt > DateTimeOffset.UtcNow;

    private static bool ValidateDiscordManagementViewResponse(DiscordManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Roles is not null &&
        payload.Channels is not null &&
        payload.VenueStatus is { } venueStatus &&
        !string.IsNullOrWhiteSpace(venueStatus.OpenMessage) &&
        !string.IsNullOrWhiteSpace(venueStatus.ClosedMessage) &&
        (!venueStatus.Enabled || venueStatus.ChannelId is > 0) &&
        (venueStatus.CurrentPublication is null ||
         (venueStatus.CurrentPublication.OpeningId > 0 &&
          venueStatus.CurrentPublication.ChannelId > 0 &&
          !string.IsNullOrWhiteSpace(venueStatus.CurrentPublication.ChannelName) &&
          (venueStatus.CurrentPublication.PublicationState == DiscordVenueStatusPublicationStates.Pending ||
           venueStatus.CurrentPublication.PublicationState == DiscordVenueStatusPublicationStates.Open ||
           venueStatus.CurrentPublication.PublicationState == DiscordVenueStatusPublicationStates.Closed))) &&
        (payload.Guild is null ||
         (payload.Guild.GuildId > 0 && !string.IsNullOrWhiteSpace(payload.Guild.GuildName))) &&
        payload.Roles.All(static role =>
            role.RoleId > 0 && !string.IsNullOrWhiteSpace(role.Name) && role.Position >= 0) &&
        payload.Channels.All(static channel =>
            channel is not null && channel.ChannelId > 0 && !string.IsNullOrWhiteSpace(channel.Name) &&
            !string.IsNullOrWhiteSpace(channel.ChannelType) && channel.Position >= 0);

    private static bool ValidateGiveawayManagementViewResponse(GiveawayManagementViewResponse payload) =>
        payload.Capabilities is not null && payload.ServerNow != default &&
        payload.Channels is not null && payload.ActiveAndPending is not null &&
        payload.Ended is not null && payload.Schedulers is not null &&
        payload.Channels.All(static channel => channel is not null && channel.ChannelId > 0 &&
            !string.IsNullOrWhiteSpace(channel.Name) && !string.IsNullOrWhiteSpace(channel.ChannelType) &&
            channel.Position >= 0 && channel.LastSeenAt != default) &&
        payload.ActiveAndPending.All(ValidateGiveawaySummary) &&
        payload.Ended.All(ValidateGiveawaySummary) &&
        payload.Schedulers.All(static scheduler => scheduler is not null &&
            scheduler.SchedulerId > 0 && scheduler.ChannelId > 0 &&
            !string.IsNullOrWhiteSpace(scheduler.ChannelName) && !string.IsNullOrWhiteSpace(scheduler.Title) &&
            !string.IsNullOrWhiteSpace(scheduler.Description) &&
            !string.IsNullOrWhiteSpace(scheduler.CongratulationsMessage) &&
            scheduler.CongratulationsMessage.Contains("<winner>", StringComparison.Ordinal) &&
            scheduler.RepeatIntervalMinutes > 0 && scheduler.CreatedAt != default && scheduler.UpdatedAt != default);

    private static bool ValidateGiveawaySummary(GiveawaySummary? giveaway) =>
        giveaway is not null && giveaway.GiveawayId > 0 && giveaway.ChannelId > 0 &&
        !string.IsNullOrWhiteSpace(giveaway.Status) && !string.IsNullOrWhiteSpace(giveaway.ChannelName) &&
        !string.IsNullOrWhiteSpace(giveaway.Title) && !string.IsNullOrWhiteSpace(giveaway.Description) &&
        !string.IsNullOrWhiteSpace(giveaway.CongratulationsMessage) &&
        giveaway.CongratulationsMessage.Contains("<winner>", StringComparison.Ordinal) &&
        giveaway.EndsAt > giveaway.StartsAt && giveaway.EntryCount >= 0 &&
        giveaway.CreatedAt != default && giveaway.UpdatedAt != default;

    private static bool ValidateVenueUserOperationResponse(VenueUserOperationResponse payload) =>
        payload.UserId > 0;

    private static bool ValidateSetVenueUserPermissionsResponse(SetVenueUserPermissionsResponse payload) =>
        payload.UserId > 0 && payload.AssignedPermissionCount >= 0;

    private static bool ValidateCreateRecoveryCodeResponse(CreateRecoveryCodeResponse payload) =>
        payload.UserId > 0 &&
        !string.IsNullOrWhiteSpace(payload.RecoveryCode) &&
        payload.RecoveryCodeExpiresAt > DateTimeOffset.UtcNow;

    private static bool ValidateRestoreVenueUserResponse(RestoreVenueUserResponse payload) =>
        payload.UserId > 0 &&
        !string.IsNullOrWhiteSpace(payload.InviteCode) &&
        payload.InviteExpiresAt > DateTimeOffset.UtcNow;

    public void Dispose() => httpClient.Dispose();

    private async Task<ApiResult<TResponse>> SendJsonAsync<TResponse>(
        HttpRequestMessage message,
        Func<TResponse, bool> validator,
        CancellationToken cancellationToken)
    {
        using (message)
        {
            message.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

            try
            {
                using var response = await httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return ApiResult<TResponse>.Failed(await CreateFailureAsync(response, cancellationToken));
                }

                TResponse? payload;
                try
                {
                    payload = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
                }
                catch (JsonException)
                {
                    payload = default;
                }

                if (payload is null || !validator(payload))
                {
                    return ApiResult<TResponse>.Failed(new ApiFailure(
                        ApiFailureKind.InvalidResponse,
                        "INVALID_API_RESPONSE",
                        "The API returned an incomplete or invalid response.",
                        response.StatusCode));
                }

                return ApiResult<TResponse>.Succeeded(payload);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ApiResult<TResponse>.Failed(new ApiFailure(
                    ApiFailureKind.Transport,
                    "REQUEST_TIMEOUT",
                    "The API request timed out."));
            }
            catch (HttpRequestException exception)
            {
                return ApiResult<TResponse>.Failed(new ApiFailure(
                    ApiFailureKind.Transport,
                    "NETWORK_ERROR",
                    exception.Message));
            }
        }
    }

    public Task<ApiResult<PurchasesManagementViewResponse>> GetPurchasesAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<PurchasesManagementViewResponse>(
            baseUri,
            HttpMethod.Get,
            "api/v1/purchases",
            accessToken,
            null,
            ValidatePurchasesViewResponse,
            cancellationToken);

    public Task<ApiResult<CreatePurchaseResponse>> CreatePurchaseAsync(
        Uri baseUri,
        string accessToken,
        CreatePurchaseRequest request,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<CreatePurchaseResponse>(
            baseUri,
            HttpMethod.Post,
            "api/v1/purchases",
            accessToken,
            request,
            static payload =>
                payload.PurchaseId > 0 &&
                !string.IsNullOrWhiteSpace(payload.Status) &&
                payload.CreatedAt != default,
            cancellationToken);

    public Task<ApiResult<PurchaseStateChangeResponse>> ApprovePurchaseAsync(
        Uri baseUri,
        string accessToken,
        long purchaseId,
        CancellationToken cancellationToken) =>
        SendPurchaseStateChangeAsync(
            baseUri,
            accessToken,
            purchaseId,
            "approve",
            null,
            cancellationToken);

    public Task<ApiResult<PurchaseStateChangeResponse>> ConfirmPurchasePaidAsync(
        Uri baseUri,
        string accessToken,
        long purchaseId,
        CancellationToken cancellationToken) =>
        SendPurchaseStateChangeAsync(
            baseUri,
            accessToken,
            purchaseId,
            "confirm-paid",
            null,
            cancellationToken);

    public Task<ApiResult<PurchaseStateChangeResponse>> RejectPurchaseAsync(
        Uri baseUri,
        string accessToken,
        long purchaseId,
        RejectPurchaseRequest request,
        CancellationToken cancellationToken) =>
        SendPurchaseStateChangeAsync(
            baseUri,
            accessToken,
            purchaseId,
            "reject",
            request,
            cancellationToken);

    public Task<ApiResult<PurchaseStateChangeResponse>> CancelPurchaseAsync(
        Uri baseUri,
        string accessToken,
        long purchaseId,
        CancelPurchaseRequest request,
        CancellationToken cancellationToken) =>
        SendPurchaseStateChangeAsync(
            baseUri,
            accessToken,
            purchaseId,
            "cancel",
            request,
            cancellationToken);

    private Task<ApiResult<PurchaseStateChangeResponse>> SendPurchaseStateChangeAsync(
        Uri baseUri,
        string accessToken,
        long purchaseId,
        string action,
        object? body,
        CancellationToken cancellationToken) =>
        SendAuthorizedAsync<PurchaseStateChangeResponse>(
            baseUri,
            HttpMethod.Post,
            $"api/v1/purchases/{purchaseId}/{action}",
            accessToken,
            body,
            static payload =>
                payload.PurchaseId > 0 &&
                !string.IsNullOrWhiteSpace(payload.Status) &&
                payload.ChangedAt != default,
            cancellationToken);

    private static bool ValidatePurchasesViewResponse(PurchasesManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Purchases is not null &&
        payload.Purchases.All(static purchase =>
            purchase.PurchaseId > 0 &&
            purchase.TotalPriceGil > 0 &&
            purchase.TotalPriceGil <= int.MaxValue &&
            !string.IsNullOrWhiteSpace(purchase.Title) &&
            !string.IsNullOrWhiteSpace(purchase.Details) &&
            !string.IsNullOrWhiteSpace(purchase.Status) &&
            !string.IsNullOrWhiteSpace(purchase.CreatedByDisplayName) &&
            !string.IsNullOrWhiteSpace(purchase.CreatedByCharacterName) &&
            !string.IsNullOrWhiteSpace(purchase.CreatedByWorldName) &&
            purchase.CreatedAt != default);

    private static bool ValidatePublicVenueResponse(PublicVenueResponse payload) =>
        payload.VenueId > 0 &&
        VenueConnectionConfiguration.TryNormalizeVenueCode(payload.VenueCode, out _) &&
        !string.IsNullOrWhiteSpace(payload.VenueName);

    private static bool ValidateRefreshResponse(RefreshTokenResponse payload) =>
        !string.IsNullOrWhiteSpace(payload.AccessToken) &&
        !string.IsNullOrWhiteSpace(payload.RefreshToken) &&
        string.Equals(payload.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) &&
        payload.AccessTokenExpiresAt > DateTimeOffset.UtcNow;

    private static bool ValidateDeviceAuthenticationResponse(DeviceAuthenticationResponse payload) =>
        payload.DeviceId > 0 &&
        !string.IsNullOrWhiteSpace(payload.AccessToken) &&
        !string.IsNullOrWhiteSpace(payload.RefreshToken) &&
        string.Equals(payload.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) &&
        payload.AccessTokenExpiresAt > DateTimeOffset.UtcNow;

    private static bool ValidateConfirmationResponse(AuthenticationConfirmationResponse payload) =>
        !string.IsNullOrWhiteSpace(payload.AccessToken) &&
        string.Equals(payload.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) &&
        payload.AccessTokenExpiresAt > DateTimeOffset.UtcNow;

    private static async Task<ApiFailure> CreateFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ApiProblemDetails? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ApiProblemDetails>(JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            // The fallback below intentionally avoids returning the raw response body.
        }

        var kind = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => ApiFailureKind.Validation,
            HttpStatusCode.Unauthorized => ApiFailureKind.Authentication,
            HttpStatusCode.Forbidden => ApiFailureKind.Permission,
            HttpStatusCode.TooManyRequests => ApiFailureKind.RateLimited,
            HttpStatusCode.ServiceUnavailable => ApiFailureKind.Unavailable,
            _ => ApiFailureKind.Unknown,
        };

        var retryAfter = response.Headers.RetryAfter?.Delta;
        var code = string.IsNullOrWhiteSpace(problem?.Code)
            ? $"HTTP_{(int)response.StatusCode}"
            : problem.Code;
        var failureMessage = !string.IsNullOrWhiteSpace(problem?.Detail)
            ? problem.Detail
            : !string.IsNullOrWhiteSpace(problem?.Title)
                ? problem.Title
                : "The API rejected the request.";

        return new ApiFailure(kind, code, failureMessage, response.StatusCode, problem?.TraceId, retryAfter);
    }
}
