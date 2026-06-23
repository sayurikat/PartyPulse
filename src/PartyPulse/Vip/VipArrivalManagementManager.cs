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

public sealed class VipArrivalManagementManager : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, VipArrivalManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, VipNewMemberOffer> newMemberOffers = new();

    public VipArrivalManagementManager(
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

    public VipArrivalManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : VipArrivalManagementSnapshot.NotLoaded;

    public bool IsBusy(Guid profileId) =>
        gates.TryGetValue(profileId, out var gate) && gate.CurrentCount == 0;

    public bool ShouldLoad(VenueConnectionConfiguration venue)
    {
        if (!venue.IsRegistered) return false;
        var snapshot = GetSnapshot(venue);
        if (snapshot.Status == VipArrivalManagementStatus.Loading) return false;
        if (snapshot.Status == VipArrivalManagementStatus.NotLoaded) return true;
        return snapshot.LastAttemptAt is null || snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(PollInterval);
    }

    public Task<ApiResult<VipArrivalContextResponse>> LoadAsync(
        VenueConnectionConfiguration venue,
        bool force,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            if (!force && !ShouldLoad(venue))
            {
                var existing = GetSnapshot(venue);
                return existing.Context is not null
                    ? ApiResult<VipArrivalContextResponse>.Succeeded(existing.Context)
                    : ApiResult<VipArrivalContextResponse>.Failed(new ApiFailure(
                        ApiFailureKind.Unknown, "VIP_ARRIVAL_DATA_NOT_AVAILABLE", existing.Message));
            }

            var attemptAt = DateTimeOffset.UtcNow;
            snapshots[venue.ProfileId] = new VipArrivalManagementSnapshot(
                VipArrivalManagementStatus.Loading, "Loading VIP arrival data...", GetSnapshot(venue).Context, attemptAt);
            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            if (!context.Success)
            {
                SetFailure(venue, context.Failure!, attemptAt);
                return ApiResult<VipArrivalContextResponse>.Failed(context.Failure!);
            }

            var result = await apiClient.GetVipArrivalContextAsync(
                context.BaseUri!, context.AccessToken!, cancellationToken);
            if (!result.Success || result.Value is null)
            {
                SetFailure(venue, result.Failure!, attemptAt);
                return result;
            }

            snapshots[venue.ProfileId] = new VipArrivalManagementSnapshot(
                VipArrivalManagementStatus.Ready, "VIP arrival data loaded.", result.Value, attemptAt);
            return result;
        }, cancellationToken);

    public Task<ApiResult<ObserveVipArrivalsResponse>> ObserveAsync(
        VenueConnectionConfiguration venue,
        ObserveVipArrivalsRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.ObserveVipArrivalsAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<ObserveVipArrivalsResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<RecordVipArrivalActionResponse>> RecordActionAsync(
        VenueConnectionConfiguration venue,
        int vipPlayerId,
        RecordVipArrivalActionRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.RecordVipArrivalActionAsync(
                    context.BaseUri!, context.AccessToken!, vipPlayerId, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<RecordVipArrivalActionResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<UpdateVenueMacroResponse>> UpdateMacroAsync(
        VenueConnectionConfiguration venue,
        string macroCode,
        UpdateVenueMacroRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.UpdateVenueMacroAsync(
                    context.BaseUri!, context.AccessToken!, macroCode, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<UpdateVenueMacroResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<VenueOpeningSummary>> StartTemporaryOpeningAsync(
        VenueConnectionConfiguration venue,
        StartTemporaryOpeningRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.StartTemporaryVenueOpeningAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<VenueOpeningSummary>.Failed,
            cancellationToken);

    public Task<ApiResult<CloseVenueOpeningResponse>> CloseOpeningAsync(
        VenueConnectionConfiguration venue,
        long openingId,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.CloseVenueOpeningAsync(
                    context.BaseUri!, context.AccessToken!, openingId, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<CloseVenueOpeningResponse>.Failed,
            cancellationToken);

    public void SetNewMemberOffer(VipNewMemberOffer offer) => newMemberOffers[offer.VenueProfileId] = offer;

    public bool TryGetNewMemberOffer(Guid profileId, out VipNewMemberOffer? offer)
    {
        if (newMemberOffers.TryGetValue(profileId, out var value))
        {
            offer = value;
            return true;
        }
        offer = null;
        return false;
    }

    public void ClearNewMemberOffer(Guid profileId) => newMemberOffers.TryRemove(profileId, out _);

    public void ClearProfile(Guid profileId)
    {
        snapshots.TryRemove(profileId, out _);
        newMemberOffers.TryRemove(profileId, out _);
    }

    public void Clear()
    {
        snapshots.Clear();
        newMemberOffers.Clear();
    }

    private async Task RefreshCoreAsync(
        VenueConnectionConfiguration venue,
        AuthorizedContext context,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.GetVipArrivalContextAsync(
            context.BaseUri!, context.AccessToken!, cancellationToken);
        if (result.Success && result.Value is not null)
        {
            snapshots[venue.ProfileId] = new VipArrivalManagementSnapshot(
                VipArrivalManagementStatus.Ready, "VIP arrival data loaded.", result.Value, DateTimeOffset.UtcNow);
        }
        else
        {
            log.Warning("VIP arrival mutation succeeded but context refresh failed: {Code} {Message}",
                result.Failure?.Code, result.Failure?.Message);
            snapshots[venue.ProfileId] = VipArrivalManagementSnapshot.NotLoaded with
            {
                Message = "VIP arrival data changed. Refresh to load the latest state."
            };
        }
    }

    private Task<T> WithAuthorizedMutationAsync<T>(
        VenueConnectionConfiguration venue,
        Func<AuthorizedContext, Task<T>> operation,
        Func<ApiFailure, T> failureFactory,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            return context.Success
                ? await operation(context)
                : failureFactory(context.Failure!);
        }, cancellationToken);

    private async Task<AuthorizedContext> GetAuthorizedContextAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        if (!venue.IsRegistered)
            return AuthorizedContext.Failed(new ApiFailure(ApiFailureKind.Authentication, "VENUE_NOT_REGISTERED", "This venue is not registered on this device."));
        if (!identityProvider.TryGetCurrent(out var identity, out var identityError))
            return AuthorizedContext.Failed(new ApiFailure(ApiFailureKind.Validation, "PLAYER_NOT_AVAILABLE", identityError));
        if (!PartyPulseApiClient.TryCreateBaseUri(configuration.ApiBaseUrl, out var baseUri, out var uriError))
            return AuthorizedContext.Failed(new ApiFailure(ApiFailureKind.Validation, "INVALID_API_BASE_URL", uriError));
        var token = await authentication.EnsureAccessTokenAsync(
            venue, identity!, configuration.ApiBaseUrl, cancellationToken);
        return token.Success && !string.IsNullOrWhiteSpace(token.AccessToken)
            ? AuthorizedContext.Succeeded(baseUri!, token.AccessToken)
            : AuthorizedContext.Failed(token.Failure ?? new ApiFailure(
                ApiFailureKind.Authentication, "ACCESS_TOKEN_NOT_AVAILABLE", "A valid access token could not be obtained."));
    }

    private void SetFailure(VenueConnectionConfiguration venue, ApiFailure failure, DateTimeOffset at)
    {
        snapshots[venue.ProfileId] = new VipArrivalManagementSnapshot(
            failure.Kind == ApiFailureKind.Permission ? VipArrivalManagementStatus.Denied : VipArrivalManagementStatus.Failed,
            failure.Message, null, at);
    }

    private async Task<T> WithGateAsync<T>(
        VenueConnectionConfiguration venue,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var gate = gates.GetOrAdd(venue.ProfileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await operation(); }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        foreach (var gate in gates.Values) gate.Dispose();
        gates.Clear();
        snapshots.Clear();
        newMemberOffers.Clear();
    }

    private sealed record AuthorizedContext(bool Success, Uri? BaseUri, string? AccessToken, ApiFailure? Failure)
    {
        public static AuthorizedContext Succeeded(Uri baseUri, string token) => new(true, baseUri, token, null);
        public static AuthorizedContext Failed(ApiFailure failure) => new(false, null, null, failure);
    }
}
