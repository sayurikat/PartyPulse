using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.OtherGames;

public sealed class OtherGamesManagementManager : IDisposable
{
    private static readonly TimeSpan FailedReloadDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeniedReloadDelay = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly ConcurrentDictionary<Guid, OtherGamesManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public OtherGamesManagementManager(
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

    public OtherGamesManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : OtherGamesManagementSnapshot.NotLoaded;

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

        if (snapshot.Status is OtherGamesManagementStatus.Loading or OtherGamesManagementStatus.Ready)
        {
            return false;
        }

        var retryDelay = snapshot.Status == OtherGamesManagementStatus.Denied
            ? DeniedReloadDelay
            : FailedReloadDelay;
        return snapshot.LastAttemptAt is null ||
               snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(retryDelay);
    }

    public Task<ApiResult<OtherGamesManagementViewResponse>> LoadAsync(
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
                        ? ApiResult<OtherGamesManagementViewResponse>.Succeeded(existing.View)
                        : ApiResult<OtherGamesManagementViewResponse>.Failed(new ApiFailure(
                            existing.Status == OtherGamesManagementStatus.Denied
                                ? ApiFailureKind.Permission
                                : ApiFailureKind.Unknown,
                            "OTHER_GAMES_NOT_AVAILABLE",
                            existing.Message));
                }

                return await LoadCoreAsync(venue, cancellationToken);
            },
            cancellationToken);

    public Task<ApiResult<OtherGameItemOperationResponse>> CreateItemAsync(
        VenueConnectionConfiguration venue,
        CreateOtherGameItemRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.CreateOtherGameItemAsync(baseUri, accessToken, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<OtherGameItemOperationResponse>> UpdateItemAsync(
        VenueConnectionConfiguration venue,
        int itemId,
        UpdateOtherGameItemRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.UpdateOtherGameItemAsync(baseUri, accessToken, itemId, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<UpdateOtherGameSellerPercentageResponse>> UpdateSellerPercentageAsync(
        VenueConnectionConfiguration venue,
        int itemId,
        UpdateOtherGameSellerPercentageRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.UpdateOtherGameSellerPercentageAsync(baseUri, accessToken, itemId, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<SellOtherGameResponse>> SellAsync(
        VenueConnectionConfiguration venue,
        SellOtherGameRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.SellOtherGameAsync(baseUri, accessToken, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<OtherGameOutcomeResponse>> SetOutcomeAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        SetOtherGameOutcomeRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.SetOtherGameOutcomeAsync(baseUri, accessToken, saleId, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<OtherGameSettlementStatusResponse>> SetSaleSettlementStatusAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        SetOtherGameSettlementStatusRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.SetOtherGameSettlementStatusAsync(baseUri, accessToken, saleId, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<OtherGameSaleCancellationResponse>> CancelSaleAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        CancelOtherGameSaleRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.CancelOtherGameSaleAsync(baseUri, accessToken, saleId, request, cancellationToken),
            cancellationToken);

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

    private async Task<ApiResult<OtherGamesManagementViewResponse>> LoadCoreAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        snapshots[venue.ProfileId] = new OtherGamesManagementSnapshot(
            OtherGamesManagementStatus.Loading,
            "Loading Other Games...",
            null,
            attemptedAt);

        var context = await GetAuthorizedContextAsync(venue, cancellationToken);
        if (!context.Success)
        {
            snapshots[venue.ProfileId] = new OtherGamesManagementSnapshot(
                context.Failure!.Kind == ApiFailureKind.Permission
                    ? OtherGamesManagementStatus.Denied
                    : OtherGamesManagementStatus.Failed,
                context.Failure.Message,
                null,
                attemptedAt);
            return ApiResult<OtherGamesManagementViewResponse>.Failed(context.Failure);
        }

        var result = await apiClient.GetOtherGamesAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new OtherGamesManagementSnapshot(
                OtherGamesManagementStatus.Ready,
                "Other Games loaded.",
                result.Value,
                attemptedAt)
            : new OtherGamesManagementSnapshot(
                result.Failure?.Kind == ApiFailureKind.Permission
                    ? OtherGamesManagementStatus.Denied
                    : OtherGamesManagementStatus.Failed,
                result.Failure?.Message ?? "Other Games could not be loaded.",
                null,
                attemptedAt);
        return result;
    }

    private Task<ApiResult<T>> MutateAsync<T>(
        VenueConnectionConfiguration venue,
        Func<Uri, string, Task<ApiResult<T>>> operation,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<T>.Failed(context.Failure!);
                }

                var result = await operation(context.BaseUri!, context.AccessToken!);
                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    private async Task RefreshAfterMutationAsync(
        VenueConnectionConfiguration venue,
        AuthorizedContext context,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.GetOtherGamesAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new OtherGamesManagementSnapshot(
                OtherGamesManagementStatus.Ready,
                "Other Games loaded.",
                result.Value,
                DateTimeOffset.UtcNow)
            : OtherGamesManagementSnapshot.NotLoaded with
            {
                Message = "Other Games changed. Refresh to load the latest state."
            };
    }

    private async Task<AuthorizedContext> GetAuthorizedContextAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        if (!venue.IsRegistered)
        {
            return AuthorizedContext.Fail(new ApiFailure(
                ApiFailureKind.Authentication,
                "VENUE_NOT_REGISTERED",
                "This venue has no registered staff device."));
        }

        if (!identityProvider.TryGetCurrent(out var identity, out var identityError))
        {
            return AuthorizedContext.Fail(new ApiFailure(
                ApiFailureKind.Validation,
                "PLAYER_NOT_AVAILABLE",
                identityError));
        }

        if (!PartyPulseApiClient.TryCreateBaseUri(
                configuration.ApiBaseUrl,
                out var baseUri,
                out var uriError))
        {
            return AuthorizedContext.Fail(new ApiFailure(
                ApiFailureKind.Validation,
                "INVALID_API_BASE_URL",
                uriError));
        }

        var access = await authentication.EnsureAccessTokenAsync(
            venue,
            identity!,
            configuration.ApiBaseUrl,
            cancellationToken);
        return access.Success && !string.IsNullOrWhiteSpace(access.AccessToken)
            ? AuthorizedContext.Ok(baseUri!, access.AccessToken)
            : AuthorizedContext.Fail(access.Failure ?? new ApiFailure(
                ApiFailureKind.Authentication,
                "ACCESS_TOKEN_NOT_AVAILABLE",
                "A valid access token could not be obtained."));
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
        public static AuthorizedContext Ok(Uri baseUri, string accessToken) =>
            new(true, baseUri, accessToken, null);

        public static AuthorizedContext Fail(ApiFailure failure) =>
            new(false, null, null, failure);
    }
}
