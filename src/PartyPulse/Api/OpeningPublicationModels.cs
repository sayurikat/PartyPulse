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

public sealed record OpeningPublicationCapabilities(
    bool CanManageOpenings,
    bool CanManageShoutrunnerTemplates,
    bool CanManagePartyFinderTemplates,
    bool CanUsePartyFinder);

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
    string Djs,
    IReadOnlyList<OpeningPublicationTextSummary> Texts);

public sealed record OpeningPublicationContextResponse(
    OpeningPublicationCapabilities Capabilities,
    DateTimeOffset ServerNow,
    IReadOnlyList<OpeningPublicationTemplateSummary> Templates,
    IReadOnlyList<OpeningPublicationOpeningSummary> Openings);

public sealed record SaveOpeningPublicationTemplateRequest(string? TemplateText);
public sealed record GenerateOpeningPublicationsRequest(string ChannelCode);
public sealed record SaveOpeningPublicationTextRequest(string? PublicationText);

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
