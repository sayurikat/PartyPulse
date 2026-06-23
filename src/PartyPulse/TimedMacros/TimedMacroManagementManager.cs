using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.TimedMacros;

public sealed class TimedMacroManagementManager : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, TimedMacroManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public TimedMacroManagementManager(
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

    public TimedMacroManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : TimedMacroManagementSnapshot.NotLoaded;

    public bool IsBusy(Guid profileId) =>
        gates.TryGetValue(profileId, out var gate) && gate.CurrentCount == 0;

    public bool ShouldLoad(VenueConnectionConfiguration venue)
    {
        if (!venue.IsRegistered) return false;
        var snapshot = GetSnapshot(venue);
        if (snapshot.Status == TimedMacroManagementStatus.Loading) return false;
        if (snapshot.Status == TimedMacroManagementStatus.NotLoaded) return true;
        var lastContactAt = snapshot.ReceivedAt ?? snapshot.LastAttemptAt;
        return lastContactAt is null ||
               lastContactAt <= DateTimeOffset.UtcNow.Subtract(PollInterval);
    }

    public Task<ApiResult<TimedMacroViewResponse>> LoadAsync(
        VenueConnectionConfiguration venue,
        bool force,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            if (!force && !ShouldLoad(venue))
            {
                var existing = GetSnapshot(venue);
                return existing.View is not null
                    ? ApiResult<TimedMacroViewResponse>.Succeeded(existing.View)
                    : ApiResult<TimedMacroViewResponse>.Failed(new ApiFailure(
                        ApiFailureKind.Unknown,
                        "TIMED_MACRO_DATA_NOT_AVAILABLE",
                        existing.Message));
            }

            var attemptAt = DateTimeOffset.UtcNow;
            var existingSnapshot = GetSnapshot(venue);
            snapshots[venue.ProfileId] = new TimedMacroManagementSnapshot(
                TimedMacroManagementStatus.Loading,
                existingSnapshot.View is null ? "Loading timed macros..." : "Refreshing timed macros...",
                existingSnapshot.View,
                attemptAt,
                existingSnapshot.ReceivedAt);

            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            if (!context.Success)
            {
                SetFailure(venue, context.Failure!, attemptAt);
                return ApiResult<TimedMacroViewResponse>.Failed(context.Failure!);
            }

            var result = await apiClient.GetTimedMacrosAsync(
                context.BaseUri!, context.AccessToken!, cancellationToken);
            if (!result.Success || result.Value is null)
            {
                SetFailure(venue, result.Failure!, attemptAt);
                return result;
            }

            var receivedAt = DateTimeOffset.UtcNow;
            snapshots[venue.ProfileId] = new TimedMacroManagementSnapshot(
                TimedMacroManagementStatus.Ready,
                "Timed macros loaded.",
                result.Value,
                attemptAt,
                receivedAt);
            return result;
        }, cancellationToken);

    public Task<ApiResult<SaveTimedMacroResponse>> CreateAsync(
        VenueConnectionConfiguration venue,
        CreateTimedMacroRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.CreateTimedMacroAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<SaveTimedMacroResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<SaveTimedMacroResponse>> UpdateAsync(
        VenueConnectionConfiguration venue,
        long timedMacroId,
        UpdateTimedMacroRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.UpdateTimedMacroAsync(
                    context.BaseUri!, context.AccessToken!, timedMacroId, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<SaveTimedMacroResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<ArchiveTimedMacroResponse>> ArchiveAsync(
        VenueConnectionConfiguration venue,
        long timedMacroId,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.ArchiveTimedMacroAsync(
                    context.BaseUri!, context.AccessToken!, timedMacroId, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<ArchiveTimedMacroResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<RecordTimedMacroExecutionResponse>> RecordExecutionAsync(
        VenueConnectionConfiguration venue,
        long timedMacroId,
        RecordTimedMacroExecutionRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.RecordTimedMacroExecutionAsync(
                    context.BaseUri!, context.AccessToken!, timedMacroId, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<RecordTimedMacroExecutionResponse>.Failed,
            cancellationToken);

    public void RemoveProfile(Guid profileId) => snapshots.TryRemove(profileId, out _);

    public void Clear(string message = "Timed macro data was cleared.")
    {
        foreach (var pair in snapshots)
        {
            snapshots[pair.Key] = TimedMacroManagementSnapshot.NotLoaded with { Message = message };
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
        var result = await apiClient.GetTimedMacrosAsync(
            context.BaseUri!, context.AccessToken!, cancellationToken);
        if (result.Success && result.Value is not null)
        {
            var now = DateTimeOffset.UtcNow;
            snapshots[venue.ProfileId] = new TimedMacroManagementSnapshot(
                TimedMacroManagementStatus.Ready,
                "Timed macros loaded.",
                result.Value,
                now,
                now);
        }
        else
        {
            log.Warning(
                "Timed macro mutation succeeded but refresh failed: {Code} {Message}",
                result.Failure?.Code,
                result.Failure?.Message);
            var existing = GetSnapshot(venue);
            var now = DateTimeOffset.UtcNow;
            snapshots[venue.ProfileId] = new TimedMacroManagementSnapshot(
                TimedMacroManagementStatus.Failed,
                "Timed macros changed, but the refreshed state could not be loaded.",
                existing.View,
                now,
                existing.ReceivedAt);
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
        snapshots[venue.ProfileId] = new TimedMacroManagementSnapshot(
            TimedMacroManagementStatus.Failed,
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
