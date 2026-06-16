using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Models;
using System.Collections.Concurrent;

namespace PartyPulse.Authentication;

public sealed class AuthenticationManager : IDisposable
{
    private static readonly TimeSpan MinimumUsableAccessTokenLifetime = TimeSpan.FromMinutes(1);

    private readonly Configuration configuration;
    private readonly PartyPulseApiClient apiClient;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, SessionState> sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new(StringComparer.Ordinal);

    public AuthenticationManager(
        Configuration configuration,
        PartyPulseApiClient apiClient,
        IFramework framework,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.apiClient = apiClient;
        this.framework = framework;
        this.log = log;
    }

    public AuthenticationSnapshot GetSnapshot(VenueConnectionConfiguration venue)
    {
        if (!venue.TryValidate(out var validationError))
        {
            return new AuthenticationSnapshot(
                AuthenticationStatus.NotConfigured,
                validationError,
                null,
                null,
                null);
        }

        if (!sessions.TryGetValue(venue.ProfileId, out var state))
        {
            return AuthenticationSnapshot.Disconnected;
        }

        if (state.Status == AuthenticationStatus.Connected &&
            state.AccessTokenExpiresAt is { } expiresAt &&
            expiresAt <= DateTimeOffset.UtcNow)
        {
            return new AuthenticationSnapshot(
                AuthenticationStatus.Expired,
                "The access token expired. The next authorized operation will refresh it.",
                expiresAt,
                state.LastAttemptAt,
                state.LastSuccessAt);
        }

        return state.ToSnapshot();
    }

    public void SetClientError(VenueConnectionConfiguration venue, string message)
    {
        sessions.AddOrUpdate(
            venue.ProfileId,
            _ => new SessionState(
                AuthenticationStatus.WaitingForPlayer,
                message,
                null,
                null,
                DateTimeOffset.UtcNow,
                null),
            (_, previous) => previous with
            {
                Status = AuthenticationStatus.WaitingForPlayer,
                Message = message,
                AccessToken = null,
                AccessTokenExpiresAt = null,
                LastAttemptAt = DateTimeOffset.UtcNow,
            });
    }

    public async Task ConnectConfiguredAsync(
        IReadOnlyCollection<VenueConnectionConfiguration> venues,
        PlayerIdentity identity,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        foreach (var venue in venues)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!venue.TryValidate(out _))
            {
                continue;
            }

            await RefreshAsync(venue, identity, apiBaseUrl, cancellationToken);
        }
    }

    public async Task<ApiResult<RefreshTokenResponse>> RefreshAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!venue.TryValidate(out var validationError))
        {
            var failure = new ApiFailure(
                ApiFailureKind.Validation,
                "INVALID_VENUE_CONFIGURATION",
                validationError);
            SetFailure(venue.ProfileId, failure);
            return ApiResult<RefreshTokenResponse>.Failed(failure);
        }

        if (!PartyPulseApiClient.TryCreateBaseUri(apiBaseUrl, out var baseUri, out var urlError))
        {
            var failure = new ApiFailure(
                ApiFailureKind.Validation,
                "INVALID_API_URL",
                urlError);
            SetFailure(venue.ProfileId, failure);
            return ApiResult<RefreshTokenResponse>.Failed(failure);
        }

        var lockKey = $"{venue.VenueId}:{venue.DeviceId}";
        var gate = refreshLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            var attemptAt = DateTimeOffset.UtcNow;
            sessions.AddOrUpdate(
                venue.ProfileId,
                _ => SessionState.Connecting(attemptAt),
                (_, previous) => previous with
                {
                    Status = AuthenticationStatus.Connecting,
                    Message = $"Authenticating {identity.DisplayName}...",
                    AccessToken = null,
                    AccessTokenExpiresAt = null,
                    LastAttemptAt = attemptAt,
                });

            var request = new RefreshTokenRequest(
                venue.VenueId,
                identity.CharacterName,
                identity.WorldName,
                venue.DeviceId,
                venue.RefreshToken.Trim());

            var result = await apiClient.RefreshAsync(baseUri!, request, cancellationToken);
            if (!result.Success || result.Value is null)
            {
                SetFailure(venue.ProfileId, result.Failure!);
                return result;
            }

            var response = result.Value;
            await PersistRefreshTokenAsync(venue, response.RefreshToken, cancellationToken);

            var successAt = DateTimeOffset.UtcNow;
            sessions[venue.ProfileId] = new SessionState(
                AuthenticationStatus.Connected,
                $"Connected as {identity.DisplayName}.",
                response.AccessToken,
                response.AccessTokenExpiresAt,
                attemptAt,
                successAt);

            log.Information(
                "Authenticated venue profile {ProfileId} for venue {VenueId}, device {DeviceId}, character {CharacterName} @ {WorldName}. Access token expires at {ExpiresAt}.",
                venue.ProfileId,
                venue.VenueId,
                venue.DeviceId,
                identity.CharacterName,
                identity.WorldName,
                response.AccessTokenExpiresAt);

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AccessTokenResult> EnsureAccessTokenAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        if (TryGetValidAccessToken(venue.ProfileId, MinimumUsableAccessTokenLifetime, out var accessToken))
        {
            return AccessTokenResult.Succeeded(accessToken!);
        }

        var refreshResult = await RefreshAsync(venue, identity, apiBaseUrl, cancellationToken);
        if (!refreshResult.Success || refreshResult.Value is null)
        {
            return AccessTokenResult.Failed(refreshResult.Failure!);
        }

        return AccessTokenResult.Succeeded(refreshResult.Value.AccessToken);
    }

    public bool TryGetValidAccessToken(
        Guid profileId,
        TimeSpan minimumRemainingLifetime,
        out string? accessToken)
    {
        accessToken = null;

        if (!sessions.TryGetValue(profileId, out var state) ||
            state.Status != AuthenticationStatus.Connected ||
            string.IsNullOrWhiteSpace(state.AccessToken) ||
            state.AccessTokenExpiresAt is not { } expiresAt ||
            expiresAt <= DateTimeOffset.UtcNow.Add(minimumRemainingLifetime))
        {
            return false;
        }

        accessToken = state.AccessToken;
        return true;
    }

    public void ClearAccessTokens(string message = "Not connected.")
    {
        foreach (var pair in sessions)
        {
            sessions[pair.Key] = pair.Value with
            {
                Status = AuthenticationStatus.Disconnected,
                Message = message,
                AccessToken = null,
                AccessTokenExpiresAt = null,
            };
        }
    }

    public void RemoveProfile(Guid profileId) => sessions.TryRemove(profileId, out _);

    public void Dispose()
    {
        foreach (var gate in refreshLocks.Values)
        {
            gate.Dispose();
        }

        refreshLocks.Clear();
        sessions.Clear();
    }

    private async Task PersistRefreshTokenAsync(
        VenueConnectionConfiguration venue,
        string newRefreshToken,
        CancellationToken cancellationToken)
    {
        await framework.RunOnTick(
            () =>
            {
                var previousToken = venue.RefreshToken;
                var previousUpdatedAt = venue.RefreshTokenUpdatedAt;

                venue.RefreshToken = newRefreshToken;
                venue.RefreshTokenUpdatedAt = DateTimeOffset.UtcNow;

                try
                {
                    configuration.Save();
                }
                catch
                {
                    venue.RefreshToken = previousToken;
                    venue.RefreshTokenUpdatedAt = previousUpdatedAt;
                    throw;
                }
            },
            cancellationToken: cancellationToken);
    }

    private void SetFailure(Guid profileId, ApiFailure failure)
    {
        var message = failure.TraceId is { Length: > 0 }
            ? $"{failure.Message} (trace {failure.TraceId})"
            : failure.Message;

        sessions.AddOrUpdate(
            profileId,
            _ => SessionState.Failed(message),
            (_, previous) => previous with
            {
                Status = AuthenticationStatus.Failed,
                Message = message,
                AccessToken = null,
                AccessTokenExpiresAt = null,
                LastAttemptAt = DateTimeOffset.UtcNow,
            });

        log.Warning(
            "PartyPulse authentication failed for profile {ProfileId}. Code: {Code}; status: {Status}; trace: {TraceId}.",
            profileId,
            failure.Code,
            failure.StatusCode,
            failure.TraceId);
    }

    private sealed record SessionState(
        AuthenticationStatus Status,
        string Message,
        string? AccessToken,
        DateTimeOffset? AccessTokenExpiresAt,
        DateTimeOffset? LastAttemptAt,
        DateTimeOffset? LastSuccessAt)
    {
        public static SessionState Connecting(DateTimeOffset attemptedAt) => new(
            AuthenticationStatus.Connecting,
            "Authenticating...",
            null,
            null,
            attemptedAt,
            null);

        public static SessionState Failed(string message) => new(
            AuthenticationStatus.Failed,
            message,
            null,
            null,
            DateTimeOffset.UtcNow,
            null);

        public AuthenticationSnapshot ToSnapshot() => new(
            Status,
            Message,
            AccessTokenExpiresAt,
            LastAttemptAt,
            LastSuccessAt);
    }
}
