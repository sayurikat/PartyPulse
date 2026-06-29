using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Finance;

public sealed class FinanceManagementManager : IDisposable
{
    private static readonly TimeSpan FailedReloadDelay = TimeSpan.FromSeconds(30);
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly ConcurrentDictionary<Guid, FinanceManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public FinanceManagementManager(
        Configuration configuration,
        AuthenticationManager authentication,
        PartyPulseApiClient apiClient,
        PlayerIdentityProvider identityProvider)
    {
        this.configuration = configuration;
        this.authentication = authentication;
        this.apiClient = apiClient;
        this.identityProvider = identityProvider;
    }

    public FinanceManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : FinanceManagementSnapshot.NotLoaded;

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

        if (snapshot.Status is FinanceManagementStatus.Loading or FinanceManagementStatus.Ready)
        {
            return false;
        }

        return snapshot.LastAttemptAt is null ||
               snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(FailedReloadDelay);
    }

    public Task<ApiResult<FinanceViewResponse>> LoadAsync(
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
                        ? ApiResult<FinanceViewResponse>.Succeeded(existing.View)
                        : ApiResult<FinanceViewResponse>.Failed(new ApiFailure(
                            ApiFailureKind.Unknown,
                            "FINANCE_DATA_NOT_AVAILABLE",
                            existing.Message));
                }

                return await LoadCoreAsync(venue, cancellationToken);
            },
            cancellationToken);

    public Task<ApiResult<CreateVipSettlementResponse>> CreateVipSettlementAsync(
        VenueConnectionConfiguration venue,
        CreateVipSettlementRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<CreateVipSettlementResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.CreateVipSettlementAsync(
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

    public Task<ApiResult<CreatePhotoshootSettlementResponse>> CreatePhotoshootSettlementAsync(
        VenueConnectionConfiguration venue,
        CreatePhotoshootSettlementRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                    return ApiResult<CreatePhotoshootSettlementResponse>.Failed(context.Failure!);
                var result = await apiClient.CreatePhotoshootSettlementAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success)
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                return result;
            },
            cancellationToken);

    public Task<ApiResult<CreateBarSettlementResponse>> CreateBarSettlementAsync(
        VenueConnectionConfiguration venue,
        CreateBarSettlementRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                    return ApiResult<CreateBarSettlementResponse>.Failed(context.Failure!);
                var result = await apiClient.CreateBarSettlementAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success)
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                return result;
            },
            cancellationToken);

    public Task<ApiResult<RespondSettlementResponse>> RespondSettlementAsync(
        VenueConnectionConfiguration venue,
        long settlementId,
        RespondSettlementRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<RespondSettlementResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.RespondSettlementAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    settlementId,
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
            snapshots[pair.Key] = FinanceManagementSnapshot.NotLoaded with { Message = message };
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

    private async Task<ApiResult<FinanceViewResponse>> LoadCoreAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        var attemptAt = DateTimeOffset.UtcNow;
        snapshots[venue.ProfileId] = new FinanceManagementSnapshot(
            FinanceManagementStatus.Loading,
            "Loading finance data...",
            null,
            attemptAt);

        var context = await GetAuthorizedContextAsync(venue, cancellationToken);
        if (!context.Success)
        {
            snapshots[venue.ProfileId] = new FinanceManagementSnapshot(
                FinanceManagementStatus.Failed,
                context.Failure!.Message,
                null,
                attemptAt);
            return ApiResult<FinanceViewResponse>.Failed(context.Failure);
        }

        var result = await apiClient.GetFinanceAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        if (!result.Success || result.Value is null)
        {
            snapshots[venue.ProfileId] = new FinanceManagementSnapshot(
                FinanceManagementStatus.Failed,
                result.Failure?.Message ?? "Finance data could not be loaded.",
                null,
                attemptAt);
            return result;
        }

        snapshots[venue.ProfileId] = new FinanceManagementSnapshot(
            FinanceManagementStatus.Ready,
            "Finance data loaded.",
            result.Value,
            attemptAt);
        return result;
    }

    private async Task RefreshAfterMutationAsync(
        VenueConnectionConfiguration venue,
        AuthorizedContext context,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.GetFinanceAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new FinanceManagementSnapshot(
                FinanceManagementStatus.Ready,
                "Finance data loaded.",
                result.Value,
                DateTimeOffset.UtcNow)
            : FinanceManagementSnapshot.NotLoaded with
            {
                Message = "Finance data changed. Refresh to load the latest state."
            };
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
                "This venue has no registered staff device."));
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
