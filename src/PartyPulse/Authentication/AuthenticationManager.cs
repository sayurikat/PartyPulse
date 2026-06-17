using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Models;

namespace PartyPulse.Authentication;

public sealed class AuthenticationManager : IDisposable
{
    private static readonly TimeSpan MinimumUsableAccessTokenLifetime = TimeSpan.FromMinutes(1);

    private readonly Configuration configuration;
    private readonly PartyPulseApiClient apiClient;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, SessionState> sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> authenticationLocks = new(StringComparer.Ordinal);

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
        if (sessions.TryGetValue(venue.ProfileId, out var activeState) &&
            activeState.Status is AuthenticationStatus.Connecting or
                AuthenticationStatus.Failed or
                AuthenticationStatus.WaitingForPlayer)
        {
            return activeState.ToSnapshot();
        }

        if (!venue.TryValidateForRefresh(out var validationError))
        {
            return new AuthenticationSnapshot(
                AuthenticationStatus.NotConfigured,
                validationError,
                null,
                activeState?.LastAttemptAt,
                activeState?.LastSuccessAt);
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
                false,
                DateTimeOffset.UtcNow,
                null),
            (_, previous) => previous with
            {
                Status = AuthenticationStatus.WaitingForPlayer,
                Message = message,
                AccessToken = null,
                AccessTokenExpiresAt = null,
                ConfirmationPending = false,
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

            if (!venue.TryValidateForRefresh(out _))
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
        if (!venue.TryValidateForRefresh(out var validationError))
        {
            var failure = ValidationFailure(validationError);
            SetFailure(venue.ProfileId, failure);
            return ApiResult<RefreshTokenResponse>.Failed(failure);
        }

        if (!TryGetBaseUri(apiBaseUrl, venue.ProfileId, out var baseUri, out var baseUriFailure))
        {
            return ApiResult<RefreshTokenResponse>.Failed(baseUriFailure!);
        }

        var gate = GetAuthenticationLock(venue);
        await gate.WaitAsync(cancellationToken);

        try
        {
            var attemptAt = BeginConnecting(venue, $"Authenticating {identity.DisplayName}...");
            var request = new RefreshTokenRequest(
                VenueConnectionConfiguration.NormalizeVenueCode(venue.VenueCode),
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
            await CompleteAuthenticationAsync(
                venue,
                identity,
                baseUri!,
                response.AccessToken,
                response.AccessTokenExpiresAt,
                attemptAt,
                cancellationToken);

            log.Information(
                "Authenticated venue profile {ProfileId} for venue {VenueId}, device {DeviceId}, character {CharacterName} @ {WorldName}.",
                venue.ProfileId,
                venue.VenueId,
                venue.DeviceId,
                identity.CharacterName,
                identity.WorldName);

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<ApiResult<DeviceAuthenticationResponse>> RedeemInviteAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string inviteCode,
        string apiBaseUrl,
        CancellationToken cancellationToken) =>
        BootstrapAsync(venue, identity, inviteCode, apiBaseUrl, BootstrapKind.Invite, cancellationToken);

    public Task<ApiResult<DeviceAuthenticationResponse>> RecoverAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string recoveryCode,
        string apiBaseUrl,
        CancellationToken cancellationToken) =>
        BootstrapAsync(venue, identity, recoveryCode, apiBaseUrl, BootstrapKind.Recovery, cancellationToken);

    public Task<ApiResult<DeviceAuthenticationResponse>> RedeemPairingCodeAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string pairingCode,
        string apiBaseUrl,
        CancellationToken cancellationToken) =>
        BootstrapAsync(venue, identity, pairingCode, apiBaseUrl, BootstrapKind.Pairing, cancellationToken);

    public async Task<ApiResult<LinkCurrentCharacterResponse>> LinkCurrentCharacterAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!venue.TryValidateForRefresh(out var validationError))
        {
            var failure = ValidationFailure(validationError);
            SetFailure(venue.ProfileId, failure);
            return ApiResult<LinkCurrentCharacterResponse>.Failed(failure);
        }

        if (!TryGetBaseUri(apiBaseUrl, venue.ProfileId, out var baseUri, out var baseUriFailure))
        {
            return ApiResult<LinkCurrentCharacterResponse>.Failed(baseUriFailure!);
        }

        ApiResult<LinkCurrentCharacterResponse> linkResult;
        var gate = GetAuthenticationLock(venue);
        await gate.WaitAsync(cancellationToken);
        try
        {
            linkResult = await apiClient.LinkCurrentCharacterAsync(
                baseUri!,
                new LinkCurrentCharacterRequest(
                    VenueConnectionConfiguration.NormalizeVenueCode(venue.VenueCode),
                    identity.CharacterName,
                    identity.WorldName,
                    venue.DeviceId,
                    venue.RefreshToken.Trim()),
                cancellationToken);

            if (!linkResult.Success)
            {
                SetFailure(venue.ProfileId, linkResult.Failure!);
                return linkResult;
            }
        }
        finally
        {
            gate.Release();
        }

        var refreshResult = await RefreshAsync(venue, identity, apiBaseUrl, cancellationToken);
        if (!refreshResult.Success)
        {
            return ApiResult<LinkCurrentCharacterResponse>.Failed(refreshResult.Failure!);
        }

        return linkResult;
    }

    public async Task<AccessTokenResult> EnsureAccessTokenAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        if (TryGetValidAccessToken(venue.ProfileId, MinimumUsableAccessTokenLifetime, out var accessToken))
        {
            if (sessions.TryGetValue(venue.ProfileId, out var state) && state.ConfirmationPending)
            {
                await TryConfirmPendingAsync(venue, identity, apiBaseUrl, cancellationToken);
                TryGetValidAccessToken(venue.ProfileId, TimeSpan.Zero, out accessToken);
            }

            return AccessTokenResult.Succeeded(accessToken!);
        }

        var refreshResult = await RefreshAsync(venue, identity, apiBaseUrl, cancellationToken);
        if (!refreshResult.Success)
        {
            return AccessTokenResult.Failed(refreshResult.Failure!);
        }

        return TryGetValidAccessToken(venue.ProfileId, TimeSpan.Zero, out accessToken)
            ? AccessTokenResult.Succeeded(accessToken!)
            : AccessTokenResult.Failed(new ApiFailure(
                ApiFailureKind.InvalidResponse,
                "ACCESS_TOKEN_NOT_AVAILABLE",
                "Authentication completed without a usable access token."));
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
                ConfirmationPending = false,
            };
        }
    }

    public void RemoveProfile(Guid profileId) => sessions.TryRemove(profileId, out _);

    public void Dispose()
    {
        foreach (var gate in authenticationLocks.Values)
        {
            gate.Dispose();
        }

        authenticationLocks.Clear();
        sessions.Clear();
    }

    private async Task<ApiResult<DeviceAuthenticationResponse>> BootstrapAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string code,
        string apiBaseUrl,
        BootstrapKind kind,
        CancellationToken cancellationToken)
    {
        if (!venue.TryValidateForEnrollment(out var validationError))
        {
            var failure = ValidationFailure(validationError);
            SetFailure(venue.ProfileId, failure);
            return ApiResult<DeviceAuthenticationResponse>.Failed(failure);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            var failure = ValidationFailure(kind switch
            {
                BootstrapKind.Invite => "An invite code is required.",
                BootstrapKind.Recovery => "A recovery code is required.",
                BootstrapKind.Pairing => "A device pairing code is required.",
                _ => "A one-time code is required."
            });
            SetFailure(venue.ProfileId, failure);
            return ApiResult<DeviceAuthenticationResponse>.Failed(failure);
        }

        if (!TryGetBaseUri(apiBaseUrl, venue.ProfileId, out var baseUri, out var baseUriFailure))
        {
            return ApiResult<DeviceAuthenticationResponse>.Failed(baseUriFailure!);
        }

        var gate = GetAuthenticationLock(venue);
        await gate.WaitAsync(cancellationToken);

        try
        {
            var operation = kind switch
            {
                BootstrapKind.Invite => "Registering device",
                BootstrapKind.Recovery => "Recovering account",
                BootstrapKind.Pairing => "Pairing device",
                _ => "Registering device"
            };
            var attemptAt = BeginConnecting(venue, $"{operation} for {identity.DisplayName}...");

            ApiResult<DeviceAuthenticationResponse> result = kind switch
            {
                BootstrapKind.Invite => await apiClient.RedeemInviteAsync(
                    baseUri!,
                    new RedeemInviteRequest(
                        VenueConnectionConfiguration.NormalizeVenueCode(venue.VenueCode),
                        identity.CharacterName,
                        identity.WorldName,
                        venue.DeviceName.Trim(),
                        code.Trim()),
                    cancellationToken),
                BootstrapKind.Recovery => await apiClient.RecoverAsync(
                    baseUri!,
                    new RecoverAccountRequest(
                        VenueConnectionConfiguration.NormalizeVenueCode(venue.VenueCode),
                        identity.CharacterName,
                        identity.WorldName,
                        venue.DeviceName.Trim(),
                        code.Trim()),
                    cancellationToken),
                BootstrapKind.Pairing => await apiClient.RedeemDevicePairingCodeAsync(
                    baseUri!,
                    new RedeemDevicePairingCodeRequest(
                        VenueConnectionConfiguration.NormalizeVenueCode(venue.VenueCode),
                        identity.CharacterName,
                        identity.WorldName,
                        venue.DeviceName.Trim(),
                        code.Trim()),
                    cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };

            if (!result.Success || result.Value is null)
            {
                SetFailure(venue.ProfileId, result.Failure!);
                return result;
            }

            var response = result.Value;
            await PersistDeviceRegistrationAsync(
                venue,
                response.DeviceId,
                response.RefreshToken,
                cancellationToken);

            await CompleteAuthenticationAsync(
                venue,
                identity,
                baseUri!,
                response.AccessToken,
                response.AccessTokenExpiresAt,
                attemptAt,
                cancellationToken);

            log.Information(
                "{Operation} completed for venue profile {ProfileId}, venue {VenueCode} ({VenueId}), device {DeviceId}, character {CharacterName} @ {WorldName}.",
                kind switch
                {
                    BootstrapKind.Invite => "Invite redemption",
                    BootstrapKind.Recovery => "Account recovery",
                    BootstrapKind.Pairing => "Device pairing",
                    _ => "Device registration"
                },
                venue.ProfileId,
                venue.VenueCode,
                venue.VenueId,
                venue.DeviceId,
                identity.CharacterName,
                identity.WorldName);

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CompleteAuthenticationAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        Uri baseUri,
        string accessToken,
        DateTimeOffset accessTokenExpiresAt,
        DateTimeOffset attemptAt,
        CancellationToken cancellationToken)
    {
        var confirmation = await apiClient.ConfirmAuthenticationAsync(
            baseUri,
            accessToken,
            cancellationToken);

        var successAt = DateTimeOffset.UtcNow;
        if (confirmation.Success && confirmation.Value is not null)
        {
            sessions[venue.ProfileId] = new SessionState(
                AuthenticationStatus.Connected,
                $"Connected as {identity.DisplayName}.",
                confirmation.Value.AccessToken,
                confirmation.Value.AccessTokenExpiresAt,
                false,
                attemptAt,
                successAt);
            return;
        }

        sessions[venue.ProfileId] = new SessionState(
            AuthenticationStatus.Connected,
            $"Connected as {identity.DisplayName}; token confirmation will retry automatically.",
            accessToken,
            accessTokenExpiresAt,
            true,
            attemptAt,
            successAt);

        log.Warning(
            "Authentication confirmation remains pending for profile {ProfileId}. Code: {Code}; status: {Status}; trace: {TraceId}.",
            venue.ProfileId,
            confirmation.Failure?.Code,
            confirmation.Failure?.StatusCode,
            confirmation.Failure?.TraceId);
    }

    private async Task TryConfirmPendingAsync(
        VenueConnectionConfiguration venue,
        PlayerIdentity identity,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(venue.ProfileId, out var state) ||
            !state.ConfirmationPending ||
            string.IsNullOrWhiteSpace(state.AccessToken) ||
            !PartyPulseApiClient.TryCreateBaseUri(apiBaseUrl, out var baseUri, out _))
        {
            return;
        }

        var result = await apiClient.ConfirmAuthenticationAsync(baseUri!, state.AccessToken, cancellationToken);
        if (!result.Success || result.Value is null)
        {
            return;
        }

        sessions[venue.ProfileId] = state with
        {
            Message = $"Connected as {identity.DisplayName}.",
            AccessToken = result.Value.AccessToken,
            AccessTokenExpiresAt = result.Value.AccessTokenExpiresAt,
            ConfirmationPending = false,
            LastSuccessAt = DateTimeOffset.UtcNow,
        };
    }

    private SemaphoreSlim GetAuthenticationLock(VenueConnectionConfiguration venue) =>
        authenticationLocks.GetOrAdd(
            venue.DeviceId > 0
                ? $"{venue.VenueId}:{venue.DeviceId}"
                : $"profile:{venue.ProfileId:N}",
            _ => new SemaphoreSlim(1, 1));

    private DateTimeOffset BeginConnecting(VenueConnectionConfiguration venue, string message)
    {
        var attemptAt = DateTimeOffset.UtcNow;
        sessions.AddOrUpdate(
            venue.ProfileId,
            _ => SessionState.Connecting(attemptAt, message),
            (_, previous) => previous with
            {
                Status = AuthenticationStatus.Connecting,
                Message = message,
                AccessToken = null,
                AccessTokenExpiresAt = null,
                ConfirmationPending = false,
                LastAttemptAt = attemptAt,
            });
        return attemptAt;
    }

    private bool TryGetBaseUri(
        string apiBaseUrl,
        Guid profileId,
        out Uri? baseUri,
        out ApiFailure? failure)
    {
        if (PartyPulseApiClient.TryCreateBaseUri(apiBaseUrl, out baseUri, out var urlError))
        {
            failure = null;
            return true;
        }

        failure = new ApiFailure(
            ApiFailureKind.Validation,
            "INVALID_API_URL",
            urlError);
        SetFailure(profileId, failure);
        return false;
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

    private async Task PersistDeviceRegistrationAsync(
        VenueConnectionConfiguration venue,
        int deviceId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        await framework.RunOnTick(
            () =>
            {
                var previousDeviceId = venue.DeviceId;
                var previousToken = venue.RefreshToken;
                var previousUpdatedAt = venue.RefreshTokenUpdatedAt;

                venue.DeviceId = deviceId;
                venue.RefreshToken = refreshToken;
                venue.RefreshTokenUpdatedAt = DateTimeOffset.UtcNow;

                try
                {
                    configuration.Save();
                }
                catch
                {
                    venue.DeviceId = previousDeviceId;
                    venue.RefreshToken = previousToken;
                    venue.RefreshTokenUpdatedAt = previousUpdatedAt;
                    throw;
                }
            },
            cancellationToken: cancellationToken);
    }

    private static ApiFailure ValidationFailure(string message) => new(
        ApiFailureKind.Validation,
        "INVALID_VENUE_CONFIGURATION",
        message);

    private void SetFailure(Guid profileId, ApiFailure failure)
    {
        var message = failure.TraceId is { Length: > 0 }
            ? $"{failure.Message} (trace {failure.TraceId})"
            : failure.Message;

        var status = string.Equals(failure.Code, "CHARACTER_NOT_LINKED", StringComparison.Ordinal)
            ? AuthenticationStatus.CharacterNotLinked
            : AuthenticationStatus.Failed;

        sessions.AddOrUpdate(
            profileId,
            _ => new SessionState(status, message, null, null, false, DateTimeOffset.UtcNow, null),
            (_, previous) => previous with
            {
                Status = status,
                Message = message,
                AccessToken = null,
                AccessTokenExpiresAt = null,
                ConfirmationPending = false,
                LastAttemptAt = DateTimeOffset.UtcNow,
            });

        log.Warning(
            "PartyPulse authentication failed for profile {ProfileId}. Code: {Code}; status: {Status}; trace: {TraceId}.",
            profileId,
            failure.Code,
            failure.StatusCode,
            failure.TraceId);
    }

    private enum BootstrapKind
    {
        Invite,
        Recovery,
        Pairing
    }

    private sealed record SessionState(
        AuthenticationStatus Status,
        string Message,
        string? AccessToken,
        DateTimeOffset? AccessTokenExpiresAt,
        bool ConfirmationPending,
        DateTimeOffset? LastAttemptAt,
        DateTimeOffset? LastSuccessAt)
    {
        public static SessionState Connecting(DateTimeOffset attemptedAt, string message) => new(
            AuthenticationStatus.Connecting,
            message,
            null,
            null,
            false,
            attemptedAt,
            null);

        public static SessionState Failed(string message) => new(
            AuthenticationStatus.Failed,
            message,
            null,
            null,
            false,
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
