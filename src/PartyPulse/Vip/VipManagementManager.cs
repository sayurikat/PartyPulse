using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Vip;

public sealed class VipManagementManager : IDisposable
{
    private static readonly TimeSpan FailedReloadDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeniedReloadDelay = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, VipManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public VipManagementManager(
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

    public VipManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : VipManagementSnapshot.NotLoaded;

    public bool IsBusy(Guid profileId) =>
        gates.TryGetValue(profileId, out var gate) && gate.CurrentCount == 0;

    public bool ShouldLoad(VenueConnectionConfiguration venue)
    {
        if (!venue.IsRegistered)
        {
            return false;
        }

        if (!snapshots.TryGetValue(venue.ProfileId, out var snapshot))
        {
            return true;
        }

        if (snapshot.Status is VipManagementStatus.Loading or VipManagementStatus.Ready)
        {
            return false;
        }

        var retryDelay = snapshot.Status == VipManagementStatus.Denied
            ? DeniedReloadDelay
            : FailedReloadDelay;

        return snapshot.LastAttemptAt is null ||
               snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(retryDelay);
    }

    public Task<ApiResult<VipManagementViewResponse>> LoadAsync(
        VenueConnectionConfiguration venue,
        bool force,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                if (!force && !ShouldLoad(venue))
                {
                    var existing = GetSnapshot(venue);
                    return existing.View is not null
                        ? ApiResult<VipManagementViewResponse>.Succeeded(existing.View)
                        : ApiResult<VipManagementViewResponse>.Failed(new ApiFailure(
                            existing.Status == VipManagementStatus.Denied
                                ? ApiFailureKind.Permission
                                : ApiFailureKind.Unknown,
                            "VIP_DATA_NOT_AVAILABLE",
                            existing.Message));
                }

                return await LoadCoreAsync(venue, cancellationToken);
            },
            cancellationToken);

    public Task<ApiResult<VipManagementViewResponse>> RefreshQuietlyAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var attemptAt = DateTimeOffset.UtcNow;
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    TouchSnapshotAttempt(venue.ProfileId, attemptAt);
                    return ApiResult<VipManagementViewResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.GetVipAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    cancellationToken);
                if (result.Success && result.Value is not null)
                {
                    snapshots[venue.ProfileId] = new VipManagementSnapshot(
                        VipManagementStatus.Ready,
                        "VIP data loaded.",
                        result.Value,
                        attemptAt);
                }
                else
                {
                    TouchSnapshotAttempt(venue.ProfileId, attemptAt);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<VipPackageOperationResponse>> CreatePackageAsync(
        VenueConnectionConfiguration venue,
        CreateVipPackageRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<VipPackageOperationResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.CreateVipPackageAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    request,
                    cancellationToken);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<VipPackageOperationResponse>> UpdatePackageAsync(
        VenueConnectionConfiguration venue,
        int packageId,
        UpdateVipPackageRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<VipPackageOperationResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.UpdateVipPackageAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    packageId,
                    request,
                    cancellationToken);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<SellVipSubscriptionResponse>> SellSubscriptionAsync(
        VenueConnectionConfiguration venue,
        SellVipSubscriptionRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<SellVipSubscriptionResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.SellVipSubscriptionAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    request,
                    cancellationToken);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<VipCharacterOperationResponse>> LinkCharacterAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        LinkVipCharacterRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<VipCharacterOperationResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.LinkVipCharacterAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    vipPlayerId,
                    request,
                    cancellationToken);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<VipPreferredCharacterResponse>> SetPreferredCharacterAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        int characterId,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<VipPreferredCharacterResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.SetVipPreferredCharacterAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    vipPlayerId,
                    characterId,
                    cancellationToken);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<VipPlayerOperationResponse>> UpdatePlayerAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        UpdateVipPlayerRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<VipPlayerOperationResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.UpdateVipPlayerAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    vipPlayerId,
                    request,
                    cancellationToken);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<VipCharacterOperationResponse>> UnlinkCharacterAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        int characterId,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<VipCharacterOperationResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.UnlinkVipCharacterAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    vipPlayerId,
                    characterId,
                    cancellationToken);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<VipSubscriptionCancellationResponse>> CancelSubscriptionAsync(
        VenueConnectionConfiguration venue,
        long subscriptionId,
        CancelVipSubscriptionRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<VipSubscriptionCancellationResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.CancelVipSubscriptionAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    subscriptionId,
                    request,
                    cancellationToken);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<VipSubscriptionPaymentStatusResponse>> SetSubscriptionPaymentStatusAsync(
        VenueConnectionConfiguration venue,
        long subscriptionId,
        SetVipSubscriptionPaymentStatusRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<VipSubscriptionPaymentStatusResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.SetVipSubscriptionPaymentStatusAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    subscriptionId,
                    request,
                    cancellationToken);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public void Clear(string message)
    {
        foreach (var pair in snapshots)
        {
            snapshots[pair.Key] = VipManagementSnapshot.NotLoaded with { Message = message };
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

    private async Task<ApiResult<VipManagementViewResponse>> LoadCoreAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        var attemptAt = DateTimeOffset.UtcNow;
        snapshots.AddOrUpdate(
            venue.ProfileId,
            _ => new VipManagementSnapshot(
                VipManagementStatus.Loading,
                "Loading VIP data...",
                null,
                attemptAt),
            (_, previous) => previous with
            {
                Status = VipManagementStatus.Loading,
                Message = "Loading VIP data...",
                LastAttemptAt = attemptAt,
            });

        var context = await GetAuthorizedContextAsync(venue, cancellationToken);
        if (!context.Success)
        {
            SetLoadFailure(venue, context.Failure!, attemptAt);
            return ApiResult<VipManagementViewResponse>.Failed(context.Failure!);
        }

        var result = await apiClient.GetVipAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        if (!result.Success || result.Value is null)
        {
            SetLoadFailure(venue, result.Failure!, attemptAt);
            return result;
        }

        snapshots[venue.ProfileId] = new VipManagementSnapshot(
            VipManagementStatus.Ready,
            "VIP data loaded.",
            result.Value,
            attemptAt);
        return result;
    }

    private async Task RefreshAfterMutationAsync(
        VenueConnectionConfiguration venue,
        AuthorizedContext context,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.GetVipAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);

        if (!result.Success || result.Value is null)
        {
            log.Warning(
                "VIP mutation succeeded but refresh failed for venue profile {ProfileId}: {Code} {Message}",
                venue.ProfileId,
                result.Failure?.Code,
                result.Failure?.Message);
            snapshots[venue.ProfileId] = VipManagementSnapshot.NotLoaded with
            {
                Message = "VIP data changed. Refresh to load the latest state."
            };
            return;
        }

        snapshots[venue.ProfileId] = new VipManagementSnapshot(
            VipManagementStatus.Ready,
            "VIP data loaded.",
            result.Value,
            DateTimeOffset.UtcNow);
    }

    private async Task<AuthorizedContext> GetAuthorizedContextAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        if (!venue.IsRegistered)
        {
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Authentication,
                "VENUE_NOT_REGISTERED",
                "This venue is saved in visitor mode and has no registered staff device."));
        }

        if (!identityProvider.TryGetCurrent(out var identity, out var identityError))
        {
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "PLAYER_NOT_AVAILABLE",
                identityError));
        }

        if (!PartyPulseApiClient.TryCreateBaseUri(configuration.ApiBaseUrl, out var baseUri, out var uriError))
        {
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "INVALID_API_BASE_URL",
                uriError));
        }

        var accessToken = await authentication.EnsureAccessTokenAsync(
            venue,
            identity!,
            configuration.ApiBaseUrl,
            cancellationToken);
        if (!accessToken.Success || string.IsNullOrWhiteSpace(accessToken.AccessToken))
        {
            return AuthorizedContext.Failed(accessToken.Failure ?? new ApiFailure(
                ApiFailureKind.Authentication,
                "ACCESS_TOKEN_NOT_AVAILABLE",
                "A valid access token could not be obtained."));
        }

        return AuthorizedContext.Succeeded(baseUri!, accessToken.AccessToken);
    }

    private void SetLoadFailure(
        VenueConnectionConfiguration venue,
        ApiFailure failure,
        DateTimeOffset attemptAt)
    {
        var status = failure.Kind == ApiFailureKind.Permission
            ? VipManagementStatus.Denied
            : VipManagementStatus.Failed;

        snapshots[venue.ProfileId] = new VipManagementSnapshot(
            status,
            failure.Message,
            null,
            attemptAt);
    }

    private void TouchSnapshotAttempt(Guid profileId, DateTimeOffset attemptAt)
    {
        if (snapshots.TryGetValue(profileId, out var existing))
        {
            snapshots[profileId] = existing with { LastAttemptAt = attemptAt };
        }
    }

    private async Task<T> WithGateAsync<T>(
        VenueConnectionConfiguration venue,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var gate = gates.GetOrAdd(venue.ProfileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record AuthorizedContext(
        bool Success,
        Uri? BaseUri,
        string? AccessToken,
        ApiFailure? Failure)
    {
        public static AuthorizedContext Succeeded(Uri baseUri, string accessToken) =>
            new(true, baseUri, accessToken, null);

        public static AuthorizedContext Failed(ApiFailure failure) =>
            new(false, null, null, failure);
    }
}
