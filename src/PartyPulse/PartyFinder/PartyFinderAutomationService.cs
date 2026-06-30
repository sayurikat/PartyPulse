using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PartyPulse.Models;
using PartyPulse.Services;
using PartyPulse.OpeningPublications;

namespace PartyPulse.PartyFinder;

public enum PartyFinderAutomationState
{
    Idle,
    WaitingForInitialWindow,
    WaitingForInitialConditions,
    AwaitingInitialRecruitment,
    WaitingForRefresh,
    WaitingForDetail,
    WaitingForConditions,
}

public sealed unsafe class PartyFinderAutomationService
{
    private enum TransientIssue
    {
        None,
        AgentUnavailable,
        ListingStateUnavailable,
        RecruitmentUnavailable,
        OpenListingFailed,
    }

    private static readonly TimeSpan AddonTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TransientInterruptionGrace = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(5);
    private readonly Configuration configuration;
    private readonly OpeningPublicationManagementManager publications;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;

    private Guid profileId;
    private long openingId;
    private string publicationCode = string.Empty;
    private string expectedText = string.Empty;
    private DateTimeOffset openingClosesAt;
    private TimeSpan interval = TimeSpan.FromMinutes(60);
    private DateTimeOffset? stageDeadline;
    private DateTimeOffset? transientInterruptionStartedAt;
    private TransientIssue transientIssue;

    public PartyFinderAutomationService(
        Configuration configuration,
        OpeningPublicationManagementManager publications,
        ICondition condition,
        IGameGui gameGui,
        IPlayerState playerState,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.publications = publications;
        this.condition = condition;
        this.gameGui = gameGui;
        this.playerState = playerState;
        this.log = log;
    }

    public PartyFinderAutomationState State { get; private set; }
    public string StatusMessage { get; private set; } = "Party Finder refresher is stopped.";
    public DateTimeOffset? NextRefreshAt { get; private set; }
    public bool IsRunning => State != PartyFinderAutomationState.Idle;
    public Guid ProfileId => profileId;
    public long OpeningId => openingId;
    public string ExpectedText => expectedText;

    public bool Start(
        VenueConnectionConfiguration venue,
        ActivePartyFinderPublication publication,
        int intervalMinutes,
        out string error)
    {
        error = string.Empty;
        if (IsRunning)
        {
            error = "A Party Finder refresher is already running. Stop it before starting another.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(publication.Text))
        {
            error = "The active Party Finder text is empty.";
            return false;
        }
        if (publication.Text.Contains('\n') || publication.Text.Contains('\r') || publication.Text.Length > 192)
        {
            error = "Party Finder text must be a single line of at most 192 characters. Shorten it in the opening publicity editor first.";
            return false;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            error = "Party Finder is not available from the game client right now.";
            return false;
        }

        var normalizedText = publication.Text.Trim();
        var currentText = agent->StoredRecruitmentInfo.CommentString.ToString();
        var currentlyRecruiting = condition[ConditionFlag.UsingPartyFinder];
        if (currentlyRecruiting &&
            !string.Equals(currentText, normalizedText, StringComparison.Ordinal))
        {
            error = "Your current Party Finder description does not match PartyPulse. Stop or update that listing manually before starting the refresher.";
            return false;
        }

        profileId = venue.ProfileId;
        openingId = publication.OpeningId;
        publicationCode = publication.PublicationCode;
        expectedText = normalizedText;
        openingClosesAt = publication.ClosesAt;
        interval = TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 1, 1440));
        NextRefreshAt = null;
        stageDeadline = null;
        transientInterruptionStartedAt = null;
        transientIssue = TransientIssue.None;

        if (currentlyRecruiting)
        {
            BeginRefresh(DateTimeOffset.UtcNow);
            if (!IsRunning)
            {
                error = StatusMessage;
                return false;
            }
            StatusMessage = "Refreshing the current Party Finder listing...";
            return true;
        }

        agent->StoredRecruitmentInfo.CommentString = expectedText;
        agent->Show();
        State = PartyFinderAutomationState.WaitingForInitialWindow;
        stageDeadline = DateTimeOffset.UtcNow + AddonTimeout;
        StatusMessage = "Opening Party Finder and attempting to start recruitment with the saved game-side conditions...";
        return true;
    }

    public void Stop(string message = "Party Finder refresher stopped.")
    {
        State = PartyFinderAutomationState.Idle;
        StatusMessage = message;
        NextRefreshAt = null;
        stageDeadline = null;
        transientInterruptionStartedAt = null;
        transientIssue = TransientIssue.None;
        profileId = Guid.Empty;
        openingId = 0;
        publicationCode = string.Empty;
        expectedText = string.Empty;
        openingClosesAt = default;
    }

    public void Tick()
    {
        if (!IsRunning) return;

        var venue = configuration.VenueConnections.Find(value => value.ProfileId == profileId);
        if (venue is null)
        {
            Stop("Party Finder refresher stopped because the venue profile was removed.");
            return;
        }

        var snapshot = publications.GetSnapshot(venue);
        var current = PartyFinderPublicationSelector.Resolve(snapshot.View, snapshot.EstimatedServerNow, VenueTimeZone.Resolve(venue));
        if (current is null ||
            current.OpeningId != openingId ||
            !string.Equals(current.PublicationCode, publicationCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.Text.Trim(), expectedText, StringComparison.Ordinal))
        {
            Stop("Party Finder refresher stopped because the current PartyPulse text changed or no longer applies.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (snapshot.EstimatedServerNow >= openingClosesAt)
        {
            Stop("Party Finder refresher stopped because the opening finished.");
            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            PauseForTransientInterruption(
                now,
                TransientIssue.AgentUnavailable,
                "Party Finder is temporarily unavailable, usually during a zone transition. The refresher will resume automatically.",
                "Party Finder refresher stopped because Party Finder remained unavailable after the zone-transition grace period.");
            return;
        }

        ClearTransientInterruption(TransientIssue.AgentUnavailable);

        var currentText = agent->StoredRecruitmentInfo.CommentString.ToString();
        if (!string.Equals(currentText, expectedText, StringComparison.Ordinal))
        {
            if (!condition[ConditionFlag.UsingPartyFinder] || string.IsNullOrWhiteSpace(currentText))
            {
                PauseForTransientInterruption(
                    now,
                    TransientIssue.ListingStateUnavailable,
                    "Party Finder state is temporarily unavailable. The refresher is preserving its schedule while the game finishes loading.",
                    "Party Finder refresher stopped because the expected listing did not return after the zone-transition grace period.");
                return;
            }

            Stop("Party Finder refresher stopped because the in-game description was changed. PartyPulse did not overwrite it.");
            return;
        }

        ClearTransientInterruption(TransientIssue.ListingStateUnavailable);

        switch (State)
        {
            case PartyFinderAutomationState.WaitingForInitialWindow:
                if (condition[ConditionFlag.UsingPartyFinder])
                {
                    BeginRefreshCountdown(venue, now, "Party Finder recruitment started.");
                }
                else if (TryClickButton("LookingForGroup", 46))
                {
                    State = PartyFinderAutomationState.WaitingForInitialConditions;
                    stageDeadline = now + AddonTimeout;
                    StatusMessage = "Opening the Party Finder recruitment conditions...";
                }
                else if (stageDeadline is { } mainDeadline && now >= mainDeadline)
                {
                    State = PartyFinderAutomationState.AwaitingInitialRecruitment;
                    stageDeadline = null;
                    StatusMessage = "Party Finder is open with the PartyPulse description. Click Recruit Members, choose any missing game-side conditions, and click Recruit; automatic refresh will begin after the listing starts.";
                }
                break;

            case PartyFinderAutomationState.WaitingForInitialConditions:
            {
                if (condition[ConditionFlag.UsingPartyFinder])
                {
                    BeginRefreshCountdown(venue, now, "Party Finder recruitment started.");
                    break;
                }

                var clicked = TryClickButton("LookingForGroupCondition", 113, out var buttonReady);
                if (clicked)
                {
                    State = PartyFinderAutomationState.AwaitingInitialRecruitment;
                    stageDeadline = null;
                    StatusMessage = "Party Finder recruitment was submitted. Waiting for the listing to become active...";
                }
                else if (buttonReady || stageDeadline is { } initialConditionsDeadline && now >= initialConditionsDeadline)
                {
                    State = PartyFinderAutomationState.AwaitingInitialRecruitment;
                    stageDeadline = null;
                    StatusMessage = "Choose any missing game-side recruitment conditions and click Recruit; automatic refresh will begin after the listing starts.";
                }
                break;
            }

            case PartyFinderAutomationState.AwaitingInitialRecruitment:
                if (condition[ConditionFlag.UsingPartyFinder])
                    BeginRefreshCountdown(venue, now, "Party Finder recruitment started.");
                break;

            case PartyFinderAutomationState.WaitingForRefresh:
                if (!condition[ConditionFlag.UsingPartyFinder])
                {
                    PauseForTransientInterruption(
                        now,
                        TransientIssue.RecruitmentUnavailable,
                        "Party Finder recruitment is temporarily unavailable. The refresher will resume if the listing returns after the zone transition.",
                        "Party Finder refresher stopped because the listing was no longer recruiting after the zone-transition grace period.");
                    return;
                }
                ClearTransientInterruption(TransientIssue.RecruitmentUnavailable);
                if (NextRefreshAt is { } next && now >= next)
                    BeginRefresh(now);
                break;

            case PartyFinderAutomationState.WaitingForDetail:
                if (!condition[ConditionFlag.UsingPartyFinder])
                {
                    PauseForTransientInterruption(
                        now,
                        TransientIssue.RecruitmentUnavailable,
                        "Party Finder recruitment is temporarily unavailable while opening the listing. The refresher will retry automatically.",
                        "Party Finder refresher stopped because the listing was no longer recruiting after the zone-transition grace period.");
                    return;
                }
                ClearTransientInterruption(TransientIssue.RecruitmentUnavailable);
                if (TryClickButton("LookingForGroupDetail", 109))
                {
                    State = PartyFinderAutomationState.WaitingForConditions;
                    stageDeadline = now + AddonTimeout;
                    StatusMessage = "Re-submitting the current Party Finder conditions...";
                }
                else if (stageDeadline is { } detailDeadline && now >= detailDeadline)
                {
                    Stop("Party Finder refresher stopped because the listing details window could not be opened.");
                }
                break;

            case PartyFinderAutomationState.WaitingForConditions:
                if (TryClickButton("LookingForGroupCondition", 113))
                {
                    NextRefreshAt = now + interval;
                    State = PartyFinderAutomationState.WaitingForRefresh;
                    stageDeadline = null;
                    StatusMessage = $"Party Finder refreshed. Next refresh at {VenueTimeZone.Format(venue, NextRefreshAt.Value, "t")}.";
                }
                else if (stageDeadline is { } conditionsDeadline && now >= conditionsDeadline)
                {
                    Stop("Party Finder refresher stopped because the Recruit button could not be submitted.");
                }
                break;
        }
    }

    private void BeginRefreshCountdown(VenueConnectionConfiguration venue, DateTimeOffset now, string prefix)
    {
        NextRefreshAt = now + interval;
        State = PartyFinderAutomationState.WaitingForRefresh;
        stageDeadline = null;
        StatusMessage = $"{prefix} Next refresh at {VenueTimeZone.Format(venue, NextRefreshAt.Value, "t")}.";
    }

    private void BeginRefresh(DateTimeOffset now)
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null || playerState.ContentId == 0 || !agent->OpenListingByContentId(playerState.ContentId))
        {
            if (!IsRunning)
            {
                Stop("Party Finder refresher could not start because the current listing could not be opened.");
                return;
            }

            PauseForTransientInterruption(
                now,
                TransientIssue.OpenListingFailed,
                "The current Party Finder listing could not be opened yet. PartyPulse will retry automatically.",
                "Party Finder refresher stopped because the current listing could not be opened after repeated retries.");
            if (IsRunning)
            {
                State = PartyFinderAutomationState.WaitingForRefresh;
                NextRefreshAt = now + TransientRetryDelay;
            }
            return;
        }

        ClearTransientInterruption(TransientIssue.OpenListingFailed);
        State = PartyFinderAutomationState.WaitingForDetail;
        stageDeadline = now + AddonTimeout;
        NextRefreshAt = null;
        StatusMessage = "Opening the current Party Finder listing for refresh...";
    }


    private void PauseForTransientInterruption(
        DateTimeOffset now,
        TransientIssue issue,
        string waitingMessage,
        string timeoutMessage)
    {
        if (transientIssue != issue)
        {
            transientIssue = issue;
            transientInterruptionStartedAt = now;
        }
        else
        {
            transientInterruptionStartedAt ??= now;
        }

        if (now - transientInterruptionStartedAt.Value >= TransientInterruptionGrace)
        {
            Stop(timeoutMessage);
            return;
        }

        if (State is PartyFinderAutomationState.WaitingForInitialWindow or
            PartyFinderAutomationState.WaitingForInitialConditions or
            PartyFinderAutomationState.WaitingForDetail or
            PartyFinderAutomationState.WaitingForConditions)
        {
            stageDeadline = now + AddonTimeout;
        }

        StatusMessage = waitingMessage;
    }

    private void ClearTransientInterruption(TransientIssue issue)
    {
        if (transientIssue != issue)
        {
            return;
        }

        transientIssue = TransientIssue.None;
        transientInterruptionStartedAt = null;
    }

    private bool TryClickButton(string addonName, uint buttonId) =>
        TryClickButton(addonName, buttonId, out _);

    private bool TryClickButton(string addonName, uint buttonId, out bool buttonReady)
    {
        buttonReady = false;
        try
        {
            var addonPtr = gameGui.GetAddonByName(addonName, 1);
            if (addonPtr.IsNull)
                return false;

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (!addon->IsVisible) return false;
            var button = addon->GetComponentButtonById(buttonId);
            if (button == null || button->AtkResNode == null || !button->AtkResNode->IsVisible()) return false;
            buttonReady = true;
            if (!button->IsEnabled) return false;

            var ownerNode = button->AtkComponentBase.OwnerNode;
            if (ownerNode == null) return false;
            var eventPointer = ownerNode->AtkResNode.AtkEventManager.Event;
            if (eventPointer == null) return false;
            var addonEvent = (AtkEvent*)eventPointer;
            addon->ReceiveEvent(
                addonEvent->State.EventType,
                (int)addonEvent->Param,
                eventPointer);
            return true;
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Failed to click Party Finder addon button {ButtonId} in {AddonName}.", buttonId, addonName);
            return false;
        }
    }
}
