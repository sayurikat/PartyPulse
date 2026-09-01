using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Djs;

public sealed class DjManagementManager : IDisposable
{
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, DjManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> lastSuccessfulBookingSaveAt = new();

    public DjManagementManager(
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

    public DjManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : DjManagementSnapshot.NotLoaded;

    public bool IsBusy(Guid profileId) =>
        gates.TryGetValue(profileId, out var gate) && gate.CurrentCount == 0;

    public DateTimeOffset? GetLastSuccessfulBookingSaveAt(Guid profileId) =>
        lastSuccessfulBookingSaveAt.TryGetValue(profileId, out var value) ? value : null;

    public bool ShouldLoad(VenueConnectionConfiguration venue) =>
        venue.IsRegistered && GetSnapshot(venue).Status == DjManagementStatus.NotLoaded;

    public Task<ApiResult<DjViewResponse>> LoadAsync(
        VenueConnectionConfiguration venue,
        bool force,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            var existing = GetSnapshot(venue);
            if (!force && existing.Status == DjManagementStatus.Ready && existing.View is not null)
                return ApiResult<DjViewResponse>.Succeeded(existing.View);

            var attemptAt = DateTimeOffset.UtcNow;
            snapshots[venue.ProfileId] = new DjManagementSnapshot(
                DjManagementStatus.Loading,
                existing.View is null ? "Loading DJs..." : "Refreshing DJs...",
                existing.View,
                attemptAt);

            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            if (!context.Success)
            {
                SetFailure(venue, context.Failure!, attemptAt);
                return ApiResult<DjViewResponse>.Failed(context.Failure!);
            }

            var result = await apiClient.GetDjsAsync(
                context.BaseUri!, context.AccessToken!, cancellationToken);
            if (!result.Success || result.Value is null)
            {
                SetFailure(venue, result.Failure!, attemptAt);
                return result;
            }

            snapshots[venue.ProfileId] = new DjManagementSnapshot(
                DjManagementStatus.Ready,
                "DJ data loaded.",
                result.Value,
                attemptAt);
            return result;
        }, cancellationToken);

    public Task<ApiResult<DjSummary>> SaveDjAsync(
        VenueConnectionConfiguration venue,
        long? djId,
        SaveDjRequest request,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.SaveDjAsync(
                    context.BaseUri!, context.AccessToken!, djId, request, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<DjSummary>.Failed,
            cancellationToken);

    public Task<ApiResult<ArchiveDjResponse>> ArchiveDjAsync(
        VenueConnectionConfiguration venue,
        long djId,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.ArchiveDjAsync(
                    context.BaseUri!, context.AccessToken!, djId, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<ArchiveDjResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<UpdateDjSettingsResponse>> UpdateSettingsAsync(
        VenueConnectionConfiguration venue,
        UpdateDjSettingsRequest request,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.UpdateDjSettingsAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<UpdateDjSettingsResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<DjCharacterLinkResponse>> LinkCharacterAsync(
        VenueConnectionConfiguration venue,
        LinkDjCharacterRequest request,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.LinkDjCharacterAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<DjCharacterLinkResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<DjBookingSummary>> SaveBookingAsync(
        VenueConnectionConfiguration venue,
        long? bookingId,
        SaveDjBookingRequest request,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.SaveDjBookingAsync(
                    context.BaseUri!, context.AccessToken!, bookingId, request, cancellationToken);
                if (result.Success)
                {
                    if (await RefreshCoreAsync(venue, context, cancellationToken))
                        lastSuccessfulBookingSaveAt[venue.ProfileId] = DateTimeOffset.UtcNow;
                }
                return result;
            },
            ApiResult<DjBookingSummary>.Failed,
            cancellationToken);

    public Task<ApiResult<DeleteDjBookingResponse>> DeleteBookingAsync(
        VenueConnectionConfiguration venue,
        long openingId,
        long bookingId,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.DeleteDjBookingAsync(
                    context.BaseUri!, context.AccessToken!, openingId, bookingId, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<DeleteDjBookingResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<DjPaymentOperationResponse>> StartPaymentAsync(
        VenueConnectionConfiguration venue,
        long bookingId,
        StartDjPaymentRequest request,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.StartDjPaymentAsync(
                    context.BaseUri!, context.AccessToken!, bookingId, request, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<DjPaymentOperationResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<DjBalancePaymentResponse>> StartBalancePaymentAsync(
        VenueConnectionConfiguration venue,
        StartDjBalancePaymentRequest request,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.StartDjBalancePaymentAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<DjBalancePaymentResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<IReadOnlyList<DjPaymentOperationResponse>>> ConfirmPaymentsAsync(
        VenueConnectionConfiguration venue,
        IReadOnlyList<long> paymentIds,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var confirmed = new List<DjPaymentOperationResponse>(paymentIds.Count);
                foreach (var paymentId in paymentIds)
                {
                    var result = await apiClient.ConfirmDjPaymentAsync(
                        context.BaseUri!, context.AccessToken!, paymentId, cancellationToken);
                    if (!result.Success || result.Value is null)
                    {
                        if (confirmed.Count > 0)
                            await RefreshCoreAsync(venue, context, cancellationToken);
                        return ApiResult<IReadOnlyList<DjPaymentOperationResponse>>.Failed(result.Failure!);
                    }
                    confirmed.Add(result.Value);
                }

                await RefreshCoreAsync(venue, context, cancellationToken);
                return ApiResult<IReadOnlyList<DjPaymentOperationResponse>>.Succeeded(confirmed);
            },
            ApiResult<IReadOnlyList<DjPaymentOperationResponse>>.Failed,
            cancellationToken);

    public Task<ApiResult<IReadOnlyList<DjPaymentOperationResponse>>> CancelPaymentsAsync(
        VenueConnectionConfiguration venue,
        IReadOnlyList<long> paymentIds,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var cancelled = new List<DjPaymentOperationResponse>(paymentIds.Count);
                foreach (var paymentId in paymentIds)
                {
                    var result = await apiClient.CancelDjPaymentAsync(
                        context.BaseUri!,
                        context.AccessToken!,
                        paymentId,
                        new CancelDjPaymentRequest(true),
                        cancellationToken);
                    if (!result.Success || result.Value is null)
                    {
                        if (cancelled.Count > 0)
                            await RefreshCoreAsync(venue, context, cancellationToken);
                        return ApiResult<IReadOnlyList<DjPaymentOperationResponse>>.Failed(result.Failure!);
                    }
                    cancelled.Add(result.Value);
                }

                await RefreshCoreAsync(venue, context, cancellationToken);
                return ApiResult<IReadOnlyList<DjPaymentOperationResponse>>.Succeeded(cancelled);
            },
            ApiResult<IReadOnlyList<DjPaymentOperationResponse>>.Failed,
            cancellationToken);

    public Task<ApiResult<DjPaymentOperationResponse>> ConfirmPaymentAsync(
        VenueConnectionConfiguration venue,
        long paymentId,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.ConfirmDjPaymentAsync(
                    context.BaseUri!, context.AccessToken!, paymentId, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<DjPaymentOperationResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<DjPaymentOperationResponse>> CancelPaymentAsync(
        VenueConnectionConfiguration venue,
        long paymentId,
        CancelDjPaymentRequest request,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.CancelDjPaymentAsync(
                    context.BaseUri!, context.AccessToken!, paymentId, request, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<DjPaymentOperationResponse>.Failed,
            cancellationToken);

    public void RemoveProfile(Guid profileId)
    {
        snapshots.TryRemove(profileId, out _);
        lastSuccessfulBookingSaveAt.TryRemove(profileId, out _);
    }

    public void Clear(string message = "DJ data was cleared.")
    {
        foreach (var pair in snapshots)
            snapshots[pair.Key] = DjManagementSnapshot.NotLoaded with { Message = message };
        lastSuccessfulBookingSaveAt.Clear();
    }

    public void Dispose()
    {
        foreach (var gate in gates.Values) gate.Dispose();
        gates.Clear();
        snapshots.Clear();
        lastSuccessfulBookingSaveAt.Clear();
    }

    private async Task<bool> RefreshCoreAsync(
        VenueConnectionConfiguration venue,
        AuthorizedContext context,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.GetDjsAsync(
            context.BaseUri!, context.AccessToken!, cancellationToken);
        if (result.Success && result.Value is not null)
        {
            snapshots[venue.ProfileId] = new DjManagementSnapshot(
                DjManagementStatus.Ready,
                "DJ data loaded.",
                result.Value,
                DateTimeOffset.UtcNow);
            return true;
        }
        else
        {
            log.Warning(
                "DJ mutation succeeded but refresh failed: {Code} {Message}",
                result.Failure?.Code,
                result.Failure?.Message);
            snapshots[venue.ProfileId] = DjManagementSnapshot.NotLoaded with
            {
                Message = "DJ data changed. Refresh to load the latest state."
            };
            return false;
        }
    }

    private Task<T> WithMutationAsync<T>(
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
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Authentication,
                "VENUE_NOT_REGISTERED",
                "This venue is not registered on this device."));

        if (!identityProvider.TryGetCurrent(out var identity, out var reason))
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "PLAYER_NOT_AVAILABLE",
                reason));

        if (!PartyPulseApiClient.TryCreateBaseUri(configuration.ApiBaseUrl, out var baseUri, out var urlError))
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "INVALID_API_URL",
                urlError));

        var token = await authentication.EnsureAccessTokenAsync(
            venue,
            identity!,
            configuration.ApiBaseUrl,
            cancellationToken);
        return token.Success
            ? AuthorizedContext.Succeeded(baseUri!, token.AccessToken!)
            : AuthorizedContext.Failed(token.Failure!);
    }

    private async Task<T> WithGateAsync<T>(
        VenueConnectionConfiguration venue,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var gate = gates.GetOrAdd(venue.ProfileId, _ => new SemaphoreSlim(1, 1));
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

    private void SetFailure(
        VenueConnectionConfiguration venue,
        ApiFailure failure,
        DateTimeOffset attemptAt)
    {
        var existing = GetSnapshot(venue);
        snapshots[venue.ProfileId] = new DjManagementSnapshot(
            failure.Kind == ApiFailureKind.Permission
                ? DjManagementStatus.Denied
                : DjManagementStatus.Failed,
            failure.Message,
            existing.View,
            attemptAt);
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
