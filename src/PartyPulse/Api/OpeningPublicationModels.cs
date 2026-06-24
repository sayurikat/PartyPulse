using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public static class OpeningPublicationCodes
{
    public const string ShoutrunnerBeforeOpeningDay = "shoutrunner.before_opening_day";
    public const string ShoutrunnerSameDayBeforeOpening = "shoutrunner.same_day_before_opening";
    public const string ShoutrunnerDuringOpening = "shoutrunner.during_opening";
    public const string PartyFinderBeforeOpeningDay = "partyfinder.before_opening_day";
    public const string PartyFinderOpeningDay = "partyfinder.opening_day";
}

public static class ShoutrunnerDutyEventTypes
{
    public const string Shout = "shout";
    public const string Reset = "reset";
    public const string Completed = "completed";
}

public sealed record OpeningPublicationCapabilities(
    bool CanManageOpenings,
    bool CanUseShoutrunner,
    bool CanManageShoutrunnerTemplates,
    bool CanUsePartyFinder,
    bool CanManagePartyFinderTemplates);

public sealed record ShoutrunnerWorldSummary(
    int WorldId,
    string WorldName,
    string DatacenterName,
    string RegionName);

public sealed record OpeningPublicationTemplateSummary(
    string PublicationCode,
    string ChannelCode,
    string DisplayName,
    string? Description,
    byte MaxLines,
    short MaxLineLength,
    string? TemplateText,
    DateTimeOffset? UpdatedAt,
    bool CanManage);

public sealed record OpeningPublicationTextSummary(
    long OpeningId,
    string PublicationCode,
    string ChannelCode,
    string DisplayName,
    byte MaxLines,
    short MaxLineLength,
    string? PublicationText,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? UpdatedAt);

public sealed record OpeningPublicationOpeningSummary(
    long OpeningId,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    string? ThemeName,
    string? Title,
    string AddressWorldName,
    string AddressCityName,
    int AddressWard,
    int AddressPlot,
    string Djs,
    IReadOnlyList<OpeningPublicationTextSummary> Texts);

public sealed record OpeningPublicationContextResponse(
    OpeningPublicationCapabilities Capabilities,
    DateTimeOffset ServerNow,
    IReadOnlyList<OpeningPublicationTemplateSummary> Templates,
    IReadOnlyList<OpeningPublicationOpeningSummary> Openings,
    IReadOnlyList<ShoutrunnerWorldSummary> Worlds);

public sealed record SaveOpeningPublicationTemplateRequest(string? TemplateText);
public sealed record GenerateOpeningPublicationsRequest(
    string ChannelCode,
    string? DisplayDate,
    string? DisplayTime);
public sealed record SaveOpeningPublicationTextRequest(string? PublicationText);

public sealed record ShoutrunnerDutyLogEntryRequest(
    Guid ClientEntryId,
    long OpeningId,
    DateTimeOffset OccurredAt,
    string EventType,
    int CompletedLocations,
    int TotalLocations,
    int? WorldId,
    string? WorldName,
    string? DatacenterName,
    string? CityName,
    string? Reason);

public sealed record ReportShoutrunnerDutyRequest(
    IReadOnlyList<ShoutrunnerDutyLogEntryRequest> Entries);

public sealed record SaveOpeningPublicationTemplateResponse(
    string PublicationCode,
    string? TemplateText,
    DateTimeOffset UpdatedAt);

public sealed record GenerateOpeningPublicationsResponse(
    long OpeningId,
    string ChannelCode,
    IReadOnlyList<OpeningPublicationTextSummary> Texts);

public sealed record SaveOpeningPublicationTextResponse(
    long OpeningId,
    string PublicationCode,
    string? PublicationText,
    DateTimeOffset UpdatedAt);

public sealed record ReportShoutrunnerDutyResponse(
    int AcceptedCount,
    int DuplicateCount,
    DateTimeOffset ReportedAt);
