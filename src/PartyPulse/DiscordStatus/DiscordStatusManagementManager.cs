using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.DiscordStatus;

public sealed class DiscordStatusManagementManager : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, DiscordStatusManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public DiscordStatusManagementManager(
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

    public DiscordStatusManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : DiscordStatusManagementSnapshot.NotLoaded;

    public bool IsBusy(Guid profileId) =>
        gates.TryGetValue(profileId, out var gate) && gate.CurrentCount == 0;

    public bool ShouldLoad(VenueConnectionConfiguration venue)
    {
        if (!venue.IsRegistered)
        {
            return false;
        }

        var snapshot = GetSnapshot(venue);
        if (snapshot.Status == DiscordStatusManagementStatus.Loading)
        {
            return false;
        }

        if (snapshot.Status == DiscordStatusManagementStatus.NotLoaded)
        {
            return true;
        }

        var lastContactAt = snapshot.ReceivedAt ?? snapshot.LastAttemptAt;
        return lastContactAt is null || lastContactAt <= DateTimeOffset.UtcNow.Subtract(PollInterval);
    }

    public Task<ApiResult<DiscordManagementViewResponse>> LoadAsync(
        VenueConnectionConfiguration venue,
        bool force,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            if (!force && !ShouldLoad(venue))
            {
                var existing = GetSnapshot(venue);
                return existing.View is not null
                    ? ApiResult<DiscordManagementViewResponse>.Succeeded(existing.View)
                    : ApiResult<DiscordManagementViewResponse>.Failed(new ApiFailure(
                        ApiFailureKind.Unknown,
                        "DISCORD_STATUS_NOT_AVAILABLE",
                        existing.Message));
            }

            var attemptAt = DateTimeOffset.UtcNow;
            var existingSnapshot = GetSnapshot(venue);
            snapshots[venue.ProfileId] = new DiscordStatusManagementSnapshot(
                DiscordStatusManagementStatus.Loading,
                existingSnapshot.View is null
                    ? "Loading Discord venue status..."
                    : "Refreshing Discord venue status...",
                existingSnapshot.View,
                attemptAt,
                existingSnapshot.ReceivedAt);

            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            if (!context.Success)
            {
                SetFailure(venue, context.Failure!, attemptAt);
                return ApiResult<DiscordManagementViewResponse>.Failed(context.Failure!);
            }

            var result = await apiClient.GetDiscordManagementAsync(
                context.BaseUri!,
                context.AccessToken!,
                cancellationToken);
            if (!result.Success || result.Value is null)
            {
                SetFailure(venue, result.Failure!, attemptAt);
                return result;
            }

            SetReady(venue, result.Value);
            return result;
        }, cancellationToken);

    public Task<ApiResult<SaveDiscordVenueStatusResponse>> SaveAsync(
        VenueConnectionConfiguration venue,
        SaveDiscordVenueStatusRequest request,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            var attemptAt = DateTimeOffset.UtcNow;
            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            if (!context.Success)
            {
                SetFailure(venue, context.Failure!, attemptAt);
                return ApiResult<SaveDiscordVenueStatusResponse>.Failed(context.Failure!);
            }

            var result = await apiClient.SaveDiscordVenueStatusAsync(
                context.BaseUri!,
                context.AccessToken!,
                request,
                cancellationToken);
            if (!result.Success)
            {
                SetFailure(venue, result.Failure!, attemptAt);
                return result;
            }

            var refresh = await apiClient.GetDiscordManagementAsync(
                context.BaseUri!,
                context.AccessToken!,
                cancellationToken);
            if (refresh.Success && refresh.Value is not null)
            {
                SetReady(venue, refresh.Value);
            }
            else
            {
                log.Warning(
                    "Discord venue-status settings were saved, but refresh failed: {Code} {Message}",
                    refresh.Failure?.Code,
                    refresh.Failure?.Message);
                var existing = GetSnapshot(venue);
                snapshots[venue.ProfileId] = new DiscordStatusManagementSnapshot(
                    DiscordStatusManagementStatus.Failed,
                    "Settings were saved, but the refreshed Discord status could not be loaded.",
                    existing.View,
                    DateTimeOffset.UtcNow,
                    existing.ReceivedAt);
            }

            return result;
        }, cancellationToken);

    public void RemoveProfile(Guid profileId) => snapshots.TryRemove(profileId, out _);

    public void Clear(string message = "Discord venue-status data was cleared.")
    {
        foreach (var pair in snapshots)
        {
            snapshots[pair.Key] = DiscordStatusManagementSnapshot.NotLoaded with { Message = message };
        }
    }

    public void Dispose()
    {
        foreach (var gate in gates.Values)
        {
            gate.Dispose();
        }

        gates.Clear();
        snapshots.Clear();
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
                "This venue is not registered on this device."));
        }

        if (!identityProvider.TryGetCurrent(out var identity, out var reason))
        {
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "PLAYER_NOT_AVAILABLE",
                reason));
        }

        if (!PartyPulseApiClient.TryCreateBaseUri(
                configuration.ApiBaseUrl,
                out var baseUri,
                out var urlError))
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

    private void SetReady(VenueConnectionConfiguration venue, DiscordManagementViewResponse view)
    {
        var now = DateTimeOffset.UtcNow;
        snapshots[venue.ProfileId] = new DiscordStatusManagementSnapshot(
            DiscordStatusManagementStatus.Ready,
            "Discord venue status loaded.",
            view,
            now,
            now);
    }

    private void SetFailure(
        VenueConnectionConfiguration venue,
        ApiFailure failure,
        DateTimeOffset attemptAt)
    {
        var existing = GetSnapshot(venue);
        snapshots[venue.ProfileId] = new DiscordStatusManagementSnapshot(
            failure.Kind == ApiFailureKind.Permission
                ? DiscordStatusManagementStatus.Denied
                : DiscordStatusManagementStatus.Failed,
            failure.Message,
            existing.View,
            attemptAt,
            existing.ReceivedAt);
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
