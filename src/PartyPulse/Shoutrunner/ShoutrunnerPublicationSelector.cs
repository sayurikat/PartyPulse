using System;
using System.Linq;
using PartyPulse.Api;

namespace PartyPulse.Shoutrunner;

public sealed record ActiveShoutrunnerPublication(
    long OpeningId,
    string PublicationCode,
    string DisplayName,
    string Text,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt);

public static class ShoutrunnerPublicationSelector
{
    public static ActiveShoutrunnerPublication? Resolve(
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
            return Create(active, OpeningPublicationCodes.ShoutrunnerDuringOpening);

        var localDate = TimeZoneInfo.ConvertTime(serverNow, displayTimeZone).Date;
        var sameDay = openings.FirstOrDefault(opening =>
            opening.OpensAt > serverNow && TimeZoneInfo.ConvertTime(opening.OpensAt, displayTimeZone).Date == localDate);
        if (sameDay is not null)
            return Create(sameDay, OpeningPublicationCodes.ShoutrunnerSameDayBeforeOpening);

        var next = openings.FirstOrDefault(opening => opening.OpensAt > serverNow);
        return next is null
            ? null
            : Create(next, OpeningPublicationCodes.ShoutrunnerBeforeOpeningDay);
    }

    private static ActiveShoutrunnerPublication? Create(
        OpeningPublicationOpeningSummary opening,
        string publicationCode)
    {
        var publication = opening.Texts.FirstOrDefault(text =>
            string.Equals(text.PublicationCode, publicationCode, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(publication?.PublicationText)) return null;

        return new ActiveShoutrunnerPublication(
            opening.OpeningId,
            publication.PublicationCode,
            publication.DisplayName,
            publication.PublicationText!,
            opening.OpensAt,
            opening.ClosesAt);
    }
}
