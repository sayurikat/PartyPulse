using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Bar;

public sealed class BarManagementManager : IDisposable
{
    private static readonly TimeSpan FailedReloadDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeniedReloadDelay = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly ConcurrentDictionary<Guid, BarManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public BarManagementManager(
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

    public BarManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : BarManagementSnapshot.NotLoaded;

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

        if (snapshot.Status is BarManagementStatus.Loading or BarManagementStatus.Ready)
        {
            return false;
        }

        var retryDelay = snapshot.Status == BarManagementStatus.Denied
            ? DeniedReloadDelay
            : FailedReloadDelay;
        return snapshot.LastAttemptAt is null ||
               snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(retryDelay);
    }

    public Task<ApiResult<BarManagementViewResponse>> LoadAsync(
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
                        ? ApiResult<BarManagementViewResponse>.Succeeded(existing.View)
                        : ApiResult<BarManagementViewResponse>.Failed(new ApiFailure(
                            existing.Status == BarManagementStatus.Denied
                                ? ApiFailureKind.Permission
                                : ApiFailureKind.Unknown,
                            "BAR_NOT_AVAILABLE",
                            existing.Message));
                }

                return await LoadCoreAsync(venue, cancellationToken);
            },
            cancellationToken);

    public Task<ApiResult<BarBuyoutPackageOperationResponse>> CreateBuyoutPackageAsync(
        VenueConnectionConfiguration venue, CreateBarBuyoutPackageRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.CreateBarBuyoutPackageAsync(baseUri, accessToken, request, cancellationToken), cancellationToken);

    public Task<ApiResult<BarBuyoutPackageOperationResponse>> UpdateBuyoutPackageAsync(
        VenueConnectionConfiguration venue, long packageId, UpdateBarBuyoutPackageRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.UpdateBarBuyoutPackageAsync(baseUri, accessToken, packageId, request, cancellationToken), cancellationToken);

    public Task<ApiResult<UpdateBarSettingsResponse>> UpdateSettingsAsync(
        VenueConnectionConfiguration venue, UpdateBarSettingsRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.UpdateBarSettingsAsync(baseUri, accessToken, request, cancellationToken), cancellationToken);

    public Task<ApiResult<SellBarBuyoutResponse>> SellBuyoutAsync(
        VenueConnectionConfiguration venue, SellBarBuyoutRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.SellBarBuyoutAsync(baseUri, accessToken, request, cancellationToken), cancellationToken);

    public Task<ApiResult<BarSalePaymentStatusResponse>> SetBuyoutPaymentStatusAsync(
        VenueConnectionConfiguration venue, long saleId, SetBarSalePaymentStatusRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.SetBarBuyoutPaymentStatusAsync(baseUri, accessToken, saleId, request, cancellationToken), cancellationToken);

    public Task<ApiResult<BarSaleCancellationResponse>> CancelBuyoutAsync(
        VenueConnectionConfiguration venue, long saleId, CancelBarSaleRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.CancelBarBuyoutAsync(baseUri, accessToken, saleId, request, cancellationToken), cancellationToken);

    public Task<ApiResult<StartGambaGameResponse>> StartGambaGameAsync(
        VenueConnectionConfiguration venue, StartGambaGameRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.StartGambaGameAsync(baseUri, accessToken, request, cancellationToken), cancellationToken);

    public Task<ApiResult<SellGambaTicketsResponse>> SellGambaTicketsAsync(
        VenueConnectionConfiguration venue, SellGambaTicketsRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.SellGambaTicketsAsync(baseUri, accessToken, request, cancellationToken), cancellationToken);

    public Task<ApiResult<BarSalePaymentStatusResponse>> SetGambaTicketPaymentStatusAsync(
        VenueConnectionConfiguration venue, long saleId, SetBarSalePaymentStatusRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.SetGambaTicketPaymentStatusAsync(baseUri, accessToken, saleId, request, cancellationToken), cancellationToken);

    public Task<ApiResult<BarSaleCancellationResponse>> CancelGambaTicketSaleAsync(
        VenueConnectionConfiguration venue, long saleId, CancelBarSaleRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.CancelGambaTicketSaleAsync(baseUri, accessToken, saleId, request, cancellationToken), cancellationToken);

    public Task<ApiResult<CompleteGambaGameResponse>> CompleteGambaGameAsync(
        VenueConnectionConfiguration venue, long gameId, CompleteGambaGameRequest request, CancellationToken cancellationToken) =>
        MutateAsync(venue, (baseUri, accessToken) => apiClient.CompleteGambaGameAsync(baseUri, accessToken, gameId, request, cancellationToken), cancellationToken);

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

    private async Task<ApiResult<BarManagementViewResponse>> LoadCoreAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        snapshots[venue.ProfileId] = new BarManagementSnapshot(
            BarManagementStatus.Loading,
            "Loading bar...",
            null,
            attemptedAt);

        var context = await GetAuthorizedContextAsync(venue, cancellationToken);
        if (!context.Success)
        {
            snapshots[venue.ProfileId] = new BarManagementSnapshot(
                context.Failure!.Kind == ApiFailureKind.Permission
                    ? BarManagementStatus.Denied
                    : BarManagementStatus.Failed,
                context.Failure.Message,
                null,
                attemptedAt);
            return ApiResult<BarManagementViewResponse>.Failed(context.Failure);
        }

        var result = await apiClient.GetBarAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new BarManagementSnapshot(
                BarManagementStatus.Ready,
                "Bar loaded.",
                result.Value,
                attemptedAt)
            : new BarManagementSnapshot(
                result.Failure?.Kind == ApiFailureKind.Permission
                    ? BarManagementStatus.Denied
                    : BarManagementStatus.Failed,
                result.Failure?.Message ?? "Bar could not be loaded.",
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
        var result = await apiClient.GetBarAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new BarManagementSnapshot(
                BarManagementStatus.Ready,
                "Bar loaded.",
                result.Value,
                DateTimeOffset.UtcNow)
            : BarManagementSnapshot.NotLoaded with
            {
                Message = "Bar changed. Refresh to load the latest state."
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
