using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Court;

public sealed class CourtManagementManager : IDisposable
{
    private static readonly TimeSpan FailedReloadDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeniedReloadDelay = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly ConcurrentDictionary<Guid, CourtManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public CourtManagementManager(
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

    public CourtManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : CourtManagementSnapshot.NotLoaded;

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

        if (snapshot.Status is CourtManagementStatus.Loading or CourtManagementStatus.Ready)
        {
            return false;
        }

        var retryDelay = snapshot.Status == CourtManagementStatus.Denied
            ? DeniedReloadDelay
            : FailedReloadDelay;
        return snapshot.LastAttemptAt is null ||
               snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(retryDelay);
    }

    public Task<ApiResult<CourtManagementViewResponse>> LoadAsync(
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
                        ? ApiResult<CourtManagementViewResponse>.Succeeded(existing.View)
                        : ApiResult<CourtManagementViewResponse>.Failed(new ApiFailure(
                            existing.Status == CourtManagementStatus.Denied
                                ? ApiFailureKind.Permission
                                : ApiFailureKind.Unknown,
                            "COURT_NOT_AVAILABLE",
                            existing.Message));
                }

                return await LoadCoreAsync(venue, cancellationToken);
            },
            cancellationToken);

    public Task<ApiResult<CourtOfferOperationResponse>> SaveOfferAsync(
        VenueConnectionConfiguration venue,
        long? offerId,
        SaveCourtOfferRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => offerId is null
                ? apiClient.CreateCourtOfferAsync(
                    baseUri,
                    accessToken,
                    request,
                    cancellationToken)
                : apiClient.UpdateCourtOfferAsync(
                    baseUri,
                    accessToken,
                    offerId.Value,
                    request,
                    cancellationToken),
            cancellationToken);

    public Task<ApiResult<SellCourtServiceResponse>> SellAsync(
        VenueConnectionConfiguration venue,
        SellCourtServiceRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.SellCourtServiceAsync(
                baseUri,
                accessToken,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<CourtSaleCancellationResponse>> CancelSaleAsync(
        VenueConnectionConfiguration venue,
        long saleId,
        CancelCourtSaleRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.CancelCourtSaleAsync(
                baseUri,
                accessToken,
                saleId,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<CourtFinancialTransactionResponse>> CreateStaffSettlementAsync(
        VenueConnectionConfiguration venue,
        CreateCourtStaffSettlementRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.CreateCourtStaffSettlementAsync(
                baseUri,
                accessToken,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<CourtFinancialTransactionResponse>> CreateAccountantPrepayAsync(
        VenueConnectionConfiguration venue,
        CreateCourtAccountantPrepayRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.CreateCourtAccountantPrepayAsync(
                baseUri,
                accessToken,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<CourtFinancialTransactionResponse>> CreateAccountantFinalizationAsync(
        VenueConnectionConfiguration venue,
        CreateCourtAccountantFinalizationRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.CreateCourtAccountantFinalizationAsync(
                baseUri,
                accessToken,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<CourtTransactionConfirmationResponse>> ConfirmTransactionAsync(
        VenueConnectionConfiguration venue,
        long transactionId,
        ConfirmCourtTransactionRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.ConfirmCourtTransactionAsync(
                baseUri,
                accessToken,
                transactionId,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<CourtTransactionCancellationResponse>> CancelTransactionAsync(
        VenueConnectionConfiguration venue,
        long transactionId,
        CancelCourtTransactionRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.CancelCourtTransactionAsync(
                baseUri,
                accessToken,
                transactionId,
                request,
                cancellationToken),
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

    private async Task<ApiResult<CourtManagementViewResponse>> LoadCoreAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        snapshots[venue.ProfileId] = new CourtManagementSnapshot(
            CourtManagementStatus.Loading,
            "Loading Court Services...",
            null,
            attemptedAt);

        var context = await GetAuthorizedContextAsync(venue, cancellationToken);
        if (!context.Success)
        {
            snapshots[venue.ProfileId] = new CourtManagementSnapshot(
                context.Failure!.Kind == ApiFailureKind.Permission
                    ? CourtManagementStatus.Denied
                    : CourtManagementStatus.Failed,
                context.Failure.Message,
                null,
                attemptedAt);
            return ApiResult<CourtManagementViewResponse>.Failed(context.Failure);
        }

        var result = await apiClient.GetCourtAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new CourtManagementSnapshot(
                CourtManagementStatus.Ready,
                "Court Services loaded.",
                result.Value,
                attemptedAt)
            : new CourtManagementSnapshot(
                result.Failure?.Kind == ApiFailureKind.Permission
                    ? CourtManagementStatus.Denied
                    : CourtManagementStatus.Failed,
                result.Failure?.Message ?? "Court Services could not be loaded.",
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
        var result = await apiClient.GetCourtAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new CourtManagementSnapshot(
                CourtManagementStatus.Ready,
                "Court Services loaded.",
                result.Value,
                DateTimeOffset.UtcNow)
            : CourtManagementSnapshot.NotLoaded with
            {
                Message = "Court Services changed. Refresh to load the latest state."
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
