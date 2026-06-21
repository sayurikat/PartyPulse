using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
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

    private static bool ValidateVipManagementViewResponse(VipManagementViewResponse payload) =>
        payload.Capabilities is not null &&
        payload.PersonalUnpaidGil >= 0 &&
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
            !string.IsNullOrWhiteSpace(player.DiscordUsername) &&
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

    private static bool ValidateVipSaleResponse(SellVipSubscriptionResponse payload) =>
        payload.SubscriptionId > 0 &&
        payload.VipPlayerId > 0 &&
        payload.CharacterId > 0 &&
        payload.PackageId > 0 &&
        payload.PurchasePriceGil >= 0 &&
        payload.StartsAt != default &&
        (payload.Lifetime ? payload.EndsAt is null : payload.EndsAt > payload.StartsAt) &&
        payload.PersonalUnpaidGil >= 0;

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
