using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Greeter;

public sealed class GreeterManagementManager : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, GreeterManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public GreeterManagementManager(
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

    public GreeterManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : GreeterManagementSnapshot.NotLoaded;

    public bool IsBusy(Guid profileId) =>
        gates.TryGetValue(profileId, out var gate) && gate.CurrentCount == 0;

    public bool ShouldLoad(VenueConnectionConfiguration venue)
    {
        if (!venue.IsRegistered) return false;
        var snapshot = GetSnapshot(venue);
        if (snapshot.Status == GreeterManagementStatus.Loading) return false;
        if (snapshot.Status == GreeterManagementStatus.NotLoaded) return true;
        return snapshot.LastAttemptAt is null ||
               snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(PollInterval);
    }

    public Task<ApiResult<GreeterContextResponse>> LoadAsync(
        VenueConnectionConfiguration venue,
        bool force,
        CancellationToken cancellationToken) =>
        WithGateAsync(venue, async () =>
        {
            if (!force && !ShouldLoad(venue))
            {
                var existing = GetSnapshot(venue);
                return existing.Context is not null
                    ? ApiResult<GreeterContextResponse>.Succeeded(existing.Context)
                    : ApiResult<GreeterContextResponse>.Failed(new ApiFailure(
                        ApiFailureKind.Unknown,
                        "GREETER_DATA_NOT_AVAILABLE",
                        existing.Message));
            }

            var attemptAt = DateTimeOffset.UtcNow;
            var existingContext = GetSnapshot(venue).Context;
            snapshots[venue.ProfileId] = new GreeterManagementSnapshot(
                GreeterManagementStatus.Loading,
                existingContext is null ? "Loading greeter data..." : "Refreshing greeter data...",
                existingContext,
                attemptAt);

            var context = await GetAuthorizedContextAsync(venue, cancellationToken);
            if (!context.Success)
            {
                SetFailure(venue, context.Failure!, attemptAt);
                return ApiResult<GreeterContextResponse>.Failed(context.Failure!);
            }

            var result = await apiClient.GetGreeterContextAsync(
                context.BaseUri!, context.AccessToken!, cancellationToken);
            if (!result.Success || result.Value is null)
            {
                SetFailure(venue, result.Failure!, attemptAt);
                return result;
            }

            snapshots[venue.ProfileId] = new GreeterManagementSnapshot(
                GreeterManagementStatus.Ready,
                "Greeter data loaded.",
                result.Value,
                attemptAt);
            return result;
        }, cancellationToken);

    public Task<ApiResult<ObserveGreeterArrivalsResponse>> ObserveAsync(
        VenueConnectionConfiguration venue,
        ObserveGreeterArrivalsRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.ObserveGreeterArrivalsAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<ObserveGreeterArrivalsResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<RecordGreeterActionResponse>> RecordActionAsync(
        VenueConnectionConfiguration venue,
        RecordGreeterActionRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.RecordGreeterActionAsync(
                    context.BaseUri!, context.AccessToken!, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<RecordGreeterActionResponse>.Failed,
            cancellationToken);

    public Task<ApiResult<UpdateGreeterMacroResponse>> UpdateMacroAsync(
        VenueConnectionConfiguration venue,
        string macroCode,
        UpdateGreeterMacroRequest request,
        CancellationToken cancellationToken) =>
        WithAuthorizedMutationAsync(
            venue,
            async context =>
            {
                var result = await apiClient.UpdateGreeterMacroAsync(
                    context.BaseUri!, context.AccessToken!, macroCode, request, cancellationToken);
                if (result.Success) await RefreshCoreAsync(venue, context, cancellationToken);
                return result;
            },
            ApiResult<UpdateGreeterMacroResponse>.Failed,
            cancellationToken);

    public void ClearProfile(Guid profileId) => snapshots.TryRemove(profileId, out _);

    public void Clear() => snapshots.Clear();

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
        var result = await apiClient.GetGreeterContextAsync(
            context.BaseUri!, context.AccessToken!, cancellationToken);
        if (result.Success && result.Value is not null)
        {
            snapshots[venue.ProfileId] = new GreeterManagementSnapshot(
                GreeterManagementStatus.Ready,
                "Greeter data loaded.",
                result.Value,
                DateTimeOffset.UtcNow);
        }
        else
        {
            log.Warning(
                "Greeter mutation succeeded but context refresh failed: {Code} {Message}",
                result.Failure?.Code,
                result.Failure?.Message);
            snapshots[venue.ProfileId] = GreeterManagementSnapshot.NotLoaded with
            {
                Message = "Greeter data changed. Refresh to load the latest state."
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
        snapshots[venue.ProfileId] = new GreeterManagementSnapshot(
            failure.Code == "PERMISSION_DENIED"
                ? GreeterManagementStatus.Denied
                : GreeterManagementStatus.Failed,
            failure.Message,
            existing.Context,
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
