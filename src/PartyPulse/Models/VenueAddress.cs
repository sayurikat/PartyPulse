namespace PartyPulse.Models;

public sealed record VenueAddress(
    string WorldName,
    string CityName,
    int Ward,
    int Plot)
{
    public string DisplayText => $"{WorldName}, {CityName}, Ward {Ward}, Plot {Plot}";
}
