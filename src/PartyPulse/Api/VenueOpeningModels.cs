using System;
using System.Collections.Generic;

namespace PartyPulse.Api;

public sealed record VenueOpeningScheduleCapabilities(bool CanManage);

public sealed record VenueOpeningAddressSummary(
    int WorldId,
    string WorldName,
    int CityId,
    string CityName,
    int Ward,
    int Plot)
{
    public string DisplayText => $"{WorldName}, {CityName}, Ward {Ward}, Plot {Plot}";
}

public sealed record VenueOpeningThemeSummary(int ThemeId, string Name);

public sealed record VenueOpeningScheduleItem(
    long OpeningId,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    VenueOpeningAddressSummary Address,
    int? ThemeId,
    string? ThemeName,
    string? Title,
    string SourceType,
    DateTimeOffset CreatedAt,
    bool IsCancelled,
    DateTimeOffset? CancelledAt)
{
    public TimeSpan Duration => ClosesAt - OpensAt;
}

public sealed record VenueOpeningScheduleResponse(
    VenueOpeningScheduleCapabilities Capabilities,
    DateTimeOffset ServerNow,
    DateTimeOffset SuggestedOpensAt,
    int SuggestedDurationMinutes,
    VenueOpeningAddressSummary? DefaultAddress,
    IReadOnlyList<VenueOpeningThemeSummary> Themes,
    IReadOnlyList<VenueOpeningScheduleItem> Openings);

public sealed record SaveVenueOpeningRequest(
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    string AddressWorldName,
    string AddressCityName,
    int AddressWard,
    int AddressPlot,
    string ThemeName,
    string? Title);

public sealed record CancelVenueOpeningResponse(long OpeningId, DateTimeOffset CancelledAt);
