using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PartyPulse.Contracts.PartyFinder;
using PartyPulse.Models;
using PartyPulse.Services;

namespace PartyPulse.PartyFinder;

public sealed unsafe partial class PartyFinderAutomationService
{
    private static readonly TimeSpan DevListingLifetime = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan DevRenewalLead = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DevOptionalGuard = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DevPlanTolerance = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DevConfirmationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DevGameTimerTolerance = TimeSpan.FromSeconds(2);

    private VenueConnectionConfiguration? devVenue;
    private ulong devCharacterId;
    private ulong devListingId;
    private DateTimeOffset? devExpiresAt;
    private DateTimeOffset? devRenewAt;
    private DateTimeOffset? devRepostAt;
    private DateTimeOffset? devRetryAt;
    private DateTimeOffset? devLastOptionalAttemptAt;
    private DateTimeOffset? devRecruitmentLostAt;
    private DateTimeOffset devSubmittedAt;
    private uint devTimeLeftBeforeSubmit;
    private bool devOpenedDetail;
    private bool devRequestedDetail;
    private bool devRetriedConfirmation;
    private bool devOrdinaryRenewalAttempted;

    public PartyFinderCoordinator? Coordinator { get; set; }
    public bool IsUsingCoordination => devVenue is not null;
    public long? CoordinatedOpeningId => IsUsingCoordination ? openingId : null;

    private bool BeginDevSession(VenueConnectionConfiguration venue, bool alreadyRecruiting, DateTimeOffset now)
    {
        if (Coordinator is not { } coordinator || playerState.ContentId == 0)
            return false;

        var settings = coordinator.GetPlan(venue, openingId)?.Settings ?? coordinator.GetSettings(venue, openingId);
        if (settings?.Enabled != true)
            return false;

        devVenue = venue;
        devCharacterId = playerState.ContentId;
        if (!alreadyRecruiting)
            return false;

        State = PartyFinderAutomationState.ReadingExistingListing;
        stageDeadline = now + DevConfirmationTimeout;
        StatusMessage = "Reading the remaining time on the live Party Finder listing.";
        return true;
    }

    private void EndDevSession(string kind)
    {
        if (devVenue is { } venue && devExpiresAt.HasValue && playerState.ContentId == devCharacterId)
            ReportDevEvent(venue, kind, publications.GetSnapshot(venue).EstimatedServerNow);

        devVenue = null;
        devCharacterId = 0;
        devListingId = 0;
        devExpiresAt = null;
        devRenewAt = null;
        devRepostAt = null;
        devRetryAt = null;
        devLastOptionalAttemptAt = null;
        devRecruitmentLostAt = null;
        devSubmittedAt = default;
        devTimeLeftBeforeSubmit = 0;
        devOpenedDetail = false;
        devRequestedDetail = false;
        devRetriedConfirmation = false;
        devOrdinaryRenewalAttempted = false;
    }

    private void TickDevAdoption(
        VenueConnectionConfiguration venue,
        AgentLookingForGroup* agent,
        DateTimeOffset now,
        DateTimeOffset serverNow)
    {
        if (condition[ConditionFlag.UsingPartyFinder] && TryReadDevOwnDetail(agent, out var remaining))
        {
            var listingId = agent->LastViewedListing.ListingId;
            CloseDevOwnDetail(agent);
            ConfirmDevListing(venue, listingId, now, serverNow, TimeSpan.FromSeconds(remaining),
                "started", "Live Party Finder listing adopted.");
            return;
        }

        if (stageDeadline is { } deadline && now >= deadline)
        {
            Stop("Party Finder could not read the remaining time. Open your own listing detail, then start the refresher again.");
            return;
        }

        RequestDevOwnDetail(agent, now);
    }

    private void TickDevRenewal(
        VenueConnectionConfiguration venue,
        AgentLookingForGroup* agent,
        DateTimeOffset now,
        DateTimeOffset serverNow)
    {
        if (!condition[ConditionFlag.UsingPartyFinder])
        {
            TickDevMissingListing(venue, agent, now, serverNow);
            return;
        }

        ClearTransientInterruption(TransientIssue.RecruitmentUnavailable);
        devRecruitmentLostAt = null;
        if (devRenewAt is not { } renewAt)
            return;

        var optional = GetEligibleDevRepost(venue, now, serverNow);
        var next = optional is { } repostAt ? now + (repostAt - serverNow) : renewAt;
        if (NextRefreshAt is not { } previous || (previous - next).Duration() >= TimeSpan.FromSeconds(1))
        {
            NextRefreshAt = next;
            var action = optional.HasValue ? "Spacing repost" : "Expiry renewal";
            StatusMessage = $"{action} at {VenueTimeZone.Format(venue, next, "t")}.";
        }

        if (now < next || now < devRetryAt || playerState.ContentId != devCharacterId)
            return;

        if (GetVisibleDevAddon("LookingForGroupCondition") != null ||
            GetVisibleDevAddon("LookingForGroupDetail") != null && !IsDevOwnDetail(agent))
        {
            StatusMessage = "Party Finder renewal is waiting for the other recruitment window to close.";
            return;
        }

        devRepostAt = optional;
        devOrdinaryRenewalAttempted |= optional is null;
        devTimeLeftBeforeSubmit = 0;
        devOpenedDetail = GetVisibleDevAddon("LookingForGroupDetail") == null;
        BeginRefresh(now);
        if (State == PartyFinderAutomationState.WaitingForRefresh)
            devRetryAt = now + TransientRetryDelay;
    }

    private DateTimeOffset? GetEligibleDevRepost(
        VenueConnectionConfiguration venue,
        DateTimeOffset now,
        DateTimeOffset serverNow)
    {
        if (devExpiresAt is not { } expiry || expiry <= now + DevOptionalGuard ||
            devLastOptionalAttemptAt is { } attempted && now < attempted + DevListingLifetime)
            return null;

        var plan = Coordinator?.GetPlan(venue, openingId);
        if (plan is not { Settings.Enabled: true, HasActiveListing: true, RepostAt: { } repostAt, ListingExpiresAt: { } reportedExpiry })
            return null;

        var estimatedServerExpiry = serverNow + (expiry - now);
        if ((reportedExpiry - estimatedServerExpiry).Duration() > DevPlanTolerance ||
            repostAt < serverNow - DevPlanTolerance ||
            repostAt >= estimatedServerExpiry - DevOptionalGuard)
            return null;

        return repostAt;
    }

    private bool PrepareDevRefresh(AgentLookingForGroup* agent, DateTimeOffset now)
    {
        if (IsDevOwnDetail(agent))
        {
            devListingId = agent->LastViewedListing.ListingId;
            if (TryReadDevOwnDetail(agent, out var remaining))
                devTimeLeftBeforeSubmit = remaining;
            return true;
        }

        if (stageDeadline is { } deadline && now >= deadline)
            FailDevRefresh(now, "The active Party Finder listing detail could not be verified.");
        return false;
    }

    private bool CanSubmitDevRefresh(
        VenueConnectionConfiguration venue,
        AgentLookingForGroup* agent,
        DateTimeOffset now,
        DateTimeOffset serverNow)
    {
        if (!condition[ConditionFlag.UsingPartyFinder] || playerState.ContentId != devCharacterId ||
            devListingId == 0 || agent->LastViewedListing.ListingId != devListingId ||
            agent->LastViewedListing.LeaderContentId != devCharacterId)
        {
            CloseDevConditions();
            FailDevRefresh(now, "The Party Finder listing changed before renewal could be submitted.");
            return false;
        }

        if (devRepostAt is { } target && GetEligibleDevRepost(venue, now, serverNow) != target)
        {
            CloseDevConditions();
            CloseDevOwnDetail(agent);
            FailDevRefresh(now, "The spacing repost was superseded. Waiting for the updated schedule.");
            return false;
        }

        return true;
    }

    private void BeginDevRefreshConfirmation(DateTimeOffset now)
    {
        if (devRepostAt.HasValue)
            devLastOptionalAttemptAt = now;
        devSubmittedAt = now;
        devRequestedDetail = false;
        devRetriedConfirmation = false;
        devRetryAt = null;
        stageDeadline = now + DevConfirmationTimeout;
        State = PartyFinderAutomationState.VerifyingRefresh;
        StatusMessage = "Waiting for Party Finder to confirm the renewed listing.";
    }

    private void TickDevRefreshConfirmation(
        VenueConnectionConfiguration venue,
        AgentLookingForGroup* agent,
        DateTimeOffset now,
        DateTimeOffset serverNow)
    {
        var hasListing = condition[ConditionFlag.UsingPartyFinder] && playerState.ContentId == devCharacterId;
        if (hasListing)
        {
            devRecruitmentLostAt = null;
            if (TryReadDevOwnDetail(agent, out var remaining))
            {
                var listingId = agent->LastViewedListing.ListingId;
                if (listingId != devListingId || IsDevTimerReset(now, remaining))
                {
                    CloseDevOwnDetail(agent);
                    ConfirmDevListing(venue, listingId, now, serverNow, TimeSpan.FromSeconds(remaining),
                        "renewed", "Party Finder renewal confirmed.");
                    return;
                }
            }

            if (!devRequestedDetail && now >= devSubmittedAt + TimeSpan.FromSeconds(1) &&
                GetVisibleDevAddon("LookingForGroupCondition") == null)
            {
                CloseDevOwnDetail(agent);
                RequestDevOwnDetail(agent, now);
            }
        }
        else
        {
            devRecruitmentLostAt ??= now;
        }

        if (stageDeadline is { } deadline && now >= deadline)
        {
            CloseDevOwnDetail(agent);
            if (!hasListing && CanRecoverDevExpiredListing(now))
            {
                TickDevMissingListing(venue, agent, now, serverNow);
            }
            else if (!devRetriedConfirmation)
            {
                // A successful submit might leave the old listing ID and cached detail in place.
                // Read it again without resubmitting the recruitment.
                devRetriedConfirmation = true;
                devRequestedDetail = false;
                devRetryAt = null;
                stageDeadline = now + DevConfirmationTimeout;
            }
            else
            {
                Stop("Party Finder could not confirm the timer reset. The live listing was left in place.");
            }
        }
    }

    private bool IsDevTimerReset(DateTimeOffset now, uint remaining)
    {
        // Same-ID refreshes need a timer change. The last viewed detail can survive the submit.
        if (remaining < DevListingLifetime.TotalSeconds - 30)
            return false;
        if (devTimeLeftBeforeSubmit != 0)
            return remaining > devTimeLeftBeforeSubmit + 2;
        return devRequestedDetail && devExpiresAt is { } expiry &&
            now + TimeSpan.FromSeconds(remaining) > expiry + DevGameTimerTolerance;
    }

    private void TickDevMissingListing(
        VenueConnectionConfiguration venue,
        AgentLookingForGroup* agent,
        DateTimeOffset now,
        DateTimeOffset serverNow)
    {
        var missingSince = devRecruitmentLostAt ??= now;
        if (CanRecoverDevExpiredListing(now))
        {
            ReportDevEvent(venue, "removed", serverNow);
            devListingId = 0;
            devExpiresAt = null;
            devRenewAt = null;
            devRepostAt = null;
            devRetryAt = null;
            devRecruitmentLostAt = null;
            ClearTransientInterruption(TransientIssue.RecruitmentUnavailable);
            agent->StoredRecruitmentInfo.CommentString = expectedText;
            agent->Show();
            State = PartyFinderAutomationState.WaitingForInitialWindow;
            stageDeadline = now + AddonTimeout;
            StatusMessage = "The listing expired during renewal. Opening Party Finder to repost it.";
            return;
        }

        if (IsDevListingAbsent() && now - missingSince >= TransientInterruptionGrace)
        {
            EndDevSession("removed");
            Stop("Party Finder refresher stopped because the listing was removed.");
            return;
        }

        PauseForTransientInterruption(
            now,
            TransientIssue.RecruitmentUnavailable,
            "The Party Finder listing is temporarily unavailable.",
            "Party Finder refresher stopped because the listing did not return after the zone-transition grace period.");
    }

    private bool CanRecoverDevExpiredListing(DateTimeOffset now) =>
        devOrdinaryRenewalAttempted && IsDevListingAbsent() &&
        devExpiresAt is { } expiry && devRecruitmentLostAt is { } missingSince &&
        missingSince >= expiry - DevGameTimerTolerance && now >= expiry + DevGameTimerTolerance &&
        now - missingSince >= DevGameTimerTolerance && now - missingSince < TransientInterruptionGrace &&
        GetVisibleDevAddon("LookingForGroupCondition") == null;

    private bool IsDevListingAbsent() =>
        playerState.ContentId == devCharacterId &&
        !condition[ConditionFlag.UsingPartyFinder] &&
        !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51];

    private void ConfirmDevListing(
        VenueConnectionConfiguration venue,
        ulong listingId,
        DateTimeOffset now,
        DateTimeOffset serverNow,
        TimeSpan remaining,
        string kind,
        string prefix)
    {
        devListingId = listingId;
        devExpiresAt = now + remaining;
        devRenewAt = now + (remaining > DevRenewalLead ? remaining - DevRenewalLead : TimeSpan.Zero);
        devRepostAt = null;
        devRetryAt = null;
        devRecruitmentLostAt = null;
        devRequestedDetail = false;
        devOpenedDetail = false;
        devOrdinaryRenewalAttempted = false;
        stageDeadline = null;
        State = PartyFinderAutomationState.WaitingForRefresh;
        NextRefreshAt = devRenewAt;
        StatusMessage = $"{prefix} Expiry renewal at {VenueTimeZone.Format(venue, devRenewAt.Value, "t")}.";
        ReportDevEvent(venue, kind, serverNow, serverNow + remaining);
    }

    private void FailDevRefresh(DateTimeOffset now, string message)
    {
        devRepostAt = null;
        devRetryAt = now + TransientRetryDelay;
        stageDeadline = null;
        NextRefreshAt = devRenewAt;
        State = PartyFinderAutomationState.WaitingForRefresh;
        StatusMessage = message;
    }

    private void ReportDevEvent(
        VenueConnectionConfiguration venue,
        string kind,
        DateTimeOffset serverNow,
        DateTimeOffset? expiresAt = null) =>
        Coordinator?.Report(venue, openingId, new PartyFinderDevEventRequest(Guid.NewGuid(), kind, serverNow, expiresAt));

    private void RequestDevOwnDetail(AgentLookingForGroup* agent, DateTimeOffset now)
    {
        if (devRequestedDetail || now < devRetryAt ||
            playerState.ContentId != devCharacterId || GetVisibleDevAddon("LookingForGroupDetail") != null)
            return;
        devRetryAt = now + TransientRetryDelay;
        devRequestedDetail = agent->OpenListingByContentId(devCharacterId);
        devOpenedDetail = devRequestedDetail;
    }

    private bool TryReadDevOwnDetail(AgentLookingForGroup* agent, out uint remaining)
    {
        remaining = IsDevOwnDetail(agent) ? agent->LastViewedListing.TimeLeft : 0;
        return remaining > 0 && remaining <= DevListingLifetime.TotalSeconds;
    }

    private bool IsDevOwnDetail(AgentLookingForGroup* agent) =>
        GetVisibleDevAddon("LookingForGroupDetail") != null &&
        agent->ListingContentId == devCharacterId &&
        agent->LastViewedListing.LeaderContentId == devCharacterId &&
        agent->LastViewedListing.ListingId != 0;

    private void CloseDevOwnDetail(AgentLookingForGroup* agent)
    {
        if (!devOpenedDetail || !IsDevOwnDetail(agent))
            return;
        var addon = GetVisibleDevAddon("LookingForGroupDetail");
        if (addon != null)
            addon->Close(true);
        devOpenedDetail = false;
    }

    private void CloseDevConditions()
    {
        var addon = GetVisibleDevAddon("LookingForGroupCondition");
        if (addon != null)
            addon->Close(true);
    }

    private AtkUnitBase* GetVisibleDevAddon(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name, 1).Address;
        return addon != null && addon->IsVisible ? addon : null;
    }
}
