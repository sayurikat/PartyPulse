using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Staff;

public sealed class StaffManagementManager : IDisposable
{
    private static readonly TimeSpan FailedReloadDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeniedReloadDelay = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly ConcurrentDictionary<Guid, StaffManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public StaffManagementManager(
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

    public StaffManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : StaffManagementSnapshot.NotLoaded;

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

        if (snapshot.Status is StaffManagementStatus.Loading or StaffManagementStatus.Ready)
        {
            return false;
        }

        var retryDelay = snapshot.Status == StaffManagementStatus.Denied
            ? DeniedReloadDelay
            : FailedReloadDelay;
        return snapshot.LastAttemptAt is null ||
               snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(retryDelay);
    }

    public Task<ApiResult<StaffManagementViewResponse>> LoadAsync(
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
                        ? ApiResult<StaffManagementViewResponse>.Succeeded(existing.View)
                        : ApiResult<StaffManagementViewResponse>.Failed(new ApiFailure(
                            existing.Status == StaffManagementStatus.Denied
                                ? ApiFailureKind.Permission
                                : ApiFailureKind.Unknown,
                            "STAFF_NOT_AVAILABLE",
                            existing.Message));
                }

                return await LoadCoreAsync(venue, cancellationToken);
            },
            cancellationToken);

    public Task<ApiResult<StaffManagementViewResponse>> RefreshQuietlyAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var attemptedAt = DateTimeOffset.UtcNow;
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    TouchSnapshotAttempt(venue.ProfileId, attemptedAt);
                    return ApiResult<StaffManagementViewResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.GetStaffAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    cancellationToken);
                if (result.Success && result.Value is not null)
                {
                    snapshots[venue.ProfileId] = new StaffManagementSnapshot(
                        StaffManagementStatus.Ready,
                        "Staff loaded.",
                        result.Value,
                        attemptedAt);
                }
                else
                {
                    TouchSnapshotAttempt(venue.ProfileId, attemptedAt);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<StaffJobOperationResponse>> SaveJobAsync(
        VenueConnectionConfiguration venue,
        long? jobId,
        SaveStaffJobRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => jobId is null
                ? apiClient.CreateStaffJobAsync(
                    baseUri,
                    accessToken,
                    request,
                    cancellationToken)
                : apiClient.UpdateStaffJobAsync(
                    baseUri,
                    accessToken,
                    jobId.Value,
                    request,
                    cancellationToken),
            cancellationToken);

    public Task<ApiResult<StaffMemberOperationResponse>> SaveMemberAsync(
        VenueConnectionConfiguration venue,
        long? staffMemberId,
        SaveStaffMemberRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => staffMemberId is null
                ? apiClient.CreateStaffMemberAsync(
                    baseUri,
                    accessToken,
                    request,
                    cancellationToken)
                : apiClient.UpdateStaffMemberAsync(
                    baseUri,
                    accessToken,
                    staffMemberId.Value,
                    request,
                    cancellationToken),
            cancellationToken);

    public Task<ApiResult<StaffCharacterLinkResponse>> LinkCharacterAsync(
        VenueConnectionConfiguration venue,
        LinkStaffCharacterRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.LinkStaffCharacterAsync(
                baseUri,
                accessToken,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<StaffTimeEntryOperationResponse>> SaveTimeEntryAsync(
        VenueConnectionConfiguration venue,
        long? timeEntryId,
        SaveStaffTimeEntryRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => timeEntryId is null
                ? apiClient.CreateStaffTimeEntryAsync(
                    baseUri,
                    accessToken,
                    request,
                    cancellationToken)
                : apiClient.UpdateStaffTimeEntryAsync(
                    baseUri,
                    accessToken,
                    timeEntryId.Value,
                    request,
                    cancellationToken),
            cancellationToken);

    public Task<ApiResult<StaffTimeEntryCancellationResponse>> CancelTimeEntryAsync(
        VenueConnectionConfiguration venue,
        long timeEntryId,
        CancelStaffTimeEntryRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.CancelStaffTimeEntryAsync(
                baseUri,
                accessToken,
                timeEntryId,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<ObserveStaffFirstSeenResponse>> ObserveFirstSeenAsync(
        VenueConnectionConfiguration venue,
        ObserveStaffFirstSeenRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.ObserveStaffFirstSeenAsync(
                baseUri,
                accessToken,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<StaffAbsenceOperationResponse>> SetAbsenceAsync(
        VenueConnectionConfiguration venue,
        long openingId,
        SetStaffAbsenceRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.SetStaffAbsenceAsync(
                baseUri,
                accessToken,
                openingId,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<StaffAbsenceCancellationResponse>> CancelAbsenceAsync(
        VenueConnectionConfiguration venue,
        long absenceId,
        CancelStaffAbsenceRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.CancelStaffAbsenceAsync(
                baseUri,
                accessToken,
                absenceId,
                request,
                cancellationToken),
            cancellationToken);

    public Task<ApiResult<StaffPayoutResponse>> CreatePayoutAsync(
        VenueConnectionConfiguration venue,
        CreateStaffPayoutRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            venue,
            (baseUri, accessToken) => apiClient.CreateStaffPayoutAsync(
                baseUri,
                accessToken,
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

    private async Task<ApiResult<StaffManagementViewResponse>> LoadCoreAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        snapshots[venue.ProfileId] = new StaffManagementSnapshot(
            StaffManagementStatus.Loading,
            "Loading staff...",
            null,
            attemptedAt);

        var context = await GetAuthorizedContextAsync(venue, cancellationToken);
        if (!context.Success)
        {
            snapshots[venue.ProfileId] = new StaffManagementSnapshot(
                context.Failure!.Kind == ApiFailureKind.Permission
                    ? StaffManagementStatus.Denied
                    : StaffManagementStatus.Failed,
                context.Failure.Message,
                null,
                attemptedAt);
            return ApiResult<StaffManagementViewResponse>.Failed(context.Failure);
        }

        var result = await apiClient.GetStaffAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new StaffManagementSnapshot(
                StaffManagementStatus.Ready,
                "Staff loaded.",
                result.Value,
                attemptedAt)
            : new StaffManagementSnapshot(
                result.Failure?.Kind == ApiFailureKind.Permission
                    ? StaffManagementStatus.Denied
                    : StaffManagementStatus.Failed,
                result.Failure?.Message ?? "Staff could not be loaded.",
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
        var result = await apiClient.GetStaffAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);
        snapshots[venue.ProfileId] = result.Success && result.Value is not null
            ? new StaffManagementSnapshot(
                StaffManagementStatus.Ready,
                "Staff loaded.",
                result.Value,
                DateTimeOffset.UtcNow)
            : StaffManagementSnapshot.NotLoaded with
            {
                Message = "Staff changed. Refresh to load the latest state."
            };
    }

    private void TouchSnapshotAttempt(Guid profileId, DateTimeOffset attemptedAt)
    {
        if (snapshots.TryGetValue(profileId, out var existing))
        {
            snapshots[profileId] = existing with { LastAttemptAt = attemptedAt };
        }
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
