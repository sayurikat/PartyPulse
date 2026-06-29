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
                payload.OpeningId > 0 &&
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

    public Task<ApiResult<SellPhotoshootResponse>> SellPhotoshootAsync(
        Uri baseUri, string accessToken, SellPhotoshootRequest request, CancellationToken cancellationToken) =>
        SendAuthorizedAsync<SellPhotoshootResponse>(
            baseUri, HttpMethod.Post, "api/v1/photoshoots/sales", accessToken, request,
            static payload => payload.SaleId > 0 && payload.PackageId > 0 && payload.TotalGil >= 0 && payload.SoldAt != default,
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
                              payload.AmountGil > 0 &&
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
        payload.Packages.All(static package =>
            package.PackageId > 0 &&
            !string.IsNullOrWhiteSpace(package.Name) &&
            package.Tier > 0 &&
            package.PriceGil >= 0) &&
        payload.Players.All(static player =>
            player.VipPlayerId > 0 &&
            !string.IsNullOrWhiteSpace(player.DisplayCharacterName) &&
            !string.IsNullOrWhiteSpace(player.DisplayWorldName)) &&
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
            !string.IsNullOrWhiteSpace(subscription.SellerDisplayName));

    private static bool ValidateVipPerkViewResponse(VipPerkManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Perks is not null && payload.PackageAssignments is not null &&
        payload.Availability is not null && payload.Redemptions is not null &&
        payload.Perks.All(static value => value.PerkId > 0 && !string.IsNullOrWhiteSpace(value.Name)) &&
        payload.PackageAssignments.All(static value => value.PackagePerkId > 0 && value.PackageId > 0 && value.PerkId > 0) &&
        payload.Availability.All(static value => value.CharacterId > 0 && value.SubscriptionId > 0 && value.PerkId > 0) &&
        payload.Redemptions.All(static value => value.RedemptionId > 0 && value.PerkId > 0);

    private static bool ValidatePhotoshootViewResponse(PhotoshootManagementViewResponse payload) =>
        payload.Capabilities is not null && payload.PersonalUnpaidGil >= 0 &&
        payload.PersonalPendingGil >= 0 && payload.PersonalAvailableGil >= 0 &&
        payload.Packages is not null && payload.Sales is not null && payload.VipStatuses is not null && payload.VipPerkAvailability is not null &&
        payload.Packages.All(static value => value.PackageId > 0 && value.IncludedCharacters > 0 && !string.IsNullOrWhiteSpace(value.Name)) &&
        payload.Sales.All(static value => value.SaleId > 0 && value.PackageId > 0 && value.TotalGil >= 0) &&
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
        payload.VenuePendingCount >= 0 &&
        payload.Settlements is not null &&
        payload.Items is not null &&
        payload.Settlements.All(static settlement =>
            settlement.SettlementId > 0 &&
            settlement.AmountGil > 0 &&
            !string.IsNullOrWhiteSpace(settlement.SettlementType) &&
            !string.IsNullOrWhiteSpace(settlement.Status) &&
            !string.IsNullOrWhiteSpace(settlement.InitiatedByDisplayName) &&
            !string.IsNullOrWhiteSpace(settlement.TargetUserDisplayName)) &&
        payload.Items.All(static item =>
            item.SettlementItemId > 0 &&
            item.SettlementId > 0 &&
            item.SourceId > 0 &&
            item.AmountGil > 0 &&
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
        (!payload.HasMore || (payload.NextBeforeOpensAt is not null && payload.NextBeforeOpeningId is > 0));

    private static bool ValidateVenueOpeningScheduleItem(VenueOpeningScheduleItem opening) =>
        opening.OpeningId > 0 &&
        opening.ClosesAt > opening.OpensAt &&
        ValidateVenueOpeningAddress(opening.Address) &&
        !string.IsNullOrWhiteSpace(opening.SourceType) &&
        opening.CreatedAt != default;

    private static bool ValidateVenueOpeningAddress(VenueOpeningAddressSummary address) =>
        address.WorldId > 0 &&
        !string.IsNullOrWhiteSpace(address.WorldName) &&
        address.CityId > 0 &&
        !string.IsNullOrWhiteSpace(address.CityName) &&
        address.Ward is >= 1 and <= 30 &&
        address.Plot is >= 1 and <= 60;


    private static bool ValidateDjViewResponse(DjViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Djs is not null &&
        payload.Bookings is not null &&
        payload.Statuses is not null &&
        payload.Djs.All(ValidateDjSummary) &&
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
        booking.CreatedAt != default;

    private static bool ValidateTimedMacroViewResponse(TimedMacroViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.Macros is not null &&
        (payload.CurrentOpening is null ||
         (payload.CurrentOpening.OpeningId > 0 &&
          payload.CurrentOpening.ClosesAt > payload.CurrentOpening.OpensAt &&
          payload.CurrentOpening.AddressWorldId > 0 &&
          !string.IsNullOrWhiteSpace(payload.CurrentOpening.AddressWorldName) &&
          payload.CurrentOpening.AddressCityId > 0 &&
          !string.IsNullOrWhiteSpace(payload.CurrentOpening.AddressCityName) &&
          payload.CurrentOpening.AddressWard is >= 1 and <= 30 &&
          payload.CurrentOpening.AddressPlot is >= 1 and <= 60)) &&
        (payload.CurrentOpening is not null ||
         payload.Macros.All(static macro => macro.NextDueAt is null)) &&
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
        opening.AddressWorldId > 0 &&
        !string.IsNullOrWhiteSpace(opening.AddressWorldName) &&
        opening.AddressCityId > 0 &&
        !string.IsNullOrWhiteSpace(opening.AddressCityName) &&
        opening.AddressWard > 0 &&
        opening.AddressPlot > 0 &&
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
          payload.CurrentOpening.AddressWorldId > 0 &&
          !string.IsNullOrWhiteSpace(payload.CurrentOpening.AddressWorldName) &&
          payload.CurrentOpening.AddressCityId > 0 &&
          !string.IsNullOrWhiteSpace(payload.CurrentOpening.AddressCityName) &&
          payload.CurrentOpening.AddressWard is >= 1 and <= 30 &&
          payload.CurrentOpening.AddressPlot is >= 1 and <= 60)) &&
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
