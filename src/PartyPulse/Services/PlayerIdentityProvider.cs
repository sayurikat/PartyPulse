using Dalamud.Plugin.Services;
using PartyPulse.Models;

namespace PartyPulse.Services;

public sealed class PlayerIdentityProvider(IPlayerState playerState)
{
    public bool TryGetCurrent(out PlayerIdentity? identity, out string reason)
    {
        identity = null;

        if (!playerState.IsLoaded)
        {
            reason = "Log into a character before connecting.";
            return false;
        }

        var characterName = playerState.CharacterName?.Trim() ?? string.Empty;
        if (characterName.Length == 0)
        {
            reason = "Dalamud has not loaded the character name yet.";
            return false;
        }

        if (!playerState.HomeWorld.IsValid)
        {
            reason = "Dalamud has not loaded the character's home world yet.";
            return false;
        }

        var worldName = playerState.HomeWorld.Value.Name.ToString().Trim();
        if (worldName.Length == 0)
        {
            reason = "Dalamud returned an empty home-world name.";
            return false;
        }

        identity = new PlayerIdentity(characterName, worldName);
        reason = string.Empty;
        return true;
    }
}
