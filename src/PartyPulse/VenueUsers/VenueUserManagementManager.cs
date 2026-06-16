using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.VenueUsers;

public sealed class VenueUserManagementManager : IDisposable
{
    private static readonly TimeSpan FailedReloadDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeniedReloadDelay = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<Guid, VenueUserManagementSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<string, IssuedOneTimeCode> recoveryCodes = new(StringComparer.Ordinal);

    public VenueUserManagementManager(
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

    public VenueUserManagementSnapshot GetSnapshot(VenueConnectionConfiguration venue) =>
        snapshots.TryGetValue(venue.ProfileId, out var snapshot)
            ? snapshot
            : VenueUserManagementSnapshot.NotLoaded;

    public IssuedOneTimeCode? GetLastRecoveryCode(Guid profileId, int userId) =>
        recoveryCodes.TryGetValue(GetRecoveryKey(profileId, userId), out var code) ? code : null;

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

        if (snapshot.Status is VenueUserManagementStatus.Loading or VenueUserManagementStatus.Ready)
        {
            return false;
        }

        var retryDelay = snapshot.Status == VenueUserManagementStatus.Denied
            ? DeniedReloadDelay
            : FailedReloadDelay;

        return snapshot.LastAttemptAt is null || snapshot.LastAttemptAt <= DateTimeOffset.UtcNow.Subtract(retryDelay);
    }

    public Task<ApiResult<VenueUserManagementViewResponse>> LoadAsync(
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
                        ? ApiResult<VenueUserManagementViewResponse>.Succeeded(existing.View)
                        : ApiResult<VenueUserManagementViewResponse>.Failed(new ApiFailure(
                            existing.Status == VenueUserManagementStatus.Denied
                                ? ApiFailureKind.Permission
                                : ApiFailureKind.Unknown,
                            "VENUE_USERS_NOT_AVAILABLE",
                            existing.Message));
                }

                return await LoadCoreAsync(venue, cancellationToken);
            },
            cancellationToken);

    public Task<ApiResult<CreateVenueUserResponse>> CreateAsync(
        VenueConnectionConfiguration venue,
        string displayName,
        string? discordHandle,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<CreateVenueUserResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.CreateVenueUserAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    new CreateVenueUserRequest(displayName.Trim(), NormalizeOptional(discordHandle)),
                    cancellationToken);

                if (result.Success && result.Value is not null)
                {
                    var code = new IssuedOneTimeCode(
                        result.Value.UserId,
                        displayName.Trim(),
                        result.Value.InviteCode,
                        result.Value.InviteExpiresAt);
                    snapshots.AddOrUpdate(
                        venue.ProfileId,
                        _ => VenueUserManagementSnapshot.NotLoaded with { LastInviteCode = code },
                        (_, previous) => previous with { LastInviteCode = code });

                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<VenueUserOperationResponse>> UpdateProfileAsync(
        VenueConnectionConfiguration venue,
        int userId,
        string displayName,
        string? discordHandle,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<VenueUserOperationResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.UpdateVenueUserProfileAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    userId,
                    new UpdateVenueUserProfileRequest(displayName.Trim(), NormalizeOptional(discordHandle)),
                    cancellationToken);

                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<SetVenueUserPermissionsResponse>> SetPermissionsAsync(
        VenueConnectionConfiguration venue,
        int userId,
        IReadOnlyCollection<string> permissionKeys,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<SetVenueUserPermissionsResponse>.Failed(context.Failure!);
                }

                var normalized = permissionKeys
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(static value => value.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray();

                var result = await apiClient.SetVenueUserPermissionsAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    userId,
                    new SetVenueUserPermissionsRequest(normalized),
                    cancellationToken);

                if (result.Success)
                {
                    await RefreshAfterMutationAsync(venue, context, cancellationToken);
                }

                return result;
            },
            cancellationToken);

    public Task<ApiResult<CreateRecoveryCodeResponse>> CreateRecoveryCodeAsync(
        VenueConnectionConfiguration venue,
        VenueUserSummary user,
        CancellationToken cancellationToken) =>
        WithGateAsync(
            venue,
            async () =>
            {
                var context = await GetAuthorizedContextAsync(venue, cancellationToken);
                if (!context.Success)
                {
                    return ApiResult<CreateRecoveryCodeResponse>.Failed(context.Failure!);
                }

                var result = await apiClient.CreateVenueUserRecoveryCodeAsync(
                    context.BaseUri!,
                    context.AccessToken!,
                    user.UserId,
                    cancellationToken);

                if (result.Success && result.Value is not null)
                {
                    recoveryCodes[GetRecoveryKey(venue.ProfileId, user.UserId)] = new IssuedOneTimeCode(
                        user.UserId,
                        user.DisplayName,
                        result.Value.RecoveryCode,
                        result.Value.RecoveryCodeExpiresAt);
                }

                return result;
            },
            cancellationToken);

    public void Clear(string message)
    {
        foreach (var pair in snapshots)
        {
            snapshots[pair.Key] = VenueUserManagementSnapshot.NotLoaded with { Message = message };
        }

        recoveryCodes.Clear();
    }

    public void RemoveProfile(Guid profileId)
    {
        snapshots.TryRemove(profileId, out _);
        foreach (var key in recoveryCodes.Keys.Where(key => key.StartsWith(profileId.ToString("N"), StringComparison.Ordinal)))
        {
            recoveryCodes.TryRemove(key, out _);
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
        recoveryCodes.Clear();
    }

    private async Task<ApiResult<VenueUserManagementViewResponse>> LoadCoreAsync(
        VenueConnectionConfiguration venue,
        CancellationToken cancellationToken)
    {
        var attemptAt = DateTimeOffset.UtcNow;
        snapshots.AddOrUpdate(
            venue.ProfileId,
            _ => new VenueUserManagementSnapshot(
                VenueUserManagementStatus.Loading,
                "Loading venue users...",
                null,
                null,
                attemptAt),
            (_, previous) => previous with
            {
                Status = VenueUserManagementStatus.Loading,
                Message = "Loading venue users...",
                LastAttemptAt = attemptAt,
            });

        var context = await GetAuthorizedContextAsync(venue, cancellationToken);
        if (!context.Success)
        {
            SetLoadFailure(venue, context.Failure!, attemptAt);
            return ApiResult<VenueUserManagementViewResponse>.Failed(context.Failure!);
        }

        var result = await apiClient.GetVenueUsersAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);

        if (!result.Success || result.Value is null)
        {
            SetLoadFailure(venue, result.Failure!, attemptAt);
            return result;
        }

        snapshots.AddOrUpdate(
            venue.ProfileId,
            _ => new VenueUserManagementSnapshot(
                VenueUserManagementStatus.Ready,
                "Venue users loaded.",
                result.Value,
                null,
                attemptAt),
            (_, previous) => previous with
            {
                Status = VenueUserManagementStatus.Ready,
                Message = "Venue users loaded.",
                View = result.Value,
                LastAttemptAt = attemptAt,
            });

        return result;
    }

    private async Task RefreshAfterMutationAsync(
        VenueConnectionConfiguration venue,
        AuthorizedContext context,
        CancellationToken cancellationToken)
    {
        var result = await apiClient.GetVenueUsersAsync(
            context.BaseUri!,
            context.AccessToken!,
            cancellationToken);

        if (!result.Success || result.Value is null)
        {
            log.Warning(
                "Venue-user mutation succeeded, but refresh failed for profile {ProfileId}. Code: {Code}.",
                venue.ProfileId,
                result.Failure?.Code);
            return;
        }

        snapshots.AddOrUpdate(
            venue.ProfileId,
            _ => new VenueUserManagementSnapshot(
                VenueUserManagementStatus.Ready,
                "Venue users loaded.",
                result.Value,
                null,
                DateTimeOffset.UtcNow),
            (_, previous) => previous with
            {
                Status = VenueUserManagementStatus.Ready,
                Message = "Venue users loaded.",
                View = result.Value,
                LastAttemptAt = DateTimeOffset.UtcNow,
            });
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
                "This venue is saved in visitor mode and has no registered staff device."));
        }

        if (!identityProvider.TryGetCurrent(out var identity, out var identityError))
        {
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "PLAYER_NOT_AVAILABLE",
                identityError));
        }

        if (!PartyPulseApiClient.TryCreateBaseUri(configuration.ApiBaseUrl, out var baseUri, out var uriError))
        {
            return AuthorizedContext.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "INVALID_API_BASE_URL",
                uriError));
        }

        var accessToken = await authentication.EnsureAccessTokenAsync(
            venue,
            identity!,
            configuration.ApiBaseUrl,
            cancellationToken);
        if (!accessToken.Success || string.IsNullOrWhiteSpace(accessToken.AccessToken))
        {
            return AuthorizedContext.Failed(accessToken.Failure ?? new ApiFailure(
                ApiFailureKind.Authentication,
                "ACCESS_TOKEN_NOT_AVAILABLE",
                "A valid access token could not be obtained."));
        }

        return AuthorizedContext.Succeeded(baseUri!, accessToken.AccessToken);
    }

    private void SetLoadFailure(
        VenueConnectionConfiguration venue,
        ApiFailure failure,
        DateTimeOffset attemptAt)
    {
        var status = failure.Kind == ApiFailureKind.Permission
            ? VenueUserManagementStatus.Denied
            : VenueUserManagementStatus.Failed;

        snapshots.AddOrUpdate(
            venue.ProfileId,
            _ => new VenueUserManagementSnapshot(
                status,
                failure.Message,
                null,
                null,
                attemptAt),
            (_, previous) => previous with
            {
                Status = status,
                Message = failure.Message,
                View = null,
                LastAttemptAt = attemptAt,
            });
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

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized[0] == '@' ? normalized[1..] : normalized;
    }

    private static string GetRecoveryKey(Guid profileId, int userId) =>
        $"{profileId:N}:{userId}";

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
