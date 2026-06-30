using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.OtherSales;

public sealed class OtherSalesManagementManager : IDisposable
{
    private static readonly TimeSpan FailedReloadDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeniedReloadDelay = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly ConcurrentDictionary<Guid, OtherSalesManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public OtherSalesManagementManager(
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

    public OtherSalesManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : OtherSalesManagementSnapshot.NotLoaded;

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

        if (snapshot.Status is OtherSalesManagementStatus.Loading or OtherSalesManagementStatus.Ready)
        {
            return false;
        }

        var retryDelay = snapshot.Status == OtherSalesManagementStatus.Denied
            ? DeniedReloadDelay
            : FailedReloadDelay;
        return snapshot.LastAttemptAt is null ||
               snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(retryDelay);
    }

    public Task<ApiResult<OtherSalesManagementViewResponse>> LoadAsync(
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
                        ? ApiResult<OtherSalesManagementViewResponse>.Succeeded(existing.View)
                        : ApiResult<OtherSalesManagementViewResponse>.Failed(new ApiFailure(
                            existing.Status == OtherSalesManagementStatus.Denied
                                ? ApiFailureKind.Permission
                                : ApiFailureKind.Unknown,
                            "OTHER_SALES_NOT_AVAILABLE",
                            existing.Message));
                }

                return await LoadCoreAsync(venue, cancellationToken);
            },
            cancellationToken);

    public Task<ApiResult<OtherSaleItemOperationResponse>> CreateItemAsync(
        VenueConnectionConfiguration venue,
        CreateOtherSaleItemRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.CreateOtherSaleItemAsync(baseUri, accessToken, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<OtherSaleItemOperationResponse>> UpdateItemAsync(
        VenueConnectionConfiguration venue,
        int itemId,
        UpdateOtherSaleItemRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.UpdateOtherSaleItemAsync(baseUri, accessToken, itemId, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<UpdateOtherSaleSellerPercentageResponse>> UpdateSellerPercentageAsync(
        VenueConnectionConfiguration venue,
        int itemId,
        UpdateOtherSaleSellerPercentageRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.UpdateOtherSaleSellerPercentageAsync(baseUri, accessToken, itemId, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<SellOtherSaleResponse>> SellAsync(
        VenueConnectionConfiguration venue,
        SellOtherSaleRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.SellOtherSaleAsync(baseUri, accessToken, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<OtherSalePaymentStatusResponse>> SetSalePaymentStatusAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        SetOtherSalePaymentStatusRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.SetOtherSalePaymentStatusAsync(baseUri, accessToken, saleId, request, cancellationToken),
            cancellationToken);

    public Task<ApiResult<OtherSaleCancellationResponse>> CancelSaleAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        CancelOtherSaleRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) =>
            apiClient.CancelOtherSaleAsync(baseUri, accessToken, saleId, request, cancellationToken),
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

    private async Task<ApiResult<OtherSalesManagementViewResponse>> LoadCoreAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        snapshots[venue.ProfileId] = new OtherSalesManagementSnapshot(
            OtherSalesManagementStatus.Loading,
            "Loading Other Sales...",
            null,
            attemptedAt);

        var context = await GetAuthorizedContextAsync(venue, cancellationToken);
        if (!context.Success)
        {
            snapshots[venue.ProfileId] = new OtherSalesManagementSnapshot(
                context.Failure!.Kind == ApiFailureKind.Permission
                    ? OtherSalesManagementStatus.Denied
                    : OtherSalesManagementStatus.Failed,
                context.Failure.Message,
                null,
                attemptedAt);
            return ApiResult<OtherSalesManagementViewResponse>.Failed(context.Failure);
        }

        var result = await apiClient.GetOtherSalesAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new OtherSalesManagementSnapshot(
                OtherSalesManagementStatus.Ready,
                "Other Sales loaded.",
                result.Value,
                attemptedAt)
            : new OtherSalesManagementSnapshot(
                result.Failure?.Kind == ApiFailureKind.Permission
                    ? OtherSalesManagementStatus.Denied
                    : OtherSalesManagementStatus.Failed,
                result.Failure?.Message ?? "Other Sales could not be loaded.",
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
        var result = await apiClient.GetOtherSalesAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new OtherSalesManagementSnapshot(
                OtherSalesManagementStatus.Ready,
                "Other Sales loaded.",
                result.Value,
                DateTimeOffset.UtcNow)
            : OtherSalesManagementSnapshot.NotLoaded with
            {
                Message = "Other Sales changed. Refresh to load the latest state."
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
