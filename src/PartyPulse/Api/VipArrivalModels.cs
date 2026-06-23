using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public static class VipArrivalMacroCodes
{
    public const string Welcome = "vip.arrival.welcome";
    public const string Renewal = "vip.arrival.renewal";
    public const string NewMember = "vip.sale.new_member";
}

public sealed record VipArrivalCapabilities(
    bool CanUseArrival,
    bool CanManageMacros,
    bool CanManageOpenings);

public sealed record VenueOpeningSummary(
    long OpeningId,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    int AddressWorldId,
    string AddressWorldName,
    int AddressCityId,
    string AddressCityName,
    int AddressWard,
    int AddressPlot,
    string? Title,
    string SourceType)
{
    public string AddressDisplay =>
        $"{AddressWorldName}, {AddressCityName}, Ward {AddressWard}, Plot {AddressPlot}";
}

public sealed record VenueMacroSummary(
    string MacroCode,
    string DisplayName,
    string? Description,
    byte MaxLines,
    short MaxLineLength,
    string? MacroText,
    DateTimeOffset? UpdatedAt,
    int? UpdatedByUserId,
    bool CanManage)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(MacroText);
}

public sealed record VipArrivalSummary(
    long OpeningId,
    int VipPlayerId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int LastSeenCharacterId,
    DateTimeOffset? WelcomedAt,
    DateTimeOffset? RenewalRemindedAt,
    DateTimeOffset? NewVipMessageSentAt,
    DateTimeOffset? DismissedAt,
    DateTimeOffset? CompletedAt,
    string? CompletionReason,
    bool RenewalRequired);

public sealed record VipArrivalContextResponse(
    VipArrivalCapabilities Capabilities,
    DateTimeOffset ServerNow,
    VenueOpeningSummary? CurrentOpening,
    IReadOnlyList<VenueMacroSummary> Macros,
    IReadOnlyList<VipArrivalSummary> Arrivals);

public sealed record VipArrivalObservationRequest(int VipPlayerId, int CharacterId);
public sealed record ObserveVipArrivalsRequest(long OpeningId, IReadOnlyList<VipArrivalObservationRequest> Observations);
public sealed record ObserveVipArrivalsResponse(long OpeningId, int ObservedCount, int PendingCount);
public sealed record RecordVipArrivalActionRequest(long OpeningId, string ActionKey, int? CharacterId);
public sealed record RecordVipArrivalActionResponse(
    long OpeningId,
    int VipPlayerId,
    string ActionKey,
    bool RenewalRequired,
    DateTimeOffset? WelcomedAt,
    DateTimeOffset? RenewalRemindedAt,
    DateTimeOffset? NewVipMessageSentAt,
    DateTimeOffset? DismissedAt,
    DateTimeOffset? CompletedAt,
    string? CompletionReason);
public sealed record UpdateVenueMacroRequest(string? MacroText);
public sealed record UpdateVenueMacroResponse(string MacroCode, string? MacroText, DateTimeOffset UpdatedAt);
public sealed record StartTemporaryOpeningRequest(int DurationMinutes, string? Title);
public sealed record CloseVenueOpeningResponse(long OpeningId, DateTimeOffset ClosedAt);

public sealed record VipNewMemberOffer(
    Guid VenueProfileId,
    long OpeningId,
    int VipPlayerId,
    int CharacterId,
    string CharacterName,
    string WorldName);
