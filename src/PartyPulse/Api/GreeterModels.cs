using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public static class GreeterMacroCodes
{
    public const string GreetWithDj = "greeter.greet.with_dj";
    public const string VipGreetWithDj = "greeter.vip_greet.with_dj";
    public const string GreetWithoutDj = "greeter.greet.no_dj";
    public const string VipGreetWithoutDj = "greeter.vip_greet.no_dj";
}

public sealed record GreeterCapabilities(bool CanUse, bool CanManageMacros);

public sealed record GreeterOpeningSummary(
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

public sealed record GreeterCurrentDjSummary(
    long BookingId,
    string Name,
    string? TwitchUrl,
    bool Resident,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

public sealed record GreeterMacroSummary(
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

public sealed record GreeterArrivalSummary(
    long OpeningId,
    int WorldId,
    string WorldName,
    string CharacterName,
    int? CharacterId,
    int? VipPlayerId,
    bool IsVip,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? GreetedAt,
    DateTimeOffset? DismissedAt,
    DateTimeOffset? CompletedAt,
    string? CompletionReason)
{
    public string DisplayName => $"{CharacterName} @ {WorldName}";
}

public sealed record GreeterContextResponse(
    GreeterCapabilities Capabilities,
    DateTimeOffset ServerNow,
    GreeterOpeningSummary? CurrentOpening,
    GreeterCurrentDjSummary? CurrentDj,
    IReadOnlyList<GreeterMacroSummary> Macros,
    IReadOnlyList<GreeterArrivalSummary> Arrivals);

public sealed record GreeterObservationRequest(string CharacterName, string WorldName);

public sealed record ObserveGreeterArrivalsRequest(
    long OpeningId,
    IReadOnlyList<GreeterObservationRequest> Observations);

public sealed record ObserveGreeterArrivalsResponse(
    long OpeningId,
    int ObservedCount,
    int PendingCount);

public sealed record RecordGreeterActionRequest(
    long OpeningId,
    string CharacterName,
    string WorldName,
    string ActionKey);

public sealed record RecordGreeterActionResponse(
    long OpeningId,
    string CharacterName,
    string WorldName,
    string ActionKey,
    DateTimeOffset? GreetedAt,
    DateTimeOffset? DismissedAt,
    DateTimeOffset? CompletedAt,
    string? CompletionReason);

public sealed record UpdateGreeterMacroRequest(string? MacroText);

public sealed record UpdateGreeterMacroResponse(
    string MacroCode,
    string? MacroText,
    DateTimeOffset UpdatedAt);
