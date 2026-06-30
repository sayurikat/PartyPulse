using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Purchases;

public sealed class PurchaseManagementManager : IDisposable
{
    private static readonly TimeSpan FailedReloadDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeniedReloadDelay = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly ConcurrentDictionary<Guid, PurchaseManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public PurchaseManagementManager(
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

    public PurchaseManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : PurchaseManagementSnapshot.NotLoaded;

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

        if (snapshot.Status is PurchaseManagementStatus.Loading or PurchaseManagementStatus.Ready)
        {
            return false;
        }

        var retryDelay = snapshot.Status == PurchaseManagementStatus.Denied
            ? DeniedReloadDelay
            : FailedReloadDelay;
        return snapshot.LastAttemptAt is null ||
               snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(retryDelay);
    }

    public Task<ApiResult<PurchasesManagementViewResponse>> LoadAsync(
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
                        ? ApiResult<PurchasesManagementViewResponse>.Succeeded(existing.View)
                        : ApiResult<PurchasesManagementViewResponse>.Failed(new ApiFailure(
                            existing.Status == PurchaseManagementStatus.Denied
                                ? ApiFailureKind.Permission
                                : ApiFailureKind.Unknown,
                            "PURCHASES_NOT_AVAILABLE",
                            existing.Message));
                }

                return await LoadCoreAsync(venue, cancellationToken);
            },
            cancellationToken);

    public Task<ApiResult<CreatePurchaseResponse>> CreateAsync(
        VenueConnectionConfiguration venue,
        CreatePurchaseRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.CreatePurchaseAsync(
                baseUri,
                accessToken,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<PurchaseStateChangeResponse>> ApproveAsync(
        VenueConnectionConfiguration venue,
        long purchaseId,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.ApprovePurchaseAsync(
                baseUri,
                accessToken,
                purchaseId,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<PurchaseStateChangeResponse>> ConfirmPaidAsync(
        VenueConnectionConfiguration venue,
        long purchaseId,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.ConfirmPurchasePaidAsync(
                baseUri,
                accessToken,
                purchaseId,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<PurchaseStateChangeResponse>> RejectAsync(
        VenueConnectionConfiguration venue,
        long purchaseId,
        RejectPurchaseRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.RejectPurchaseAsync(
                baseUri,
                accessToken,
                purchaseId,
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

    private async Task<ApiResult<PurchasesManagementViewResponse>> LoadCoreAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        snapshots[venue.ProfileId] = new PurchaseManagementSnapshot(
            PurchaseManagementStatus.Loading,
            "Loading Purchases...",
            null,
            attemptedAt);

        var context = await GetAuthorizedContextAsync(venue, cancellationToken);
        if (!context.Success)
        {
            snapshots[venue.ProfileId] = new PurchaseManagementSnapshot(
                context.Failure!.Kind == ApiFailureKind.Permission
                    ? PurchaseManagementStatus.Denied
                    : PurchaseManagementStatus.Failed,
                context.Failure.Message,
                null,
                attemptedAt);
            return ApiResult<PurchasesManagementViewResponse>.Failed(context.Failure);
        }

        var result = await apiClient.GetPurchasesAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new PurchaseManagementSnapshot(
                PurchaseManagementStatus.Ready,
                "Purchases loaded.",
                result.Value,
                attemptedAt)
            : new PurchaseManagementSnapshot(
                result.Failure?.Kind == ApiFailureKind.Permission
                    ? PurchaseManagementStatus.Denied
                    : PurchaseManagementStatus.Failed,
                result.Failure?.Message ?? "Purchases could not be loaded.",
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
        var result = await apiClient.GetPurchasesAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new PurchaseManagementSnapshot(
                PurchaseManagementStatus.Ready,
                "Purchases loaded.",
                result.Value,
                DateTimeOffset.UtcNow)
            : PurchaseManagementSnapshot.NotLoaded with
            {
                Message = "Purchases changed. Refresh to load the latest state."
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
