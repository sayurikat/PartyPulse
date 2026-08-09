using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Giveaways;

public sealed class GiveawayManagementManager : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, GiveawayManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public GiveawayManagementManager(
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

    public GiveawayManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : GiveawayManagementSnapshot.NotLoaded;

    public bool IsBusy(Guid profileId) =>
        gates.TryGetValue(profileId, out var gate) && gate.CurrentCount == 0;

    public bool ShouldLoad(VenueConnectionConfiguration venue)
    {
        if (!venue.IsRegistered) return false;
        var snapshot = GetSnapshot(venue);
        if (snapshot.Status == GiveawayManagementStatus.Loading) return false;
        if (snapshot.Status == GiveawayManagementStatus.NotLoaded) return true;
        var lastContactAt = snapshot.ReceivedAt ?? snapshot.LastAttemptAt;
        return lastContactAt is null || lastContactAt <= DateTimeOffset.UtcNow.Subtract(PollInterval);
    }

    public Task<ApiResult<GiveawayManagementViewResponse>> LoadAsync(
        VenueConnectionConfiguration venue,
        bool force,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            if (!force && !ShouldLoad(venue))
            {
                var existing = GetSnapshot(venue);
                return existing.View is not null
                    ? ApiResult<GiveawayManagementViewResponse>.Succeeded(existing.View)
                    : ApiResult<GiveawayManagementViewResponse>.Failed(new ApiFailure(
                        ApiFailureKind.Unknown,
                        "GIVEAWAY_DATA_NOT_AVAILABLE",
                        existing.Message));
            }

            var attemptAt = DateTimeOffset.UtcNow;
            var existingSnapshot = GetSnapshot(venue);
            snapshots[venue.ProfileId] = new GiveawayManagementSnapshot(
                GiveawayManagementStatus.Loading,
                existingSnapshot.View is null ? "Loading giveaways..." : "Refreshing giveaways...",
                existingSnapshot.View,
                attemptAt,
                existingSnapshot.ReceivedAt);

            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            if (!context.Success)
            {
                SetFailure(venue, context.Failure!, attemptAt);
                return ApiResult<GiveawayManagementViewResponse>.Failed(context.Failure!);
            }

            var result = await apiClient.GetGiveawaysAsync(context.BaseUri!, context.AccessToken!, cancellationToken);
            if (!result.Success || result.Value is null)
            {
                SetFailure(venue, result.Failure!, attemptAt);
                return result;
            }

            SetReady(venue, result.Value);
            return result;
        }, cancellationToken);

    public Task<ApiResult<SaveGiveawayResponse>> SaveGiveawayAsync(
        VenueConnectionConfiguration venue,
        long? giveawayId,
        SaveGiveawayRequest request,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            context => giveawayId is { } id
                ? apiClient.UpdateGiveawayAsync(context.BaseUri!, context.AccessToken!, id, request, cancellationToken)
                : apiClient.CreateGiveawayAsync(context.BaseUri!, context.AccessToken!, request, cancellationToken),
            static result => result.Success,
            ApiResult<SaveGiveawayResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<SaveGiveawaySchedulerResponse>> SaveSchedulerAsync(
        VenueConnectionConfiguration venue,
        long? schedulerId,
        SaveGiveawaySchedulerRequest request,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            venue,
            context => schedulerId is { } id
                ? apiClient.UpdateGiveawaySchedulerAsync(context.BaseUri!, context.AccessToken!, id, request, cancellationToken)
                : apiClient.CreateGiveawaySchedulerAsync(context.BaseUri!, context.AccessToken!, request, cancellationToken),
            static result => result.Success,
            ApiResult<SaveGiveawaySchedulerResponse>.Failed,
            cancellationToken);

    public void RemoveProfile(Guid profileId) => snapshots.TryRemove(profileId, out _);

    public void Clear(string message = "Giveaway data was cleared.")
    {
        foreach (var pair in snapshots)
            snapshots[pair.Key] = GiveawayManagementSnapshot.NotLoaded with { Message = message };
    }

    public void Dispose()
    {
        foreach (var gate in gates.Values) gate.Dispose();
        gates.Clear();
        snapshots.Clear();
    }

    private Task<T> WithMutationAsync<T>(
        VenueConnectionConfiguration venue,
        Func<AuthorizedContext, Task<T>> operation,
        Func<T, bool> succeeded,
        Func<ApiFailure, T> failureFactory,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            if (!context.Success) return failureFactory(context.Failure!);
            var result = await operation(context);
            if (succeeded(result))
            {
                await RefreshCoreAsync(venue, context, cancellationToken);
            }
            return result;
        }, cancellationToken);

    private async Task RefreshCoreAsync(
        VenueConnectionConfiguration venue,
        AuthorizedContext context,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.GetGiveawaysAsync(context.BaseUri!, context.AccessToken!, cancellationToken);
        if (result.Success && result.Value is not null)
        {
            SetReady(venue, result.Value);
            return;
        }

        log.Warning("Giveaway mutation succeeded but refresh failed: {Code} {Message}",
            result.Failure?.Code, result.Failure?.Message);
        var existing = GetSnapshot(venue);
        snapshots[venue.ProfileId] = new GiveawayManagementSnapshot(
            GiveawayManagementStatus.Failed,
            "Giveaways changed, but the refreshed state could not be loaded.",
            existing.View,
            DateTimeOffset.UtcNow,
            existing.ReceivedAt);
    }

    private void SetReady(VenueConnectionConfiguration venue, GiveawayManagementViewResponse view)
    {
        var now = DateTimeOffset.UtcNow;
        snapshots[venue.ProfileId] = new GiveawayManagementSnapshot(
            GiveawayManagementStatus.Ready,
            "Giveaways loaded.",
            view,
            now,
            now);
    }

    private async Task<AuthorizedContext> GetAuthorizedContextAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        if (!venue.IsRegistered)
            return AuthorizedContext.Failed(new ApiFailure(ApiFailureKind.Authentication, "VENUE_NOT_REGISTERED", "This venue is not registered on this device."));
        if (!identityProvider.TryGetCurrent(out var identity, out var reason))
            return AuthorizedContext.Failed(new ApiFailure(ApiFailureKind.Validation, "PLAYER_NOT_AVAILABLE", reason));
        if (!PartyPulseApiClient.TryCreateBaseUri(configuration.ApiBaseUrl, out var baseUri, out var urlError))
            return AuthorizedContext.Failed(new ApiFailure(ApiFailureKind.Validation, "INVALID_API_URL", urlError));

        var token = await authentication.EnsureAccessTokenAsync(
            venue, identity!, configuration.ApiBaseUrl, cancellationToken);
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
        try { return await operation(); }
        finally { gate.Release(); }
    }

    private void SetFailure(VenueConnectionConfiguration venue, ApiFailure failure, DateTimeOffset attemptAt)
    {
        var existing = GetSnapshot(venue);
        snapshots[venue.ProfileId] = new GiveawayManagementSnapshot(
            GiveawayManagementStatus.Failed, failure.Message, existing.View, attemptAt, existing.ReceivedAt);
    }

    private sealed record AuthorizedContext(bool Success, Uri? BaseUri, string? AccessToken, ApiFailure? Failure)
    {
        public static AuthorizedContext Succeeded(Uri baseUri, string accessToken) => new(true, baseUri, accessToken, null);
        public static AuthorizedContext Failed(ApiFailure failure) => new(false, null, null, failure);
    }
}
