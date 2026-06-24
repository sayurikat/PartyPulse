using System;
using System.Linq;
using PartyPulse.Api;

namespace PartyPulse.PartyFinder;

public sealed record ActivePartyFinderPublication(
    long OpeningId,
    string PublicationCode,
    string DisplayName,
    string Text,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    bool IsOpeningDay);

public static class PartyFinderPublicationSelector
{
    public static ActivePartyFinderPublication? Resolve(
        OpeningPublicationContextResponse? context,
        DateTimeOffset serverNow,
        TimeZoneInfo displayTimeZone)
    {
        if (context is null) return null;

        var openings = context.Openings
            .Where(opening => opening.ClosesAt > serverNow)
            .OrderBy(opening => opening.OpensAt)
            .ThenBy(opening => opening.OpeningId)
            .ToArray();

        var active = openings.FirstOrDefault(opening =>
            opening.OpensAt <= serverNow && opening.ClosesAt > serverNow);
        if (active is not null)
            return Create(active, OpeningPublicationCodes.PartyFinderOpeningDay, true);

        var localDate = TimeZoneInfo.ConvertTime(serverNow, displayTimeZone).Date;
        var sameDay = openings.FirstOrDefault(opening =>
            TimeZoneInfo.ConvertTime(opening.OpensAt, displayTimeZone).Date == localDate);
        if (sameDay is not null)
            return Create(sameDay, OpeningPublicationCodes.PartyFinderOpeningDay, true);

        var next = openings.FirstOrDefault(opening => opening.OpensAt > serverNow);
        return next is null
            ? null
            : Create(next, OpeningPublicationCodes.PartyFinderBeforeOpeningDay, false);
    }

    private static ActivePartyFinderPublication? Create(
        OpeningPublicationOpeningSummary opening,
        string publicationCode,
        bool isOpeningDay)
    {
        var publication = opening.Texts.FirstOrDefault(text =>
            string.Equals(text.PublicationCode, publicationCode, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(publication?.PublicationText)) return null;

        return new ActivePartyFinderPublication(
            opening.OpeningId,
            publication.PublicationCode,
            publication.DisplayName,
            publication.PublicationText!,
            opening.OpensAt,
            opening.ClosesAt,
            isOpeningDay);
    }
}
