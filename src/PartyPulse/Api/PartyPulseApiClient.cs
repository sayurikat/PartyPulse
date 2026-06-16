using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(method, new Uri(baseUri, relativePath.TrimStart('/')));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await SendJsonAsync<TResponse>(message, static _ => true, cancellationToken);
    }

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
