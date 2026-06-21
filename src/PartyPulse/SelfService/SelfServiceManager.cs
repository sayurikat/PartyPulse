using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.SelfService;

public sealed class SelfServiceManager : IDisposable
{
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, SelfServiceSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public SelfServiceManager(
        Configuration configuration,
        AuthenticationManager authentication,
        PartyPulseApiClient apiClient,
        PlayerIdentityProvider identityProvider,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.authentication = authentication;
        this.apiClient = apiClient;
        this.identityProvider = identityProvider;
        this.log = log;
    }

    public SelfServiceSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : SelfServiceSnapshot.NotLoaded;

    public bool ShouldLoad(VenueConnectionConfiguration venue)
    {
        if (!venue.IsRegistered)
        {
            return false;
        }

        return !snapshots.TryGetValue(venue.ProfileId, out var snapshot) ||
               snapshot.Status is SelfServiceStatus.NotLoaded or SelfServiceStatus.Failed;
    }

    public Task<ApiResult<SelfServiceViewResponse>> LoadAsync(
        VenueConnectionConfiguration venue,
        bool force,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            venue,
            async (baseUri, accessToken, token) =>
            {
                snapshots.TryGetValue(venue.ProfileId, out var current);
                if (!force && current is not null &&
                    current.Status == SelfServiceStatus.Ready && current.View is not null)
                {
                    return ApiResult<SelfServiceViewResponse>.Succeeded(current.View);
                }

                snapshots[venue.ProfileId] = new SelfServiceSnapshot(
                    SelfServiceStatus.Loading,
                    "Loading your venue account...",
                    current?.View,
                    current?.LatestPairingCode);

                var result = await apiClient.GetSelfServiceAsync(baseUri, accessToken, token);
                if (result.Success && result.Value is not null)
                {
                    snapshots[venue.ProfileId] = new SelfServiceSnapshot(
                        SelfServiceStatus.Ready,
                        "Your venue account is ready.",
                        result.Value,
                        current?.LatestPairingCode);
                }
                else
                {
                    SetFailure(venue.ProfileId, result.Failure);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<SelfServiceOperationResponse>> UnlinkCharacterAsync(
        VenueConnectionConfiguration venue,
        int characterId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            venue,
            async (baseUri, accessToken, token) =>
            {
                var result = await apiClient.UnlinkSelfCharacterAsync(
                    baseUri,
                    accessToken,
                    characterId,
                    token);

                if (result.Success)
                {
                    await ReloadWithinGateAsync(venue, baseUri, accessToken, token);
                }
                else
                {
                    SetFailure(venue.ProfileId, result.Failure);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<DevicePairingCodeResponse>> CreatePairingCodeAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            venue,
            async (baseUri, accessToken, token) =>
            {
                var result = await apiClient.CreateDevicePairingCodeAsync(baseUri, accessToken, token);
                if (result.Success && result.Value is not null)
                {
                    var current = GetSnapshot(venue);
                    snapshots[venue.ProfileId] = current with
                    {
                        Status = current.View is null ? SelfServiceStatus.NotLoaded : SelfServiceStatus.Ready,
                        Message = "Device pairing code created.",
                        LatestPairingCode = result.Value
                    };
                }
                else
                {
                    SetFailure(venue.ProfileId, result.Failure);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<SelfServiceOperationResponse>> UnauthorizeFromVenueAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            venue,
            async (baseUri, accessToken, token) =>
            {
                var result = await apiClient.UnauthorizeFromVenueAsync(baseUri, accessToken, token);
                if (!result.Success)
                {
                    SetFailure(venue.ProfileId, result.Failure);
                }

                return result;
            },
            cancellationToken);

    public void Clear(string message)
    {
        foreach (var key in snapshots.Keys)
        {
            snapshots[key] = new SelfServiceSnapshot(
                SelfServiceStatus.NotLoaded,
                message,
                null,
                null);
        }
    }

    public void RemoveProfile(Guid profileId) => snapshots.TryRemove(profileId, out _);

    public void Dispose()
    {
        foreach (var gate in gates.Values)
        {
            gate.Dispose();
        }

        gates.Clear();
        snapshots.Clear();
    }

    private async Task<ApiResult<T>> ExecuteAsync<T>(
        VenueConnectionConfiguration venue,
        Func<Uri, string, CancellationToken, Task<ApiResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        var gate = gates.GetOrAdd(venue.ProfileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!identityProvider.TryGetCurrent(out var identity, out var reason))
            {
                var failure = new ApiFailure(ApiFailureKind.Validation, "PLAYER_NOT_AVAILABLE", reason);
                SetFailure(venue.ProfileId, failure);
                return ApiResult<T>.Failed(failure);
            }

            if (!PartyPulseApiClient.TryCreateBaseUri(configuration.ApiBaseUrl, out var baseUri, out var urlError))
            {
                var failure = new ApiFailure(ApiFailureKind.Validation, "INVALID_API_URL", urlError);
                SetFailure(venue.ProfileId, failure);
                return ApiResult<T>.Failed(failure);
            }

            var access = await authentication.EnsureAccessTokenAsync(
                venue,
                identity!,
                configuration.ApiBaseUrl,
                cancellationToken);
            if (!access.Success || string.IsNullOrWhiteSpace(access.AccessToken))
            {
                var failure = access.Failure ?? new ApiFailure(
                    ApiFailureKind.Authentication,
                    "ACCESS_TOKEN_NOT_AVAILABLE",
                    "A valid access token is required.");
                SetFailure(venue.ProfileId, failure);
                return ApiResult<T>.Failed(failure);
            }

            return await operation(baseUri!, access.AccessToken, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ReloadWithinGateAsync(
        VenueConnectionConfiguration venue,
        Uri baseUri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.GetSelfServiceAsync(baseUri, accessToken, cancellationToken);
        if (result.Success && result.Value is not null)
        {
            var current = GetSnapshot(venue);
            snapshots[venue.ProfileId] = new SelfServiceSnapshot(
                SelfServiceStatus.Ready,
                "Your venue account is ready.",
                result.Value,
                current.LatestPairingCode);
        }
    }

    private void SetFailure(Guid profileId, ApiFailure? failure)
    {
        var message = failure?.Message ?? "The self-service request failed.";
        var current = snapshots.TryGetValue(profileId, out var snapshot)
            ? snapshot
            : SelfServiceSnapshot.NotLoaded;
        snapshots[profileId] = current with
        {
            Status = SelfServiceStatus.Failed,
            Message = message
        };

        log.Warning(
            "PartyPulse self-service request failed for profile {ProfileId}. Code: {Code}; trace: {TraceId}.",
            profileId,
            failure?.Code,
            failure?.TraceId);
    }
}
