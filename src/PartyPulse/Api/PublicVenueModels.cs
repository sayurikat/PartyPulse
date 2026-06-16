namespace PartyPulse.Api;

public sealed record PublicVenueResponse(
    int VenueId,
    string VenueCode,
    string VenueName,
    string? AddressWorldName,
    string? AddressCityName,
    int? AddressWard,
    int? AddressPlot)
{
    public bool HasCompleteAddress =>
        !string.IsNullOrWhiteSpace(AddressWorldName) &&
        !string.IsNullOrWhiteSpace(AddressCityName) &&
        AddressWard > 0 &&
        AddressPlot > 0;

    public string AddressDisplay => HasCompleteAddress
        ? $"{AddressWorldName}, {AddressCityName}, Ward {AddressWard}, Plot {AddressPlot}"
        : "Address not published";
}
