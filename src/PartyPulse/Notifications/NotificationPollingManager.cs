using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.Notifications;

public sealed record QueuedPartyPulseNotification(
    Guid VenueProfileId,
    UserNotificationSummary Notification);

public sealed record NotificationVenueSummary(
    int UnseenNotificationCount,
    int PendingSettlementCount,
    bool CanManageSettlements,
    DateTimeOffset UpdatedAt);

public sealed class NotificationPollingManager : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private readonly Configuration configuration;
    private readonly AuthenticationManager authentication;
    private readonly PartyPulseApiClient apiClient;
    private readonly PlayerIdentityProvider identityProvider;
    private readonly ConcurrentQueue<QueuedPartyPulseNotification> queue = new();
    private readonly ConcurrentDictionary<Guid, NotificationVenueSummary> summaries = new();
    private readonly HashSet<(Guid ProfileId, long NotificationId)> announced = new();
    private readonly object announcedLock = new();
    private long nextPollUtcTicks;
    private int polling;

    public NotificationPollingManager(
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

    public bool IsPollDue =>
        Volatile.Read(ref polling) == 0 &&
        DateTimeOffset.UtcNow.UtcDateTime.Ticks >= Volatile.Read(ref nextPollUtcTicks);

    public NotificationVenueSummary? GetSummary(Guid profileId) =>
        summaries.TryGetValue(profileId, out var summary) ? summary : null;

    public bool TryDequeue(out QueuedPartyPulseNotification? notification) =>
        queue.TryDequeue(out notification);

    public async Task PollDueAsync(
        IReadOnlyCollection<VenueConnectionConfiguration> venues,
        CancellationToken cancellationToken)
    {
        if (venues.Count == 0 || Interlocked.CompareExchange(ref polling, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(
            ref nextPollUtcTicks,
            DateTimeOffset.UtcNow.Add(PollInterval).UtcDateTime.Ticks);

        try
        {
            if (!identityProvider.TryGetCurrent(out var identity, out _))
            {
                return;
            }

            if (!PartyPulseApiClient.TryCreateBaseUri(
                    configuration.ApiBaseUrl,
                    out var baseUri,
                    out _))
            {
                return;
            }

            foreach (var venue in venues.Where(static value => value.IsRegistered))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var token = await authentication.EnsureAccessTokenAsync(
                    venue,
                    identity!,
                    configuration.ApiBaseUrl,
                    cancellationToken);
                if (!token.Success || string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    continue;
                }

                var result = await apiClient.PollNotificationsAsync(
                    baseUri!,
                    token.AccessToken,
                    20,
                    cancellationToken);
                if (!result.Success || result.Value is null)
                {
                    continue;
                }

                summaries[venue.ProfileId] = new NotificationVenueSummary(
                    result.Value.UnseenNotificationCount,
                    result.Value.PendingSettlementCount,
                    result.Value.CanManageSettlements,
                    DateTimeOffset.UtcNow);

                foreach (var item in result.Value.Notifications)
                {
                    var key = (venue.ProfileId, item.NotificationId);
                    lock (announcedLock)
                    {
                        if (!announced.Add(key))
                        {
                            continue;
                        }
                    }

                    queue.Enqueue(new QueuedPartyPulseNotification(venue.ProfileId, item));
                }
            }
        }
        finally
        {
            Volatile.Write(ref polling, 0);
        }
    }

    public async Task<ApiResult<MarkNotificationSeenResponse>> MarkSeenAsync(
        VenueConnectionConfiguration venue,
        long notificationId,
        bool dismissed,
        CancellationToken cancellationToken)
    {
        if (!identityProvider.TryGetCurrent(out var identity, out var identityError))
        {
            return ApiResult<MarkNotificationSeenResponse>.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "PLAYER_NOT_AVAILABLE",
                identityError));
        }

        if (!PartyPulseApiClient.TryCreateBaseUri(
                configuration.ApiBaseUrl,
                out var baseUri,
                out var uriError))
        {
            return ApiResult<MarkNotificationSeenResponse>.Failed(new ApiFailure(
                ApiFailureKind.Validation,
                "INVALID_API_BASE_URL",
                uriError));
        }

        var token = await authentication.EnsureAccessTokenAsync(
            venue,
            identity!,
            configuration.ApiBaseUrl,
            cancellationToken);
        if (!token.Success || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return ApiResult<MarkNotificationSeenResponse>.Failed(token.Failure ?? new ApiFailure(
                ApiFailureKind.Authentication,
                "ACCESS_TOKEN_NOT_AVAILABLE",
                "A valid access token could not be obtained."));
        }

        return await apiClient.MarkNotificationSeenAsync(
            baseUri!,
            token.AccessToken,
            notificationId,
            dismissed,
            cancellationToken);
    }

    public void PollSoon() => Volatile.Write(ref nextPollUtcTicks, 0);

    public void Clear()
    {
        while (queue.TryDequeue(out _))
        {
        }

        summaries.Clear();
        lock (announcedLock)
        {
            announced.Clear();
        }

        PollSoon();
    }

    public void RemoveProfile(Guid profileId)
    {
        summaries.TryRemove(profileId, out _);
        lock (announcedLock)
        {
            announced.RemoveWhere(value => value.ProfileId == profileId);
        }
    }

    public void Dispose() => Clear();
}
