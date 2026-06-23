using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.VenueOpenings;

public sealed class VenueOpeningScheduleManager : IDisposable
{
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, VenueOpeningScheduleSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public VenueOpeningScheduleManager(
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

    public VenueOpeningScheduleSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : VenueOpeningScheduleSnapshot.NotLoaded;

    public bool IsBusy(Guid profileId) =>
        gates.TryGetValue(profileId, out var gate) && gate.CurrentCount == 0;

    public bool ShouldLoad(VenueConnectionConfiguration venue) =>
        venue.IsRegistered && GetSnapshot(venue).Status == VenueOpeningScheduleStatus.NotLoaded;

    public Task<ApiResult<VenueOpeningScheduleResponse>> LoadAsync(
        VenueConnectionConfiguration venue,
        bool force,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            var existing = GetSnapshot(venue);
            if (!force && existing.Status == VenueOpeningScheduleStatus.Ready && existing.View is not null)
                return ApiResult<VenueOpeningScheduleResponse>.Succeeded(existing.View);

            var attemptAt = DateTimeOffset.UtcNow;
            snapshots[venue.ProfileId] = new VenueOpeningScheduleSnapshot(
                VenueOpeningScheduleStatus.Loading,
                existing.View is null ? "Loading venue openings..." : "Refreshing venue openings...",
                existing.View,
                attemptAt);

            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            if (!context.Success)
            {
                SetFailure(venue, context.Failure!, attemptAt);
                return ApiResult<VenueOpeningScheduleResponse>.Failed(context.Failure!);
            }

            var result = await apiClient.GetVenueOpeningScheduleAsync(
                context.BaseUri!, context.AccessToken!, cancellationToken);
            if (!result.Success || result.Value is null)
            {
                SetFailure(venue, result.Failure!, attemptAt);
                return result;
            }

            snapshots[venue.ProfileId] = new VenueOpeningScheduleSnapshot(
                VenueOpeningScheduleStatus.Ready,
                "Venue openings loaded.",
                result.Value,
                attemptAt);
            return result;
        }, cancellationToken);

    public Task<ApiResult<VenueOpeningScheduleItem>> SaveAsync(
        VenueConnectionConfiguration venue,
        long? openingId,
        SaveVenueOpeningRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.SaveVenueOpeningAsync(
                    context.BaseUri!, context.AccessToken!, openingId, request, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<VenueOpeningScheduleItem>.Failed,
            cancellationToken);

    public Task<ApiResult<CancelVenueOpeningResponse>> CancelAsync(
        VenueConnectionConfiguration venue,
        long openingId,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.CancelVenueOpeningAsync(
                    context.BaseUri!, context.AccessToken!, openingId, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<CancelVenueOpeningResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<CloseVenueOpeningResponse>> CloseAsync(
        VenueConnectionConfiguration venue,
        long openingId,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.CloseVenueOpeningAsync(
                    context.BaseUri!, context.AccessToken!, openingId, cancellationToken);
                if (result.Success)
                    await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<CloseVenueOpeningResponse>.Failed,
            cancellationToken);

    public void RemoveProfile(Guid profileId) => snapshots.TryRemove(profileId, out _);

    public void Clear(string message = "Opening schedule was cleared.")
    {
        foreach (var pair in snapshots)
        {
            snapshots[pair.Key] = VenueOpeningScheduleSnapshot.NotLoaded with { Message = message };
        }
    }

    public void Dispose()
    {
        foreach (var gate in gates.Values)
            gate.Dispose();
        gates.Clear();
        snapshots.Clear();
    }

    private async Task RefreshCoreAsync(
        VenueConnectionConfiguration venue,
        AuthorizedContext context,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.GetVenueOpeningScheduleAsync(
            context.BaseUri!, context.AccessToken!, cancellationToken);
        if (result.Success && result.Value is not null)
        {
            snapshots[venue.ProfileId] = new VenueOpeningScheduleSnapshot(
                VenueOpeningScheduleStatus.Ready,
                "Venue openings loaded.",
                result.Value,
                DateTimeOffset.UtcNow);
        }
        else
        {
            log.Warning(
                "Venue opening mutation succeeded but schedule refresh failed: {Code} {Message}",
                result.Failure?.Code,
                result.Failure?.Message);
            snapshots[venue.ProfileId] = VenueOpeningScheduleSnapshot.NotLoaded with
            {
                Message = "Venue openings changed. Refresh to load the latest schedule."
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
        {
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Authentication,
                "VENUE_NOT_REGISTERED",
                "This venue is not registered on this device."));
        }

        if (!identityProvider.TryGetCurrent(out var identity, out var reason))
        {
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "PLAYER_NOT_AVAILABLE",
                reason));
        }

        if (!PartyPulseApiClient.TryCreateBaseUri(configuration.ApiBaseUrl, out var baseUri, out var urlError))
        {
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "INVALID_API_URL",
                urlError));
        }

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
        snapshots[venue.ProfileId] = new VenueOpeningScheduleSnapshot(
            VenueOpeningScheduleStatus.Failed,
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
