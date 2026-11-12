using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PartyPulse.Api;
using PartyPulse.Authentication;
using PartyPulse.Contracts.OpeningPublications;
using PartyPulse.Contracts.PartyFinder;
using PartyPulse.Models;
using PartyPulse.OpeningPublications;
using PartyPulse.Services;

namespace PartyPulse.PartyFinder;

public sealed class PartyFinderCoordinator(
    Configuration configuration,
    AuthenticationManager authentication,
    PartyPulseApiClient apiClient,
    PlayerIdentityProvider identities,
    VenueLocationProvider locations,
    OpeningPublicationManagementManager publications,
    IPluginLog log,
    CancellationToken lifetimeToken)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettingsInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackgroundPublicationInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<(Guid ProfileId, long OpeningId), OpeningState> states = new();
    private readonly PartyFinderStartReminder reminder = new();
    private DateTimeOffset nextDiscoveryAt;

    public PartyFinderDevSettings? GetSettings(VenueConnectionConfiguration venue, long openingId) =>
        Find(venue, openingId)?.Settings;

    public bool IsSettingsBusy(VenueConnectionConfiguration venue, long openingId) =>
        Find(venue, openingId) is { SettingsBusy: true } or { SettingsToSave: not null } or
            { Settings: null, Pending: not null };

    public string? GetSettingsMessage(VenueConnectionConfiguration venue, long openingId) =>
        Find(venue, openingId)?.SettingsMessage;

    public PartyFinderDevPollResponse? GetPlan(VenueConnectionConfiguration venue, long openingId) =>
        Find(venue, openingId) is { Plan: not null } state &&
        DateTimeOffset.UtcNow - state.PlanReceivedAt <= PlanLifetime
            ? state.Plan
            : null;

    public void EnsureSettingsLoaded(VenueConnectionConfiguration venue, long openingId)
    {
        var state = GetOrCreate(venue, openingId);
        if (state.Pending is not null || DateTimeOffset.UtcNow < state.NextSettingsAt)
            return;

        StartSettingsRequest(state, HttpMethod.Get, null);
    }

    public void SaveSettings(VenueConnectionConfiguration venue, long openingId, PartyFinderDevSettings settings)
    {
        var state = GetOrCreate(venue, openingId);
        if (state.Pending is not null)
        {
            state.SettingsToSave = settings;
            state.SettingsMessage = "Saving Party Finder settings…";
            return;
        }

        StartSettingsRequest(state, HttpMethod.Put, settings);
    }

    public void Report(VenueConnectionConfiguration venue, long openingId, PartyFinderDevEventRequest report)
    {
        if (!venue.IsRegistered || lifetimeToken.IsCancellationRequested ||
            !identities.TryGetCurrent(out var identity, out _) || identity is null)
            return;

        var state = GetOrCreate(venue, openingId);
        state.Events.Enqueue((identity, report));
        state.Plan = null;
        state.NextPollAt = default;
    }

    public void Tick(
        PlayerIdentity identity,
        IReadOnlyList<VenueConnectionConfiguration> venues,
        Guid? runningProfileId,
        long? runningOpeningId,
        bool isRecruiting,
        Action<string> notify,
        Action<VenueConnectionConfiguration> ensurePublicationsLoaded)
    {
        var now = DateTimeOffset.UtcNow;
        if (now >= nextDiscoveryAt)
        {
            nextDiscoveryAt = now.Add(PollInterval);
            foreach (var venue in venues)
            {
                var snapshot = publications.GetSnapshot(venue);
                var lastPublicationCheck = snapshot.LastAttemptAt ?? snapshot.ReceivedAt;
                if (lastPublicationCheck is null || now - lastPublicationCheck >= BackgroundPublicationInterval)
                    ensurePublicationsLoaded(venue);
                if (snapshot.View is not { Capabilities.CanUsePartyFinder: true } view)
                    continue;

                var serverNow = snapshot.EstimatedServerNow;
                foreach (var opening in view.Openings.Where(opening =>
                    opening.OpensAt.AddHours(-1) <= serverNow && serverNow < opening.ClosesAt && IsAtOpening(opening)))
                {
                    GetOrCreate(venue, opening.OpeningId).Opening = opening;
                }
            }
        }

        foreach (var state in states.Values)
        {
            if (state.Pending is { IsCompleted: true })
            {
                state.CompletePending!();
                state.Pending = null;
                state.CompletePending = null;
            }
            if (state.Pending is not null)
                continue;

            if (state.Remind)
            {
                state.Remind = false;
                var snapshot = publications.GetSnapshot(state.Venue);
                var serverNow = snapshot.EstimatedServerNow;
                if (!isRecruiting && runningProfileId is null && state.Venue.IsRegistered &&
                    snapshot.View is { Capabilities.CanUsePartyFinder: true } view &&
                    view.Openings.FirstOrDefault(opening => opening.OpeningId == state.OpeningId) is { } reminderOpening &&
                    reminderOpening.OpensAt.AddHours(-1) <= serverNow && serverNow < reminderOpening.ClosesAt &&
                    IsAtOpening(reminderOpening))
                {
                    reminder.TryRemind(state.Venue.VenueId, state.OpeningId, identity, notify, state.Venue.DisplayLabel);
                }
            }

            if (state.SettingsToSave is { } settings)
            {
                state.SettingsToSave = null;
                StartSettingsRequest(state, HttpMethod.Put, settings);
                continue;
            }

            while (state.Events.TryPeek(out var queued) && queued.Identity != identity)
                state.Events.Dequeue();
            if (state.Events.TryPeek(out var item) && now >= state.NextEventAt)
            {
                SendEvent(state, item.Request, identity);
                continue;
            }

            if (now < state.NextPollAt)
                continue;

            var atVenue = state.Opening is { } opening &&
                publications.GetSnapshot(state.Venue) is { View.Capabilities.CanUsePartyFinder: true } snapshotAtVenue &&
                opening.OpensAt.AddHours(-1) <= snapshotAtVenue.EstimatedServerNow &&
                snapshotAtVenue.EstimatedServerNow < opening.ClosesAt && IsAtOpening(opening);
            var isRunning = runningProfileId == state.Venue.ProfileId && runningOpeningId == state.OpeningId;
            if (atVenue || isRunning)
            {
                StartPoll(state, identity, atVenue, now);
            }
            else
            {
                state.NextPollAt = now.Add(PollInterval);
            }
        }
    }

    public void Clear()
    {
        states.Clear();
        nextDiscoveryAt = default;
    }

    public void RemoveProfile(Guid profileId)
    {
        foreach (var key in states.Keys.Where(key => key.ProfileId == profileId).ToArray())
            states.Remove(key);
    }

    private void StartSettingsRequest(OpeningState state, HttpMethod method, PartyFinderDevSettings? settings)
    {
        if (!identities.TryGetCurrent(out var identity, out _) || identity is null)
            return;

        state.NextSettingsAt = DateTimeOffset.UtcNow.Add(SettingsInterval);
        state.SettingsBusy = true;
        state.SettingsMessage = null;
        var request = SendAsync<PartyFinderDevSettings>(state, identity, method, "settings", settings, ValidSettings);
        SetPending(state, request, result =>
        {
            state.SettingsBusy = false;
            if (result.Success)
            {
                state.Settings = result.Value;
                if (method == HttpMethod.Put)
                {
                    state.SettingsMessage = "Party Finder settings saved.";
                    state.Plan = null;
                    state.NextPollAt = default;
                }
            }
            else
            {
                state.SettingsMessage = result.Failure?.Message;
            }
        });
    }

    private void StartPoll(OpeningState state, PlayerIdentity identity, bool atVenue, DateTimeOffset now)
    {
        state.NextPollAt = now.Add(PollInterval);
        var request = SendAsync<PartyFinderDevPollResponse>(state, identity, HttpMethod.Post, "poll",
            new PartyFinderDevPollRequest(atVenue), response => response.ServerNow != default && ValidSettings(response.Settings));
        SetPending(state, request, result =>
        {
            if (result.Value is not { } plan)
            {
                state.Plan = null;
                state.NextPollAt = now.Add(SettingsInterval);
                return;
            }

            state.Settings = plan.Settings;
            if (state.Events.Count == 0)
            {
                state.Plan = plan;
                state.PlanReceivedAt = DateTimeOffset.UtcNow;
                state.Remind = plan.NotifyToStart && !plan.HasActiveListing;
            }
        });
    }

    private void SendEvent(OpeningState state, PartyFinderDevEventRequest report, PlayerIdentity identity)
    {
        var request = SendAsync<PartyFinderDevEventResponse>(state, identity, HttpMethod.Post, "events",
            report, response => response.Accepted);
        SetPending(state, request, result =>
        {
            if (result.Failure is { Kind: ApiFailureKind.Transport or ApiFailureKind.Unavailable or ApiFailureKind.RateLimited } failure &&
                state.EventAttempts++ < 4)
            {
                state.NextEventAt = DateTimeOffset.UtcNow.Add(failure.RetryAfter ?? PollInterval);
                return;
            }

            state.Events.Dequeue();
            state.EventAttempts = 0;
            state.NextEventAt = default;
            state.NextPollAt = default;
            if (!result.Success)
                log.Debug("Party Finder coordination event was not recorded. Code {Code}.", result.Failure?.Code ?? "UNKNOWN");
        });
    }

    private async Task<ApiResult<T>> SendAsync<T>(OpeningState state, PlayerIdentity identity, HttpMethod method,
        string action, object? body, Func<T, bool> validate)
    {
        try
        {
            if (!PartyPulseApiClient.TryCreateBaseUri(configuration.ApiBaseUrl, out var baseUri, out var error))
                return ApiResult<T>.Failed(new(ApiFailureKind.Validation, "INVALID_API_URL", error));

            var token = await authentication.EnsureAccessTokenAsync(state.Venue, identity, configuration.ApiBaseUrl, lifetimeToken);
            if (!token.Success)
                return ApiResult<T>.Failed(token.Failure!);

            return await apiClient.SendAuthorizedAsync(baseUri!, method,
                $"api/v1/dev/party-finder/{state.OpeningId}/{action}", token.AccessToken!, body, validate, lifetimeToken);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            return ApiResult<T>.Failed(new(ApiFailureKind.Unavailable, "REQUEST_CANCELLED", "PartyPulse is unloading."));
        }
        catch (Exception exception)
        {
            log.Error(exception, "Party Finder coordination request failed.");
            return ApiResult<T>.Failed(new(ApiFailureKind.Unknown, "PF_COORDINATION_REQUEST_FAILED", "The Party Finder coordination request failed."));
        }
    }

    private static void SetPending<T>(OpeningState state, Task<ApiResult<T>> request, Action<ApiResult<T>> complete)
    {
        state.Pending = request;
        state.CompletePending = () => complete(request.GetAwaiter().GetResult());
    }

    private static bool ValidSettings(PartyFinderDevSettings? settings) =>
        settings is { MaximumSpacingMinutes: 0 or >= 10 and <= 60,
            Strategy: PartyFinderDevStrategies.ExpiryOnly or PartyFinderDevStrategies.PredecessorChain or PartyFinderDevStrategies.BalanceAllGaps };

    private bool IsAtOpening(OpeningPublicationOpeningSummary opening) => locations.IsAtOpeningLocation(
        opening.AddressWorldName, opening.AddressCityName, opening.AddressWard, opening.AddressPlot,
        opening.LocationType, opening.OutdoorLocationName, out _);

    private OpeningState? Find(VenueConnectionConfiguration venue, long openingId) =>
        states.GetValueOrDefault((venue.ProfileId, openingId));

    private OpeningState GetOrCreate(VenueConnectionConfiguration venue, long openingId)
    {
        var key = (venue.ProfileId, openingId);
        if (!states.TryGetValue(key, out var state))
            states.Add(key, state = new(venue, openingId));
        return state;
    }

    private sealed class OpeningState(VenueConnectionConfiguration venue, long openingId)
    {
        public VenueConnectionConfiguration Venue { get; } = venue;
        public long OpeningId { get; } = openingId;
        public OpeningPublicationOpeningSummary? Opening;
        public PartyFinderDevSettings? Settings;
        public PartyFinderDevSettings? SettingsToSave;
        public PartyFinderDevPollResponse? Plan;
        public DateTimeOffset PlanReceivedAt;
        public DateTimeOffset NextPollAt;
        public DateTimeOffset NextSettingsAt;
        public DateTimeOffset NextEventAt;
        public Task? Pending;
        public Action? CompletePending;
        public bool Remind;
        public bool SettingsBusy;
        public string? SettingsMessage;
        public int EventAttempts;
        public Queue<(PlayerIdentity Identity, PartyFinderDevEventRequest Request)> Events { get; } = new();
    }
}
