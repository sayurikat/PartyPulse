namespace PartyPulse.Models;

public sealed record PlayerIdentity(string CharacterName, string WorldName)
{
    public string DisplayName => $"{CharacterName} @ {WorldName}";
}
