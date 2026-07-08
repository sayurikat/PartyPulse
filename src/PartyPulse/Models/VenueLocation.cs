namespace PartyPulse.Models;

public sealed record VenueLocation(
    string WorldName,
    string LocationName)
{
    public string DisplayText => $"{WorldName}, {LocationName}";
}
